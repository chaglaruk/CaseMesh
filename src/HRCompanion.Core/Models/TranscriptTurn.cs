namespace HRCompanion.Core.Models;

public sealed record TranscriptTurn(
    Guid Id,
    Guid MeetingId,
    SpeakerRole Speaker,
    string Text,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    bool IsFinal,
    string Source,
    string? ProviderItemId = null)
{
    public static TranscriptTurn Final(
        Guid meetingId,
        SpeakerRole speaker,
        string text,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        string source,
        string? providerItemId = null) =>
        new(Guid.NewGuid(), meetingId, speaker, text.Trim(), startedAt, endedAt, true, source, providerItemId);
}
