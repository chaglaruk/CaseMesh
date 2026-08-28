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
        Assert.Equal(fixture.CurrentSourceSpanId, current.Citation.SourceSpanId);
        Assert.Equal(fixture.DocumentVersionId, current.Citation.DocumentVersionId);
        Assert.Equal(fixture.OriginalObjectId, current.Citation.OriginalObjectId);
        Assert.Equal(new string('A', 64), current.Citation.ContentSha256);
        Assert.Null(current.HistoricalReason);

        var historical = Assert.Single(context.Evidence, item => item.RecordStatus == LiveEvidenceRecordStatus.Historical);
        Assert.Equal(fixture.RejectedAssertionId, historical.AssertionId);
        Assert.Equal("Rejected", historical.HistoricalReason);

        var ai = Assert.Single(context.AiAnalysis);
        Assert.Equal(fixture.AiAssertionId, ai.AssertionId);
        Assert.Equal("synthetic-test-model", ai.CreatedByModel);
        Assert.DoesNotContain(context.Evidence, item => item.AssertionId == fixture.AiAssertionId);
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
                    Guid.Parse("91000000-0000-0000-0000-000000000003"),
                    LiveConversationOrigin.AiSuggested,
                    "  Ask which record supports that date.  ",
                    start.AddSeconds(4),
                    start.AddSeconds(5),
                    [fixture.CurrentSourceSpanId]),
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
                    [fixture.CurrentSourceSpanId])
            ]);

        Assert.Equal(
            [LiveConversationOrigin.HrSaid, LiveConversationOrigin.UserActuallySaid, LiveConversationOrigin.AiSuggested],
            review.Items.Select(item => item.Origin));
        Assert.Equal("Ask which record supports that date.", review.Items[2].Text);
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
            IntegrityState.OriginalHashVerified,
            VerificationState.NotReviewed,
            currentSourceSpanId);

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
            rejectedSourceSpanId);

        var aiAssertionId = Guid.Parse("50000000-0000-0000-0000-000000000003");
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
            aiAssertionId);
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
        Guid AiAssertionId);
}
