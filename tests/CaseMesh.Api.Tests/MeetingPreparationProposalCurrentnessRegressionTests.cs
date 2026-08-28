using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Core.Workplace;
using CaseMesh.MatterBrain;
using CaseMesh.Qa;

namespace CaseMesh.Api.Tests;

public sealed class MeetingPreparationProposalCurrentnessRegressionTests
{
    [Fact]
    public async Task Multi_source_entity_match_survives_partial_reextraction_and_retires_after_full_reextraction()
    {
        var now = new DateTimeOffset(2026, 6, 3, 9, 0, 0, TimeSpan.Zero);
        var context = CreateContext(now);
        await context.Service.ExtractAndMergeAsync(context.Brain, [context.First.Id, context.Second.Id],
            InitialProvider(context.First.Id, context.Second.Id, "multi-source-v1"));
        Assert.Contains(FactualGapAnalyzer.Analyze(context.Graph, context.Workplace, context.Brain),
            item => item.Code == "entity-ambiguity");

        await context.Service.ExtractAndMergeAsync(context.Brain, [context.First.Id], EmptyProvider("partial-v2"));
        Assert.Contains(FactualGapAnalyzer.Analyze(context.Graph, context.Workplace, context.Brain),
            item => item.Code == "entity-ambiguity");

        await context.Service.ExtractAndMergeAsync(context.Brain, [context.First.Id, context.Second.Id], EmptyProvider("full-v3"));
        Assert.DoesNotContain(FactualGapAnalyzer.Analyze(context.Graph, context.Workplace, context.Brain),
            item => item.Code == "entity-ambiguity");
    }

    [Fact]
    public async Task Equal_time_legacy_runs_without_sequence_keep_ambiguity_and_explain_unknown_order()
    {
        var now = new DateTimeOffset(2026, 6, 3, 10, 0, 0, TimeSpan.Zero);
        var context = CreateContext(now);
        await context.Service.ExtractAndMergeAsync(context.Brain, [context.First.Id, context.Second.Id],
            InitialProvider(context.First.Id, context.Second.Id, "legacy-v1"));
        await context.Service.ExtractAndMergeAsync(context.Brain, [context.First.Id, context.Second.Id],
            EmptyProvider("legacy-v2"));
        Assert.DoesNotContain(FactualGapAnalyzer.Analyze(context.Graph, context.Workplace, context.Brain),
            item => item.Code == "entity-ambiguity");

        var snapshot = context.Brain.CaptureSnapshot();
        var legacy = MatterBrainState.Rehydrate(context.Graph, snapshot with
        {
            Runs = snapshot.Runs.Select(run => run with { Sequence = null }).ToArray()
        });

        var ambiguity = Assert.Single(FactualGapAnalyzer.Analyze(context.Graph, context.Workplace, legacy),
            item => item.Code == "entity-ambiguity");
        Assert.Contains("legacy extraction runs share the same timestamp", ambiguity.Summary,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no truthful execution sequence", ambiguity.Summary,
            StringComparison.OrdinalIgnoreCase);
    }

    private static TestContext CreateContext(DateTimeOffset now)
    {
        var graph = new MatterEvidenceGraph(new Matter(Guid.NewGuid(), new TenantId(Guid.NewGuid()),
            "workplace-dispute", "Synthetic proposal-currentness Matter", "active", now, now));
        var firstVersion = graph.RegisterDocumentVersion(Guid.NewGuid(), Guid.NewGuid(), new string('A', 64), Guid.NewGuid());
        var secondVersion = graph.RegisterDocumentVersion(Guid.NewGuid(), Guid.NewGuid(), new string('B', 64), Guid.NewGuid());
        const string firstText = "Synthetic first source names Jordan Lee.";
        const string secondText = "Synthetic second source names Jordon Lee.";
        var first = graph.AddSourceSpan(Guid.NewGuid(), firstVersion, firstText, "synthetic-parser/1", 0.99m,
            pageNumber: 1, textStart: 0, textEnd: firstText.Length);
        var second = graph.AddSourceSpan(Guid.NewGuid(), secondVersion, secondText, "synthetic-parser/1", 0.98m,
            pageNumber: 1, textStart: 0, textEnd: secondText.Length);
        var workplace = new WorkplaceMatter(graph);
        var brain = new MatterBrainState(graph);
        return new TestContext(graph, workplace, brain, new MatterBrainMergeService(new FixedTimeProvider(now)), first, second);
    }

    private static FixedExtractionProvider InitialProvider(Guid firstSpanId, Guid secondSpanId, string model) => new(
        new StructuredExtractionOutput(
            "{\"match\":true}", new StructuredCandidateBatch(
                [
                    new EntityCandidate("person-a", CanonicalEntityKind.Person, "Jordan Lee", "person",
                        ["Jordan Lee"], ["employee"], [firstSpanId], 0.99m),
                    new EntityCandidate("person-b", CanonicalEntityKind.Person, "Jordon Lee", "person",
                        ["Jordon Lee"], ["manager"], [secondSpanId], 0.98m)
                ],
                [], [], [], [],
                [new EntityMatchCandidate("possible-same-person", CanonicalEntityKind.Person,
                    "person-a", "person-b", 0.88m, [firstSpanId, secondSpanId], 0.90m)], [])), model);

    private static FixedExtractionProvider EmptyProvider(string model) => new(
        new StructuredExtractionOutput(
            "{\"match\":false}", new StructuredCandidateBatch([], [], [], [], [], [], [])), model);

    private sealed record TestContext(
        MatterEvidenceGraph Graph,
        WorkplaceMatter Workplace,
        MatterBrainState Brain,
        MatterBrainMergeService Service,
        SourceSpan First,
        SourceSpan Second);

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
