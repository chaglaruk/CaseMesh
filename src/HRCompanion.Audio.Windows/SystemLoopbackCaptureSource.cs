using HRCompanion.Core.Abstractions;
using HRCompanion.Core.Models;
using NAudio.Wave;

namespace HRCompanion.Audio.Windows;

/// <summary>
/// Safe fallback that captures the default render endpoint. This is NOT process-specific Teams capture,
/// so other system audio can appear in this stream. It must not be represented as a verified Gate 1 solution.
/// </summary>
public sealed class SystemLoopbackCaptureSource : IAudioCaptureSource
{
    private WasapiLoopbackCapture? _capture;
    private AudioFrameConverter? _converter;

    public SpeakerRole Speaker => SpeakerRole.Hr;
    public string DisplayName => "System loopback (fallback — not Teams-isolated)";
    public event EventHandler<AudioFrame>? FrameReady;
    public event EventHandler<Exception>? Faulted;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_capture is not null) return Task.CompletedTask;
        _capture = new WasapiLoopbackCapture();
        _converter = new AudioFrameConverter(_capture.WaveFormat);
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        _capture.StartRecording();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_capture is null) return Task.CompletedTask;
        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        _capture.StopRecording();
        _capture.Dispose();
        _capture = null;
        _converter = null;
        return Task.CompletedTask;
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null) Faulted?.Invoke(this, e.Exception);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_converter is null) return;
        foreach (var frame in _converter.Push(e.Buffer, e.BytesRecorded, DateTimeOffset.UtcNow))
        {
            FrameReady?.Invoke(this, frame);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
