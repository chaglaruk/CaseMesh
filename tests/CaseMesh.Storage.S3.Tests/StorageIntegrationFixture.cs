using System.Net;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Core.Workplace;
using CaseMesh.Persistence.Postgres;
using Npgsql;

namespace CaseMesh.Storage.S3.Tests;

[CollectionDefinition(Name)]
public sealed class StorageCollection : ICollectionFixture<StorageIntegrationFixture>
{
    public const string Name = "storage";
}

public sealed class StorageIntegrationFixture : IAsyncLifetime
{
    private string? _adminRootConnectionString;
    private string? _databaseName;
    private string? _roleName;
    private AmazonS3Client? _s3;

    public string AdminConnectionString { get; private set; } = string.Empty;
    public string AppConnectionString { get; private set; } = string.Empty;
    public string BucketName { get; private set; } = string.Empty;
    public S3ObjectStorageOptions Options { get; private set; } = null!;
    internal AmazonS3Client S3 => _s3 ?? throw new InvalidOperationException("Storage fixture is not initialized.");

    public async Task InitializeAsync()
    {
        _adminRootConnectionString = Environment.GetEnvironmentVariable(StorageFactAttribute.PostgresVariable);
        var endpoint = Environment.GetEnvironmentVariable(StorageFactAttribute.EndpointVariable);
        var accessKey = Environment.GetEnvironmentVariable(StorageFactAttribute.AccessKeyVariable);
        var secretKey = Environment.GetEnvironmentVariable(StorageFactAttribute.SecretKeyVariable);
        if (string.IsNullOrWhiteSpace(_adminRootConnectionString) ||
            string.IsNullOrWhiteSpace(endpoint) ||
            string.IsNullOrWhiteSpace(accessKey) ||
            string.IsNullOrWhiteSpace(secretKey))
        {
            return;
        }

        var suffix = Guid.NewGuid().ToString("N");
        _databaseName = $"casemesh_storage_{suffix}";
        _roleName = $"casemesh_storage_app_{suffix}";
        var password = $"synthetic-{Guid.NewGuid():N}";
        var rootBuilder = new NpgsqlConnectionStringBuilder(_adminRootConnectionString);

        await using (var root = new NpgsqlConnection(rootBuilder.ConnectionString))
        {
            await root.OpenAsync();
            await using var createDatabase = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\";", root);
            await createDatabase.ExecuteNonQueryAsync();
        }

        var adminBuilder = new NpgsqlConnectionStringBuilder(rootBuilder.ConnectionString) { Database = _databaseName };
        AdminConnectionString = adminBuilder.ConnectionString;
        var migrator = new PostgresMigrator();
        await migrator.MigrateThroughAsync(AdminConnectionString, "0001");

        await using (var admin = new NpgsqlConnection(AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var createRole = new NpgsqlCommand($"""
                CREATE ROLE "{_roleName}" LOGIN PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS;
                GRANT CONNECT ON DATABASE "{_databaseName}" TO "{_roleName}";
                GRANT USAGE ON SCHEMA casemesh TO "{_roleName}";
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA casemesh TO "{_roleName}";
                """, admin);
            await createRole.ExecuteNonQueryAsync();
        }
        await migrator.MigrateAsync(AdminConnectionString);

        AppConnectionString = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
        {
            Username = _roleName,
            Password = password,
            Pooling = true,
            MinPoolSize = 0,
            MaxPoolSize = 8
        }.ConnectionString;

        BucketName = $"casemesh-synthetic-{suffix}";
        Options = new S3ObjectStorageOptions
        {
            Endpoint = new Uri(endpoint, UriKind.Absolute),
            Region = "us-east-1",
            BucketName = BucketName,
            AccessKey = accessKey,
            SecretKey = secretKey,
            AllowInsecureLocalEndpoint = true
        };
        _s3 = new AmazonS3Client(
            new BasicAWSCredentials(accessKey, secretKey),
            new AmazonS3Config
            {
                ServiceURL = endpoint.TrimEnd('/'),
                AuthenticationRegion = "us-east-1",
                ForcePathStyle = true,
                UseHttp = new Uri(endpoint).Scheme == Uri.UriSchemeHttp
            });
        await S3.PutBucketAsync(new PutBucketRequest { BucketName = BucketName });
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (_s3 is not null)
            {
                try
                {
                    string? token = null;
                    do
                    {
                        var listed = await _s3.ListObjectsV2Async(new ListObjectsV2Request
                        {
                            BucketName = BucketName,
                            ContinuationToken = token
                        });
                        foreach (var item in listed.S3Objects ?? [])
                        {
                            await _s3.DeleteObjectAsync(BucketName, item.Key);
                        }

                        token = listed.IsTruncated == true ? listed.NextContinuationToken : null;
                    } while (token is not null);

                    await _s3.DeleteBucketAsync(BucketName);
                }
                finally
                {
                    _s3.Dispose();
                }
            }
        }
        finally
        {
            await DropPostgresAsync();
        }
    }

    private async Task DropPostgresAsync()
    {
        if (string.IsNullOrWhiteSpace(_adminRootConnectionString) || _databaseName is null || _roleName is null)
        {
            return;
        }

        NpgsqlConnection.ClearAllPools();
        await using var root = new NpgsqlConnection(_adminRootConnectionString);
        await root.OpenAsync();
        await using (var dropDatabase = new NpgsqlCommand(
                         $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE);",
                         root))
        {
            await dropDatabase.ExecuteNonQueryAsync();
        }

        await using var dropRole = new NpgsqlCommand($"DROP ROLE IF EXISTS \"{_roleName}\";", root);
        await dropRole.ExecuteNonQueryAsync();
    }

    internal S3OriginalEvidenceStore CreateStore() => CreateStore(BucketName);

    internal S3OriginalEvidenceStore CreateStore(string bucketName) => new(
        AppConnectionString,
        new S3ObjectStorageOptions
        {
            Endpoint = Options.Endpoint,
            Region = Options.Region,
            BucketName = bucketName,
            AccessKey = Options.AccessKey,
            SecretKey = Options.SecretKey,
            AllowInsecureLocalEndpoint = Options.AllowInsecureLocalEndpoint
        });

    internal async Task<SyntheticObjectScope> CreateScopeAsync(
        byte[] bytes,
        TenantId? tenantId = null,
        Guid? matterId = null,
        Guid? originalObjectId = null,
        bool duplicateVersion = false,
        string title = "Synthetic workplace matter")
    {
        var tenant = tenantId ?? new TenantId(Guid.NewGuid());
        var matter = matterId ?? Guid.NewGuid();
        var original = originalObjectId ?? Guid.NewGuid();
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var timestamp = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var root = new Matter(matter, tenant, "workplace-dispute", title, "Open", timestamp, timestamp, "England and Wales");
        var evidence = new MatterEvidenceGraph(root);
        var first = evidence.RegisterDocumentVersion(Guid.NewGuid(), Guid.NewGuid(), hash, original);
        if (duplicateVersion)
        {
            var second = evidence.RegisterDocumentVersion(Guid.NewGuid(), Guid.NewGuid(), hash, Guid.NewGuid());
            Assert.Equal(first.OriginalObjectId, second.OriginalObjectId);
        }

        var workplace = new WorkplaceMatter(evidence);
        await using var postgres = new PostgresMatterStore(AppConnectionString);
        await postgres.CreateTenantAsync(tenant, "Synthetic tenant", timestamp);
        await postgres.SaveAsync(evidence, workplace);
        return new SyntheticObjectScope(tenant, matter, first.OriginalObjectId, hash, bytes);
    }

    internal string KeyFor(SyntheticObjectScope scope) =>
        $"v1/tenants/{scope.TenantId.Value:D}/matters/{scope.MatterId:D}/originals/{scope.OriginalObjectId:D}";

    internal Task<bool> PhysicalExistsAsync(SyntheticObjectScope scope) =>
        PhysicalExistsAsync(BucketName, scope);

    internal async Task<bool> PhysicalExistsAsync(string bucketName, SyntheticObjectScope scope)
    {
        try
        {
            await S3.GetObjectMetadataAsync(bucketName, KeyFor(scope));
            return true;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    internal async Task DeleteVersionedBucketAsync(string bucketName)
    {
        string? keyMarker = null;
        string? versionIdMarker = null;
        do
        {
            var listed = await S3.ListVersionsAsync(new ListVersionsRequest
            {
                BucketName = bucketName,
                KeyMarker = keyMarker,
                VersionIdMarker = versionIdMarker
            });
            foreach (var version in listed.Versions ?? [])
            {
                await S3.DeleteObjectAsync(new DeleteObjectRequest
                {
                    BucketName = bucketName,
                    Key = version.Key,
                    VersionId = version.VersionId
                });
            }

            keyMarker = listed.IsTruncated == true ? listed.NextKeyMarker : null;
            versionIdMarker = listed.IsTruncated == true ? listed.NextVersionIdMarker : null;
        } while (keyMarker is not null);

        await S3.DeleteBucketAsync(bucketName);
    }

    internal async Task<string?> ReadStoredKeyAsync(SyntheticObjectScope scope)
    {
        await using var connection = new NpgsqlConnection(AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var context = new NpgsqlCommand(
                         "SELECT set_config('casemesh.tenant_id', $1, true);",
                         connection,
                         transaction))
        {
            context.Parameters.AddWithValue(scope.TenantId.Value.ToString());
            await context.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand("""
            SELECT object_key FROM casemesh.original_object_storage
            WHERE tenant_id = $1 AND matter_id = $2 AND original_object_id = $3;
            """, connection, transaction);
        command.Parameters.AddWithValue(scope.TenantId.Value);
        command.Parameters.AddWithValue(scope.MatterId);
        command.Parameters.AddWithValue(scope.OriginalObjectId);
        var value = await command.ExecuteScalarAsync();
        await transaction.CommitAsync();
        return value as string;
    }

    internal async Task<bool> MatterExistsAsync(SyntheticObjectScope scope)
    {
        await using var postgres = new PostgresMatterStore(AppConnectionString);
        return await postgres.LoadAsync(scope.TenantId, scope.MatterId) is not null;
    }
}

internal sealed record SyntheticObjectScope(
    TenantId TenantId,
    Guid MatterId,
    Guid OriginalObjectId,
    string ContentSha256,
    byte[] Bytes);
