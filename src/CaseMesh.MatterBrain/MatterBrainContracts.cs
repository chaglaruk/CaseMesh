using CaseMesh.Core.Models;

namespace CaseMesh.MatterBrain;

public enum ExtractionCandidateKind
{
    Person = 0,
    Organisation = 1,
    Communication = 2,
    Assertion = 3,
    Event = 4,
    AssertionEventLink = 5,
    EntityMatch = 6,
    Contradiction = 7
}

public enum CandidateDisposition
{
    Validated = 0,
    Rejected = 1
}

public enum CanonicalRecordKind
{
    Person = 0,
    Organisation = 1,
    Communication = 2,
    Assertion = 3,
    Event = 4,
    AssertionEventLink = 5,
    Contradiction = 6,
    AnalysisNode = 7
}

public enum EntityResolutionActionKind
{
    Proposed = 0,
    Accepted = 1,
    Rejected = 2,
    Reversed = 3
}

public sealed record StructuredExtractionProviderDescriptor(
    string Provider,
    string Model,
    string ExtractionVersion,
    string PromptVersion,
    string SchemaVersion);

public sealed record StructuredSourceSpan(Guid Id, string Text, string TextDigest);

public sealed record StructuredExtractionInput(
    TenantId TenantId,
    Guid MatterId,
    IReadOnlyList<StructuredSourceSpan> SourceSpans);

public interface IStructuredCandidate
{
    string Key { get; }
    IReadOnlyList<Guid> SourceSpanIds { get; }
    decimal? ExtractionConfidence { get; }
}

public sealed record EntityCandidate(
    string Key,
    CanonicalEntityKind Kind,
    string DisplayName,
    string TypeLabel,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> RoleLabels,
    IReadOnlyList<Guid> SourceSpanIds,
    decimal? ExtractionConfidence) : IStructuredCandidate;

public sealed record CommunicationCandidate(
    string Key,
    CommunicationKind Kind,
    string NeutralLabel,
    DateTimeOffset? OccurredAt,
    string? SenderEntityKey,
    IReadOnlyList<string> ParticipantEntityKeys,
    IReadOnlyList<Guid> SourceSpanIds,
    decimal? ExtractionConfidence) : IStructuredCandidate;

public sealed record AssertionCandidate(
    string Key,
    string SubjectReference,
    string Predicate,
    string Value,
    string AssertedBy,
    DateTimeOffset AssertedAt,
    DateTimeOffset? EventTime,
    Guid? SourceSpanId,
    EvidenceOriginClass OriginClass,
    AssertionClass AssertionClass,
    IntegrityState IntegrityState,
    IReadOnlyList<Guid> SourceSpanIds,
    decimal? ExtractionConfidence) : IStructuredCandidate;

public sealed record EventCandidate(
    string Key,
    string EventType,
    string NeutralLabel,
    DateTimeOffset? StartTime,
    DateTimeOffset? EndTime,
    IReadOnlyList<string> ParticipantEntityKeys,
    IReadOnlyList<Guid> SourceSpanIds,
    decimal? ExtractionConfidence) : IStructuredCandidate;

public sealed record AssertionEventLinkCandidate(
    string Key,
    string AssertionKey,
    string EventKey,
    AssertionEventRelation Relation,
    IReadOnlyList<Guid> SourceSpanIds,
    decimal? ExtractionConfidence) : IStructuredCandidate;

public sealed record EntityMatchCandidate(
    string Key,
    CanonicalEntityKind Kind,
    string SourceEntityKey,
    string TargetEntityKey,
    decimal MatchScore,
    IReadOnlyList<Guid> SourceSpanIds,
    decimal? ExtractionConfidence) : IStructuredCandidate;

public sealed record ContradictionCandidate(
    string Key,
    string AssertionAKey,
    string AssertionBKey,
    ContradictionType Type,
    string DetectedBy,
    IReadOnlyList<Guid> SourceSpanIds,
    decimal? ExtractionConfidence) : IStructuredCandidate;

public sealed record StructuredCandidateBatch(
    IReadOnlyList<EntityCandidate> Entities,
    IReadOnlyList<CommunicationCandidate> Communications,
    IReadOnlyList<AssertionCandidate> Assertions,
    IReadOnlyList<EventCandidate> Events,
    IReadOnlyList<AssertionEventLinkCandidate> AssertionEventLinks,
    IReadOnlyList<EntityMatchCandidate> EntityMatches,
    IReadOnlyList<ContradictionCandidate> Contradictions);

public sealed record StructuredExtractionOutput(
    string RawStructuredResult,
    StructuredCandidateBatch Candidates);

public interface IStructuredExtractionProvider
{
    StructuredExtractionProviderDescriptor Descriptor { get; }

    Task<StructuredExtractionOutput> ExtractAsync(
        StructuredExtractionInput input,
        CancellationToken cancellationToken = default);
}

public sealed record ExtractionRun(
    Guid Id,
    Guid MatterId,
    string Fingerprint,
    StructuredExtractionProviderDescriptor Provider,
    IReadOnlyList<Guid> SourceSpanIds,
    DateTimeOffset GeneratedAt,
    string RawResultDigest);

public sealed record ExtractionCandidateRecord(
    Guid Id,
    Guid MatterId,
    Guid RunId,
    string ExternalKey,
    ExtractionCandidateKind Kind,
    CandidateDisposition Disposition,
    string? RejectionCode,
    IReadOnlyList<Guid> SourceSpanIds,
    decimal? ExtractionConfidence,
    CanonicalRecordKind? CanonicalKind,
    Guid? CanonicalId,
    string PayloadJson,
    string PayloadDigest);

public sealed record MatterBrainDependency(
    Guid Id,
    Guid MatterId,
    Guid RunId,
    Guid SourceSpanId,
    Guid CandidateId,
    CanonicalRecordKind CanonicalKind,
    Guid CanonicalId);

public sealed record DependencyInvalidation(
    Guid Id,
    Guid MatterId,
    Guid DependencyId,
    Guid? InvalidatedByRunId,
    Guid? InvalidatedByAuditEventId,
    DateTimeOffset InvalidatedAt);

public sealed record MatterBrainSnapshot(
    Guid MatterId,
    IReadOnlyList<Person> People,
    IReadOnlyList<Organisation> Organisations,
    IReadOnlyList<EntityAlias> Aliases,
    IReadOnlyList<Communication> Communications,
    IReadOnlyList<ExtractionRun> Runs,
    IReadOnlyList<ExtractionCandidateRecord> Candidates,
    IReadOnlyList<MatterBrainDependency> Dependencies,
    IReadOnlyList<DependencyInvalidation> DependencyInvalidations,
    IReadOnlyList<EntityResolutionAction> EntityResolutionActions);

public sealed record EntityResolutionAction(
    Guid Id,
    Guid MatterId,
    Guid ProposalId,
    EntityResolutionActionKind Kind,
    CanonicalEntityKind EntityKind,
    Guid SourceEntityId,
    Guid TargetEntityId,
    IReadOnlyList<Guid> EvidenceSourceSpanIds,
    decimal? MatchScore,
    string Actor,
    DateTimeOffset OccurredAt,
    Guid? ReversesActionId);

public sealed record MatterBrainMergeResult(
    ExtractionRun Run,
    IReadOnlyList<ExtractionCandidateRecord> Candidates,
    IReadOnlyList<Guid> ChangedCanonicalIds,
    bool WasAlreadyCompleted);
