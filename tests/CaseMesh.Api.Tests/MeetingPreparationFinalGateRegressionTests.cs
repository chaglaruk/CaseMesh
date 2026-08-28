using System.Text.Json;
using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Core.Workplace;
using CaseMesh.MatterBrain;
using CaseMesh.Persistence.Postgres;

namespace CaseMesh.Api.Tests;

public sealed class MeetingPreparationFinalGateRegressionTests
{
    [Fact]
    public async Task Partially_invalidated_multi_source_supersedes_link_cannot_hide_current_event()
    {
        var now = new DateTimeOffset(2026, 6, 5, 9, 0, 0, TimeSpan.Zero);
        var graph = CreateGraph(now);
        var assertionSpan = AddSource(graph, 'A', "Synthetic superseding assertion source.");
        var eventSpan = AddSource(graph, 'B', "Synthetic direct event source.");
        var firstLinkSpan = AddSource(graph, 'C', "Synthetic first relationship source.");
        var secondLinkSpan = AddSource(graph, 'D', "Synthetic second relationship source.");
        var state = new MatterBrainState(graph);
        var eventTime = new DateTimeOffset(2026, 5, 24, 10, 0, 0, TimeSpan.Zero);
        var service = new MatterBrainMergeService(new SteppingTimeProvider(now));
        var initial = new FixedExtractionProvider(new StructuredExtractionOutput(
            "{\"relationship\":\"multi-source-supersedes\"}",
            new StructuredCandidateBatch(
                [],
                [],
                [new AssertionCandidate(
                    "assertion-1", "synthetic-subject", "meeting-date", "replacement account", "synthetic source",
                    now, eventTime.AddDays(1), assertionSpan.Id,
                    EvidenceOriginClass.OriginalContemporaneousRecord, AssertionClass.AttributedAssertion,
                    IntegrityState.OriginalHashVerified, [assertionSpan.Id], 0.95m)],
                [new EventCandidate("event-1", "meeting", "Synthetic old event", eventTime, null,
                    [], [eventSpan.Id], 0.96m)],
                [new AssertionEventLinkCandidate("link-1", "assertion-1", "event-1",
                    AssertionEventRelation.Supersedes, [firstLinkSpan.Id, secondLinkSpan.Id], 0.92m)],
                [],
                [])), "multi-source-link-v1");

        await service.ExtractAndMergeAsync(
            state,
            [assertionSpan.Id, eventSpan.Id, firstLinkSpan.Id, secondLinkSpan.Id],
            initial);
        var matterEvent = Assert.Single(graph.Events);
        var link = Assert.Single(graph.AssertionEventLinks);

        using (var before = Project(graph, state))
        {
            Assert.DoesNotContain(before.RootElement.GetProperty("chronology").EnumerateArray(),
                item => item.GetProperty("Id").GetGuid() == matterEvent.Id);
        }

        await service.ExtractAndMergeAsync(
            state,
            [firstLinkSpan.Id],
            new FixedExtractionProvider(EmptyOutput("partial-link-reextraction"), "multi-source-link-v2"));

        Assert.DoesNotContain(state.ActiveDependencies, dependency =>
            dependency.CanonicalKind == CanonicalRecordKind.AssertionEventLink &&
            dependency.CanonicalId == link.Id &&
            dependency.SourceSpanId == firstLinkSpan.Id);
        Assert.Contains(state.ActiveDependencies, dependency =>
            dependency.CanonicalKind == CanonicalRecordKind.AssertionEventLink &&
            dependency.CanonicalId == link.Id &&
            dependency.SourceSpanId == secondLinkSpan.Id);

        using var after = Project(graph, state);
        Assert.Contains(after.RootElement.GetProperty("chronology").EnumerateArray(),
            item => item.GetProperty("Id").GetGuid() == matterEvent.Id);
        Assert.Equal(EventStatus.Candidate, matterEvent.Status);
    }

    [Fact]
    public async Task Invalidated_event_cannot_leave_stale_chronology_date_conflict_question()
    {
        var now = new DateTimeOffset(2026, 6, 5, 10, 0, 0, TimeSpan.Zero);
        var graph = CreateGraph(now);
        var firstAssertionSpan = AddSource(graph, 'A', "Synthetic first dated assertion.");
        var secondAssertionSpan = AddSource(graph, 'B', "Synthetic second dated assertion.");
        var eventSpan = AddSource(graph, 'C', "Synthetic event source.");
        var firstLinkSpan = AddSource(graph, 'D', "Synthetic first link source.");
        var secondLinkSpan = AddSource(graph, 'E', "Synthetic second link source.");
        var state = new MatterBrainState(graph);
        var eventTime = new DateTimeOffset(2026, 5, 25, 10, 0, 0, TimeSpan.Zero);
        var service = new MatterBrainMergeService(new SteppingTimeProvider(now));
        var initial = new FixedExtractionProvider(new StructuredExtractionOutput(
            "{\"chronology\":\"two-dates\"}",
            new StructuredCandidateBatch(
                [],
                [],
                [
                    new AssertionCandidate(
                        "assertion-1", "synthetic-subject", "meeting-date", "25 May", "synthetic source one",
                        now, eventTime, firstAssertionSpan.Id,
                        EvidenceOriginClass.OriginalContemporaneousRecord, AssertionClass.AttributedAssertion,
                        IntegrityState.OriginalHashVerified, [firstAssertionSpan.Id], 0.95m),
                    new AssertionCandidate(
                        "assertion-2", "synthetic-subject", "meeting-date", "26 May", "synthetic source two",
                        now.AddMinutes(1), eventTime.AddDays(1), secondAssertionSpan.Id,
                        EvidenceOriginClass.OriginalContemporaneousRecord, AssertionClass.AttributedAssertion,
                        IntegrityState.OriginalHashVerified, [secondAssertionSpan.Id], 0.94m)
                ],
                [new EventCandidate("event-1", "meeting", "Synthetic event", eventTime, null,
                    [], [eventSpan.Id], 0.96m)],
                [
                    new AssertionEventLinkCandidate("link-1", "assertion-1", "event-1",
                        AssertionEventRelation.Supports, [firstLinkSpan.Id], 0.93m),
                    new AssertionEventLinkCandidate("link-2", "assertion-2", "event-1",
                        AssertionEventRelation.Supports, [secondLinkSpan.Id], 0.92m)
                ],
                [],
                [])), "event-currentness-v1");

        await service.ExtractAndMergeAsync(
            state,
            [firstAssertionSpan.Id, secondAssertionSpan.Id, eventSpan.Id, firstLinkSpan.Id, secondLinkSpan.Id],
            initial);

        using (var before = Project(graph, state))
        {
            Assert.Contains(before.RootElement.GetProperty("questionsToClarify").EnumerateArray(),
                item => item.GetProperty("Code").GetString() == "chronology-date-conflict");
        }

        await service.ExtractAndMergeAsync(
            state,
            [eventSpan.Id],
            new FixedExtractionProvider(EmptyOutput("event-reextracted-away"), "event-currentness-v2"));

        Assert.DoesNotContain(state.ActiveDependencies, dependency =>
            dependency.CanonicalKind == CanonicalRecordKind.Event &&
            dependency.SourceSpanId == eventSpan.Id);
        Assert.Contains(state.ActiveDependencies, dependency =>
            dependency.CanonicalKind == CanonicalRecordKind.AssertionEventLink &&
            dependency.SourceSpanId == firstLinkSpan.Id);

        using var after = Project(graph, state);
        Assert.DoesNotContain(after.RootElement.GetProperty("questionsToClarify").EnumerateArray(),
            item => item.GetProperty("Code").GetString() == "chronology-date-conflict");
    }

    [Fact]
    public void Superseded_source_less_correction_cannot_remain_as_documentary_source_gap()
    {
        var now = new DateTimeOffset(2026, 6, 5, 11, 0, 0, TimeSpan.Zero);
        var graph = CreateGraph(now);
        var sourceSpan = AddSource(graph, 'F', "Synthetic original documentary assertion.");
        var state = new MatterBrainState(graph);
        var originalId = Guid.NewGuid();
        graph.AddAssertion(
            originalId,
            "synthetic-subject",
            "recorded-point",
            "original value",
            "synthetic source",
            now,
            EvidenceOriginClass.OriginalContemporaneousRecord,
            AssertionClass.AttributedAssertion,
            DisputeState.Unverified,
            IntegrityState.OriginalHashVerified,
            VerificationState.NotReviewed,
            sourceSpan.Id,
            now.AddDays(-1),
            0.95m,
            null);

        var firstCorrectionId = Guid.NewGuid();
        graph.CorrectAssertion(
            originalId,
            firstCorrectionId,
            "first human correction",
            now.AddDays(-1),
            Guid.NewGuid(),
            "synthetic-user",
            now.AddMinutes(1));

        using (var firstProjection = Project(graph, state))
        {
            Assert.Contains(SourceLessAssertionGaps(firstProjection), gap =>
                gap.GetProperty("RelatedRecordIds").EnumerateArray()
                    .Any(id => id.GetGuid() == firstCorrectionId));
        }

        var secondCorrectionId = Guid.NewGuid();
        graph.CorrectAssertion(
            firstCorrectionId,
            secondCorrectionId,
            "second human correction",
            now.AddDays(-1),
            Guid.NewGuid(),
            "synthetic-user",
            now.AddMinutes(2));

        using var secondProjection = Project(graph, state);
        var currentGaps = SourceLessAssertionGaps(secondProjection);
        Assert.DoesNotContain(currentGaps, gap =>
            gap.GetProperty("RelatedRecordIds").EnumerateArray()
                .Any(id => id.GetGuid() == firstCorrectionId));
        Assert.Contains(currentGaps, gap =>
            gap.GetProperty("RelatedRecordIds").EnumerateArray()
                .Any(id => id.GetGuid() == secondCorrectionId));
        Assert.Contains(secondProjection.RootElement.GetProperty("correctionHistory").EnumerateArray(),
            item => item.GetProperty("Replacement").GetProperty("Id").GetGuid() == secondCorrectionId);
    }

    private static JsonElement[] SourceLessAssertionGaps(JsonDocument document) =>
        document.RootElement.GetProperty("questionsToClarify").EnumerateArray()
            .Where(item => item.GetProperty("Code").GetString() == "assertion-without-documentary-source")
            .ToArray();

    private static StructuredExtractionOutput EmptyOutput(string marker) =>
        new($"{{\"{marker}\":true}}", new StructuredCandidateBatch([], [], [], [], [], [], []));

    private static JsonDocument Project(MatterEvidenceGraph graph, MatterBrainState state) =>
        JsonDocument.Parse(JsonSerializer.Serialize(MeetingPreparationProjection.Create(
            new PersistedMatterBrain(graph, new WorkplaceMatter(graph), state), false)));

    private static MatterEvidenceGraph CreateGraph(DateTimeOffset now) => new(new Matter(
        Guid.NewGuid(), new TenantId(Guid.NewGuid()), "workplace-dispute", "Synthetic final-gate Matter", "active", now, now));

    private static SourceSpan AddSource(MatterEvidenceGraph graph, char hash, string text)
    {
        var version = graph.RegisterDocumentVersion(Guid.NewGuid(), Guid.NewGuid(), new string(hash, 64), Guid.NewGuid());
        return graph.AddSourceSpan(Guid.NewGuid(), version, text, "synthetic-parser/1", 0.99m,
            pageNumber: 1, textStart: 0, textEnd: text.Length);
    }

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
