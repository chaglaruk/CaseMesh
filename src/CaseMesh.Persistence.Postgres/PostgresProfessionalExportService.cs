using CaseMesh.Core.Models;
using CaseMesh.ProfessionalExport;
using Npgsql;

namespace CaseMesh.Persistence.Postgres;

public sealed class PostgresProfessionalExportService : IAsyncDisposable
{
    private readonly PostgresMatterStore _store;
    private readonly ProfessionalExportGenerator _generator;

    public PostgresProfessionalExportService(string connectionString, TimeProvider timeProvider)
    {
        _store = new PostgresMatterStore(connectionString);
        _generator = new ProfessionalExportGenerator(timeProvider);
    }

    public async Task<ProfessionalExportPackage?> GenerateAsync(
        ProfessionalExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var input = await _store.InTenantTransactionAsync(request.TenantId, async (connection, transaction) =>
        {
            var persisted = await PostgresMatterBrainStore.ReadPersistedAsync(
                connection, transaction, request.TenantId, request.MatterId, cancellationToken);
            if (persisted is null)
            {
                return null;
            }

            var documents = await ReadDocumentsAsync(
                connection, transaction, request.TenantId, request.MatterId, cancellationToken);
            return new ProfessionalExportInput(
                persisted.Evidence, persisted.Workplace, persisted.Brain, documents);
        }, cancellationToken);
        if (input is null)
        {
            return null;
        }

        var package = _generator.Generate(request, input);
        await _store.InTenantTransactionAsync(request.TenantId, async (connection, transaction) =>
        {
            await SaveRunAsync(connection, transaction, package.Run, cancellationToken);
            return true;
        }, cancellationToken);
        return package;
    }

    public Task<PersistedProfessionalExportRun?> GetRunAsync(
        TenantId tenantId,
        Guid matterId,
        Guid exportId,
        CancellationToken cancellationToken = default)
    {
        if (matterId == Guid.Empty || exportId == Guid.Empty)
        {
            throw new ArgumentException("Matter and export identifiers must be non-empty.");
        }

        return _store.InTenantTransactionAsync(tenantId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                SELECT snapshot_digest,schema_version,template_version,generated_at,artifact_manifest_digest
                FROM casemesh.professional_export_runs
                WHERE tenant_id=$1 AND matter_id=$2 AND export_id=$3;
                """, connection, transaction);
            PostgresMatterStore.AddParameters(command, tenantId.Value, matterId, exportId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }
            var snapshotDigest = reader.GetString(0);
            var schemaVersion = reader.GetString(1);
            var templateVersion = reader.GetString(2);
            var generatedAt = reader.GetFieldValue<DateTimeOffset>(3);
            var artifactManifestDigest = reader.GetString(4);
            await reader.DisposeAsync();
            var inclusions = await ReadInclusionsAsync(
                connection, transaction, tenantId.Value, matterId, exportId, cancellationToken);
            var artifacts = await ReadArtifactsAsync(
                connection, transaction, tenantId.Value, matterId, exportId, cancellationToken);
            return new PersistedProfessionalExportRun(new ProfessionalExportRun(
                exportId, tenantId, matterId, snapshotDigest, schemaVersion, templateVersion,
                generatedAt, artifactManifestDigest,
                inclusions.GetValueOrDefault(ExportInclusionKind.DocumentVersion) ?? [],
                inclusions.GetValueOrDefault(ExportInclusionKind.SourceSpan) ?? [],
                inclusions.GetValueOrDefault(ExportInclusionKind.Assertion) ?? [],
                inclusions.GetValueOrDefault(ExportInclusionKind.Event) ?? [],
                inclusions.GetValueOrDefault(ExportInclusionKind.Contradiction) ?? [],
                artifacts));
        }, cancellationToken);
    }

    public async ValueTask DisposeAsync() => await _store.DisposeAsync();

    private static async Task<IReadOnlyList<ExportDocumentMetadata>> ReadDocumentsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TenantId tenantId,
        Guid matterId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT dv.document_id,dv.document_version_id,dv.original_object_id,dv.content_sha256,
                   state.detected_media_type,state.byte_length,state.status,
                   COALESCE(bool_or(span.extraction_route = 1) FILTER (WHERE span.source_span_id IS NOT NULL),false),
                   COALESCE(bool_or(span.extraction_route = 2) FILTER (WHERE span.source_span_id IS NOT NULL),false),
                   max(span.parser_version),
                   max(span.extraction_provider) FILTER (WHERE span.extraction_route = 2),
                   max(span.extraction_provider_version) FILTER (WHERE span.extraction_route = 2)
            FROM casemesh.document_versions dv
            LEFT JOIN casemesh.document_ingestion_state state
              ON state.tenant_id=dv.tenant_id AND state.matter_id=dv.matter_id
             AND state.document_version_id=dv.document_version_id
            LEFT JOIN casemesh.source_spans span
              ON span.tenant_id=dv.tenant_id AND span.matter_id=dv.matter_id
             AND span.document_version_id=dv.document_version_id
            WHERE dv.tenant_id=$1 AND dv.matter_id=$2
            GROUP BY dv.document_id,dv.document_version_id,dv.original_object_id,dv.content_sha256,
                     state.detected_media_type,state.byte_length,state.status
            ORDER BY dv.document_id,dv.document_version_id;
            """, connection, transaction);
        PostgresMatterStore.AddParameters(command, tenantId.Value, matterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var documents = new List<ExportDocumentMetadata>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var routes = ExportExtractionRoute.None;
            if (reader.GetBoolean(7)) routes |= ExportExtractionRoute.Native;
            if (reader.GetBoolean(8)) routes |= ExportExtractionRoute.Ocr;
            documents.Add(new ExportDocumentMetadata(
                tenantId, matterId, reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2),
                reader.GetString(3), reader.IsDBNull(4) ? null : MediaType(reader.GetInt16(4)),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.IsDBNull(6) ? ExportDocumentProcessingStatus.NotRecorded : Status(reader.GetInt16(6)),
                routes, reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11)));
        }
        return documents;
    }

    private static async Task SaveRunAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProfessionalExportRun run,
        CancellationToken cancellationToken)
    {
        await EnsureAsync(connection, transaction,
            """
            INSERT INTO casemesh.professional_export_runs
                (tenant_id,matter_id,export_id,snapshot_digest,schema_version,template_version,
                 generated_at,artifact_manifest_digest)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8) ON CONFLICT DO NOTHING;
            """,
            """
            SELECT EXISTS (SELECT 1 FROM casemesh.professional_export_runs
              WHERE tenant_id=$1 AND matter_id=$2 AND export_id=$3 AND snapshot_digest=$4
                AND schema_version=$5 AND template_version=$6 AND generated_at=$7
                AND artifact_manifest_digest=$8);
            """, cancellationToken,
            run.TenantId.Value, run.MatterId, run.ExportId, run.SnapshotDigest, run.SchemaVersion,
            run.TemplateVersion, run.GeneratedAt, run.ArtifactManifestDigest);

        await SaveInclusionsAsync(connection, transaction, run, ExportInclusionKind.DocumentVersion,
            run.DocumentVersionIds, "document_version_id", cancellationToken);
        await SaveInclusionsAsync(connection, transaction, run, ExportInclusionKind.SourceSpan,
            run.SourceSpanIds, "source_span_id", cancellationToken);
        await SaveInclusionsAsync(connection, transaction, run, ExportInclusionKind.Assertion,
            run.AssertionIds, "assertion_id", cancellationToken);
        await SaveInclusionsAsync(connection, transaction, run, ExportInclusionKind.Event,
            run.EventIds, "event_id", cancellationToken);
        await SaveInclusionsAsync(connection, transaction, run, ExportInclusionKind.Contradiction,
            run.ContradictionIds, "contradiction_id", cancellationToken);
        foreach (var artifact in run.Artifacts.OrderBy(item => item.Kind))
        {
            await EnsureAsync(connection, transaction,
                """
                INSERT INTO casemesh.professional_export_artifacts
                    (tenant_id,matter_id,export_id,artifact_kind,file_name,content_sha256,byte_length)
                VALUES ($1,$2,$3,$4,$5,$6,$7) ON CONFLICT DO NOTHING;
                """,
                """
                SELECT EXISTS (SELECT 1 FROM casemesh.professional_export_artifacts
                  WHERE tenant_id=$1 AND matter_id=$2 AND export_id=$3 AND artifact_kind=$4
                    AND file_name=$5 AND content_sha256=$6 AND byte_length=$7);
                """, cancellationToken,
                run.TenantId.Value, run.MatterId, run.ExportId, (short)artifact.Kind,
                artifact.FileName, artifact.Sha256, artifact.ByteLength);
        }
    }

    private static async Task SaveInclusionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProfessionalExportRun run,
        ExportInclusionKind kind,
        IReadOnlyList<Guid> ids,
        string column,
        CancellationToken cancellationToken)
    {
        var allowedColumn = kind switch
        {
            ExportInclusionKind.DocumentVersion => "document_version_id",
            ExportInclusionKind.SourceSpan => "source_span_id",
            ExportInclusionKind.Assertion => "assertion_id",
            ExportInclusionKind.Event => "event_id",
            ExportInclusionKind.Contradiction => "contradiction_id",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        if (column != allowedColumn)
        {
            throw new InvalidOperationException("Export inclusion column is not valid for its typed kind.");
        }
        for (var ordinal = 0; ordinal < ids.Count; ordinal++)
        {
            await EnsureAsync(connection, transaction,
                $"INSERT INTO casemesh.professional_export_inclusions (tenant_id,matter_id,export_id,inclusion_kind,ordinal,{column}) VALUES ($1,$2,$3,$4,$5,$6) ON CONFLICT DO NOTHING;",
                $"SELECT EXISTS (SELECT 1 FROM casemesh.professional_export_inclusions WHERE tenant_id=$1 AND matter_id=$2 AND export_id=$3 AND inclusion_kind=$4 AND ordinal=$5 AND {column}=$6);",
                cancellationToken, run.TenantId.Value, run.MatterId, run.ExportId, (short)kind, ordinal, ids[ordinal]);
        }
    }

    private static async Task EnsureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string insertSql,
        string verifySql,
        CancellationToken cancellationToken,
        params object?[] values)
    {
        var inserted = await PostgresMatterStore.ExecuteAsync(
            connection, transaction, insertSql, cancellationToken, values);
        if (inserted == 1)
        {
            return;
        }
        await using var verify = new NpgsqlCommand(verifySql, connection, transaction);
        PostgresMatterStore.AddParameters(verify, values);
        if (await verify.ExecuteScalarAsync(cancellationToken) is not true)
        {
            throw new InvalidOperationException("An export identity cannot overwrite divergent audit metadata.");
        }
    }

    private static async Task<Dictionary<ExportInclusionKind, IReadOnlyList<Guid>>> ReadInclusionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid tenantId,
        Guid matterId,
        Guid exportId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT inclusion_kind,COALESCE(document_version_id,source_span_id,assertion_id,event_id,contradiction_id)
            FROM casemesh.professional_export_inclusions
            WHERE tenant_id=$1 AND matter_id=$2 AND export_id=$3
            ORDER BY inclusion_kind,ordinal;
            """, connection, transaction);
        PostgresMatterStore.AddParameters(command, tenantId, matterId, exportId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var mutable = new Dictionary<ExportInclusionKind, List<Guid>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var kind = ReadEnum<ExportInclusionKind>(reader.GetInt16(0), "export inclusion kind");
            if (!mutable.TryGetValue(kind, out var ids))
            {
                ids = [];
                mutable.Add(kind, ids);
            }
            ids.Add(reader.GetGuid(1));
        }
        return mutable.ToDictionary(item => item.Key, item => (IReadOnlyList<Guid>)item.Value.ToArray());
    }

    private static async Task<IReadOnlyList<ProfessionalExportArtifactDigest>> ReadArtifactsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid tenantId,
        Guid matterId,
        Guid exportId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT artifact_kind,file_name,content_sha256,byte_length
            FROM casemesh.professional_export_artifacts
            WHERE tenant_id=$1 AND matter_id=$2 AND export_id=$3 ORDER BY artifact_kind;
            """, connection, transaction);
        PostgresMatterStore.AddParameters(command, tenantId, matterId, exportId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var artifacts = new List<ProfessionalExportArtifactDigest>();
        while (await reader.ReadAsync(cancellationToken))
        {
            artifacts.Add(new ProfessionalExportArtifactDigest(
                ReadEnum<ProfessionalExportArtifactKind>(reader.GetInt16(0), "export artifact kind"),
                reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
        }
        return artifacts;
    }

    private static ExportDocumentProcessingStatus Status(short value) => value switch
    {
        1 => ExportDocumentProcessingStatus.Pending,
        2 => ExportDocumentProcessingStatus.Completed,
        3 => ExportDocumentProcessingStatus.Quarantined,
        4 => ExportDocumentProcessingStatus.Failed,
        _ => throw new InvalidOperationException("Persisted ingestion status is invalid for export.")
    };

    private static string MediaType(short value) => value switch
    {
        1 => "pdf",
        2 => "docx",
        3 => "eml",
        4 => "text",
        5 => "png",
        6 => "jpeg",
        _ => throw new InvalidOperationException("Persisted media type is invalid for export.")
    };

    private static T ReadEnum<T>(short value, string label) where T : struct, Enum
    {
        var result = (T)Enum.ToObject(typeof(T), value);
        return Enum.IsDefined(result) ? result : throw new InvalidOperationException($"Persisted {label} is invalid.");
    }
}
