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
        Assert.Equal(now, proposal.OccurredAt);

        var accepted = brain.AcceptEntityMerge(Guid.NewGuid(), proposal.Id, "synthetic-reviewer", now);
        Assert.Equal(now, accepted.OccurredAt);
        Assert.DoesNotContain(FactualGapAnalyzer.Analyze(graph, workplace, brain), item => item.Code == "entity-ambiguity");

        var second = await service.ExtractAndMergeAsync(brain, [span.Id], replacement);
        Assert.Equal(now, second.Run.GeneratedAt);
        Assert.All(brain.Runs, run => Assert.Equal(now, run.GeneratedAt));
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
