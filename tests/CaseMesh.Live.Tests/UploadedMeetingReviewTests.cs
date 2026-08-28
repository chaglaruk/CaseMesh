using CaseMesh.Core.Models;

namespace CaseMesh.Live.Tests;

public sealed class UploadedMeetingReviewTests
{
    [Fact]
    public void Builder_enforces_bounded_transcript_contract()
    {
        var fixture = CreateContext();
        var builder = new UploadedMeetingReviewBuilder();
        var now = DateTimeOffset.Parse("2026-08-28T10:00:00Z");

        Assert.Throws<InvalidOperationException>(() => builder.Build(
            fixture.Context,
            Guid.NewGuid(),
            [new LiveConversationItem(Guid.NewGuid(), LiveConversationOrigin.HrSaid, " ", now, now, [])]));

        Assert.Throws<InvalidOperationException>(() => builder.Build(
            fixture.Context,
            Guid.NewGuid(),
            [new LiveConversationItem(
                Guid.NewGuid(),
                LiveConversationOrigin.HrSaid,
                new string('x', UploadedMeetingReviewBuilder.MaximumItemTextCharacters + 1),
                now,
                now,
                [])]));

        Assert.Throws<InvalidOperationException>(() => builder.Build(
            fixture.Context,
            Guid.NewGuid(),
            [new LiveConversationItem(Guid.NewGuid(), LiveConversationOrigin.HrSaid, "Statement", default, now, [])]));

        Assert.Throws<InvalidOperationException>(() => builder.Build(
            fixture.Context,
            Guid.NewGuid(),
            [new LiveConversationItem(
                Guid.NewGuid(),
                LiveConversationOrigin.HrSaid,
                "Statement",
                now,
                now,
                Enumerable.Range(0, UploadedMeetingReviewBuilder.MaximumContextCitationsPerItem + 1)
                    .Select(_ => Guid.NewGuid())
                    .ToArray())]));
    }

    [Fact]
    public void Analyzer_preserves_review_history_without_promoting_context_to_spoken_provenance()
    {
        var fixture = CreateContext();
        var now = DateTimeOffset.Parse("2026-08-28T10:00:00Z");
        var review = new UploadedMeetingReview(
            fixture.Context.TenantId,
            fixture.Context.MatterId,
            Guid.NewGuid(),
            CanonicalLiveCurrentness.Current,
            [
                new LiveConversationItem(
                    Guid.NewGuid(),
                    LiveConversationOrigin.HrSaid,
                    "The date was Tuesday.",
                    now,
                    now.AddSeconds(1),
                    [fixture.HistoricalSourceId]),
                new LiveConversationItem(
                    Guid.NewGuid(),
                    LiveConversationOrigin.UserActuallySaid,
                    "I need to check the email.",
                    now.AddSeconds(2),
                    now.AddSeconds(3),
                    [fixture.CurrentSourceId]),
                new LiveConversationItem(
                    Guid.NewGuid(),
                    LiveConversationOrigin.AiSuggested,
                    "Check the supporting record.",
                    now.AddSeconds(4),
                    now.AddSeconds(5),
                    [fixture.MissingSourceId])
            ]);

        var analysis = new UploadedMeetingReviewAnalyzer().Analyze(review, fixture.Context);

        Assert.Equal(
            [
                UploadedMeetingContextReferenceStatus.Current,
                UploadedMeetingContextReferenceStatus.Historical,
                UploadedMeetingContextReferenceStatus.Missing
            ],
            analysis.ContextReferences.OrderBy(item => item.Status).Select(item => item.Status));
        Assert.Single(analysis.RelevantUnresolvedContradictions);
        Assert.Contains(analysis.FollowUpPrompts, prompt => prompt.Contains("no longer current", StringComparison.Ordinal));
        Assert.Contains(analysis.FollowUpPrompts, prompt => prompt.Contains("does not choose", StringComparison.Ordinal));
        Assert.Contains(analysis.FollowUpPrompts, prompt => prompt.Contains("AI suggestions", StringComparison.Ordinal));
        Assert.Equal("The date was Tuesday.", review.Items[0].Text);
        Assert.Equal(fixture.HistoricalSourceId, Assert.Single(review.Items[0].ContextCitationSourceSpanIds));
    }

    private static ReviewFixture CreateContext()
    {
        var tenantId = new TenantId(Guid.Parse("71000000-0000-0000-0000-000000000001"));
        var matterId = Guid.Parse("72000000-0000-0000-0000-000000000001");
        var currentSourceId = Guid.Parse("73000000-0000-0000-0000-000000000001");
        var historicalSourceId = Guid.Parse("73000000-0000-0000-0000-000000000002");
        var missingSourceId = Guid.Parse("73000000-0000-0000-0000-000000000003");
        var assertionA = Guid.Parse("74000000-0000-0000-0000-000000000001");
        var assertionB = Guid.Parse("74000000-0000-0000-0000-000000000002");
        var now = DateTimeOffset.Parse("2026-08-28T09:00:00Z");
        var version = Guid.Parse("75000000-0000-0000-0000-000000000001");
        var document = Guid.Parse("76000000-0000-0000-0000-000000000001");
        var original = Guid.Parse("77000000-0000-0000-0000-000000000001");

        var sources = new[]
        {
            new LiveSourceCitation(currentSourceId, document, version, original, new string('A', 64), 1, null, null,
                new string('B', 64), 24, "synthetic-parser/1", 0.95m),
            new LiveSourceCitation(historicalSourceId, document, version, original, new string('A', 64), 2, null, null,
                new string('C', 64), 25, "synthetic-parser/1", 0.90m)
        };
        var evidence = new[]
        {
            new CanonicalLiveEvidenceItem(
                assertionA, "meeting", "scheduled-date", "Monday", "Employer", null, now,
                EvidenceOriginClass.EmployerAuthoredDocument, AssertionClass.EmployerAssertion,
                DisputeState.Unverified, IntegrityState.OriginalHashVerified, VerificationState.NotReviewed,
                0.95m, LiveEvidenceRecordStatus.Current, null,
                "Current attributed documentary evidence; not automatically an established fact.", currentSourceId),
            new CanonicalLiveEvidenceItem(
                assertionB, "meeting", "scheduled-date", "Tuesday", "Employer", null, now.AddMinutes(1),
                EvidenceOriginClass.EmployerAuthoredDocument, AssertionClass.EmployerAssertion,
                DisputeState.Superseded, IntegrityState.OriginalHashVerified, VerificationState.NotReviewed,
                0.90m, LiveEvidenceRecordStatus.Historical, "Superseded",
                "Historical documentary evidence retained for correction/audit context; not current and not automatically an established fact.", historicalSourceId)
        };
        var contradiction = new CanonicalLiveContradiction(
            Guid.Parse("78000000-0000-0000-0000-000000000001"),
            assertionA,
            assertionB,
            ContradictionType.DirectConflict,
            "DeterministicRule",
            []);
        var context = new CanonicalLiveContext(
            tenantId,
            matterId,
            "Synthetic Review Matter",
            CanonicalLiveCurrentness.Current,
            sources,
            evidence,
            [],
            [],
            [contradiction]);
        return new ReviewFixture(context, currentSourceId, historicalSourceId, missingSourceId);
    }

    private sealed record ReviewFixture(
        CanonicalLiveContext Context,
        Guid CurrentSourceId,
        Guid HistoricalSourceId,
        Guid MissingSourceId);
}