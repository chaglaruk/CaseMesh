using System.Text.Json;
using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Core.Workplace;
using CaseMesh.MatterBrain;
using CaseMesh.Persistence.Postgres;

namespace CaseMesh.Api.Tests;

public sealed class MeetingPreparationAliasProvenanceTests
{
    [Fact]
    public async Task Preparation_derives_multi_source_alias_provenance_from_active_candidate_dependencies()
    {
        var now = new DateTimeOffset(2026, 5, 5, 9, 0, 0, TimeSpan.Zero);
        var graph = new MatterEvidenceGraph(new Matter(Guid.NewGuid(), new TenantId(Guid.NewGuid()),
            "workplace-dispute", "Synthetic alias Matter", "active", now, now));
        var first = AddSource(graph, 'A', "Alex Smith is also referred to as A. Smith in this record.");
        var second = AddSource(graph, 'B', "A second record also uses the name A. Smith for Alex Smith.");
        var brain = new MatterBrainState(graph);
        var loaded = new PersistedMatterBrain(graph, new WorkplaceMatter(graph), brain);
        var provider = new FixedExtractionProvider(new StructuredExtractionOutput(
            "{\"person\":true}", new StructuredCandidateBatch(
                [new EntityCandidate("person", CanonicalEntityKind.Person, "Alex Smith", "person",
                    ["A. Smith"], ["Employee"], [first.Id, second.Id], 0.99m)],
                [], [], [], [], [], [])));

        await new MatterBrainMergeService(TimeProvider.System)
            .ExtractAndMergeAsync(brain, [first.Id, second.Id], provider);

        var storedAlias = Assert.Single(brain.Aliases, item => item.Value == "A. Smith");
        Assert.Null(storedAlias.SourceSpanId);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(MeetingPreparationProjection.Create(loaded, false)));
        var participant = Assert.Single(json.RootElement.GetProperty("participants").EnumerateArray());
        var alias = Assert.Single(participant.GetProperty("identityAliases").EnumerateArray(),
            item => item.GetProperty("Value").GetString() == "A. Smith");

        Assert.Equal("SourceBackedExtraction", alias.GetProperty("provenanceStatus").GetString());
        Assert.Equal(new[] { first.Id, second.Id }.Order().ToArray(),
            alias.GetProperty("sourceSpanIds").EnumerateArray().Select(item => item.GetGuid()).ToArray());
        Assert.Equal(new[] { first.DocumentVersion.DocumentVersionId, second.DocumentVersion.DocumentVersionId }.Order().ToArray(),
            alias.GetProperty("documentVersionIds").EnumerateArray().Select(item => item.GetGuid()).ToArray());
        var projectedSources = json.RootElement.GetProperty("sourceSpans").EnumerateArray()
            .Select(item => item.GetProperty("Id").GetGuid()).ToArray();
        Assert.Contains(first.Id, projectedSources);
        Assert.Contains(second.Id, projectedSources);
    }

    private static SourceSpan AddSource(MatterEvidenceGraph graph, char hash, string text)
    {
        var version = graph.RegisterDocumentVersion(Guid.NewGuid(), Guid.NewGuid(), new string(hash, 64), Guid.NewGuid());
        return graph.AddSourceSpan(Guid.NewGuid(), version, text, "synthetic-parser/1", 0.99m,
            pageNumber: 1, textStart: 0, textEnd: text.Length);
    }

    private sealed class FixedExtractionProvider(StructuredExtractionOutput output) : IStructuredExtractionProvider
    {
        public StructuredExtractionProviderDescriptor Descriptor { get; } =
            new("synthetic-provider", "alias-model", "1", "1", "1");

        public Task<StructuredExtractionOutput> ExtractAsync(
            StructuredExtractionInput input,
            CancellationToken cancellationToken = default) => Task.FromResult(output);
    }
}
