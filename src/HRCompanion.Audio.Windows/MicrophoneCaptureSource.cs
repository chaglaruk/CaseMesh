using HRCompanion.Core.Abstractions;
using HRCompanion.Core.Models;
using NAudio.Wave;

namespace HRCompanion.Audio.Windows;

public sealed class MicrophoneCaptureSource : IAudioCaptureSource
{
    private readonly int _deviceNumber;
    private WaveInEvent? _capture;
    private AudioFrameConverter? _converter;

    public MicrophoneCaptureSource(int deviceNumber = 0) => _deviceNumber = deviceNumber;

    public SpeakerRole Speaker => SpeakerRole.User;
    public string DisplayName => _deviceNumber >= 0 && _deviceNumber < WaveIn.DeviceCount
        ? WaveIn.GetCapabilities(_deviceNumber).ProductName
        : "Microphone";

    public event EventHandler<AudioFrame>? FrameReady;
    public event EventHandler<Exception>? Faulted;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_capture is not null) return Task.CompletedTask;
        if (WaveIn.DeviceCount == 0) throw new InvalidOperationException("No microphone capture device is available.");

        _capture = new WaveInEvent
        {
            DeviceNumber = _deviceNumber,
            WaveFormat = new WaveFormat(48000, 16, 1),
            BufferMilliseconds = 50,
            NumberOfBuffers = 3
        };
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

    public static IReadOnlyList<MicrophoneDeviceInfo> GetDevices() =>
        Enumerable.Range(0, WaveIn.DeviceCount)
            .Select(index => new MicrophoneDeviceInfo(index, WaveIn.GetCapabilities(index).ProductName))
            .ToArray();

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

public sealed record MicrophoneDeviceInfo(int DeviceNumber, string DisplayName);
