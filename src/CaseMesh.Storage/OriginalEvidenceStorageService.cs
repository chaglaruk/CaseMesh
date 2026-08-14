using System.Buffers;
using System.Security.Cryptography;
using CaseMesh.Core.Models;

namespace CaseMesh.Storage;

public sealed class OriginalEvidenceStorageService : IOriginalEvidenceStore
{
    private const int BufferSize = 64 * 1024;
    private readonly IImmutableObjectBackend _backend;
    private readonly IOriginalObjectStorageMetadataRepository _metadata;
    private readonly TimeProvider _timeProvider;

    internal OriginalEvidenceStorageService(
        IImmutableObjectBackend backend,
        IOriginalObjectStorageMetadataRepository metadata,
        TimeProvider? timeProvider = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<StoredOriginalEvidence> StoreAsync(
        TenantId tenantId,
        Guid matterId,
        Guid originalObjectId,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
        {
            throw new ArgumentException("The evidence content stream must be readable.", nameof(content));
        }

        var identity = RequireIdentity(tenantId, matterId, originalObjectId);
        var state = await _metadata.ResolveAsync(identity, cancellationToken)
            ?? throw new OriginalEvidenceNotFoundException("The tenant-scoped original-object identity was not found.");

        await using var staged = await StagedContent.CreateAsync(content, cancellationToken);
        if (!string.Equals(staged.ContentSha256, state.ExpectedSha256, StringComparison.Ordinal) )
        {
            throw new OriginalEvidenceIntegrityException(
                "The streamed evidence bytes do not match the registered original-object SHA-256 identity.");
        }

        var address = _backend.AddressFor(identity);
        if (state.Storage is not null)
        {
            RequireMatchingMetadata(state.Storage, address, staged.ContentSha256, staged.ByteLength);
            await VerifyPhysicalAsync(state.Storage, cancellationToken);
            return state.Storage.ToPublic();
        }

        var create = await _backend.CreateIfAbsentAsync(
            address,
            staged.Stream,
            staged.ByteLength,
            cancellationToken);
        if (!create.Created)
        {
            var candidate = new OriginalObjectStorageMetadata(
                identity,
                address,
                staged.ContentSha256,
                staged.ByteLength,
                _timeProvider.GetUtcNow());
            await VerifyPhysicalAsync(candidate, cancellationToken);
        }

        var proposed = new OriginalObjectStorageMetadata(
            identity,
            address,
            staged.ContentSha256,
            staged.ByteLength,
            _timeProvider.GetUtcNow());
        try
        {
            var persisted = await _metadata.SaveAsync(proposed, cancellationToken);
            RequireMatchingMetadata(persisted, address, staged.ContentSha256, staged.ByteLength);
            return persisted.ToPublic();
        }
        catch (Exception metadataFailure)
        {
            if (!create.Created)
            {
                throw;
            }

            await CompensateNewObjectAsync(identity, address, metadataFailure, CancellationToken.None);
            throw;
        }
    }

    public async Task<StoredOriginalEvidence?> GetMetadataAsync(
        TenantId tenantId,
        Guid matterId,
        Guid originalObjectId,
        CancellationToken cancellationToken = default)
    {
        var state = await _metadata.ResolveAsync(
            RequireIdentity(tenantId, matterId, originalObjectId),
            cancellationToken);
        return state?.Storage?.ToPublic();
    }

    public async Task<StoredOriginalEvidence> ReadVerifiedAsync(
        TenantId tenantId,
        Guid matterId,
        Guid originalObjectId,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The evidence destination stream must be writable.", nameof(destination));
        }

        var metadata = await RequireStoredAsync(tenantId, matterId, originalObjectId, cancellationToken);
        await using var verified = await StagePhysicalAsync(metadata, cancellationToken);
        await verified.Stream.CopyToAsync(destination, BufferSize, cancellationToken);
        return metadata.ToPublic();
    }

    public async Task<StoredOriginalEvidence> VerifyIntegrityAsync(
        TenantId tenantId,
        Guid matterId,
        Guid originalObjectId,
        CancellationToken cancellationToken = default)
    {
        var metadata = await RequireStoredAsync(tenantId, matterId, originalObjectId, cancellationToken);
        await VerifyPhysicalAsync(metadata, cancellationToken);
        return metadata.ToPublic();
    }

    public async Task<bool> DeleteOriginalAsync(
        TenantId tenantId,
        Guid matterId,
        Guid originalObjectId,
        CancellationToken cancellationToken = default)
    {
        var identity = RequireIdentity(tenantId, matterId, originalObjectId);
        var state = await _metadata.ResolveAsync(identity, cancellationToken);
        if (state?.Storage is null)
        {
            return false;
        }

        await _backend.DeleteIfExistsAsync(state.Storage.Address, cancellationToken);
        await _metadata.DeleteOriginalMetadataAsync(identity, cancellationToken);
        return true;
    }

    public async Task<bool> DeleteMatterAsync(
        TenantId tenantId,
        Guid matterId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(matterId, nameof(matterId));
        var objects = await _metadata.ListMatterAsync(tenantId, matterId, cancellationToken);
        foreach (var item in objects)
        {
            await _backend.DeleteIfExistsAsync(item.Address, cancellationToken);
        }

        return await _metadata.DeleteMatterAfterObjectsAsync(tenantId, matterId, objects, cancellationToken);
    }

    public async Task<bool> DeleteTenantAsync(
        TenantId tenantId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        var objects = await _metadata.ListTenantAsync(tenantId, cancellationToken);
        foreach (var item in objects)
        {
            await _backend.DeleteIfExistsAsync(item.Address, cancellationToken);
        }

        return await _metadata.DeleteTenantAfterObjectsAsync(tenantId, objects, cancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<OriginalObjectStorageMetadata> RequireStoredAsync(
        TenantId tenantId,
        Guid matterId,
        Guid originalObjectId,
        CancellationToken cancellationToken)
    {
        var state = await _metadata.ResolveAsync(
            RequireIdentity(tenantId, matterId, originalObjectId),
            cancellationToken);
        return state?.Storage
            ?? throw new OriginalEvidenceNotFoundException("Stored evidence metadata was not found for this tenant scope.");
    }

    private async Task VerifyPhysicalAsync(
        OriginalObjectStorageMetadata metadata,
        CancellationToken cancellationToken)
    {
        await using var ignored = await StagePhysicalAsync(metadata, cancellationToken);
    }

    private async Task<StagedContent> StagePhysicalAsync(
        OriginalObjectStorageMetadata metadata,
        CancellationToken cancellationToken)
    {
        Stream source;
        try
        {
            source = await _backend.OpenReadAsync(metadata.Address, cancellationToken);
        }
        catch (OriginalEvidenceNotFoundException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new OriginalEvidenceNotFoundException("The physical evidence object could not be opened.", exception);
        }

        await using (source)
        {
            var staged = await StagedContent.CreateAsync(source, cancellationToken);
            if (staged.ByteLength != metadata.ByteLength ||
                !string.Equals(staged.ContentSha256, metadata.ContentSha256, StringComparison.Ordinal))
            {
                await staged.DisposeAsync();
                throw new OriginalEvidenceIntegrityException(
                    "The physical evidence object does not match its persisted SHA-256 and byte-length metadata.");
            }

            return staged;
        }
    }

    private async Task CompensateNewObjectAsync(
        OriginalObjectIdentity identity,
        StorageAddress address,
        Exception metadataFailure,
        CancellationToken cancellationToken)
    {
        try
        {
            var current = await _metadata.ResolveAsync(identity, cancellationToken);
            if (current?.Storage is not null)
            {
                return;
            }

            await _backend.DeleteIfExistsAsync(address, cancellationToken);
        }
        catch (Exception compensationFailure) when (compensationFailure is not OperationCanceledException)
        {
            throw new OriginalEvidenceCompensationException(
                "Storage metadata persistence failed and compensating object cleanup could not be confirmed; retry is required.",
                new AggregateException(metadataFailure, compensationFailure));
        }
    }

    private static void RequireMatchingMetadata(
        OriginalObjectStorageMetadata metadata,
        StorageAddress address,
        string contentSha256,
        long byteLength)
    {
        if (metadata.Address != address ||
            !string.Equals(metadata.ContentSha256, contentSha256, StringComparison.Ordinal) ||
            metadata.ByteLength != byteLength)
        {
            throw new OriginalEvidenceConflictException(
                "Stored evidence metadata diverges from the immutable original-object identity.");
        }
    }

    private static OriginalObjectIdentity RequireIdentity(
        TenantId tenantId,
        Guid matterId,
        Guid originalObjectId)
    {
        RequireTenant(tenantId);
        RequireId(matterId, nameof(matterId));
        RequireId(originalObjectId, nameof(originalObjectId));
        return new OriginalObjectIdentity(tenantId, matterId, originalObjectId);
    }

    private static void RequireTenant(TenantId tenantId)
    {
        if (tenantId.Value == Guid.Empty)
        {
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));
        }
    }

    private static void RequireId(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A non-empty id is required.", parameterName);
        }
    }

    private sealed class StagedContent : IAsyncDisposable
    {
        private readonly string _path;

        private StagedContent(string path, FileStream stream, string contentSha256, long byteLength)
        {
            _path = path;
            Stream = stream;
            ContentSha256 = contentSha256;
            ByteLength = byteLength;
        }

        internal FileStream Stream { get; }
        internal string ContentSha256 { get; }
        internal long ByteLength { get; }

        internal static async Task<StagedContent> CreateAsync(
            Stream source,
            CancellationToken cancellationToken)
        {
            var path = Path.Combine(Path.GetTempPath(), $"casemesh-{Guid.NewGuid():N}.tmp");
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                BufferSize = BufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            var stream = new FileStream(path, options);
            try
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                long length = 0;
                try
                {
                    while (true)
                    {
                        var read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken);
                        if (read == 0)
                        {
                            break;
                        }

                        hash.AppendData(buffer, 0, read);
                        await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        length = checked(length + read);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                }

                await stream.FlushAsync(cancellationToken);
                stream.Position = 0;
                return new StagedContent(path, stream, Convert.ToHexString(hash.GetHashAndReset()), length);
            }
            catch
            {
                await stream.DisposeAsync();
                TryDelete(path);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Stream.DisposeAsync();
            TryDelete(_path);
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
