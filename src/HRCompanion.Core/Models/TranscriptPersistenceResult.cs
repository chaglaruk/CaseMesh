namespace HRCompanion.Core.Models;

public enum TranscriptPersistenceStatus
{
    Inserted,
    AlreadyDurable,
    Failed
}

public readonly record struct TranscriptPersistenceResult(
    TranscriptPersistenceStatus Status,
    DateTimeOffset? PersistedAt = null)
{
    public bool WasInserted => Status == TranscriptPersistenceStatus.Inserted;

    public static TranscriptPersistenceResult Inserted(DateTimeOffset persistedAt) =>
        new(TranscriptPersistenceStatus.Inserted, persistedAt);

    public static TranscriptPersistenceResult AlreadyDurable() =>
        new(TranscriptPersistenceStatus.AlreadyDurable);

    public static TranscriptPersistenceResult Failed() =>
        new(TranscriptPersistenceStatus.Failed);
}
