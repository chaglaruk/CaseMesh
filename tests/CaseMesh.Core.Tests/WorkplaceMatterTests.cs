using System.Reflection;
using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Core.Workplace;

namespace CaseMesh.Core.Tests;

public sealed class WorkplaceMatterTests
{
    [Fact]
    public void Rehydrate_rejects_self_referential_workplace_supersession()
    {
        var evidence = SyntheticWorkplaceMatterFixture.CreateGraph(10);
        var workplace = new WorkplaceMatter(evidence);
        var assertion = AddDocumentaryAssertion(evidence, 20, "Synthetic workplace evidence.");
        var matterEvent = evidence.AddEvent(
            Id(30),
            "synthetic-event",
            "Synthetic workplace event",
            EventStatus.Candidate,
            VerificationState.NotReviewed);
        var term = workplace.AddEmploymentTerm(
            Id(31), EmploymentTermKind.WorkingHours, "37.5 hours", [assertion.Id]);
        var process = workplace.AddWorkplaceProcess(
            Id(32), WorkplaceProcessKind.Grievance, "Initial", WorkplaceProcessStatus.Open,
            [assertion.Id], [matterEvent.Id]);
        var snapshot = workplace.CaptureSnapshot();

        Assert.Throws<InvalidOperationException>(() => WorkplaceMatter.Rehydrate(evidence, snapshot with
        {
            EmploymentTerms = snapshot.EmploymentTerms.Select(item => item.Id == term.Id
                ? item with { SupersedesEmploymentTermId = item.Id }
                : item).ToArray()
        }));
        Assert.Throws<InvalidOperationException>(() => WorkplaceMatter.Rehydrate(evidence, snapshot with
        {
            WorkplaceProcesses = snapshot.WorkplaceProcesses.Select(item => item.Id == process.Id
                ? item with { SupersedesWorkplaceProcessId = item.Id }
                : item).ToArray()
        }));
    }

    [Fact]
    public void ExtensionRecords_AllUseTheOwningMatterBoundary()
    {
        var evidence = SyntheticWorkplaceMatterFixture.CreateGraph();
        var workplace = new WorkplaceMatter(evidence);
        var assertion = AddDocumentaryAssertion(evidence, 1000, "Synthetic employment context.");
        var matterEvent = evidence.AddEvent(
            Id(1010), "synthetic-event", "Synthetic workplace event",
            EventStatus.Candidate, VerificationState.NotReviewed);

        var profile = workplace.AddEmploymentProfile(
            Id(1020), "example-employer", "Example role", supportingAssertionIds: [assertion.Id]);
        var term = workplace.AddEmploymentTerm(
            Id(1021), EmploymentTermKind.JobTitle, "Example role", [assertion.Id]);
        var absence = workplace.AddHealthAbsenceRecord(
            Id(1022), HealthAbsenceKind.ReportedSicknessAbsence, "Reported absence", [assertion.Id]);
        var adjustment = workplace.AddAdjustmentRequest(
            Id(1023), "Example adjustment request", [assertion.Id]);
        var process = workplace.AddWorkplaceProcess(
            Id(1024), WorkplaceProcessKind.Grievance, "Initial stage", WorkplaceProcessStatus.Open,
            [assertion.Id], [matterEvent.Id]);
        var acas = workplace.AddAcasProcessState(Id(1025), AcasStage.CertificateRecorded, [assertion.Id]);

        Assert.All(
            new[] { profile.MatterId, term.MatterId, absence.MatterId, adjustment.MatterId, process.MatterId, acas.MatterId },
            matterId => Assert.Equal(evidence.Matter.Id, matterId));
    }

    [Fact]
    public void EmploymentProfile_IsContextAndMayRemainUnverifiedWithoutEvidence()
    {
        var workplace = new WorkplaceMatter(SyntheticWorkplaceMatterFixture.CreateGraph());

        var profile = workplace.AddEmploymentProfile(
            Id(1100),
            "example-employer",
            "Example role",
            new DateOnly(2024, 1, 1));

        Assert.False(profile.HasSupportingAssertions);
        Assert.Equal(VerificationState.NotReviewed, profile.EvidenceReviewState);
    }

    [Fact]
    public void EmploymentProfile_CannotBeConfirmedWithoutSupportingEvidence()
    {
        var workplace = new WorkplaceMatter(SyntheticWorkplaceMatterFixture.CreateGraph());

        Assert.Throws<InvalidOperationException>(() => workplace.AddEmploymentProfile(
            Id(1110), "example-employer", "Example role", evidenceReviewState: VerificationState.Confirmed));
        Assert.Empty(workplace.EmploymentProfiles);
    }

    [Fact]
    public void ConflictingEmploymentTerms_PreserveBothSourcedVersions()
    {
        var evidence = SyntheticWorkplaceMatterFixture.CreateGraph();
        var workplace = new WorkplaceMatter(evidence);
        var contractAssertion = AddDocumentaryAssertion(
            evidence, 1200, "The synthetic contract records 37.5 weekly hours.", "37.5");
        var amendmentAssertion = AddDocumentaryAssertion(
            evidence, 1210, "The synthetic amendment records 40 weekly hours.", "40");
        var original = workplace.AddEmploymentTerm(
            Id(1220), EmploymentTermKind.WorkingHours, "37.5 hours", [contractAssertion.Id],
            effectiveFrom: new DateOnly(2024, 1, 1));
        var amendment = workplace.AddEmploymentTerm(
            Id(1221), EmploymentTermKind.WorkingHours, "40 hours", [amendmentAssertion.Id],
            effectiveFrom: new DateOnly(2024, 6, 1), supersedesEmploymentTermId: original.Id);

        evidence.AddContradiction(
            Id(1222), contractAssertion.Id, amendmentAssertion.Id, ContradictionType.TemporalMismatch,
            "synthetic-term-comparison", SyntheticWorkplaceMatterFixture.RecordedAt);

        Assert.Equal(2, workplace.EmploymentTerms.Count);
        Assert.Contains(workplace.EmploymentTerms, term => term.Id == original.Id && term.Value == "37.5 hours");
        Assert.Contains(workplace.EmploymentTerms, term => term.Id == amendment.Id && term.Value == "40 hours");
        Assert.Equal(original.Id, amendment.SupersedesEmploymentTermId);
        Assert.Equal(2, evidence.Assertions.Count);
        Assert.Single(evidence.Contradictions);
    }

    [Fact]
    public void EmploymentTerm_RequiresSourceBackedGenericAssertion()
    {
        var evidence = SyntheticWorkplaceMatterFixture.CreateGraph();
        var workplace = new WorkplaceMatter(evidence);
        var unbacked = evidence.AddAssertion(
            Id(1300), "synthetic-employment", "working-hours", "40", "synthetic-user",
            SyntheticWorkplaceMatterFixture.RecordedAt,
            EvidenceOriginClass.ParticipantOrWitnessStatement,
            AssertionClass.UserAssertion,
            DisputeState.Unverified,
            IntegrityState.MetadataUncertain,
            VerificationState.NotReviewed);

        Assert.Throws<InvalidOperationException>(() => workplace.AddEmploymentTerm(
            Id(1301), EmploymentTermKind.WorkingHours, "40 hours", [unbacked.Id]));
    }

    [Fact]
    public void EmployerAbsenceCount_RemainsSeparateFromConflictingAttendanceEvidence()
    {
        var evidence = SyntheticWorkplaceMatterFixture.CreateGraph();
        var workplace = new WorkplaceMatter(evidence);
        var employerAssertion = AddDocumentaryAssertion(
            evidence, 1400, "Example Employer Ltd states 12 sickness days.", "12",
            EvidenceOriginClass.EmployerAuthoredDocument, AssertionClass.EmployerAssertion, "Example Employer Ltd");
        var attendanceAssertion = AddDocumentaryAssertion(
            evidence, 1410, "Synthetic attendance rows total 10 sickness days.", "10",
            EvidenceOriginClass.OriginalContemporaneousRecord, AssertionClass.DerivedCalculation,
            "synthetic attendance record");
        var employerRecord = workplace.AddHealthAbsenceRecord(
            Id(1420), HealthAbsenceKind.ReportedSicknessAbsence, "Employer-reported absence count",
            [employerAssertion.Id]);
        var attendanceRecord = workplace.AddHealthAbsenceRecord(
            Id(1421), HealthAbsenceKind.AttendanceRecord, "Attendance-record count",
            [attendanceAssertion.Id]);

        evidence.AddContradiction(
            Id(1422), employerAssertion.Id, attendanceAssertion.Id, ContradictionType.NumericMismatch,
            "synthetic-absence-comparison", SyntheticWorkplaceMatterFixture.RecordedAt);

        Assert.Equal(VerificationState.NotReviewed, employerRecord.EvidenceReviewState);
        Assert.Equal(VerificationState.NotReviewed, attendanceRecord.EvidenceReviewState);
        Assert.Equal(2, workplace.HealthAbsenceRecords.Count);
        Assert.All(evidence.Assertions, assertion => Assert.Equal(DisputeState.Contradicted, assertion.DisputeState));
    }

    [Fact]
    public void OccupationalHealthRecommendation_IsSeparateFromEmployerResponseAndAction()
    {
        var evidence = SyntheticWorkplaceMatterFixture.CreateGraph();
        var workplace = new WorkplaceMatter(evidence);
        var recommendation = AddDocumentaryAssertion(
            evidence, 1500, "Synthetic OH report recommends adjusted working hours.", "adjusted hours",
            EvidenceOriginClass.IndependentThirdPartyRecord, AssertionClass.ThirdPartyAssertion,
            "Synthetic Occupational Health");
        var employeeRequest = AddDocumentaryAssertion(
            evidence, 1510, "Synthetic employee requests adjusted working hours.", "adjusted hours",
            EvidenceOriginClass.EmployeeAuthoredDocument, AssertionClass.UserAssertion, "synthetic employee");
        var employerResponse = AddDocumentaryAssertion(
            evidence, 1520, "Example Employer Ltd accepts the request in principle.", "accepted in principle",
            EvidenceOriginClass.EmployerAuthoredDocument, AssertionClass.EmployerAssertion, "Example Employer Ltd");

        var ohRecord = workplace.AddHealthAbsenceRecord(
            Id(1530), HealthAbsenceKind.OccupationalHealthRecommendation,
            "Occupational Health recommendation", [recommendation.Id]);
        var request = workplace.AddAdjustmentRequest(
            Id(1531), "Adjusted working hours", [employeeRequest.Id], AdjustmentResponseStatus.Accepted,
            [employerResponse.Id]);

        Assert.Equal(recommendation.Id, Assert.Single(ohRecord.AssertionIds));
        Assert.Equal(employeeRequest.Id, Assert.Single(request.RequestAssertionIds));
        Assert.Equal(employerResponse.Id, Assert.Single(request.ResponseAssertionIds));
        Assert.False(request.HasImplementationEvidence);
        Assert.Empty(request.ImplementationAssertionIds);
    }

    [Theory]
    [InlineData(AdjustmentResponseStatus.Partial)]
    [InlineData(AdjustmentResponseStatus.Refused)]
    [InlineData(AdjustmentResponseStatus.Accepted)]
    public void AdjustmentRequest_ResponseStatusDoesNotCollapseRequestAndResponse(
        AdjustmentResponseStatus responseStatus)
    {
        var evidence = SyntheticWorkplaceMatterFixture.CreateGraph();
        var workplace = new WorkplaceMatter(evidence);
        var requestAssertion = AddDocumentaryAssertion(
            evidence, 1600, "Synthetic adjustment request.", "requested",
            EvidenceOriginClass.EmployeeAuthoredDocument, AssertionClass.UserAssertion, "synthetic employee");
        var responseAssertion = AddDocumentaryAssertion(
            evidence, 1610, $"Synthetic employer response: {responseStatus}.", responseStatus.ToString(),
            EvidenceOriginClass.EmployerAuthoredDocument, AssertionClass.EmployerAssertion, "Example Employer Ltd");

        var request = workplace.AddAdjustmentRequest(
            Id(1620), "Synthetic adjustment", [requestAssertion.Id], responseStatus, [responseAssertion.Id]);

        Assert.NotEqual(Assert.Single(request.RequestAssertionIds), Assert.Single(request.ResponseAssertionIds));
        Assert.Equal(responseStatus, request.ResponseStatus);
        Assert.False(request.HasImplementationEvidence);
    }

    [Fact]
    public void AdjustmentEvidenceGroups_CannotReuseTheSameAssertion()
    {
        var evidence = SyntheticWorkplaceMatterFixture.CreateGraph();
        var workplace = new WorkplaceMatter(evidence);
        var assertion = AddDocumentaryAssertion(evidence, 1700, "Synthetic request and response text.");

        Assert.Throws<InvalidOperationException>(() => workplace.AddAdjustmentRequest(
            Id(1710), "Synthetic adjustment", [assertion.Id], AdjustmentResponseStatus.Partial, [assertion.Id]));
    }

    [Fact]
    public void GrievanceAndCapabilityProcesses_RemainNeutralAndSourceLinked()
    {
        var evidence = SyntheticWorkplaceMatterFixture.CreateGraph();
        var workplace = new WorkplaceMatter(evidence);
        var grievanceAssertion = AddDocumentaryAssertion(
            evidence, 1800, "Synthetic grievance acknowledgement.", "acknowledged");
        var capabilityAssertion = AddDocumentaryAssertion(
            evidence, 1810, "Synthetic capability invitation.", "invited");
        var grievanceEvent = evidence.AddEvent(
            Id(1820), "grievance-meeting", "Synthetic grievance meeting",
            EventStatus.Candidate, VerificationState.NotReviewed, SyntheticWorkplaceMatterFixture.RecordedAt);
        var capabilityEvent = evidence.AddEvent(
            Id(1821), "capability-meeting", "Synthetic capability meeting",
            EventStatus.Candidate, VerificationState.NotReviewed,
            SyntheticWorkplaceMatterFixture.RecordedAt.AddDays(7));
        evidence.AddAssertionEventLink(Id(1822), grievanceAssertion.Id, grievanceEvent.Id, AssertionEventRelation.Supports);
        evidence.AddAssertionEventLink(Id(1823), capabilityAssertion.Id, capabilityEvent.Id, AssertionEventRelation.Supports);

        var grievance = workplace.AddWorkplaceProcess(
            Id(1830), WorkplaceProcessKind.Grievance, "Acknowledgement and meeting",
            WorkplaceProcessStatus.Open, [grievanceAssertion.Id], [grievanceEvent.Id]);
        var capability = workplace.AddWorkplaceProcess(
            Id(1831), WorkplaceProcessKind.Capability, "Invitation and meeting",
            WorkplaceProcessStatus.Open, [capabilityAssertion.Id], [capabilityEvent.Id]);

        Assert.Equal(WorkplaceProcessStatus.Open, grievance.Status);
        Assert.Equal(WorkplaceProcessStatus.Open, capability.Status);
        Assert.All(evidence.Events, matterEvent => Assert.Equal(EventStatus.Candidate, matterEvent.Status));
        Assert.Equal(2, evidence.AssertionEventLinks.Count);
    }

    [Fact]
    public void WorkplaceRecord_CannotReferenceAnotherMattersEvidence()
    {
        var firstEvidence = SyntheticWorkplaceMatterFixture.CreateGraph(1);
        var secondEvidence = SyntheticWorkplaceMatterFixture.CreateGraph(2);
        var workplace = new WorkplaceMatter(firstEvidence);
        var foreignAssertion = AddDocumentaryAssertion(secondEvidence, 1900, "Synthetic foreign evidence.");
        var foreignEvent = secondEvidence.AddEvent(
            Id(1910), "foreign-event", "Foreign event", EventStatus.Candidate, VerificationState.NotReviewed);

        Assert.Throws<InvalidOperationException>(() => workplace.AddEmploymentTerm(
            Id(1920), EmploymentTermKind.JobTitle, "Foreign title", [foreignAssertion.Id]));
        Assert.Throws<InvalidOperationException>(() => workplace.AddEmploymentProfile(
            Id(1921), "foreign-employer", "Foreign role", supportingAssertionIds: [foreignAssertion.Id]));
        Assert.Throws<InvalidOperationException>(() => workplace.AddHealthAbsenceRecord(
            Id(1922), HealthAbsenceKind.AttendanceRecord, "Foreign absence", [foreignAssertion.Id]));
        Assert.Throws<InvalidOperationException>(() => workplace.AddAdjustmentRequest(
            Id(1923), "Foreign request", [foreignAssertion.Id]));
        Assert.Throws<InvalidOperationException>(() => workplace.AddAcasProcessState(
            Id(1924), AcasStage.CertificateRecorded, [foreignAssertion.Id]));
        Assert.Throws<InvalidOperationException>(() => workplace.AddWorkplaceProcess(
            Id(1925), WorkplaceProcessKind.Grievance, "Foreign assertion stage", WorkplaceProcessStatus.Open,
            assertionIds: [foreignAssertion.Id]));
        Assert.Throws<InvalidOperationException>(() => workplace.AddWorkplaceProcess(
            Id(1926), WorkplaceProcessKind.Grievance, "Foreign event stage", WorkplaceProcessStatus.Open,
            eventIds: [foreignEvent.Id]));
        Assert.Empty(workplace.EmploymentProfiles);
        Assert.Empty(workplace.EmploymentTerms);
        Assert.Empty(workplace.HealthAbsenceRecords);
        Assert.Empty(workplace.AdjustmentRequests);
        Assert.Empty(workplace.WorkplaceProcesses);
        Assert.Empty(workplace.AcasProcessStates);
    }

    [Fact]
    public void CorrectedProcessEvent_PreservesGenericAuditAndWorkplaceHistory()
    {
        var evidence = SyntheticWorkplaceMatterFixture.CreateGraph();
        var workplace = new WorkplaceMatter(evidence);
        var sourceAssertion = AddDocumentaryAssertion(
            evidence, 2000, "Synthetic process letter dated 12 March.", "12 March");
        var extractedDate = new DateTimeOffset(2026, 3, 12, 0, 0, 0, TimeSpan.Zero);
        var originalEvent = evidence.AddEvent(
            Id(2010), "grievance-meeting", "Synthetic grievance meeting on 12 March",
            EventStatus.Candidate, VerificationState.NotReviewed, extractedDate);
        var originalProcess = workplace.AddWorkplaceProcess(
            Id(2020), WorkplaceProcessKind.Grievance, "Meeting date extracted",
            WorkplaceProcessStatus.Open, [sourceAssertion.Id], [originalEvent.Id]);

        var correction = evidence.CorrectEventDate(
            originalEvent.Id, Id(2011), extractedDate.AddDays(1), null,
            "Synthetic grievance meeting on 13 March", Id(2012), "synthetic-reviewer",
            SyntheticWorkplaceMatterFixture.RecordedAt);
        var correctedProcess = workplace.AddWorkplaceProcess(
            Id(2021), WorkplaceProcessKind.Grievance, "Meeting date corrected",
            WorkplaceProcessStatus.Open, [sourceAssertion.Id], [correction.CorrectedEvent.Id], originalProcess.Id,
            correction.AuditEvent.Id);

        Assert.Equal(2, workplace.WorkplaceProcesses.Count);
        Assert.Equal(originalProcess.Id, correctedProcess.SupersedesWorkplaceProcessId);
        Assert.Equal(correction.AuditEvent.Id, correctedProcess.SupersessionAuditEventId);
        Assert.Contains(workplace.WorkplaceProcesses, process => process.EventIds.Contains(originalEvent.Id));
        Assert.Contains(workplace.WorkplaceProcesses, process => process.EventIds.Contains(correction.CorrectedEvent.Id));
        Assert.Single(evidence.AuditEvents);
        Assert.Equal(AuditEventKind.EventCorrected, correction.AuditEvent.Kind);
    }

    [Fact]
    public void WorkplaceProcessSupersession_RequiresMatchingGenericAudit()
    {
        var evidence = SyntheticWorkplaceMatterFixture.CreateGraph();
        var workplace = new WorkplaceMatter(evidence);
        var sourceAssertion = AddDocumentaryAssertion(evidence, 2040, "Synthetic process letter.");
        var processEvent = evidence.AddEvent(
            Id(2050), "grievance-meeting", "Synthetic grievance meeting",
            EventStatus.Candidate, VerificationState.NotReviewed, SyntheticWorkplaceMatterFixture.RecordedAt);
        var originalProcess = workplace.AddWorkplaceProcess(
            Id(2060), WorkplaceProcessKind.Grievance, "Meeting arranged",
            WorkplaceProcessStatus.Open, [sourceAssertion.Id], [processEvent.Id]);

        Assert.Throws<InvalidOperationException>(() => workplace.AddWorkplaceProcess(
            Id(2061), WorkplaceProcessKind.Grievance, "Meeting corrected",
            WorkplaceProcessStatus.Open, [sourceAssertion.Id], [processEvent.Id], originalProcess.Id));
        Assert.Single(workplace.WorkplaceProcesses);
    }

    [Fact]
    public void NewWorkplaceRecord_CannotReferenceSupersededOrRejectedEvent()
    {
        var evidence = SyntheticWorkplaceMatterFixture.CreateGraph();
        var workplace = new WorkplaceMatter(evidence);
        var superseded = evidence.AddEvent(
            Id(2070), "grievance-meeting", "Synthetic superseded event",
            EventStatus.Superseded, VerificationState.NotReviewed);
        var rejected = evidence.AddEvent(
            Id(2071), "grievance-meeting", "Synthetic rejected event",
            EventStatus.Rejected, VerificationState.Rejected);

        Assert.Throws<InvalidOperationException>(() => workplace.AddHealthAbsenceRecord(
            Id(2072), HealthAbsenceKind.ReportedSicknessAbsence, "Superseded event", eventIds: [superseded.Id]));
        Assert.Throws<InvalidOperationException>(() => workplace.AddAcasProcessState(
            Id(2073), AcasStage.EarlyConciliationContacted, eventIds: [rejected.Id]));
        Assert.Throws<InvalidOperationException>(() => workplace.AddWorkplaceProcess(
            Id(2074), WorkplaceProcessKind.Grievance, "Superseded event", WorkplaceProcessStatus.Open,
            eventIds: [superseded.Id]));
        Assert.Empty(workplace.HealthAbsenceRecords);
        Assert.Empty(workplace.AcasProcessStates);
        Assert.Empty(workplace.WorkplaceProcesses);
    }

    [Fact]
    public void WorkplaceRegistration_RejectsUndefinedEnumValues()
    {
        var evidence = SyntheticWorkplaceMatterFixture.CreateGraph();
        var workplace = new WorkplaceMatter(evidence);
        var assertion = AddDocumentaryAssertion(evidence, 2080, "Synthetic enum guard evidence.");

        Assert.Throws<ArgumentOutOfRangeException>(() => workplace.AddEmploymentProfile(
            Id(2090), "example-employer", "Example role", evidenceReviewState: (VerificationState)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => workplace.AddEmploymentTerm(
            Id(2091), (EmploymentTermKind)999, "Example value", [assertion.Id]));
        Assert.Throws<ArgumentOutOfRangeException>(() => workplace.AddHealthAbsenceRecord(
            Id(2092), (HealthAbsenceKind)999, "Example absence", [assertion.Id]));
        Assert.Throws<ArgumentOutOfRangeException>(() => workplace.AddHealthAbsenceRecord(
            Id(2093), HealthAbsenceKind.AttendanceRecord, "Example absence", [assertion.Id],
            evidenceReviewState: (VerificationState)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => workplace.AddAdjustmentRequest(
            Id(2094), "Example request", [assertion.Id], (AdjustmentResponseStatus)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => workplace.AddWorkplaceProcess(
            Id(2095), (WorkplaceProcessKind)999, "Example stage", WorkplaceProcessStatus.Open,
            assertionIds: [assertion.Id]));
        Assert.Throws<ArgumentOutOfRangeException>(() => workplace.AddWorkplaceProcess(
            Id(2096), WorkplaceProcessKind.Grievance, "Example stage", (WorkplaceProcessStatus)999,
            assertionIds: [assertion.Id]));
        Assert.Throws<ArgumentOutOfRangeException>(() => workplace.AddAcasProcessState(
            Id(2097), (AcasStage)999, [assertion.Id]));
        Assert.Empty(workplace.EmploymentProfiles);
        Assert.Empty(workplace.EmploymentTerms);
        Assert.Empty(workplace.HealthAbsenceRecords);
        Assert.Empty(workplace.AdjustmentRequests);
        Assert.Empty(workplace.WorkplaceProcesses);
        Assert.Empty(workplace.AcasProcessStates);
    }

    [Fact]
    public void AcasState_IsDescriptiveEvidenceLinkedAndContainsNoDeadlineCalculation()
    {
        var evidence = SyntheticWorkplaceMatterFixture.CreateGraph();
        var workplace = new WorkplaceMatter(evidence);
        var certificateAssertion = AddDocumentaryAssertion(
            evidence, 2100, "Synthetic Acas certificate reference recorded.", "certificate recorded");

        var state = workplace.AddAcasProcessState(
            Id(2110), AcasStage.CertificateRecorded, [certificateAssertion.Id]);

        Assert.Equal(AcasStage.CertificateRecorded, state.Stage);
        Assert.Equal(certificateAssertion.Id, Assert.Single(state.AssertionIds));
        Assert.DoesNotContain(
            typeof(AcasProcessState).GetProperties(),
            property => property.Name.Contains("Deadline", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WorkplaceDomain_ContainsNoOutcomeOrScoringConcepts()
    {
        var forbiddenTerms = new[] { "Compensation", "Liability", "Outcome", "TruthScore", "WinProbability", "Deadline" };
        var workplaceTypes = typeof(WorkplaceMatter).Assembly.GetTypes()
            .Where(type => type.IsPublic && type.Namespace == typeof(WorkplaceMatter).Namespace)
            .ToArray();
        var publicNames = workplaceTypes
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Select(property => property.Name)
                .Concat(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Select(method => method.Name))
                .Concat(type.IsEnum ? Enum.GetNames(type) : [])
                .Append(type.Name))
            .ToArray();

        Assert.All(forbiddenTerms, forbidden =>
            Assert.DoesNotContain(publicNames, name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase)));
    }

    private static Assertion AddDocumentaryAssertion(
        MatterEvidenceGraph evidence,
        int seed,
        string text,
        string value = "synthetic value",
        EvidenceOriginClass originClass = EvidenceOriginClass.EmployeeAuthoredDocument,
        AssertionClass assertionClass = AssertionClass.AttributedAssertion,
        string assertedBy = "synthetic author")
    {
        var hashCharacter = "0123456789ABCDEF"[seed % 16];
        var source = SyntheticWorkplaceMatterFixture.AddSource(evidence, seed, text, hashCharacter);
        return evidence.AddAssertion(
            Id(seed + 4),
            "synthetic-subject",
            "synthetic-predicate",
            value,
            assertedBy,
            SyntheticWorkplaceMatterFixture.RecordedAt,
            originClass,
            assertionClass,
            DisputeState.Unverified,
            IntegrityState.OriginalHashVerified,
            VerificationState.NotReviewed,
            source.Id,
            extractionConfidence: 0.98m);
    }

    private static Guid Id(int value) => SyntheticWorkplaceMatterFixture.Id(value);
}
