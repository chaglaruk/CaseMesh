using Npgsql;

namespace CaseMesh.Persistence.Postgres.Tests;

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

public sealed class PostgresFixture : IAsyncLifetime
{
    private string? _adminRootConnectionString;
    private string? _databaseName;
    private string? _roleName;

    public string AdminConnectionString { get; private set; } = string.Empty;
    public string AppConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        _adminRootConnectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable);
        if (string.IsNullOrWhiteSpace(_adminRootConnectionString))
        {
            return;
        }

        var suffix = Guid.NewGuid().ToString("N");
        _databaseName = $"casemesh_test_{suffix}";
        _roleName = $"casemesh_app_{suffix}";
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
        await migrator.MigrateAsync(AdminConnectionString);

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

        var appBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
        {
            Username = _roleName,
            Password = password,
            Pooling = true,
            MinPoolSize = 0,
            MaxPoolSize = 4
        };
        AppConnectionString = appBuilder.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(_adminRootConnectionString) || _databaseName is null || _roleName is null)
        {
            return;
        }

        NpgsqlConnection.ClearAllPools();
        await using var root = new NpgsqlConnection(_adminRootConnectionString);
        await root.OpenAsync();
        await using var cleanup = new NpgsqlCommand($"""
            SELECT pg_terminate_backend(pid)
            FROM pg_stat_activity
            WHERE datname = '{_databaseName}' AND pid <> pg_backend_pid();
            DROP DATABASE IF EXISTS "{_databaseName}";
            DROP ROLE IF EXISTS "{_roleName}";
            """, root);
        await cleanup.ExecuteNonQueryAsync();
    }
}
