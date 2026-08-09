namespace HRCompanion.Core.Models;

public sealed record MeetingAnalysis(
    MeetingIntent Intent,
    AssistantImportance Importance,
    bool NeedsAssistant,
    bool PotentialCommitment,
    bool PotentialWrittenFollowUp,
    IReadOnlyList<string> RetrievalTerms);
