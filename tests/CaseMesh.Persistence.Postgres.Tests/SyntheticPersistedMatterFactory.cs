using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Core.Workplace;

namespace CaseMesh.Persistence.Postgres.Tests;

internal static class SyntheticPersistedMatterFactory
{
    internal static readonly DateTimeOffset RecordedAt = new(2026, 5, 1, 9, 0, 0, TimeSpan.Zero);

    internal static PersistedMatter Create(TenantId tenantId, Guid matterId, int seed)
    {
        var matter = new Matter(
            matterId,
            tenantId,
            "workplace-dispute",
            $"Synthetic persisted matter {seed}",
            "open",
            RecordedAt,
            RecordedAt,
            "England and Wales");
        var evidence = new MatterEvidenceGraph(matter);
        var workplace = new WorkplaceMatter(evidence);

        var employerSource = AddSource(evidence, seed, 10, "Example Employer Ltd states 12 sickness days.", 'A');
        var duplicateVersion = evidence.RegisterDocumentVersion(
            Id(seed, 16), Id(seed, 17), new string('A', 64), Id(seed, 18));
        var attendanceSource = AddSource(evidence, seed, 20, "Synthetic attendance rows total 10 sickness days.", 'B');
        var originalTermSource = AddSource(evidence, seed, 30, "Synthetic contract records 37.5 hours.", 'C');
        var amendedTermSource = AddSource(evidence, seed, 40, "Synthetic amendment records 40 hours.", 'D');
        var ohSource = AddSource(evidence, seed, 50, "Synthetic OH report recommends adjusted hours.", 'E');
        var requestSource = AddSource(evidence, seed, 60, "Synthetic employee requests adjusted hours.", 'F');
        var responseSource = AddSource(evidence, seed, 70, "Example Employer Ltd accepts adjusted hours.", '1');
        var implementationSource = AddSource(evidence, seed, 80, "Synthetic rota records adjusted hours in use.", '2');
        var processSource = AddSource(evidence, seed, 90, "Synthetic grievance meeting notice dated 12 March.", '3');

        var employerTwelve = AddAssertion(evidence, seed, 110, employerSource, "sickness-day-count", "12",
            "Example Employer Ltd", EvidenceOriginClass.EmployerAuthoredDocument, AssertionClass.EmployerAssertion);
        var attendanceTen = AddAssertion(evidence, seed, 111, attendanceSource, "sickness-day-count", "10",
            "synthetic attendance record", EvidenceOriginClass.OriginalContemporaneousRecord, AssertionClass.DerivedCalculation);
        evidence.AddContradiction(Id(seed, 112), employerTwelve.Id, attendanceTen.Id,
            ContradictionType.NumericMismatch, "synthetic-rule", RecordedAt);

        var originalTermAssertion = AddAssertion(evidence, seed, 120, originalTermSource, "working-hours", "37.5",
            "synthetic contract", EvidenceOriginClass.EmployeeAuthoredDocument, AssertionClass.AttributedAssertion);
        var amendedTermAssertion = AddAssertion(evidence, seed, 121, amendedTermSource, "working-hours", "40",
            "synthetic amendment", EvidenceOriginClass.EmployerAuthoredDocument, AssertionClass.EmployerAssertion);
        var ohAssertion = AddAssertion(evidence, seed, 122, ohSource, "oh-recommendation", "adjusted hours",
            "Synthetic Occupational Health", EvidenceOriginClass.IndependentThirdPartyRecord, AssertionClass.ThirdPartyAssertion);
        var requestAssertion = AddAssertion(evidence, seed, 123, requestSource, "adjustment-request", "adjusted hours",
            "synthetic employee", EvidenceOriginClass.EmployeeAuthoredDocument, AssertionClass.UserAssertion);
        var responseAssertion = AddAssertion(evidence, seed, 124, responseSource, "adjustment-response", "accepted",
            "Example Employer Ltd", EvidenceOriginClass.EmployerAuthoredDocument, AssertionClass.EmployerAssertion);
        var implementationAssertion = AddAssertion(evidence, seed, 125, implementationSource, "adjustment-action", "implemented",
            "synthetic rota", EvidenceOriginClass.OriginalContemporaneousRecord, AssertionClass.DirectlyDocumentedEvent);
        var processAssertion = AddAssertion(evidence, seed, 126, processSource, "grievance-meeting-date", "12 March",
            "Example Employer Ltd", EvidenceOriginClass.EmployerAuthoredDocument, AssertionClass.EmployerAssertion);
        evidence.AddAssertion(
            Id(seed, 127), "synthetic-witness", "withdrawn-context", "withdrawn", "synthetic witness",
            RecordedAt, EvidenceOriginClass.ParticipantOrWitnessStatement, AssertionClass.ThirdPartyAssertion,
            DisputeState.Superseded, IntegrityState.MetadataUncertain, VerificationState.Rejected);

        var originalEvent = evidence.AddEvent(
            Id(seed, 130), "grievance-meeting", "Synthetic meeting on 12 March",
            EventStatus.Candidate, VerificationState.Confirmed,
            new DateTimeOffset(2026, 3, 12, 10, 0, 0, TimeSpan.Zero));
        evidence.AddAssertionEventLink(Id(seed, 131), processAssertion.Id, originalEvent.Id, AssertionEventRelation.Supports);
        var originalProcess = workplace.AddWorkplaceProcess(
            Id(seed, 132), WorkplaceProcessKind.Grievance, "Meeting extracted", WorkplaceProcessStatus.Open,
            [processAssertion.Id], [originalEvent.Id]);
        var correction = evidence.CorrectEventDate(
            originalEvent.Id, Id(seed, 133), new DateTimeOffset(2026, 3, 13, 10, 0, 0, TimeSpan.Zero), null,
            "Synthetic meeting on 13 March", Id(seed, 134), "synthetic-reviewer", RecordedAt.AddHours(1));
        evidence.AddAssertionEventLink(
            Id(seed, 135), processAssertion.Id, correction.CorrectedEvent.Id, AssertionEventRelation.Supports);
        evidence.AddEvent(
            Id(seed, 136), "withdrawn-meeting", "Synthetic rejected meeting",
            EventStatus.Rejected, VerificationState.Rejected);

        evidence.AddAnalysisNode(
            Id(seed, 140), "synthetic-gap-analysis", [employerSource.Id, attendanceSource.Id],
            "synthetic-provider", "synthetic-model", "v1", "Synthetic evidence remains disputed.",
            RecordedAt, VerificationState.NotReviewed);

        workplace.AddEmploymentProfile(
            Id(seed, 150), "example-employer", "Example role", new DateOnly(2024, 1, 1),
            supportingAssertionIds: [originalTermAssertion.Id]);
        var originalTerm = workplace.AddEmploymentTerm(
            Id(seed, 151), EmploymentTermKind.WorkingHours, "37.5 hours", [originalTermAssertion.Id],
            effectiveFrom: new DateOnly(2024, 1, 1));
        workplace.AddEmploymentTerm(
            Id(seed, 152), EmploymentTermKind.WorkingHours, "40 hours", [amendedTermAssertion.Id],
            effectiveFrom: new DateOnly(2024, 6, 1), supersedesEmploymentTermId: originalTerm.Id);
        workplace.AddHealthAbsenceRecord(
            Id(seed, 153), HealthAbsenceKind.ReportedSicknessAbsence, "Employer-reported absence",
            [employerTwelve.Id]);
        workplace.AddHealthAbsenceRecord(
            Id(seed, 154), HealthAbsenceKind.AttendanceRecord, "Attendance evidence", [attendanceTen.Id]);
        workplace.AddHealthAbsenceRecord(
            Id(seed, 155), HealthAbsenceKind.OccupationalHealthRecommendation, "OH recommendation", [ohAssertion.Id]);
        workplace.AddAdjustmentRequest(
            Id(seed, 156), "Adjusted hours", [requestAssertion.Id], AdjustmentResponseStatus.Accepted,
            [responseAssertion.Id], [implementationAssertion.Id]);
        workplace.AddWorkplaceProcess(
            Id(seed, 157), WorkplaceProcessKind.Grievance, "Meeting corrected", WorkplaceProcessStatus.Open,
            [processAssertion.Id], [correction.CorrectedEvent.Id], originalProcess.Id, correction.AuditEvent.Id);
        workplace.AddAcasProcessState(Id(seed, 158), AcasStage.CertificateRecorded, [processAssertion.Id]);

        if (duplicateVersion.OriginalObjectId != employerSource.DocumentVersion.OriginalObjectId)
        {
            throw new InvalidOperationException("Synthetic duplicate version did not share the logical original.");
        }

        return new PersistedMatter(evidence, workplace);
    }

    private static SourceSpan AddSource(
        MatterEvidenceGraph evidence,
        int seed,
        int offset,
        string text,
        char hashCharacter)
    {
        var version = evidence.RegisterDocumentVersion(
            Id(seed, offset), Id(seed, offset + 1), new string(hashCharacter, 64), Id(seed, offset + 2));
        return evidence.AddSourceSpan(
            Id(seed, offset + 3), version, text, "synthetic-parser/1", 0.99m,
            pageNumber: 1, textStart: 0, textEnd: text.Length);
    }

    private static Assertion AddAssertion(
        MatterEvidenceGraph evidence,
        int seed,
        int offset,
        SourceSpan source,
        string predicate,
        string value,
        string assertedBy,
        EvidenceOriginClass origin,
        AssertionClass assertionClass) => evidence.AddAssertion(
            Id(seed, offset), "synthetic-employee", predicate, value, assertedBy, RecordedAt,
            origin, assertionClass, DisputeState.Unverified, IntegrityState.OriginalHashVerified,
            VerificationState.NotReviewed, source.Id, extractionConfidence: 0.98m);

    internal static Guid Id(int seed, int offset) => Guid.Parse($"{seed:X8}-0000-0000-0000-{offset:D12}");
}
