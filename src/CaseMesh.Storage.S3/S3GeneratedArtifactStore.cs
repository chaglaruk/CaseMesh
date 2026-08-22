using CaseMesh.Core.Models;
using CaseMesh.Persistence.Postgres;

namespace CaseMesh.Storage.S3;

public sealed class S3GeneratedArtifactStore : IGeneratedArtifactStore
{
    private readonly PostgresMatterStore _matterStore;
    private readonly S3ImmutableObjectBackend _backend;
    private readonly GeneratedArtifactStorageService _service;

    public S3GeneratedArtifactStore(string postgresConnectionString, S3ObjectStorageOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _backend = new S3ImmutableObjectBackend(options);
        _matterStore = new PostgresMatterStore(postgresConnectionString);
        _service = new GeneratedArtifactStorageService(_backend,
            new PostgresGeneratedArtifactRepository(_matterStore), options.BucketName,
            timeProvider ?? TimeProvider.System);
    }

    public Task<StoredGeneratedArtifact> StoreAsync(GeneratedArtifactIdentity identity, Stream content,
        DateTimeOffset expiresAt, CancellationToken cancellationToken = default) =>
        _service.StoreAsync(identity, content, expiresAt, cancellationToken);
    public Task<StoredGeneratedArtifact?> GetMetadataAsync(GeneratedArtifactIdentity identity,
        CancellationToken cancellationToken = default) => _service.GetMetadataAsync(identity, cancellationToken);
    public Task<StoredGeneratedArtifact> ReadVerifiedAsync(GeneratedArtifactIdentity identity,
        Stream destination, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        _service.ReadVerifiedAsync(identity, destination, now, cancellationToken);
    public Task<bool> DeleteMatterAsync(TenantId tenantId, Guid matterId,
        CancellationToken cancellationToken = default) => _service.DeleteMatterAsync(tenantId, matterId, cancellationToken);
    public Task<bool> DeleteTenantAsync(TenantId tenantId,
        CancellationToken cancellationToken = default) => _service.DeleteTenantAsync(tenantId, cancellationToken);
    public Task<int> DeleteExpiredAsync(TenantId tenantId, DateTimeOffset now,
        CancellationToken cancellationToken = default) => _service.DeleteExpiredAsync(tenantId, now, cancellationToken);
    public Task<bool> CheckReadinessAsync(CancellationToken cancellationToken = default) =>
        _service.CheckReadinessAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _service.DisposeAsync();
        await _backend.DisposeAsync();
        await _matterStore.DisposeAsync();
    }
}
