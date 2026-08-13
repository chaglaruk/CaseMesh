using CaseMesh.Core.Models;

namespace CaseMesh.Core.Abstractions;

public interface IAudioCaptureSource : IAsyncDisposable
{
    SpeakerRole Speaker { get; }
    string DisplayName { get; }
    event EventHandler<AudioFrame>? FrameReady;
    event EventHandler<Exception>? CaptureFailed;
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
