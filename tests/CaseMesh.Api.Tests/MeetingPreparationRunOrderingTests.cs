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

        var initial = InitialProvider(span.Id, "fixed-clock-model-v1", "Alex Smith", "Alex Smyth");
        var replacement = EmptyProvider("fixed-clock-model-v2");

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
        var sourceSpanId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        const string initialModel = "fixed-clock-order-v1";

        var probe = CreateContext(matterId, sourceSpanId, now);
        var probeFirst = await probe.Service.ExtractAndMergeAsync(
            probe.Brain, [probe.SpanId], InitialProvider(probe.SpanId, initialModel, "Jordan Lee", "Jordon Lee"));
        string? replacementModel = null;
        for (var index = 2; index <= 10_000; index++)
        {
            var model = $"fixed-clock-order-v{index}";
            var probeRun = await probe.Service.ExtractAndMergeAsync(
                probe.Brain, [probe.SpanId], EmptyProvider(model));
            if (probeRun.Run.Id.CompareTo(probeFirst.Run.Id) < 0)
            {
                replacementModel = model;
                break;
            }
        }
        Assert.False(string.IsNullOrWhiteSpace(replacementModel));

        var subject = CreateContext(matterId, sourceSpanId, now);
        var first = await subject.Service.ExtractAndMergeAsync(
            subject.Brain, [subject.SpanId], InitialProvider(subject.SpanId, initialModel, "Jordan Lee", "Jordon Lee"));
        Assert.Contains(FactualGapAnalyzer.Analyze(subject.Graph, subject.Workplace, subject.Brain),
            item => item.Code == "entity-ambiguity");

        var second = await subject.Service.ExtractAndMergeAsync(
            subject.Brain, [subject.SpanId], EmptyProvider(replacementModel!));

        Assert.Equal(now, first.Run.GeneratedAt);
        Assert.Equal(now, second.Run.GeneratedAt);
        Assert.Equal(1L, first.Run.Sequence);
        Assert.Equal(2L, second.Run.Sequence);
        Assert.True(second.Run.Id.CompareTo(first.Run.Id) < 0,
            "The regression requires the later run GUID to sort before the earlier run GUID.");
        Assert.DoesNotContain(FactualGapAnalyzer.Analyze(subject.Graph, subject.Workplace, subject.Brain),
            item => item.Code == "entity-ambiguity");
    }

    private static TestContext CreateContext(Guid matterId, Guid sourceSpanId, DateTimeOffset now)
    {
        var graph = new MatterEvidenceGraph(new Matter(matterId, new TenantId(Guid.NewGuid()),
            "workplace-dispute", "Synthetic fixed-clock ordering Matter", "active", now, now));
        var version = graph.RegisterDocumentVersion(Guid.NewGuid(), Guid.NewGuid(), new string('F', 64), Guid.NewGuid());
        const string text = "Synthetic evidence for Jordan Lee and Jordon Lee.";
        var span = graph.AddSourceSpan(sourceSpanId, version, text, "synthetic-parser/1", 0.99m,
            pageNumber: 1, textStart: 0, textEnd: text.Length);
        var workplace = new WorkplaceMatter(graph);
        var brain = new MatterBrainState(graph);
        return new TestContext(graph, workplace, brain, new MatterBrainMergeService(new FixedTimeProvider(now)), span.Id);
    }

    private static FixedExtractionProvider InitialProvider(
        Guid spanId,
        string model,
        string firstName,
        string secondName) => new(
        new StructuredExtractionOutput(
            "{\"match\":true}", new StructuredCandidateBatch(
                [
                    new EntityCandidate("person-a", CanonicalEntityKind.Person, firstName, "person",
                        [firstName], ["Employee"], [spanId], 0.99m),
                    new EntityCandidate("person-b", CanonicalEntityKind.Person, secondName, "person",
                        [secondName], ["Manager"], [spanId], 0.98m)
                ],
                [], [], [], [],
                [new EntityMatchCandidate("possible-same-person", CanonicalEntityKind.Person,
                    "person-a", "person-b", 0.88m, [spanId], 0.90m)], [])), model);

    private static FixedExtractionProvider EmptyProvider(string model) => new(
        new StructuredExtractionOutput(
            "{\"match\":false}", new StructuredCandidateBatch([], [], [], [], [], [], [])), model);

    private sealed record TestContext(
        MatterEvidenceGraph Graph,
        WorkplaceMatter Workplace,
        MatterBrainState Brain,
        MatterBrainMergeService Service,
        Guid SpanId);

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
