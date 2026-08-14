using CaseMesh.Core.Models;

namespace CaseMesh.Core.Snapshots;

public sealed record MatterEvidenceSnapshot(
    Matter Matter,
    IReadOnlyList<DocumentVersionSnapshot> DocumentVersions,
    IReadOnlyList<SourceSpanSnapshot> SourceSpans,
    IReadOnlyList<AssertionSnapshot> Assertions,
    IReadOnlyList<MatterEventSnapshot> Events,
    IReadOnlyList<AssertionEventLinkSnapshot> AssertionEventLinks,
    IReadOnlyList<ContradictionSnapshot> Contradictions,
    IReadOnlyList<AnalysisNodeSnapshot> AnalysisNodes,
    IReadOnlyList<AuditEventSnapshot> AuditEvents);

public sealed record DocumentVersionSnapshot(
    Guid DocumentId,
    Guid DocumentVersionId,
    Guid OriginalObjectId,
    string ContentSha256);

public sealed record SourceSpanSnapshot(
    Guid Id,
    Guid DocumentVersionId,
    int? PageNumber,
    int? TextStart,
    int? TextEnd,
    string ExtractedText,
    string ExtractedTextDigest,
    string ParserVersion,
    decimal? ExtractionConfidence);

public sealed record AssertionSnapshot(
    Guid Id,
    string SubjectReference,
    string Predicate,
    string Value,
    string AssertedBy,
    DateTimeOffset? EventTime,
    DateTimeOffset AssertedAt,
    Guid? SourceSpanId,
    EvidenceOriginClass OriginClass,
    AssertionClass AssertionClass,
    DisputeState DisputeState,
    IntegrityState IntegrityState,
    VerificationState VerificationState,
    decimal? ExtractionConfidence,
    string? CreatedByModel,
    Guid? SupersededByAssertionId);

public sealed record MatterEventSnapshot(
    Guid Id,
    string EventType,
    DateTimeOffset? StartTime,
    DateTimeOffset? EndTime,
    IReadOnlyList<Guid> ParticipantIds,
    string Label,
    EventStatus Status,
    VerificationState VerificationState,
    Guid? SupersedesEventId,
    Guid? SupersededByEventId);

public sealed record AssertionEventLinkSnapshot(
    Guid Id,
    Guid AssertionId,
    Guid EventId,
    AssertionEventRelation Relation);

public sealed record ContradictionSnapshot(
    Guid Id,
    Guid AssertionAId,
    Guid AssertionBId,
    ContradictionType Type,
    string DetectedBy,
    ContradictionResolutionState ResolutionState,
    string? ResolutionNote,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt);

public sealed record AnalysisNodeSnapshot(
    Guid Id,
    string AnalysisType,
    IReadOnlyList<Guid> SourceSpanIds,
    string Provider,
    string Model,
    string PromptVersion,
    string Output,
    DateTimeOffset GeneratedAt,
    VerificationState VerificationState,
    Guid? SupersededByAnalysisNodeId);

public sealed record AuditEventSnapshot(
    Guid Id,
    AuditEventKind Kind,
    string EntityType,
    Guid EntityId,
    Guid? ReplacementEntityId,
    string Actor,
    string ChangeSummary,
    DateTimeOffset OccurredAt);
