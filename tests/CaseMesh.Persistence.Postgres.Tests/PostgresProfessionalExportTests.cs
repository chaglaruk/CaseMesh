using System.Text.Json;
using CaseMesh.Core.Models;
using CaseMesh.MatterBrain;
using CaseMesh.ProfessionalExport;
using Npgsql;

namespace CaseMesh.Persistence.Postgres.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresProfessionalExportTests(PostgresFixture database)
{
    private static readonly DateTimeOffset ExportedAt =
        SyntheticPersistedMatterFactory.RecordedAt.AddDays(1);

    [PostgresFact]
    public async Task Complete_export_round_trips_immutable_audit_metadata_and_returns_payloads()
    {
        var (tenant, persisted) = await PersistMatterAsync(901);
        var exportId = SyntheticPersistedMatterFactory.Id(901, 500);

        await using var service = Service(ExportedAt);
        var package = Assert.IsType<ProfessionalExportPackage>(await service.GenerateAsync(
            new ProfessionalExportRequest(tenant, persisted.Evidence.Matter.Id, exportId)));
        var loaded = Assert.IsType<PersistedProfessionalExportRun>(await service.GetRunAsync(
            tenant, persisted.Evidence.Matter.Id, exportId));

        Assert.Equal(8, package.Artifacts.Count);
        Assert.Contains(package.Artifacts, item => item.Kind == ProfessionalExportArtifactKind.BriefDocx);
        Assert.Contains(package.Artifacts, item => item.Kind == ProfessionalExportArtifactKind.BundleZip);
        Assert.Equal(JsonSerializer.Serialize(package.Run), JsonSerializer.Serialize(loaded.Run));
        Assert.Equal(
            persisted.Evidence.DocumentVersions.Select(item => item.DocumentVersionId).Order().ToArray(),
            loaded.Run.DocumentVersionIds.Order().ToArray());
        Assert.Equal(
            persisted.Evidence.SourceSpans.Select(item => item.Id).Order().ToArray(),
            loaded.Run.SourceSpanIds.Order().ToArray());
        Assert.Equal(
            persisted.Evidence.Assertions.Select(item => item.Id).Order().ToArray(),
            loaded.Run.AssertionIds.Order().ToArray());
        Assert.Equal(
            persisted.Evidence.Events.Select(item => item.Id).Order().ToArray(),
            loaded.Run.EventIds.Order().ToArray());
        Assert.Equal(
            persisted.Evidence.Contradictions.Select(item => item.Id).Order().ToArray(),
            loaded.Run.ContradictionIds.Order().ToArray());
    }

    [PostgresFact]
    public async Task Wrong_or_missing_tenant_context_cannot_generate_resolve_or_list_export_runs()
    {
        var (tenantA, persisted) = await PersistMatterAsync(902);
        var tenantB = new TenantId(SyntheticPersistedMatterFactory.Id(902, 900));
        await using (var matterStore = new PostgresMatterStore(database.AppConnectionString))
        {
            await matterStore.CreateTenantAsync(tenantB, "Synthetic tenant B", ExportedAt);
        }
        var request = new ProfessionalExportRequest(
            tenantA, persisted.Evidence.Matter.Id, SyntheticPersistedMatterFactory.Id(902, 500));

        await using var service = Service(ExportedAt);
        Assert.NotNull(await service.GenerateAsync(request));
        Assert.Null(await service.GenerateAsync(request with { TenantId = tenantB }));
        Assert.Null(await service.GetRunAsync(tenantB, request.MatterId, request.ExportId));

        await using var connection = new NpgsqlConnection(database.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM casemesh.professional_export_runs;", connection, transaction);
        Assert.Equal(0L, await command.ExecuteScalarAsync());
        await transaction.RollbackAsync();
    }

    [PostgresFact]
    public async Task Same_Matter_id_in_two_tenants_has_independent_export_identity_and_state()
    {
        var sharedMatterId = SyntheticPersistedMatterFactory.Id(903, 2);
        var (tenantA, matterA) = await PersistMatterAsync(903, sharedMatterId);
        var (tenantB, matterB) = await PersistMatterAsync(904, sharedMatterId);
        var sharedExportId = SyntheticPersistedMatterFactory.Id(903, 500);

        await using var service = Service(ExportedAt);
        var packageA = Assert.IsType<ProfessionalExportPackage>(await service.GenerateAsync(
            new ProfessionalExportRequest(tenantA, sharedMatterId, sharedExportId)));
        var packageB = Assert.IsType<ProfessionalExportPackage>(await service.GenerateAsync(
            new ProfessionalExportRequest(tenantB, sharedMatterId, sharedExportId)));

        Assert.NotEqual(packageA.Run.SnapshotDigest, packageB.Run.SnapshotDigest);
        Assert.NotNull(await service.GetRunAsync(tenantA, matterA.Evidence.Matter.Id, sharedExportId));
        Assert.NotNull(await service.GetRunAsync(tenantB, matterB.Evidence.Matter.Id, sharedExportId));
    }

    [PostgresFact]
    public async Task Retry_is_idempotent_and_same_export_identity_rejects_divergence()
    {
        var (tenant, persisted) = await PersistMatterAsync(905);
        var request = new ProfessionalExportRequest(
            tenant, persisted.Evidence.Matter.Id, SyntheticPersistedMatterFactory.Id(905, 500));

        await using (var service = Service(ExportedAt))
        {
            var first = Assert.IsType<ProfessionalExportPackage>(await service.GenerateAsync(request));
            var retry = Assert.IsType<ProfessionalExportPackage>(await service.GenerateAsync(request));
            Assert.Equal(first.Run.ArtifactManifestDigest, retry.Run.ArtifactManifestDigest);
            Assert.All(first.Artifacts, artifact =>
                Assert.Equal(artifact.Content, retry.Artifacts.Single(item => item.Kind == artifact.Kind).Content));
        }

        await using var divergentService = Service(ExportedAt.AddMinutes(1));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            divergentService.GenerateAsync(request));
        Assert.Contains("cannot overwrite divergent", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [PostgresFact]
    public async Task Composite_foreign_keys_reject_cross_tenant_export_inclusions()
    {
        var sharedMatterId = SyntheticPersistedMatterFactory.Id(906, 2);
        var (tenantA, matterA) = await PersistMatterAsync(906, sharedMatterId);
        var (_, matterB) = await PersistMatterAsync(907, sharedMatterId);
        var exportId = SyntheticPersistedMatterFactory.Id(906, 500);
        await using (var service = Service(ExportedAt))
        {
            Assert.NotNull(await service.GenerateAsync(
                new ProfessionalExportRequest(tenantA, matterA.Evidence.Matter.Id, exportId)));
        }

        await using var admin = new NpgsqlConnection(database.AdminConnectionString);
        await admin.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO casemesh.professional_export_inclusions
                (tenant_id,matter_id,export_id,inclusion_kind,ordinal,assertion_id)
            VALUES ($1,$2,$3,2,999,$4);
            """, admin);
        command.Parameters.AddWithValue(tenantA.Value);
        command.Parameters.AddWithValue(sharedMatterId);
        command.Parameters.AddWithValue(exportId);
        command.Parameters.AddWithValue(matterB.Evidence.Assertions.First().Id);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    [PostgresFact]
    public async Task Export_audit_rows_reject_mutation_and_truncate_but_allow_whole_Matter_cascade()
    {
        var (tenant, persisted) = await PersistMatterAsync(908);
        var exportId = SyntheticPersistedMatterFactory.Id(908, 500);
        await using (var service = Service(ExportedAt))
        {
            Assert.NotNull(await service.GenerateAsync(
                new ProfessionalExportRequest(tenant, persisted.Evidence.Matter.Id, exportId)));
        }

        await AssertAppendOnlyAsync(
            "UPDATE casemesh.professional_export_runs SET template_version='changed' WHERE export_id=$1;",
            exportId);
        await AssertAppendOnlyAsync(
            "DELETE FROM casemesh.professional_export_runs WHERE export_id=$1;",
            exportId);
        await AssertAppendOnlyAsync("TRUNCATE casemesh.professional_export_artifacts CASCADE;", null);

        await using (var matterStore = new PostgresMatterStore(database.AppConnectionString))
        {
            Assert.True(await matterStore.DeleteMatterAsync(tenant, persisted.Evidence.Matter.Id));
        }
        await using var verifyingService = Service(ExportedAt);
        Assert.Null(await verifyingService.GetRunAsync(tenant, persisted.Evidence.Matter.Id, exportId));
    }

    [PostgresFact]
    public async Task Export_audit_tables_store_only_metadata_not_generated_payload_or_evidence_text()
    {
        var (tenant, persisted) = await PersistMatterAsync(909);
        var exportId = SyntheticPersistedMatterFactory.Id(909, 500);
        await using (var service = Service(ExportedAt))
        {
            Assert.NotNull(await service.GenerateAsync(
                new ProfessionalExportRequest(tenant, persisted.Evidence.Matter.Id, exportId)));
        }

        await using var admin = new NpgsqlConnection(database.AdminConnectionString);
        await admin.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT count(*)
            FROM information_schema.columns
            WHERE table_schema='casemesh'
              AND table_name IN ('professional_export_runs','professional_export_inclusions','professional_export_artifacts')
              AND (data_type='bytea' OR column_name IN ('content','payload','evidence_text','storage_key'));
            """, admin);
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    private async Task<(TenantId Tenant, PersistedMatter Matter)> PersistMatterAsync(
        int seed,
        Guid? matterId = null)
    {
        var tenant = new TenantId(SyntheticPersistedMatterFactory.Id(seed, 1));
        var persisted = SyntheticPersistedMatterFactory.Create(
            tenant, matterId ?? SyntheticPersistedMatterFactory.Id(seed, 2), seed);
        await using (var matterStore = new PostgresMatterStore(database.AppConnectionString))
        {
            await matterStore.CreateTenantAsync(tenant, $"Synthetic export tenant {seed}", ExportedAt);
        }
        await using (var brainStore = new PostgresMatterBrainStore(database.AppConnectionString))
        {
            await brainStore.SaveAsync(new MatterBrainState(persisted.Evidence), persisted.Workplace);
        }
        return (tenant, persisted);
    }

    private PostgresProfessionalExportService Service(DateTimeOffset timestamp) =>
        new(database.AppConnectionString, new FixedTimeProvider(timestamp));

    private async Task AssertAppendOnlyAsync(string sql, Guid? exportId)
    {
        await using var admin = new NpgsqlConnection(database.AdminConnectionString);
        await admin.OpenAsync();
        await using var command = new NpgsqlCommand(sql, admin);
        if (exportId.HasValue)
        {
            command.Parameters.AddWithValue(exportId.Value);
        }
        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Contains("append-only", exception.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
