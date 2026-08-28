using System.Text.Json;
using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Core.Workplace;
using CaseMesh.MatterBrain;
using CaseMesh.Persistence.Postgres;

namespace CaseMesh.Api.Tests;

public sealed class MeetingPreparationAcceptedMergeRegressionTests
{
    [Fact]
    public async Task Preparation_does_not_resurrect_inactive_merge_target_after_target_reextraction()
    {
        var loaded = CreateSyntheticMatter(out var sourceSpan, out var targetSpan);
        var initial = PeopleProvider(sourceSpan, targetSpan, "participant-model-v1");
        var mergeService = new MatterBrainMergeService(TimeProvider.System);

        await mergeService.ExtractAndMergeAsync(loaded.Brain, [sourceSpan.Id, targetSpan.Id], initial);
        var source = loaded.Brain.People.Single(item => item.DisplayName == "Alex Source");
        var target = loaded.Brain.People.Single(item => item.DisplayName == "Taylor Target");
        var proposal = loaded.Brain.ProposeEntityMerge(Guid.NewGuid(), CanonicalEntityKind.Person,
            source.Id, target.Id, [sourceSpan.Id, targetSpan.Id], 0.95m,
            "synthetic-reviewer", DateTimeOffset.UtcNow);
        loaded.Brain.AcceptEntityMerge(Guid.NewGuid(), proposal.Id,
            "synthetic-reviewer", DateTimeOffset.UtcNow.AddMinutes(1));

        var replacement = new FixedExtractionProvider(new StructuredExtractionOutput(
            "{\"people\":[]}", new StructuredCandidateBatch([], [], [], [], [], [], [])),
            "participant-model-v2");
        await mergeService.ExtractAndMergeAsync(loaded.Brain, [targetSpan.Id], replacement);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(MeetingPreparationProjection.Create(loaded, false)));
        var participant = Assert.Single(json.RootElement.GetProperty("participants").EnumerateArray());

        Assert.Equal(source.Id, participant.GetProperty("Id").GetGuid());
        Assert.Equal("Alex Source", participant.GetProperty("DisplayName").GetString());
        Assert.Contains("Employee", participant.GetProperty("RoleLabels").EnumerateArray()
            .Select(item => item.GetString()));
        Assert.DoesNotContain("Manager", participant.GetProperty("RoleLabels").EnumerateArray()
            .Select(item => item.GetString()));
        Assert.Contains(source.Id, participant.GetProperty("mergedIdentityIds").EnumerateArray()
            .Select(item => item.GetGuid()));
        Assert.DoesNotContain(target.Id, participant.GetProperty("mergedIdentityIds").EnumerateArray()
            .Select(item => item.GetGuid()));
        Assert.Contains(sourceSpan.Id, participant.GetProperty("sourceSpanIds").EnumerateArray()
            .Select(item => item.GetGuid()));
        Assert.DoesNotContain(targetSpan.Id, participant.GetProperty("sourceSpanIds").EnumerateArray()
            .Select(item => item.GetGuid()));

        var member = Assert.Single(participant.GetProperty("identityMembers").EnumerateArray());
        Assert.Equal(source.Id, member.GetProperty("Id").GetGuid());
        Assert.Contains("Employee", member.GetProperty("RoleLabels").EnumerateArray()
            .Select(item => item.GetString()));
        Assert.Contains(sourceSpan.Id, member.GetProperty("sourceSpanIds").EnumerateArray()
            .Select(item => item.GetGuid()));
    }

    [Fact]
    public async Task Preparation_preserves_each_current_merged_identity_role_and_exact_provenance()
    {
        var loaded = CreateSyntheticMatter(out var sourceSpan, out var targetSpan);
        var mergeService = new MatterBrainMergeService(TimeProvider.System);

        await mergeService.ExtractAndMergeAsync(loaded.Brain, [sourceSpan.Id, targetSpan.Id],
            PeopleProvider(sourceSpan, targetSpan, "participant-model-v1"));
        var source = loaded.Brain.People.Single(item => item.DisplayName == "Alex Source");
        var target = loaded.Brain.People.Single(item => item.DisplayName == "Taylor Target");
        var proposal = loaded.Brain.ProposeEntityMerge(Guid.NewGuid(), CanonicalEntityKind.Person,
            source.Id, target.Id, [sourceSpan.Id, targetSpan.Id], 0.95m,
            "synthetic-reviewer", DateTimeOffset.UtcNow);
        loaded.Brain.AcceptEntityMerge(Guid.NewGuid(), proposal.Id,
            "synthetic-reviewer", DateTimeOffset.UtcNow.AddMinutes(1));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(MeetingPreparationProjection.Create(loaded, false)));
        var participant = Assert.Single(json.RootElement.GetProperty("participants").EnumerateArray());
        var members = participant.GetProperty("identityMembers").EnumerateArray().ToArray();
        Assert.Equal(2, members.Length);

        var sourceMember = Assert.Single(members, item => item.GetProperty("Id").GetGuid() == source.Id);
        Assert.Contains("Employee", sourceMember.GetProperty("RoleLabels").EnumerateArray()
            .Select(item => item.GetString()));
        Assert.DoesNotContain("Manager", sourceMember.GetProperty("RoleLabels").EnumerateArray()
            .Select(item => item.GetString()));
        Assert.Equal("SourceBackedExtraction", sourceMember.GetProperty("provenanceStatus").GetString());
        Assert.Equal([sourceSpan.Id], sourceMember.GetProperty("sourceSpanIds").EnumerateArray()
            .Select(item => item.GetGuid()).ToArray());
        Assert.Equal([sourceSpan.DocumentVersion.DocumentVersionId],
            sourceMember.GetProperty("documentVersionIds").EnumerateArray()
                .Select(item => item.GetGuid()).ToArray());

        var targetMember = Assert.Single(members, item => item.GetProperty("Id").GetGuid() == target.Id);
        Assert.Contains("Manager", targetMember.GetProperty("RoleLabels").EnumerateArray()
            .Select(item => item.GetString()));
        Assert.DoesNotContain("Employee", targetMember.GetProperty("RoleLabels").EnumerateArray()
            .Select(item => item.GetString()));
        Assert.Equal("SourceBackedExtraction", targetMember.GetProperty("provenanceStatus").GetString());
        Assert.Equal([targetSpan.Id], targetMember.GetProperty("sourceSpanIds").EnumerateArray()
            .Select(item => item.GetGuid()).ToArray());
        Assert.Equal([targetSpan.DocumentVersion.DocumentVersionId],
            targetMember.GetProperty("documentVersionIds").EnumerateArray()
                .Select(item => item.GetGuid()).ToArray());
    }

    private static FixedExtractionProvider PeopleProvider(
        SourceSpan sourceSpan,
        SourceSpan targetSpan,
        string model) => new(new StructuredExtractionOutput(
            "{\"people\":true}", new StructuredCandidateBatch(
                [
                    new EntityCandidate("person-source", CanonicalEntityKind.Person, "Alex Source", "person",
                        ["Alex Source"], ["Employee"], [sourceSpan.Id], 0.99m),
                    new EntityCandidate("person-target", CanonicalEntityKind.Person, "Taylor Target", "person",
                        ["Taylor Target"], ["Manager"], [targetSpan.Id], 0.99m)
                ],
                [], [], [], [], [], [])), model);

    private static PersistedMatterBrain CreateSyntheticMatter(
        out SourceSpan sourceSpan,
        out SourceSpan targetSpan)
    {
        var now = new DateTimeOffset(2026, 5, 2, 9, 0, 0, TimeSpan.Zero);
        var graph = new MatterEvidenceGraph(new Matter(Guid.NewGuid(), new TenantId(Guid.NewGuid()),
            "workplace-dispute", "Synthetic accepted merge Matter", "active", now, now));
        sourceSpan = AddSource(graph, 'D', "Alex Source is recorded as Employee.");
        targetSpan = AddSource(graph, 'E', "Taylor Target is recorded as Manager.");
        return new PersistedMatterBrain(graph, new WorkplaceMatter(graph), new MatterBrainState(graph));
    }

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
}
