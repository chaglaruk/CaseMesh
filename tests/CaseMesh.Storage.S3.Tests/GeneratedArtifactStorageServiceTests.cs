using CaseMesh.Core.Models;

namespace CaseMesh.Storage.S3.Tests;

public sealed class GeneratedArtifactStorageServiceTests
{
    [Fact]
    public async Task Metadata_failure_happens_before_physical_create_and_cannot_orphan_bytes()
    {
        var fixture = new ServiceFixture { MetadataSaveFails = true };
        await using var source = new MemoryStream(fixture.Content, writable: false);

        await Assert.ThrowsAsync<IOException>(() => fixture.Service.StoreAsync(
            fixture.Identity, source, fixture.Now.AddHours(1)));

        Assert.Equal(0, fixture.Backend.CreateAttempts);
        Assert.False(fixture.Backend.Exists);
    }

    [Fact]
    public async Task Ambiguous_physical_create_failure_remains_visible_to_privacy_deletion()
    {
        var fixture = new ServiceFixture { PhysicalCreateFailsAfterWrite = true };
        await using var source = new MemoryStream(fixture.Content, writable: false);
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.StoreAsync(
            fixture.Identity, source, fixture.Now.AddHours(1)));

        Assert.True(fixture.Backend.Exists);
        Assert.NotNull(fixture.Metadata.Storage);
        Assert.True(await fixture.Service.DeleteMatterAsync(
            fixture.Identity.TenantId, fixture.Identity.MatterId));
        Assert.False(fixture.Backend.Exists);
        Assert.Null(fixture.Metadata.Storage);
    }

    private sealed class ServiceFixture
    {
        internal DateTimeOffset Now { get; } = new(2026, 8, 22, 10, 0, 0, TimeSpan.Zero);
        internal byte[] Content { get; } = "synthetic-private-export"u8.ToArray();
        internal GeneratedArtifactIdentity Identity { get; } = new(
            new TenantId(Guid.NewGuid()), Guid.NewGuid(), Guid.NewGuid(), 4);
        internal FakeBackend Backend { get; } = new();
        internal FakeMetadata Metadata { get; }
        internal GeneratedArtifactStorageService Service { get; }

        internal bool MetadataSaveFails { set => Metadata.SaveFails = value; }
        internal bool PhysicalCreateFailsAfterWrite { set => Backend.FailCreateAfterWrite = value; }

        internal ServiceFixture()
        {
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Content));
            Metadata = new FakeMetadata(Identity, hash, Content.LongLength);
            Service = new GeneratedArtifactStorageService(
                Backend, Metadata, "synthetic-private-bucket", new FixedTimeProvider(Now));
        }
    }

    private sealed class FakeBackend : IImmutableObjectBackend
    {
        private byte[]? _content;
        internal int CreateAttempts { get; private set; }
        internal bool Exists => _content is not null;
        internal bool FailCreateAfterWrite { get; set; }

        public StorageAddress AddressFor(OriginalObjectIdentity identity) => throw new NotSupportedException();
        public async Task<ObjectCreateResult> CreateIfAbsentAsync(StorageAddress address, Stream content,
            long byteLength, CancellationToken cancellationToken)
        {
            CreateAttempts++;
            if (_content is not null) return new ObjectCreateResult(false);
            await using var copy = new MemoryStream();
            await content.CopyToAsync(copy, cancellationToken);
            _content = copy.ToArray();
            if (FailCreateAfterWrite) throw new IOException("Synthetic ambiguous object-store failure.");
            return new ObjectCreateResult(true);
        }
        public Task<Stream> OpenReadAsync(StorageAddress address, CancellationToken cancellationToken) =>
            _content is null
                ? throw new OriginalEvidenceNotFoundException("Synthetic object missing.")
                : Task.FromResult<Stream>(new MemoryStream(_content, writable: false));
        public Task DeleteIfExistsAsync(StorageAddress address, CancellationToken cancellationToken)
        {
            _content = null;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeMetadata(
        GeneratedArtifactIdentity identity,
        string hash,
        long length) : IGeneratedArtifactMetadataRepository
    {
        internal bool SaveFails { get; set; }
        internal GeneratedArtifactStorageMetadata? Storage { get; private set; }

        public Task<IAsyncDisposable> AcquireStoreLeaseAsync(GeneratedArtifactIdentity requested,
            CancellationToken cancellationToken) => Task.FromResult<IAsyncDisposable>(new Lease());
        public Task<GeneratedArtifactState?> ResolveAsync(GeneratedArtifactIdentity requested,
            CancellationToken cancellationToken) => Task.FromResult<GeneratedArtifactState?>(
                new GeneratedArtifactState(identity, hash, length, Storage));
        public Task<GeneratedArtifactStorageMetadata> SaveAsync(GeneratedArtifactStorageMetadata value,
            CancellationToken cancellationToken)
        {
            if (SaveFails) throw new IOException("Synthetic metadata failure.");
            Storage = value;
            return Task.FromResult(value);
        }
        public Task<IReadOnlyList<GeneratedArtifactStorageMetadata>> ListMatterAsync(TenantId tenantId,
            Guid matterId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GeneratedArtifactStorageMetadata>>(
                Storage is null ? [] : [Storage]);
        public Task<IReadOnlyList<GeneratedArtifactStorageMetadata>> ListTenantAsync(TenantId tenantId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<GeneratedArtifactStorageMetadata>> ListExpiredAsync(TenantId tenantId,
            DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> DeleteMetadataAsync(TenantId tenantId,
            IReadOnlyCollection<GeneratedArtifactIdentity> identities, CancellationToken cancellationToken)
        {
            var removed = Storage is null ? 0 : 1;
            Storage = null;
            return Task.FromResult(removed);
        }

        private sealed class Lease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
