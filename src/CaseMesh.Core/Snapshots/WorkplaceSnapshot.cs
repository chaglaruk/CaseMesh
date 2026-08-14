using CaseMesh.Core.Models;
using CaseMesh.Core.Workplace;

namespace CaseMesh.Core.Snapshots;

public sealed record WorkplaceSnapshot(
    IReadOnlyList<EmploymentProfileSnapshot> EmploymentProfiles,
    IReadOnlyList<EmploymentTermSnapshot> EmploymentTerms,
    IReadOnlyList<HealthAbsenceSnapshot> HealthAbsenceRecords,
    IReadOnlyList<AdjustmentRequestSnapshot> AdjustmentRequests,
    IReadOnlyList<WorkplaceProcessSnapshot> WorkplaceProcesses,
    IReadOnlyList<AcasProcessStateSnapshot> AcasProcessStates);

public sealed record EmploymentProfileSnapshot(
    Guid Id,
    string EmployerReference,
    string RoleTitle,
    DateOnly? EmploymentStartedOn,
    DateOnly? EmploymentEndedOn,
    IReadOnlyList<Guid> SupportingAssertionIds,
    VerificationState EvidenceReviewState);

public sealed record EmploymentTermSnapshot(
    Guid Id,
    EmploymentTermKind Kind,
    string Value,
    DateOnly? EffectiveFrom,
    DateOnly? EffectiveTo,
    IReadOnlyList<Guid> SupportingAssertionIds,
    Guid? SupersedesEmploymentTermId);

public sealed record HealthAbsenceSnapshot(
    Guid Id,
    HealthAbsenceKind Kind,
    string NeutralLabel,
    IReadOnlyList<Guid> AssertionIds,
    IReadOnlyList<Guid> EventIds,
    VerificationState EvidenceReviewState);

public sealed record AdjustmentRequestSnapshot(
    Guid Id,
    string NeutralLabel,
    IReadOnlyList<Guid> RequestAssertionIds,
    AdjustmentResponseStatus ResponseStatus,
    IReadOnlyList<Guid> ResponseAssertionIds,
    IReadOnlyList<Guid> ImplementationAssertionIds);

public sealed record WorkplaceProcessSnapshot(
    Guid Id,
    WorkplaceProcessKind Kind,
    string StageLabel,
    WorkplaceProcessStatus Status,
    IReadOnlyList<Guid> AssertionIds,
    IReadOnlyList<Guid> EventIds,
    Guid? SupersedesWorkplaceProcessId,
    Guid? SupersessionAuditEventId);

public sealed record AcasProcessStateSnapshot(
    Guid Id,
    AcasStage Stage,
    IReadOnlyList<Guid> AssertionIds,
    IReadOnlyList<Guid> EventIds);
