using Amazon.S3.Model;
using CaseMesh.Persistence.Postgres;
using CaseMesh.ProfessionalExport;

namespace CaseMesh.Storage.S3.Tests;

[Collection(StorageCollection.Name)]
public sealed class GeneratedArtifactStorageTests(StorageIntegrationFixture fixture)
{
    private static readonly DateTimeOffset StoredAt = new(2026, 8, 22, 10, 0, 0, TimeSpan.Zero);

    [StorageFact]
    public async Task Readiness_proves_real_private_create_and_delete_access_without_leaving_a_probe()
    {
        await using var store = CreateStore();

        Assert.True(await store.CheckReadinessAsync());

        var missing = await Assert.ThrowsAsync<Amazon.S3.AmazonS3Exception>(() =>
            fixture.S3.GetObjectMetadataAsync(fixture.BucketName, "v1/readiness/runtime-capability-probe"));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);
    }

    [StorageFact]
    public async Task Private_export_bundle_round_trips_and_wrong_tenant_cannot_resolve_or_read_it()
    {
        var (scope, package, bundle) = await ExportAsync("generated-round-trip");
        var identity = Identity(scope, package);
        await using var store = CreateStore();
        await using var source = new MemoryStream(bundle.Content, writable: false);

        var stored = await store.StoreAsync(identity, source, StoredAt.AddHours(1));
        await using var destination = new MemoryStream();
        var verified = await store.ReadVerifiedAsync(identity, destination, StoredAt);
        var wrong = identity with { TenantId = new CaseMesh.Core.Models.TenantId(Guid.NewGuid()) };

        Assert.Equal(bundle.Content, destination.ToArray());
        Assert.Equal(bundle.Sha256, stored.ContentSha256);
        Assert.Equal(stored, verified);
        Assert.Null(await store.GetMetadataAsync(wrong));
        await Assert.ThrowsAsync<GeneratedArtifactNotFoundException>(() =>
            store.ReadVerifiedAsync(wrong, new MemoryStream(), StoredAt));
    }

    [StorageFact]
    public async Task Tampered_export_bytes_are_rejected_before_delivery()
    {
        var (scope, package, bundle) = await ExportAsync("generated-tamper");
        var identity = Identity(scope, package);
        await using var store = CreateStore();
        await using (var source = new MemoryStream(bundle.Content, writable: false))
            await store.StoreAsync(identity, source, StoredAt.AddHours(1));
        await fixture.S3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = fixture.BucketName,
            Key = Key(identity),
            InputStream = new MemoryStream("synthetic-tamper"u8.ToArray()),
            UseChunkEncoding = false
        });

        await Assert.ThrowsAsync<GeneratedArtifactIntegrityException>(() =>
            store.ReadVerifiedAsync(identity, new MemoryStream(), StoredAt));
    }

    [StorageFact]
    public async Task Expired_export_is_not_delivered_and_expiry_cleanup_is_idempotent()
    {
        var (scope, package, bundle) = await ExportAsync("generated-expiry");
        var identity = Identity(scope, package);
        await using var store = CreateStore();
        await using (var source = new MemoryStream(bundle.Content, writable: false))
            await store.StoreAsync(identity, source, StoredAt.AddMinutes(5));

        await Assert.ThrowsAsync<GeneratedArtifactExpiredException>(() =>
            store.ReadVerifiedAsync(identity, new MemoryStream(), StoredAt.AddMinutes(6)));
        Assert.Equal(1, await store.DeleteExpiredAsync(scope.TenantId, StoredAt.AddMinutes(6)));
        Assert.Equal(0, await store.DeleteExpiredAsync(scope.TenantId, StoredAt.AddMinutes(6)));
        Assert.Null(await store.GetMetadataAsync(identity));
    }

    [StorageFact]
    public async Task Matter_privacy_deletion_removes_generated_and_original_bytes_before_relational_cascade()
    {
        var (scope, package, bundle) = await ExportAsync("generated-and-original-deletion");
        var identity = Identity(scope, package);
        await using var originals = fixture.CreateStore();
        await using var originalSource = new MemoryStream(scope.Bytes, writable: false);
        await originals.StoreAsync(scope.TenantId, scope.MatterId, scope.OriginalObjectId, originalSource);
        await using var generated = CreateStore();
        await using (var bundleSource = new MemoryStream(bundle.Content, writable: false))
            await generated.StoreAsync(identity, bundleSource, StoredAt.AddHours(1));

        Assert.True(await generated.DeleteMatterAsync(scope.TenantId, scope.MatterId));
        Assert.True(await originals.DeleteMatterAsync(scope.TenantId, scope.MatterId));

        Assert.False(await fixture.PhysicalExistsAsync(scope));
        Assert.Null(await generated.GetMetadataAsync(identity));
        Assert.False(await fixture.MatterExistsAsync(scope));
        var missing = await Assert.ThrowsAsync<Amazon.S3.AmazonS3Exception>(() =>
            fixture.S3.GetObjectMetadataAsync(fixture.BucketName, Key(identity)));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);
    }

    private async Task<(SyntheticObjectScope Scope, ProfessionalExportPackage Package,
        GeneratedProfessionalExportArtifact Bundle)> ExportAsync(string content)
    {
        var scope = await fixture.CreateScopeAsync(System.Text.Encoding.UTF8.GetBytes(content));
        await using var exports = new PostgresProfessionalExportService(
            fixture.AppConnectionString, new FixedTimeProvider(StoredAt));
        var package = Assert.IsType<ProfessionalExportPackage>(await exports.GenerateAsync(
            new ProfessionalExportRequest(scope.TenantId, scope.MatterId, Guid.NewGuid())));
        return (scope, package,
            package.Artifacts.Single(item => item.Kind == ProfessionalExportArtifactKind.BundleZip));
    }

    private S3GeneratedArtifactStore CreateStore() => new(
        fixture.AppConnectionString, fixture.Options, new FixedTimeProvider(StoredAt));

    private static GeneratedArtifactIdentity Identity(SyntheticObjectScope scope,
        ProfessionalExportPackage package) => new(scope.TenantId, scope.MatterId, package.Run.ExportId,
        (short)ProfessionalExportArtifactKind.BundleZip);

    private static string Key(GeneratedArtifactIdentity identity) =>
        $"v1/tenants/{identity.TenantId.Value:D}/matters/{identity.MatterId:D}/generated/exports/{identity.ExportId:D}/{identity.ArtifactKind}";

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
