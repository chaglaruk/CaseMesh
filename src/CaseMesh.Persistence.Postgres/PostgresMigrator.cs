using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace CaseMesh.Persistence.Postgres;

public sealed record AppliedMigration(string Version, string Checksum, DateTimeOffset AppliedAt);

public sealed class PostgresMigrator
{
    private const string ResourcePrefix = "CaseMesh.Persistence.Postgres.Migrations.";

    public async Task<IReadOnlyList<AppliedMigration>> MigrateAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await ExecuteAsync(connection, transaction, """
            SELECT pg_advisory_xact_lock(hashtext('casemesh-schema-migrations'));
            CREATE SCHEMA IF NOT EXISTS casemesh_internal;
            CREATE TABLE IF NOT EXISTS casemesh_internal.schema_migrations (
                version text PRIMARY KEY,
                checksum char(64) NOT NULL,
                applied_at timestamptz NOT NULL
            );
            """, cancellationToken);

        var applied = (await ReadAppliedAsync(connection, transaction, cancellationToken))
            .ToDictionary(item => item.Version, StringComparer.Ordinal);
        foreach (var migration in DiscoverMigrations())
        {
            if (applied.TryGetValue(migration.Version, out var existing))
            {
                if (!string.Equals(existing.Checksum, migration.Checksum, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Applied migration {migration.Version} has a different checksum.");
                }

                continue;
            }

            await ExecuteAsync(connection, transaction, migration.Sql, cancellationToken);
            await using var insert = new NpgsqlCommand("""
                INSERT INTO casemesh_internal.schema_migrations (version, checksum, applied_at)
                VALUES ($1, $2, CURRENT_TIMESTAMP);
                """, connection, transaction);
            insert.Parameters.AddWithValue(migration.Version);
            insert.Parameters.AddWithValue(migration.Checksum);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return await GetAppliedMigrationsAsync(connectionString, cancellationToken);
    }

    public async Task<IReadOnlyList<AppliedMigration>> GetAppliedMigrationsAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadAppliedAsync(connection, null, cancellationToken);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<AppliedMigration>> ReadAppliedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT version, checksum, applied_at
            FROM casemesh_internal.schema_migrations
            ORDER BY version;
            """, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var migrations = new List<AppliedMigration>();
        while (await reader.ReadAsync(cancellationToken))
        {
            migrations.Add(new AppliedMigration(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetFieldValue<DateTimeOffset>(2)));
        }

        return migrations;
    }

    private static IReadOnlyList<MigrationResource> DiscoverMigrations()
    {
        var assembly = typeof(PostgresMigrator).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => ReadMigration(assembly, name))
            .ToArray();
    }

    private static MigrationResource ReadMigration(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Migration resource {resourceName} was not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var sql = reader.ReadToEnd();
        var fileName = resourceName[ResourcePrefix.Length..^4];
        var separator = fileName.IndexOf('_', StringComparison.Ordinal);
        var version = separator > 0 ? fileName[..separator] : fileName;
        if (version.Length == 0 || version.Any(character => !char.IsDigit(character)))
        {
            throw new InvalidOperationException($"Migration {resourceName} does not start with a numeric version.");
        }

        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
        return new MigrationResource(version, checksum, sql);
    }

    private sealed record MigrationResource(string Version, string Checksum, string Sql);
}
