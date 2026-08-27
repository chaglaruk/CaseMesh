using System.Text.Json;
using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Core.Workplace;
using CaseMesh.MatterBrain;
using CaseMesh.Persistence.Postgres;

namespace CaseMesh.Api.Tests;

public sealed class MeetingPreparationCorrectionHistoryTests
{
    [Fact]
    public void Chronology_separates_supporting_qualifying_and_contradicting_sources()
    {
        var now = new DateTimeOffset(2026, 3, 12, 9, 0, 0, TimeSpan.Zero);
        var graph = CreateGraph(now);
        var supportingSpan = AddSource(graph, 'A', "Synthetic source supports the meeting on 12 March.");
        var contradictingSpan = AddSource(graph, 'B', "Synthetic source says the meeting was not on 12 March.");
        var supporting = AddAssertion(graph, supportingSpan, "meeting-date", "2026-03-12", now);
        var contradicting = AddAssertion(graph, contradictingSpan, "meeting-date", "not-2026-03-12", now.AddMinutes(1));
        var matterEvent = graph.AddEvent(Guid.NewGuid(), "synthetic-meeting", "Synthetic review meeting",
            EventStatus.Candidate, VerificationState.NotReviewed, now);
        graph.AddAssertionEventLink(Guid.NewGuid(), supporting.Id, matterEvent.Id, AssertionEventRelation.Supports);
        graph.AddAssertionEventLink(Guid.NewGuid(), contradicting.Id, matterEvent.Id, AssertionEventRelation.Contradicts);
        var loaded = Load(graph);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(MeetingPreparationProjection.Create(loaded, false)));
        var chronology = Assert.Single(json.RootElement.GetProperty("chronology").EnumerateArray());
        var primary = chronology.GetProperty("sourceSpanIds").EnumerateArray().Select(item => item.GetGuid()).ToArray();
        var contradictingIds = chronology.GetProperty("contradictingSourceSpanIds").EnumerateArray()
            .Select(item => item.GetGuid()).ToArray();

        Assert.Contains(supportingSpan.Id, primary);
        Assert.DoesNotContain(contradictingSpan.Id, primary);
        Assert.Contains(contradictingSpan.Id, contradictingIds);
    }

    [Fact]
    public void Assertion_correction_is_reviewable_with_original_source_and_human_replacement()
    {
        var now = new DateTimeOffset(2026, 3, 12, 9, 0, 0, TimeSpan.Zero);
        var graph = CreateGraph(now);
        var source = AddSource(graph, 'C', "Synthetic record says twelve sickness days.");
        var original = AddAssertion(graph, source, "sickness-day-count", "12", now);
        var loaded = Load(graph);
        var correctedId = Guid.NewGuid();

        loaded.Brain.CorrectAssertion(original.Id, correctedId, "10", now,
            Guid.NewGuid(), "synthetic-reviewer", now.AddHours(1));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(MeetingPreparationProjection.Create(loaded, false)));
        var history = Assert.Single(json.RootElement.GetProperty("correctionHistory").EnumerateArray());
        Assert.Equal("AssertionCorrected", history.GetProperty("Kind").GetString());
        Assert.Equal("synthetic-reviewer", history.GetProperty("Actor").GetString());
        Assert.Equal("12", history.GetProperty("Original").GetProperty("Value").GetString());
        Assert.Equal("10", history.GetProperty("Replacement").GetProperty("Value").GetString());
        Assert.Equal("OriginalContemporaneousRecord", history.GetProperty("Original").GetProperty("Origin").GetString());
        Assert.Equal("AttributedAssertion", history.GetProperty("Original").GetProperty("AssertionClass").GetString());
        Assert.Equal("RetrospectiveNote", history.GetProperty("Replacement").GetProperty("Origin").GetString());
        Assert.Equal("AttributedAssertion", history.GetProperty("Replacement").GetProperty("AssertionClass").GetString());
        Assert.Contains(source.Id, history.GetProperty("HistoricalSourceSpanIds").EnumerateArray()
            .Select(item => item.GetGuid()));
        Assert.Contains("original attributed statement", history.GetProperty("Notice").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(json.RootElement.GetProperty("questionsToClarify").EnumerateArray(), item =>
            item.GetProperty("Code").GetString() == "corrected-history-review" &&
            item.GetProperty("route").GetString() == "prepare");
    }

    [Fact]
    public void Corrected_ai_inference_remains_labelled_as_historical_ai_and_never_gains_documentary_provenance()
    {
        var now = new DateTimeOffset(2026, 3, 12, 10, 0, 0, TimeSpan.Zero);
        var graph = CreateGraph(now);
        var original = graph.AddAssertion(
            Guid.NewGuid(), "synthetic-model", "suggested-framing", "Synthetic inference", "synthetic-model", now,
            EvidenceOriginClass.AiGeneratedInference, AssertionClass.AiInference,
            DisputeState.Unverified, IntegrityState.DerivedCopy, VerificationState.NotReviewed,
            sourceSpanId: null, eventTime: null, extractionConfidence: null, createdByModel: "synthetic-model/1");
        var loaded = Load(graph);

        loaded.Brain.CorrectAssertion(original.Id, Guid.NewGuid(), "Human correction", null,
            Guid.NewGuid(), "synthetic-reviewer", now.AddMinutes(1));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(MeetingPreparationProjection.Create(loaded, false)));
        var history = Assert.Single(json.RootElement.GetProperty("correctionHistory").EnumerateArray());
        var originalSnapshot = history.GetProperty("Original");
        var replacementSnapshot = history.GetProperty("Replacement");

        Assert.Equal("AiGeneratedInference", originalSnapshot.GetProperty("Origin").GetString());
        Assert.Equal("AiInference", originalSnapshot.GetProperty("AssertionClass").GetString());
        Assert.Equal("RetrospectiveNote", replacementSnapshot.GetProperty("Origin").GetString());
        Assert.Equal("AttributedAssertion", replacementSnapshot.GetProperty("AssertionClass").GetString());
        Assert.Empty(history.GetProperty("HistoricalSourceSpanIds").EnumerateArray());
        Assert.Contains("AI inference", history.GetProperty("Notice").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Corrected_event_is_kept_in_audit_history_without_reusing_old_span_as_current_date_support()
    {
        var extractedDate = new DateTimeOffset(2026, 3, 12, 9, 0, 0, TimeSpan.Zero);
        var correctedDate = extractedDate.AddDays(1);
        var graph = CreateGraph(extractedDate);
        var source = AddSource(graph, 'D', "Synthetic record says the meeting was on 12 March.");
        var assertion = AddAssertion(graph, source, "meeting-date", "2026-03-12", extractedDate);
        var original = graph.AddEvent(Guid.NewGuid(), "synthetic-meeting", "Synthetic review meeting",
            EventStatus.Candidate, VerificationState.Confirmed, extractedDate, extractedDate);
        graph.AddAssertionEventLink(Guid.NewGuid(), assertion.Id, original.Id, AssertionEventRelation.Supports);
        var correction = graph.CorrectEventDate(original.Id, Guid.NewGuid(), correctedDate, correctedDate,
            "Synthetic review meeting on 13 March", Guid.NewGuid(), "synthetic-reviewer", extractedDate.AddHours(1));
        graph.AddAssertionEventLink(Guid.NewGuid(), assertion.Id, correction.CorrectedEvent.Id,
            AssertionEventRelation.Supports);
        var loaded = Load(graph);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(MeetingPreparationProjection.Create(loaded, false)));
        Assert.DoesNotContain(json.RootElement.GetProperty("chronology").EnumerateArray(), item =>
            item.GetProperty("Id").GetGuid() == correction.CorrectedEvent.Id);
        var history = Assert.Single(json.RootElement.GetProperty("correctionHistory").EnumerateArray());
        Assert.Equal("EventCorrected", history.GetProperty("Kind").GetString());
        Assert.Equal(extractedDate, history.GetProperty("Original").GetProperty("StartTime").GetDateTimeOffset());
        Assert.Equal(correctedDate, history.GetProperty("Replacement").GetProperty("StartTime").GetDateTimeOffset());
        Assert.Contains(source.Id, history.GetProperty("HistoricalSourceSpanIds").EnumerateArray()
            .Select(item => item.GetGuid()));
        Assert.Contains("intentionally not cited", history.GetProperty("Notice").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static MatterEvidenceGraph CreateGraph(DateTimeOffset now) => new(new Matter(
        Guid.NewGuid(), new TenantId(Guid.NewGuid()), "workplace-dispute", "Synthetic Matter", "active", now, now));

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
        SourceSpan source,
        string predicate,
        string value,
        DateTimeOffset assertedAt) => graph.AddAssertion(
        Guid.NewGuid(), "synthetic-employee", predicate, value, "synthetic source", assertedAt,
        EvidenceOriginClass.OriginalContemporaneousRecord, AssertionClass.AttributedAssertion,
        DisputeState.Unverified, IntegrityState.OriginalHashVerified, VerificationState.NotReviewed,
        source.Id, eventTime: assertedAt, extractionConfidence: 0.99m);
}
