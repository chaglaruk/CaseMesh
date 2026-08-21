using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Core.Workplace;
using CaseMesh.MatterBrain;
using CaseMesh.ProfessionalExport;

namespace CaseMesh.ProfessionalExport.Tests;

internal static class SyntheticProfessionalExportFixture
{
    internal static readonly DateTimeOffset RecordedAt = new(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);
    internal static readonly TenantId Tenant = new(Id(1));
    internal static readonly Guid MatterId = Id(2);
    internal static readonly Guid ExportId = Id(3);

    internal static async Task<ProfessionalExportInput> CreateAsync(int seed = 700)
    {
        var tenant = seed == 700 ? Tenant : new TenantId(Id(seed, 1));
        var matterId = seed == 700 ? MatterId : Id(seed, 2);
        var matter = new Matter(matterId, tenant, "workplace-dispute", "Synthetic professional handover",
            "open", RecordedAt, RecordedAt, "England and Wales");
        var evidence = new MatterEvidenceGraph(matter);
        var workplace = new WorkplaceMatter(evidence);
        var employer = AddSource(evidence, seed, 10, "Example Employer states 12 sickness days.", 'A');
        _ = evidence.RegisterDocumentVersion(Id(seed, 15), Id(seed, 16), new string('A', 64), Id(seed, 17));
        var attendance = AddSource(evidence, seed, 20, "Synthetic attendance rows total 10 sickness days.", 'B');
        var oldTerm = AddSource(evidence, seed, 30, "Synthetic contract records 37.5 hours.", 'C');
        var newTerm = AddSource(evidence, seed, 40, "Synthetic amendment records 40 hours.", 'D');
        var oh = AddSource(evidence, seed, 50, "Synthetic OH report recommends adjusted hours.", 'E');
        var request = AddSource(evidence, seed, 60, "Synthetic employee requests adjusted hours.", 'F');
        var response = AddSource(evidence, seed, 70, "Example Employer records an accepted response.", '1');
        var implementation = AddSource(evidence, seed, 80, "Synthetic rota records adjusted hours in use.", '2');
        var processDate = AddSource(evidence, seed, 90, "Example Employer notice alleges a meeting on 12 March.", '3');
        var correctedDate = AddSource(evidence, seed, 100, "Synthetic contemporaneous diary records 13 March.", '4');
        var employerAction = AddSource(evidence, seed, 110, "Example Employer records a rota action.", '5');
        var unansweredRequest = AddSource(evidence, seed, 120, "Synthetic employee requests a quiet workspace.", '6');

        var twelve = AddAssertion(evidence, seed, 200, employer, "sickness-day-count", "12", "Example Employer",
            EvidenceOriginClass.EmployerAuthoredDocument, AssertionClass.EmployerAssertion);
        var ten = AddAssertion(evidence, seed, 201, attendance, "sickness-day-count", "10", "Synthetic attendance record",
            EvidenceOriginClass.OriginalContemporaneousRecord, AssertionClass.DerivedCalculation);
        evidence.AddContradiction(Id(seed, 202), twelve.Id, ten.Id, ContradictionType.NumericMismatch,
            "synthetic-rule", RecordedAt);
        var oldTermAssertion = AddAssertion(evidence, seed, 203, oldTerm, "working-hours", "37.5", "Synthetic contract",
            EvidenceOriginClass.EmployeeAuthoredDocument, AssertionClass.AttributedAssertion);
        var newTermAssertion = AddAssertion(evidence, seed, 204, newTerm, "working-hours", "40", "Example Employer",
            EvidenceOriginClass.EmployerAuthoredDocument, AssertionClass.EmployerAssertion);
        evidence.AddContradiction(Id(seed, 205), oldTermAssertion.Id, newTermAssertion.Id,
            ContradictionType.DirectConflict, "synthetic-review", RecordedAt);
        var ohAssertion = AddAssertion(evidence, seed, 206, oh, "oh-recommendation", "adjusted hours", "Synthetic OH clinician",
            EvidenceOriginClass.IndependentThirdPartyRecord, AssertionClass.ThirdPartyAssertion);
        var requestAssertion = AddAssertion(evidence, seed, 207, request, "adjustment-request", "adjusted hours", "Synthetic employee",
            EvidenceOriginClass.EmployeeAuthoredDocument, AssertionClass.UserAssertion);
        var responseAssertion = AddAssertion(evidence, seed, 208, response, "adjustment-response", "accepted", "Example Employer",
            EvidenceOriginClass.EmployerAuthoredDocument, AssertionClass.EmployerAssertion);
        var implementationAssertion = AddAssertion(evidence, seed, 209, implementation, "adjustment-implementation", "rota changed", "Synthetic rota",
            EvidenceOriginClass.OriginalContemporaneousRecord, AssertionClass.DirectlyDocumentedEvent);
        var processAssertion = AddAssertion(evidence, seed, 210, processDate, "meeting-date", "2026-03-12", "Example Employer",
            EvidenceOriginClass.EmployerAuthoredDocument, AssertionClass.EmployerAssertion,
            new DateTimeOffset(2026, 3, 12, 10, 0, 0, TimeSpan.Zero));
        var correctedDateAssertion = AddAssertion(evidence, seed, 211, correctedDate, "meeting-date", "2026-03-13", "Synthetic diary",
            EvidenceOriginClass.OriginalContemporaneousRecord, AssertionClass.DirectlyDocumentedEvent,
            new DateTimeOffset(2026, 3, 13, 10, 0, 0, TimeSpan.Zero));
        var actionAssertion = AddAssertion(evidence, seed, 212, employerAction, "employer-action", "rota action recorded", "Example Employer",
            EvidenceOriginClass.EmployerAuthoredDocument, AssertionClass.EmployerAssertion);
        var unanswered = AddAssertion(evidence, seed, 213, unansweredRequest, "adjustment-request", "quiet workspace", "Synthetic employee",
            EvidenceOriginClass.EmployeeAuthoredDocument, AssertionClass.UserAssertion);
        evidence.AddAssertion(Id(seed, 214), "synthetic-matter", "possible-context", "Further context may be relevant",
            "CaseMesh AI", RecordedAt, EvidenceOriginClass.AiGeneratedInference, AssertionClass.AiInference,
            DisputeState.Unverified, IntegrityState.MetadataUncertain, VerificationState.NotReviewed,
            createdByModel: "synthetic-model");

        var originalEvent = evidence.AddEvent(Id(seed, 220), "grievance-meeting", "Meeting alleged for 12 March",
            EventStatus.Candidate, VerificationState.NotReviewed,
            new DateTimeOffset(2026, 3, 12, 10, 0, 0, TimeSpan.Zero));
        evidence.AddAssertionEventLink(Id(seed, 221), processAssertion.Id, originalEvent.Id, AssertionEventRelation.Supports);
        var originalProcess = workplace.AddWorkplaceProcess(Id(seed, 222), WorkplaceProcessKind.Grievance,
            "Meeting date extracted", WorkplaceProcessStatus.Open, [processAssertion.Id], [originalEvent.Id]);
        var correction = evidence.CorrectEventDate(originalEvent.Id, Id(seed, 223),
            new DateTimeOffset(2026, 3, 13, 10, 0, 0, TimeSpan.Zero), null,
            "Meeting alleged for 13 March", Id(seed, 224), "synthetic-reviewer", RecordedAt.AddHours(1));
        evidence.AddAssertionEventLink(Id(seed, 225), correctedDateAssertion.Id,
            correction.CorrectedEvent.Id, AssertionEventRelation.Supports);
        evidence.AddEvent(Id(seed, 226), "undated-follow-up", "Undated synthetic follow-up",
            EventStatus.Candidate, VerificationState.NotReviewed);
        evidence.AddEvent(Id(seed, 227), "absence-period", "Synthetic alleged absence range",
            EventStatus.Candidate, VerificationState.NotReviewed,
            new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 4, 3, 23, 59, 59, TimeSpan.Zero));

        workplace.AddEmploymentProfile(Id(seed, 230), "example-employer", "Synthetic role",
            new DateOnly(2024, 1, 1), supportingAssertionIds: [oldTermAssertion.Id]);
        var firstTerm = workplace.AddEmploymentTerm(Id(seed, 231), EmploymentTermKind.WorkingHours,
            "37.5 hours", [oldTermAssertion.Id], effectiveFrom: new DateOnly(2024, 1, 1));
        workplace.AddEmploymentTerm(Id(seed, 232), EmploymentTermKind.WorkingHours,
            "40 hours", [newTermAssertion.Id], effectiveFrom: new DateOnly(2024, 6, 1),
            supersedesEmploymentTermId: firstTerm.Id);
        workplace.AddHealthAbsenceRecord(Id(seed, 233), HealthAbsenceKind.ReportedSicknessAbsence,
            "Employer-reported absence", [twelve.Id]);
        workplace.AddHealthAbsenceRecord(Id(seed, 234), HealthAbsenceKind.AttendanceRecord,
            "Attendance record", [ten.Id]);
        workplace.AddHealthAbsenceRecord(Id(seed, 235), HealthAbsenceKind.OccupationalHealthRecommendation,
            "OH recommendation", [ohAssertion.Id]);
        workplace.AddAdjustmentRequest(Id(seed, 236), "Adjusted hours", [requestAssertion.Id],
            AdjustmentResponseStatus.Accepted, [responseAssertion.Id], [implementationAssertion.Id]);
        workplace.AddAdjustmentRequest(Id(seed, 237), "Quiet workspace", [unanswered.Id]);
        workplace.AddWorkplaceProcess(Id(seed, 238), WorkplaceProcessKind.Grievance,
            "Meeting date corrected", WorkplaceProcessStatus.Open, [correctedDateAssertion.Id],
            [correction.CorrectedEvent.Id], originalProcess.Id, correction.AuditEvent.Id);
        workplace.AddAcasProcessState(Id(seed, 239), AcasStage.NotRecorded);
        evidence.ReviewAssertion(actionAssertion.Id, VerificationState.Confirmed,
            Id(seed, 240), "synthetic-reviewer", RecordedAt.AddHours(1));

        var brain = new MatterBrainState(evidence);
        var batch = new StructuredCandidateBatch(
            [
                new EntityCandidate("employee", CanonicalEntityKind.Person, "Alex Example", "person",
                    ["the employee"], ["employee"], [request.Id, response.Id], 0.9m),
                new EntityCandidate("employer", CanonicalEntityKind.Organisation, "Example Employer", "employer",
                    ["the employer"], [], [employer.Id], 0.9m),
                new EntityCandidate("oh-provider", CanonicalEntityKind.Organisation, "Synthetic OH Service", "health provider",
                    ["OH"], [], [oh.Id], 0.9m)
            ],
            [new CommunicationCandidate("response-letter", CommunicationKind.Letter, "Adjustment response letter",
                RecordedAt, "employer", ["employee", "employer"], [response.Id], 0.9m)],
            [new AssertionCandidate("extracted-context", "synthetic-employee", "extracted-context",
                "context candidate", "Synthetic employee", RecordedAt, null, request.Id,
                EvidenceOriginClass.EmployeeAuthoredDocument, AssertionClass.AttributedAssertion,
                IntegrityState.OriginalHashVerified, [request.Id], 0.8m)],
            [], [], [], []);
        await new MatterBrainMergeService(new FixedTimeProvider(RecordedAt.AddHours(2)))
            .ExtractAndMergeAsync(brain, [request.Id, employer.Id, oh.Id, response.Id], new GoldenProvider(batch));

        var documents = evidence.DocumentVersions.OrderBy(item => item.DocumentVersionId).Select((item, index) =>
            new ExportDocumentMetadata(
                tenant, matterId, item.DocumentId, item.DocumentVersionId, item.OriginalObjectId,
                item.ContentSha256, (index % 4) switch { 0 => "pdf", 1 => "docx", 2 => "eml", _ => "png" },
                1_000 + index, ExportDocumentProcessingStatus.Completed,
                item.DocumentVersionId == oh.DocumentVersion.DocumentVersionId
                    ? ExportExtractionRoute.Ocr
                    : ExportExtractionRoute.Native,
                [item.DocumentVersionId == oh.DocumentVersion.DocumentVersionId ? "none" : "synthetic-parser/1"],
                item.DocumentVersionId == oh.DocumentVersion.DocumentVersionId ? ["synthetic-ocr"] : [],
                item.DocumentVersionId == oh.DocumentVersion.DocumentVersionId ? ["1.0"] : [])).ToArray();
        var sourceMetadata = evidence.SourceSpans.OrderBy(item => item.Id).Select(item =>
        {
            var isOcr = item.Id == oh.Id;
            return new ExportSourceMetadata(
                tenant, matterId, item.Id, item.DocumentVersion.DocumentVersionId,
                isOcr ? ExportSourceLocatorKind.ImageBoundingBox : ExportSourceLocatorKind.PdfPage,
                isOcr ? "ocr:page:1:bbox:10,20,30,40" : "pdf:page:1",
                isOcr ? ExportExtractionRoute.Ocr : ExportExtractionRoute.Native,
                isOcr ? "synthetic-ocr" : "synthetic-parser",
                isOcr ? "1.0" : "1",
                isOcr ? 10 : null, isOcr ? 20 : null, isOcr ? 30 : null, isOcr ? 40 : null);
        }).ToArray();
        return new ProfessionalExportInput(evidence, workplace, brain, documents, sourceMetadata);
    }

    internal static ProfessionalExportRequest Request(Guid? exportId = null) =>
        new(Tenant, MatterId, exportId ?? ExportId);

    internal static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");
    internal static Guid Id(int seed, int value) => Guid.Parse($"{seed:X8}-0000-0000-0000-{value:D12}");

    private static SourceSpan AddSource(
        MatterEvidenceGraph evidence, int seed, int offset, string text, char hashCharacter)
    {
        var version = evidence.RegisterDocumentVersion(
            Id(seed, offset), Id(seed, offset + 1), new string(hashCharacter, 64), Id(seed, offset + 2));
        return evidence.AddSourceSpan(Id(seed, offset + 3), version, text, "synthetic-parser/1", 0.95m,
            pageNumber: 1, textStart: 0, textEnd: text.Length);
    }

    private static Assertion AddAssertion(
        MatterEvidenceGraph evidence, int seed, int offset, SourceSpan source,
        string predicate, string value, string assertedBy, EvidenceOriginClass origin,
        AssertionClass assertionClass, DateTimeOffset? eventTime = null) => evidence.AddAssertion(
        Id(seed, offset), "synthetic-employee", predicate, value, assertedBy, RecordedAt,
        origin, assertionClass, DisputeState.Unverified, IntegrityState.OriginalHashVerified,
        VerificationState.NotReviewed, source.Id, eventTime, 0.9m);

    private sealed class GoldenProvider(StructuredCandidateBatch batch) : IStructuredExtractionProvider
    {
        public StructuredExtractionProviderDescriptor Descriptor { get; } =
            new("synthetic-provider", "synthetic-model", "extract/v1", "prompt/v1", "schema/v1");

        public Task<StructuredExtractionOutput> ExtractAsync(
            StructuredExtractionInput input,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new StructuredExtractionOutput("{\"synthetic\":true}", batch));
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
