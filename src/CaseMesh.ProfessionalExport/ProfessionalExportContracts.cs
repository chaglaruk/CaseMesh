using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Core.Workplace;
using CaseMesh.MatterBrain;

namespace CaseMesh.ProfessionalExport;

public enum ProfessionalExportArtifactKind
{
    BriefDocx = 0,
    EvidenceIndexCsv = 1,
    ChronologyCsv = 2,
    AssertionsCsv = 3,
    ContradictionsCsv = 4,
    OriginalEvidenceManifestJson = 5,
    MatterManifestJson = 6,
    BundleZip = 7
}

public enum ExportDocumentProcessingStatus
{
    NotRecorded = 0,
    Pending = 1,
    Completed = 2,
    Quarantined = 3,
    Failed = 4
}

[Flags]
public enum ExportExtractionRoute
{
    None = 0,
    Native = 1,
    Ocr = 2
}

public enum ExportInclusionKind
{
    DocumentVersion = 0,
    SourceSpan = 1,
    Assertion = 2,
    Event = 3,
    Contradiction = 4
}

public enum ExportSourceLocatorKind
{
    PdfPage = 1,
    DocxParagraph = 2,
    DocxTableCell = 3,
    EmailHeader = 4,
    EmailBody = 5,
    EmailAttachment = 6,
    TextCharacters = 7,
    ImageBoundingBox = 8
}

public sealed record ProfessionalExportRequest(
    TenantId TenantId,
    Guid MatterId,
    Guid ExportId);

public sealed record ExportDocumentMetadata(
    TenantId TenantId,
    Guid MatterId,
    Guid DocumentId,
    Guid DocumentVersionId,
    Guid OriginalObjectId,
    string ContentSha256,
    string? DetectedMediaType,
    long? ByteLength,
    ExportDocumentProcessingStatus ProcessingStatus,
    ExportExtractionRoute ExtractionRoutes,
    IReadOnlyList<string> ParserVersions,
    IReadOnlyList<string> OcrProviders,
    IReadOnlyList<string> OcrVersions);

public sealed record ExportSourceMetadata(
    TenantId TenantId,
    Guid MatterId,
    Guid SourceSpanId,
    Guid DocumentVersionId,
    ExportSourceLocatorKind? LocatorKind,
    string? StableLocator,
    ExportExtractionRoute ExtractionRoute,
    string? ExtractionProvider,
    string? ExtractionProviderVersion,
    int? BoundingBoxLeft,
    int? BoundingBoxTop,
    int? BoundingBoxWidth,
    int? BoundingBoxHeight);

public sealed record ProfessionalExportInput(
    MatterEvidenceGraph Evidence,
    WorkplaceMatter Workplace,
    MatterBrainState Brain,
    IReadOnlyList<ExportDocumentMetadata> Documents,
    IReadOnlyList<ExportSourceMetadata> SourceMetadata);

public sealed record ExportDocumentItem(
    string Reference,
    Guid DocumentId,
    Guid DocumentVersionId,
    Guid OriginalObjectId,
    string ContentSha256,
    string DetectedMediaType,
    long? ByteLength,
    ExportDocumentProcessingStatus ProcessingStatus,
    ExportExtractionRoute ExtractionRoutes,
    IReadOnlyList<string> ParserVersions,
    IReadOnlyList<string> OcrProviders,
    IReadOnlyList<string> OcrVersions,
    int CitedSourceSpanCount,
    bool SharesLogicalOriginal);

public sealed record ExportSourceItem(
    string Reference,
    Guid SourceSpanId,
    string DocumentReference,
    Guid DocumentVersionId,
    int? PageNumber,
    int? TextStart,
    int? TextEnd,
    string ExtractedTextDigest,
    string ParserVersion,
    decimal? ExtractionConfidence,
    ExportSourceLocatorKind? LocatorKind,
    string? StableLocator,
    ExportExtractionRoute ExtractionRoute,
    string? ExtractionProvider,
    string? ExtractionProviderVersion,
    int? BoundingBoxLeft,
    int? BoundingBoxTop,
    int? BoundingBoxWidth,
    int? BoundingBoxHeight);

public sealed record ExportAssertionItem(
    string Reference,
    Guid AssertionId,
    string TopicLabel,
    string SubjectReference,
    string Predicate,
    string Value,
    string AssertedBy,
    DateTimeOffset? AllegedEventTime,
    DateTimeOffset AssertedAt,
    string OriginLabel,
    DisputeState DisputeState,
    IntegrityState IntegrityState,
    VerificationState VerificationState,
    decimal? ExtractionConfidence,
    string? SourceReference,
    bool IsCurrent,
    string? SupersededByReference);

public sealed record ExportChronologyItem(
    string Reference,
    Guid CanonicalId,
    string Kind,
    DateTimeOffset? StartTime,
    DateTimeOffset? EndTime,
    string NeutralLabel,
    string Status,
    IReadOnlyList<string> AssertionReferences,
    IReadOnlyList<string> SourceReferences);

public sealed record ExportContradictionItem(
    string Reference,
    Guid ContradictionId,
    ContradictionType Type,
    ContradictionResolutionState ResolutionState,
    string AssertionAReference,
    string AssertionBReference,
    IReadOnlyList<string> SourceReferences,
    string? ResolutionNote);

public sealed record ExportEntityItem(
    string Reference,
    Guid EntityId,
    string Kind,
    string DisplayName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> SourceReferences);

public sealed record ExportHistoryItem(
    string Reference,
    string Kind,
    Guid HistoricalId,
    string HistoricalStatus,
    string? ReplacementReference,
    Guid? ReplacementId,
    IReadOnlyList<string> SourceReferences);

public sealed record ExportAuditItem(
    string Reference,
    Guid AuditEventId,
    AuditEventKind Kind,
    string EntityType,
    Guid EntityId,
    Guid? ReplacementEntityId,
    string Actor,
    string ChangeSummary,
    DateTimeOffset OccurredAt);

public sealed record ExportOpenQuestion(
    string Reference,
    string Category,
    string NeutralQuestion,
    IReadOnlyList<string> RelatedReferences);

public sealed record ExportWorkplaceSection(
    IReadOnlyList<ExportWorkplaceItem> EmploymentProfiles,
    IReadOnlyList<ExportWorkplaceItem> EmploymentTerms,
    IReadOnlyList<ExportWorkplaceItem> HealthAndAbsence,
    IReadOnlyList<ExportAdjustmentItem> AdjustmentRequests,
    IReadOnlyList<ExportWorkplaceItem> Processes,
    IReadOnlyList<ExportWorkplaceItem> AcasStates);

public sealed record ExportWorkplaceItem(
    string Reference,
    Guid Id,
    string Kind,
    string NeutralLabel,
    string Status,
    IReadOnlyList<string> AssertionReferences,
    IReadOnlyList<string> EventReferences);

public sealed record ExportAdjustmentItem(
    string Reference,
    Guid Id,
    string NeutralLabel,
    string ResponseStatus,
    IReadOnlyList<string> RequestAssertionReferences,
    IReadOnlyList<string> ResponseAssertionReferences,
    IReadOnlyList<string> ImplementationAssertionReferences);

public sealed record ProfessionalExportManifest(
    string SchemaVersion,
    string TemplateVersion,
    Guid ExportId,
    TenantId TenantId,
    Guid MatterId,
    string MatterReference,
    string MatterType,
    string MatterTitle,
    string MatterStatus,
    string? Jurisdiction,
    DateTimeOffset GeneratedAt,
    string SnapshotDigest,
    string NeutralBrief,
    IReadOnlyList<ExportDocumentItem> Documents,
    IReadOnlyList<ExportSourceItem> Sources,
    IReadOnlyList<ExportEntityItem> PeopleAndOrganisations,
    IReadOnlyList<ExportChronologyItem> Chronology,
    IReadOnlyList<ExportAssertionItem> Assertions,
    IReadOnlyList<ExportContradictionItem> Contradictions,
    IReadOnlyList<ExportHistoryItem> SupersededHistory,
    IReadOnlyList<ExportAuditItem> AuditTrail,
    IReadOnlyList<ExportOpenQuestion> OpenQuestions,
    ExportWorkplaceSection Workplace,
    IReadOnlyList<ProfessionalExportArtifactDigest> PayloadArtifacts);

public sealed record ProfessionalExportArtifactDigest(
    ProfessionalExportArtifactKind Kind,
    string FileName,
    string Sha256,
    long ByteLength);

public sealed class GeneratedProfessionalExportArtifact : IEquatable<GeneratedProfessionalExportArtifact>
{
    private readonly byte[] _content;

    internal GeneratedProfessionalExportArtifact(
        ProfessionalExportArtifactKind kind,
        string fileName,
        byte[] content,
        string sha256)
    {
        Kind = kind;
        FileName = fileName;
        _content = content.ToArray();
        Sha256 = sha256;
    }

    public ProfessionalExportArtifactKind Kind { get; }
    public string FileName { get; }
    public byte[] Content => _content.ToArray();
    public string Sha256 { get; }
    public long ByteLength => _content.LongLength;
    internal ReadOnlySpan<byte> ContentSpan => _content;

    public bool Equals(GeneratedProfessionalExportArtifact? other) =>
        other is not null && Kind == other.Kind && FileName == other.FileName &&
        Sha256 == other.Sha256 && ByteLength == other.ByteLength;

    public override bool Equals(object? obj) => Equals(obj as GeneratedProfessionalExportArtifact);
    public override int GetHashCode() => HashCode.Combine(Kind, FileName, Sha256, ByteLength);
}

public sealed record ProfessionalExportRun(
    Guid ExportId,
    TenantId TenantId,
    Guid MatterId,
    string SnapshotDigest,
    string SchemaVersion,
    string TemplateVersion,
    DateTimeOffset GeneratedAt,
    string ArtifactManifestDigest,
    IReadOnlyList<Guid> DocumentVersionIds,
    IReadOnlyList<Guid> SourceSpanIds,
    IReadOnlyList<Guid> AssertionIds,
    IReadOnlyList<Guid> EventIds,
    IReadOnlyList<Guid> ContradictionIds,
    IReadOnlyList<ProfessionalExportArtifactDigest> Artifacts);

public sealed record ProfessionalExportPackage(
    ProfessionalExportRun Run,
    ProfessionalExportManifest Manifest,
    IReadOnlyList<GeneratedProfessionalExportArtifact> Artifacts);

public sealed record PersistedProfessionalExportRun(
    ProfessionalExportRun Run);
