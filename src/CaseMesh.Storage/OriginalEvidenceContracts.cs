using CaseMesh.Core.Models;

namespace CaseMesh.Storage;

public sealed record StoredOriginalEvidence(
    TenantId TenantId,
    Guid MatterId,
    Guid OriginalObjectId,
    string ContentSha256,
    long ByteLength,
    DateTimeOffset StoredAt);

public interface IOriginalEvidenceStore : IAsyncDisposable
{
    Task<StoredOriginalEvidence> StoreAsync(
        TenantId tenantId,
        Guid matterId,
        Guid originalObjectId,
        Stream content,
        CancellationToken cancellationToken = default);

    Task<StoredOriginalEvidence?> GetMetadataAsync(
        TenantId tenantId,
        Guid matterId,
        Guid originalObjectId,
        CancellationToken cancellationToken = default);

    Task<StoredOriginalEvidence> ReadVerifiedAsync(
        TenantId tenantId,
        Guid matterId,
        Guid originalObjectId,
        Stream destination,
        CancellationToken cancellationToken = default);

    Task<StoredOriginalEvidence> VerifyIntegrityAsync(
        TenantId tenantId,
        Guid matterId,
        Guid originalObjectId,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteOriginalAsync(
        TenantId tenantId,
        Guid matterId,
        Guid originalObjectId,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteMatterAsync(
        TenantId tenantId,
        Guid matterId,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteTenantAsync(
        TenantId tenantId,
        CancellationToken cancellationToken = default);
}

public class OriginalEvidenceStorageException : Exception
{
    public OriginalEvidenceStorageException(string message) : base(message) { }
    public OriginalEvidenceStorageException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class OriginalEvidenceNotFoundException : OriginalEvidenceStorageException
{
    public OriginalEvidenceNotFoundException(string message) : base(message) { }
    public OriginalEvidenceNotFoundException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class OriginalEvidenceIntegrityException : OriginalEvidenceStorageException
{
    public OriginalEvidenceIntegrityException(string message) : base(message) { }
}

public sealed class OriginalEvidenceConflictException : OriginalEvidenceStorageException
{
    public OriginalEvidenceConflictException(string message) : base(message) { }
    public OriginalEvidenceConflictException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class OriginalEvidenceAvailabilityException : OriginalEvidenceStorageException
{
    public OriginalEvidenceAvailabilityException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class OriginalEvidenceCompensationException : OriginalEvidenceStorageException
{
    public OriginalEvidenceCompensationException(string message, Exception innerException) : base(message, innerException) { }
}

internal sealed record OriginalObjectIdentity(TenantId TenantId, Guid MatterId, Guid OriginalObjectId);

internal sealed record StorageAddress(string BackendKind, string BucketName, string ObjectKey);

internal sealed record OriginalObjectStorageMetadata(
    OriginalObjectIdentity Identity,
    StorageAddress Address,
    string ContentSha256,
    long ByteLength,
    DateTimeOffset StoredAt)
{
    internal StoredOriginalEvidence ToPublic() => new(
        Identity.TenantId,
        Identity.MatterId,
        Identity.OriginalObjectId,
        ContentSha256,
        ByteLength,
        StoredAt);
}

internal sealed record OriginalObjectState(
    OriginalObjectIdentity Identity,
    string ExpectedSha256,
    OriginalObjectStorageMetadata? Storage);

internal interface IOriginalObjectStorageMetadataRepository
{
    Task<IAsyncDisposable> AcquireStoreLeaseAsync(
        OriginalObjectIdentity identity,
        CancellationToken cancellationToken);

    Task<OriginalObjectState?> ResolveAsync(
        OriginalObjectIdentity identity,
        CancellationToken cancellationToken);

    Task<OriginalObjectStorageMetadata> SaveAsync(
        OriginalObjectStorageMetadata metadata,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<OriginalObjectStorageMetadata>> ListMatterAsync(
        TenantId tenantId,
        Guid matterId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<OriginalObjectStorageMetadata>> ListTenantAsync(
        TenantId tenantId,
        CancellationToken cancellationToken);

    Task<bool> DeleteOriginalMetadataAsync(
        OriginalObjectIdentity identity,
        CancellationToken cancellationToken);

    Task<bool> DeleteMatterAfterObjectsAsync(
        TenantId tenantId,
        Guid matterId,
        IReadOnlyList<OriginalObjectStorageMetadata> deletedObjects,
        CancellationToken cancellationToken);

    Task<bool> DeleteTenantAfterObjectsAsync(
        TenantId tenantId,
        IReadOnlyList<OriginalObjectStorageMetadata> deletedObjects,
        CancellationToken cancellationToken);
}

internal sealed record ObjectCreateResult(bool Created);

internal interface IImmutableObjectBackend : IAsyncDisposable
{
    StorageAddress AddressFor(OriginalObjectIdentity identity);

    Task<ObjectCreateResult> CreateIfAbsentAsync(
        StorageAddress address,
        Stream content,
        long byteLength,
        CancellationToken cancellationToken);

    Task<Stream> OpenReadAsync(StorageAddress address, CancellationToken cancellationToken);

    Task DeleteIfExistsAsync(StorageAddress address, CancellationToken cancellationToken);
}
