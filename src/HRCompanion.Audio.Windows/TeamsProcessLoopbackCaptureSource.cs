using HRCompanion.Core.Abstractions;
using HRCompanion.Core.Models;

namespace HRCompanion.Audio.Windows;

/// <summary>
/// Contract for the meeting-ready Teams-isolated output source.
/// The actual Windows application-loopback implementation must be completed and validated on Windows.
/// It intentionally fails loudly instead of silently falling back and pretending Teams isolation is active.
/// </summary>
public sealed class TeamsProcessLoopbackCaptureSource : IAudioCaptureSource
{
    public TeamsProcessLoopbackCaptureSource(int processId) => ProcessId = processId;

    public int ProcessId { get; }
    public SpeakerRole Speaker => SpeakerRole.Hr;
    public string DisplayName => $"Microsoft Teams process {ProcessId} (process-specific)";
    public event EventHandler<AudioFrame>? FrameReady;

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "Process-specific Teams capture is a required Gate 1 implementation. Use SystemLoopbackCaptureSource only as an explicitly labelled fallback; do not mark Gate 1 verified.");

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // Preserve the event in the public contract until the implementation publishes frames.
    internal void Publish(AudioFrame frame) => FrameReady?.Invoke(this, frame);
}
