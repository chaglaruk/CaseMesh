using System.Text;
using CaseMesh.Core.Models;
using CaseMesh.Ingestion;
using CaseMesh.Persistence.Postgres;
using Npgsql;

namespace CaseMesh.Ingestion.Tests;

[Collection(IngestionCollection.Name)]
public sealed class IngestionIntegrationTests(IngestionIntegrationFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 15, 30, 0, TimeSpan.Zero);

    [IngestionFact]
    public async Task Real_private_storage_postgres_and_clamav_round_trip_exact_source_span()
    {
        var bytes = Encoding.UTF8.GetBytes("Synthetic exact PostgreSQL source text.");
        var scope = await fixture.CreateScopeAsync(bytes);
        await using var storage = fixture.CreateStorage();
        await using var postgres = new PostgresMatterStore(fixture.AppConnection);
        var repository = new PostgresIngestionRepository(postgres);

        var result = await CreateService(storage, repository).IngestAsync(scope.Document);
        var reloaded = await repository.FindCompletedAsync(scope.Document, Pipeline("native-1").Fingerprint, default);

        Assert.NotNull(reloaded);
        Assert.Equal(result.SpanSetId, reloaded.SpanSetId);
        var span = Assert.Single(reloaded.Regions);
        Assert.Equal("Synthetic exact PostgreSQL source text.", span.Text);
        Assert.Equal(IngestionDigests.Sha256(span.Text), span.TextDigest);
        Assert.Equal(ExtractionRoute.Native, span.Route);
    }

    [IngestionFact]
    public async Task Real_scanner_quarantines_eicar_and_unavailable_scanner_fails_closed()
    {
        var eicar = Encoding.ASCII.GetBytes(string.Concat(
            "X5O!P%@AP[4\\PZX54(P^)", "7CC)7}$EICAR-STANDARD-", "ANTIVIRUS-TEST-FILE!$H+H*"));
        var scope = await fixture.CreateScopeAsync(eicar);
        await using var storage = fixture.CreateStorage();
        await using var postgres = new PostgresMatterStore(fixture.AppConnection);
        var repository = new PostgresIngestionRepository(postgres);

        var failure = await Assert.ThrowsAsync<EvidenceIngestionException>(() =>
            CreateService(storage, repository).IngestAsync(scope.Document));
        Assert.Equal(IngestionFailureKind.MalwareDetected, failure.Kind);
        Assert.Equal(IngestionStatus.Quarantined, Assert.Single(await repository.ListAttemptsAsync(scope.Document, default)).Status);

        var clean = await fixture.CreateScopeAsync(Encoding.UTF8.GetBytes("clean synthetic"));
        var unavailable = new ClamAvCliScanner("ci", TimeSpan.FromSeconds(5), "casemesh-missing-clamscan");
        var unavailableService = new CommercialEvidenceIngestionService(storage, repository, unavailable,
            new TesseractCliOcrEngine("ci", TimeSpan.FromSeconds(30)),
            new IngestionPipeline("pipeline-1", unavailable.Provider, unavailable.Version, "native-1", "tesseract-cli", "ci"),
            timeProvider: new FixedTimeProvider(Now));
        var unavailableFailure = await Assert.ThrowsAsync<EvidenceIngestionException>(() => unavailableService.IngestAsync(clean.Document));
        Assert.Equal(IngestionFailureKind.ScannerUnavailable, unavailableFailure.Kind);
    }

    [IngestionFact]
    public async Task Real_tesseract_produces_bounded_box_provenance_for_synthetic_png()
    {
        var bytes = await File.ReadAllBytesAsync(fixture.OcrImagePath);
        var scope = await fixture.CreateScopeAsync(bytes);
        await using var storage = fixture.CreateStorage();
        await using var postgres = new PostgresMatterStore(fixture.AppConnection);

        var result = await CreateService(storage, new PostgresIngestionRepository(postgres)).IngestAsync(scope.Document);

        Assert.Equal(EvidenceMediaType.Png, result.MediaType);
        Assert.NotEmpty(result.Regions);
        Assert.All(result.Regions, region =>
        {
            Assert.Equal(ExtractionRoute.Ocr, region.Route);
            Assert.Equal(SourceLocatorKind.ImageBoundingBox, region.LocatorKind);
            Assert.True(region.BoundingBoxWidth > 0);
            Assert.InRange(region.Confidence!.Value, 0m, 1m);
        });
    }

    [IngestionFact]
    public async Task Rls_wrong_and_missing_tenant_cannot_resolve_or_link_spans()
    {
        var scope = await fixture.CreateScopeAsync(Encoding.UTF8.GetBytes("tenant isolation"));
        await using var storage = fixture.CreateStorage();
        await using var postgres = new PostgresMatterStore(fixture.AppConnection);
        var repository = new PostgresIngestionRepository(postgres);
        var completed = await CreateService(storage, repository).IngestAsync(scope.Document);
        var wrong = scope.Document with { TenantId = new TenantId(Guid.NewGuid()) };

        Assert.Null(await repository.FindCompletedAsync(wrong, Pipeline("native-1").Fingerprint, default));
        await using var connection = new NpgsqlConnection(fixture.AppConnection);
        await connection.OpenAsync();
        await using (var missing = new NpgsqlCommand("SELECT count(*) FROM casemesh.ingestion_span_sets;", connection))
            Assert.Equal(0L, await missing.ExecuteScalarAsync());

        await using var transaction = await connection.BeginTransactionAsync();
        await using (var context = new NpgsqlCommand("SELECT set_config('casemesh.tenant_id',$1,true);", connection, transaction))
        {
            context.Parameters.AddWithValue(scope.Document.TenantId.Value.ToString());
            await context.ExecuteNonQueryAsync();
        }
        await using var cross = new NpgsqlCommand("""
            INSERT INTO casemesh.source_spans (
              tenant_id,matter_id,source_span_id,document_version_id,page_number,extracted_text,
              extracted_text_digest,parser_version,span_set_id,span_ordinal,locator_kind,stable_locator,
              extraction_route,extraction_provider,extraction_provider_version)
            VALUES ($1,$2,$3,$4,1,'x',$5,'x',$6,999,1,'x',1,'x','x');
            """, connection, transaction);
        cross.Parameters.AddWithValue(scope.Document.TenantId.Value);
        cross.Parameters.AddWithValue(Guid.NewGuid());
        cross.Parameters.AddWithValue(Guid.NewGuid());
        cross.Parameters.AddWithValue(scope.Document.DocumentVersionId);
        cross.Parameters.AddWithValue(IngestionDigests.Sha256("x"));
        cross.Parameters.AddWithValue(completed.SpanSetId);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => cross.ExecuteNonQueryAsync());
        Assert.Equal("23503", exception.SqlState);
        await transaction.RollbackAsync();
    }

    [IngestionFact]
    public async Task Pipeline_version_change_preserves_old_span_set_and_attempt_history_is_append_only()
    {
        var scope = await fixture.CreateScopeAsync(Encoding.UTF8.GetBytes("pipeline history"));
        await using var storage = fixture.CreateStorage();
        await using var postgres = new PostgresMatterStore(fixture.AppConnection);
        var repository = new PostgresIngestionRepository(postgres);
        var first = await CreateService(storage, repository, "native-1").IngestAsync(scope.Document);
        var retry = await CreateService(storage, repository, "native-1").IngestAsync(scope.Document);
        var changed = await CreateService(storage, repository, "native-2").IngestAsync(scope.Document);

        Assert.True(retry.WasAlreadyCompleted);
        Assert.Equal(first.SpanSetId, retry.SpanSetId);
        Assert.NotEqual(first.SpanSetId, changed.SpanSetId);
        Assert.Equal(2, (await repository.ListAttemptsAsync(scope.Document, default)).Count);

        await using var connection = new NpgsqlConnection(fixture.AppConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var context = new NpgsqlCommand("SELECT set_config('casemesh.tenant_id',$1,true);", connection, transaction))
        {
            context.Parameters.AddWithValue(scope.Document.TenantId.Value.ToString());
            await context.ExecuteNonQueryAsync();
        }
        await using var update = new NpgsqlCommand(
            "UPDATE casemesh.ingestion_attempts SET failure_code='overwrite' WHERE attempt_id=$1;", connection, transaction);
        update.Parameters.AddWithValue(first.AttemptId);
        await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
        await transaction.RollbackAsync();

        await using var spanConnection = new NpgsqlConnection(fixture.AppConnection);
        await spanConnection.OpenAsync();
        await using var spanTransaction = await spanConnection.BeginTransactionAsync();
        await using (var spanContext = new NpgsqlCommand("SELECT set_config('casemesh.tenant_id',$1,true);", spanConnection, spanTransaction))
        {
            spanContext.Parameters.AddWithValue(scope.Document.TenantId.Value.ToString());
            await spanContext.ExecuteNonQueryAsync();
        }
        await using var mutateSpan = new NpgsqlCommand(
            "UPDATE casemesh.source_spans SET extracted_text='tampered' WHERE span_set_id=$1;",
            spanConnection, spanTransaction);
        mutateSpan.Parameters.AddWithValue(first.SpanSetId);
        await Assert.ThrowsAsync<PostgresException>(() => mutateSpan.ExecuteNonQueryAsync());
        await spanTransaction.RollbackAsync();
    }

    private static CommercialEvidenceIngestionService CreateService(
        CaseMesh.Storage.IOriginalEvidenceStore storage,
        IIngestionRepository repository,
        string parserVersion = "native-1")
    {
        var pipeline = Pipeline(parserVersion);
        return new CommercialEvidenceIngestionService(storage, repository,
            new ClamAvCliScanner(pipeline.ScannerVersion, TimeSpan.FromSeconds(45),
                databasePath: Environment.GetEnvironmentVariable("CASEMESH_CLAMAV_DATABASE")),
            new TesseractCliOcrEngine(pipeline.OcrVersion, TimeSpan.FromSeconds(45)),
            pipeline, new IngestionLimits(ExternalProcessTimeout: TimeSpan.FromSeconds(45)),
            new PopplerPdfPageRasterizer("ci", TimeSpan.FromSeconds(45)), new FixedTimeProvider(Now));
    }

    private static IngestionPipeline Pipeline(string parserVersion) =>
        new("pipeline-1", "clamav-cli", "ci", parserVersion, "tesseract-cli", "ci");

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
