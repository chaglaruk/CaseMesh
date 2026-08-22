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
public sealed record WebJobScope(Guid UserId, TenantId TenantId);

public sealed class PostgresWebWorkspaceRepository : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresWebWorkspaceRepository(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
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
        await InUserTransactionAsync(user.Id, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand(
                "SELECT casemesh.create_owned_workspace($1,$2,$3,$4);", connection, transaction);
            PostgresMatterStore.AddParameters(command, user.Id, tenantId.Value, displayName, createdAt);
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
            await using (var metadata = new NpgsqlCommand("""
                INSERT INTO casemesh.web_document_metadata
                    (tenant_id,matter_id,document_id,original_file_name,uploaded_by_user_id,uploaded_at)
                VALUES ($1,$2,$3,$4,$5,$6) ON CONFLICT DO NOTHING;
                """, connection, transaction))
            {
                PostgresMatterStore.AddParameters(metadata, tenantId.Value, matterId, documentId, safeFileName,
                    userId, createdAt);
                await metadata.ExecuteNonQueryAsync(cancellationToken);
            }
            await using var job = new NpgsqlCommand("""
                INSERT INTO casemesh.web_processing_jobs
                    (tenant_id,matter_id,job_id,document_id,document_version_id,original_object_id,
                     requested_by_user_id,status,available_at,created_at)
                VALUES ($1,$2,$3,$4,$5,$6,$7,1,$8,$8) ON CONFLICT DO NOTHING;
                """, connection, transaction);
            PostgresMatterStore.AddParameters(job, tenantId.Value, matterId, jobId, documentId,
                documentVersionId, originalObjectId, userId, createdAt);
            await job.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }, cancellationToken);

    public Task CompensateFailedUploadAsync(Guid userId, TenantId tenantId, Guid matterId,
        Guid documentId, Guid documentVersionId, Guid proposedOriginalObjectId,
        CancellationToken cancellationToken = default) =>
        InAuthorizedTenantTransactionAsync(userId, tenantId, async (connection, transaction) =>
        {
            await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                DELETE FROM casemesh.web_processing_jobs
                WHERE tenant_id=$1 AND matter_id=$2 AND document_version_id=$3;
                """, cancellationToken, tenantId.Value, matterId, documentVersionId);
            await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                DELETE FROM casemesh.web_document_metadata
                WHERE tenant_id=$1 AND matter_id=$2 AND document_id=$3;
                """, cancellationToken, tenantId.Value, matterId, documentId);
            await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                DELETE FROM casemesh.document_versions
                WHERE tenant_id=$1 AND matter_id=$2 AND document_id=$3 AND document_version_id=$4;
                """, cancellationToken, tenantId.Value, matterId, documentId, documentVersionId);
            await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                DELETE FROM casemesh.documents AS document
                WHERE tenant_id=$1 AND matter_id=$2 AND document_id=$3
                  AND NOT EXISTS (
                      SELECT 1 FROM casemesh.document_versions AS version
                      WHERE version.tenant_id=document.tenant_id
                        AND version.matter_id=document.matter_id
                        AND version.document_id=document.document_id);
                """, cancellationToken, tenantId.Value, matterId, documentId);
            await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                DELETE FROM casemesh.original_objects AS original
                WHERE tenant_id=$1 AND matter_id=$2 AND original_object_id=$3
                  AND NOT EXISTS (
                      SELECT 1 FROM casemesh.document_versions AS version
                      WHERE version.tenant_id=original.tenant_id
                        AND version.matter_id=original.matter_id
                        AND version.original_object_id=original.original_object_id);
                """, cancellationToken, tenantId.Value, matterId, proposedOriginalObjectId);
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

    public Task<bool> HasActiveJobsAsync(Guid userId, TenantId tenantId, Guid matterId,
        CancellationToken cancellationToken = default) =>
        InAuthorizedTenantTransactionAsync(userId, tenantId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1 FROM casemesh.web_processing_jobs
                    WHERE tenant_id=$1 AND matter_id=$2 AND status IN (1,2));
                """, connection, transaction);
            PostgresMatterStore.AddParameters(command, tenantId.Value, matterId);
            return await command.ExecuteScalarAsync(cancellationToken) is true;
        }, cancellationToken);

    public Task<WebProcessingJob?> ClaimAsync(Guid userId, TenantId tenantId, Guid workerId,
        DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken = default) =>
        InAuthorizedTenantTransactionAsync(userId, tenantId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                WITH candidate AS (
                    SELECT tenant_id,matter_id,job_id FROM casemesh.web_processing_jobs AS queued
                    WHERE tenant_id=$1 AND (status=1 OR (status=2 AND lease_expires_at <= $2))
                      AND available_at <= $2
                      AND pg_try_advisory_xact_lock(hashtextextended(
                          'casemesh-web-job:' || queued.tenant_id::text || ':' || queued.job_id::text, 0))
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

    public async Task<IReadOnlyList<WebJobScope>> ListPendingJobScopesAsync(DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenSafeAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT tenant_id,user_id FROM casemesh.pending_web_job_scopes($1) ORDER BY tenant_id,user_id;",
            connection);
        PostgresMatterStore.AddParameters(command, now);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<WebJobScope>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new WebJobScope(reader.GetGuid(1), new TenantId(reader.GetGuid(0))));
        return result;
    }

    public async Task<IAsyncDisposable> AcquireProcessingLockAsync(TenantId tenantId, Guid jobId,
        CancellationToken cancellationToken = default)
    {
        if (jobId == Guid.Empty) throw new ArgumentException("Job id is required.", nameof(jobId));
        return await AcquireSessionLockAsync(ProcessingLockName(tenantId, jobId), cancellationToken);
    }

    public async Task<IAsyncDisposable> AcquireMatterStateLockAsync(TenantId tenantId, Guid matterId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId.Value == Guid.Empty) throw new ArgumentException("Tenant id is required.", nameof(tenantId));
        if (matterId == Guid.Empty) throw new ArgumentException("Matter id is required.", nameof(matterId));
        return await AcquireSessionLockAsync(MatterStateLockName(tenantId, matterId), cancellationToken);
    }

    private async Task<IAsyncDisposable> AcquireSessionLockAsync(string lockName,
        CancellationToken cancellationToken)
    {
        var connection = await OpenSafeAsync(cancellationToken);
        try
        {
            await using var command = new NpgsqlCommand(
                "SELECT pg_advisory_lock(hashtextextended($1,0));", connection);
            PostgresMatterStore.AddParameters(command, lockName);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new SessionAdvisoryLock(_dataSource, connection, lockName);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public Task RenewLeaseAsync(Guid userId, WebProcessingJob job, Guid workerId,
        DateTimeOffset leaseExpiresAt, CancellationToken cancellationToken = default) =>
        InAuthorizedTenantTransactionAsync(userId, job.TenantId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                UPDATE casemesh.web_processing_jobs SET lease_expires_at=$6
                WHERE tenant_id=$1 AND matter_id=$2 AND job_id=$3
                  AND lease_owner=$4 AND attempts=$5 AND status=2;
                """, connection, transaction);
            PostgresMatterStore.AddParameters(command, job.TenantId.Value, job.MatterId, job.JobId,
                workerId, job.Attempts, leaseExpiresAt);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("The processing-job lease fencing token is no longer current.");
            return true;
        }, cancellationToken);

    public Task CompleteAsync(Guid userId, TenantId tenantId, Guid matterId, Guid jobId, Guid workerId, int attempts,
        DateTimeOffset completedAt, CancellationToken cancellationToken = default) =>
        FinishAsync(userId, tenantId, matterId, jobId, workerId, attempts, completedAt,
            true, null, maximumAttempts: 1, cancellationToken);

    public Task FailAsync(Guid userId, TenantId tenantId, Guid matterId, Guid jobId, Guid workerId, int attempts,
        DateTimeOffset failedAt, string failureCode, CancellationToken cancellationToken = default) =>
        FailAsync(userId, tenantId, matterId, jobId, workerId, attempts, failedAt, failureCode, 3, cancellationToken);

    public Task FailAsync(Guid userId, TenantId tenantId, Guid matterId, Guid jobId, Guid workerId, int attempts,
        DateTimeOffset failedAt, string failureCode, int maximumAttempts,
        CancellationToken cancellationToken = default)
    {
        if (maximumAttempts is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        return FinishAsync(userId, tenantId, matterId, jobId, workerId, attempts, failedAt,
            false, failureCode, maximumAttempts, cancellationToken);
    }

    private Task FinishAsync(Guid userId, TenantId tenantId, Guid matterId, Guid jobId, Guid workerId,
        int attempts, DateTimeOffset at, bool completed, string? failureCode, int maximumAttempts,
        CancellationToken cancellationToken) =>
        InAuthorizedTenantTransactionAsync(userId, tenantId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                UPDATE casemesh.web_processing_jobs
                SET status=CASE WHEN $7=4 AND attempts < $9 THEN 1 ELSE $7 END,
                    lease_owner=NULL,lease_expires_at=NULL,failure_code=$8,
                    completed_at=CASE WHEN $7=3 THEN $6 ELSE NULL END,
                    available_at=CASE WHEN $7=4 THEN $6 + interval '5 minutes' ELSE available_at END
                WHERE tenant_id=$1 AND matter_id=$2 AND job_id=$3
                  AND lease_owner=$4 AND attempts=$5;
                """, connection, transaction);
            PostgresMatterStore.AddParameters(command, tenantId.Value, matterId, jobId, workerId, attempts, at,
                (short)(completed ? WebProcessingStatus.Completed : WebProcessingStatus.Failed), failureCode,
                maximumAttempts);
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

    private static string ProcessingLockName(TenantId tenantId, Guid jobId) =>
        $"casemesh-web-job:{tenantId.Value:D}:{jobId:D}";

    private static string MatterStateLockName(TenantId tenantId, Guid matterId) =>
        $"casemesh-matter-state:{tenantId.Value:D}:{matterId:D}";

    private sealed class SessionAdvisoryLock(
        NpgsqlDataSource dataSource,
        NpgsqlConnection connection,
        string lockName) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Exception? unlockFailure = null;
            try
            {
                await using var command = new NpgsqlCommand(
                    "SELECT pg_advisory_unlock(hashtextextended($1,0));", connection);
                PostgresMatterStore.AddParameters(command, lockName);
                if (await command.ExecuteScalarAsync() is not true)
                    throw new InvalidOperationException("The PostgreSQL session advisory lock was not held.");
            }
            catch (Exception exception)
            {
                unlockFailure = exception;
                try { dataSource.Clear(); } catch { }
            }
            finally
            {
                try { await connection.DisposeAsync(); }
                catch when (unlockFailure is not null) { }
            }

            if (unlockFailure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(unlockFailure).Throw();
        }
    }
}
