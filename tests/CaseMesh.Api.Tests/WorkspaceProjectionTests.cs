using System.Text.Json;
using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Core.Workplace;
using CaseMesh.MatterBrain;
using CaseMesh.Persistence.Postgres;
using CaseMesh.Qa;

namespace CaseMesh.Api.Tests;

public sealed class WorkspaceProjectionTests
{
    [Fact]
    public void Timeline_projection_includes_exact_referenced_source_spans()
    {
        var now = new DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.Zero);
        var graph = new MatterEvidenceGraph(new Matter(
            Guid.NewGuid(), new TenantId(Guid.NewGuid()), "workplace-dispute", "Synthetic Matter", "open", now, now));
        var referencedSpan = AddSource(graph, 'D', "Synthetic timeline statement.");
        var unrelatedSpan = AddSource(graph, 'E', "Synthetic unrelated statement.");
        var assertion = AddAssertion(graph, referencedSpan, "synthetic-value", now);
        var matterEvent = graph.AddEvent(Guid.NewGuid(), "synthetic-event", "Synthetic event",
            EventStatus.Candidate, VerificationState.NotReviewed, now);
        graph.AddAssertionEventLink(Guid.NewGuid(), assertion.Id, matterEvent.Id, AssertionEventRelation.Supports);
        var loaded = new PersistedMatterBrain(graph, new WorkplaceMatter(graph), new MatterBrainState(graph));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(WorkspaceProjection.Timeline(loaded)));
        var projectedEvent = Assert.Single(json.RootElement.GetProperty("events").EnumerateArray());
        Assert.Equal(referencedSpan.Id,
            Assert.Single(projectedEvent.GetProperty("sourceSpanIds").EnumerateArray()).GetGuid());
        var projectedSpan = Assert.Single(json.RootElement.GetProperty("sourceSpans").EnumerateArray());
        Assert.Equal(referencedSpan.Id, projectedSpan.GetProperty("Id").GetGuid());
        Assert.Equal(referencedSpan.DocumentVersion.DocumentVersionId,
            projectedSpan.GetProperty("DocumentVersionId").GetGuid());
        Assert.NotEqual(unrelatedSpan.Id, projectedSpan.GetProperty("Id").GetGuid());
    }

    [Fact]
    public void Disputed_projection_includes_exact_referenced_source_spans()
    {
        var now = new DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.Zero);
        var graph = new MatterEvidenceGraph(new Matter(
            Guid.NewGuid(), new TenantId(Guid.NewGuid()), "workplace-dispute", "Synthetic Matter", "open", now, now));
        var firstSpan = AddSource(graph, 'A', "Synthetic employer statement.");
        var secondSpan = AddSource(graph, 'B', "Synthetic attendance statement.");
        var unrelatedSpan = AddSource(graph, 'C', "Synthetic unrelated statement.");
        var first = AddAssertion(graph, firstSpan, "12", now);
        var second = AddAssertion(graph, secondSpan, "10", now);
        graph.AddContradiction(Guid.NewGuid(), first.Id, second.Id,
            ContradictionType.NumericMismatch, "synthetic-rule", now);
        var loaded = new PersistedMatterBrain(graph, new WorkplaceMatter(graph), new MatterBrainState(graph));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(WorkspaceProjection.Disputed(loaded)));
        var spanIds = json.RootElement.GetProperty("sourceSpans").EnumerateArray()
            .Select(item => item.GetProperty("Id").GetGuid()).ToArray();

        Assert.Contains(firstSpan.Id, spanIds);
        Assert.Contains(secondSpan.Id, spanIds);
        Assert.DoesNotContain(unrelatedSpan.Id, spanIds);
    }

    [Fact]
    public void Questions_projection_exposes_factual_gaps_and_processing_state()
    {
        var loaded = CreateContradictedMatter();

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(WorkspaceProjection.Questions(loaded, true)));

        Assert.True(json.RootElement.GetProperty("processing").GetBoolean());
        Assert.Contains(json.RootElement.GetProperty("gaps").EnumerateArray(), item =>
            item.GetProperty("Code").GetString() == "unresolved-contradiction" &&
            item.GetProperty("Route").GetString() == "disputed");
        Assert.Contains("not legal", json.RootElement.GetProperty("notice").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Answer_projection_returns_only_exact_cited_source_spans()
    {
        var loaded = CreateContradictedMatter();
        var cited = loaded.Evidence.SourceSpans.First();
        var retrievalId = Guid.NewGuid();
        var answer = new MatterQaAnswer(MatterAnswerStatus.Answered, "Synthetic answer",
            [new VerifiedMatterClaim("Employer asserted a count.", MatterClaimKind.Evidence, [retrievalId])],
            [new VerifiedMatterCitation(retrievalId, RetrievalMaterialKind.Assertion,
                loaded.Evidence.Assertions.First().Id, cited.Id, cited.DocumentVersion.DocumentVersionId,
                cited.DocumentVersion.OriginalObjectId, cited.DocumentVersion.ContentSha256,
                "Employer asserted a count", "Employer", "Contradicted", false)], [], null,
            new MatterReasoningProviderDescriptor("synthetic", "golden", "v1"));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(WorkspaceProjection.QuestionAnswer(loaded, answer)));

        var span = Assert.Single(json.RootElement.GetProperty("sourceSpans").EnumerateArray());
        Assert.Equal(cited.Id, span.GetProperty("Id").GetGuid());
        Assert.Equal(cited.DocumentVersion.DocumentVersionId, span.GetProperty("DocumentVersionId").GetGuid());
        Assert.Contains("generation time", json.RootElement.GetProperty("currentnessNotice").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static PersistedMatterBrain CreateContradictedMatter()
    {
        var now = new DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.Zero);
        var graph = new MatterEvidenceGraph(new Matter(Guid.NewGuid(), new TenantId(Guid.NewGuid()),
            "workplace-dispute", "Synthetic Matter", "open", now, now));
        var first = AddAssertion(graph, AddSource(graph, 'A', "Employer states 12 days."), "12", now);
        var second = AddAssertion(graph, AddSource(graph, 'B', "Attendance records 10 days."), "10", now);
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
        DateTimeOffset assertedAt) => graph.AddAssertion(
        Guid.NewGuid(), "synthetic-employee", "sickness-day-count", value, "synthetic source", assertedAt,
        EvidenceOriginClass.OriginalContemporaneousRecord, AssertionClass.DirectlyDocumentedEvent,
        DisputeState.Unverified, IntegrityState.OriginalHashVerified, VerificationState.NotReviewed,
        source.Id, extractionConfidence: 0.99m);
}
