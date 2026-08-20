using CaseMesh.Core.Models;
using CaseMesh.Core.Services;

namespace CaseMesh.MatterBrain.Tests;

internal static class SyntheticMatterBrainFixture
{
    internal static readonly DateTimeOffset Now = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    internal static MatterEvidenceGraph CreateGraph(int seed = 1)
    {
        var matter = new Matter(
            Id(seed, 1),
            new TenantId(Id(seed, 2)),
            "workplace-dispute",
            $"Synthetic Matter Brain {seed}",
            "open",
            Now,
            Now,
            "England and Wales");
        return new MatterEvidenceGraph(matter);
    }

    internal static SourceSpan AddSource(
        MatterEvidenceGraph graph,
        int seed,
        int offset,
        string text,
        char hashCharacter)
    {
        var version = graph.RegisterDocumentVersion(
            Id(seed, offset), Id(seed, offset + 1), new string(hashCharacter, 64), Id(seed, offset + 2));
        return graph.AddSourceSpan(
            Id(seed, offset + 3), version, text, "synthetic-parser/1", 0.99m,
            pageNumber: 1, textStart: 0, textEnd: text.Length);
    }

    internal static Guid Id(int seed, int offset) =>
        Guid.Parse($"{seed:X8}-0000-0000-0000-{offset:D12}");

    internal static StructuredCandidateBatch EmptyBatch() => new([], [], [], [], [], [], []);

    internal sealed class GoldenProvider(
        StructuredExtractionProviderDescriptor descriptor,
        StructuredCandidateBatch batch,
        string raw = "{\"synthetic\":true}") : IStructuredExtractionProvider
    {
        public StructuredExtractionProviderDescriptor Descriptor { get; } = descriptor;
        public int CallCount { get; private set; }
        public StructuredExtractionInput? LastInput { get; private set; }

        public Task<StructuredExtractionOutput> ExtractAsync(
            StructuredExtractionInput input,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastInput = input;
            return Task.FromResult(new StructuredExtractionOutput(raw, batch));
        }
    }

    internal sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    internal static StructuredExtractionProviderDescriptor Descriptor(string extractionVersion = "extract/v1") =>
        new("synthetic-provider", "golden-model", extractionVersion, "prompt/v1", "schema/v1");
}
