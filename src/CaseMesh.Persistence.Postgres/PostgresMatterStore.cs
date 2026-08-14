using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Core.Workplace;
using Npgsql;
using NpgsqlTypes;

namespace CaseMesh.Persistence.Postgres;

public sealed record PersistedMatter(MatterEvidenceGraph Evidence, WorkplaceMatter Workplace);

public sealed class PostgresMatterStore : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresMatterStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _dataSource = NpgsqlDataSource.Create(connectionString);
    }

    public Task CreateTenantAsync(
        TenantId tenantId,
        string displayName,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        return InTenantTransactionAsync(tenantId, async (connection, transaction) =>
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO casemesh.tenants (tenant_id, display_name, created_at)
                VALUES ($1, $2, $3)
                ON CONFLICT (tenant_id) DO UPDATE
                SET display_name = EXCLUDED.display_name;
                """, cancellationToken, tenantId.Value, displayName, createdAt);
            return true;
        }, cancellationToken);
    }

    public Task SaveAsync(
        MatterEvidenceGraph evidence,
        WorkplaceMatter workplace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(workplace);
        if (!ReferenceEquals(evidence, workplace.Evidence) || workplace.MatterId != evidence.Matter.Id)
        {
            throw new InvalidOperationException("The workplace extension must belong to the persisted evidence graph.");
        }

        var evidenceSnapshot = evidence.CaptureSnapshot();
        var workplaceSnapshot = workplace.CaptureSnapshot();
        return InTenantTransactionAsync(evidence.Matter.TenantId, async (connection, transaction) =>
        {
            await PostgresMatterWriter.WriteAsync(
                connection,
                transaction,
                evidenceSnapshot,
                workplaceSnapshot,
                cancellationToken);
            return true;
        }, cancellationToken);
    }

    public Task<PersistedMatter?> LoadAsync(
        TenantId tenantId,
        Guid matterId,
        CancellationToken cancellationToken = default)
    {
        RequireId(matterId, nameof(matterId));
        return InTenantTransactionAsync(tenantId, (connection, transaction) =>
            PostgresMatterReader.ReadAsync(connection, transaction, tenantId, matterId, cancellationToken), cancellationToken);
    }

    public Task<bool> UpdateMatterAsync(
        TenantId tenantId,
        Guid matterId,
        string title,
        string status,
        DateTimeOffset expectedUpdatedAt,
        DateTimeOffset newUpdatedAt,
        CancellationToken cancellationToken = default)
    {
        RequireId(matterId, nameof(matterId));
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        if (newUpdatedAt <= expectedUpdatedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(newUpdatedAt),
                "A Matter update must advance its explicit timestamp.");
        }

        return InTenantTransactionAsync(tenantId, async (connection, transaction) =>
        {
            var count = await ExecuteAsync(connection, transaction, """
                UPDATE casemesh.matters
                SET title = $3, status = $4, updated_at = $6
                WHERE tenant_id = $1 AND matter_id = $2
                  AND updated_at = $5 AND created_at <= $6;
                """, cancellationToken,
                tenantId.Value, matterId, title, status, expectedUpdatedAt, newUpdatedAt);
            return count == 1;
        }, cancellationToken);
    }

    public Task<bool> DeleteMatterAsync(
        TenantId tenantId,
        Guid matterId,
        CancellationToken cancellationToken = default)
    {
        RequireId(matterId, nameof(matterId));
        return InTenantTransactionAsync(tenantId, async (connection, transaction) =>
        {
            var count = await ExecuteAsync(connection, transaction, """
                DELETE FROM casemesh.matters
                WHERE tenant_id = $1 AND matter_id = $2;
                """, cancellationToken, tenantId.Value, matterId);
            return count == 1;
        }, cancellationToken);
    }

    public Task<bool> DeleteTenantAsync(
        TenantId tenantId,
        CancellationToken cancellationToken = default) =>
        InTenantTransactionAsync(tenantId, async (connection, transaction) =>
        {
            var count = await ExecuteAsync(connection, transaction, """
                DELETE FROM casemesh.tenants WHERE tenant_id = $1;
                """, cancellationToken, tenantId.Value);
            return count == 1;
        }, cancellationToken);

    public async ValueTask DisposeAsync() => await _dataSource.DisposeAsync();

    internal async Task<IAsyncDisposable> AcquireSessionAdvisoryLockAsync(
        string lockName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockName);
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            await RejectRlsBypassRoleAsync(connection, cancellationToken);
            await using (var timeout = new NpgsqlCommand("SET lock_timeout = '30s';", connection))
            {
                await timeout.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var command = new NpgsqlCommand(
                "SELECT pg_advisory_lock(hashtextextended($1, 0));",
                connection);
            command.Parameters.AddWithValue(lockName);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await using (var resetTimeout = new NpgsqlCommand("RESET lock_timeout;", connection))
            {
                await resetTimeout.ExecuteNonQueryAsync(cancellationToken);
            }

            return new AdvisoryLockLease(_dataSource, connection, lockName);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    internal async Task<T> InTenantTransactionAsync<T>(
        TenantId tenantId,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        if (tenantId.Value == Guid.Empty)
        {
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await RejectRlsBypassRoleAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var context = new NpgsqlCommand("SELECT set_config('casemesh.tenant_id', $1, true);", connection, transaction);
        context.Parameters.AddWithValue(tenantId.Value.ToString());
        await context.ExecuteNonQueryAsync(cancellationToken);
        var result = await operation(connection, transaction);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static async Task RejectRlsBypassRoleAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT rolsuper OR rolbypassrls
            FROM pg_roles
            WHERE rolname = current_user;
            """, connection);
        var bypassesRls = await command.ExecuteScalarAsync(cancellationToken);
        if (bypassesRls is not false)
        {
            throw new InvalidOperationException(
                "The commercial Matter store requires a PostgreSQL role without SUPERUSER or BYPASSRLS privileges.");
        }
    }

    private sealed class AdvisoryLockLease(
        NpgsqlDataSource dataSource,
        NpgsqlConnection connection,
        string lockName) : IAsyncDisposable
    {
        private NpgsqlConnection? _connection = connection;

        public async ValueTask DisposeAsync()
        {
            var owned = Interlocked.Exchange(ref _connection, null);
            if (owned is null)
            {
                return;
            }

            Exception? unlockFailure = null;
            try
            {
                await using var unlock = new NpgsqlCommand(
                    "SELECT pg_advisory_unlock(hashtextextended($1, 0));",
                    owned);
                unlock.Parameters.AddWithValue(lockName);
                if (await unlock.ExecuteScalarAsync() is not true)
                {
                    throw new InvalidOperationException("The PostgreSQL storage coordination lock was not held.");
                }
            }
            catch (Exception exception)
            {
                unlockFailure = exception;
                try
                {
                    dataSource.Clear();
                }
                catch
                {
                    // Preserve the unlock failure; the owned connection is still disposed below.
                }
            }

            try
            {
                await owned.DisposeAsync();
            }
            catch when (unlockFailure is not null)
            {
                // Preserve the primary unlock failure rather than masking it with cleanup failure.
            }

            if (unlockFailure is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(unlockFailure).Throw();
            }
        }
    }

    internal static async Task<int> ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params object?[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddParameters(command, values);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static void AddParameters(NpgsqlCommand command, params object?[] values)
    {
        foreach (var value in values)
        {
            var databaseValue = value is DateTimeOffset timestamp
                ? timestamp.ToUniversalTime()
                : value;
            if (databaseValue is null)
            {
                command.Parameters.Add(new NpgsqlParameter { Value = DBNull.Value, NpgsqlDbType = NpgsqlDbType.Unknown });
            }
            else
            {
                command.Parameters.AddWithValue(databaseValue);
            }
        }
    }

    private static void RequireId(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A non-empty id is required.", parameterName);
        }
    }
}
