namespace CaseMesh.Core.Models;

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
    public static AssistantResponse NoAction(MeetingIntent intent = MeetingIntent.Information) =>
        new(intent, AssistantImportance.Low, null, null, null, false, 1.0, []);
}
