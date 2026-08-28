using System.Text.Json;
using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Core.Workplace;
using CaseMesh.MatterBrain;
using CaseMesh.Persistence.Postgres;

namespace CaseMesh.Api.Tests;

public sealed class MeetingPreparationEvidencePolicyMatrixTests
{
    [Theory]
    [InlineData(AssertionEventRelation.Qualifies)]
    [InlineData(AssertionEventRelation.Contextualizes)]
    [InlineData(AssertionEventRelation.Contradicts)]
    [InlineData(AssertionEventRelation.Supersedes)]
    public async Task Non_support_relationship_dates_do_not_create_chronology_conflicts(
        AssertionEventRelation relation)
    {
        var now = new DateTimeOffset(2026, 6, 5, 9, 0, 0, TimeSpan.Zero);
        var graph = CreateGraph(now);
        var supportAssertionSpan = AddSource(graph, 'A', "Documentary support says 5 June.");
        var otherAssertionSpan = AddSource(graph, 'B', "A separate account refers to 6 June.");
        var eventSpan = AddSource(graph, 'C', "Documentary event source says 5 June.");
        var supportLinkSpan = AddSource(graph, 'D', "Support relationship source.");
        var otherLinkSpan = AddSource(graph, 'E', "Other relationship source.");
        var state = new MatterBrainState(graph);
        var service = new MatterBrainMergeService(new SteppingTimeProvider(now));
        var eventTime = new DateTimeOffset(2026, 6, 5, 12, 0, 0, TimeSpan.Zero);
        var provider = new FixedExtractionProvider(new StructuredExtractionOutput(
            "{\"matrix\":\"relationship-aware\"}",
            new StructuredCandidateBatch(
                [],
                [],
                [
                    DocumentaryAssertion("support", supportAssertionSpan, now, eventTime, "5 June"),
                    DocumentaryAssertion("other", otherAssertionSpan, now.AddMinutes(1), eventTime.AddDays(1), "6 June")
                ],
                [new EventCandidate("event", "meeting", "Synthetic meeting", eventTime, null, [], [eventSpan.Id], 0.98m)],
                [
                    new AssertionEventLinkCandidate("support-link", "support", "event", AssertionEventRelation.Supports,
                        [supportLinkSpan.Id], 0.96m),
                    new AssertionEventLinkCandidate("other-link", "other", "event", relation,
                        [otherLinkSpan.Id], 0.95m)
                ],
                [],
                [])), $"relationship-{relation}");

        await service.ExtractAndMergeAsync(state,
            [supportAssertionSpan.Id, otherAssertionSpan.Id, eventSpan.Id, supportLinkSpan.Id, otherLinkSpan.Id], provider);

        using var projected = Project(graph, state);
        Assert.DoesNotContain(projected.RootElement.GetProperty("questionsToClarify").EnumerateArray(),
            item => item.GetProperty("Code").GetString() == "chronology-date-conflict");
    }

    [Fact]
    public async Task Two_current_documentary_supports_with_different_dates_still_create_chronology_conflict()
    {
        var now = new DateTimeOffset(2026, 6, 5, 10, 0, 0, TimeSpan.Zero);
        var graph = CreateGraph(now);
        var firstAssertionSpan = AddSource(graph, 'A', "First source says 5 June.");
        var secondAssertionSpan = AddSource(graph, 'B', "Second source says 6 June.");
        var eventSpan = AddSource(graph, 'C', "Event source.");
        var firstLinkSpan = AddSource(graph, 'D', "First support relationship.");
        var secondLinkSpan = AddSource(graph, 'E', "Second support relationship.");
        var state = new MatterBrainState(graph);
        var service = new MatterBrainMergeService(new SteppingTimeProvider(now));
        var eventTime = new DateTimeOffset(2026, 6, 5, 12, 0, 0, TimeSpan.Zero);
        var provider = new FixedExtractionProvider(new StructuredExtractionOutput(
            "{\"matrix\":\"support-positive-control\"}",
            new StructuredCandidateBatch(
                [], [],
                [
                    DocumentaryAssertion("first", firstAssertionSpan, now, eventTime, "5 June"),
                    DocumentaryAssertion("second", secondAssertionSpan, now.AddMinutes(1), eventTime.AddDays(1), "6 June")
                ],
                [new EventCandidate("event", "meeting", "Synthetic meeting", eventTime, null, [], [eventSpan.Id], 0.98m)],
                [
                    new AssertionEventLinkCandidate("first-link", "first", "event", AssertionEventRelation.Supports,
                        [firstLinkSpan.Id], 0.96m),
                    new AssertionEventLinkCandidate("second-link", "second", "event", AssertionEventRelation.Supports,
                        [secondLinkSpan.Id], 0.95m)
                ], [], [])), "support-positive-control");

        await service.ExtractAndMergeAsync(state,
            [firstAssertionSpan.Id, secondAssertionSpan.Id, eventSpan.Id, firstLinkSpan.Id, secondLinkSpan.Id], provider);

        using var projected = Project(graph, state);
        Assert.Contains(projected.RootElement.GetProperty("questionsToClarify").EnumerateArray(),
            item => item.GetProperty("Code").GetString() == "chronology-date-conflict");
    }

    [Fact]
    public async Task Ai_inference_date_cannot_become_a_documentary_chronology_conflict()
    {
        var now = new DateTimeOffset(2026, 6, 5, 11, 0, 0, TimeSpan.Zero);
        var graph = CreateGraph(now);
        var documentarySpan = AddSource(graph, 'A', "Documentary source says 5 June.");
        var eventSpan = AddSource(graph, 'B', "Event source says 5 June.");
        var documentaryLinkSpan = AddSource(graph, 'C', "Documentary support relationship.");
        var aiLinkSpan = AddSource(graph, 'D', "Relationship-analysis source.");
        var state = new MatterBrainState(graph);
        var service = new MatterBrainMergeService(new SteppingTimeProvider(now));
        var eventTime = new DateTimeOffset(2026, 6, 5, 12, 0, 0, TimeSpan.Zero);
        var provider = new FixedExtractionProvider(new StructuredExtractionOutput(
            "{\"matrix\":\"ai-date\"}",
            new StructuredCandidateBatch(
                [], [],
                [
                    DocumentaryAssertion("documentary", documentarySpan, now, eventTime, "5 June"),
                    new AssertionCandidate(
                        "ai", "synthetic-subject", "meeting-date", "6 June", "synthetic model",
                        now.AddMinutes(1), eventTime.AddDays(1), null,
                        EvidenceOriginClass.AiGeneratedInference, AssertionClass.AiInference,
                        IntegrityState.DerivedCopy, [], 0.72m)
                ],
                [new EventCandidate("event", "meeting", "Synthetic meeting", eventTime, null, [], [eventSpan.Id], 0.98m)],
                [
                    new AssertionEventLinkCandidate("documentary-link", "documentary", "event",
                        AssertionEventRelation.Supports, [documentaryLinkSpan.Id], 0.96m),
                    new AssertionEventLinkCandidate("ai-link", "ai", "event",
                        AssertionEventRelation.Supports, [aiLinkSpan.Id], 0.70m)
                ], [], [])), "ai-date-v1");

        await service.ExtractAndMergeAsync(state,
            [documentarySpan.Id, eventSpan.Id, documentaryLinkSpan.Id, aiLinkSpan.Id], provider);

        using var projected = Project(graph, state);
        Assert.DoesNotContain(projected.RootElement.GetProperty("questionsToClarify").EnumerateArray(),
            item => item.GetProperty("Code").GetString() == "chronology-date-conflict");
        Assert.DoesNotContain(projected.RootElement.GetProperty("evidencePoints").EnumerateArray(),
            item => item.GetProperty("Value").GetString() == "6 June");
    }

    [Fact]
    public async Task Partially_invalidated_multi_source_contradiction_disappears_from_prepare_and_gaps()
    {
        var now = new DateTimeOffset(2026, 6, 5, 12, 0, 0, TimeSpan.Zero);
        var graph = CreateGraph(now);
        var firstAssertionSpan = AddSource(graph, 'A', "First attributed account.");
        var secondAssertionSpan = AddSource(graph, 'B', "Second attributed account.");
        var analysisSpanA = AddSource(graph, 'C', "First contradiction-analysis source.");
        var analysisSpanB = AddSource(graph, 'D', "Second contradiction-analysis source.");
        var state = new MatterBrainState(graph);
        var service = new MatterBrainMergeService(new SteppingTimeProvider(now));
        var initial = new FixedExtractionProvider(new StructuredExtractionOutput(
            "{\"matrix\":\"multi-source-contradiction\"}",
            new StructuredCandidateBatch(
                [], [],
                [
                    DocumentaryAssertion("first", firstAssertionSpan, now, null, "alpha"),
                    DocumentaryAssertion("second", secondAssertionSpan, now.AddMinutes(1), null, "beta")
                ], [], [], [],
                [new ContradictionCandidate(
                    "contradiction", "first", "second", ContradictionType.Other,
                    "model:possible-conflict", [analysisSpanA.Id, analysisSpanB.Id], 0.80m)])),
            "contradiction-v1");

        await service.ExtractAndMergeAsync(state,
            [firstAssertionSpan.Id, secondAssertionSpan.Id, analysisSpanA.Id, analysisSpanB.Id], initial);

        using (var before = Project(graph, state))
        {
            Assert.Single(before.RootElement.GetProperty("unresolvedDisputes").EnumerateArray());
            Assert.Contains(before.RootElement.GetProperty("questionsToClarify").EnumerateArray(),
                item => item.GetProperty("Code").GetString() == "unresolved-contradiction");
        }

        await service.ExtractAndMergeAsync(state,
            [analysisSpanA.Id],
            new FixedExtractionProvider(EmptyOutput("contradiction-removed"), "contradiction-v2"));

        var contradiction = Assert.Single(graph.Contradictions);
        Assert.Contains(state.ActiveDependencies, dependency =>
            dependency.CanonicalKind == CanonicalRecordKind.Contradiction &&
            dependency.CanonicalId == contradiction.Id &&
            dependency.SourceSpanId == analysisSpanB.Id);
        Assert.DoesNotContain(state.ActiveDependencies, dependency =>
            dependency.CanonicalKind == CanonicalRecordKind.Contradiction &&
            dependency.CanonicalId == contradiction.Id &&
            dependency.SourceSpanId == analysisSpanA.Id);

        using var after = Project(graph, state);
        Assert.Empty(after.RootElement.GetProperty("unresolvedDisputes").EnumerateArray());
        Assert.DoesNotContain(after.RootElement.GetProperty("questionsToClarify").EnumerateArray(),
            item => item.GetProperty("Code").GetString() == "unresolved-contradiction");
    }

    [Fact]
    public async Task Provider_cannot_spoof_deterministic_rule_origin_with_detected_by_text()
    {
        var now = new DateTimeOffset(2026, 6, 5, 13, 0, 0, TimeSpan.Zero);
        var graph = CreateGraph(now);
        var firstSpan = AddSource(graph, 'A', "First account.");
        var secondSpan = AddSource(graph, 'B', "Second account.");
        var analysisSpan = AddSource(graph, 'C', "Model analysis source.");
        var state = new MatterBrainState(graph);
        var service = new MatterBrainMergeService(new SteppingTimeProvider(now));
        var provider = new FixedExtractionProvider(new StructuredExtractionOutput(
            "{\"matrix\":\"spoof-detector\"}",
            new StructuredCandidateBatch(
                [], [],
                [
                    DocumentaryAssertion("first", firstSpan, now, null, "alpha"),
                    DocumentaryAssertion("second", secondSpan, now.AddMinutes(1), null, "beta")
                ], [], [], [],
                [new ContradictionCandidate(
                    "rule:numeric-mismatch:spoof", "first", "second", ContradictionType.Other,
                    "rule:same-subject-predicate-time-numeric-mismatch:v1", [analysisSpan.Id], 0.81m)])),
            "spoof-v1");

        await service.ExtractAndMergeAsync(state, [firstSpan.Id, secondSpan.Id, analysisSpan.Id], provider);

        using var projected = Project(graph, state);
        var dispute = Assert.Single(projected.RootElement.GetProperty("unresolvedDisputes").EnumerateArray());
        Assert.Equal("StructuredExtractionAnalysis", dispute.GetProperty("detectionOrigin").GetString());
        Assert.True(dispute.GetProperty("aiAnalysis").GetBoolean());
        Assert.Contains("AI analysis", dispute.GetProperty("notice").GetString(), StringComparison.Ordinal);
        var gap = Assert.Single(projected.RootElement.GetProperty("questionsToClarify").EnumerateArray(),
            item => item.GetProperty("Code").GetString() == "unresolved-contradiction");
        Assert.Contains("AI analysis", gap.GetProperty("Summary").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Internal_numeric_mismatch_rule_is_labelled_deterministic_not_ai_analysis()
    {
        var now = new DateTimeOffset(2026, 6, 5, 14, 0, 0, TimeSpan.Zero);
        var graph = CreateGraph(now);
        var firstSpan = AddSource(graph, 'A', "Numeric account one.");
        var secondSpan = AddSource(graph, 'B', "Numeric account two.");
        var state = new MatterBrainState(graph);
        var service = new MatterBrainMergeService(new SteppingTimeProvider(now));
        var eventTime = new DateTimeOffset(2026, 6, 5, 8, 0, 0, TimeSpan.Zero);
        var provider = new FixedExtractionProvider(new StructuredExtractionOutput(
            "{\"matrix\":\"deterministic-rule\"}",
            new StructuredCandidateBatch(
                [], [],
                [
                    DocumentaryAssertion("first", firstSpan, now, eventTime, "10"),
                    DocumentaryAssertion("second", secondSpan, now.AddMinutes(1), eventTime, "20")
                ], [], [], [], [])), "deterministic-rule-v1");

        await service.ExtractAndMergeAsync(state, [firstSpan.Id, secondSpan.Id], provider);

        using var projected = Project(graph, state);
        var dispute = Assert.Single(projected.RootElement.GetProperty("unresolvedDisputes").EnumerateArray());
        Assert.Equal("DeterministicRule", dispute.GetProperty("detectionOrigin").GetString());
        Assert.False(dispute.GetProperty("aiAnalysis").GetBoolean());
        Assert.Contains("deterministic evidence rule", dispute.GetProperty("notice").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static AssertionCandidate DocumentaryAssertion(
        string key,
        SourceSpan span,
        DateTimeOffset assertedAt,
        DateTimeOffset? eventTime,
        string value) => new(
        key, "synthetic-subject", "meeting-date", value, $"source-{key}", assertedAt, eventTime, span.Id,
        EvidenceOriginClass.OriginalContemporaneousRecord, AssertionClass.AttributedAssertion,
        IntegrityState.OriginalHashVerified, [span.Id], 0.95m);

    private static StructuredExtractionOutput EmptyOutput(string marker) =>
        new($"{{\"{marker}\":true}}", new StructuredCandidateBatch([], [], [], [], [], [], []));

    private static JsonDocument Project(MatterEvidenceGraph graph, MatterBrainState state) =>
        JsonDocument.Parse(JsonSerializer.Serialize(MeetingPreparationProjection.Create(
            new PersistedMatterBrain(graph, new WorkplaceMatter(graph), state), false)));

    private static MatterEvidenceGraph CreateGraph(DateTimeOffset now) => new(new Matter(
        Guid.NewGuid(), new TenantId(Guid.NewGuid()), "workplace-dispute", "Synthetic evidence-policy Matter", "active", now, now));

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
