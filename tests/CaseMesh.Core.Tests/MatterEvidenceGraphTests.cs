using CaseMesh.Core.Models;
using CaseMesh.Core.Services;

namespace CaseMesh.Core.Tests;

public sealed class MatterEvidenceGraphTests
{
    [Fact]
    public void Matter_IsGenericAndDoesNotRequireEmploymentFields()
    {
        var matter = new Matter(
            SyntheticWorkplaceMatterFixture.Id(1),
            "generic-dispute",
            "Synthetic matter",
            "open",
            SyntheticWorkplaceMatterFixture.RecordedAt,
            SyntheticWorkplaceMatterFixture.RecordedAt);

        Assert.Equal("generic-dispute", matter.MatterType);
        Assert.Null(matter.Jurisdiction);
        Assert.DoesNotContain(
            matter.GetType().GetProperties(),
            property => property.Name.Contains("Employment", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SourceSpan_ResolvesThroughImmutableVersionToOriginalHash()
    {
        var graph = SyntheticWorkplaceMatterFixture.CreateGraph();
        var source = SyntheticWorkplaceMatterFixture.AddSource(
            graph,
            10,
            "The synthetic capability letter records 12 sickness days.",
            'A');

        Assert.Equal(graph.Matter.Id, source.MatterId);
        Assert.Equal(SyntheticWorkplaceMatterFixture.Id(10), source.DocumentVersion.DocumentId);
        Assert.Equal(SyntheticWorkplaceMatterFixture.Id(11), source.DocumentVersion.DocumentVersionId);
        Assert.Equal(new string('A', 64), source.DocumentVersion.ContentSha256);
        Assert.Equal(64, source.ExtractedTextDigest.Length);
    }

    [Fact]
    public void DocumentVersionIdentity_CannotBeReRegisteredWithDifferentContent()
    {
        var graph = SyntheticWorkplaceMatterFixture.CreateGraph();
        graph.RegisterDocumentVersion(
            SyntheticWorkplaceMatterFixture.Id(10),
            SyntheticWorkplaceMatterFixture.Id(11),
            new string('A', 64),
            SyntheticWorkplaceMatterFixture.Id(12));

        var error = Assert.Throws<InvalidOperationException>(() => graph.RegisterDocumentVersion(
            SyntheticWorkplaceMatterFixture.Id(10),
            SyntheticWorkplaceMatterFixture.Id(11),
            new string('B', 64),
            SyntheticWorkplaceMatterFixture.Id(13)));

        Assert.Contains("immutable", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateVersions_WithSameHashShareOneLogicalOriginal()
    {
        var graph = SyntheticWorkplaceMatterFixture.CreateGraph();
        var first = graph.RegisterDocumentVersion(
            SyntheticWorkplaceMatterFixture.Id(10),
            SyntheticWorkplaceMatterFixture.Id(11),
            new string('C', 64),
            SyntheticWorkplaceMatterFixture.Id(12));
        var duplicate = graph.RegisterDocumentVersion(
            SyntheticWorkplaceMatterFixture.Id(20),
            SyntheticWorkplaceMatterFixture.Id(21),
            new string('c', 64),
            SyntheticWorkplaceMatterFixture.Id(22));

        Assert.NotEqual(first.DocumentVersionId, duplicate.DocumentVersionId);
        Assert.Equal(first.OriginalObjectId, duplicate.OriginalObjectId);
        Assert.Equal(1, graph.LogicalOriginalCount);
        Assert.Equal(2, graph.DocumentVersions.Count);
    }

    [Fact]
    public void TwelveVersusTenSicknessDays_PreservesBothAssertionsAndContradiction()
    {
        var graph = SyntheticWorkplaceMatterFixture.CreateGraph();
        var employerSource = SyntheticWorkplaceMatterFixture.AddSource(
            graph,
            10,
            "Example Employer Ltd states that the employee had 12 sickness days.",
            'D');
        var attendanceSource = SyntheticWorkplaceMatterFixture.AddSource(
            graph,
            20,
            "Synthetic attendance rows total 10 sickness days.",
            'E');
        var twelveDays = SyntheticWorkplaceMatterFixture.AddSicknessDayAssertion(
            graph,
            30,
            employerSource,
            "12",
            EvidenceOriginClass.EmployerAuthoredDocument,
            AssertionClass.EmployerAssertion,
            "Example Employer Ltd");
        var tenDays = SyntheticWorkplaceMatterFixture.AddSicknessDayAssertion(
            graph,
            31,
            attendanceSource,
            "10",
            EvidenceOriginClass.OriginalContemporaneousRecord,
            AssertionClass.DerivedCalculation,
            "synthetic attendance record");

        var contradiction = graph.AddContradiction(
            SyntheticWorkplaceMatterFixture.Id(32),
            twelveDays.Id,
            tenDays.Id,
            ContradictionType.NumericMismatch,
            "deterministic-test-rule",
            SyntheticWorkplaceMatterFixture.RecordedAt);

        Assert.Equal(2, graph.Assertions.Count);
        Assert.Contains(graph.Assertions, assertion => assertion.Id == twelveDays.Id && assertion.Value == "12");
        Assert.Contains(graph.Assertions, assertion => assertion.Id == tenDays.Id && assertion.Value == "10");
        Assert.Equal(twelveDays.Id, contradiction.AssertionAId);
        Assert.Equal(tenDays.Id, contradiction.AssertionBId);
        Assert.Equal(ContradictionResolutionState.Unresolved, contradiction.ResolutionState);
    }

    [Fact]
    public void CorrectedEventDate_PreservesRejectedExtractionAndAppendsAuditEvent()
    {
        var graph = SyntheticWorkplaceMatterFixture.CreateGraph();
        var extractedDate = new DateTimeOffset(2026, 3, 12, 0, 0, 0, TimeSpan.Zero);
        var correctedDate = extractedDate.AddDays(1);
        var extractedEvent = graph.AddEvent(
            SyntheticWorkplaceMatterFixture.Id(40),
            "synthetic-meeting",
            "Synthetic review meeting",
            EventStatus.Candidate,
            VerificationState.NotReviewed,
            extractedDate,
            extractedDate);

        var correction = graph.CorrectEventDate(
            extractedEvent.Id,
            SyntheticWorkplaceMatterFixture.Id(41),
            correctedDate,
            correctedDate,
            SyntheticWorkplaceMatterFixture.Id(42),
            "synthetic-user",
            SyntheticWorkplaceMatterFixture.RecordedAt);

        Assert.Equal(2, graph.Events.Count);
        Assert.Equal(EventStatus.Superseded, correction.SupersededEvent.Status);
        Assert.Equal(VerificationState.Rejected, correction.SupersededEvent.VerificationState);
        Assert.Equal(correction.CorrectedEvent.Id, correction.SupersededEvent.SupersededByEventId);
        Assert.Equal(extractedEvent.Id, correction.CorrectedEvent.SupersedesEventId);
        Assert.Equal(correctedDate, correction.CorrectedEvent.StartTime);
        Assert.Single(graph.AuditEvents);
        Assert.Equal(AuditEventKind.EventCorrected, correction.AuditEvent.Kind);
        Assert.Equal(correction.CorrectedEvent.Id, correction.AuditEvent.ReplacementEntityId);
        Assert.Contains("2026-03-12", correction.AuditEvent.ChangeSummary, StringComparison.Ordinal);
        Assert.Contains("2026-03-13", correction.AuditEvent.ChangeSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceBackedAssertion_WithUnknownSpanIsRejected()
    {
        var graph = SyntheticWorkplaceMatterFixture.CreateGraph();

        var error = Assert.Throws<InvalidOperationException>(() => graph.AddAssertion(
            SyntheticWorkplaceMatterFixture.Id(50),
            "synthetic-employee",
            "received-letter",
            "yes",
            "synthetic author",
            SyntheticWorkplaceMatterFixture.RecordedAt,
            EvidenceOriginClass.EmployeeAuthoredDocument,
            AssertionClass.UserAssertion,
            DisputeState.Unverified,
            IntegrityState.OriginalHashVerified,
            VerificationState.NotReviewed,
            sourceSpanId: SyntheticWorkplaceMatterFixture.Id(999)));

        Assert.Contains("source span", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(graph.Assertions);
    }

    [Fact]
    public void SourceSpan_CannotUseDocumentVersionFromAnotherMatter()
    {
        var firstGraph = SyntheticWorkplaceMatterFixture.CreateGraph(1);
        var secondGraph = SyntheticWorkplaceMatterFixture.CreateGraph(2);
        var foreignVersion = secondGraph.RegisterDocumentVersion(
            SyntheticWorkplaceMatterFixture.Id(60),
            SyntheticWorkplaceMatterFixture.Id(61),
            new string('F', 64),
            SyntheticWorkplaceMatterFixture.Id(62));

        Assert.Throws<InvalidOperationException>(() => firstGraph.AddSourceSpan(
            SyntheticWorkplaceMatterFixture.Id(63),
            foreignVersion,
            "Synthetic foreign source text.",
            "synthetic-parser/1"));
    }

    [Fact]
    public void AssertionEventLink_CannotCrossMatterBoundary()
    {
        var firstGraph = SyntheticWorkplaceMatterFixture.CreateGraph(1);
        var secondGraph = SyntheticWorkplaceMatterFixture.CreateGraph(2);
        var source = SyntheticWorkplaceMatterFixture.AddSource(firstGraph, 70, "Synthetic source.", '1');
        var assertion = SyntheticWorkplaceMatterFixture.AddSicknessDayAssertion(
            firstGraph,
            80,
            source,
            "12",
            EvidenceOriginClass.EmployerAuthoredDocument,
            AssertionClass.EmployerAssertion,
            "Example Employer Ltd");
        var foreignEvent = secondGraph.AddEvent(
            SyntheticWorkplaceMatterFixture.Id(81),
            "synthetic-event",
            "Foreign Matter event",
            EventStatus.Candidate,
            VerificationState.NotReviewed);

        Assert.Throws<InvalidOperationException>(() => firstGraph.AddAssertionEventLink(
            SyntheticWorkplaceMatterFixture.Id(82),
            assertion.Id,
            foreignEvent.Id,
            AssertionEventRelation.Supports));
        Assert.Empty(firstGraph.AssertionEventLinks);
    }

    [Fact]
    public void AiInference_CannotMasqueradeAsDocumentaryEvidence()
    {
        var graph = SyntheticWorkplaceMatterFixture.CreateGraph();
        var source = SyntheticWorkplaceMatterFixture.AddSource(graph, 90, "Synthetic source.", '2');

        Assert.Throws<InvalidOperationException>(() => graph.AddAssertion(
            SyntheticWorkplaceMatterFixture.Id(100),
            "synthetic-employee",
            "likely-outcome",
            "unknown",
            "model",
            SyntheticWorkplaceMatterFixture.RecordedAt,
            EvidenceOriginClass.OriginalContemporaneousRecord,
            AssertionClass.AiInference,
            DisputeState.Unverified,
            IntegrityState.DerivedCopy,
            VerificationState.NotReviewed,
            source.Id,
            createdByModel: "synthetic-model"));

        Assert.Empty(graph.Assertions);
    }

    [Fact]
    public void AiAnalysis_CitesInputsButRemainsSeparateFromAssertions()
    {
        var graph = SyntheticWorkplaceMatterFixture.CreateGraph();
        var source = SyntheticWorkplaceMatterFixture.AddSource(graph, 110, "Synthetic source.", '3');

        var analysis = graph.AddAnalysisNode(
            SyntheticWorkplaceMatterFixture.Id(120),
            "synthetic-gap-analysis",
            [source.Id],
            "synthetic-provider",
            "synthetic-model",
            "v1",
            "More evidence may be needed.",
            SyntheticWorkplaceMatterFixture.RecordedAt,
            VerificationState.NotReviewed);

        Assert.Single(graph.AnalysisNodes);
        Assert.Empty(graph.Assertions);
        Assert.Equal(source.Id, Assert.Single(analysis.SourceSpanIds));
    }

    [Fact]
    public void AiInferenceAssertion_IsExplicitAndNeverSourceBacked()
    {
        var graph = SyntheticWorkplaceMatterFixture.CreateGraph();

        var inference = graph.AddAssertion(
            SyntheticWorkplaceMatterFixture.Id(130),
            "synthetic-event",
            "may-require-context",
            "true",
            "model",
            SyntheticWorkplaceMatterFixture.RecordedAt,
            EvidenceOriginClass.AiGeneratedInference,
            AssertionClass.AiInference,
            DisputeState.Unverified,
            IntegrityState.DerivedCopy,
            VerificationState.NotReviewed,
            extractionConfidence: 0.75m,
            createdByModel: "synthetic-model");

        Assert.Equal(EvidenceOriginClass.AiGeneratedInference, inference.OriginClass);
        Assert.Equal(AssertionClass.AiInference, inference.AssertionClass);
        Assert.False(inference.IsSourceBacked);
        Assert.Null(inference.SourceSpanId);
    }

    [Fact]
    public void EvidenceModel_ExposesExtractionConfidenceButNoTruthScore()
    {
        var publicProperties = new[] { typeof(SourceSpan), typeof(Assertion), typeof(AnalysisNode) }
            .SelectMany(type => type.GetProperties())
            .Select(property => property.Name)
            .ToArray();

        Assert.Contains(nameof(Assertion.ExtractionConfidence), publicProperties);
        Assert.DoesNotContain(publicProperties, name => name.Contains("Truth", StringComparison.OrdinalIgnoreCase));
    }
}
