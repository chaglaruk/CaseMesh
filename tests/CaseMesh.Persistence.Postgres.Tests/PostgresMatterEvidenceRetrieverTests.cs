using CaseMesh.Core.Models;
using CaseMesh.Ingestion;
using CaseMesh.MatterBrain;
using CaseMesh.Qa;
using Npgsql;

namespace CaseMesh.Persistence.Postgres.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresMatterEvidenceRetrieverTests(PostgresFixture database)
{
    [PostgresFact]
    public async Task Contradictory_sickness_count_retrieval_preserves_both_sides_and_full_provenance()
    {
        var tenant = new TenantId(SyntheticPersistedMatterFactory.Id(901, 1));
        var matterId = SyntheticPersistedMatterFactory.Id(901, 2);
        var persisted = await SaveAsync(tenant, matterId, 901);
        await using var retrievalStore = new PostgresMatterStore(database.AppConnectionString);
        var retriever = new PostgresMatterEvidenceRetriever(retrievalStore);

        var results = await retriever.RetrieveAsync(new MatterRetrievalRequest(
            tenant, matterId, "Who says 12 sickness days and what conflicts with it?"));

        Assert.Contains(results, item => item.Label.Contains("12", StringComparison.Ordinal));
        Assert.Contains(results, item => item.Label.Contains("10", StringComparison.Ordinal));
        Assert.Contains(results, item => item.DisputeState == "Contradicted");
        var spans = persisted.Evidence.SourceSpans.ToDictionary(item => item.Id);
        foreach (var result in results)
        {
            var span = spans[result.SourceSpanId];
            Assert.Equal(span.DocumentVersion.DocumentVersionId, result.DocumentVersionId);
            Assert.Equal(span.DocumentVersion.OriginalObjectId, result.OriginalObjectId);
            Assert.Equal(span.DocumentVersion.ContentSha256, result.OriginalSha256);
            Assert.Equal(MatterRetrievalIdentity.Create(tenant, matterId, result.Kind,
                result.CanonicalId, result.SourceSpanId), result.Id);
        }
    }

    [PostgresFact]
    public async Task Tenant_scoped_FTS_cannot_leak_same_Matter_identity_from_another_tenant()
    {
        var matterId = SyntheticPersistedMatterFactory.Id(902, 2);
        var tenantA = new TenantId(SyntheticPersistedMatterFactory.Id(902, 1));
        var tenantB = new TenantId(SyntheticPersistedMatterFactory.Id(903, 1));
        var first = await SaveAsync(tenantA, matterId, 902);
        var second = await SaveAsync(tenantB, matterId, 902);
        await using var retrievalStore = new PostgresMatterStore(database.AppConnectionString);
        var retriever = new PostgresMatterEvidenceRetriever(retrievalStore);

        var a = await retriever.RetrieveAsync(new MatterRetrievalRequest(tenantA, matterId, "sickness days"));
        var b = await retriever.RetrieveAsync(new MatterRetrievalRequest(tenantB, matterId, "sickness days"));

        var aSpans = first.Evidence.SourceSpans.Select(item => item.Id).ToHashSet();
        var bSpans = second.Evidence.SourceSpans.Select(item => item.Id).ToHashSet();
        Assert.NotEmpty(a);
        Assert.NotEmpty(b);
        Assert.All(a, item => Assert.Contains(item.SourceSpanId, aSpans));
        Assert.All(b, item => Assert.Contains(item.SourceSpanId, bSpans));
        Assert.Equal(aSpans, bSpans);
        Assert.Empty(a.Select(item => item.Id).Intersect(b.Select(item => item.Id)));
        Assert.True(await retriever.VerifyCanonicalAsync(tenantA, matterId, a));
        Assert.True(await retriever.VerifyCanonicalAsync(tenantB, matterId, b));
        Assert.False(await retriever.VerifyCanonicalAsync(tenantA, matterId, []));
    }

    [PostgresFact]
    public async Task Missing_tenant_context_fails_closed_for_retrieval_sources()
    {
        var tenant = new TenantId(SyntheticPersistedMatterFactory.Id(904, 1));
        await SaveAsync(tenant, SyntheticPersistedMatterFactory.Id(904, 2), 904);
        await using var connection = new NpgsqlConnection(database.AppConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM casemesh.source_spans WHERE to_tsvector('simple', extracted_text) @@ to_tsquery('simple', 'sickness');",
            connection);

        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [PostgresFact]
    public async Task Retry_is_stable_and_unrelated_Matter_update_does_not_change_results()
    {
        var tenant = new TenantId(SyntheticPersistedMatterFactory.Id(905, 1));
        var firstId = SyntheticPersistedMatterFactory.Id(905, 2);
        var secondId = SyntheticPersistedMatterFactory.Id(905, 3);
        await SaveAsync(tenant, firstId, 905);
        var second = await SaveAsync(tenant, secondId, 906, createTenant: false);
        await using var retrievalStore = new PostgresMatterStore(database.AppConnectionString);
        var retriever = new PostgresMatterEvidenceRetriever(retrievalStore);
        var request = new MatterRetrievalRequest(tenant, firstId, "adjustment response implementation");
        var before = await retriever.RetrieveAsync(request);
        await using (var store = new PostgresMatterStore(database.AppConnectionString))
            Assert.True(await store.UpdateMatterAsync(tenant, secondId, "Unrelated updated Matter", "open",
                second.Evidence.Matter.UpdatedAt, second.Evidence.Matter.UpdatedAt.AddMinutes(1)));

        Assert.Equal(before, await retriever.RetrieveAsync(request));
        Assert.Equal(before, await retriever.RetrieveAsync(request));
    }

    [PostgresFact]
    public async Task Retrieval_limits_are_enforced_and_FTS_indexes_are_present()
    {
        var tenant = new TenantId(SyntheticPersistedMatterFactory.Id(907, 1));
        var matterId = SyntheticPersistedMatterFactory.Id(907, 2);
        await SaveAsync(tenant, matterId, 907);
        await using var retrievalStore = new PostgresMatterStore(database.AppConnectionString);
        var retriever = new PostgresMatterEvidenceRetriever(retrievalStore);

        var results = await retriever.RetrieveAsync(new MatterRetrievalRequest(
            tenant, matterId, "synthetic employer adjustment record", MaximumResults: 3, MaximumContextBytes: 2_048));

        Assert.InRange(results.Count, 1, 3);
        Assert.True(results.Sum(item => System.Text.Encoding.UTF8.GetByteCount(item.Label) +
                                        System.Text.Encoding.UTF8.GetByteCount(item.ContextText)) <= 2_048);
        await using var admin = new NpgsqlConnection(database.AdminConnectionString);
        await admin.OpenAsync();
        await using var indexes = new NpgsqlCommand("""
            SELECT count(*) FROM pg_indexes
            WHERE schemaname='casemesh' AND indexname LIKE '%_matter_fts_ix';
            """, admin);
        Assert.Equal(11L, await indexes.ExecuteScalarAsync());
    }

    [PostgresFact]
    public async Task Synthetic_lexical_retrieval_eval_covers_Matter_questions_without_pgvector()
    {
        var tenant = new TenantId(SyntheticPersistedMatterFactory.Id(908, 1));
        var matterId = SyntheticPersistedMatterFactory.Id(908, 2);
        await SaveAsync(tenant, matterId, 908);
        await using var retrievalStore = new PostgresMatterStore(database.AppConnectionString);
        var retriever = new PostgresMatterEvidenceRetriever(retrievalStore);
        var questions = new[]
        {
            "sickness day count", "employment working hours", "adjustment request response implementation",
            "occupational health recommendation", "grievance meeting March", "employer attendance conflict"
        };

        foreach (var question in questions)
            Assert.NotEmpty(await retriever.RetrieveAsync(new MatterRetrievalRequest(tenant, matterId, question)));
        Assert.Empty(await retriever.RetrieveAsync(new MatterRetrievalRequest(
            tenant, matterId, "zebracorn hyperspace payroll")));
    }

    [PostgresFact]
    public async Task Adding_one_source_incrementally_updates_only_its_Matter_retrieval_view()
    {
        var tenant = new TenantId(SyntheticPersistedMatterFactory.Id(909, 1));
        var changedMatterId = SyntheticPersistedMatterFactory.Id(909, 2);
        var unrelatedMatterId = SyntheticPersistedMatterFactory.Id(909, 3);
        var changed = await SaveAsync(tenant, changedMatterId, 909);
        await SaveAsync(tenant, unrelatedMatterId, 910, createTenant: false);
        await using var retrievalStore = new PostgresMatterStore(database.AppConnectionString);
        var retriever = new PostgresMatterEvidenceRetriever(retrievalStore);
        var changedRequest = new MatterRetrievalRequest(tenant, changedMatterId, "zebracorn marker");
        var unrelatedRequest = new MatterRetrievalRequest(tenant, unrelatedMatterId, "zebracorn marker");
        Assert.Empty(await retriever.RetrieveAsync(changedRequest));
        Assert.Empty(await retriever.RetrieveAsync(unrelatedRequest));

        var version = changed.Evidence.RegisterDocumentVersion(
            SyntheticPersistedMatterFactory.Id(909, 990), SyntheticPersistedMatterFactory.Id(909, 991),
            new string('9', 64), SyntheticPersistedMatterFactory.Id(909, 992));
        var source = changed.Evidence.AddSourceSpan(SyntheticPersistedMatterFactory.Id(909, 993), version,
            "Synthetic zebracorn marker appears only in this Matter.", "synthetic-parser/2", 1m,
            pageNumber: 1, textStart: 0, textEnd: 55);
        changed.Evidence.AddAssertion(SyntheticPersistedMatterFactory.Id(909, 994), "synthetic-record",
            "marker", "zebracorn", "Synthetic third-party record", SyntheticPersistedMatterFactory.RecordedAt,
            EvidenceOriginClass.IndependentThirdPartyRecord, AssertionClass.ThirdPartyAssertion,
            DisputeState.Unverified, IntegrityState.OriginalHashVerified, VerificationState.NotReviewed, source.Id);
        await using (var store = new PostgresMatterStore(database.AppConnectionString))
            await store.SaveAsync(changed.Evidence, changed.Workplace);

        Assert.NotEmpty(await retriever.RetrieveAsync(changedRequest));
        Assert.Empty(await retriever.RetrieveAsync(unrelatedRequest));
    }

    [PostgresFact]
    public async Task Current_event_backed_by_a_superseded_assertion_remains_historical_and_labelled()
    {
        var tenant = new TenantId(SyntheticPersistedMatterFactory.Id(913, 1));
        var matterId = SyntheticPersistedMatterFactory.Id(913, 2);
        var persisted = SyntheticPersistedMatterFactory.Create(tenant, matterId, 913);
        var backingAssertion = persisted.Evidence.Assertions.Single(item =>
            item.Predicate == "grievance-meeting-date" && item.Value == "12 March");
        persisted.Evidence.CorrectAssertion(backingAssertion.Id,
            SyntheticPersistedMatterFactory.Id(913, 500), "13 March",
            new DateTimeOffset(2026, 3, 13, 10, 0, 0, TimeSpan.Zero),
            SyntheticPersistedMatterFactory.Id(913, 501), "synthetic-reviewer",
            SyntheticPersistedMatterFactory.RecordedAt.AddHours(2));
        await using (var persistenceStore = new PostgresMatterStore(database.AppConnectionString))
        {
            await persistenceStore.CreateTenantAsync(tenant, "Synthetic QA tenant 913",
                SyntheticPersistedMatterFactory.RecordedAt);
            await persistenceStore.SaveAsync(persisted.Evidence, persisted.Workplace);
        }
        await using var retrievalStore = new PostgresMatterStore(database.AppConnectionString);
        var retriever = new PostgresMatterEvidenceRetriever(retrievalStore);

        var results = await retriever.RetrieveAsync(new MatterRetrievalRequest(
            tenant, matterId, "grievance meeting March"));
        var currentEvent = Assert.Single(results, item => item.Kind == RetrievalMaterialKind.Event &&
            item.Label.Contains("13 March", StringComparison.Ordinal));

        Assert.True(currentEvent.IsHistorical);
        Assert.Equal("Superseded", currentEvent.DisputeState);
        Assert.True(await retriever.VerifyCanonicalAsync(tenant, matterId, [currentEvent]));
    }

    [PostgresFact]
    public async Task Reprocessing_marks_old_span_results_historical_and_invalidates_an_in_flight_current_result()
    {
        var tenant = new TenantId(SyntheticPersistedMatterFactory.Id(911, 1));
        var matterId = SyntheticPersistedMatterFactory.Id(911, 2);
        var persisted = await SaveAsync(tenant, matterId, 911);
        var version = persisted.Evidence.DocumentVersions.First();
        var document = new IngestionDocument(tenant, matterId, version.DocumentId,
            version.DocumentVersionId, version.OriginalObjectId);
        await using var store = new PostgresMatterStore(database.AppConnectionString);
        var ingestion = new PostgresIngestionRepository(store);
        var firstText = "Synthetic staleparser workplace record.";
        var first = CompletedAttempt(document, 911, new string('A', 64), minutes: 1);
        await ingestion.SaveCompletedAsync(first, EvidenceMediaType.PlainText,
            new SpanSetProvenance("synthetic-parser", "1", null, null),
            [Region(SyntheticPersistedMatterFactory.Id(911, 20), firstText)], CancellationToken.None);
        await using var retrievalStore = new PostgresMatterStore(database.AppConnectionString);
        var retriever = new PostgresMatterEvidenceRetriever(retrievalStore);
        var original = Assert.Single(await retriever.RetrieveAsync(
            new MatterRetrievalRequest(tenant, matterId, "staleparser")), item => item.Kind == RetrievalMaterialKind.SourceSpan);
        Assert.False(original.IsHistorical);
        Assert.True(await retriever.VerifyCanonicalAsync(tenant, matterId, [original]));

        var secondText = "Synthetic freshparser workplace record.";
        var second = CompletedAttempt(document, 912, new string('B', 64), minutes: 2);
        await ingestion.SaveCompletedAsync(second, EvidenceMediaType.PlainText,
            new SpanSetProvenance("synthetic-parser", "2", null, null),
            [Region(SyntheticPersistedMatterFactory.Id(911, 21), secondText)], CancellationToken.None);

        Assert.False(await retriever.VerifyCanonicalAsync(tenant, matterId, [original]));
        var historical = Assert.Single(await retriever.RetrieveAsync(
            new MatterRetrievalRequest(tenant, matterId, "staleparser")), item => item.Kind == RetrievalMaterialKind.SourceSpan);
        var current = Assert.Single(await retriever.RetrieveAsync(
            new MatterRetrievalRequest(tenant, matterId, "freshparser")), item => item.Kind == RetrievalMaterialKind.SourceSpan);
        Assert.True(historical.IsHistorical);
        Assert.False(current.IsHistorical);
    }

    private static IngestionAttempt CompletedAttempt(
        IngestionDocument document,
        int seed,
        string fingerprint,
        int minutes)
    {
        var completed = SyntheticPersistedMatterFactory.RecordedAt.AddMinutes(minutes);
        return new IngestionAttempt(SyntheticPersistedMatterFactory.Id(seed, 30), document, fingerprint,
            completed.AddSeconds(-1), completed, IngestionStatus.Completed, EvidenceMediaType.PlainText,
            40, "synthetic-scanner", "1", "clean", null, null,
            SyntheticPersistedMatterFactory.Id(seed, 31));
    }

    private static ExtractedRegion Region(Guid id, string text) => new(
        id, 0, SourceLocatorKind.TextCharacters, $"characters:0-{text.Length}", text,
        IngestionDigests.Sha256(text), ExtractionRoute.Native, "synthetic-parser", "1",
        TextStart: 0, TextEnd: text.Length, Confidence: 1m);

    private async Task<PersistedMatter> SaveAsync(
        TenantId tenant,
        Guid matterId,
        int seed,
        bool createTenant = true)
    {
        var persisted = SyntheticPersistedMatterFactory.Create(tenant, matterId, seed);
        await using var store = new PostgresMatterStore(database.AppConnectionString);
        if (createTenant)
            await store.CreateTenantAsync(tenant, $"Synthetic QA tenant {seed}", SyntheticPersistedMatterFactory.RecordedAt);
        await store.SaveAsync(persisted.Evidence, persisted.Workplace);
        return persisted;
    }
}
