using System.Text;
using CaseMesh.Core.Models;
using CaseMesh.Ingestion;
using CaseMesh.Persistence.Postgres;
using CaseMesh.ProfessionalExport;
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

        await using var connection = new NpgsqlConnection(fixture.AppConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, transaction, scope.Document.TenantId);
        await using var provenance = new NpgsqlCommand("""
            SELECT span_sets.parser_provider, span_sets.ocr_provider,
                   spans.parser_version, spans.extraction_provider_version
            FROM casemesh.ingestion_span_sets span_sets
            JOIN casemesh.source_spans spans
              ON spans.tenant_id=span_sets.tenant_id AND spans.matter_id=span_sets.matter_id
             AND spans.span_set_id=span_sets.span_set_id
            WHERE span_sets.tenant_id=$1 AND span_sets.matter_id=$2 AND span_sets.span_set_id=$3
            LIMIT 1;
            """, connection, transaction);
        provenance.Parameters.AddWithValue(scope.Document.TenantId.Value);
        provenance.Parameters.AddWithValue(scope.Document.MatterId);
        provenance.Parameters.AddWithValue(result.SpanSetId);
        await using var reader = await provenance.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("none", reader.GetString(0));
        Assert.Equal("tesseract-cli", reader.GetString(1));
        Assert.Equal("native-1", reader.GetString(2));
        Assert.Equal("ci", reader.GetString(3));
        await reader.DisposeAsync();
        await transaction.RollbackAsync();
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
        await SetTenantAsync(connection, transaction, scope.Document.TenantId);
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
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
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

        await using (var exportService = new PostgresProfessionalExportService(
                         fixture.AppConnection, new FixedTimeProvider(Now.AddHours(1))))
        {
            var package = Assert.IsType<ProfessionalExportPackage>(await exportService.GenerateAsync(
                new ProfessionalExportRequest(scope.Document.TenantId, scope.Document.MatterId, Guid.NewGuid())));
            var document = Assert.Single(package.Manifest.Documents);
            Assert.Equal(["native-2"], document.ParserVersions);
            Assert.DoesNotContain("native-1", document.ParserVersions);
            Assert.Contains(package.Manifest.Sources, source =>
                source.ExtractionProviderVersion == "native-1" && !string.IsNullOrWhiteSpace(source.StableLocator));
            Assert.Contains(package.Manifest.Sources, source =>
                source.ExtractionProviderVersion == "native-2" && !string.IsNullOrWhiteSpace(source.StableLocator));
        }

        await using var connection = new NpgsqlConnection(fixture.AppConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, transaction, scope.Document.TenantId);
        await using var update = new NpgsqlCommand(
            "UPDATE casemesh.ingestion_attempts SET failure_code='overwrite' WHERE attempt_id=$1;", connection, transaction);
        update.Parameters.AddWithValue(first.AttemptId);
        await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
        await transaction.RollbackAsync();

        await using var spanConnection = new NpgsqlConnection(fixture.AppConnection);
        await spanConnection.OpenAsync();
        await using var spanTransaction = await spanConnection.BeginTransactionAsync();
        await SetTenantAsync(spanConnection, spanTransaction, scope.Document.TenantId);
        await using var mutateSpan = new NpgsqlCommand(
            "UPDATE casemesh.source_spans SET extracted_text='tampered' WHERE span_set_id=$1;",
            spanConnection, spanTransaction);
        mutateSpan.Parameters.AddWithValue(first.SpanSetId);
        await Assert.ThrowsAsync<PostgresException>(() => mutateSpan.ExecuteNonQueryAsync());
        await spanTransaction.RollbackAsync();
    }

    [IngestionFact]
    public async Task Failed_new_pipeline_preserves_the_last_completed_span_set_pointer()
    {
        var scope = await fixture.CreateScopeAsync(Encoding.UTF8.GetBytes("last known good spans"));
        await using var storage = fixture.CreateStorage();
        await using var postgres = new PostgresMatterStore(fixture.AppConnection);
        var repository = new PostgresIngestionRepository(postgres);
        var completed = await CreateService(storage, repository, "native-1").IngestAsync(scope.Document);
        var failedPipeline = Pipeline("native-2");
        var unavailable = new ClamAvCliScanner(failedPipeline.ScannerVersion, TimeSpan.FromSeconds(5),
            "casemesh-missing-clamscan");
        var failingService = new CommercialEvidenceIngestionService(storage, repository, unavailable,
            new TesseractCliOcrEngine(failedPipeline.OcrVersion, TimeSpan.FromSeconds(5)), failedPipeline,
            timeProvider: new FixedTimeProvider(Now));

        var failure = await Assert.ThrowsAsync<EvidenceIngestionException>(() =>
            failingService.IngestAsync(scope.Document));

        Assert.Equal(IngestionFailureKind.ScannerUnavailable, failure.Kind);
        Assert.NotNull(await repository.FindCompletedAsync(scope.Document, Pipeline("native-1").Fingerprint, default));
        Assert.Equal(2, (await repository.ListAttemptsAsync(scope.Document, default)).Count);
        await using var connection = new NpgsqlConnection(fixture.AppConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, transaction, scope.Document.TenantId);
        await using var state = new NpgsqlCommand("""
            SELECT status, current_span_set_id
            FROM casemesh.document_ingestion_state
            WHERE tenant_id=$1 AND matter_id=$2 AND document_version_id=$3;
            """, connection, transaction);
        state.Parameters.AddWithValue(scope.Document.TenantId.Value);
        state.Parameters.AddWithValue(scope.Document.MatterId);
        state.Parameters.AddWithValue(scope.Document.DocumentVersionId);
        await using var reader = await state.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal((short)IngestionStatus.Failed, reader.GetInt16(0));
        Assert.Equal(completed.SpanSetId, reader.GetGuid(1));
        await reader.DisposeAsync();
        await transaction.RollbackAsync();
    }

    [IngestionFact]
    public async Task Version_scoped_foreign_keys_reject_cross_document_span_set_links()
    {
        var first = await fixture.CreateScopeAsync(Encoding.UTF8.GetBytes("first document"));
        var second = await fixture.AddDocumentVersionAsync(first, Encoding.UTF8.GetBytes("second document"));
        await using var storage = fixture.CreateStorage();
        await using var postgres = new PostgresMatterStore(fixture.AppConnection);
        var repository = new PostgresIngestionRepository(postgres);
        var firstCompleted = await CreateService(storage, repository).IngestAsync(first.Document);
        var secondCompleted = await CreateService(storage, repository).IngestAsync(second.Document);

        await using var connection = new NpgsqlConnection(fixture.AppConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, transaction, first.Document.TenantId);
        await using var cross = new NpgsqlCommand("""
            UPDATE casemesh.document_ingestion_state
            SET current_span_set_id=$1
            WHERE tenant_id=$2 AND matter_id=$3 AND document_version_id=$4;
            """, connection, transaction);
        cross.Parameters.AddWithValue(firstCompleted.SpanSetId);
        cross.Parameters.AddWithValue(second.Document.TenantId.Value);
        cross.Parameters.AddWithValue(second.Document.MatterId);
        cross.Parameters.AddWithValue(second.Document.DocumentVersionId);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => cross.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        Assert.NotEqual(firstCompleted.SpanSetId, secondCompleted.SpanSetId);
        await transaction.RollbackAsync();
    }

    private static async Task SetTenantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TenantId tenantId)
    {
        await using var context = new NpgsqlCommand(
            "SELECT set_config('casemesh.tenant_id',$1,true);", connection, transaction);
        context.Parameters.AddWithValue(tenantId.Value.ToString());
        await context.ExecuteNonQueryAsync();
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
