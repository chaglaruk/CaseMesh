namespace CaseMesh.Core.Models;

public sealed record TranscriptTurn(
    Guid Id,
    Guid MeetingId,
    SpeakerRole Speaker,
    string Text,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    bool IsFinal,
    string Source)
{
    public static TranscriptTurn Final(
        Guid meetingId,
        SpeakerRole speaker,
        string text,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        string source) =>
        new(Guid.NewGuid(), meetingId, speaker, text.Trim(), startedAt, endedAt, true, source);
}
