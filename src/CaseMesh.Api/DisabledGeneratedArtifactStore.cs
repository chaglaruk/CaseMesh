using CaseMesh.Core.Models;
using CaseMesh.Storage;

namespace CaseMesh.Api;

internal sealed class DisabledGeneratedArtifactStore : IGeneratedArtifactStore
{
    private static GeneratedArtifactAvailabilityException Disabled() =>
        new("Generated artifact storage is not configured for this test host.", new InvalidOperationException("disabled"));
    public Task<StoredGeneratedArtifact> StoreAsync(GeneratedArtifactIdentity identity, Stream content,
        DateTimeOffset expiresAt, CancellationToken cancellationToken = default) => throw Disabled();
    public Task<StoredGeneratedArtifact?> GetMetadataAsync(GeneratedArtifactIdentity identity,
        CancellationToken cancellationToken = default) => throw Disabled();
    public Task<StoredGeneratedArtifact> ReadVerifiedAsync(GeneratedArtifactIdentity identity, Stream destination,
        DateTimeOffset now, CancellationToken cancellationToken = default) => throw Disabled();
    public Task<bool> DeleteMatterAsync(TenantId tenantId, Guid matterId,
        CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<bool> DeleteTenantAsync(TenantId tenantId,
        CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<int> DeleteExpiredAsync(TenantId tenantId, DateTimeOffset now,
        CancellationToken cancellationToken = default) => Task.FromResult(0);
    public Task<bool> CheckReadinessAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
