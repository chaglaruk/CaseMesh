using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Core.Workplace;
using CaseMesh.MatterBrain;
using CaseMesh.Qa;

namespace CaseMesh.Api.Tests;

public sealed class MeetingPreparationRunOrderingTests
{
    [Fact]
    public async Task Fixed_clock_keeps_truthful_run_time_and_allows_immediate_entity_decision()
    {
        var now = new DateTimeOffset(2026, 5, 4, 9, 0, 0, TimeSpan.Zero);
        var graph = new MatterEvidenceGraph(new Matter(Guid.NewGuid(), new TenantId(Guid.NewGuid()),
            "workplace-dispute", "Synthetic fixed-clock Matter", "active", now, now));
        var version = graph.RegisterDocumentVersion(Guid.NewGuid(), Guid.NewGuid(), new string('E', 64), Guid.NewGuid());
        const string text = "Synthetic evidence for Alex Smith and Alex Smyth.";
        var span = graph.AddSourceSpan(Guid.NewGuid(), version, text, "synthetic-parser/1", 0.99m,
            pageNumber: 1, textStart: 0, textEnd: text.Length);
        var workplace = new WorkplaceMatter(graph);
        var brain = new MatterBrainState(graph);
        var service = new MatterBrainMergeService(new FixedTimeProvider(now));

        var initial = new FixedExtractionProvider(new StructuredExtractionOutput(
            "{\"match\":true}", new StructuredCandidateBatch(
                [
                    new EntityCandidate("person-a", CanonicalEntityKind.Person, "Alex Smith", "person",
                        ["Alex Smith"], ["Employee"], [span.Id], 0.99m),
                    new EntityCandidate("person-b", CanonicalEntityKind.Person, "Alex Smyth", "person",
                        ["Alex Smyth"], ["Manager"], [span.Id], 0.98m)
                ],
                [], [], [], [],
                [new EntityMatchCandidate("possible-same-person", CanonicalEntityKind.Person,
                    "person-a", "person-b", 0.88m, [span.Id], 0.90m)], [])), "fixed-clock-model-v1");
        var replacement = new FixedExtractionProvider(new StructuredExtractionOutput(
            "{\"match\":false}", new StructuredCandidateBatch([], [], [], [], [], [], [])),
            "fixed-clock-model-v2");

        var first = await service.ExtractAndMergeAsync(brain, [span.Id], initial);
        var proposal = Assert.Single(brain.EntityResolutionActions,
            item => item.Kind == EntityResolutionActionKind.Proposed);
        Assert.Equal(now, first.Run.GeneratedAt);
        Assert.Equal(1L, first.Run.Sequence);
        Assert.Equal(now, proposal.OccurredAt);

        var accepted = brain.AcceptEntityMerge(Guid.NewGuid(), proposal.Id, "synthetic-reviewer", now);
        Assert.Equal(now, accepted.OccurredAt);
        Assert.DoesNotContain(FactualGapAnalyzer.Analyze(graph, workplace, brain), item => item.Code == "entity-ambiguity");

        var second = await service.ExtractAndMergeAsync(brain, [span.Id], replacement);
        Assert.Equal(now, second.Run.GeneratedAt);
        Assert.Equal(2L, second.Run.Sequence);
        Assert.All(brain.Runs, run => Assert.Equal(now, run.GeneratedAt));
    }

    [Fact]
    public async Task Fixed_clock_later_reextraction_retires_unresolved_proposal_even_when_its_guid_sorts_first()
    {
        var now = new DateTimeOffset(2026, 5, 4, 10, 0, 0, TimeSpan.Zero);
        var matterId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var graph = new MatterEvidenceGraph(new Matter(matterId, new TenantId(Guid.NewGuid()),
            "workplace-dispute", "Synthetic fixed-clock ordering Matter", "active", now, now));
        var version = graph.RegisterDocumentVersion(Guid.NewGuid(), Guid.NewGuid(), new string('F', 64), Guid.NewGuid());
        const string text = "Synthetic evidence for Jordan Lee and Jordon Lee.";
        var span = graph.AddSourceSpan(Guid.Parse("11111111-2222-3333-4444-555555555555"), version, text,
            "synthetic-parser/1", 0.99m, pageNumber: 1, textStart: 0, textEnd: text.Length);
        var workplace = new WorkplaceMatter(graph);
        var brain = new MatterBrainState(graph);
        var service = new MatterBrainMergeService(new FixedTimeProvider(now));
        const string initialModel = "fixed-clock-order-v1";
        var initialDescriptor = new StructuredExtractionProviderDescriptor("synthetic-provider", initialModel, "1", "1", "1");
        var firstFingerprint = MatterBrainIntegrity.Fingerprint(initialDescriptor, [span.Id]);
        var firstRunId = MatterBrainState.DeterministicId("extraction-run", matterId, firstFingerprint);
        var replacementModel = Enumerable.Range(2, 10_000)
            .Select(index => $"fixed-clock-order-v{index}")
            .First(model =>
            {
                var descriptor = new StructuredExtractionProviderDescriptor("synthetic-provider", model, "1", "1", "1");
                var fingerprint = MatterBrainIntegrity.Fingerprint(descriptor, [span.Id]);
                var runId = MatterBrainState.DeterministicId("extraction-run", matterId, fingerprint);
                return runId.CompareTo(firstRunId) < 0;
            });

        var initial = new FixedExtractionProvider(new StructuredExtractionOutput(
            "{\"match\":true}", new StructuredCandidateBatch(
                [
                    new EntityCandidate("person-a", CanonicalEntityKind.Person, "Jordan Lee", "person",
                        ["Jordan Lee"], ["Employee"], [span.Id], 0.99m),
                    new EntityCandidate("person-b", CanonicalEntityKind.Person, "Jordon Lee", "person",
                        ["Jordon Lee"], ["Manager"], [span.Id], 0.98m)
                ],
                [], [], [], [],
                [new EntityMatchCandidate("possible-same-person", CanonicalEntityKind.Person,
                    "person-a", "person-b", 0.88m, [span.Id], 0.90m)], [])), initialModel);
        var replacement = new FixedExtractionProvider(new StructuredExtractionOutput(
            "{\"match\":false}", new StructuredCandidateBatch([], [], [], [], [], [], [])), replacementModel);

        var first = await service.ExtractAndMergeAsync(brain, [span.Id], initial);
        Assert.Contains(FactualGapAnalyzer.Analyze(graph, workplace, brain), item => item.Code == "entity-ambiguity");

        var second = await service.ExtractAndMergeAsync(brain, [span.Id], replacement);

        Assert.Equal(now, first.Run.GeneratedAt);
        Assert.Equal(now, second.Run.GeneratedAt);
        Assert.Equal(1L, first.Run.Sequence);
        Assert.Equal(2L, second.Run.Sequence);
        Assert.True(second.Run.Id.CompareTo(first.Run.Id) < 0,
            "The regression requires the later run GUID to sort before the earlier run GUID.");
        Assert.DoesNotContain(FactualGapAnalyzer.Analyze(graph, workplace, brain), item => item.Code == "entity-ambiguity");
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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
