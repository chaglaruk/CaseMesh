using System.Text.Json;
using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Core.Workplace;
using CaseMesh.MatterBrain;
using CaseMesh.Persistence.Postgres;

namespace CaseMesh.Api.Tests;

public sealed class MeetingPreparationLateReviewRegressionTests
{
    [Fact]
    public async Task Invalidated_support_link_cannot_supply_current_chronology_citation()
    {
        var now = new DateTimeOffset(2026, 6, 3, 9, 0, 0, TimeSpan.Zero);
        var graph = CreateGraph(now);
        var assertionSpan = AddSource(graph, 'A', "Synthetic statement supports the dated event.");
        var eventSpan = AddSource(graph, 'B', "Synthetic direct event source.");
        var linkSpan = AddSource(graph, 'C', "Synthetic relationship extraction source.");
        var state = new MatterBrainState(graph);
        var eventTime = new DateTimeOffset(2026, 5, 20, 11, 0, 0, TimeSpan.Zero);
        var service = new MatterBrainMergeService(new SteppingTimeProvider(now));
        var initial = new FixedExtractionProvider(new StructuredExtractionOutput(
            "{\"relationship\":\"supports\"}",
            new StructuredCandidateBatch(
                [],
                [],
                [new AssertionCandidate(
                    "assertion-1", "synthetic-subject", "meeting-date", "20 May", "synthetic source",
                    now, eventTime, assertionSpan.Id,
                    EvidenceOriginClass.OriginalContemporaneousRecord, AssertionClass.AttributedAssertion,
                    IntegrityState.OriginalHashVerified, [assertionSpan.Id], 0.95m)],
                [new EventCandidate("event-1", "meeting", "Synthetic event", eventTime, null,
                    [], [eventSpan.Id], 0.96m)],
                [new AssertionEventLinkCandidate("link-1", "assertion-1", "event-1",
                    AssertionEventRelation.Supports, [linkSpan.Id], 0.91m)],
                [],
                [])), "relationship-v1");

        await service.ExtractAndMergeAsync(state, [assertionSpan.Id, eventSpan.Id, linkSpan.Id], initial);
        var matterEvent = Assert.Single(graph.Events);
        using (var before = Project(graph, state))
        {
            var projected = Assert.Single(before.RootElement.GetProperty("chronology").EnumerateArray(),
                item => item.GetProperty("Id").GetGuid() == matterEvent.Id);
            Assert.Contains(assertionSpan.Id,
                projected.GetProperty("sourceSpanIds").EnumerateArray().Select(item => item.GetGuid()));
        }

        await service.ExtractAndMergeAsync(
            state,
            [linkSpan.Id],
            new FixedExtractionProvider(EmptyOutput("support-link-reextracted"), "relationship-v2"));

        Assert.Contains(state.ActiveDependencies, dependency =>
            dependency.CanonicalKind == CanonicalRecordKind.Assertion && dependency.SourceSpanId == assertionSpan.Id);
        Assert.Contains(state.ActiveDependencies, dependency =>
            dependency.CanonicalKind == CanonicalRecordKind.Event && dependency.SourceSpanId == eventSpan.Id);
        Assert.DoesNotContain(state.ActiveDependencies, dependency =>
            dependency.CanonicalKind == CanonicalRecordKind.AssertionEventLink && dependency.SourceSpanId == linkSpan.Id);

        using var after = Project(graph, state);
        var current = Assert.Single(after.RootElement.GetProperty("chronology").EnumerateArray(),
            item => item.GetProperty("Id").GetGuid() == matterEvent.Id);
        var sourceIds = current.GetProperty("sourceSpanIds").EnumerateArray().Select(item => item.GetGuid()).ToArray();
        Assert.Contains(eventSpan.Id, sourceIds);
        Assert.DoesNotContain(assertionSpan.Id, sourceIds);
    }

    [Fact]
    public async Task Invalidated_supersedes_link_cannot_hide_current_event()
    {
        var now = new DateTimeOffset(2026, 6, 3, 10, 0, 0, TimeSpan.Zero);
        var graph = CreateGraph(now);
        var assertionSpan = AddSource(graph, 'D', "Synthetic superseding statement.");
        var eventSpan = AddSource(graph, 'E', "Synthetic current event source.");
        var linkSpan = AddSource(graph, 'F', "Synthetic supersedes relationship source.");
        var state = new MatterBrainState(graph);
        var eventTime = new DateTimeOffset(2026, 5, 21, 11, 0, 0, TimeSpan.Zero);
        var service = new MatterBrainMergeService(new SteppingTimeProvider(now));
        var initial = new FixedExtractionProvider(new StructuredExtractionOutput(
            "{\"relationship\":\"supersedes\"}",
            new StructuredCandidateBatch(
                [],
                [],
                [new AssertionCandidate(
                    "assertion-1", "synthetic-subject", "meeting-date", "replacement account", "synthetic source",
                    now, eventTime.AddDays(1), assertionSpan.Id,
                    EvidenceOriginClass.OriginalContemporaneousRecord, AssertionClass.AttributedAssertion,
                    IntegrityState.OriginalHashVerified, [assertionSpan.Id], 0.94m)],
                [new EventCandidate("event-1", "meeting", "Synthetic old event", eventTime, null,
                    [], [eventSpan.Id], 0.96m)],
                [new AssertionEventLinkCandidate("link-1", "assertion-1", "event-1",
                    AssertionEventRelation.Supersedes, [linkSpan.Id], 0.92m)],
                [],
                [])), "supersedes-v1");

        await service.ExtractAndMergeAsync(state, [assertionSpan.Id, eventSpan.Id, linkSpan.Id], initial);
        var matterEvent = Assert.Single(graph.Events);
        using (var before = Project(graph, state))
        {
            Assert.DoesNotContain(before.RootElement.GetProperty("chronology").EnumerateArray(),
                item => item.GetProperty("Id").GetGuid() == matterEvent.Id);
        }

        await service.ExtractAndMergeAsync(
            state,
            [linkSpan.Id],
            new FixedExtractionProvider(EmptyOutput("supersedes-link-reextracted"), "supersedes-v2"));

        using var after = Project(graph, state);
        Assert.Contains(after.RootElement.GetProperty("chronology").EnumerateArray(),
            item => item.GetProperty("Id").GetGuid() == matterEvent.Id);
        Assert.Equal(EventStatus.Candidate, matterEvent.Status);
    }

    [Fact]
    public async Task Partially_invalidated_multi_source_person_does_not_claim_field_level_provenance()
    {
        var now = new DateTimeOffset(2026, 6, 3, 11, 0, 0, TimeSpan.Zero);
        var graph = CreateGraph(now);
        var firstSpan = AddSource(graph, 'A', "Synthetic first identity source.");
        var secondSpan = AddSource(graph, 'B', "Synthetic second identity source.");
        var state = new MatterBrainState(graph);
        var service = new MatterBrainMergeService(new SteppingTimeProvider(now));
        var initial = new FixedExtractionProvider(new StructuredExtractionOutput(
            "{\"person\":\"multi-source\"}",
            new StructuredCandidateBatch(
                [new EntityCandidate(
                    "person-1", CanonicalEntityKind.Person, "Synthetic Person", "person",
                    ["S. Person"], ["Employee"], [firstSpan.Id, secondSpan.Id], 0.93m)],
                [], [], [], [], [], [])), "person-v1");

        await service.ExtractAndMergeAsync(state, [firstSpan.Id, secondSpan.Id], initial);
        var person = Assert.Single(state.People);
        using (var before = Project(graph, state))
        {
            var participant = Assert.Single(before.RootElement.GetProperty("participants").EnumerateArray(),
                item => item.GetProperty("Id").GetGuid() == person.Id);
            Assert.Equal("SourceBackedExtraction", participant.GetProperty("provenanceStatus").GetString());
            Assert.Equal(2, participant.GetProperty("sourceSpanIds").GetArrayLength());
        }

        await service.ExtractAndMergeAsync(
            state,
            [firstSpan.Id],
            new FixedExtractionProvider(EmptyOutput("person-source-reextracted"), "person-v2"));

        Assert.Contains(state.ActiveDependencies, dependency =>
            dependency.CanonicalKind == CanonicalRecordKind.Person &&
            dependency.CanonicalId == person.Id && dependency.SourceSpanId == secondSpan.Id);
        Assert.DoesNotContain(state.ActiveDependencies, dependency =>
            dependency.CanonicalKind == CanonicalRecordKind.Person &&
            dependency.CanonicalId == person.Id && dependency.SourceSpanId == firstSpan.Id);

        using var after = Project(graph, state);
        var current = Assert.Single(after.RootElement.GetProperty("participants").EnumerateArray(),
            item => item.GetProperty("Id").GetGuid() == person.Id);
        Assert.Equal("Unsupported", current.GetProperty("provenanceStatus").GetString());
        Assert.Empty(current.GetProperty("sourceSpanIds").EnumerateArray());
        var member = Assert.Single(current.GetProperty("identityMembers").EnumerateArray());
        Assert.Equal("Unsupported", member.GetProperty("provenanceStatus").GetString());
        Assert.Empty(member.GetProperty("sourceSpanIds").EnumerateArray());
        Assert.Contains("incomplete", member.GetProperty("provenanceNotice").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    private static StructuredExtractionOutput EmptyOutput(string marker) =>
        new($"{{\"{marker}\":true}}", new StructuredCandidateBatch([], [], [], [], [], [], []));

    private static JsonDocument Project(MatterEvidenceGraph graph, MatterBrainState state) =>
        JsonDocument.Parse(JsonSerializer.Serialize(MeetingPreparationProjection.Create(
            new PersistedMatterBrain(graph, new WorkplaceMatter(graph), state), false)));

    private static MatterEvidenceGraph CreateGraph(DateTimeOffset now) => new(new Matter(
        Guid.NewGuid(), new TenantId(Guid.NewGuid()), "workplace-dispute", "Synthetic late-review Matter", "active", now, now));

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
