using System.Text.Json;
using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Core.Workplace;
using CaseMesh.MatterBrain;
using CaseMesh.Persistence.Postgres;

namespace CaseMesh.Api.Tests;

public sealed class WorkspaceProjectionTests
{
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
