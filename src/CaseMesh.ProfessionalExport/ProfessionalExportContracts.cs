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
    string? ParserVersion,
    string? OcrProvider,
    string? OcrVersion);

public sealed record ProfessionalExportInput(
    MatterEvidenceGraph Evidence,
    WorkplaceMatter Workplace,
    MatterBrainState Brain,
    IReadOnlyList<ExportDocumentMetadata> Documents);

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
    string? ParserVersion,
    string? OcrProvider,
    string? OcrVersion,
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
    decimal? ExtractionConfidence);

public sealed record ExportAssertionItem(
    string Reference,
    Guid AssertionId,
    string Topic,
    string SubjectReference,
    string Predicate,
    string Value,
    string AssertedBy,
    DateTimeOffset? AllegedEventTime,
    DateTimeOffset AssertedAt,
    string OriginLabel,
    DisputeState DisputeState,
    VerificationState VerificationState,
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
    IReadOnlyList<ExportOpenQuestion> OpenQuestions,
    ExportWorkplaceSection Workplace,
    IReadOnlyList<ProfessionalExportArtifactDigest> PayloadArtifacts);

public sealed record ProfessionalExportArtifactDigest(
    ProfessionalExportArtifactKind Kind,
    string FileName,
    string Sha256,
    long ByteLength);

public sealed record GeneratedProfessionalExportArtifact(
    ProfessionalExportArtifactKind Kind,
    string FileName,
    byte[] Content,
    string Sha256);

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
