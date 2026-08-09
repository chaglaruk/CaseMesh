using HRCompanion.Core.Models;

namespace HRCompanion.Core.Abstractions;

public sealed record AudioFrame(ReadOnlyMemory<byte> Pcm16Bit24KhzMono, DateTimeOffset CapturedAt);
public sealed record TranscriptionUpdate(
    string Text,
    bool IsFinal,
    DateTimeOffset OccurredAt,
    string? ItemId = null,
    DateTimeOffset? StartedAt = null,
    string? PreviousItemId = null,
    bool IsSpeechStarted = false);

public sealed record TranscriberDiagnostics(
    long FramesAccepted,
    long FramesSent,
    long FramesDropped,
    int QueueDepth,
    int QueueHighWaterMark,
    bool HasTranscriptionGap);

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
    TranscriberDiagnostics Diagnostics { get; }
    event EventHandler<TranscriptionUpdate>? Updated;
    event EventHandler<TranscriberConnectionState>? StateChanged;
    event EventHandler<TranscriberDiagnostics>? DiagnosticsChanged;
    event EventHandler<Exception>? Faulted;
    Task StartAsync(CancellationToken cancellationToken = default);
    bool TryEnqueue(AudioFrame frame);
    Task StopAsync(CancellationToken cancellationToken = default);
}
