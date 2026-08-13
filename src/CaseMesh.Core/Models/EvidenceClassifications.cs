namespace CaseMesh.Core.Models;

public enum EvidenceOriginClass
{
    OriginalContemporaneousRecord = 0,
    IndependentThirdPartyRecord,
    EmployerAuthoredDocument,
    EmployeeAuthoredDocument,
    ParticipantOrWitnessStatement,
    RetrospectiveNote,
    TranscriptDerivedRecord,
    OcrDerivedRecord,
    AiGeneratedInference
}

public enum AssertionClass
{
    DirectlyDocumentedEvent = 0,
    DirectQuotation,
    AttributedAssertion,
    UserAssertion,
    EmployerAssertion,
    ThirdPartyAssertion,
    DerivedCalculation,
    AiInference
}

public enum DisputeState
{
    Corroborated = 0,
    Uncorroborated,
    Disputed,
    Contradicted,
    Superseded,
    Incomplete,
    Unverified
}

public enum IntegrityState
{
    OriginalHashVerified = 0,
    Duplicate,
    DerivedCopy,
    Incomplete,
    OcrUncertain,
    MetadataUncertain
}

public enum VerificationState
{
    NotReviewed = 0,
    Confirmed,
    Rejected,
    NeedsContext
}

public enum AssertionEventRelation
{
    Supports = 0,
    Contradicts,
    Qualifies,
    Supersedes,
    Contextualizes
}

public enum EventStatus
{
    Candidate = 0,
    Confirmed,
    Disputed,
    Superseded,
    Rejected
}

public enum ContradictionType
{
    NumericMismatch = 0,
    TemporalMismatch,
    AttributionMismatch,
    DirectConflict,
    Other
}

public enum ContradictionResolutionState
{
    Unresolved = 0,
    Resolved,
    Dismissed
}

public enum AuditEventKind
{
    EventCorrected = 0,
    AssertionCorrected,
    AssertionRejected,
    ContradictionResolved,
    AnalysisSuperseded
}
