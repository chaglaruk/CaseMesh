namespace HRCompanion.Core.Models;

public sealed record DocumentRecord(
    Guid Id,
    string DisplayName,
    string OriginalPath,
    string Sha256,
    string MediaType,
    DateTimeOffset ImportedAt,
    DateTimeOffset? SourceDate,
    int ChunkCount);

public sealed record DocumentChunk(
    Guid Id,
    Guid DocumentId,
    int Ordinal,
    string Text,
    string? Locator);

public sealed record DocumentImportResult(
    int FilesSeen,
    int Imported,
    int SkippedDuplicate,
    int Unsupported,
    IReadOnlyList<string> Errors);
