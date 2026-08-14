using CaseMesh.Core.Models;
using CaseMesh.Ingestion;
using Npgsql;

namespace CaseMesh.Persistence.Postgres;

public sealed class PostgresIngestionRepository : IIngestionRepository
{
    private readonly PostgresMatterStore _store;

    public PostgresIngestionRepository(PostgresMatterStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task<CompletedIngestion?> FindCompletedAsync(
        IngestionDocument document,
        string pipelineFingerprint,
        CancellationToken cancellationToken) =>
        _store.InTenantTransactionAsync(document.TenantId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                SELECT span_set_id, detected_media_type
                FROM casemesh.ingestion_span_sets
                WHERE tenant_id = $1 AND matter_id = $2 AND document_version_id = $3
                  AND pipeline_fingerprint = $4;
                """, connection, transaction);
            PostgresMatterStore.AddParameters(command, document.TenantId.Value, document.MatterId,
                document.DocumentVersionId, pipelineFingerprint);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            var spanSetId = reader.GetGuid(0);
            var mediaType = ReadEnum<EvidenceMediaType>(reader.GetInt16(1), "detected media type");
            await reader.DisposeAsync();
            var regions = await ReadRegionsAsync(connection, transaction, document, spanSetId, cancellationToken);
            var attemptId = await ReadLatestAttemptIdAsync(connection, transaction, document, spanSetId, cancellationToken);
            var byteLength = await ReadByteLengthAsync(connection, transaction, document, cancellationToken);
            return new CompletedIngestion(attemptId, spanSetId, mediaType, byteLength,
                pipelineFingerprint, regions, true);
        }, cancellationToken);

    public Task<CompletedIngestion> SaveCompletedAsync(
        IngestionAttempt attempt,
        EvidenceMediaType mediaType,
        string parserProvider,
        string parserVersion,
        string? ocrProvider,
        string? ocrVersion,
        IReadOnlyList<ExtractedRegion> regions,
        CancellationToken cancellationToken) =>
        _store.InTenantTransactionAsync(attempt.Document.TenantId, async (connection, transaction) =>
        {
            RequireCompleted(attempt);
            await LockPipelineAsync(connection, transaction, attempt.Document, attempt.PipelineFingerprint, cancellationToken);
            await RequireDocumentIdentityAsync(connection, transaction, attempt.Document, cancellationToken);

            var insertedSet = await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                INSERT INTO casemesh.ingestion_span_sets (
                    tenant_id, matter_id, document_id, document_version_id, span_set_id,
                    pipeline_fingerprint, detected_media_type, parser_provider, parser_version,
                    ocr_provider, ocr_version, created_at)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)
                ON CONFLICT DO NOTHING;
                """, cancellationToken,
                attempt.Document.TenantId.Value, attempt.Document.MatterId, attempt.Document.DocumentId,
                attempt.Document.DocumentVersionId, attempt.SpanSetId!.Value, attempt.PipelineFingerprint,
                (short)mediaType, parserProvider, parserVersion, ocrProvider, ocrVersion, attempt.CompletedAt);

            if (insertedSet == 0)
                await RequireMatchingSpanSetAsync(connection, transaction, attempt, mediaType, parserProvider,
                    parserVersion, ocrProvider, ocrVersion, cancellationToken);

            foreach (var region in regions.OrderBy(item => item.Ordinal))
                await SaveRegionAsync(connection, transaction, attempt, region, cancellationToken);

            await SaveAttemptAsync(connection, transaction, attempt, cancellationToken);
            await SaveCurrentStateAsync(connection, transaction, attempt, cancellationToken);
            return new CompletedIngestion(attempt.AttemptId, attempt.SpanSetId.Value, mediaType,
                attempt.ByteLength, attempt.PipelineFingerprint, regions.ToArray(), insertedSet == 0);
        }, cancellationToken);

    public Task SaveFailureAsync(IngestionAttempt attempt, CancellationToken cancellationToken) =>
        _store.InTenantTransactionAsync(attempt.Document.TenantId, async (connection, transaction) =>
        {
            if (attempt.Status is not (IngestionStatus.Failed or IngestionStatus.Quarantined) ||
                attempt.FailureKind is null || string.IsNullOrWhiteSpace(attempt.FailureCode) ||
                attempt.SpanSetId is not null)
                throw new ArgumentException("A persisted ingestion failure requires a typed failure and no span set.");
            await RequireDocumentIdentityAsync(connection, transaction, attempt.Document, cancellationToken);
            await SaveAttemptAsync(connection, transaction, attempt, cancellationToken);
            await SaveCurrentStateAsync(connection, transaction, attempt, cancellationToken);
            return true;
        }, cancellationToken);

    public Task<IReadOnlyList<IngestionAttempt>> ListAttemptsAsync(
        IngestionDocument document,
        CancellationToken cancellationToken) =>
        _store.InTenantTransactionAsync(document.TenantId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                SELECT attempt_id, pipeline_fingerprint, started_at, completed_at, status,
                       detected_media_type, byte_length, scanner_provider, scanner_version,
                       scanner_result, failure_kind, failure_code, span_set_id
                FROM casemesh.ingestion_attempts
                WHERE tenant_id = $1 AND matter_id = $2 AND document_id = $3
                  AND document_version_id = $4 AND original_object_id = $5
                ORDER BY started_at, attempt_id;
                """, connection, transaction);
            PostgresMatterStore.AddParameters(command, document.TenantId.Value, document.MatterId,
                document.DocumentId, document.DocumentVersionId, document.OriginalObjectId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var attempts = new List<IngestionAttempt>();
            while (await reader.ReadAsync(cancellationToken))
            {
                attempts.Add(new IngestionAttempt(reader.GetGuid(0), document, reader.GetString(1),
                    reader.GetFieldValue<DateTimeOffset>(2), reader.GetFieldValue<DateTimeOffset>(3),
                    ReadEnum<IngestionStatus>(reader.GetInt16(4), "ingestion status"),
                    reader.IsDBNull(5) ? null : ReadEnum<EvidenceMediaType>(reader.GetInt16(5), "detected media type"),
                    reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : ReadEnum<IngestionFailureKind>(reader.GetInt16(10), "failure kind"),
                    reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetGuid(12)));
            }
            return (IReadOnlyList<IngestionAttempt>)attempts;
        }, cancellationToken);

    private static async Task SaveRegionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IngestionAttempt attempt,
        ExtractedRegion region,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(region.TextDigest, IngestionDigests.Sha256(region.Text), StringComparison.Ordinal))
            throw new InvalidOperationException("An extracted region cannot be persisted with a divergent text digest.");
        var count = await PostgresMatterStore.ExecuteAsync(connection, transaction, """
            INSERT INTO casemesh.source_spans (
                tenant_id, matter_id, source_span_id, document_version_id, page_number,
                text_start, text_end, extracted_text, extracted_text_digest, parser_version,
                extraction_confidence, span_set_id, span_ordinal, locator_kind, stable_locator,
                extraction_route, extraction_provider, extraction_provider_version,
                bbox_left, bbox_top, bbox_width, bbox_height)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21,$22)
            ON CONFLICT (tenant_id, matter_id, source_span_id) DO UPDATE
            SET source_span_id = EXCLUDED.source_span_id
            WHERE casemesh.source_spans.document_version_id = EXCLUDED.document_version_id
              AND casemesh.source_spans.page_number IS NOT DISTINCT FROM EXCLUDED.page_number
              AND casemesh.source_spans.text_start IS NOT DISTINCT FROM EXCLUDED.text_start
              AND casemesh.source_spans.text_end IS NOT DISTINCT FROM EXCLUDED.text_end
              AND casemesh.source_spans.extracted_text = EXCLUDED.extracted_text
              AND casemesh.source_spans.extracted_text_digest = EXCLUDED.extracted_text_digest
              AND casemesh.source_spans.parser_version = EXCLUDED.parser_version
              AND casemesh.source_spans.extraction_confidence IS NOT DISTINCT FROM EXCLUDED.extraction_confidence
              AND casemesh.source_spans.span_set_id = EXCLUDED.span_set_id
              AND casemesh.source_spans.span_ordinal = EXCLUDED.span_ordinal
              AND casemesh.source_spans.locator_kind = EXCLUDED.locator_kind
              AND casemesh.source_spans.stable_locator = EXCLUDED.stable_locator
              AND casemesh.source_spans.extraction_route = EXCLUDED.extraction_route
              AND casemesh.source_spans.extraction_provider = EXCLUDED.extraction_provider
              AND casemesh.source_spans.extraction_provider_version = EXCLUDED.extraction_provider_version
              AND casemesh.source_spans.bbox_left IS NOT DISTINCT FROM EXCLUDED.bbox_left
              AND casemesh.source_spans.bbox_top IS NOT DISTINCT FROM EXCLUDED.bbox_top
              AND casemesh.source_spans.bbox_width IS NOT DISTINCT FROM EXCLUDED.bbox_width
              AND casemesh.source_spans.bbox_height IS NOT DISTINCT FROM EXCLUDED.bbox_height;
            """, cancellationToken,
            attempt.Document.TenantId.Value, attempt.Document.MatterId, region.SourceSpanId,
            attempt.Document.DocumentVersionId, region.PageNumber, region.TextStart, region.TextEnd,
            region.Text, region.TextDigest, region.ProviderVersion, region.Confidence,
            attempt.SpanSetId!.Value, region.Ordinal, (short)region.LocatorKind, region.Locator,
            (short)region.Route, region.Provider, region.ProviderVersion, region.BoundingBoxLeft,
            region.BoundingBoxTop, region.BoundingBoxWidth, region.BoundingBoxHeight);
        if (count != 1)
            throw new InvalidOperationException("A source-span identity cannot overwrite divergent ingestion provenance.");
    }

    private static Task SaveAttemptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IngestionAttempt attempt,
        CancellationToken cancellationToken) => PostgresMatterStore.ExecuteAsync(connection, transaction, """
            INSERT INTO casemesh.ingestion_attempts (
                tenant_id, matter_id, document_id, document_version_id, original_object_id,
                attempt_id, pipeline_fingerprint, started_at, completed_at, status,
                detected_media_type, byte_length, scanner_provider, scanner_version,
                scanner_result, failure_kind, failure_code, span_set_id)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18);
            """, cancellationToken,
            attempt.Document.TenantId.Value, attempt.Document.MatterId, attempt.Document.DocumentId,
            attempt.Document.DocumentVersionId, attempt.Document.OriginalObjectId, attempt.AttemptId,
            attempt.PipelineFingerprint, attempt.StartedAt, attempt.CompletedAt, (short)attempt.Status,
            attempt.DetectedMediaType is null ? null : (short)attempt.DetectedMediaType.Value,
            attempt.ByteLength, attempt.ScannerProvider, attempt.ScannerVersion, attempt.ScannerResult,
            attempt.FailureKind is null ? null : (short)attempt.FailureKind.Value,
            attempt.FailureCode, attempt.SpanSetId);

    private static async Task SaveCurrentStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IngestionAttempt attempt,
        CancellationToken cancellationToken)
    {
        var count = await PostgresMatterStore.ExecuteAsync(connection, transaction, """
            INSERT INTO casemesh.document_ingestion_state (
                tenant_id, matter_id, document_id, document_version_id, original_object_id,
                detected_media_type, byte_length, status, quarantined, latest_attempt_id,
                current_span_set_id, updated_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)
            ON CONFLICT (tenant_id, matter_id, document_version_id) DO UPDATE
            SET detected_media_type = EXCLUDED.detected_media_type,
                byte_length = EXCLUDED.byte_length,
                status = EXCLUDED.status,
                quarantined = EXCLUDED.quarantined,
                latest_attempt_id = EXCLUDED.latest_attempt_id,
                current_span_set_id = CASE
                    WHEN EXCLUDED.status = 4 THEN COALESCE(
                        EXCLUDED.current_span_set_id,
                        casemesh.document_ingestion_state.current_span_set_id)
                    ELSE EXCLUDED.current_span_set_id
                END,
                updated_at = EXCLUDED.updated_at
            WHERE casemesh.document_ingestion_state.document_id = EXCLUDED.document_id
              AND casemesh.document_ingestion_state.original_object_id = EXCLUDED.original_object_id
              AND casemesh.document_ingestion_state.updated_at <= EXCLUDED.updated_at;
            """, cancellationToken,
            attempt.Document.TenantId.Value, attempt.Document.MatterId, attempt.Document.DocumentId,
            attempt.Document.DocumentVersionId, attempt.Document.OriginalObjectId,
            attempt.DetectedMediaType is null ? null : (short)attempt.DetectedMediaType.Value,
            attempt.ByteLength, (short)attempt.Status, attempt.Status == IngestionStatus.Quarantined,
            attempt.AttemptId, attempt.SpanSetId, attempt.CompletedAt);
        if (count != 1)
            throw new InvalidOperationException("A stale ingestion attempt cannot overwrite newer document processing state.");
    }

    private static async Task RequireDocumentIdentityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IngestionDocument document,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT 1 FROM casemesh.document_versions
            WHERE tenant_id = $1 AND matter_id = $2 AND document_id = $3
              AND document_version_id = $4 AND original_object_id = $5;
            """, connection, transaction);
        PostgresMatterStore.AddParameters(command, document.TenantId.Value, document.MatterId,
            document.DocumentId, document.DocumentVersionId, document.OriginalObjectId);
        if (await command.ExecuteScalarAsync(cancellationToken) is not int)
            throw new InvalidOperationException("The tenant-scoped immutable document version was not found.");
    }

    private static async Task LockPipelineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IngestionDocument document,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 0));", connection, transaction);
        command.Parameters.AddWithValue(
            $"casemesh:ingestion:{document.TenantId.Value:D}:{document.MatterId:D}:{document.DocumentVersionId:D}:{fingerprint}");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RequireMatchingSpanSetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IngestionAttempt attempt,
        EvidenceMediaType mediaType,
        string parserProvider,
        string parserVersion,
        string? ocrProvider,
        string? ocrVersion,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT 1 FROM casemesh.ingestion_span_sets
            WHERE tenant_id=$1 AND matter_id=$2 AND document_id=$3 AND document_version_id=$4
              AND span_set_id=$5 AND pipeline_fingerprint=$6 AND detected_media_type=$7
              AND parser_provider=$8 AND parser_version=$9
              AND ocr_provider IS NOT DISTINCT FROM $10 AND ocr_version IS NOT DISTINCT FROM $11;
            """, connection, transaction);
        PostgresMatterStore.AddParameters(command, attempt.Document.TenantId.Value, attempt.Document.MatterId,
            attempt.Document.DocumentId, attempt.Document.DocumentVersionId, attempt.SpanSetId!.Value,
            attempt.PipelineFingerprint, (short)mediaType, parserProvider, parserVersion, ocrProvider, ocrVersion);
        if (await command.ExecuteScalarAsync(cancellationToken) is not int)
            throw new InvalidOperationException("The versioned ingestion span-set identity has divergent metadata.");
    }

    private static async Task<IReadOnlyList<ExtractedRegion>> ReadRegionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IngestionDocument document,
        Guid spanSetId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT source_span_id, span_ordinal, locator_kind, stable_locator, extracted_text,
                   extracted_text_digest, extraction_route, extraction_provider,
                   extraction_provider_version, page_number, text_start, text_end,
                   extraction_confidence, bbox_left, bbox_top, bbox_width, bbox_height
            FROM casemesh.source_spans
            WHERE tenant_id=$1 AND matter_id=$2 AND document_version_id=$3 AND span_set_id=$4
            ORDER BY span_ordinal;
            """, connection, transaction);
        PostgresMatterStore.AddParameters(command, document.TenantId.Value, document.MatterId,
            document.DocumentVersionId, spanSetId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var regions = new List<ExtractedRegion>();
        while (await reader.ReadAsync(cancellationToken))
        {
            regions.Add(new ExtractedRegion(reader.GetGuid(0), reader.GetInt32(1),
                ReadEnum<SourceLocatorKind>(reader.GetInt16(2), "source locator kind"), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), ReadEnum<ExtractionRoute>(reader.GetInt16(6), "extraction route"),
                reader.GetString(7), reader.GetString(8), NullableInt(reader, 9), NullableInt(reader, 10),
                NullableInt(reader, 11), reader.IsDBNull(12) ? null : reader.GetDecimal(12), NullableInt(reader, 13),
                NullableInt(reader, 14), NullableInt(reader, 15), NullableInt(reader, 16)));
        }
        return regions;
    }

    private static async Task<Guid> ReadLatestAttemptIdAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, IngestionDocument document,
        Guid spanSetId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT attempt_id FROM casemesh.ingestion_attempts
            WHERE tenant_id=$1 AND matter_id=$2 AND document_version_id=$3 AND span_set_id=$4
            ORDER BY completed_at DESC, attempt_id DESC LIMIT 1;
            """, connection, transaction);
        PostgresMatterStore.AddParameters(command, document.TenantId.Value, document.MatterId,
            document.DocumentVersionId, spanSetId);
        return await command.ExecuteScalarAsync(cancellationToken) is Guid id ? id : Guid.Empty;
    }

    private static async Task<long> ReadByteLengthAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, IngestionDocument document,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT byte_length FROM casemesh.document_ingestion_state
            WHERE tenant_id=$1 AND matter_id=$2 AND document_version_id=$3;
            """, connection, transaction);
        PostgresMatterStore.AddParameters(command, document.TenantId.Value, document.MatterId,
            document.DocumentVersionId);
        return await command.ExecuteScalarAsync(cancellationToken) is long value ? value : 0;
    }

    private static void RequireCompleted(IngestionAttempt attempt)
    {
        if (attempt.Status != IngestionStatus.Completed || attempt.DetectedMediaType is null ||
            attempt.FailureKind is not null || attempt.FailureCode is not null || attempt.SpanSetId is null)
            throw new ArgumentException("A completed ingestion attempt requires media and a span set without a failure.");
    }

    private static int? NullableInt(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static TEnum ReadEnum<TEnum>(short value, string field) where TEnum : struct, Enum
    {
        var parsed = (TEnum)Enum.ToObject(typeof(TEnum), value);
        return Enum.IsDefined(parsed) ? parsed : throw new InvalidOperationException($"Persisted {field} is invalid.");
    }
}
