using CaseMesh.Core.Models;
using CaseMesh.MatterBrain;
using CaseMesh.Qa;

namespace CaseMesh.Persistence.Postgres.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresExtractionRunSequenceTests(PostgresFixture database)
{
    [PostgresFact]
    public async Task Fixed_clock_run_sequence_round_trips_and_retires_obsolete_entity_match()
    {
        var tenant = new TenantId(SyntheticPersistedMatterFactory.Id(829, 1));
        var persisted = SyntheticPersistedMatterFactory.Create(
            tenant, SyntheticPersistedMatterFactory.Id(829, 2), 829);
        var source = persisted.Evidence.SourceSpans.First();
        var now = SyntheticPersistedMatterFactory.RecordedAt.AddHours(7);
        var brain = new MatterBrainState(persisted.Evidence);
        var service = new MatterBrainMergeService(new FixedTimeProvider(now));

        var initial = new FixedExtractionProvider(new StructuredExtractionOutput(
            "{\"match\":true}", new StructuredCandidateBatch(
                [
                    new EntityCandidate("person-a", CanonicalEntityKind.Person, "Jordan Lee", "person",
                        ["Jordan Lee"], ["employee"], [source.Id], 0.99m),
                    new EntityCandidate("person-b", CanonicalEntityKind.Person, "Jordon Lee", "person",
                        ["Jordon Lee"], ["manager"], [source.Id], 0.98m)
                ],
                [], [], [], [],
                [new EntityMatchCandidate("possible-same-person", CanonicalEntityKind.Person,
                    "person-a", "person-b", 0.88m, [source.Id], 0.90m)], [])),
            "fixed-clock-sequence-v1");
        var replacement = new FixedExtractionProvider(new StructuredExtractionOutput(
            "{\"match\":false}", new StructuredCandidateBatch([], [], [], [], [], [], [])),
            "fixed-clock-sequence-v2");

        var first = await service.ExtractAndMergeAsync(brain, [source.Id], initial);
        Assert.Contains(FactualGapAnalyzer.Analyze(persisted.Evidence, persisted.Workplace, brain),
            item => item.Code == "entity-ambiguity");
        var second = await service.ExtractAndMergeAsync(brain, [source.Id], replacement);

        Assert.Equal(now, first.Run.GeneratedAt);
        Assert.Equal(now, second.Run.GeneratedAt);
        Assert.Equal(1L, first.Run.Sequence);
        Assert.Equal(2L, second.Run.Sequence);
        Assert.DoesNotContain(FactualGapAnalyzer.Analyze(persisted.Evidence, persisted.Workplace, brain),
            item => item.Code == "entity-ambiguity");

        await using var matterStore = new PostgresMatterStore(database.AppConnectionString);
        await matterStore.CreateTenantAsync(tenant, "Synthetic sequence tenant", SyntheticPersistedMatterFactory.RecordedAt);
        await using var store = new PostgresMatterBrainStore(database.AppConnectionString);
        await store.SaveAsync(brain, persisted.Workplace);
        var loaded = await store.LoadAsync(tenant, persisted.Evidence.Matter.Id);

        Assert.NotNull(loaded);
        var loadedRuns = loaded.Brain.Runs.OrderBy(item => item.Sequence).ToArray();
        Assert.Equal(2, loadedRuns.Length);
        Assert.Equal([1L, 2L], loadedRuns.Select(item => item.Sequence!.Value).ToArray());
        Assert.All(loadedRuns, run => Assert.Equal(now, run.GeneratedAt));
        Assert.DoesNotContain(FactualGapAnalyzer.Analyze(loaded.Evidence, loaded.Workplace, loaded.Brain),
            item => item.Code == "entity-ambiguity");
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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
