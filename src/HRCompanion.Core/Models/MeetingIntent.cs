namespace HRCompanion.Core.Models;

public enum MeetingIntent
{
    Unknown = 0,
    SmallTalk,
    Information,
    Question,
    Request,
    Proposal,
    CommitmentRequest
}

public enum AssistantImportance
{
    Low = 0,
    Normal,
    High,
    Critical
}
