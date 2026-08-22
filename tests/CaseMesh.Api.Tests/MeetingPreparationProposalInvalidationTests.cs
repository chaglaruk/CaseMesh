using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Core.Workplace;
using CaseMesh.MatterBrain;
using CaseMesh.Qa;

namespace CaseMesh.Api.Tests;

public sealed class MeetingPreparationProposalInvalidationTests
{
    [Fact]
    public async Task Audit_correction_does_not_retire_unrelated_entity_ambiguity_from_same_extraction_run()
    {
        var now = new DateTimeOffset(2026, 5, 2, 9, 0, 0, TimeSpan.Zero);
        var graph = new MatterEvidenceGraph(new Matter(Guid.NewGuid(), new TenantId(Guid.NewGuid()),
            "workplace-dispute", "Synthetic Matter", "active", now, now));
        var version = graph.RegisterDocumentVersion(Guid.NewGuid(), Guid.NewGuid(), new string('D', 64), Guid.NewGuid());
        const string sourceText = "Synthetic source supports an assertion and a possible person match.";
        var span = graph.AddSourceSpan(Guid.NewGuid(), version, sourceText, "synthetic-parser/1", 0.99m,
            pageNumber: 1, textStart: 0, textEnd: sourceText.Length);
        var workplace = new WorkplaceMatter(graph);
        var brain = new MatterBrainState(graph);
        var provider = new FixedExtractionProvider(new StructuredExtractionOutput(
            "{\"sharedRun\":true}",
            new StructuredCandidateBatch(
                [
                    new EntityCandidate("person-a", CanonicalEntityKind.Person, "Alex Smith", "person",
                        ["Alex Smith"], ["Employee"], [span.Id], 0.99m),
                    new EntityCandidate("person-b", CanonicalEntityKind.Person, "Alex Smyth", "person",
                        ["Alex Smyth"], ["Manager"], [span.Id], 0.98m)
                ],
                [],
                [new AssertionCandidate("shared-assertion", "synthetic-employee", "shared-run-value", "original",
                    "Synthetic source", now, now, span.Id,
                    EvidenceOriginClass.OriginalContemporaneousRecord, AssertionClass.AttributedAssertion,
                    IntegrityState.OriginalHashVerified, [span.Id], 0.95m)],
                [], [],
                [new EntityMatchCandidate("possible-same-person", CanonicalEntityKind.Person,
                    "person-a", "person-b", 0.88m, [span.Id], 0.90m)],
                [])));

        await new MatterBrainMergeService(TimeProvider.System)
            .ExtractAndMergeAsync(brain, [span.Id], provider);
        var assertion = graph.Assertions.Single(item => item.Predicate == "shared-run-value");
        Assert.Contains(brain.EntityResolutionActions,
            item => item.Kind == EntityResolutionActionKind.Proposed && item.Actor == "structured-extraction");

        brain.CorrectAssertion(assertion.Id, Guid.NewGuid(), "corrected", assertion.EventTime,
            Guid.NewGuid(), "synthetic-reviewer", now.AddHours(1));

        Assert.Contains(brain.DependencyInvalidations,
            item => item.InvalidatedByAuditEventId.HasValue && !item.InvalidatedByRunId.HasValue);
        var gaps = FactualGapAnalyzer.Analyze(graph, workplace, brain);
        Assert.Contains(gaps, item => item.Code == "entity-ambiguity");
    }

    [Fact]
    public async Task Reextracting_entity_match_only_source_retires_old_ambiguity_without_dependency_invalidation()
    {
        var now = new DateTimeOffset(2026, 5, 3, 9, 0, 0, TimeSpan.Zero);
        var graph = new MatterEvidenceGraph(new Matter(Guid.NewGuid(), new TenantId(Guid.NewGuid()),
            "workplace-dispute", "Synthetic Matter", "active", now, now));
        var identityVersion = graph.RegisterDocumentVersion(Guid.NewGuid(), Guid.NewGuid(), new string('E', 64), Guid.NewGuid());
        const string identityText = "Synthetic identity evidence for Alex Smith and Alex Smyth.";
        var identitySpan = graph.AddSourceSpan(Guid.NewGuid(), identityVersion, identityText, "synthetic-parser/1", 0.99m,
            pageNumber: 1, textStart: 0, textEnd: identityText.Length);
        var matchVersion = graph.RegisterDocumentVersion(Guid.NewGuid(), Guid.NewGuid(), new string('F', 64), Guid.NewGuid());
        const string matchText = "Synthetic contextual span suggesting the two names may refer to one person.";
        var matchSpan = graph.AddSourceSpan(Guid.NewGuid(), matchVersion, matchText, "synthetic-parser/1", 0.98m,
            pageNumber: 1, textStart: 0, textEnd: matchText.Length);
        var workplace = new WorkplaceMatter(graph);
        var brain = new MatterBrainState(graph);
        var time = new SteppingTimeProvider(now);
        var service = new MatterBrainMergeService(time);
        var initial = new FixedExtractionProvider(new StructuredExtractionOutput(
            "{\"matchOnlySource\":true}",
            new StructuredCandidateBatch(
                [
                    new EntityCandidate("person-a", CanonicalEntityKind.Person, "Alex Smith", "person",
                        ["Alex Smith"], ["Employee"], [identitySpan.Id], 0.99m),
                    new EntityCandidate("person-b", CanonicalEntityKind.Person, "Alex Smyth", "person",
                        ["Alex Smyth"], ["Manager"], [identitySpan.Id], 0.98m)
                ],
                [], [], [], [],
                [new EntityMatchCandidate("possible-same-person", CanonicalEntityKind.Person,
                    "person-a", "person-b", 0.88m, [matchSpan.Id], 0.90m)],
                [])), "entity-match-model-v1");

        await service.ExtractAndMergeAsync(brain, [identitySpan.Id, matchSpan.Id], initial);
        Assert.DoesNotContain(brain.Dependencies, dependency => dependency.SourceSpanId == matchSpan.Id);
        Assert.Contains(FactualGapAnalyzer.Analyze(graph, workplace, brain), item => item.Code == "entity-ambiguity");

        var replacement = new FixedExtractionProvider(new StructuredExtractionOutput(
            "{\"matchRemoved\":true}", new StructuredCandidateBatch([], [], [], [], [], [], [])),
            "entity-match-model-v2");
        await service.ExtractAndMergeAsync(brain, [matchSpan.Id], replacement);

        Assert.DoesNotContain(brain.DependencyInvalidations,
            invalidation => brain.Dependencies.Any(dependency =>
                dependency.Id == invalidation.DependencyId && dependency.SourceSpanId == matchSpan.Id));
        Assert.DoesNotContain(FactualGapAnalyzer.Analyze(graph, workplace, brain), item => item.Code == "entity-ambiguity");
    }

    private sealed class FixedExtractionProvider(StructuredExtractionOutput output, string model = "shared-run-model")
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
