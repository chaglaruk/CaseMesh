using CaseMesh.Core.Models;
using CaseMesh.Storage;

namespace CaseMesh.Api;

internal sealed class DisabledEvidenceStore : IOriginalEvidenceStore
{
    private static OriginalEvidenceAvailabilityException Disabled() =>
        new("Object storage is not configured for this test host.", new InvalidOperationException("disabled"));
    public Task<StoredOriginalEvidence> StoreAsync(TenantId tenantId, Guid matterId, Guid originalObjectId, Stream content, CancellationToken cancellationToken = default) => throw Disabled();
    public Task<StoredOriginalEvidence?> GetMetadataAsync(TenantId tenantId, Guid matterId, Guid originalObjectId, CancellationToken cancellationToken = default) => throw Disabled();
    public Task<StoredOriginalEvidence> ReadVerifiedAsync(TenantId tenantId, Guid matterId, Guid originalObjectId, Stream destination, CancellationToken cancellationToken = default) => throw Disabled();
    public Task<StoredOriginalEvidence> VerifyIntegrityAsync(TenantId tenantId, Guid matterId, Guid originalObjectId, CancellationToken cancellationToken = default) => throw Disabled();
    public Task<bool> DeleteOriginalAsync(TenantId tenantId, Guid matterId, Guid originalObjectId, CancellationToken cancellationToken = default) => throw Disabled();
    public Task<bool> DeleteMatterAsync(TenantId tenantId, Guid matterId, CancellationToken cancellationToken = default) => throw Disabled();
    public Task<bool> DeleteTenantAsync(TenantId tenantId, CancellationToken cancellationToken = default) => throw Disabled();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
