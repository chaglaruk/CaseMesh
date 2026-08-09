using HRCompanion.Core.Models;

namespace HRCompanion.Core.Abstractions;

public sealed record AudioFrame(ReadOnlyMemory<byte> Pcm16Bit24KhzMono, DateTimeOffset CapturedAt);
public sealed record TranscriptionUpdate(
    string Text,
    bool IsFinal,
    DateTimeOffset OccurredAt,
    string? ItemId = null,
    DateTimeOffset? StartedAt = null,
    string? PreviousItemId = null);

public enum TranscriberConnectionState
{
    Stopped,
    Connecting,
    Listening,
    Reconnecting,
    Failed
}

public interface IRealtimeTranscriber : IAsyncDisposable
{
    SpeakerRole Speaker { get; }
    event EventHandler<TranscriptionUpdate>? Updated;
    event EventHandler<TranscriberConnectionState>? StateChanged;
    event EventHandler<Exception>? Faulted;
    Task StartAsync(CancellationToken cancellationToken = default);
    ValueTask SendAsync(AudioFrame frame, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
