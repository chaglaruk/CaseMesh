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

    private sealed class FixedExtractionProvider(StructuredExtractionOutput output) : IStructuredExtractionProvider
    {
        public StructuredExtractionProviderDescriptor Descriptor { get; } =
            new("synthetic-provider", "shared-run-model", "1", "1", "1");

        public Task<StructuredExtractionOutput> ExtractAsync(
            StructuredExtractionInput input,
            CancellationToken cancellationToken = default) => Task.FromResult(output);
    }
}
