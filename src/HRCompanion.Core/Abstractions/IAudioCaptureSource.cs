using HRCompanion.Core.Models;

namespace HRCompanion.Core.Abstractions;

public interface IAudioCaptureSource : IAsyncDisposable
{
    SpeakerRole Speaker { get; }
    string DisplayName { get; }
    event EventHandler<AudioFrame>? FrameReady;
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
