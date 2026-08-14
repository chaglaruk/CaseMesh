using System.Net;
using Amazon.S3.Model;
using CaseMesh.Core.Models;
using CaseMesh.Persistence.Postgres;
using Npgsql;

namespace CaseMesh.Storage.S3.Tests;

[Collection(StorageCollection.Name)]
public sealed class OriginalEvidenceStorageTests(StorageIntegrationFixture fixture)
{
    [Fact]
    public void Production_http_endpoint_is_rejected_even_with_local_override()
    {
        var options = new S3ObjectStorageOptions
        {
            Endpoint = new Uri("http://storage.example.test"),
            Region = "us-east-1",
            BucketName = "synthetic-private",
            AccessKey = "synthetic-access",
            SecretKey = "synthetic-secret",
            AllowInsecureLocalEndpoint = true
        };

        Assert.Throws<InvalidOperationException>(() => new S3ImmutableObjectBackend(options));
    }

    [StorageFact]
    public async Task Exact_bytes_sha256_and_length_round_trip()
    {
        var scope = await fixture.CreateScopeAsync(Bytes("synthetic-original-evidence"));
        await using var store = fixture.CreateStore();

        var stored = await store.StoreAsync(scope.TenantId, scope.MatterId, scope.OriginalObjectId, Stream(scope.Bytes));
        await using var destination = new MemoryStream();
        var read = await store.ReadVerifiedAsync(
            scope.TenantId,
            scope.MatterId,
            scope.OriginalObjectId,
            destination);

        Assert.Equal(scope.Bytes, destination.ToArray());
        Assert.Equal(scope.ContentSha256, stored.ContentSha256);
        Assert.Equal(scope.Bytes.LongLength, stored.ByteLength);
        Assert.Equal(stored, read);
    }

    [StorageFact]
    public async Task Identical_retry_is_idempotent()
    {
        var scope = await fixture.CreateScopeAsync(Bytes("synthetic-idempotent"));
        await using var store = fixture.CreateStore();

        var first = await store.StoreAsync(scope.TenantId, scope.MatterId, scope.OriginalObjectId, Stream(scope.Bytes));
        var second = await store.StoreAsync(scope.TenantId, scope.MatterId, scope.OriginalObjectId, Stream(scope.Bytes));

        Assert.Equal(first, second);
        Assert.True(await fixture.PhysicalExistsAsync(scope));
    }

    [StorageFact]
    public async Task Different_content_overwrite_is_rejected_and_original_bytes_remain()
    {
        var scope = await fixture.CreateScopeAsync(Bytes("synthetic-immutable"));
        await using var store = fixture.CreateStore();
        await store.StoreAsync(scope.TenantId, scope.MatterId, scope.OriginalObjectId, Stream(scope.Bytes));

        await Assert.ThrowsAsync<OriginalEvidenceIntegrityException>(() =>
            store.StoreAsync(scope.TenantId, scope.MatterId, scope.OriginalObjectId, Stream(Bytes("different"))));
        await using var destination = new MemoryStream();
        await store.ReadVerifiedAsync(scope.TenantId, scope.MatterId, scope.OriginalObjectId, destination);
        Assert.Equal(scope.Bytes, destination.ToArray());
    }

    [StorageFact]
    public async Task Wrong_tenant_cannot_resolve_metadata()
    {
        var scope = await fixture.CreateScopeAsync(Bytes("synthetic-resolve"));
        await using var store = fixture.CreateStore();
        await store.StoreAsync(scope.TenantId, scope.MatterId, scope.OriginalObjectId, Stream(scope.Bytes));

        var result = await store.GetMetadataAsync(
            new TenantId(Guid.NewGuid()),
            scope.MatterId,
            scope.OriginalObjectId);

        Assert.Null(result);
    }

    [StorageFact]
    public async Task Missing_tenant_context_cannot_resolve_storage_metadata()
    {
        var scope = await fixture.CreateScopeAsync(Bytes("synthetic-missing-context"));
        await using var store = fixture.CreateStore();
        await store.StoreAsync(scope.TenantId, scope.MatterId, scope.OriginalObjectId, Stream(scope.Bytes));

        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM casemesh.original_object_storage;",
            connection);

        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [StorageFact]
    public async Task Wrong_tenant_cannot_read_bytes()
    {
        var scope = await fixture.CreateScopeAsync(Bytes("synthetic-private-read"));
        await using var store = fixture.CreateStore();
        await store.StoreAsync(scope.TenantId, scope.MatterId, scope.OriginalObjectId, Stream(scope.Bytes));

        await Assert.ThrowsAsync<OriginalEvidenceNotFoundException>(() =>
            store.ReadVerifiedAsync(
                new TenantId(Guid.NewGuid()),
                scope.MatterId,
                scope.OriginalObjectId,
                new MemoryStream()));
    }

    [StorageFact]
    public async Task Wrong_tenant_cannot_delete_bytes()
    {
        var scope = await fixture.CreateScopeAsync(Bytes("synthetic-private-delete"));
        await using var store = fixture.CreateStore();
        await store.StoreAsync(scope.TenantId, scope.MatterId, scope.OriginalObjectId, Stream(scope.Bytes));

        var deleted = await store.DeleteOriginalAsync(
            new TenantId(Guid.NewGuid()),
            scope.MatterId,
            scope.OriginalObjectId);

        Assert.False(deleted);
        Assert.True(await fixture.PhysicalExistsAsync(scope));
    }

    [StorageFact]
    public async Task Equal_hashes_in_different_tenants_are_physically_independent()
    {
        var bytes = Bytes("synthetic-cross-tenant-same-hash");
        var first = await fixture.CreateScopeAsync(bytes);
        var second = await fixture.CreateScopeAsync(bytes);
        await using var store = fixture.CreateStore();
        await store.StoreAsync(first.TenantId, first.MatterId, first.OriginalObjectId, Stream(bytes));
        await store.StoreAsync(second.TenantId, second.MatterId, second.OriginalObjectId, Stream(bytes));

        Assert.NotEqual(fixture.KeyFor(first), fixture.KeyFor(second));
        Assert.True(await store.DeleteTenantAsync(first.TenantId));
        await using var destination = new MemoryStream();
        await store.ReadVerifiedAsync(second.TenantId, second.MatterId, second.OriginalObjectId, destination);
        Assert.Equal(bytes, destination.ToArray());
    }

    [StorageFact]
    public async Task Same_matter_duplicate_versions_share_one_storage_identity()
    {
        var scope = await fixture.CreateScopeAsync(Bytes("synthetic-logical-duplicate"), duplicateVersion: true);
        await using var store = fixture.CreateStore();
        await store.StoreAsync(scope.TenantId, scope.MatterId, scope.OriginalObjectId, Stream(scope.Bytes));

        await using var connection = new NpgsqlConnection(fixture.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT
                (SELECT COUNT(*) FROM casemesh.document_versions WHERE tenant_id = $1 AND matter_id = $2),
                (SELECT COUNT(*) FROM casemesh.original_objects WHERE tenant_id = $1 AND matter_id = $2),
                (SELECT COUNT(*) FROM casemesh.original_object_storage WHERE tenant_id = $1 AND matter_id = $2);
            """, connection);
        command.Parameters.AddWithValue(scope.TenantId.Value);
        command.Parameters.AddWithValue(scope.MatterId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.GetInt64(0));
        Assert.Equal(1, reader.GetInt64(1));
        Assert.Equal(1, reader.GetInt64(2));
    }

    [StorageFact]
    public async Task Database_metadata_conflict_rejects_divergence()
    {
        var scope = await fixture.CreateScopeAsync(Bytes("synthetic-metadata-conflict"));
        await using var store = fixture.CreateStore();
        var stored = await store.StoreAsync(scope.TenantId, scope.MatterId, scope.OriginalObjectId, Stream(scope.Bytes));
        await using var matterStore = new PostgresMatterStore(fixture.AppConnectionString);
        IOriginalObjectStorageMetadataRepository repository =
            new PostgresOriginalObjectStorageRepository(matterStore);
        var divergent = new OriginalObjectStorageMetadata(
            new OriginalObjectIdentity(scope.TenantId, scope.MatterId, scope.OriginalObjectId),
            new StorageAddress("s3", fixture.BucketName, fixture.KeyFor(scope) + "-different"),
            scope.ContentSha256,
            stored.ByteLength,
            stored.StoredAt);

        await Assert.ThrowsAsync<OriginalEvidenceConflictException>(() =>
            repository.SaveAsync(divergent, CancellationToken.None));
    }

    [StorageFact]
    public async Task Cross_matter_storage_locator_is_rejected_by_composite_ownership()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var first = await fixture.CreateScopeAsync(Bytes("synthetic-cross-matter-a"), tenant);
        var second = await fixture.CreateScopeAsync(Bytes("synthetic-cross-matter-b"), tenant);
        await using var store = fixture.CreateStore();
        await store.StoreAsync(first.TenantId, first.MatterId, first.OriginalObjectId, Stream(first.Bytes));

        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var context = new NpgsqlCommand(
                         "SELECT set_config('casemesh.tenant_id', $1, true);",
                         connection,
                         transaction))
        {
            context.Parameters.AddWithValue(tenant.Value.ToString());
            await context.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand("""
            INSERT INTO casemesh.original_object_storage (
                tenant_id, matter_id, original_object_id, backend_kind,
                bucket_name, object_key, content_sha256, byte_length, stored_at)
            VALUES ($1, $2, $3, 's3', $4, $5, $6, $7, CURRENT_TIMESTAMP);
            """, connection, transaction);
        command.Parameters.AddWithValue(tenant.Value);
        command.Parameters.AddWithValue(second.MatterId);
        command.Parameters.AddWithValue(first.OriginalObjectId);
        command.Parameters.AddWithValue(fixture.BucketName);
        command.Parameters.AddWithValue(
            $"v1/tenants/{tenant.Value:D}/matters/{second.MatterId:D}/originals/{first.OriginalObjectId:D}");
        command.Parameters.AddWithValue(first.ContentSha256);
        command.Parameters.AddWithValue(first.Bytes.LongLength);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    [StorageFact]
    public async Task Hash_mismatch_is_rejected_without_object_or_metadata()
    {
        var scope = await fixture.CreateScopeAsync(Bytes("synthetic-expected"));
        await using var store = fixture.CreateStore();

        await Assert.ThrowsAsync<OriginalEvidenceIntegrityException>(() =>
            store.StoreAsync(scope.TenantId, scope.MatterId, scope.OriginalObjectId, Stream(Bytes("synthetic-actual"))));

        Assert.Null(await store.GetMetadataAsync(scope.TenantId, scope.MatterId, scope.OriginalObjectId));
        Assert.False(await fixture.PhysicalExistsAsync(scope));
    }

    [StorageFact]
    public async Task Metadata_failure_executes_new_object_compensation()
    {
        var scope = await fixture.CreateScopeAsync(Bytes("synthetic-compensation"));
        await using var matterStore = new PostgresMatterStore(fixture.AppConnectionString);
        IOriginalObjectStorageMetadataRepository inner =
            new PostgresOriginalObjectStorageRepository(matterStore);
        var failing = new FailFirstSaveRepository(inner);
        await using var backend = new S3ImmutableObjectBackend(fixture.Options);
        var service = new OriginalEvidenceStorageService(backend, failing);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StoreAsync(scope.TenantId, scope.MatterId, scope.OriginalObjectId, Stream(scope.Bytes)));

        Assert.True(failing.SaveAttempted);
        Assert.False(await fixture.PhysicalExistsAsync(scope));
        Assert.Null(await service.GetMetadataAsync(scope.TenantId, scope.MatterId, scope.OriginalObjectId));
    }

    [StorageFact]
    public async Task Deliberately_tampered_object_is_detected()
    {
        var scope = await fixture.CreateScopeAsync(Bytes("synthetic-before-tamper"));
        await using var store = fixture.CreateStore();
        await store.StoreAsync(scope.TenantId, scope.MatterId, scope.OriginalObjectId, Stream(scope.Bytes));
        await fixture.S3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = fixture.BucketName,
            Key = fixture.KeyFor(scope),
            InputStream = Stream(Bytes("synthetic-tampered"))
        });

        await Assert.ThrowsAsync<OriginalEvidenceIntegrityException>(() =>
            store.VerifyIntegrityAsync(scope.TenantId, scope.MatterId, scope.OriginalObjectId));
    }

    [StorageFact]
    public async Task Missing_physical_object_is_explicitly_reported()
    {
        var scope = await fixture.CreateScopeAsync(Bytes("synthetic-missing"));
        await using var store = fixture.CreateStore();
        await store.StoreAsync(scope.TenantId, scope.MatterId, scope.OriginalObjectId, Stream(scope.Bytes));
        await fixture.S3.DeleteObjectAsync(fixture.BucketName, fixture.KeyFor(scope));

        await Assert.ThrowsAsync<OriginalEvidenceNotFoundException>(() =>
            store.VerifyIntegrityAsync(scope.TenantId, scope.MatterId, scope.OriginalObjectId));
    }

    [StorageFact]
    public async Task Scoped_object_delete_is_idempotent()
    {
        var scope = await fixture.CreateScopeAsync(Bytes("synthetic-delete-idempotent"));
        await using var store = fixture.CreateStore();
        await store.StoreAsync(scope.TenantId, scope.MatterId, scope.OriginalObjectId, Stream(scope.Bytes));

        Assert.True(await store.DeleteOriginalAsync(scope.TenantId, scope.MatterId, scope.OriginalObjectId));
        Assert.False(await store.DeleteOriginalAsync(scope.TenantId, scope.MatterId, scope.OriginalObjectId));
        Assert.False(await fixture.PhysicalExistsAsync(scope));
    }

    [StorageFact]
    public async Task Matter_delete_is_guarded_and_partial_failure_is_retryable()
    {
        var first = await fixture.CreateScopeAsync(Bytes("synthetic-matter-delete-a"));
        var second = await fixture.CreateScopeAsync(
            Bytes("synthetic-matter-delete-b"),
            first.TenantId,
            first.MatterId);
        await using var normal = fixture.CreateStore();
        await normal.StoreAsync(first.TenantId, first.MatterId, first.OriginalObjectId, Stream(first.Bytes));
        await normal.StoreAsync(second.TenantId, second.MatterId, second.OriginalObjectId, Stream(second.Bytes));
        await using (var rawStore = new PostgresMatterStore(fixture.AppConnectionString))
        {
            await Assert.ThrowsAsync<PostgresException>(() => rawStore.DeleteMatterAsync(first.TenantId, first.MatterId));
        }

        await using var matterStore = new PostgresMatterStore(fixture.AppConnectionString);
        IOriginalObjectStorageMetadataRepository repository =
            new PostgresOriginalObjectStorageRepository(matterStore);
        await using var backend = new FailFirstDeleteBackend(new S3ImmutableObjectBackend(fixture.Options));
        var failing = new OriginalEvidenceStorageService(backend, repository);
        await Assert.ThrowsAsync<IOException>(() => failing.DeleteMatterAsync(first.TenantId, first.MatterId));
        Assert.True(await fixture.MatterExistsAsync(first));

        Assert.True(await normal.DeleteMatterAsync(first.TenantId, first.MatterId));
        Assert.False(await fixture.MatterExistsAsync(first));
        Assert.False(await fixture.PhysicalExistsAsync(first));
        Assert.False(await fixture.PhysicalExistsAsync(second));
    }

    [StorageFact]
    public async Task Concurrent_store_cannot_be_orphaned_by_matter_deletion()
    {
        var first = await fixture.CreateScopeAsync(Bytes("synthetic-delete-race-a"));
        var second = await fixture.CreateScopeAsync(
            Bytes("synthetic-delete-race-b"),
            first.TenantId,
            first.MatterId);
        await using var normal = fixture.CreateStore();
        await normal.StoreAsync(first.TenantId, first.MatterId, first.OriginalObjectId, Stream(first.Bytes));

        await using var matterStore = new PostgresMatterStore(fixture.AppConnectionString);
        IOriginalObjectStorageMetadataRepository inner =
            new PostgresOriginalObjectStorageRepository(matterStore);
        var gated = new GateMatterDeleteRepository(inner);
        await using var backend = new S3ImmutableObjectBackend(fixture.Options);
        var deleting = new OriginalEvidenceStorageService(backend, gated);
        var deleteTask = deleting.DeleteMatterAsync(first.TenantId, first.MatterId);
        await gated.DeleteReached.Task;

        await normal.StoreAsync(second.TenantId, second.MatterId, second.OriginalObjectId, Stream(second.Bytes));
        gated.ContinueDelete.SetResult();

        await Assert.ThrowsAsync<OriginalEvidenceConflictException>(() => deleteTask);
        Assert.True(await fixture.MatterExistsAsync(first));
        Assert.True(await fixture.PhysicalExistsAsync(second));
        Assert.True(await normal.DeleteMatterAsync(first.TenantId, first.MatterId));
        Assert.False(await fixture.PhysicalExistsAsync(first));
        Assert.False(await fixture.PhysicalExistsAsync(second));
    }

    [StorageFact]
    public async Task Tenant_delete_failure_is_retryable_and_cannot_affect_another_tenant()
    {
        var first = await fixture.CreateScopeAsync(Bytes("synthetic-tenant-delete-a"));
        var second = await fixture.CreateScopeAsync(Bytes("synthetic-tenant-delete-b"));
        await using var normal = fixture.CreateStore();
        await normal.StoreAsync(first.TenantId, first.MatterId, first.OriginalObjectId, Stream(first.Bytes));
        await normal.StoreAsync(second.TenantId, second.MatterId, second.OriginalObjectId, Stream(second.Bytes));

        await using var matterStore = new PostgresMatterStore(fixture.AppConnectionString);
        IOriginalObjectStorageMetadataRepository repository =
            new PostgresOriginalObjectStorageRepository(matterStore);
        await using var backend = new FailFirstDeleteBackend(new S3ImmutableObjectBackend(fixture.Options));
        var failing = new OriginalEvidenceStorageService(backend, repository);
        await Assert.ThrowsAsync<IOException>(() => failing.DeleteTenantAsync(first.TenantId));
        Assert.True(await fixture.MatterExistsAsync(first));

        Assert.True(await normal.DeleteTenantAsync(first.TenantId));
        await using var destination = new MemoryStream();
        await normal.ReadVerifiedAsync(second.TenantId, second.MatterId, second.OriginalObjectId, destination);
        Assert.Equal(second.Bytes, destination.ToArray());
    }

    [StorageFact]
    public async Task Object_key_uses_only_typed_identifiers()
    {
        const string sensitiveTitle = "Synthetic Employee grievance filename.eml";
        var scope = await fixture.CreateScopeAsync(Bytes("synthetic-safe-key"), title: sensitiveTitle);
        await using var store = fixture.CreateStore();
        await store.StoreAsync(scope.TenantId, scope.MatterId, scope.OriginalObjectId, Stream(scope.Bytes));

        var key = await fixture.ReadStoredKeyAsync(scope);
        Assert.Equal(fixture.KeyFor(scope), key);
        Assert.DoesNotContain("Employee", key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("filename", key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("grievance", key, StringComparison.OrdinalIgnoreCase);
    }

    [StorageFact]
    public async Task Adapter_does_not_grant_public_or_anonymous_read()
    {
        var scope = await fixture.CreateScopeAsync(Bytes("synthetic-private-acl"));
        await using var store = fixture.CreateStore();
        await store.StoreAsync(scope.TenantId, scope.MatterId, scope.OriginalObjectId, Stream(scope.Bytes));

        var acl = await fixture.S3.GetObjectAclAsync(new GetObjectAclRequest
        {
            BucketName = fixture.BucketName,
            Key = fixture.KeyFor(scope)
        });
        Assert.DoesNotContain(acl.Grants ?? [], grant =>
            string.Equals(grant.Grantee?.URI, "http://acs.amazonaws.com/groups/global/AllUsers", StringComparison.Ordinal) ||
            string.Equals(grant.Grantee?.URI, "http://acs.amazonaws.com/groups/global/AuthenticatedUsers", StringComparison.Ordinal));

        using var anonymous = new HttpClient();
        var response = await anonymous.GetAsync(
            new Uri(fixture.Options.Endpoint, $"{fixture.BucketName}/{fixture.KeyFor(scope)}"));
        Assert.False(response.IsSuccessStatusCode);
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized, HttpStatusCode.NotFound });
    }

    private static byte[] Bytes(string value) => System.Text.Encoding.UTF8.GetBytes(value);
    private static MemoryStream Stream(byte[] bytes) => new(bytes, writable: false);

    private sealed class FailFirstSaveRepository(IOriginalObjectStorageMetadataRepository inner)
        : IOriginalObjectStorageMetadataRepository
    {
        internal bool SaveAttempted { get; private set; }

        public Task<OriginalObjectState?> ResolveAsync(OriginalObjectIdentity identity, CancellationToken cancellationToken) =>
            inner.ResolveAsync(identity, cancellationToken);

        public Task<OriginalObjectStorageMetadata> SaveAsync(
            OriginalObjectStorageMetadata metadata,
            CancellationToken cancellationToken)
        {
            SaveAttempted = true;
            throw new InvalidOperationException("Synthetic metadata failure.");
        }

        public Task<IReadOnlyList<OriginalObjectStorageMetadata>> ListMatterAsync(
            TenantId tenantId,
            Guid matterId,
            CancellationToken cancellationToken) => inner.ListMatterAsync(tenantId, matterId, cancellationToken);

        public Task<IReadOnlyList<OriginalObjectStorageMetadata>> ListTenantAsync(
            TenantId tenantId,
            CancellationToken cancellationToken) => inner.ListTenantAsync(tenantId, cancellationToken);

        public Task<bool> DeleteOriginalMetadataAsync(
            OriginalObjectIdentity identity,
            CancellationToken cancellationToken) => inner.DeleteOriginalMetadataAsync(identity, cancellationToken);

        public Task<bool> DeleteMatterAfterObjectsAsync(
            TenantId tenantId,
            Guid matterId,
            IReadOnlyList<OriginalObjectStorageMetadata> deletedObjects,
            CancellationToken cancellationToken) =>
            inner.DeleteMatterAfterObjectsAsync(tenantId, matterId, deletedObjects, cancellationToken);

        public Task<bool> DeleteTenantAfterObjectsAsync(
            TenantId tenantId,
            IReadOnlyList<OriginalObjectStorageMetadata> deletedObjects,
            CancellationToken cancellationToken) => inner.DeleteTenantAfterObjectsAsync(tenantId, deletedObjects, cancellationToken);
    }

    private sealed class FailFirstDeleteBackend(IImmutableObjectBackend inner) : IImmutableObjectBackend
    {
        private bool _failed;

        public StorageAddress AddressFor(OriginalObjectIdentity identity) => inner.AddressFor(identity);

        public Task<ObjectCreateResult> CreateIfAbsentAsync(
            StorageAddress address,
            Stream content,
            long byteLength,
            CancellationToken cancellationToken) => inner.CreateIfAbsentAsync(address, content, byteLength, cancellationToken);

        public Task<Stream> OpenReadAsync(StorageAddress address, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(address, cancellationToken);

        public Task DeleteIfExistsAsync(StorageAddress address, CancellationToken cancellationToken)
        {
            if (!_failed)
            {
                _failed = true;
                throw new IOException("Synthetic object deletion failure.");
            }

            return inner.DeleteIfExistsAsync(address, cancellationToken);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class GateMatterDeleteRepository(IOriginalObjectStorageMetadataRepository inner)
        : IOriginalObjectStorageMetadataRepository
    {
        internal TaskCompletionSource DeleteReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ContinueDelete { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<OriginalObjectState?> ResolveAsync(OriginalObjectIdentity identity, CancellationToken cancellationToken) =>
            inner.ResolveAsync(identity, cancellationToken);

        public Task<OriginalObjectStorageMetadata> SaveAsync(
            OriginalObjectStorageMetadata metadata,
            CancellationToken cancellationToken) => inner.SaveAsync(metadata, cancellationToken);

        public Task<IReadOnlyList<OriginalObjectStorageMetadata>> ListMatterAsync(
            TenantId tenantId,
            Guid matterId,
            CancellationToken cancellationToken) => inner.ListMatterAsync(tenantId, matterId, cancellationToken);

        public Task<IReadOnlyList<OriginalObjectStorageMetadata>> ListTenantAsync(
            TenantId tenantId,
            CancellationToken cancellationToken) => inner.ListTenantAsync(tenantId, cancellationToken);

        public Task<bool> DeleteOriginalMetadataAsync(
            OriginalObjectIdentity identity,
            CancellationToken cancellationToken) => inner.DeleteOriginalMetadataAsync(identity, cancellationToken);

        public async Task<bool> DeleteMatterAfterObjectsAsync(
            TenantId tenantId,
            Guid matterId,
            IReadOnlyList<OriginalObjectStorageMetadata> deletedObjects,
            CancellationToken cancellationToken)
        {
            DeleteReached.SetResult();
            await ContinueDelete.Task.WaitAsync(cancellationToken);
            return await inner.DeleteMatterAfterObjectsAsync(
                tenantId,
                matterId,
                deletedObjects,
                cancellationToken);
        }

        public Task<bool> DeleteTenantAfterObjectsAsync(
            TenantId tenantId,
            IReadOnlyList<OriginalObjectStorageMetadata> deletedObjects,
            CancellationToken cancellationToken) =>
            inner.DeleteTenantAfterObjectsAsync(tenantId, deletedObjects, cancellationToken);
    }
}
