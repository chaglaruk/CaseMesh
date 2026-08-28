using System.Text.Json;
using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Core.Workplace;
using CaseMesh.MatterBrain;
using CaseMesh.Persistence.Postgres;

namespace CaseMesh.Api.Tests;

public sealed class MeetingPreparationPostFinalReviewRegressionTests
{
    [Fact]
    public async Task Invalidated_assertion_event_link_cannot_create_stale_chronology_gap()
    {
        var now = new DateTimeOffset(2026, 6, 4, 9, 0, 0, TimeSpan.Zero);
        var graph = CreateGraph(now);
        var firstAssertionSpan = AddSource(graph, 'A', "Synthetic first dated account.");
        var secondAssertionSpan = AddSource(graph, 'B', "Synthetic second dated account.");
        var eventSpan = AddSource(graph, 'C', "Synthetic event source.");
        var firstLinkSpan = AddSource(graph, 'D', "Synthetic first relationship source.");
        var secondLinkSpan = AddSource(graph, 'E', "Synthetic second relationship source.");
        var state = new MatterBrainState(graph);
        var eventTime = new DateTimeOffset(2026, 5, 22, 11, 0, 0, TimeSpan.Zero);
        var service = new MatterBrainMergeService(new SteppingTimeProvider(now));
        var initial = new FixedExtractionProvider(new StructuredExtractionOutput(
            "{\"relationships\":\"two-supports\"}",
            new StructuredCandidateBatch(
                [],
                [],
                [
                    new AssertionCandidate(
                        "assertion-1", "synthetic-subject", "meeting-date", "22 May", "synthetic source one",
                        now, eventTime, firstAssertionSpan.Id,
                        EvidenceOriginClass.OriginalContemporaneousRecord, AssertionClass.AttributedAssertion,
                        IntegrityState.OriginalHashVerified, [firstAssertionSpan.Id], 0.95m),
                    new AssertionCandidate(
                        "assertion-2", "synthetic-subject", "meeting-date", "23 May", "synthetic source two",
                        now.AddMinutes(1), eventTime.AddDays(1), secondAssertionSpan.Id,
                        EvidenceOriginClass.OriginalContemporaneousRecord, AssertionClass.AttributedAssertion,
                        IntegrityState.OriginalHashVerified, [secondAssertionSpan.Id], 0.94m)
                ],
                [new EventCandidate("event-1", "meeting", "Synthetic event", eventTime, null,
                    [], [eventSpan.Id], 0.96m)],
                [
                    new AssertionEventLinkCandidate("link-1", "assertion-1", "event-1",
                        AssertionEventRelation.Supports, [firstLinkSpan.Id], 0.92m),
                    new AssertionEventLinkCandidate("link-2", "assertion-2", "event-1",
                        AssertionEventRelation.Supports, [secondLinkSpan.Id], 0.91m)
                ],
                [],
                [])), "gap-link-v1");

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
            [secondLinkSpan.Id],
            new FixedExtractionProvider(EmptyOutput("stale-link-removed"), "gap-link-v2"));

        Assert.DoesNotContain(state.ActiveDependencies, dependency =>
            dependency.CanonicalKind == CanonicalRecordKind.AssertionEventLink &&
            dependency.SourceSpanId == secondLinkSpan.Id);
        Assert.Contains(state.ActiveDependencies, dependency =>
            dependency.CanonicalKind == CanonicalRecordKind.Assertion &&
            dependency.SourceSpanId == secondAssertionSpan.Id);

        using var after = Project(graph, state);
        Assert.DoesNotContain(after.RootElement.GetProperty("questionsToClarify").EnumerateArray(),
            item => item.GetProperty("Code").GetString() == "chronology-date-conflict");
    }

    [Fact]
    public async Task Duplicate_candidate_roles_preserve_source_backed_participant_provenance()
    {
        var now = new DateTimeOffset(2026, 6, 4, 10, 0, 0, TimeSpan.Zero);
        var graph = CreateGraph(now);
        var personSpan = AddSource(graph, 'F', "Synthetic employee identity source.");
        var state = new MatterBrainState(graph);
        var service = new MatterBrainMergeService(new SteppingTimeProvider(now));
        var provider = new FixedExtractionProvider(new StructuredExtractionOutput(
            "{\"person\":\"duplicate-role\"}",
            new StructuredCandidateBatch(
                [new EntityCandidate(
                    "person-1", CanonicalEntityKind.Person, "Synthetic Employee", "person",
                    [], ["Employee", "Employee"], [personSpan.Id], 0.97m)],
                [], [], [], [], [], [])), "duplicate-role-v1");

        await service.ExtractAndMergeAsync(state, [personSpan.Id], provider);

        var person = Assert.Single(state.People);
        Assert.Equal(["Employee"], person.RoleLabels);
        using var projected = Project(graph, state);
        var participant = Assert.Single(projected.RootElement.GetProperty("participants").EnumerateArray(),
            item => item.GetProperty("Id").GetGuid() == person.Id);
        Assert.Equal("SourceBackedExtraction", participant.GetProperty("provenanceStatus").GetString());
        Assert.Contains(personSpan.Id,
            participant.GetProperty("sourceSpanIds").EnumerateArray().Select(item => item.GetGuid()));
    }

    [Fact]
    public async Task Superseding_assertion_outside_priority_cap_remains_visible_when_event_is_gated()
    {
        var now = new DateTimeOffset(2026, 6, 4, 11, 0, 0, TimeSpan.Zero);
        var graph = CreateGraph(now);
        var assertionSpans = Enumerable.Range(0, 13)
            .Select(index => AddSource(graph, (char)('A' + (index % 6)), $"Synthetic assertion source {index + 1}."))
            .ToArray();
        var eventSpan = AddSource(graph, 'E', "Synthetic old event source.");
        var linkSpan = AddSource(graph, 'F', "Synthetic superseding relationship source.");
        var state = new MatterBrainState(graph);
        var service = new MatterBrainMergeService(new SteppingTimeProvider(now));
        var assertionCandidates = assertionSpans
            .Select((span, index) => new AssertionCandidate(
                $"assertion-{index + 1}",
                "synthetic-subject",
                "recorded-point",
                index == 12 ? "superseding-value" : $"priority-value-{index + 1}",
                $"synthetic source {index + 1}",
                now.AddMinutes(index),
                null,
                span.Id,
                EvidenceOriginClass.OriginalContemporaneousRecord,
                AssertionClass.AttributedAssertion,
                IntegrityState.OriginalHashVerified,
                [span.Id],
                0.90m + index / 1000m))
            .ToArray();
        var eventTime = new DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.Zero);
        var provider = new FixedExtractionProvider(new StructuredExtractionOutput(
            "{\"supersedes\":true}",
            new StructuredCandidateBatch(
                [],
                [],
                assertionCandidates,
                [new EventCandidate("event-1", "meeting", "Synthetic old event", eventTime, null,
                    [], [eventSpan.Id], 0.96m)],
                [new AssertionEventLinkCandidate("link-supersedes", "assertion-13", "event-1",
                    AssertionEventRelation.Supersedes, [linkSpan.Id], 0.93m)],
                [],
                [])), "supersedes-cap-v1");
        var allSources = assertionSpans.Select(span => span.Id)
            .Append(eventSpan.Id)
            .Append(linkSpan.Id)
            .ToArray();

        await service.ExtractAndMergeAsync(state, allSources, provider);
        var matterEvent = Assert.Single(graph.Events);

        using var projected = Project(graph, state);
        Assert.DoesNotContain(projected.RootElement.GetProperty("chronology").EnumerateArray(),
            item => item.GetProperty("Id").GetGuid() == matterEvent.Id);
        var points = projected.RootElement.GetProperty("evidencePoints").EnumerateArray().ToArray();
        Assert.Equal(13, points.Length);
        var supersedingPoint = Assert.Single(points,
            item => item.GetProperty("Value").GetString() == "superseding-value");
        Assert.Contains("superseding", supersedingPoint.GetProperty("epistemicNotice").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(assertionSpans[12].Id,
            supersedingPoint.GetProperty("sourceSpanIds").EnumerateArray().Select(item => item.GetGuid()));
        Assert.Contains(projected.RootElement.GetProperty("sourceSpans").EnumerateArray(),
            item => item.GetProperty("Id").GetGuid() == assertionSpans[12].Id);
    }

    private static StructuredExtractionOutput EmptyOutput(string marker) =>
        new($"{{\"{marker}\":true}}", new StructuredCandidateBatch([], [], [], [], [], [], []));

    private static JsonDocument Project(MatterEvidenceGraph graph, MatterBrainState state) =>
        JsonDocument.Parse(JsonSerializer.Serialize(MeetingPreparationProjection.Create(
            new PersistedMatterBrain(graph, new WorkplaceMatter(graph), state), false)));

    private static MatterEvidenceGraph CreateGraph(DateTimeOffset now) => new(new Matter(
        Guid.NewGuid(), new TenantId(Guid.NewGuid()), "workplace-dispute", "Synthetic post-final-review Matter", "active", now, now));

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
