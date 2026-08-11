namespace HRCompanion.Core.Models;

public sealed record AssistantSource(
    string EvidenceId,
    string SourceName,
    string? Locator);

public sealed record AssistantResponse(
    MeetingIntent Intent,
    AssistantImportance Importance,
    string? Say,
    string? Watch,
    string? Ask,
    bool NeedsWrittenFollowUp,
    double Confidence,
    IReadOnlyList<AssistantSource> Sources)
{
    /// <summary>
    /// Optional preparation cue for a distinct topic HR explicitly signposted but has not yet
    /// asked the user to answer. This is not part of SAY and should not be spoken automatically.
    /// </summary>
    public string? Next { get; init; }

    public static AssistantResponse NoAction(MeetingIntent intent = MeetingIntent.Information) =>
        new(intent, AssistantImportance.Low, null, null, null, false, 1.0, []);
}
