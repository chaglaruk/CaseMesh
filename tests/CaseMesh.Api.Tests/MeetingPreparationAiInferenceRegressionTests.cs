using System.Text.Json;
using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Core.Workplace;
using CaseMesh.MatterBrain;
using CaseMesh.Persistence.Postgres;

namespace CaseMesh.Api.Tests;

public sealed class MeetingPreparationAiInferenceRegressionTests
{
    [Fact]
    public void Preparation_excludes_both_cited_and_source_less_ai_inference_without_creating_evidence_gaps()
    {
        var now = new DateTimeOffset(2026, 5, 3, 9, 0, 0, TimeSpan.Zero);
        var graph = new MatterEvidenceGraph(new Matter(Guid.NewGuid(), new TenantId(Guid.NewGuid()),
            "workplace-dispute", "Synthetic AI inference Matter", "active", now, now));
        var version = graph.RegisterDocumentVersion(Guid.NewGuid(), Guid.NewGuid(), new string('F', 64), Guid.NewGuid());
        var span = graph.AddSourceSpan(Guid.NewGuid(), version,
            "Synthetic source text that must not make model inference documentary evidence.",
            "synthetic-parser/1", 0.99m, pageNumber: 1, textStart: 0, textEnd: 72);

        var citedInference = graph.AddAssertion(Guid.NewGuid(), "synthetic-employee", "cited-ai-prediction", "win",
            "synthetic model", now,
            EvidenceOriginClass.AiGeneratedInference, AssertionClass.AiInference,
            DisputeState.Unverified, IntegrityState.DerivedCopy, VerificationState.NotReviewed,
            span.Id, createdByModel: "synthetic-model/1");
        var sourceLessInference = graph.AddAssertion(Guid.NewGuid(), "synthetic-employee", "source-less-ai-prediction", "lose",
            "synthetic model", now.AddMinutes(1),
            EvidenceOriginClass.AiGeneratedInference, AssertionClass.AiInference,
            DisputeState.Unverified, IntegrityState.DerivedCopy, VerificationState.NotReviewed,
            createdByModel: "synthetic-model/1");
        var loaded = new PersistedMatterBrain(graph, new WorkplaceMatter(graph), new MatterBrainState(graph));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(MeetingPreparationProjection.Create(loaded, false)));
        var evidencePoints = json.RootElement.GetProperty("evidencePoints").ToString();

        Assert.DoesNotContain(citedInference.Predicate, evidencePoints, StringComparison.Ordinal);
        Assert.DoesNotContain(sourceLessInference.Predicate, evidencePoints, StringComparison.Ordinal);
        Assert.DoesNotContain(json.RootElement.GetProperty("questionsToClarify").EnumerateArray(), gap =>
            gap.GetProperty("Code").GetString() == "assertion-without-documentary-source" &&
            gap.GetProperty("RelatedRecordIds").EnumerateArray().Any(id => id.GetGuid() == sourceLessInference.Id));
    }
}
