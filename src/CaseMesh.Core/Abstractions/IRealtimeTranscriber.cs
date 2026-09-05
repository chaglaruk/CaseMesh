using CaseMesh.Core.Models;

namespace CaseMesh.Core.Abstractions;

public sealed record AudioFrame(ReadOnlyMemory<byte> Pcm16Bit24KhzMono, DateTimeOffset CapturedAt);
public sealed record TranscriptionUpdate(string Text, bool IsFinal, DateTimeOffset OccurredAt, string? ItemId = null);

public interface IRealtimeTranscriber : IAsyncDisposable
{
    SpeakerRole Speaker { get; }
    event EventHandler<TranscriptionUpdate>? Updated;
    event EventHandler<Exception>? Failed;
    Task StartAsync(CancellationToken cancellationToken = default);
    ValueTask SendAsync(AudioFrame frame, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
