using System.Text.Json;
using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Core.Workplace;
using CaseMesh.MatterBrain;
using CaseMesh.Persistence.Postgres;

namespace CaseMesh.Api.Tests;

public sealed class MeetingPreparationProjectionTests
{
    [Fact]
    public void Preparation_is_deterministic_and_projects_only_referenced_source_spans()
    {
        var loaded = CreateSyntheticMatter(out var firstSpan, out var secondSpan, out var unrelatedSpan);

        var first = JsonSerializer.Serialize(MeetingPreparationProjection.Create(loaded, false));
        var second = JsonSerializer.Serialize(MeetingPreparationProjection.Create(loaded, false));

        Assert.Equal(first, second);
        using var json = JsonDocument.Parse(first);
        Assert.False(json.RootElement.GetProperty("processing").GetBoolean());
        Assert.Contains("canonical Matter evidence state",
            json.RootElement.GetProperty("currentnessNotice").GetString(), StringComparison.OrdinalIgnoreCase);

        var spans = json.RootElement.GetProperty("sourceSpans").EnumerateArray()
            .Select(item => item.GetProperty("Id").GetGuid()).ToArray();
        Assert.Contains(firstSpan.Id, spans);
        Assert.Contains(secondSpan.Id, spans);
        Assert.DoesNotContain(unrelatedSpan.Id, spans);

        var dispute = Assert.Single(json.RootElement.GetProperty("unresolvedDisputes").EnumerateArray());
        Assert.Equal("Unresolved", dispute.GetProperty("resolutionState").GetString());
        Assert.Equal(2, dispute.GetProperty("sourceSpanIds").GetArrayLength());
        Assert.Contains(json.RootElement.GetProperty("questionsToClarify").EnumerateArray(), item =>
            item.GetProperty("Code").GetString() == "unresolved-contradiction");
    }

    [Fact]
    public void Preparation_never_promotes_ai_inference_or_rejected_evidence_into_priority_points()
    {
        var loaded = CreateSyntheticMatter(out var firstSpan, out _, out _);
        loaded.Evidence.AddAssertion(Guid.NewGuid(), "synthetic-employee", "predicted-outcome", "win",
            "synthetic model", DateTimeOffset.UtcNow,
            EvidenceOriginClass.AiGeneratedInference, AssertionClass.AiInference,
            DisputeState.Unverified, IntegrityState.DerivedCopy, VerificationState.NotReviewed,
            createdByModel: "synthetic-model/1");
        var rejected = loaded.Evidence.AddAssertion(Guid.NewGuid(), "synthetic-employee", "rejected-value", "rejected",
            "synthetic source", DateTimeOffset.UtcNow,
            EvidenceOriginClass.OriginalContemporaneousRecord, AssertionClass.AttributedAssertion,
            DisputeState.Unverified, IntegrityState.OriginalHashVerified, VerificationState.Rejected,
            firstSpan.Id, extractionConfidence: 0.99m);
        var current = loaded.Evidence.Assertions.First(item => item.VerificationState != VerificationState.Rejected);
        loaded.Evidence.AddContradiction(Guid.NewGuid(), current.Id, rejected.Id,
            ContradictionType.DirectConflict, "synthetic-rejected-rule", DateTimeOffset.UtcNow);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(MeetingPreparationProjection.Create(loaded, true)));
        var text = json.RootElement.GetProperty("evidencePoints").ToString();

        Assert.DoesNotContain("predicted-outcome", text, StringComparison.Ordinal);
        Assert.DoesNotContain("rejected-value", text, StringComparison.Ordinal);
        Assert.Contains("still active", json.RootElement.GetProperty("currentnessNotice").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not legal advice", json.RootElement.GetProperty("notices")[0].GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(json.RootElement.GetProperty("unresolvedDisputes").EnumerateArray()
                .SelectMany(item => item.GetProperty("assertions").EnumerateArray()),
            assertion => assertion.GetProperty("Id").GetGuid() == rejected.Id &&
                         assertion.GetProperty("rejected").GetBoolean() &&
                         !assertion.GetProperty("current").GetBoolean());
    }

    [Fact]
    public async Task Preparation_projects_active_participant_provenance_and_excludes_completed_entity_proposals()
    {
        var loaded = CreateSyntheticMatter(out var firstSpan, out _, out _);
        var brain = loaded.Brain;
        var provider = new FixedExtractionProvider(new StructuredExtractionOutput(
            "{\"synthetic\":true}",
            new StructuredCandidateBatch(
                [
                    new EntityCandidate("person-a", CanonicalEntityKind.Person, "Alex Smith", "person",
                        ["Alex Smith"], ["Employee"], [firstSpan.Id], 0.99m),
                    new EntityCandidate("person-b", CanonicalEntityKind.Person, "Alex Smyth", "person",
                        ["Alex Smyth"], ["Manager"], [firstSpan.Id], 0.98m)
                ],
                [], [], [], [], [], [])));

        await new MatterBrainMergeService(TimeProvider.System)
            .ExtractAndMergeAsync(brain, [firstSpan.Id], provider);
        var people = brain.People.OrderBy(item => item.DisplayName, StringComparer.Ordinal).ToArray();
        Assert.Equal(2, people.Length);
        var proposal = brain.ProposeEntityMerge(Guid.NewGuid(), CanonicalEntityKind.Person,
            people[0].Id, people[1].Id, [firstSpan.Id], 0.90m, "synthetic-reviewer", DateTimeOffset.UtcNow);
        brain.AcceptEntityMerge(Guid.NewGuid(), proposal.Id, "synthetic-reviewer", DateTimeOffset.UtcNow.AddMinutes(1));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(MeetingPreparationProjection.Create(loaded, false)));
        var participants = json.RootElement.GetProperty("participants").EnumerateArray().ToArray();
        Assert.Equal(2, participants.Length);
        Assert.All(participants, participant =>
        {
            Assert.Equal("SourceBackedExtraction", participant.GetProperty("provenanceStatus").GetString());
            Assert.Contains(firstSpan.Id,
                participant.GetProperty("sourceSpanIds").EnumerateArray().Select(item => item.GetGuid()));
            Assert.Contains(firstSpan.DocumentVersion.DocumentVersionId,
                participant.GetProperty("documentVersionIds").EnumerateArray().Select(item => item.GetGuid()));
        });
        Assert.DoesNotContain(json.RootElement.GetProperty("questionsToClarify").EnumerateArray(), item =>
            item.GetProperty("Code").GetString() == "entity-ambiguity");
        Assert.Contains(firstSpan.Id, json.RootElement.GetProperty("sourceSpans").EnumerateArray()
            .Select(item => item.GetProperty("Id").GetGuid()));
    }

    private static PersistedMatterBrain CreateSyntheticMatter(
        out SourceSpan firstSpan,
        out SourceSpan secondSpan,
        out SourceSpan unrelatedSpan)
    {
        var now = new DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.Zero);
        var graph = new MatterEvidenceGraph(new Matter(Guid.NewGuid(), new TenantId(Guid.NewGuid()),
            "workplace-dispute", "Synthetic Matter", "active", now, now));
        firstSpan = AddSource(graph, 'A', "Employer states twelve absence days.");
        secondSpan = AddSource(graph, 'B', "Attendance record states ten absence days.");
        unrelatedSpan = AddSource(graph, 'C', "Synthetic unrelated evidence that should not be projected.");
        var first = AddAssertion(graph, firstSpan, "12", "Employer", now);
        var second = AddAssertion(graph, secondSpan, "10", "Attendance record", now.AddMinutes(1));
        var matterEvent = graph.AddEvent(Guid.NewGuid(), "absence-review", "Absence count reviewed",
            EventStatus.Disputed, VerificationState.NeedsContext, now);
        graph.AddAssertionEventLink(Guid.NewGuid(), first.Id, matterEvent.Id, AssertionEventRelation.Supports);
        graph.AddAssertionEventLink(Guid.NewGuid(), second.Id, matterEvent.Id, AssertionEventRelation.Contradicts);
        graph.AddContradiction(Guid.NewGuid(), first.Id, second.Id,
            ContradictionType.NumericMismatch, "synthetic-rule", now);
        return new PersistedMatterBrain(graph, new WorkplaceMatter(graph), new MatterBrainState(graph));
    }

    private static SourceSpan AddSource(MatterEvidenceGraph graph, char hash, string text)
    {
        var version = graph.RegisterDocumentVersion(Guid.NewGuid(), Guid.NewGuid(), new string(hash, 64), Guid.NewGuid());
        return graph.AddSourceSpan(Guid.NewGuid(), version, text, "synthetic-parser/1", 0.99m,
            pageNumber: 1, textStart: 0, textEnd: text.Length);
    }

    private static Assertion AddAssertion(
        MatterEvidenceGraph graph,
        SourceSpan source,
        string value,
        string assertedBy,
        DateTimeOffset assertedAt) => graph.AddAssertion(
        Guid.NewGuid(), "synthetic-employee", "sickness-day-count", value, assertedBy, assertedAt,
        EvidenceOriginClass.OriginalContemporaneousRecord, AssertionClass.AttributedAssertion,
        DisputeState.Contradicted, IntegrityState.OriginalHashVerified, VerificationState.NeedsContext,
        source.Id, eventTime: assertedAt, extractionConfidence: 0.99m);

    private sealed class FixedExtractionProvider(StructuredExtractionOutput output) : IStructuredExtractionProvider
    {
        public StructuredExtractionProviderDescriptor Descriptor { get; } =
            new("synthetic-provider", "synthetic-model", "1", "1", "1");

        public Task<StructuredExtractionOutput> ExtractAsync(
            StructuredExtractionInput input,
            CancellationToken cancellationToken = default) => Task.FromResult(output);
    }
}
