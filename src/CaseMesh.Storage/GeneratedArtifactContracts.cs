using CaseMesh.Core.Models;

namespace CaseMesh.Storage;

public sealed record GeneratedArtifactIdentity(
    TenantId TenantId,
    Guid MatterId,
    Guid ExportId,
    short ArtifactKind);

public sealed record StoredGeneratedArtifact(
    GeneratedArtifactIdentity Identity,
    string ContentSha256,
    long ByteLength,
    DateTimeOffset StoredAt,
    DateTimeOffset ExpiresAt);

public interface IGeneratedArtifactStore : IAsyncDisposable
{
    Task<StoredGeneratedArtifact> StoreAsync(
        GeneratedArtifactIdentity identity,
        Stream content,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default);

    Task<StoredGeneratedArtifact?> GetMetadataAsync(
        GeneratedArtifactIdentity identity,
        CancellationToken cancellationToken = default);

    Task<StoredGeneratedArtifact> ReadVerifiedAsync(
        GeneratedArtifactIdentity identity,
        Stream destination,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteMatterAsync(
        TenantId tenantId,
        Guid matterId,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteTenantAsync(
        TenantId tenantId,
        CancellationToken cancellationToken = default);

    Task<int> DeleteExpiredAsync(
        TenantId tenantId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<bool> CheckReadinessAsync(CancellationToken cancellationToken = default);
}

public class GeneratedArtifactStorageException : Exception
{
    public GeneratedArtifactStorageException(string message) : base(message) { }
    public GeneratedArtifactStorageException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class GeneratedArtifactNotFoundException(string message) : GeneratedArtifactStorageException(message);
public sealed class GeneratedArtifactExpiredException(string message) : GeneratedArtifactStorageException(message);
public sealed class GeneratedArtifactIntegrityException(string message) : GeneratedArtifactStorageException(message);
public sealed class GeneratedArtifactConflictException(string message) : GeneratedArtifactStorageException(message);
public sealed class GeneratedArtifactAvailabilityException(string message, Exception innerException)
    : GeneratedArtifactStorageException(message, innerException);

internal sealed record GeneratedArtifactStorageMetadata(
    GeneratedArtifactIdentity Identity,
    StorageAddress Address,
    string ContentSha256,
    long ByteLength,
    DateTimeOffset StoredAt,
    DateTimeOffset ExpiresAt)
{
    internal StoredGeneratedArtifact ToPublic() => new(
        Identity, ContentSha256, ByteLength, StoredAt, ExpiresAt);
}

internal sealed record GeneratedArtifactState(
    GeneratedArtifactIdentity Identity,
    string ExpectedSha256,
    long ExpectedByteLength,
    GeneratedArtifactStorageMetadata? Storage);

internal interface IGeneratedArtifactMetadataRepository
{
    Task<IAsyncDisposable> AcquireStoreLeaseAsync(
        GeneratedArtifactIdentity identity,
        CancellationToken cancellationToken);

    Task<GeneratedArtifactState?> ResolveAsync(
        GeneratedArtifactIdentity identity,
        CancellationToken cancellationToken);

    Task<GeneratedArtifactStorageMetadata> SaveAsync(
        GeneratedArtifactStorageMetadata metadata,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<GeneratedArtifactStorageMetadata>> ListMatterAsync(
        TenantId tenantId,
        Guid matterId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<GeneratedArtifactStorageMetadata>> ListTenantAsync(
        TenantId tenantId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<GeneratedArtifactStorageMetadata>> ListExpiredAsync(
        TenantId tenantId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<int> DeleteMetadataAsync(
        TenantId tenantId,
        IReadOnlyCollection<GeneratedArtifactIdentity> identities,
        CancellationToken cancellationToken);
}
