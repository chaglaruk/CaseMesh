using CaseMesh.Core.Models;

namespace CaseMesh.Core.Workplace;

public sealed record EmploymentProfile
{
    internal EmploymentProfile(
        Guid id,
        Guid matterId,
        string employerReference,
        string roleTitle,
        DateOnly? employmentStartedOn,
        DateOnly? employmentEndedOn,
        IReadOnlyList<Guid> supportingAssertionIds,
        VerificationState evidenceReviewState)
    {
        Id = id;
        MatterId = matterId;
        EmployerReference = employerReference;
        RoleTitle = roleTitle;
        EmploymentStartedOn = employmentStartedOn;
        EmploymentEndedOn = employmentEndedOn;
        SupportingAssertionIds = supportingAssertionIds;
        EvidenceReviewState = evidenceReviewState;
    }

    public Guid Id { get; }
    public Guid MatterId { get; }
    public string EmployerReference { get; }
    public string RoleTitle { get; }
    public DateOnly? EmploymentStartedOn { get; }
    public DateOnly? EmploymentEndedOn { get; }
    public IReadOnlyList<Guid> SupportingAssertionIds { get; }
    public VerificationState EvidenceReviewState { get; }
    public bool HasSupportingAssertions => SupportingAssertionIds.Count > 0;
}

public sealed record EmploymentTerm
{
    internal EmploymentTerm(
        Guid id,
        Guid matterId,
        EmploymentTermKind kind,
        string value,
        DateOnly? effectiveFrom,
        DateOnly? effectiveTo,
        IReadOnlyList<Guid> supportingAssertionIds,
        Guid? supersedesEmploymentTermId)
    {
        Id = id;
        MatterId = matterId;
        Kind = kind;
        Value = value;
        EffectiveFrom = effectiveFrom;
        EffectiveTo = effectiveTo;
        SupportingAssertionIds = supportingAssertionIds;
        SupersedesEmploymentTermId = supersedesEmploymentTermId;
    }

    public Guid Id { get; }
    public Guid MatterId { get; }
    public EmploymentTermKind Kind { get; }
    public string Value { get; }
    public DateOnly? EffectiveFrom { get; }
    public DateOnly? EffectiveTo { get; }
    public IReadOnlyList<Guid> SupportingAssertionIds { get; }
    public Guid? SupersedesEmploymentTermId { get; }
}

public sealed record HealthAbsenceRecord
{
    internal HealthAbsenceRecord(
        Guid id,
        Guid matterId,
        HealthAbsenceKind kind,
        string neutralLabel,
        IReadOnlyList<Guid> assertionIds,
        IReadOnlyList<Guid> eventIds,
        VerificationState evidenceReviewState)
    {
        Id = id;
        MatterId = matterId;
        Kind = kind;
        NeutralLabel = neutralLabel;
        AssertionIds = assertionIds;
        EventIds = eventIds;
        EvidenceReviewState = evidenceReviewState;
    }

    public Guid Id { get; }
    public Guid MatterId { get; }
    public HealthAbsenceKind Kind { get; }
    public string NeutralLabel { get; }
    public IReadOnlyList<Guid> AssertionIds { get; }
    public IReadOnlyList<Guid> EventIds { get; }
    public VerificationState EvidenceReviewState { get; }
}

public sealed record AdjustmentRequest
{
    internal AdjustmentRequest(
        Guid id,
        Guid matterId,
        string neutralLabel,
        IReadOnlyList<Guid> requestAssertionIds,
        AdjustmentResponseStatus responseStatus,
        IReadOnlyList<Guid> responseAssertionIds,
        IReadOnlyList<Guid> implementationAssertionIds)
    {
        Id = id;
        MatterId = matterId;
        NeutralLabel = neutralLabel;
        RequestAssertionIds = requestAssertionIds;
        ResponseStatus = responseStatus;
        ResponseAssertionIds = responseAssertionIds;
        ImplementationAssertionIds = implementationAssertionIds;
    }

    public Guid Id { get; }
    public Guid MatterId { get; }
    public string NeutralLabel { get; }
    public IReadOnlyList<Guid> RequestAssertionIds { get; }
    public AdjustmentResponseStatus ResponseStatus { get; }
    public IReadOnlyList<Guid> ResponseAssertionIds { get; }
    public IReadOnlyList<Guid> ImplementationAssertionIds { get; }
    public bool HasImplementationEvidence => ImplementationAssertionIds.Count > 0;
}

public sealed record WorkplaceProcess
{
    internal WorkplaceProcess(
        Guid id,
        Guid matterId,
        WorkplaceProcessKind kind,
        string stageLabel,
        WorkplaceProcessStatus status,
        IReadOnlyList<Guid> assertionIds,
        IReadOnlyList<Guid> eventIds,
        Guid? supersedesWorkplaceProcessId,
        Guid? supersessionAuditEventId)
    {
        Id = id;
        MatterId = matterId;
        Kind = kind;
        StageLabel = stageLabel;
        Status = status;
        AssertionIds = assertionIds;
        EventIds = eventIds;
        SupersedesWorkplaceProcessId = supersedesWorkplaceProcessId;
        SupersessionAuditEventId = supersessionAuditEventId;
    }

    public Guid Id { get; }
    public Guid MatterId { get; }
    public WorkplaceProcessKind Kind { get; }
    public string StageLabel { get; }
    public WorkplaceProcessStatus Status { get; }
    public IReadOnlyList<Guid> AssertionIds { get; }
    public IReadOnlyList<Guid> EventIds { get; }
    public Guid? SupersedesWorkplaceProcessId { get; }
    public Guid? SupersessionAuditEventId { get; }
}

public sealed record AcasProcessState
{
    internal AcasProcessState(
        Guid id,
        Guid matterId,
        AcasStage stage,
        IReadOnlyList<Guid> assertionIds,
        IReadOnlyList<Guid> eventIds)
    {
        Id = id;
        MatterId = matterId;
        Stage = stage;
        AssertionIds = assertionIds;
        EventIds = eventIds;
    }

    public Guid Id { get; }
    public Guid MatterId { get; }
    public AcasStage Stage { get; }
    public IReadOnlyList<Guid> AssertionIds { get; }
    public IReadOnlyList<Guid> EventIds { get; }
}
