namespace CaseMesh.Core.Workplace;

public enum EmploymentTermKind
{
    JobTitle = 0,
    Salary,
    WorkingHours,
    WorkLocation,
    NoticePeriod,
    SickPay,
    Other
}

public enum HealthAbsenceKind
{
    ReportedSicknessAbsence = 0,
    AttendanceRecord,
    OccupationalHealthRecommendation,
    ReturnToWorkRecord,
    Other
}

public enum AdjustmentResponseStatus
{
    NotRecorded = 0,
    Partial,
    Refused,
    Accepted
}

public enum WorkplaceProcessKind
{
    Grievance = 0,
    Disciplinary,
    Capability,
    Appeal,
    Redundancy,
    Redeployment,
    Other
}

public enum WorkplaceProcessStatus
{
    NotStarted = 0,
    Open,
    Paused,
    Closed,
    Appealed
}

public enum AcasStage
{
    NotRecorded = 0,
    EarlyConciliationContacted,
    CertificateRecorded,
    Closed
}
