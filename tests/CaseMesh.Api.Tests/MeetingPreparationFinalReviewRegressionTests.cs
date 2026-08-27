using System.Text.Json;
using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Core.Workplace;
using CaseMesh.MatterBrain;
using CaseMesh.Persistence.Postgres;

namespace CaseMesh.Api.Tests;

public sealed class MeetingPreparationFinalReviewRegressionTests
{
    [Fact]
    public void Event_correction_history_separates_support_qualifying_and_date_mismatched_sources()
    {
        var now = new DateTimeOffset(2026, 6, 2, 9, 0, 0, TimeSpan.Zero);
        var graph = CreateGraph(now);
        var supportSpan = AddSource(graph, 'A', "Synthetic source supports the event date.");
        var qualifyingSpan = AddSource(graph, 'B', "Synthetic source qualifies the event context.");
        var mismatchedSpan = AddSource(graph, 'C', "Synthetic source alleges a different event date.");
        var eventTime = new DateTimeOffset(2026, 5, 12, 10, 0, 0, TimeSpan.Zero);
        var support = AddAssertion(graph, supportSpan, "support", now, eventTime, 0.91m);
        var qualifying = AddAssertion(graph, qualifyingSpan, "qualifying", now.AddMinutes(1), eventTime, 0.82m);
        var mismatched = AddAssertion(graph, mismatchedSpan, "mismatch", now.AddMinutes(2), eventTime.AddDays(1), 0.73m);
        var original = graph.AddEvent(Guid.NewGuid(), "meeting", "Synthetic original event",
            EventStatus.Candidate, VerificationState.NeedsContext, eventTime);
        graph.AddAssertionEventLink(Guid.NewGuid(), support.Id, original.Id, AssertionEventRelation.Supports);
        graph.AddAssertionEventLink(Guid.NewGuid(), qualifying.Id, original.Id, AssertionEventRelation.Qualifies);
        graph.AddAssertionEventLink(Guid.NewGuid(), mismatched.Id, original.Id, AssertionEventRelation.Supports);
        graph.CorrectEventDate(original.Id, Guid.NewGuid(), eventTime.AddDays(2), null,
            "Synthetic corrected event", Guid.NewGuid(), "synthetic-reviewer", now.AddHours(1));
        var loaded = Load(graph);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(MeetingPreparationProjection.Create(loaded, false)));
        var history = Assert.Single(json.RootElement.GetProperty("correctionHistory").EnumerateArray());
        var supporting = history.GetProperty("HistoricalSourceSpanIds").EnumerateArray().Select(item => item.GetGuid()).ToArray();
        var qualifyingIds = history.GetProperty("HistoricalQualifyingSourceSpanIds").EnumerateArray().Select(item => item.GetGuid()).ToArray();
        var contradicting = history.GetProperty("HistoricalContradictingSourceSpanIds").EnumerateArray().Select(item => item.GetGuid()).ToArray();

        Assert.Contains(supportSpan.Id, supporting);
        Assert.DoesNotContain(qualifyingSpan.Id, supporting);
        Assert.DoesNotContain(mismatchedSpan.Id, supporting);
        Assert.Contains(qualifyingSpan.Id, qualifyingIds);
        Assert.Contains(mismatchedSpan.Id, contradicting);
        Assert.Contains("labelled separately", history.GetProperty("Notice").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Value_only_assertion_correction_without_event_time_does_not_hide_supported_chronology()
    {
        var now = new DateTimeOffset(2026, 6, 2, 10, 0, 0, TimeSpan.Zero);
        var graph = CreateGraph(now);
        var correctedSpan = AddSource(graph, 'A', "Synthetic statement has no alleged event date.");
        var stableSpan = AddSource(graph, 'B', "Synthetic statement supports the dated event.");
        var eventTime = new DateTimeOffset(2026, 5, 13, 10, 0, 0, TimeSpan.Zero);
        var original = AddAssertion(graph, correctedSpan, "old value", now, null, 0.88m);
        var stable = AddAssertion(graph, stableSpan, "stable support", now.AddMinutes(1), eventTime, 0.94m);
        var matterEvent = graph.AddEvent(Guid.NewGuid(), "meeting", "Synthetic supported chronology event",
            EventStatus.Candidate, VerificationState.NotReviewed, eventTime);
        graph.AddAssertionEventLink(Guid.NewGuid(), original.Id, matterEvent.Id, AssertionEventRelation.Supports);
        graph.AddAssertionEventLink(Guid.NewGuid(), stable.Id, matterEvent.Id, AssertionEventRelation.Supports);
        var brain = new MatterBrainState(graph);
        var loaded = new PersistedMatterBrain(graph, new WorkplaceMatter(graph), brain);

        brain.CorrectAssertion(original.Id, Guid.NewGuid(), "new value", null,
            Guid.NewGuid(), "synthetic-reviewer", now.AddHours(1));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(MeetingPreparationProjection.Create(loaded, false)));
        var projected = Assert.Single(json.RootElement.GetProperty("chronology").EnumerateArray(),
            item => item.GetProperty("Id").GetGuid() == matterEvent.Id);
        Assert.Equal(eventTime, projected.GetProperty("StartTime").GetDateTimeOffset());
        Assert.Contains(stableSpan.Id, projected.GetProperty("sourceSpanIds").EnumerateArray().Select(item => item.GetGuid()));
    }

    [Fact]
    public void Undated_support_for_dated_event_is_contextual_not_primary_date_provenance()
    {
        var now = new DateTimeOffset(2026, 6, 2, 10, 30, 0, TimeSpan.Zero);
        var graph = CreateGraph(now);
        var exactSpan = AddSource(graph, 'C', "Synthetic source explicitly dates the event.");
        var undatedSpan = AddSource(graph, 'D', "Synthetic source supports the event without alleging a date.");
        var eventTime = new DateTimeOffset(2026, 5, 14, 10, 0, 0, TimeSpan.Zero);
        var exact = AddAssertion(graph, exactSpan, "dated support", now, eventTime, 0.96m);
        var undated = AddAssertion(graph, undatedSpan, "undated support", now.AddMinutes(1), null, 0.90m);
        var matterEvent = graph.AddEvent(Guid.NewGuid(), "meeting", "Synthetic dated chronology event",
            EventStatus.Candidate, VerificationState.NotReviewed, eventTime);
        graph.AddAssertionEventLink(Guid.NewGuid(), exact.Id, matterEvent.Id, AssertionEventRelation.Supports);
        graph.AddAssertionEventLink(Guid.NewGuid(), undated.Id, matterEvent.Id, AssertionEventRelation.Supports);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(MeetingPreparationProjection.Create(Load(graph), false)));
        var projected = Assert.Single(json.RootElement.GetProperty("chronology").EnumerateArray(),
            item => item.GetProperty("Id").GetGuid() == matterEvent.Id);
        var primary = projected.GetProperty("sourceSpanIds").EnumerateArray().Select(item => item.GetGuid()).ToArray();
        var qualifying = projected.GetProperty("qualifyingSourceSpanIds").EnumerateArray().Select(item => item.GetGuid()).ToArray();

        Assert.Contains(exactSpan.Id, primary);
        Assert.DoesNotContain(undatedSpan.Id, primary);
        Assert.Contains(undatedSpan.Id, qualifying);
    }

    [Fact]
    public async Task Partially_invalidated_multi_source_event_is_suppressed_without_field_level_provenance()
    {
        var now = new DateTimeOffset(2026, 6, 2, 10, 45, 0, TimeSpan.Zero);
        var graph = CreateGraph(now);
        var firstSpan = AddSource(graph, 'E', "Synthetic first source contributes to one extracted event.");
        var secondSpan = AddSource(graph, 'F', "Synthetic second source contributes to the same extracted event.");
        var brain = new MatterBrainState(graph);
        var eventTime = new DateTimeOffset(2026, 5, 15, 10, 0, 0, TimeSpan.Zero);
        var service = new MatterBrainMergeService(new SteppingTimeProvider(now));
        var initial = new FixedExtractionProvider(new StructuredExtractionOutput(
            "{\"multiSourceEvent\":true}",
            new StructuredCandidateBatch([], [], [],
                [new EventCandidate("event-1", "meeting", "Synthetic multi-source event", eventTime, null,
                    [], [firstSpan.Id, secondSpan.Id], 0.95m)],
                [], [], [])), "event-model-v1");

        await service.ExtractAndMergeAsync(brain, [firstSpan.Id, secondSpan.Id], initial);
        var matterEvent = Assert.Single(graph.Events);
        using (var before = JsonDocument.Parse(JsonSerializer.Serialize(
                   MeetingPreparationProjection.Create(new PersistedMatterBrain(graph, new WorkplaceMatter(graph), brain), false))))
        {
            Assert.Contains(before.RootElement.GetProperty("chronology").EnumerateArray(),
                item => item.GetProperty("Id").GetGuid() == matterEvent.Id);
        }

        var replacement = new FixedExtractionProvider(new StructuredExtractionOutput(
            "{\"firstSourceReextracted\":true}", new StructuredCandidateBatch([], [], [], [], [], [], [])),
            "event-model-v2");
        await service.ExtractAndMergeAsync(brain, [firstSpan.Id], replacement);

        Assert.Contains(brain.ActiveDependencies, dependency =>
            dependency.CanonicalKind == CanonicalRecordKind.Event &&
            dependency.CanonicalId == matterEvent.Id &&
            dependency.SourceSpanId == secondSpan.Id);
        Assert.DoesNotContain(brain.ActiveDependencies, dependency =>
            dependency.CanonicalKind == CanonicalRecordKind.Event &&
            dependency.CanonicalId == matterEvent.Id &&
            dependency.SourceSpanId == firstSpan.Id);

        using var after = JsonDocument.Parse(JsonSerializer.Serialize(
            MeetingPreparationProjection.Create(new PersistedMatterBrain(graph, new WorkplaceMatter(graph), brain), false)));
        Assert.DoesNotContain(after.RootElement.GetProperty("chronology").EnumerateArray(),
            item => item.GetProperty("Id").GetGuid() == matterEvent.Id);
    }

    [Fact]
    public void Current_supersedes_assertion_explicitly_suppresses_old_event_without_mutating_event_status()
    {
        var now = new DateTimeOffset(2026, 6, 2, 10, 50, 0, TimeSpan.Zero);
        var graph = CreateGraph(now);
        var supportSpan = AddSource(graph, 'A', "Synthetic source supports the old event date.");
        var supersedingSpan = AddSource(graph, 'B', "Synthetic source supersedes the old event account.");
        var eventTime = new DateTimeOffset(2026, 5, 16, 10, 0, 0, TimeSpan.Zero);
        var support = AddAssertion(graph, supportSpan, "old event support", now, eventTime, 0.94m);
        var superseding = AddAssertion(graph, supersedingSpan, "superseding account", now.AddMinutes(1), eventTime.AddDays(1), 0.92m);
        var matterEvent = graph.AddEvent(Guid.NewGuid(), "meeting", "Synthetic old event",
            EventStatus.Candidate, VerificationState.NotReviewed, eventTime);
        graph.AddAssertionEventLink(Guid.NewGuid(), support.Id, matterEvent.Id, AssertionEventRelation.Supports);
        graph.AddAssertionEventLink(Guid.NewGuid(), superseding.Id, matterEvent.Id, AssertionEventRelation.Supersedes);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(MeetingPreparationProjection.Create(Load(graph), false)));

        Assert.DoesNotContain(json.RootElement.GetProperty("chronology").EnumerateArray(),
            item => item.GetProperty("Id").GetGuid() == matterEvent.Id);
        Assert.Contains(json.RootElement.GetProperty("evidencePoints").EnumerateArray(),
            item => item.GetProperty("Id").GetGuid() == superseding.Id);
        Assert.Equal(EventStatus.Candidate, matterEvent.Status);
        Assert.Null(superseding.SupersededByAssertionId);
    }

    [Fact]
    public void Unresolved_dispute_preserves_assertion_time_integrity_confidence_and_review_state()
    {
        var now = new DateTimeOffset(2026, 6, 2, 11, 0, 0, TimeSpan.Zero);
        var graph = CreateGraph(now);
        var firstSpan = AddSource(graph, 'A', "Synthetic first conflicting account.");
        var secondSpan = AddSource(graph, 'B', "Synthetic second conflicting account.");
        var first = graph.AddAssertion(Guid.NewGuid(), "synthetic-subject", "count", "10", "source one", now,
            EvidenceOriginClass.OriginalContemporaneousRecord, AssertionClass.AttributedAssertion,
            DisputeState.Contradicted, IntegrityState.OriginalHashVerified, VerificationState.NeedsContext,
            firstSpan.Id, now.AddDays(-1), 0.41m);
        var second = graph.AddAssertion(Guid.NewGuid(), "synthetic-subject", "count", "12", "source two", now.AddMinutes(3),
            EvidenceOriginClass.EmployerAuthoredDocument, AssertionClass.EmployerAssertion,
            DisputeState.Contradicted, IntegrityState.DerivedCopy, VerificationState.Confirmed,
            secondSpan.Id, now.AddDays(-1), 0.62m);
        var contradiction = graph.AddContradiction(Guid.NewGuid(), first.Id, second.Id,
            ContradictionType.NumericMismatch, "synthetic-rule", now.AddMinutes(4));
        var loaded = Load(graph);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(MeetingPreparationProjection.Create(loaded, false)));
        var dispute = Assert.Single(json.RootElement.GetProperty("unresolvedDisputes").EnumerateArray(),
            item => item.GetProperty("Id").GetGuid() == contradiction.Id);
        var projectedFirst = Assert.Single(dispute.GetProperty("assertions").EnumerateArray(),
            item => item.GetProperty("Id").GetGuid() == first.Id);
        var projectedSecond = Assert.Single(dispute.GetProperty("assertions").EnumerateArray(),
            item => item.GetProperty("Id").GetGuid() == second.Id);

        Assert.Equal(now, projectedFirst.GetProperty("AssertedAt").GetDateTimeOffset());
        Assert.Equal("OriginalHashVerified", projectedFirst.GetProperty("integrity").GetString());
        Assert.Equal(0.41m, projectedFirst.GetProperty("ExtractionConfidence").GetDecimal());
        Assert.Equal("Contradicted", projectedFirst.GetProperty("dispute").GetString());
        Assert.Equal("NeedsContext", projectedFirst.GetProperty("verification").GetString());
        Assert.Equal(now.AddMinutes(3), projectedSecond.GetProperty("AssertedAt").GetDateTimeOffset());
        Assert.Equal("DerivedCopy", projectedSecond.GetProperty("integrity").GetString());
        Assert.Equal(0.62m, projectedSecond.GetProperty("ExtractionConfidence").GetDecimal());
        Assert.Equal("Confirmed", projectedSecond.GetProperty("verification").GetString());
    }

    [Fact]
    public void Assertion_correction_snapshot_exposes_historical_dispute_and_verification_state()
    {
        var now = new DateTimeOffset(2026, 6, 2, 12, 0, 0, TimeSpan.Zero);
        var graph = CreateGraph(now);
        var span = AddSource(graph, 'A', "Synthetic statement requiring correction.");
        var original = graph.AddAssertion(Guid.NewGuid(), "synthetic-subject", "status", "old", "synthetic source", now,
            EvidenceOriginClass.OriginalContemporaneousRecord, AssertionClass.AttributedAssertion,
            DisputeState.Contradicted, IntegrityState.OriginalHashVerified, VerificationState.Confirmed,
            span.Id, null, 0.55m);
        var brain = new MatterBrainState(graph);
        var loaded = new PersistedMatterBrain(graph, new WorkplaceMatter(graph), brain);
        brain.CorrectAssertion(original.Id, Guid.NewGuid(), "new", null,
            Guid.NewGuid(), "synthetic-reviewer", now.AddMinutes(1));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(MeetingPreparationProjection.Create(loaded, false)));
        var history = Assert.Single(json.RootElement.GetProperty("correctionHistory").EnumerateArray());
        var originalSnapshot = history.GetProperty("Original");
        var replacementSnapshot = history.GetProperty("Replacement");

        Assert.Equal("Superseded", originalSnapshot.GetProperty("Status").GetString());
        Assert.Equal("Rejected", originalSnapshot.GetProperty("Verification").GetString());
        Assert.Equal("Unverified", replacementSnapshot.GetProperty("Status").GetString());
        Assert.Equal("NotReviewed", replacementSnapshot.GetProperty("Verification").GetString());
    }

    private static MatterEvidenceGraph CreateGraph(DateTimeOffset now) => new(new Matter(
        Guid.NewGuid(), new TenantId(Guid.NewGuid()), "workplace-dispute", "Synthetic final-review Matter", "active", now, now));

    private static PersistedMatterBrain Load(MatterEvidenceGraph graph) =>
        new(graph, new WorkplaceMatter(graph), new MatterBrainState(graph));

    private static SourceSpan AddSource(MatterEvidenceGraph graph, char hash, string text)
    {
        var version = graph.RegisterDocumentVersion(Guid.NewGuid(), Guid.NewGuid(), new string(hash, 64), Guid.NewGuid());
        return graph.AddSourceSpan(Guid.NewGuid(), version, text, "synthetic-parser/1", 0.99m,
            pageNumber: 1, textStart: 0, textEnd: text.Length);
    }

    private static Assertion AddAssertion(
        MatterEvidenceGraph graph,
        SourceSpan span,
        string value,
        DateTimeOffset assertedAt,
        DateTimeOffset? eventTime,
        decimal confidence) => graph.AddAssertion(
        Guid.NewGuid(), "synthetic-subject", "meeting-date", value, "synthetic source", assertedAt,
        EvidenceOriginClass.OriginalContemporaneousRecord, AssertionClass.AttributedAssertion,
        DisputeState.Unverified, IntegrityState.OriginalHashVerified, VerificationState.NotReviewed,
        span.Id, eventTime, confidence);

    private sealed class FixedExtractionProvider(StructuredExtractionOutput output, string model)
        : IStructuredExtractionProvider
    {
        public StructuredExtractionProviderDescriptor Descriptor { get; } =
            new("synthetic-provider", model, "1", "1", "1");

        public Task<StructuredExtractionOutput> ExtractAsync(
            StructuredExtractionInput input,
            CancellationToken cancellationToken = default) => Task.FromResult(output);
    }

    private sealed class SteppingTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _current = initial;

        public override DateTimeOffset GetUtcNow()
        {
            var value = _current;
            _current = _current.AddSeconds(1);
            return value;
        }
    }
}
