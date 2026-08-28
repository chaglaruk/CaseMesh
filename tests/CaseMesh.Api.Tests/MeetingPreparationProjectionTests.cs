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
        var aiInference = loaded.Evidence.AddAssertion(Guid.NewGuid(), "synthetic-employee", "predicted-outcome", "win",
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
        Assert.DoesNotContain(json.RootElement.GetProperty("questionsToClarify").EnumerateArray(), gap =>
            gap.GetProperty("Code").GetString() == "assertion-without-documentary-source" &&
            gap.GetProperty("RelatedRecordIds").EnumerateArray().Any(id => id.GetGuid() == aiInference.Id));
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
        var participant = Assert.Single(json.RootElement.GetProperty("participants").EnumerateArray());
        Assert.Equal(people[1].Id, participant.GetProperty("Id").GetGuid());
        Assert.Equal("SourceBackedExtraction", participant.GetProperty("provenanceStatus").GetString());
        Assert.Equal(2, participant.GetProperty("mergedIdentityIds").GetArrayLength());
        Assert.Contains(people[0].Id,
            participant.GetProperty("mergedIdentityIds").EnumerateArray().Select(item => item.GetGuid()));
        Assert.Contains(people[1].Id,
            participant.GetProperty("mergedIdentityIds").EnumerateArray().Select(item => item.GetGuid()));
        var aliases = participant.GetProperty("identityAliases").EnumerateArray()
            .Select(item => item.GetProperty("Value").GetString()).ToArray();
        Assert.Contains("Alex Smith", aliases);
        Assert.Contains("Alex Smyth", aliases);
        Assert.Contains(firstSpan.Id,
            participant.GetProperty("sourceSpanIds").EnumerateArray().Select(item => item.GetGuid()));
        Assert.Contains(firstSpan.DocumentVersion.DocumentVersionId,
            participant.GetProperty("documentVersionIds").EnumerateArray().Select(item => item.GetGuid()));
        Assert.Contains("collapsed current participant records", participant.GetProperty("identityNotice").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(json.RootElement.GetProperty("questionsToClarify").EnumerateArray(), item =>
            item.GetProperty("Code").GetString() == "entity-ambiguity");
        Assert.Contains(firstSpan.Id, json.RootElement.GetProperty("sourceSpans").EnumerateArray()
            .Select(item => item.GetProperty("Id").GetGuid()));
    }

    [Fact]
    public async Task Preparation_marks_reused_participant_fields_unsupported_when_active_candidate_changes_roles()
    {
        var loaded = CreateSyntheticMatter(out var firstSpan, out _, out _);
        var original = new FixedExtractionProvider(new StructuredExtractionOutput(
            "{\"person\":\"employee\"}", new StructuredCandidateBatch(
                [new EntityCandidate("person", CanonicalEntityKind.Person, "Alex Smith", "person",
                    ["Alex Smith"], ["Employee"], [firstSpan.Id], 0.99m)], [], [], [], [], [], [])),
            "participant-model-v1");
        var replacement = new FixedExtractionProvider(new StructuredExtractionOutput(
            "{\"person\":\"manager\"}", new StructuredCandidateBatch(
                [new EntityCandidate("person", CanonicalEntityKind.Person, "Alex Smith", "person",
                    ["Alex Smith"], ["Manager"], [firstSpan.Id], 0.99m)], [], [], [], [], [], [])),
            "participant-model-v2");

        await new MatterBrainMergeService(TimeProvider.System)
            .ExtractAndMergeAsync(loaded.Brain, [firstSpan.Id], original);
        await new MatterBrainMergeService(TimeProvider.System)
            .ExtractAndMergeAsync(loaded.Brain, [firstSpan.Id], replacement);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(MeetingPreparationProjection.Create(loaded, false)));
        var participant = Assert.Single(json.RootElement.GetProperty("participants").EnumerateArray());
        Assert.Equal("Alex Smith", participant.GetProperty("DisplayName").GetString());
        Assert.Contains("Employee", participant.GetProperty("RoleLabels").EnumerateArray()
            .Select(item => item.GetString()));
        Assert.Equal("Unsupported", participant.GetProperty("provenanceStatus").GetString());
        Assert.Equal(0, participant.GetProperty("sourceSpanIds").GetArrayLength());
        Assert.Contains("no longer exactly supports", participant.GetProperty("identityNotice").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Preparation_retires_structured_entity_match_proposals_after_source_reextraction()
    {
        var loaded = CreateSyntheticMatter(out var firstSpan, out _, out _);
        EntityCandidate[] entities =
        [
            new("person-a", CanonicalEntityKind.Person, "Alex Smith", "person",
                ["Alex Smith"], ["Employee"], [firstSpan.Id], 0.99m),
            new("person-b", CanonicalEntityKind.Person, "Alex Smyth", "person",
                ["Alex Smyth"], ["Manager"], [firstSpan.Id], 0.98m)
        ];
        var original = new FixedExtractionProvider(new StructuredExtractionOutput(
            "{\"match\":true}", new StructuredCandidateBatch(entities, [], [], [], [],
                [new EntityMatchCandidate("possible-same-person", CanonicalEntityKind.Person,
                    "person-a", "person-b", 0.88m, [firstSpan.Id], 0.90m)], [])), "match-model-v1");
        var replacement = new FixedExtractionProvider(new StructuredExtractionOutput(
            "{\"match\":false}", new StructuredCandidateBatch(entities, [], [], [], [], [], [])), "match-model-v2");

        await new MatterBrainMergeService(TimeProvider.System)
            .ExtractAndMergeAsync(loaded.Brain, [firstSpan.Id], original);
        Assert.Single(loaded.Brain.EntityResolutionActions,
            item => item.Kind == EntityResolutionActionKind.Proposed);

        await new MatterBrainMergeService(TimeProvider.System)
            .ExtractAndMergeAsync(loaded.Brain, [firstSpan.Id], replacement);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(MeetingPreparationProjection.Create(loaded, false)));
        Assert.DoesNotContain(json.RootElement.GetProperty("questionsToClarify").EnumerateArray(), item =>
            item.GetProperty("Code").GetString() == "entity-ambiguity");
    }

    [Fact]
    public async Task Preparation_uses_event_dependencies_and_prioritizes_dated_chronology()
    {
        var loaded = CreateSyntheticMatter(out var firstSpan, out _, out _);
        var undated = Enumerable.Range(0, 12)
            .Select(index => new EventCandidate($"undated-{index}", "meeting", $"Undated extracted event {index}",
                null, null, [], [firstSpan.Id], 0.80m))
            .ToArray();
        var events = undated.Append(new EventCandidate("dated", "meeting", "Dated extracted meeting",
            new DateTimeOffset(2026, 4, 20, 10, 0, 0, TimeSpan.Zero), null, [], [firstSpan.Id], 0.99m)).ToArray();
        var provider = new FixedExtractionProvider(new StructuredExtractionOutput(
            "{\"events\":true}", new StructuredCandidateBatch([], [], [], events, [], [], [])), "event-model");

        await new MatterBrainMergeService(TimeProvider.System)
            .ExtractAndMergeAsync(loaded.Brain, [firstSpan.Id], provider);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(MeetingPreparationProjection.Create(loaded, false)));
        var chronology = json.RootElement.GetProperty("chronology").EnumerateArray().ToArray();
        Assert.Equal(12, chronology.Length);
        var dated = Assert.Single(chronology, item =>
            item.GetProperty("Label").GetString() == "Dated extracted meeting");
        Assert.Contains(firstSpan.Id,
            dated.GetProperty("sourceSpanIds").EnumerateArray().Select(item => item.GetGuid()));
        Assert.All(chronology, item => Assert.True(item.GetProperty("sourceSpanIds").GetArrayLength() > 0));
    }

    [Fact]
    public void Preparation_omits_stale_event_date_after_assertion_date_correction()
    {
        var loaded = CreateSyntheticMatter(out _, out _, out _);
        var matterEvent = Assert.Single(loaded.Evidence.Events);
        var original = loaded.Evidence.Assertions.OrderBy(item => item.AssertedAt).First();
        var correctedEventTime = matterEvent.StartTime!.Value.AddDays(3);

        loaded.Brain.CorrectAssertion(original.Id, Guid.NewGuid(), "11", correctedEventTime,
            Guid.NewGuid(), "synthetic-reviewer", original.AssertedAt.AddHours(1));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(MeetingPreparationProjection.Create(loaded, false)));
        Assert.DoesNotContain(json.RootElement.GetProperty("chronology").EnumerateArray(), item =>
            item.GetProperty("Id").GetGuid() == matterEvent.Id);
        Assert.Contains(json.RootElement.GetProperty("questionsToClarify").EnumerateArray(), item =>
            item.GetProperty("Code").GetString() == "corrected-history-review");
    }

    [Fact]
    public async Task Preparation_excludes_assertions_with_invalidated_extraction_dependencies()
    {
        var loaded = CreateSyntheticMatter(out var firstSpan, out _, out _);
        var assertedAt = new DateTimeOffset(2026, 4, 21, 11, 0, 0, TimeSpan.Zero);
        var oldProvider = new FixedExtractionProvider(AssertionOutput(firstSpan, "old-extracted-value", assertedAt),
            "assertion-model-v1");
        var newProvider = new FixedExtractionProvider(AssertionOutput(firstSpan, "new-extracted-value", assertedAt),
            "assertion-model-v2");

        await new MatterBrainMergeService(TimeProvider.System)
            .ExtractAndMergeAsync(loaded.Brain, [firstSpan.Id], oldProvider);
        await new MatterBrainMergeService(TimeProvider.System)
            .ExtractAndMergeAsync(loaded.Brain, [firstSpan.Id], newProvider);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(MeetingPreparationProjection.Create(loaded, false)));
        var evidencePoints = json.RootElement.GetProperty("evidencePoints").ToString();
        Assert.Contains("new-extracted-value", evidencePoints, StringComparison.Ordinal);
        Assert.DoesNotContain("old-extracted-value", evidencePoints, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preparation_excludes_invalidated_extracted_contradictions_from_disputes_and_gaps()
    {
        var loaded = CreateSyntheticMatter(out var firstSpan, out _, out _);
        var assertedAt = new DateTimeOffset(2026, 4, 22, 11, 0, 0, TimeSpan.Zero);
        var staleProvider = new FixedExtractionProvider(StaleContradictionOutput(firstSpan, assertedAt),
            "contradiction-model-v1");

        await new MatterBrainMergeService(TimeProvider.System)
            .ExtractAndMergeAsync(loaded.Brain, [firstSpan.Id], staleProvider);
        var staleContradiction = loaded.Evidence.Contradictions.Single(item =>
            item.DetectedBy == "synthetic-extractor-conflict");

        var replacementProvider = new FixedExtractionProvider(
            AssertionOutput(firstSpan, "replacement-current-value", assertedAt), "contradiction-model-v2");
        await new MatterBrainMergeService(TimeProvider.System)
            .ExtractAndMergeAsync(loaded.Brain, [firstSpan.Id], replacementProvider);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(MeetingPreparationProjection.Create(loaded, false)));
        Assert.DoesNotContain(json.RootElement.GetProperty("unresolvedDisputes").EnumerateArray(), item =>
            item.GetProperty("Id").GetGuid() == staleContradiction.Id);
        Assert.DoesNotContain(json.RootElement.GetProperty("questionsToClarify").EnumerateArray(), item =>
            item.GetProperty("RelatedRecordIds").EnumerateArray().Any(id => id.GetGuid() == staleContradiction.Id));
    }

    private static StructuredExtractionOutput AssertionOutput(SourceSpan source, string value, DateTimeOffset assertedAt) =>
        new("{\"assertion\":true}", new StructuredCandidateBatch([], [],
            [new AssertionCandidate("extracted-assertion", "synthetic-employee", "extracted-value", value,
                "Synthetic extractor", assertedAt, assertedAt, source.Id,
                EvidenceOriginClass.OriginalContemporaneousRecord, AssertionClass.AttributedAssertion,
                IntegrityState.OriginalHashVerified, [source.Id], 0.95m)], [], [], [], []));

    private static StructuredExtractionOutput StaleContradictionOutput(
        SourceSpan source,
        DateTimeOffset assertedAt) =>
        new("{\"contradiction\":true}", new StructuredCandidateBatch([], [],
            [
                new AssertionCandidate("stale-a", "synthetic-employee", "stale-count", "one",
                    "Synthetic source A", assertedAt, assertedAt, source.Id,
                    EvidenceOriginClass.OriginalContemporaneousRecord, AssertionClass.AttributedAssertion,
                    IntegrityState.OriginalHashVerified, [source.Id], 0.95m),
                new AssertionCandidate("stale-b", "synthetic-employee", "stale-count", "two",
                    "Synthetic source B", assertedAt.AddMinutes(1), assertedAt.AddMinutes(1), source.Id,
                    EvidenceOriginClass.OriginalContemporaneousRecord, AssertionClass.AttributedAssertion,
                    IntegrityState.OriginalHashVerified, [source.Id], 0.95m)
            ], [], [], [],
            [new ContradictionCandidate("stale-conflict", "stale-a", "stale-b",
                ContradictionType.DirectConflict, "synthetic-extractor-conflict", [source.Id], 0.90m)]));

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

    private sealed class FixedExtractionProvider(StructuredExtractionOutput output, string model = "synthetic-model")
        : IStructuredExtractionProvider
    {
        public StructuredExtractionProviderDescriptor Descriptor { get; } =
            new("synthetic-provider", model, "1", "1", "1");

        public Task<StructuredExtractionOutput> ExtractAsync(
            StructuredExtractionInput input,
            CancellationToken cancellationToken = default) => Task.FromResult(output);
    }
}