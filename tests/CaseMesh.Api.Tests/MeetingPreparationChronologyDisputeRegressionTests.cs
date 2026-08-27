using System.Text.Json;
using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Core.Workplace;
using CaseMesh.MatterBrain;
using CaseMesh.Persistence.Postgres;
using CaseMesh.Qa;

namespace CaseMesh.Api.Tests;

public sealed class MeetingPreparationChronologyDisputeRegressionTests
{
    [Fact]
    public void Corrected_contradicting_date_does_not_remove_supported_chronology_item()
    {
        var now = new DateTimeOffset(2026, 5, 6, 9, 0, 0, TimeSpan.Zero);
        var graph = CreateGraph(now, out var supportSpan, out var contradictSpan);
        var eventTime = new DateTimeOffset(2026, 4, 10, 10, 0, 0, TimeSpan.Zero);
        var support = AddAssertion(graph, supportSpan, "Support", eventTime, now);
        var contradict = AddAssertion(graph, contradictSpan, "Contradict", eventTime.AddDays(1), now.AddMinutes(1));
        var matterEvent = graph.AddEvent(Guid.NewGuid(), "meeting", "Synthetic supported meeting",
            EventStatus.Disputed, VerificationState.NeedsContext, eventTime);
        graph.AddAssertionEventLink(Guid.NewGuid(), support.Id, matterEvent.Id, AssertionEventRelation.Supports);
        graph.AddAssertionEventLink(Guid.NewGuid(), contradict.Id, matterEvent.Id, AssertionEventRelation.Contradicts);
        var brain = new MatterBrainState(graph);
        var loaded = new PersistedMatterBrain(graph, new WorkplaceMatter(graph), brain);

        brain.CorrectAssertion(contradict.Id, Guid.NewGuid(), contradict.Value, eventTime.AddDays(2),
            Guid.NewGuid(), "synthetic-reviewer", now.AddHours(1));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(MeetingPreparationProjection.Create(loaded, false)));
        var chronology = json.RootElement.GetProperty("chronology").EnumerateArray().ToArray();
        var projected = Assert.Single(chronology, item => item.GetProperty("Id").GetGuid() == matterEvent.Id);
        Assert.Equal(eventTime, projected.GetProperty("StartTime").GetDateTimeOffset());
        Assert.Contains(supportSpan.Id,
            projected.GetProperty("sourceSpanIds").EnumerateArray().Select(item => item.GetGuid()));
    }

    [Fact]
    public void Temporal_dispute_preserves_each_asserted_event_time()
    {
        var now = new DateTimeOffset(2026, 5, 7, 9, 0, 0, TimeSpan.Zero);
        var graph = CreateGraph(now, out var firstSpan, out var secondSpan);
        var firstTime = new DateTimeOffset(2026, 4, 11, 9, 0, 0, TimeSpan.Zero);
        var secondTime = firstTime.AddDays(1);
        var first = AddAssertion(graph, firstSpan, "Same value", firstTime, now);
        var second = AddAssertion(graph, secondSpan, "Same value", secondTime, now.AddMinutes(1));
        var contradiction = graph.AddContradiction(Guid.NewGuid(), first.Id, second.Id,
            ContradictionType.TemporalMismatch, "synthetic-temporal-rule", now.AddMinutes(2));
        var loaded = new PersistedMatterBrain(graph, new WorkplaceMatter(graph), new MatterBrainState(graph));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(MeetingPreparationProjection.Create(loaded, false)));
        var dispute = Assert.Single(json.RootElement.GetProperty("unresolvedDisputes").EnumerateArray(),
            item => item.GetProperty("Id").GetGuid() == contradiction.Id);
        var assertions = dispute.GetProperty("assertions").EnumerateArray().ToArray();

        Assert.Contains(assertions, item => item.GetProperty("Id").GetGuid() == first.Id &&
                                           item.GetProperty("EventTime").GetDateTimeOffset() == firstTime);
        Assert.Contains(assertions, item => item.GetProperty("Id").GetGuid() == second.Id &&
                                           item.GetProperty("EventTime").GetDateTimeOffset() == secondTime);
    }

    [Fact]
    public void Superseded_correction_does_not_create_a_stale_chronology_date_conflict()
    {
        var now = new DateTimeOffset(2026, 5, 8, 9, 0, 0, TimeSpan.Zero);
        var graph = CreateGraph(now, out var sourceSpan, out _);
        var eventTime = new DateTimeOffset(2026, 4, 12, 9, 0, 0, TimeSpan.Zero);
        var original = AddAssertion(graph, sourceSpan, "Original date", eventTime, now);
        var matterEvent = graph.AddEvent(Guid.NewGuid(), "meeting", "Synthetic corrected meeting",
            EventStatus.Candidate, VerificationState.NotReviewed, eventTime);
        graph.AddAssertionEventLink(Guid.NewGuid(), original.Id, matterEvent.Id, AssertionEventRelation.Supports);
        var workplace = new WorkplaceMatter(graph);
        var brain = new MatterBrainState(graph);
        var firstCorrectionId = Guid.NewGuid();
        var finalCorrectionId = Guid.NewGuid();

        brain.CorrectAssertion(original.Id, firstCorrectionId, original.Value, eventTime.AddDays(1),
            Guid.NewGuid(), "synthetic-reviewer", now.AddHours(1));
        brain.CorrectAssertion(firstCorrectionId, finalCorrectionId, original.Value, eventTime,
            Guid.NewGuid(), "synthetic-reviewer", now.AddHours(2));

        var gaps = FactualGapAnalyzer.Analyze(graph, workplace, brain);

        Assert.DoesNotContain(gaps, item => item.Code == "chronology-date-conflict" &&
                                            item.RelatedRecordIds.Contains(matterEvent.Id));
        Assert.Contains(gaps, item => item.Code == "corrected-history-review" &&
                                     item.RelatedRecordIds.Contains(firstCorrectionId));
        Assert.Contains(graph.Assertions, item => item.Id == firstCorrectionId &&
                                                  item.DisputeState == DisputeState.Superseded &&
                                                  item.SupersededByAssertionId == finalCorrectionId);
    }

    private static MatterEvidenceGraph CreateGraph(
        DateTimeOffset now,
        out SourceSpan firstSpan,
        out SourceSpan secondSpan)
    {
        var graph = new MatterEvidenceGraph(new Matter(Guid.NewGuid(), new TenantId(Guid.NewGuid()),
            "workplace-dispute", "Synthetic chronology Matter", "active", now, now));
        firstSpan = AddSource(graph, 'C', "Synthetic first attributed event-time statement.");
        secondSpan = AddSource(graph, 'D', "Synthetic second attributed event-time statement.");
        return graph;
    }

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
        DateTimeOffset eventTime,
        DateTimeOffset assertedAt) => graph.AddAssertion(
        Guid.NewGuid(), "synthetic-employee", "meeting-date", value, "Synthetic source", assertedAt,
        EvidenceOriginClass.OriginalContemporaneousRecord, AssertionClass.AttributedAssertion,
        DisputeState.Contradicted, IntegrityState.OriginalHashVerified, VerificationState.NeedsContext,
        span.Id, eventTime, 0.99m);
}
