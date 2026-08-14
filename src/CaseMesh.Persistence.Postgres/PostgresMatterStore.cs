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
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        RequireId(matterId, nameof(matterId));
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        return InTenantTransactionAsync(tenantId, async (connection, transaction) =>
        {
            var count = await ExecuteAsync(connection, transaction, """
                UPDATE casemesh.matters
                SET title = $3, status = $4, updated_at = $5
                WHERE tenant_id = $1 AND matter_id = $2
                  AND created_at <= $5 AND updated_at <= $5;
                """, cancellationToken, tenantId.Value, matterId, title, status, updatedAt);
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

    private async Task<T> InTenantTransactionAsync<T>(
        TenantId tenantId,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await RejectRlsBypassRoleAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var context = new NpgsqlCommand("SELECT set_config('casemesh.tenant_id', $1, true);", connection, transaction);
            context.Parameters.AddWithValue(tenantId.Value.ToString());
            await context.ExecuteNonQueryAsync(cancellationToken);
            var result = await operation(connection, transaction);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
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
            if (value is null)
            {
                command.Parameters.Add(new NpgsqlParameter { Value = DBNull.Value, NpgsqlDbType = NpgsqlDbType.Unknown });
            }
            else
            {
                command.Parameters.AddWithValue(value);
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
