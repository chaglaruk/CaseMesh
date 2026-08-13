using System.Diagnostics;
using CaseMesh.Core.Abstractions;
using CaseMesh.Core.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CaseMesh.Audio.Windows;

/// <summary>
/// Captures only audio rendered by a selected Microsoft Teams process and its descendants.
/// This source never falls back to system-wide loopback.
/// </summary>
public sealed class TeamsProcessLoopbackCaptureSource : IAudioCaptureSource
{
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private WasapiRecorder? _capture;
    private AudioFrameConverter? _converter;
    private Process? _process;
    private long _capturedPacketCount;
    private int _disposeStarted;

    public TeamsProcessLoopbackCaptureSource(int processId) => ProcessId = processId;

    public int ProcessId { get; }
    public long CapturedPacketCount => Interlocked.Read(ref _capturedPacketCount);
    public SpeakerRole Speaker => SpeakerRole.Hr;
    public string DisplayName => $"Microsoft Teams PID {ProcessId} (process tree, isolated)";
    public event EventHandler<AudioFrame>? FrameReady;
    public event EventHandler<Exception>? CaptureFailed;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposeStarted) != 0) throw new ObjectDisposedException(nameof(TeamsProcessLoopbackCaptureSource));
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_capture is not null) return;
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            {
                throw new PlatformNotSupportedException(
                    "Process-specific Teams loopback requires Windows 10 version 2004 (build 19041) or later.");
            }

            var process = ResolveRunningTeamsProcess();
            WasapiRecorder? capture = null;
            try
            {
                var buildTask = new WasapiRecorderBuilder()
                    .WithProcessLoopback((uint)ProcessId, ProcessLoopbackMode.IncludeTargetProcessTree)
                    .WithBufferLength(50)
                    .BuildAsync();
                try
                {
                    capture = await buildTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _ = DisposeWhenBuiltAsync(buildTask);
                    throw;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                    throw new InvalidOperationException($"Microsoft Teams process PID {ProcessId} exited before capture could start.");

                var converter = new AudioFrameConverter(capture.WaveFormat);
                Interlocked.Exchange(ref _capturedPacketCount, 0);
                capture.DataAvailable += OnDataAvailable;
                capture.RecordingStopped += OnRecordingStopped;
                process.EnableRaisingEvents = true;
                process.Exited += OnProcessExited;

                _converter = converter;
                _process = process;
                capture.StartRecording();
                _capture = capture;
                capture = null;
            }
            catch (OperationCanceledException)
            {
                _converter = null;
                _process = null;
                process.Exited -= OnProcessExited;
                process.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                _converter = null;
                _process = null;
                process.Exited -= OnProcessExited;
                process.Dispose();
                throw new InvalidOperationException(
                    $"Could not start process-specific capture for Microsoft Teams PID {ProcessId}. " +
                    "Make sure Teams is still running and Windows application-loopback capture is available.", ex);
            }
            finally
            {
                if (capture is not null)
                {
                    capture.DataAvailable -= OnDataAvailable;
                    capture.RecordingStopped -= OnRecordingStopped;
                    await capture.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var capture = _capture;
            var process = _process;
            _capture = null;
            _converter = null;
            _process = null;

            if (process is not null)
            {
                process.Exited -= OnProcessExited;
                process.Dispose();
            }
            if (capture is null) return;
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            await capture.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private Process ResolveRunningTeamsProcess()
    {
        if (ProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(ProcessId), "A positive Microsoft Teams PID is required.");
        Process process;
        try
        {
            process = Process.GetProcessById(ProcessId);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"Microsoft Teams process PID {ProcessId} does not exist or has already exited.", ex);
        }

        try
        {
            if (process.HasExited)
                throw new InvalidOperationException($"Microsoft Teams process PID {ProcessId} has already exited.");
            if (!TeamsProcessLocator.IsTeamsProcessName(process.ProcessName))
                throw new InvalidOperationException(
                    $"PID {ProcessId} is '{process.ProcessName}', not a recognised Microsoft Teams process. Select Teams explicitly.");
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private void OnDataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        Interlocked.Increment(ref _capturedPacketCount);
        var converter = _converter;
        if (converter is null) return;
        foreach (var frame in converter.Push(buffer, DateTimeOffset.UtcNow))
        {
            FrameReady?.Invoke(this, frame);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            CaptureFailed?.Invoke(this, new InvalidOperationException(
                $"Process-specific Teams capture for PID {ProcessId} stopped unexpectedly.", e.Exception));
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        CaptureFailed?.Invoke(this, new InvalidOperationException(
            $"Microsoft Teams process PID {ProcessId} exited; process-specific capture has stopped."));
        _ = StopAfterProcessExitAsync();
    }

    private async Task StopAfterProcessExitAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CaptureFailed?.Invoke(this, ex);
        }
    }

    private static async Task DisposeWhenBuiltAsync(Task<WasapiRecorder> buildTask)
    {
        try
        {
            var capture = await buildTask.ConfigureAwait(false);
            await capture.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Cancellation caller has already left; a failed activation owns no recorder to release.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        await StopAsync().ConfigureAwait(false);
    }
}
