namespace HRCompanion.Core.Models;

public enum EvidenceChannel
{
    OrdinaryHr = 0,
    AcasWithoutPrejudice = 1
}

public enum EvidenceAuthority
{
    CurrentFinal = 0,
    Historical = 1
}

public sealed record DocumentImportOptions(
    EvidenceChannel Channel,
    EvidenceAuthority Authority)
{
    public static DocumentImportOptions OrdinaryCurrent { get; } =
        new(EvidenceChannel.OrdinaryHr, EvidenceAuthority.CurrentFinal);

    public static DocumentImportOptions RestrictedAcasWithoutPrejudice { get; } =
        new(EvidenceChannel.AcasWithoutPrejudice, EvidenceAuthority.CurrentFinal);
}

public sealed record DocumentRecord(
    Guid Id,
    string DisplayName,
    string OriginalPath,
    string Sha256,
    string MediaType,
    DateTimeOffset ImportedAt,
    DateTimeOffset? SourceDate,
    int ChunkCount,
    EvidenceChannel Channel = EvidenceChannel.OrdinaryHr,
    EvidenceAuthority Authority = EvidenceAuthority.CurrentFinal);

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
