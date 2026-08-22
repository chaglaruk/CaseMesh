using CaseMesh.Core.Models;
using CaseMesh.Storage;
using Npgsql;

namespace CaseMesh.Persistence.Postgres;

public sealed class PostgresGeneratedArtifactRepository : IGeneratedArtifactMetadataRepository
{
    private readonly PostgresMatterStore _matterStore;

    public PostgresGeneratedArtifactRepository(PostgresMatterStore matterStore) =>
        _matterStore = matterStore ?? throw new ArgumentNullException(nameof(matterStore));

    Task<IAsyncDisposable> IGeneratedArtifactMetadataRepository.AcquireStoreLeaseAsync(
        GeneratedArtifactIdentity identity, CancellationToken cancellationToken) =>
        _matterStore.AcquireSessionAdvisoryLockAsync(
            $"casemesh:generated-store:{identity.TenantId.Value:D}:{identity.MatterId:D}:{identity.ExportId:D}:{identity.ArtifactKind}",
            cancellationToken);

    Task<GeneratedArtifactState?> IGeneratedArtifactMetadataRepository.ResolveAsync(
        GeneratedArtifactIdentity identity, CancellationToken cancellationToken) =>
        _matterStore.InTenantTransactionAsync(identity.TenantId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                SELECT a.content_sha256, a.byte_length,
                       g.backend_kind, g.bucket_name, g.object_key,
                       g.content_sha256, g.byte_length, g.stored_at, g.expires_at
                FROM casemesh.professional_export_artifacts a
                LEFT JOIN casemesh.generated_export_objects g
                  ON g.tenant_id = a.tenant_id AND g.matter_id = a.matter_id
                 AND g.export_id = a.export_id AND g.artifact_kind = a.artifact_kind
                WHERE a.tenant_id = $1 AND a.matter_id = $2
                  AND a.export_id = $3 AND a.artifact_kind = $4;
                """, connection, transaction);
            PostgresMatterStore.AddParameters(command, identity.TenantId.Value, identity.MatterId,
                identity.ExportId, identity.ArtifactKind);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            GeneratedArtifactStorageMetadata? storage = null;
            if (!reader.IsDBNull(2))
            {
                storage = new GeneratedArtifactStorageMetadata(identity,
                    new StorageAddress(reader.GetString(2), reader.GetString(3), reader.GetString(4)),
                    reader.GetString(5), reader.GetInt64(6), reader.GetFieldValue<DateTimeOffset>(7),
                    reader.GetFieldValue<DateTimeOffset>(8));
            }
            return new GeneratedArtifactState(identity, reader.GetString(0), reader.GetInt64(1), storage);
        }, cancellationToken);

    Task<GeneratedArtifactStorageMetadata> IGeneratedArtifactMetadataRepository.SaveAsync(
        GeneratedArtifactStorageMetadata metadata, CancellationToken cancellationToken) =>
        _matterStore.InTenantTransactionAsync(metadata.Identity.TenantId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO casemesh.generated_export_objects (
                    tenant_id, matter_id, export_id, artifact_kind, backend_kind,
                    bucket_name, object_key, content_sha256, byte_length, stored_at, expires_at)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)
                ON CONFLICT DO NOTHING
                RETURNING stored_at, expires_at;
                """, connection, transaction);
            Add(command, metadata);
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                    return metadata with
                    {
                        StoredAt = reader.GetFieldValue<DateTimeOffset>(0),
                        ExpiresAt = reader.GetFieldValue<DateTimeOffset>(1)
                    };
            }
            await using var verify = new NpgsqlCommand("""
                SELECT stored_at, expires_at FROM casemesh.generated_export_objects
                WHERE tenant_id=$1 AND matter_id=$2 AND export_id=$3 AND artifact_kind=$4
                  AND backend_kind=$5 AND bucket_name=$6 AND object_key=$7
                  AND content_sha256=$8 AND byte_length=$9;
                """, connection, transaction);
            Add(verify, metadata, includeTimes: false);
            await using var existing = await verify.ExecuteReaderAsync(cancellationToken);
            if (await existing.ReadAsync(cancellationToken))
                return metadata with
                {
                    StoredAt = existing.GetFieldValue<DateTimeOffset>(0),
                    ExpiresAt = existing.GetFieldValue<DateTimeOffset>(1)
                };
            throw new GeneratedArtifactConflictException(
                "A generated artifact identity cannot overwrite divergent storage metadata.");
        }, cancellationToken);

    Task<IReadOnlyList<GeneratedArtifactStorageMetadata>> IGeneratedArtifactMetadataRepository.ListMatterAsync(
        TenantId tenantId, Guid matterId, CancellationToken cancellationToken) =>
        ListAsync(tenantId, GeneratedArtifactFilter.Matter, matterId, cancellationToken);

    Task<IReadOnlyList<GeneratedArtifactStorageMetadata>> IGeneratedArtifactMetadataRepository.ListTenantAsync(
        TenantId tenantId, CancellationToken cancellationToken) =>
        ListAsync(tenantId, GeneratedArtifactFilter.Tenant, null, cancellationToken);

    Task<IReadOnlyList<GeneratedArtifactStorageMetadata>> IGeneratedArtifactMetadataRepository.ListExpiredAsync(
        TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken) =>
        ListAsync(tenantId, GeneratedArtifactFilter.Expired, now, cancellationToken);

    Task<int> IGeneratedArtifactMetadataRepository.DeleteMetadataAsync(
        TenantId tenantId, IReadOnlyCollection<GeneratedArtifactIdentity> identities,
        CancellationToken cancellationToken) =>
        _matterStore.InTenantTransactionAsync(tenantId, async (connection, transaction) =>
        {
            var removed = 0;
            foreach (var identity in identities)
            {
                removed += await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                    DELETE FROM casemesh.generated_export_objects
                    WHERE tenant_id=$1 AND matter_id=$2 AND export_id=$3 AND artifact_kind=$4;
                    """, cancellationToken, tenantId.Value, identity.MatterId,
                    identity.ExportId, identity.ArtifactKind);
            }
            return removed;
        }, cancellationToken);

    private Task<IReadOnlyList<GeneratedArtifactStorageMetadata>> ListAsync(
        TenantId tenantId, GeneratedArtifactFilter filter, object? value,
        CancellationToken cancellationToken) =>
        _matterStore.InTenantTransactionAsync(tenantId, async (connection, transaction) =>
        {
            var commandText = filter switch
            {
                GeneratedArtifactFilter.Tenant => """
                    SELECT matter_id, export_id, artifact_kind, backend_kind, bucket_name,
                           object_key, content_sha256, byte_length, stored_at, expires_at
                    FROM casemesh.generated_export_objects
                    WHERE tenant_id=$1
                    ORDER BY matter_id, export_id, artifact_kind;
                    """,
                GeneratedArtifactFilter.Matter => """
                    SELECT matter_id, export_id, artifact_kind, backend_kind, bucket_name,
                           object_key, content_sha256, byte_length, stored_at, expires_at
                    FROM casemesh.generated_export_objects
                    WHERE tenant_id=$1 AND matter_id=$2
                    ORDER BY matter_id, export_id, artifact_kind;
                    """,
                GeneratedArtifactFilter.Expired => """
                    SELECT matter_id, export_id, artifact_kind, backend_kind, bucket_name,
                           object_key, content_sha256, byte_length, stored_at, expires_at
                    FROM casemesh.generated_export_objects
                    WHERE tenant_id=$1 AND expires_at <= $2
                    ORDER BY matter_id, export_id, artifact_kind;
                    """,
                _ => throw new ArgumentOutOfRangeException(nameof(filter))
            };
            await using var command = new NpgsqlCommand(commandText, connection, transaction);
            if (filter == GeneratedArtifactFilter.Tenant)
                PostgresMatterStore.AddParameters(command, tenantId.Value);
            else
                PostgresMatterStore.AddParameters(command, tenantId.Value, value!);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var result = new List<GeneratedArtifactStorageMetadata>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var identity = new GeneratedArtifactIdentity(tenantId, reader.GetGuid(0),
                    reader.GetGuid(1), reader.GetInt16(2));
                result.Add(new GeneratedArtifactStorageMetadata(identity,
                    new StorageAddress(reader.GetString(3), reader.GetString(4), reader.GetString(5)),
                    reader.GetString(6), reader.GetInt64(7), reader.GetFieldValue<DateTimeOffset>(8),
                    reader.GetFieldValue<DateTimeOffset>(9)));
            }
            return (IReadOnlyList<GeneratedArtifactStorageMetadata>)result;
        }, cancellationToken);

    private enum GeneratedArtifactFilter { Tenant, Matter, Expired }

    private static void Add(NpgsqlCommand command, GeneratedArtifactStorageMetadata metadata,
        bool includeTimes = true)
    {
        var values = new List<object>
        {
            metadata.Identity.TenantId.Value, metadata.Identity.MatterId,
            metadata.Identity.ExportId, metadata.Identity.ArtifactKind,
            metadata.Address.BackendKind, metadata.Address.BucketName, metadata.Address.ObjectKey,
            metadata.ContentSha256, metadata.ByteLength
        };
        if (includeTimes)
        {
            values.Add(metadata.StoredAt);
            values.Add(metadata.ExpiresAt);
        }
        PostgresMatterStore.AddParameters(command, values.ToArray());
    }
}
