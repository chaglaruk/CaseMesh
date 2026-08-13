using CaseMesh.Core.Models;
using CaseMesh.Core.Services;

namespace CaseMesh.Core.Tests;

internal static class SyntheticWorkplaceMatterFixture
{
    internal static readonly DateTimeOffset RecordedAt = new(2026, 4, 20, 9, 0, 0, TimeSpan.Zero);

    internal static MatterEvidenceGraph CreateGraph(int seed = 1)
    {
        var matter = new Matter(
            Id(seed),
            "workplace-dispute",
            $"Synthetic workplace matter {seed}",
            "open",
            RecordedAt,
            RecordedAt,
            "England and Wales");
        return new MatterEvidenceGraph(matter);
    }

    internal static SourceSpan AddSource(
        MatterEvidenceGraph graph,
        int seed,
        string text,
        char hashCharacter)
    {
        var version = graph.RegisterDocumentVersion(
            Id(seed),
            Id(seed + 1),
            new string(hashCharacter, 64),
            Id(seed + 2));
        return graph.AddSourceSpan(
            Id(seed + 3),
            version,
            text,
            "synthetic-parser/1",
            extractionConfidence: 0.99m,
            pageNumber: 1,
            textStart: 0,
            textEnd: text.Length);
    }

    internal static Assertion AddSicknessDayAssertion(
        MatterEvidenceGraph graph,
        int idSeed,
        SourceSpan source,
        string value,
        EvidenceOriginClass origin,
        AssertionClass assertionClass,
        string assertedBy)
    {
        return graph.AddAssertion(
            Id(idSeed),
            "synthetic-employee",
            "sickness-day-count",
            value,
            assertedBy,
            RecordedAt,
            origin,
            assertionClass,
            DisputeState.Contradicted,
            IntegrityState.OriginalHashVerified,
            VerificationState.NotReviewed,
            source.Id,
            extractionConfidence: 0.98m);
    }

    internal static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");
}
