using CaseMesh.Core.Models;
using CaseMesh.Persistence.Postgres;

namespace CaseMesh.Storage.S3;

public sealed class S3OriginalEvidenceStore : IOriginalEvidenceStore
{
    private readonly PostgresMatterStore _matterStore;
    private readonly S3ImmutableObjectBackend _backend;
    private readonly OriginalEvidenceStorageService _service;

    public S3OriginalEvidenceStore(
        string postgresConnectionString,
        S3ObjectStorageOptions options,
        TimeProvider? timeProvider = null)
    {
        _matterStore = new PostgresMatterStore(postgresConnectionString);
        _backend = new S3ImmutableObjectBackend(options);
        var metadata = new PostgresOriginalObjectStorageRepository(_matterStore);
        _service = new OriginalEvidenceStorageService(_backend, metadata, timeProvider);
    }

    public Task<StoredOriginalEvidence> StoreAsync(
        TenantId tenantId,
        Guid matterId,
        Guid originalObjectId,
        Stream content,
        CancellationToken cancellationToken = default) =>
        _service.StoreAsync(tenantId, matterId, originalObjectId, content, cancellationToken);

    public Task<StoredOriginalEvidence?> GetMetadataAsync(
        TenantId tenantId,
        Guid matterId,
        Guid originalObjectId,
        CancellationToken cancellationToken = default) =>
        _service.GetMetadataAsync(tenantId, matterId, originalObjectId, cancellationToken);

    public Task<StoredOriginalEvidence> ReadVerifiedAsync(
        TenantId tenantId,
        Guid matterId,
        Guid originalObjectId,
        Stream destination,
        CancellationToken cancellationToken = default) =>
        _service.ReadVerifiedAsync(tenantId, matterId, originalObjectId, destination, cancellationToken);

    public Task<StoredOriginalEvidence> VerifyIntegrityAsync(
        TenantId tenantId,
        Guid matterId,
        Guid originalObjectId,
        CancellationToken cancellationToken = default) =>
        _service.VerifyIntegrityAsync(tenantId, matterId, originalObjectId, cancellationToken);

    public Task<bool> DeleteOriginalAsync(
        TenantId tenantId,
        Guid matterId,
        Guid originalObjectId,
        CancellationToken cancellationToken = default) =>
        _service.DeleteOriginalAsync(tenantId, matterId, originalObjectId, cancellationToken);

    public Task<bool> DeleteMatterAsync(
        TenantId tenantId,
        Guid matterId,
        CancellationToken cancellationToken = default) =>
        _service.DeleteMatterAsync(tenantId, matterId, cancellationToken);

    public Task<bool> DeleteTenantAsync(
        TenantId tenantId,
        CancellationToken cancellationToken = default) =>
        _service.DeleteTenantAsync(tenantId, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _backend.DisposeAsync();
        await _matterStore.DisposeAsync();
    }
}
