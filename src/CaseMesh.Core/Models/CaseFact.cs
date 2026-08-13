namespace CaseMesh.Core.Models;

public enum FactStatus
{
    Unverified = 0,
    UserPosition,
    Verified
}

public sealed record CaseFact(
    Guid Id,
    string Statement,
    FactStatus Status,
    Guid? SourceDocumentId,
    string? SourceLocator,
    DateTimeOffset? EffectiveDate,
    DateTimeOffset CreatedAt);
