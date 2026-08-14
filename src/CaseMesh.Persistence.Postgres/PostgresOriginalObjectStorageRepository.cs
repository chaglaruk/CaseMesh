using CaseMesh.Core.Models;
using CaseMesh.Storage;
using Npgsql;

namespace CaseMesh.Persistence.Postgres;

public sealed class PostgresOriginalObjectStorageRepository : IOriginalObjectStorageMetadataRepository
{
    private readonly PostgresMatterStore _matterStore;

    public PostgresOriginalObjectStorageRepository(PostgresMatterStore matterStore)
    {
        _matterStore = matterStore ?? throw new ArgumentNullException(nameof(matterStore));
    }

    Task<OriginalObjectState?> IOriginalObjectStorageMetadataRepository.ResolveAsync(
        OriginalObjectIdentity identity,
        CancellationToken cancellationToken) =>
        _matterStore.InTenantTransactionAsync(identity.TenantId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                SELECT o.content_sha256,
                       s.backend_kind, s.bucket_name, s.object_key,
                       s.content_sha256, s.byte_length, s.stored_at
                FROM casemesh.original_objects o
                LEFT JOIN casemesh.original_object_storage s
                  ON s.tenant_id = o.tenant_id
                 AND s.matter_id = o.matter_id
                 AND s.original_object_id = o.original_object_id
                WHERE o.tenant_id = $1 AND o.matter_id = $2 AND o.original_object_id = $3;
                """, connection, transaction);
            PostgresMatterStore.AddParameters(
                command,
                identity.TenantId.Value,
                identity.MatterId,
                identity.OriginalObjectId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            var expectedHash = reader.GetString(0);
            OriginalObjectStorageMetadata? storage = null;
            if (!reader.IsDBNull(1))
            {
                storage = new OriginalObjectStorageMetadata(
                    identity,
                    new StorageAddress(reader.GetString(1), reader.GetString(2), reader.GetString(3)),
                    reader.GetString(4),
                    reader.GetInt64(5),
                    reader.GetFieldValue<DateTimeOffset>(6));
            }

            return new OriginalObjectState(identity, expectedHash, storage);
        }, cancellationToken);

    Task<OriginalObjectStorageMetadata> IOriginalObjectStorageMetadataRepository.SaveAsync(
        OriginalObjectStorageMetadata metadata,
        CancellationToken cancellationToken) =>
        _matterStore.InTenantTransactionAsync(metadata.Identity.TenantId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO casemesh.original_object_storage (
                    tenant_id, matter_id, original_object_id, backend_kind,
                    bucket_name, object_key, content_sha256, byte_length, stored_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
                ON CONFLICT DO NOTHING;
                """, connection, transaction);
            PostgresMatterStore.AddParameters(
                command,
                metadata.Identity.TenantId.Value,
                metadata.Identity.MatterId,
                metadata.Identity.OriginalObjectId,
                metadata.Address.BackendKind,
                metadata.Address.BucketName,
                metadata.Address.ObjectKey,
                metadata.ContentSha256,
                metadata.ByteLength,
                metadata.StoredAt);
            try
            {
                var inserted = await command.ExecuteNonQueryAsync(cancellationToken);
                if (inserted == 1)
                {
                    return metadata;
                }

                await using var verify = new NpgsqlCommand("""
                    SELECT stored_at
                    FROM casemesh.original_object_storage
                    WHERE tenant_id = $1 AND matter_id = $2 AND original_object_id = $3
                      AND backend_kind = $4 AND bucket_name = $5 AND object_key = $6
                      AND content_sha256 = $7 AND byte_length = $8;
                    """, connection, transaction);
                PostgresMatterStore.AddParameters(
                    verify,
                    metadata.Identity.TenantId.Value,
                    metadata.Identity.MatterId,
                    metadata.Identity.OriginalObjectId,
                    metadata.Address.BackendKind,
                    metadata.Address.BucketName,
                    metadata.Address.ObjectKey,
                    metadata.ContentSha256,
                    metadata.ByteLength);
                var storedAt = await verify.ExecuteScalarAsync(cancellationToken);
                return storedAt is DateTimeOffset timestamp
                    ? metadata with { StoredAt = timestamp }
                    : throw new OriginalEvidenceConflictException(
                        "An original-object storage identity cannot overwrite divergent metadata.");
            }
            catch (PostgresException exception) when (exception.SqlState is "23505" or "23514" or "23503")
            {
                throw new OriginalEvidenceConflictException(
                    "Original-object storage metadata violates immutable ownership or locator constraints.",
                    exception);
            }
        }, cancellationToken);

    Task<IReadOnlyList<OriginalObjectStorageMetadata>> IOriginalObjectStorageMetadataRepository.ListMatterAsync(
        TenantId tenantId,
        Guid matterId,
        CancellationToken cancellationToken) =>
        ListAsync(tenantId, "WHERE tenant_id = $1 AND matter_id = $2", cancellationToken, matterId);

    Task<IReadOnlyList<OriginalObjectStorageMetadata>> IOriginalObjectStorageMetadataRepository.ListTenantAsync(
        TenantId tenantId,
        CancellationToken cancellationToken) =>
        ListAsync(tenantId, "WHERE tenant_id = $1", cancellationToken);

    Task<bool> IOriginalObjectStorageMetadataRepository.DeleteOriginalMetadataAsync(
        OriginalObjectIdentity identity,
        CancellationToken cancellationToken) =>
        _matterStore.InTenantTransactionAsync(identity.TenantId, async (connection, transaction) =>
        {
            var count = await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                DELETE FROM casemesh.original_object_storage
                WHERE tenant_id = $1 AND matter_id = $2 AND original_object_id = $3;
                """, cancellationToken,
                identity.TenantId.Value,
                identity.MatterId,
                identity.OriginalObjectId);
            return count == 1;
        }, cancellationToken);

    Task<bool> IOriginalObjectStorageMetadataRepository.DeleteMatterAfterObjectsAsync(
        TenantId tenantId,
        Guid matterId,
        IReadOnlyList<OriginalObjectStorageMetadata> deletedObjects,
        CancellationToken cancellationToken) =>
        _matterStore.InTenantTransactionAsync(tenantId, async (connection, transaction) =>
        {
            await LockMatterAsync(connection, transaction, tenantId, matterId, cancellationToken);
            var current = await ReadStorageAsync(
                connection,
                transaction,
                "WHERE tenant_id = $1 AND matter_id = $2",
                cancellationToken,
                tenantId.Value,
                matterId);
            RequireSameDeletionSet(current, deletedObjects);
            await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                DELETE FROM casemesh.original_object_storage
                WHERE tenant_id = $1 AND matter_id = $2;
                """, cancellationToken, tenantId.Value, matterId);
            var count = await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                DELETE FROM casemesh.matters
                WHERE tenant_id = $1 AND matter_id = $2;
                """, cancellationToken, tenantId.Value, matterId);
            return count == 1;
        }, cancellationToken);

    Task<bool> IOriginalObjectStorageMetadataRepository.DeleteTenantAfterObjectsAsync(
        TenantId tenantId,
        IReadOnlyList<OriginalObjectStorageMetadata> deletedObjects,
        CancellationToken cancellationToken) =>
        _matterStore.InTenantTransactionAsync(tenantId, async (connection, transaction) =>
        {
            await using var lockTenant = new NpgsqlCommand("""
                SELECT tenant_id FROM casemesh.tenants
                WHERE tenant_id = $1
                FOR UPDATE;
                """, connection, transaction);
            PostgresMatterStore.AddParameters(lockTenant, tenantId.Value);
            if (await lockTenant.ExecuteScalarAsync(cancellationToken) is not Guid)
            {
                return false;
            }

            await using var lockMatters = new NpgsqlCommand("""
                SELECT matter_id FROM casemesh.matters
                WHERE tenant_id = $1
                ORDER BY matter_id
                FOR UPDATE;
                """, connection, transaction);
            PostgresMatterStore.AddParameters(lockMatters, tenantId.Value);
            await using (var reader = await lockMatters.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                }
            }
            var current = await ReadStorageAsync(
                connection,
                transaction,
                "WHERE tenant_id = $1",
                cancellationToken,
                tenantId.Value);
            RequireSameDeletionSet(current, deletedObjects);
            await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                DELETE FROM casemesh.original_object_storage WHERE tenant_id = $1;
                """, cancellationToken, tenantId.Value);
            var count = await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                DELETE FROM casemesh.tenants WHERE tenant_id = $1;
                """, cancellationToken, tenantId.Value);
            return count == 1;
        }, cancellationToken);

    private Task<IReadOnlyList<OriginalObjectStorageMetadata>> ListAsync(
        TenantId tenantId,
        string predicate,
        CancellationToken cancellationToken,
        params object[] additionalValues) =>
        _matterStore.InTenantTransactionAsync(tenantId, (connection, transaction) =>
            ReadStorageAsync(
                connection,
                transaction,
                predicate,
                cancellationToken,
                new object[] { tenantId.Value }.Concat(additionalValues).ToArray()),
            cancellationToken);

    private static async Task<IReadOnlyList<OriginalObjectStorageMetadata>> ReadStorageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string predicate,
        CancellationToken cancellationToken,
        params object[] values)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT tenant_id, matter_id, original_object_id, backend_kind,
                   bucket_name, object_key, content_sha256, byte_length, stored_at
            FROM casemesh.original_object_storage
            {predicate}
            ORDER BY matter_id, original_object_id;
            """, connection, transaction);
        PostgresMatterStore.AddParameters(command, values);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<OriginalObjectStorageMetadata>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var identity = new OriginalObjectIdentity(
                new TenantId(reader.GetGuid(0)),
                reader.GetGuid(1),
                reader.GetGuid(2));
            result.Add(new OriginalObjectStorageMetadata(
                identity,
                new StorageAddress(reader.GetString(3), reader.GetString(4), reader.GetString(5)),
                reader.GetString(6),
                reader.GetInt64(7),
                reader.GetFieldValue<DateTimeOffset>(8)));
        }

        return result;
    }

    private static async Task LockMatterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TenantId tenantId,
        Guid matterId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT matter_id FROM casemesh.matters
            WHERE tenant_id = $1 AND matter_id = $2
            FOR UPDATE;
            """, connection, transaction);
        PostgresMatterStore.AddParameters(command, tenantId.Value, matterId);
        _ = await command.ExecuteScalarAsync(cancellationToken);
    }

    private static void RequireSameDeletionSet(
        IReadOnlyList<OriginalObjectStorageMetadata> current,
        IReadOnlyList<OriginalObjectStorageMetadata> deleted)
    {
        if (current.Count != deleted.Count || !current.SequenceEqual(deleted))
        {
            throw new OriginalEvidenceConflictException(
                "Stored evidence changed during deletion; retry the storage-aware deletion workflow.");
        }
    }
}
