using CaseMesh.Core.Models;

namespace CaseMesh.Live;

public enum CanonicalLiveCurrentness
{
    Current = 0,
    Processing = 1
}

public enum LiveEvidenceRecordStatus
{
    Current = 0,
    Historical = 1
}

public enum LiveConversationOrigin
{
    HrSaid = 0,
    UserActuallySaid = 1,
    AiSuggested = 2
}

public sealed record LiveSourceCitation(
    Guid SourceSpanId,
    Guid DocumentId,
    Guid DocumentVersionId,
    Guid OriginalObjectId,
    string ContentSha256,
    int? PageNumber,
    int? TextStart,
    int? TextEnd,
    string ExactText,
    string ExactTextDigest);

public sealed record CanonicalLiveEvidenceItem(
    Guid AssertionId,
    string SubjectReference,
    string Predicate,
    string Value,
    string AssertedBy,
    DateTimeOffset? EventTime,
    DateTimeOffset AssertedAt,
    EvidenceOriginClass OriginClass,
    AssertionClass AssertionClass,
    DisputeState DisputeState,
    VerificationState VerificationState,
    LiveEvidenceRecordStatus RecordStatus,
    string? HistoricalReason,
    LiveSourceCitation Citation);

public sealed record CanonicalLiveAnalysisItem(
    Guid AssertionId,
    string SubjectReference,
    string Predicate,
    string Value,
    string AssertedBy,
    DateTimeOffset AssertedAt,
    string CreatedByModel);

public sealed record CanonicalLiveContradiction(
    Guid ContradictionId,
    Guid AssertionAId,
    Guid AssertionBId,
    ContradictionType Type,
    string DetectionOrigin);

public sealed record CanonicalLiveContext(
    TenantId TenantId,
    Guid MatterId,
    string MatterTitle,
    CanonicalLiveCurrentness Currentness,
    IReadOnlyList<CanonicalLiveEvidenceItem> Evidence,
    IReadOnlyList<CanonicalLiveAnalysisItem> AiAnalysis,
    IReadOnlyList<CanonicalLiveContradiction> UnresolvedContradictions);

public sealed record LiveConversationItem(
    Guid Id,
    LiveConversationOrigin Origin,
    string Text,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    IReadOnlyList<Guid> ContextCitationSourceSpanIds);

public sealed record UploadedMeetingReview(
    TenantId TenantId,
    Guid MatterId,
    Guid MeetingId,
    CanonicalLiveCurrentness ContextCurrentness,
    IReadOnlyList<LiveConversationItem> Items);
