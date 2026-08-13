using CaseMesh.Core.Abstractions;
using CaseMesh.Core.Models;
using NAudio.Wave;

namespace CaseMesh.Audio.Windows;

/// <summary>
/// Safe fallback that captures the default render endpoint. This is NOT process-specific Teams capture,
/// so other system audio can appear in this stream. It must not be represented as a verified Gate 1 solution.
/// </summary>
public sealed class SystemLoopbackCaptureSource : IAudioCaptureSource
{
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private WasapiRecorder? _capture;
    private AudioFrameConverter? _converter;
    private long _capturedPacketCount;
    private int _disposeStarted;

    public SpeakerRole Speaker => SpeakerRole.Hr;
    public long CapturedPacketCount => Interlocked.Read(ref _capturedPacketCount);
    public string DisplayName => "System loopback (fallback — not Teams-isolated)";
    public event EventHandler<AudioFrame>? FrameReady;
    public event EventHandler<Exception>? CaptureFailed;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposeStarted) != 0) throw new ObjectDisposedException(nameof(SystemLoopbackCaptureSource));
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_capture is not null) return;
            var capture = new WasapiRecorderBuilder()
                .WithLoopbackCapture()
                .WithBufferLength(50)
                .Build();
            var converter = new AudioFrameConverter(capture.WaveFormat);
            Interlocked.Exchange(ref _capturedPacketCount, 0);
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            try
            {
                _converter = converter;
                capture.StartRecording();
                _capture = capture;
            }
            catch
            {
                _converter = null;
                capture.DataAvailable -= OnDataAvailable;
                capture.RecordingStopped -= OnRecordingStopped;
                await capture.DisposeAsync().ConfigureAwait(false);
                throw;
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
            if (_capture is null) return;
            var capture = _capture;
            _capture = null;
            _converter = null;
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            await capture.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void OnDataAvailable(ReadOnlySpan<byte> buffer, NAudio.CoreAudioApi.AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        Interlocked.Increment(ref _capturedPacketCount);
        if (_converter is null) return;
        foreach (var frame in _converter.Push(buffer, DateTimeOffset.UtcNow))
        {
            FrameReady?.Invoke(this, frame);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null) CaptureFailed?.Invoke(this, e.Exception);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        await StopAsync().ConfigureAwait(false);
    }
}
