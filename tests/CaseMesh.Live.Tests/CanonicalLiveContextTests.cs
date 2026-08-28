using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Live;
using CaseMesh.MatterBrain;
using Xunit;

namespace CaseMesh.Live.Tests;

public sealed class CanonicalLiveContextTests
{
    [Fact]
    public void Adapter_separates_documentary_evidence_ai_analysis_and_history()
    {
        var fixture = CreateFixture();

        var context = new CanonicalLiveContextAdapter().Build(
            fixture.TenantId,
            fixture.MatterId,
            fixture.State,
            evidenceProcessingActive: true);

        Assert.Equal(CanonicalLiveCurrentness.Processing, context.Currentness);
        Assert.Equal(fixture.TenantId, context.TenantId);
        Assert.Equal(fixture.MatterId, context.MatterId);

        var current = Assert.Single(context.Evidence, item => item.RecordStatus == LiveEvidenceRecordStatus.Current);
        Assert.Equal(fixture.CurrentAssertionId, current.AssertionId);
        Assert.Equal(fixture.CurrentSourceSpanId, current.SourceSpanId);
        Assert.Equal(IntegrityState.OcrUncertain, current.IntegrityState);
        Assert.Equal(0.73m, current.ExtractionConfidence);
        Assert.Null(current.HistoricalReason);

        var currentSource = Assert.Single(context.SourceSpans, item => item.SourceSpanId == fixture.CurrentSourceSpanId);
        Assert.Equal(fixture.DocumentVersionId, currentSource.DocumentVersionId);
        Assert.Equal(fixture.OriginalObjectId, currentSource.OriginalObjectId);
        Assert.Equal(new string('A', 64), currentSource.ContentSha256);

        var historical = Assert.Single(context.Evidence, item => item.RecordStatus == LiveEvidenceRecordStatus.Historical);
        Assert.Equal(fixture.RejectedAssertionId, historical.AssertionId);
        Assert.Equal("Rejected", historical.HistoricalReason);

        var ai = Assert.Single(context.AiAnalysis);
        Assert.Equal(fixture.AiAssertionId, ai.AssertionId);
        Assert.Equal("synthetic-test-model", ai.CreatedByModel);
        Assert.Equal(fixture.AiEventTime, ai.EventTime);
        Assert.Equal(IntegrityState.DerivedCopy, ai.IntegrityState);
        Assert.DoesNotContain(context.Evidence, item => item.AssertionId == fixture.AiAssertionId);
    }

    [Fact]
    public void Adapter_emits_each_exact_source_span_once_even_when_multiple_assertions_reference_it()
    {
        var fixture = CreateFixture();
        fixture.State.Evidence.AddAssertion(
            Guid.Parse("50000000-0000-0000-0000-000000000004"),
            "meeting",
            "location",
            "Room 4",
            "Employer",
            DateTimeOffset.Parse("2026-08-28T08:03:00Z"),
            EvidenceOriginClass.EmployerAuthoredDocument,
            AssertionClass.EmployerAssertion,
            DisputeState.Unverified,
            IntegrityState.OcrUncertain,
            VerificationState.NotReviewed,
            sourceSpanId: fixture.CurrentSourceSpanId,
            extractionConfidence: 0.61m);

        var context = new CanonicalLiveContextAdapter().Build(fixture.TenantId, fixture.MatterId, fixture.State);

        Assert.Equal(2, context.Evidence.Count(item => item.SourceSpanId == fixture.CurrentSourceSpanId));
        Assert.Equal(2, context.SourceSpans.Count);
        Assert.Equal(
            context.Evidence.Select(item => item.SourceSpanId).Distinct().OrderBy(id => id),
            context.SourceSpans.Select(item => item.SourceSpanId));
    }

    [Fact]
    public void Adapter_fails_closed_for_wrong_tenant_or_matter()
    {
        var fixture = CreateFixture();
        var adapter = new CanonicalLiveContextAdapter();

        Assert.Throws<UnauthorizedAccessException>(() => adapter.Build(
            new TenantId(Guid.NewGuid()),
            fixture.MatterId,
            fixture.State));

        Assert.Throws<UnauthorizedAccessException>(() => adapter.Build(
            fixture.TenantId,
            Guid.NewGuid(),
            fixture.State));
    }

    [Fact]
    public void Uploaded_review_keeps_spoken_and_ai_origins_distinct_and_uses_context_citations_only()
    {
        var fixture = CreateFixture();
        var context = new CanonicalLiveContextAdapter().Build(fixture.TenantId, fixture.MatterId, fixture.State);
        var start = DateTimeOffset.Parse("2026-08-28T09:00:00Z");
        var review = new UploadedMeetingReviewBuilder().Build(
            context,
            Guid.Parse("90000000-0000-0000-0000-000000000001"),
            [
                new LiveConversationItem(
                    Guid.Parse("91000000-0000-0000-0000-000000000001"),
                    LiveConversationOrigin.HrSaid,
                    "The meeting was on Monday.",
                    start,
                    start.AddSeconds(1),
                    []),
                new LiveConversationItem(
                    Guid.Parse("91000000-0000-0000-0000-000000000002"),
                    LiveConversationOrigin.UserActuallySaid,
                    "I need to check the email.",
                    start.AddSeconds(2),
                    start.AddSeconds(3),
                    [fixture.CurrentSourceSpanId]),
                new LiveConversationItem(
                    Guid.Parse("91000000-0000-0000-0000-000000000003"),
                    LiveConversationOrigin.AiSuggested,
                    "  Ask which record supports that date.  ",
                    start.AddSeconds(4),
                    start.AddSeconds(5),
                    [fixture.CurrentSourceSpanId])
            ]);

        Assert.Equal(
            [LiveConversationOrigin.HrSaid, LiveConversationOrigin.UserActuallySaid, LiveConversationOrigin.AiSuggested],
            review.Items.Select(item => item.Origin));
        Assert.Equal("  Ask which record supports that date.  ", review.Items[2].Text);
        Assert.Equal(fixture.CurrentSourceSpanId, Assert.Single(review.Items[2].ContextCitationSourceSpanIds));
    }

    [Fact]
    public void Uploaded_review_rejects_historical_or_unknown_context_citations()
    {
        var fixture = CreateFixture();
        var context = new CanonicalLiveContextAdapter().Build(fixture.TenantId, fixture.MatterId, fixture.State);
        var start = DateTimeOffset.Parse("2026-08-28T09:00:00Z");
        var builder = new UploadedMeetingReviewBuilder();

        Assert.Throws<InvalidOperationException>(() => builder.Build(
            context,
            Guid.NewGuid(),
            [new LiveConversationItem(Guid.NewGuid(), LiveConversationOrigin.HrSaid, "Statement", start, start, [fixture.RejectedSourceSpanId])]));

        Assert.Throws<InvalidOperationException>(() => builder.Build(
            context,
            Guid.NewGuid(),
            [new LiveConversationItem(Guid.NewGuid(), LiveConversationOrigin.AiSuggested, "Suggestion", start, start, [Guid.NewGuid()])]));
    }

    [Fact]
    public async Task Structured_extraction_contradiction_retains_auditable_model_run_and_input_provenance()
    {
        var tenantId = new TenantId(Guid.Parse("12000000-0000-0000-0000-000000000001"));
        var matterId = Guid.Parse("22000000-0000-0000-0000-000000000001");
        var now = DateTimeOffset.Parse("2026-08-28T10:00:00Z");
        var graph = new MatterEvidenceGraph(new Matter(
            matterId,
            tenantId,
            "workplace-dispute",
            "Synthetic contradiction Matter",
            "open",
            now,
            now));
        var version = graph.RegisterDocumentVersion(
            Guid.Parse("32000000-0000-0000-0000-000000000101"),
            Guid.Parse("33000000-0000-0000-0000-000000000101"),
            new string('B', 64),
            Guid.Parse("34000000-0000-0000-0000-000000000101"));
        var sourceA = Guid.Parse("42000000-0000-0000-0000-000000000101");
        var sourceB = Guid.Parse("42000000-0000-0000-0000-000000000102");
        graph.AddSourceSpan(sourceA, version, "Employer says the request was approved.", "synthetic-parser/1", pageNumber: 1);
        graph.AddSourceSpan(sourceB, version, "Employer says the request was not approved.", "synthetic-parser/1", pageNumber: 2);

        var output = new StructuredExtractionOutput(
            "{\"result\":\"synthetic-conflict\"}",
            new StructuredCandidateBatch(
                [],
                [],
                [
                    new AssertionCandidate(
                        "assertion-a",
                        "request",
                        "approval",
                        "approved",
                        "Employer",
                        now,
                        null,
                        sourceA,
                        EvidenceOriginClass.EmployerAuthoredDocument,
                        AssertionClass.EmployerAssertion,
                        IntegrityState.OriginalHashVerified,
                        [sourceA],
                        0.93m),
                    new AssertionCandidate(
                        "assertion-b",
                        "request",
                        "approval",
                        "not-approved",
                        "Employer",
                        now.AddMinutes(1),
                        null,
                        sourceB,
                        EvidenceOriginClass.EmployerAuthoredDocument,
                        AssertionClass.EmployerAssertion,
                        IntegrityState.OcrUncertain,
                        [sourceB],
                        0.81m)
                ],
                [],
                [],
                [],
                [
                    new ContradictionCandidate(
                        "contradiction-a-b",
                        "assertion-a",
                        "assertion-b",
                        ContradictionType.DirectConflict,
                        "synthetic-model-detector",
                        [sourceA, sourceB],
                        0.88m)
                ]));
        var provider = new StaticStructuredProvider(output);
        var state = new MatterBrainState(graph);

        await new MatterBrainMergeService(new FixedTimeProvider(now.AddMinutes(5)))
            .ExtractAndMergeAsync(state, [sourceA, sourceB], provider);

        var context = new CanonicalLiveContextAdapter().Build(tenantId, matterId, state);
        var contradiction = Assert.Single(context.UnresolvedContradictions);
        Assert.Equal(ContradictionDetectionOrigin.StructuredExtractionAnalysis.ToString(), contradiction.DetectionOrigin);
        var provenance = Assert.Single(contradiction.AnalysisProvenance);
        Assert.Equal([sourceA, sourceB], provenance.SourceSpanIds);
        Assert.Equal(0.88m, provenance.ExtractionConfidence);
        Assert.Equal(provider.Descriptor.Provider, provenance.Provider);
        Assert.Equal(provider.Descriptor.Model, provenance.Model);
        Assert.Equal(provider.Descriptor.ExtractionVersion, provenance.ExtractionVersion);
        Assert.Equal(provider.Descriptor.PromptVersion, provenance.PromptVersion);
        Assert.Equal(provider.Descriptor.SchemaVersion, provenance.SchemaVersion);
        Assert.Equal(now.AddMinutes(5), provenance.GeneratedAt);
        Assert.Equal(64, provenance.RawResultDigest.Length);
        Assert.Equal(64, provenance.CandidatePayloadDigest.Length);
        Assert.All(provenance.SourceSpanIds, id => Assert.Contains(context.SourceSpans, span => span.SourceSpanId == id));
    }

    private static Fixture CreateFixture()
    {
        var tenantId = new TenantId(Guid.Parse("10000000-0000-0000-0000-000000000001"));
        var matterId = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var createdAt = DateTimeOffset.Parse("2026-08-28T08:00:00Z");
        var matter = new Matter(matterId, tenantId, "workplace-dispute", "Synthetic Matter", "open", createdAt, createdAt);
        var graph = new MatterEvidenceGraph(matter);

        var documentVersionId = Guid.Parse("30000000-0000-0000-0000-000000000001");
        var originalObjectId = Guid.Parse("31000000-0000-0000-0000-000000000001");
        var version = graph.RegisterDocumentVersion(
            Guid.Parse("32000000-0000-0000-0000-000000000001"),
            documentVersionId,
            new string('A', 64),
            originalObjectId);

        var currentSourceSpanId = Guid.Parse("40000000-0000-0000-0000-000000000001");
        graph.AddSourceSpan(currentSourceSpanId, version, "Meeting scheduled for Monday.", "synthetic-parser/1", pageNumber: 1);
        var rejectedSourceSpanId = Guid.Parse("40000000-0000-0000-0000-000000000002");
        graph.AddSourceSpan(rejectedSourceSpanId, version, "Meeting scheduled for Tuesday.", "synthetic-parser/1", pageNumber: 1);

        var currentAssertionId = Guid.Parse("50000000-0000-0000-0000-000000000001");
        graph.AddAssertion(
            currentAssertionId,
            "meeting",
            "scheduled-date",
            "Monday",
            "Employer",
            createdAt,
            EvidenceOriginClass.EmployerAuthoredDocument,
            AssertionClass.EmployerAssertion,
            DisputeState.Unverified,
            IntegrityState.OcrUncertain,
            VerificationState.NotReviewed,
            sourceSpanId: currentSourceSpanId,
            extractionConfidence: 0.73m);

        var rejectedAssertionId = Guid.Parse("50000000-0000-0000-0000-000000000002");
        graph.AddAssertion(
            rejectedAssertionId,
            "meeting",
            "scheduled-date",
            "Tuesday",
            "Employer",
            createdAt.AddMinutes(1),
            EvidenceOriginClass.EmployerAuthoredDocument,
            AssertionClass.EmployerAssertion,
            DisputeState.Unverified,
            IntegrityState.OriginalHashVerified,
            VerificationState.Rejected,
            sourceSpanId: rejectedSourceSpanId,
            extractionConfidence: 0.96m);

        var aiAssertionId = Guid.Parse("50000000-0000-0000-0000-000000000003");
        var aiEventTime = createdAt.AddDays(1);
        graph.AddAssertion(
            aiAssertionId,
            "meeting",
            "risk-note",
            "Date may need clarification",
            "CaseMesh",
            createdAt.AddMinutes(2),
            EvidenceOriginClass.AiGeneratedInference,
            AssertionClass.AiInference,
            DisputeState.Unverified,
            IntegrityState.DerivedCopy,
            VerificationState.NotReviewed,
            eventTime: aiEventTime,
            createdByModel: "synthetic-test-model");

        return new Fixture(
            tenantId,
            matterId,
            new MatterBrainState(graph),
            documentVersionId,
            originalObjectId,
            currentSourceSpanId,
            rejectedSourceSpanId,
            currentAssertionId,
            rejectedAssertionId,
            aiAssertionId,
            aiEventTime);
    }

    private sealed class StaticStructuredProvider(StructuredExtractionOutput output) : IStructuredExtractionProvider
    {
        private readonly StructuredExtractionOutput _output = output;

        public StructuredExtractionProviderDescriptor Descriptor { get; } = new(
            "synthetic-provider",
            "synthetic-contradiction-model",
            "extract-v1",
            "prompt-v2",
            "schema-v3");

        public Task<StructuredExtractionOutput> ExtractAsync(
            StructuredExtractionInput input,
            CancellationToken cancellationToken = default) => Task.FromResult(_output);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record Fixture(
        TenantId TenantId,
        Guid MatterId,
        MatterBrainState State,
        Guid DocumentVersionId,
        Guid OriginalObjectId,
        Guid CurrentSourceSpanId,
        Guid RejectedSourceSpanId,
        Guid CurrentAssertionId,
        Guid RejectedAssertionId,
        Guid AiAssertionId,
        DateTimeOffset AiEventTime);
}
