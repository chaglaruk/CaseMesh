using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Core.Workplace;
using CaseMesh.Ingestion;
using CaseMesh.Persistence.Postgres;
using CaseMesh.Storage.S3;
using Npgsql;

namespace CaseMesh.Ingestion.Tests;

[CollectionDefinition(Name)]
public sealed class IngestionCollection : ICollectionFixture<IngestionIntegrationFixture>
{
    public const string Name = "ingestion-integration";
}

public sealed class IngestionIntegrationFixture : IAsyncLifetime
{
    private string? _rootConnection;
    private string? _database;
    private string? _role;
    private AmazonS3Client? _s3;

    public string AdminConnection { get; private set; } = string.Empty;
    public string AppConnection { get; private set; } = string.Empty;
    public S3ObjectStorageOptions StorageOptions { get; private set; } = null!;
    public string OcrImagePath => Environment.GetEnvironmentVariable(IngestionFactAttribute.OcrImage) ?? string.Empty;

    public async Task InitializeAsync()
    {
        _rootConnection = Environment.GetEnvironmentVariable(IngestionFactAttribute.Postgres);
        var endpoint = Environment.GetEnvironmentVariable(IngestionFactAttribute.Endpoint);
        var access = Environment.GetEnvironmentVariable(IngestionFactAttribute.AccessKey);
        var secret = Environment.GetEnvironmentVariable(IngestionFactAttribute.SecretKey);
        if (string.IsNullOrWhiteSpace(_rootConnection) || string.IsNullOrWhiteSpace(endpoint) ||
            string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(secret)) return;

        var suffix = Guid.NewGuid().ToString("N");
        _database = $"casemesh_ingestion_{suffix}";
        _role = $"casemesh_ingestion_app_{suffix}";
        var password = $"synthetic-{Guid.NewGuid():N}";
        var rootBuilder = new NpgsqlConnectionStringBuilder(_rootConnection);
        await using (var root = new NpgsqlConnection(rootBuilder.ConnectionString))
        {
            await root.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{_database}\";", root);
            await create.ExecuteNonQueryAsync();
        }

        AdminConnection = new NpgsqlConnectionStringBuilder(rootBuilder.ConnectionString) { Database = _database }.ConnectionString;
        var migrator = new PostgresMigrator();
        await migrator.MigrateThroughAsync(AdminConnection, "0001");
        await using (var admin = new NpgsqlConnection(AdminConnection))
        {
            await admin.OpenAsync();
            await using var role = new NpgsqlCommand($"""
                CREATE ROLE "{_role}" LOGIN PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS;
                GRANT CONNECT ON DATABASE "{_database}" TO "{_role}";
                GRANT USAGE ON SCHEMA casemesh TO "{_role}";
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA casemesh TO "{_role}";
                """, admin);
            await role.ExecuteNonQueryAsync();
        }
        await migrator.MigrateAsync(AdminConnection);

        AppConnection = new NpgsqlConnectionStringBuilder(AdminConnection)
        {
            Username = _role,
            Password = password,
            Pooling = true,
            MaxPoolSize = 8
        }.ConnectionString;
        var bucket = $"casemesh-ingestion-{suffix}";
        StorageOptions = new S3ObjectStorageOptions
        {
            Endpoint = new Uri(endpoint),
            Region = "us-east-1",
            BucketName = bucket,
            AccessKey = access,
            SecretKey = secret,
            AllowInsecureLocalEndpoint = true
        };
        _s3 = new AmazonS3Client(new BasicAWSCredentials(access, secret), new AmazonS3Config
        {
            ServiceURL = endpoint.TrimEnd('/'),
            AuthenticationRegion = "us-east-1",
            ForcePathStyle = true,
            UseHttp = new Uri(endpoint).Scheme == Uri.UriSchemeHttp
        });
        await _s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (_s3 is not null)
            {
                string? continuationToken = null;
                do
                {
                    var objects = await _s3.ListObjectsV2Async(new ListObjectsV2Request
                    {
                        BucketName = StorageOptions.BucketName,
                        ContinuationToken = continuationToken
                    });
                    foreach (var item in objects.S3Objects ?? [])
                        await _s3.DeleteObjectAsync(StorageOptions.BucketName, item.Key);
                    continuationToken = objects.IsTruncated == true ? objects.NextContinuationToken : null;
                } while (continuationToken is not null);
                await _s3.DeleteBucketAsync(StorageOptions.BucketName);
            }
        }
        finally
        {
            _s3?.Dispose();
            await DropDatabaseAsync();
        }
    }

    private async Task DropDatabaseAsync()
    {
        if (string.IsNullOrWhiteSpace(_rootConnection) || _database is null || _role is null) return;
        NpgsqlConnection.ClearAllPools();
        await using var root = new NpgsqlConnection(_rootConnection);
        await root.OpenAsync();
        await using (var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE);", root))
            await drop.ExecuteNonQueryAsync();
        await using (var role = new NpgsqlCommand($"DROP ROLE IF EXISTS \"{_role}\";", root))
            await role.ExecuteNonQueryAsync();
    }

    public async Task<IntegrationScope> CreateScopeAsync(byte[] bytes)
    {
        var tenant = new TenantId(Guid.NewGuid());
        var matter = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var originalId = Guid.NewGuid();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        var now = new DateTimeOffset(2026, 8, 14, 15, 0, 0, TimeSpan.Zero);
        var graph = new MatterEvidenceGraph(new Matter(matter, tenant, "workplace-dispute",
            "Synthetic ingestion matter", "Open", now, now, "England and Wales"));
        graph.RegisterDocumentVersion(documentId, versionId, hash, originalId);
        await using (var store = new PostgresMatterStore(AppConnection))
        {
            await store.CreateTenantAsync(tenant, "Synthetic ingestion tenant", now);
            await store.SaveAsync(graph, new WorkplaceMatter(graph));
        }
        await using (var storage = CreateStorage())
        await using (var content = new MemoryStream(bytes, writable: false))
            await storage.StoreAsync(tenant, matter, originalId, content);
        return new IntegrationScope(new IngestionDocument(tenant, matter, documentId, versionId, originalId), bytes);
    }

    public async Task<IntegrationScope> AddDocumentVersionAsync(IntegrationScope existing, byte[] bytes)
    {
        await using var postgres = new PostgresMatterStore(AppConnection);
        var persisted = await postgres.LoadAsync(existing.Document.TenantId, existing.Document.MatterId)
            ?? throw new InvalidOperationException("Synthetic Matter was not found.");
        var documentId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var originalId = Guid.NewGuid();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        persisted.Evidence.RegisterDocumentVersion(documentId, versionId, hash, originalId);
        await postgres.SaveAsync(persisted.Evidence, persisted.Workplace);
        await using (var storage = CreateStorage())
        await using (var content = new MemoryStream(bytes, writable: false))
            await storage.StoreAsync(existing.Document.TenantId, existing.Document.MatterId, originalId, content);
        return new IntegrationScope(new IngestionDocument(existing.Document.TenantId,
            existing.Document.MatterId, documentId, versionId, originalId), bytes);
    }

    public S3OriginalEvidenceStore CreateStorage() => new(AppConnection, StorageOptions);
}

public sealed record IntegrationScope(IngestionDocument Document, byte[] Bytes);
