using System.Text.Json;
using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Live;
using CaseMesh.MatterBrain;
using Xunit;

namespace CaseMesh.Live.Tests;

public sealed class CanonicalLiveHardeningTests
{
    [Fact]
    public void Context_keeps_source_and_ai_confidence_without_inlining_exact_source_text()
    {
        var tenantId = new TenantId(Guid.Parse("13000000-0000-0000-0000-000000000001"));
        var matterId = Guid.Parse("23000000-0000-0000-0000-000000000001");
        var now = DateTimeOffset.Parse("2026-08-28T11:00:00Z");
        var graph = new MatterEvidenceGraph(new Matter(
            matterId, tenantId, "workplace-dispute", "Synthetic bounded context Matter", "open", now, now));
        var version = graph.RegisterDocumentVersion(
            Guid.Parse("33000000-0000-0000-0000-000000000001"),
            Guid.Parse("34000000-0000-0000-0000-000000000001"),
            new string('C', 64),
            Guid.Parse("35000000-0000-0000-0000-000000000001"));
        var sourceSpanId = Guid.Parse("43000000-0000-0000-0000-000000000001");
        const string exactText = "Synthetic OCR text that must be fetched on demand only.";
        graph.AddSourceSpan(
            sourceSpanId,
            version,
            exactText,
            "synthetic-ocr/7",
            extractionConfidence: 0.64m,
            pageNumber: 2);
        graph.AddAssertion(
            Guid.Parse("53000000-0000-0000-0000-000000000001"),
            "meeting",
            "date",
            "Monday",
            "Employer",
            now,
            EvidenceOriginClass.OcrDerivedRecord,
            AssertionClass.EmployerAssertion,
            DisputeState.Unverified,
            IntegrityState.OcrUncertain,
            VerificationState.NotReviewed,
            sourceSpanId,
            extractionConfidence: 0.82m);
        var aiAssertionId = Guid.Parse("53000000-0000-0000-0000-000000000002");
        graph.AddAssertion(
            aiAssertionId,
            "meeting",
            "analysis-note",
            "The date may need clarification.",
            "CaseMesh",
            now.AddMinutes(1),
            EvidenceOriginClass.AiGeneratedInference,
            AssertionClass.AiInference,
            DisputeState.Unverified,
            IntegrityState.DerivedCopy,
            VerificationState.NotReviewed,
            extractionConfidence: 0.51m,
            createdByModel: "synthetic-analysis-model");
        var state = new MatterBrainState(graph);
        var adapter = new CanonicalLiveContextAdapter();

        var context = adapter.Build(tenantId, matterId, state);

        var source = Assert.Single(context.SourceSpans);
        Assert.Equal(sourceSpanId, source.SourceSpanId);
        Assert.Equal("synthetic-ocr/7", source.ParserVersion);
        Assert.Equal(0.64m, source.ExtractionConfidence);
        Assert.Equal(exactText.Length, source.ExactTextLength);
        Assert.Equal(64, source.ExactTextDigest.Length);
        Assert.DoesNotContain(exactText, JsonSerializer.Serialize(context), StringComparison.Ordinal);

        var ai = Assert.Single(context.AiAnalysis);
        Assert.Equal(aiAssertionId, ai.AssertionId);
        Assert.Equal(0.51m, ai.ExtractionConfidence);

        var detail = adapter.BuildSourceDetail(tenantId, matterId, sourceSpanId, state);
        Assert.Equal(exactText, detail.ExactText);
        Assert.Equal(source, detail.Citation);
        Assert.Throws<UnauthorizedAccessException>(() => adapter.BuildSourceDetail(
            new TenantId(Guid.NewGuid()), matterId, sourceSpanId, state));
        Assert.Throws<KeyNotFoundException>(() => adapter.BuildSourceDetail(
            tenantId, matterId, Guid.NewGuid(), state));
    }

    [Fact]
    public void Source_less_corrections_preserve_historical_and_current_statements_without_citations()
    {
        var tenantId = new TenantId(Guid.Parse("13000000-0000-0000-0000-000000000002"));
        var matterId = Guid.Parse("23000000-0000-0000-0000-000000000002");
        var now = DateTimeOffset.Parse("2026-08-28T11:30:00Z");
        var graph = new MatterEvidenceGraph(new Matter(
            matterId, tenantId, "workplace-dispute", "Synthetic correction Matter", "open", now, now));
        var originalId = Guid.Parse("53000000-0000-0000-0000-000000000011");
        var correctedId = Guid.Parse("53000000-0000-0000-0000-000000000012");
        graph.AddAssertion(
            originalId,
            "employee",
            "position",
            "I agreed to the change.",
            "Employee",
            now,
            EvidenceOriginClass.ParticipantOrWitnessStatement,
            AssertionClass.UserAssertion,
            DisputeState.Uncorroborated,
            IntegrityState.Incomplete,
            VerificationState.NotReviewed);
        var state = new MatterBrainState(graph);
        state.CorrectAssertion(
            originalId,
            correctedId,
            "I did not agree to the change.",
            null,
            Guid.Parse("63000000-0000-0000-0000-000000000001"),
            "Employee",
            now.AddMinutes(1));

        var context = new CanonicalLiveContextAdapter().Build(tenantId, matterId, state);

        Assert.Empty(context.SourceSpans);
        Assert.Equal(2, context.UnsupportedStatements.Count);
        var current = Assert.Single(context.UnsupportedStatements, item => item.RecordStatus == LiveEvidenceRecordStatus.Current);
        Assert.Equal(correctedId, current.AssertionId);
        Assert.Null(current.HistoricalReason);
        Assert.Contains("without documentary SourceSpan provenance", current.EvidenceNotice, StringComparison.Ordinal);
        var historical = Assert.Single(context.UnsupportedStatements, item => item.RecordStatus == LiveEvidenceRecordStatus.Historical);
        Assert.Equal(originalId, historical.AssertionId);
        Assert.Equal("Superseded", historical.HistoricalReason);
        Assert.Contains("Historical attributed Matter statement", historical.EvidenceNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void Human_correction_numeric_contradiction_is_labelled_as_deterministic_rule()
    {
        var tenantId = new TenantId(Guid.Parse("13000000-0000-0000-0000-000000000003"));
        var matterId = Guid.Parse("23000000-0000-0000-0000-000000000003");
        var now = DateTimeOffset.Parse("2026-08-28T12:00:00Z");
        var eventTime = now.AddDays(-1);
        var graph = new MatterEvidenceGraph(new Matter(
            matterId, tenantId, "workplace-dispute", "Synthetic rule Matter", "open", now, now));
        var correctedSourceId = Guid.Parse("53000000-0000-0000-0000-000000000021");
        var otherId = Guid.Parse("53000000-0000-0000-0000-000000000022");
        var correctedId = Guid.Parse("53000000-0000-0000-0000-000000000023");
        graph.AddAssertion(
            correctedSourceId,
            "absence",
            "days",
            "12",
            "Employee",
            now,
            EvidenceOriginClass.ParticipantOrWitnessStatement,
            AssertionClass.UserAssertion,
            DisputeState.Uncorroborated,
            IntegrityState.Incomplete,
            VerificationState.NotReviewed,
            eventTime: eventTime);
        graph.AddAssertion(
            otherId,
            "absence",
            "days",
            "10",
            "Employer",
            now.AddMinutes(1),
            EvidenceOriginClass.ParticipantOrWitnessStatement,
            AssertionClass.EmployerAssertion,
            DisputeState.Uncorroborated,
            IntegrityState.Incomplete,
            VerificationState.NotReviewed,
            eventTime: eventTime);
        var state = new MatterBrainState(graph);
        state.CorrectAssertion(
            correctedSourceId,
            correctedId,
            "11",
            eventTime,
            Guid.Parse("63000000-0000-0000-0000-000000000002"),
            "Employee",
            now.AddMinutes(2));

        var context = new CanonicalLiveContextAdapter().Build(tenantId, matterId, state);

        var contradiction = Assert.Single(context.UnresolvedContradictions);
        Assert.Equal(ContradictionDetectionOrigin.DeterministicRule.ToString(), contradiction.DetectionOrigin);
        Assert.Empty(contradiction.AnalysisProvenance);
    }
}
