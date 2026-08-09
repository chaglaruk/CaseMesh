using System.Diagnostics;
using System.Runtime.InteropServices;
using HRCompanion.Core.Abstractions;
using HRCompanion.Core.Models;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wasapi.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace HRCompanion.Audio.Windows;

/// <summary>
/// Captures only audio rendered by the selected process and its child process tree through
/// Windows process-loopback activation. It never substitutes system loopback.
/// </summary>
public sealed class TeamsProcessLoopbackCaptureSource : IAudioCaptureSource
{
    private const string ProcessLoopbackDevice = "VAD\\Process_Loopback";
    private static readonly Guid AudioClientInterfaceId = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private readonly object _sync = new();
    private AudioClient? _audioClient;
    private AudioCaptureClient? _captureClient;
    private AudioFrameConverter? _converter;
    private EventWaitHandle? _sampleReady;
    private CancellationTokenSource? _captureCts;
    private Task? _captureTask;
    private Process? _targetProcess;

    public TeamsProcessLoopbackCaptureSource(int processId) => ProcessId = processId;

    public int ProcessId { get; }
    public SpeakerRole Speaker => SpeakerRole.Hr;
    public string DisplayName => $"Microsoft Teams process tree {ProcessId} (Teams-isolated)";
    public event EventHandler<AudioFrame>? FrameReady;
    public event EventHandler<Exception>? Faulted;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
        {
            throw new PlatformNotSupportedException("Process-specific application loopback requires Windows build 20348 or later.");
        }

        lock (_sync)
        {
            if (_captureTask is not null) return;
        }

        var process = Process.GetProcessById(ProcessId);
        if (process.HasExited)
        {
            process.Dispose();
            throw new InvalidOperationException($"The selected Teams process {ProcessId} has exited. Detect Teams again and restart the meeting.");
        }

        AudioClient? audioClient = null;
        EventWaitHandle? sampleReady = null;
        try
        {
            audioClient = await ActivateProcessLoopbackAsync((uint)ProcessId, cancellationToken).ConfigureAwait(false);
            var captureFormat = new WaveFormat(44100, 16, 2);
            audioClient.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback,
                1_000_000,
                0,
                captureFormat,
                Guid.Empty);

            sampleReady = new EventWaitHandle(false, EventResetMode.AutoReset);
            audioClient.SetEventHandle(sampleReady.SafeWaitHandle.DangerousGetHandle());
            var captureClient = audioClient.AudioCaptureClient;
            var converter = new AudioFrameConverter(captureFormat);
            var captureCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            process.EnableRaisingEvents = true;
            process.Exited += OnTargetProcessExited;

            lock (_sync)
            {
                _targetProcess = process;
                _audioClient = audioClient;
                _captureClient = captureClient;
                _converter = converter;
                _sampleReady = sampleReady;
                _captureCts = captureCts;
                _captureTask = Task.Run(() => CaptureLoop(captureCts.Token), CancellationToken.None);
            }

            audioClient.Start();
            audioClient = null;
            sampleReady = null;
            process = null!;
        }
        catch
        {
            sampleReady?.Dispose();
            audioClient?.Dispose();
            process?.Dispose();
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? captureTask;
        CancellationTokenSource? captureCts;
        EventWaitHandle? sampleReady;
        lock (_sync)
        {
            captureTask = _captureTask;
            captureCts = _captureCts;
            sampleReady = _sampleReady;
        }
        if (captureTask is null) return;

        captureCts?.Cancel();
        sampleReady?.Set();
        try
        {
            await captureTask.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Faulted?.Invoke(this, new TimeoutException("Teams process-loopback capture did not stop within three seconds."));
        }
        finally
        {
            Cleanup();
        }
    }

    private void CaptureLoop(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                _sampleReady?.WaitOne(300);
                if (cancellationToken.IsCancellationRequested) break;

                var capture = _captureClient;
                var converter = _converter;
                if (capture is null || converter is null) break;

                var packetFrames = capture.GetNextPacketSize();
                while (packetFrames > 0)
                {
                    var buffer = capture.GetBuffer(out var frames, out var flags, out _, out _);
                    try
                    {
                        var byteCount = frames * 4;
                        var bytes = new byte[byteCount];
                        if ((flags & AudioClientBufferFlags.Silent) == 0 && byteCount > 0)
                        {
                            Marshal.Copy(buffer, bytes, 0, byteCount);
                        }

                        foreach (var frame in converter.Push(bytes, byteCount, DateTimeOffset.UtcNow))
                        {
                            FrameReady?.Invoke(this, frame);
                        }
                    }
                    finally
                    {
                        capture.ReleaseBuffer(frames);
                    }
                    packetFrames = capture.GetNextPacketSize();
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Faulted?.Invoke(this, ex);
        }
        finally
        {
            try { _audioClient?.Stop(); } catch { }
        }
    }

    private void OnTargetProcessExited(object? sender, EventArgs e)
    {
        _captureCts?.Cancel();
        _sampleReady?.Set();
        Faulted?.Invoke(this, new InvalidOperationException(
            $"The selected Teams process {ProcessId} exited. Detect the restarted Teams process and start the meeting again."));
    }

    private void Cleanup()
    {
        lock (_sync)
        {
            if (_targetProcess is not null)
            {
                _targetProcess.Exited -= OnTargetProcessExited;
                _targetProcess.Dispose();
            }
            try { _audioClient?.Stop(); } catch { }
            _audioClient?.Dispose();
            _sampleReady?.Dispose();
            _captureCts?.Dispose();
            _targetProcess = null;
            _captureClient = null;
            _audioClient = null;
            _converter = null;
            _sampleReady = null;
            _captureCts = null;
            _captureTask = null;
        }
    }

    private static async Task<AudioClient> ActivateProcessLoopbackAsync(uint processId, CancellationToken cancellationToken)
    {
        var activationParams = new ProcessLoopbackActivationParameters
        {
            ActivationType = ProcessLoopbackActivationType.ProcessLoopback,
            ProcessLoopbackParameters = new ProcessLoopbackParameters
            {
                TargetProcessId = processId,
                Mode = ProcessLoopbackCaptureMode.IncludeTargetProcessTree
            }
        };

        var paramsSize = Marshal.SizeOf<ProcessLoopbackActivationParameters>();
        var paramsPointer = Marshal.AllocHGlobal(paramsSize);
        var propVariantPointer = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        IActivateAudioInterfaceAsyncOperation? operation = null;
        try
        {
            Marshal.StructureToPtr(activationParams, paramsPointer, false);
            Marshal.StructureToPtr(new PropVariantBlob
            {
                VariantType = 65, // VT_BLOB
                BlobSize = (uint)paramsSize,
                BlobData = paramsPointer
            }, propVariantPointer, false);

            var completion = new ActivationCompletionHandler();
            ActivateAudioInterfaceAsync(
                ProcessLoopbackDevice,
                AudioClientInterfaceId,
                propVariantPointer,
                completion,
                out operation);
            var audioClientInterface = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new AudioClient(audioClientInterface);
        }
        finally
        {
            if (operation is not null && Marshal.IsComObject(operation)) Marshal.ReleaseComObject(operation);
            Marshal.FreeHGlobal(propVariantPointer);
            Marshal.FreeHGlobal(paramsPointer);
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [In, MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [In, MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId,
        [In] IntPtr activationParams,
        [In] IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessLoopbackActivationParameters
    {
        public ProcessLoopbackActivationType ActivationType;
        public ProcessLoopbackParameters ProcessLoopbackParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessLoopbackParameters
    {
        public uint TargetProcessId;
        public ProcessLoopbackCaptureMode Mode;
    }

    private enum ProcessLoopbackActivationType
    {
        Default,
        ProcessLoopback
    }

    private enum ProcessLoopbackCaptureMode
    {
        IncludeTargetProcessTree,
        ExcludeTargetProcessTree
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob
    {
        public ushort VariantType;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public uint BlobSize;
        public IntPtr BlobData;
    }

    private sealed class ActivationCompletionHandler : IActivateAudioInterfaceCompletionHandler, IProcessLoopbackAgileObject
    {
        private readonly TaskCompletionSource<IAudioClient> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IAudioClient> Task => _completion.Task;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                activateOperation.GetActivateResult(out var result, out var activated);
                if (result != 0)
                {
                    _completion.TrySetException(Marshal.GetExceptionForHR(result) ?? new COMException("Process-loopback activation failed.", result));
                }
                else if (activated is IAudioClient audioClient)
                {
                    _completion.TrySetResult(audioClient);
                }
                else
                {
                    _completion.TrySetException(new InvalidCastException("Process-loopback activation did not return IAudioClient."));
                }
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
            }
        }
    }

    [ComImport]
    [Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IProcessLoopbackAgileObject
    {
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
