using CaseMesh.Core.Models;
using Npgsql;

namespace CaseMesh.Persistence.Postgres;

public enum WebMembershipRole { Owner = 1, Member = 2 }
public enum WebProcessingStatus { Pending = 1, Processing = 2, Completed = 3, Failed = 4 }

public sealed record WebUser(Guid Id, string Issuer, string Subject, string DisplayName);
public sealed record WebMembership(TenantId TenantId, string WorkspaceName, WebMembershipRole Role);
public sealed record WebMatterSummary(Guid Id, string Title, string Status, string MatterType,
    string? Jurisdiction, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record WebDocumentMetadata(Guid DocumentId, string OriginalFileName, Guid UploadedByUserId,
    DateTimeOffset UploadedAt);
public sealed record WebProcessingJob(TenantId TenantId, Guid MatterId, Guid JobId, Guid DocumentId,
    Guid DocumentVersionId, Guid OriginalObjectId, WebProcessingStatus Status, int Attempts,
    string? FailureCode, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

public sealed class PostgresWebWorkspaceRepository : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _connectionString;

    public PostgresWebWorkspaceRepository(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _dataSource = NpgsqlDataSource.Create(connectionString);
    }

    public async Task<WebUser> UpsertUserAsync(string issuer, string subject, string displayName,
        DateTimeOffset createdAt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        await using var connection = await OpenSafeAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO casemesh.web_users (user_id,issuer,subject,display_name,created_at)
            VALUES ($1,$2,$3,$4,$5)
            ON CONFLICT (issuer,subject) DO UPDATE SET display_name=EXCLUDED.display_name
            RETURNING user_id,issuer,subject,display_name;
            """, connection);
        PostgresMatterStore.AddParameters(command, Guid.NewGuid(), issuer, subject, displayName, createdAt);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new WebUser(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
    }

    public async Task CreateWorkspaceAsync(WebUser user, TenantId tenantId, string displayName,
        DateTimeOffset createdAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        await using (var matterStore = new PostgresMatterStore(_connectionString))
            await matterStore.CreateTenantAsync(tenantId, displayName, createdAt, cancellationToken);
        await InTenantUserTransactionAsync(tenantId, user.Id, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO casemesh.tenant_memberships (tenant_id,user_id,membership_role,created_at)
                VALUES ($1,$2,$3,$4) ON CONFLICT DO NOTHING;
                """, connection, transaction);
            PostgresMatterStore.AddParameters(command, tenantId.Value, user.Id, (short)WebMembershipRole.Owner, createdAt);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<WebMembership>> ListMembershipsAsync(Guid userId,
        CancellationToken cancellationToken = default) =>
        InUserTransactionAsync(userId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                SELECT tenant_id,membership_role
                FROM casemesh.tenant_memberships
                ORDER BY tenant_id;
                """, connection, transaction);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var result = new List<WebMembership>();
            while (await reader.ReadAsync(cancellationToken))
                result.Add(new WebMembership(new TenantId(reader.GetGuid(0)), "CaseMesh workspace",
                    (WebMembershipRole)reader.GetInt16(1)));
            return (IReadOnlyList<WebMembership>)result;
        }, cancellationToken);

    public Task<bool> HasMembershipAsync(Guid userId, TenantId tenantId,
        CancellationToken cancellationToken = default) =>
        InTenantUserTransactionAsync(tenantId, userId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (SELECT 1 FROM casemesh.tenant_memberships
                    WHERE tenant_id=$1 AND user_id=$2);
                """, connection, transaction);
            PostgresMatterStore.AddParameters(command, tenantId.Value, userId);
            return await command.ExecuteScalarAsync(cancellationToken) is true;
        }, cancellationToken);

    public Task<IReadOnlyList<WebMatterSummary>> ListMattersAsync(Guid userId, TenantId tenantId,
        CancellationToken cancellationToken = default) =>
        InAuthorizedTenantTransactionAsync(userId, tenantId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                SELECT matter_id,title,status,matter_type,jurisdiction,created_at,updated_at
                FROM casemesh.matters WHERE tenant_id=$1 ORDER BY updated_at DESC,matter_id;
                """, connection, transaction);
            PostgresMatterStore.AddParameters(command, tenantId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var result = new List<WebMatterSummary>();
            while (await reader.ReadAsync(cancellationToken)) result.Add(ReadMatter(reader));
            return (IReadOnlyList<WebMatterSummary>)result;
        }, cancellationToken);

    public Task<WebMatterSummary?> GetMatterAsync(Guid userId, TenantId tenantId, Guid matterId,
        CancellationToken cancellationToken = default) =>
        InAuthorizedTenantTransactionAsync(userId, tenantId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                SELECT matter_id,title,status,matter_type,jurisdiction,created_at,updated_at
                FROM casemesh.matters WHERE tenant_id=$1 AND matter_id=$2;
                """, connection, transaction);
            PostgresMatterStore.AddParameters(command, tenantId.Value, matterId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadMatter(reader) : null;
        }, cancellationToken);

    public Task AddDocumentJobAsync(Guid userId, TenantId tenantId, Guid matterId,
        Guid jobId, Guid documentId, Guid documentVersionId, Guid originalObjectId,
        string safeFileName, DateTimeOffset createdAt, CancellationToken cancellationToken = default) =>
        InAuthorizedTenantTransactionAsync(userId, tenantId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO casemesh.web_document_metadata
                    (tenant_id,matter_id,document_id,original_file_name,uploaded_by_user_id,uploaded_at)
                VALUES ($1,$2,$3,$4,$5,$6) ON CONFLICT DO NOTHING;
                INSERT INTO casemesh.web_processing_jobs
                    (tenant_id,matter_id,job_id,document_id,document_version_id,original_object_id,
                     status,available_at,created_at)
                VALUES ($1,$2,$7,$3,$8,$9,1,$6,$6) ON CONFLICT DO NOTHING;
                """, connection, transaction);
            PostgresMatterStore.AddParameters(command, tenantId.Value, matterId, documentId, safeFileName,
                userId, createdAt, jobId, documentVersionId, originalObjectId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }, cancellationToken);

    public Task<WebProcessingJob?> GetJobAsync(Guid userId, TenantId tenantId, Guid matterId, Guid jobId,
        CancellationToken cancellationToken = default) =>
        InAuthorizedTenantTransactionAsync(userId, tenantId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                SELECT tenant_id,matter_id,job_id,document_id,document_version_id,original_object_id,
                       status,attempts,failure_code,created_at,completed_at
                FROM casemesh.web_processing_jobs
                WHERE tenant_id=$1 AND matter_id=$2 AND job_id=$3;
                """, connection, transaction);
            PostgresMatterStore.AddParameters(command, tenantId.Value, matterId, jobId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadJob(reader) : null;
        }, cancellationToken);

    public Task<WebProcessingJob?> ClaimAsync(Guid userId, TenantId tenantId, Guid workerId,
        DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken = default) =>
        InAuthorizedTenantTransactionAsync(userId, tenantId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                WITH candidate AS (
                    SELECT tenant_id,matter_id,job_id FROM casemesh.web_processing_jobs
                    WHERE tenant_id=$1 AND (status=1 OR (status=2 AND lease_expires_at <= $2))
                      AND available_at <= $2
                    ORDER BY created_at,job_id FOR UPDATE SKIP LOCKED LIMIT 1
                )
                UPDATE casemesh.web_processing_jobs job
                SET status=2,attempts=attempts+1,lease_owner=$3,lease_expires_at=$4,failure_code=NULL
                FROM candidate WHERE job.tenant_id=candidate.tenant_id
                  AND job.matter_id=candidate.matter_id AND job.job_id=candidate.job_id
                RETURNING job.tenant_id,job.matter_id,job.job_id,job.document_id,
                    job.document_version_id,job.original_object_id,job.status,job.attempts,
                    job.failure_code,job.created_at,job.completed_at;
                """, connection, transaction);
            PostgresMatterStore.AddParameters(command, tenantId.Value, now, workerId, now + leaseDuration);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadJob(reader) : null;
        }, cancellationToken);

    public Task CompleteAsync(Guid userId, TenantId tenantId, Guid matterId, Guid jobId, Guid workerId,
        DateTimeOffset completedAt, CancellationToken cancellationToken = default) =>
        FinishAsync(userId, tenantId, matterId, jobId, workerId, completedAt, true, null, cancellationToken);

    public Task FailAsync(Guid userId, TenantId tenantId, Guid matterId, Guid jobId, Guid workerId,
        DateTimeOffset failedAt, string failureCode, CancellationToken cancellationToken = default) =>
        FinishAsync(userId, tenantId, matterId, jobId, workerId, failedAt, false, failureCode, cancellationToken);

    private Task FinishAsync(Guid userId, TenantId tenantId, Guid matterId, Guid jobId, Guid workerId,
        DateTimeOffset at, bool completed, string? failureCode, CancellationToken cancellationToken) =>
        InAuthorizedTenantTransactionAsync(userId, tenantId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                UPDATE casemesh.web_processing_jobs
                SET status=CASE WHEN $6=4 AND attempts < 3 THEN 1 ELSE $6 END,
                    lease_owner=NULL,lease_expires_at=NULL,failure_code=$7,
                    completed_at=CASE WHEN $6=3 THEN $5 ELSE NULL END,
                    available_at=CASE WHEN $6=4 THEN $5 + interval '5 minutes' ELSE available_at END
                WHERE tenant_id=$1 AND matter_id=$2 AND job_id=$3 AND lease_owner=$4;
                """, connection, transaction);
            PostgresMatterStore.AddParameters(command, tenantId.Value, matterId, jobId, workerId, at,
                (short)(completed ? WebProcessingStatus.Completed : WebProcessingStatus.Failed), failureCode);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("The processing-job lease is missing or no longer owned by this worker.");
            return true;
        }, cancellationToken);

    public async ValueTask DisposeAsync() => await _dataSource.DisposeAsync();

    private Task<T> InAuthorizedTenantTransactionAsync<T>(Guid userId, TenantId tenantId,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> operation, CancellationToken cancellationToken) =>
        InTenantUserTransactionAsync(tenantId, userId, async (connection, transaction) =>
        {
            await using var membership = new NpgsqlCommand("""
                SELECT EXISTS (SELECT 1 FROM casemesh.tenant_memberships
                    WHERE tenant_id=$1 AND user_id=$2);
                """, connection, transaction);
            PostgresMatterStore.AddParameters(membership, tenantId.Value, userId);
            if (await membership.ExecuteScalarAsync(cancellationToken) is not true)
                throw new UnauthorizedAccessException("No workspace membership was found.");
            return await operation(connection, transaction);
        }, cancellationToken);

    private Task<T> InUserTransactionAsync<T>(Guid userId,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> operation, CancellationToken cancellationToken) =>
        InContextTransactionAsync(null, userId, operation, cancellationToken);

    private Task<T> InTenantUserTransactionAsync<T>(TenantId tenantId, Guid userId,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> operation, CancellationToken cancellationToken) =>
        InContextTransactionAsync(tenantId, userId, operation, cancellationToken);

    private async Task<T> InContextTransactionAsync<T>(TenantId? tenantId, Guid userId,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> operation, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty) throw new ArgumentException("User id is required.", nameof(userId));
        await using var connection = await OpenSafeAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var context = new NpgsqlCommand("""
            SELECT set_config('casemesh.user_id',$1,true),
                   set_config('casemesh.tenant_id',$2,true);
            """, connection, transaction);
        PostgresMatterStore.AddParameters(context, userId.ToString(), tenantId?.Value.ToString() ?? string.Empty);
        await context.ExecuteNonQueryAsync(cancellationToken);
        var result = await operation(connection, transaction);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<NpgsqlConnection> OpenSafeAsync(CancellationToken cancellationToken)
    {
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT rolsuper OR rolbypassrls FROM pg_roles WHERE rolname=current_user;
            """, connection);
        if (await command.ExecuteScalarAsync(cancellationToken) is not false)
        {
            await connection.DisposeAsync();
            throw new InvalidOperationException("The Web workspace requires a PostgreSQL role without RLS bypass.");
        }
        return connection;
    }

    private static WebMatterSummary ReadMatter(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5),
        reader.GetFieldValue<DateTimeOffset>(6));

    private static WebProcessingJob ReadJob(NpgsqlDataReader reader) => new(
        new TenantId(reader.GetGuid(0)), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3),
        reader.GetGuid(4), reader.GetGuid(5), (WebProcessingStatus)reader.GetInt16(6), reader.GetInt32(7),
        reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetFieldValue<DateTimeOffset>(9),
        reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10));
}
