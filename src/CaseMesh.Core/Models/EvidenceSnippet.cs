namespace CaseMesh.Core.Models;

public sealed record EvidenceSnippet(
    string EvidenceId,
    Guid DocumentId,
    string SourceName,
    string? SourceLocator,
    string Text,
    double Score,
    DateTimeOffset? SourceDate = null);
