using CaseMesh.Core.Models;
using Npgsql;

namespace CaseMesh.Persistence.Postgres;

public enum PilotQuotaResource { ActiveMatter = 1, OriginalBytes = 2, EvidenceItem = 3 }
public enum PilotDailyUsageKind { QaRequest = 1, ExportGeneration = 2, IngestionAttempt = 3, ExportDownload = 4 }
public enum PilotUsageEventKind
{
    MatterCreated = 1, UploadAccepted, Ingestion, Qa, ExportGenerated,
    ExportDownloaded, QuotaRejected, Deletion, Reconciliation, ApiRequest
}

public sealed record PilotEntitlements(
    TenantId TenantId,
    string TierCode,
    int ActiveMatterLimit,
    long MatterOriginalBytesLimit,
    long TenantOriginalBytesLimit,
    int MatterEvidenceItemLimit,
    int TenantEvidenceItemLimit,
    int IngestionAttemptLimit,
    int QaDailyRequestLimit,
    int QaContextByteLimit,
    int ExportDailyLimit,
    int ConversationHistoryLimit,
    int FailedJobRetentionDays,
    int QaMetadataRetentionDays,
    int ExportArtifactRetentionHours,
    int OperationalLogRetentionDays,
    DateTimeOffset ConfiguredAt,
    string ConfiguredBy);

public sealed record PilotQuotaReservation(
    TenantId TenantId,
    Guid ReservationId,
    Guid MatterId,
    PilotQuotaResource Resource,
    long Amount,
    DateTimeOffset ExpiresAt);

public sealed record PilotEvidenceReservation(
    IReadOnlyList<PilotQuotaReservation> Reservations,
    long AdditionalOriginalBytes,
    int AdditionalEvidenceItems);

public sealed record PilotUsageSnapshot(
    PilotEntitlements Entitlements,
    int ActiveMatters,
    long MatterOriginalBytes,
    long TenantOriginalBytes,
    int MatterEvidenceItems,
    int TenantEvidenceItems,
    long QaRequestsToday,
    long ExportsToday);

public enum PrivacyDeletionStatus { Pending = 1, Processing = 2, Completed = 3, Retry = 4 }
public sealed record PrivacyDeletionJob(TenantId TenantId, Guid DeletionId, Guid MatterId,
    Guid RequestedByUserId, PrivacyDeletionStatus Status, int Attempts, string? FailureCategory,
    DateTimeOffset RequestedAt, DateTimeOffset? CompletedAt);
public sealed record PrivacyDeletionScope(Guid UserId, TenantId TenantId);
public sealed record PilotQueueSnapshot(long PendingJobs, double OldestJobAgeSeconds,
    long PendingDeletions, double OldestDeletionAgeSeconds);

public sealed class PilotQuotaExceededException(string code) : Exception("The configured pilot resource limit was reached.")
{
    public string Code { get; } = code;
}

public sealed class PostgresPilotOperationsRepository : IAsyncDisposable
{
    private static readonly TimeSpan ReservationLifetime = TimeSpan.FromMinutes(15);
    private readonly PostgresMatterStore _store;
    private readonly TimeProvider _timeProvider;

    public PostgresPilotOperationsRepository(string connectionString, TimeProvider timeProvider)
    {
        _store = new PostgresMatterStore(connectionString);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public Task<PilotEntitlements> GetEntitlementsAsync(
        TenantId tenantId,
        CancellationToken cancellationToken = default) =>
        _store.InTenantTransactionAsync(tenantId, (connection, transaction) =>
            ReadEntitlementsAsync(connection, transaction, tenantId, cancellationToken), cancellationToken);

    public Task<PilotQuotaReservation> ReserveActiveMatterAsync(
        TenantId tenantId,
        Guid matterId,
        CancellationToken cancellationToken = default)
    {
        RequireMatter(matterId);
        var now = _timeProvider.GetUtcNow();
        return _store.InTenantTransactionAsync(tenantId, async (connection, transaction) =>
        {
            await LockQuotaAsync(connection, transaction, tenantId, cancellationToken);
            var entitlement = await ReadEntitlementsAsync(connection, transaction, tenantId, cancellationToken);
            var used = await ScalarInt64Async(connection, transaction, """
                SELECT (SELECT count(*) FROM casemesh.matters WHERE tenant_id=$1 AND status='active') +
                       (SELECT COALESCE(sum(amount),0) FROM casemesh.pilot_quota_reservations
                        WHERE tenant_id=$1 AND resource_kind=1 AND expires_at>$2);
                """, cancellationToken, tenantId.Value, now);
            if (used >= entitlement.ActiveMatterLimit)
                throw new PilotQuotaExceededException("active-matter-limit");
            return await InsertReservationAsync(connection, transaction, tenantId, matterId,
                PilotQuotaResource.ActiveMatter, 1, now, cancellationToken);
        }, cancellationToken);
    }

    public Task<PilotEvidenceReservation> ReserveEvidenceAsync(
        TenantId tenantId,
        Guid matterId,
        string contentSha256,
        long byteLength,
        CancellationToken cancellationToken = default)
    {
        RequireMatter(matterId);
        if (byteLength <= 0) throw new ArgumentOutOfRangeException(nameof(byteLength));
        if (contentSha256.Length != 64 || contentSha256.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("A SHA-256 identity is required.", nameof(contentSha256));
        var now = _timeProvider.GetUtcNow();
        return _store.InTenantTransactionAsync(tenantId, async (connection, transaction) =>
        {
            await LockQuotaAsync(connection, transaction, tenantId, cancellationToken);
            var entitlement = await ReadEntitlementsAsync(connection, transaction, tenantId, cancellationToken);
            var duplicate = await ScalarBoolAsync(connection, transaction, """
                SELECT EXISTS (SELECT 1 FROM casemesh.original_objects
                  WHERE tenant_id=$1 AND matter_id=$2 AND content_sha256=$3);
                """, cancellationToken, tenantId.Value, matterId, contentSha256.ToUpperInvariant());
            var additionalBytes = duplicate ? 0 : byteLength;

            var matterBytes = await ScalarInt64Async(connection, transaction, """
                SELECT COALESCE((SELECT sum(byte_length) FROM casemesh.original_object_storage
                                 WHERE tenant_id=$1 AND matter_id=$2),0) +
                       COALESCE((SELECT sum(amount) FROM casemesh.pilot_quota_reservations
                                 WHERE tenant_id=$1 AND matter_id=$2 AND resource_kind=2 AND expires_at>$3),0);
                """, cancellationToken, tenantId.Value, matterId, now);
            var tenantBytes = await ScalarInt64Async(connection, transaction, """
                SELECT COALESCE((SELECT sum(byte_length) FROM casemesh.original_object_storage
                                 WHERE tenant_id=$1),0) +
                       COALESCE((SELECT sum(amount) FROM casemesh.pilot_quota_reservations
                                 WHERE tenant_id=$1 AND resource_kind=2 AND expires_at>$2),0);
                """, cancellationToken, tenantId.Value, now);
            var matterItems = await ScalarInt64Async(connection, transaction, """
                SELECT (SELECT count(*) FROM casemesh.document_versions WHERE tenant_id=$1 AND matter_id=$2) +
                       (SELECT COALESCE(sum(amount),0) FROM casemesh.pilot_quota_reservations
                        WHERE tenant_id=$1 AND matter_id=$2 AND resource_kind=3 AND expires_at>$3);
                """, cancellationToken, tenantId.Value, matterId, now);
            var tenantItems = await ScalarInt64Async(connection, transaction, """
                SELECT (SELECT count(*) FROM casemesh.document_versions WHERE tenant_id=$1) +
                       (SELECT COALESCE(sum(amount),0) FROM casemesh.pilot_quota_reservations
                        WHERE tenant_id=$1 AND resource_kind=3 AND expires_at>$2);
                """, cancellationToken, tenantId.Value, now);

            if (checked(matterBytes + additionalBytes) > entitlement.MatterOriginalBytesLimit)
                throw new PilotQuotaExceededException("matter-original-bytes-limit");
            if (checked(tenantBytes + additionalBytes) > entitlement.TenantOriginalBytesLimit)
                throw new PilotQuotaExceededException("tenant-original-bytes-limit");
            if (matterItems + 1 > entitlement.MatterEvidenceItemLimit)
                throw new PilotQuotaExceededException("matter-evidence-item-limit");
            if (tenantItems + 1 > entitlement.TenantEvidenceItemLimit)
                throw new PilotQuotaExceededException("tenant-evidence-item-limit");

            var reservations = new List<PilotQuotaReservation>();
            if (additionalBytes > 0)
                reservations.Add(await InsertReservationAsync(connection, transaction, tenantId, matterId,
                    PilotQuotaResource.OriginalBytes, additionalBytes, now, cancellationToken));
            reservations.Add(await InsertReservationAsync(connection, transaction, tenantId, matterId,
                PilotQuotaResource.EvidenceItem, 1, now, cancellationToken));
            return new PilotEvidenceReservation(reservations, additionalBytes, 1);
        }, cancellationToken);
    }

    public Task ReleaseReservationsAsync(
        TenantId tenantId,
        IEnumerable<Guid> reservationIds,
        CancellationToken cancellationToken = default)
    {
        var ids = reservationIds.Distinct().ToArray();
        if (ids.Length == 0) return Task.CompletedTask;
        if (ids.Any(id => id == Guid.Empty)) throw new ArgumentException("Reservation ids must be non-empty.");
        return _store.InTenantTransactionAsync(tenantId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                DELETE FROM casemesh.pilot_quota_reservations
                WHERE tenant_id=$1 AND reservation_id=ANY($2);
                """, connection, transaction);
            command.Parameters.AddWithValue(tenantId.Value);
            command.Parameters.AddWithValue(ids);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }, cancellationToken);
    }

    public Task<long> ConsumeDailyAsync(
        TenantId tenantId,
        PilotDailyUsageKind kind,
        long amount = 1,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        var now = _timeProvider.GetUtcNow();
        return _store.InTenantTransactionAsync(tenantId, async (connection, transaction) =>
        {
            var entitlement = await ReadEntitlementsAsync(connection, transaction, tenantId, cancellationToken);
            var limit = kind switch
            {
                PilotDailyUsageKind.QaRequest => entitlement.QaDailyRequestLimit,
                PilotDailyUsageKind.ExportGeneration => entitlement.ExportDailyLimit,
                PilotDailyUsageKind.IngestionAttempt => long.MaxValue,
                PilotDailyUsageKind.ExportDownload => long.MaxValue,
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
            if (amount > limit) throw DailyLimit(kind);
            await using var command = new NpgsqlCommand("""
                INSERT INTO casemesh.pilot_usage_daily (tenant_id,usage_date,usage_kind,quantity,updated_at)
                VALUES ($1,$2,$3,$4,$5)
                ON CONFLICT (tenant_id,usage_date,usage_kind) DO UPDATE
                SET quantity=casemesh.pilot_usage_daily.quantity+EXCLUDED.quantity,
                    updated_at=EXCLUDED.updated_at
                WHERE casemesh.pilot_usage_daily.quantity+EXCLUDED.quantity <= $6
                RETURNING quantity;
                """, connection, transaction);
            PostgresMatterStore.AddParameters(command, tenantId.Value, DateOnly.FromDateTime(now.UtcDateTime),
                (short)kind, amount, now, limit);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (result is not long consumed) throw DailyLimit(kind);
            return consumed;
        }, cancellationToken);
    }

    public async Task EnsureIngestionAttemptAllowedAsync(
        TenantId tenantId,
        int attempt,
        CancellationToken cancellationToken = default)
    {
        if (attempt < 1) throw new ArgumentOutOfRangeException(nameof(attempt));
        var entitlement = await GetEntitlementsAsync(tenantId, cancellationToken);
        if (attempt > entitlement.IngestionAttemptLimit)
            throw new PilotQuotaExceededException("ingestion-attempt-limit");
        await ConsumeDailyAsync(tenantId, PilotDailyUsageKind.IngestionAttempt, 1, cancellationToken);
    }

    public Task RecordUsageEventAsync(
        TenantId tenantId,
        Guid? matterId,
        PilotUsageEventKind kind,
        string outcomeCode,
        long quantity = 0,
        long? durationMilliseconds = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (quantity < 0 || durationMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        if (string.IsNullOrWhiteSpace(outcomeCode) || outcomeCode.Length > 80 ||
            outcomeCode.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-')))
            throw new ArgumentException("A bounded typed outcome code is required.", nameof(outcomeCode));
        var now = _timeProvider.GetUtcNow();
        return _store.InTenantTransactionAsync(tenantId, async (connection, transaction) =>
        {
            await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                INSERT INTO casemesh.pilot_usage_events
                    (tenant_id,usage_event_id,matter_id,usage_kind,outcome_code,quantity,duration_ms,occurred_at)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8);
                """, cancellationToken, tenantId.Value, Guid.NewGuid(), matterId, (short)kind,
                outcomeCode.ToLowerInvariant(), quantity, durationMilliseconds, now);
            return true;
        }, cancellationToken);
    }

    public Task<PilotUsageSnapshot> GetUsageAsync(
        TenantId tenantId,
        Guid matterId,
        CancellationToken cancellationToken = default)
    {
        RequireMatter(matterId);
        var today = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
        return _store.InTenantTransactionAsync(tenantId, async (connection, transaction) =>
        {
            var entitlement = await ReadEntitlementsAsync(connection, transaction, tenantId, cancellationToken);
            var active = await ScalarInt64Async(connection, transaction,
                "SELECT count(*) FROM casemesh.matters WHERE tenant_id=$1 AND status='active';",
                cancellationToken, tenantId.Value);
            var matterBytes = await ScalarInt64Async(connection, transaction,
                "SELECT COALESCE(sum(byte_length),0) FROM casemesh.original_object_storage WHERE tenant_id=$1 AND matter_id=$2;",
                cancellationToken, tenantId.Value, matterId);
            var tenantBytes = await ScalarInt64Async(connection, transaction,
                "SELECT COALESCE(sum(byte_length),0) FROM casemesh.original_object_storage WHERE tenant_id=$1;",
                cancellationToken, tenantId.Value);
            var matterItems = await ScalarInt64Async(connection, transaction,
                "SELECT count(*) FROM casemesh.document_versions WHERE tenant_id=$1 AND matter_id=$2;",
                cancellationToken, tenantId.Value, matterId);
            var tenantItems = await ScalarInt64Async(connection, transaction,
                "SELECT count(*) FROM casemesh.document_versions WHERE tenant_id=$1;",
                cancellationToken, tenantId.Value);
            var qa = await DailyUsageAsync(connection, transaction, tenantId, today,
                PilotDailyUsageKind.QaRequest, cancellationToken);
            var exports = await DailyUsageAsync(connection, transaction, tenantId, today,
                PilotDailyUsageKind.ExportGeneration, cancellationToken);
            return new PilotUsageSnapshot(entitlement, checked((int)active), matterBytes, tenantBytes,
                checked((int)matterItems), checked((int)tenantItems), qa, exports);
        }, cancellationToken);
    }

    public Task<bool> CheckReadinessAsync(CancellationToken cancellationToken = default) =>
        _store.InTenantTransactionAsync(new TenantId(Guid.Parse("00000000-0000-0000-0000-000000000001")),
            async (connection, transaction) =>
            {
                await using var command = new NpgsqlCommand("""
                    SELECT NOT (role.rolsuper OR role.rolbypassrls) AND
                           COALESCE((SELECT max(version) FROM casemesh.schema_migrations),0) >= 8
                    FROM pg_roles role WHERE role.rolname=current_user;
                    """, connection, transaction);
                return await command.ExecuteScalarAsync(cancellationToken) is true;
            }, cancellationToken);

    public Task<int> PruneOperationalMetadataAsync(
        TenantId tenantId,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        return _store.InTenantTransactionAsync(tenantId, async (connection, transaction) =>
        {
            var entitlement = await ReadEntitlementsAsync(connection, transaction, tenantId, cancellationToken);
            var count = await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                DELETE FROM casemesh.pilot_usage_events
                WHERE tenant_id=$1 AND occurred_at < $2;
                """, cancellationToken, tenantId.Value, now.AddDays(-entitlement.OperationalLogRetentionDays));
            count += await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                DELETE FROM casemesh.pilot_quota_reservations
                WHERE tenant_id=$1 AND expires_at <= $2;
                """, cancellationToken, tenantId.Value, now);
            count += await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                DELETE FROM casemesh.pilot_usage_daily
                WHERE tenant_id=$1 AND usage_date < $2;
                """, cancellationToken, tenantId.Value,
                DateOnly.FromDateTime(now.AddDays(-entitlement.QaMetadataRetentionDays).UtcDateTime));
            count += await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                DELETE FROM casemesh.web_processing_jobs
                WHERE tenant_id=$1 AND status=4 AND created_at < $2;
                """, cancellationToken, tenantId.Value, now.AddDays(-entitlement.FailedJobRetentionDays));
            count += await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                DELETE FROM casemesh.privacy_deletion_jobs
                WHERE tenant_id=$1 AND status=3 AND completed_at < $2;
                """, cancellationToken, tenantId.Value,
                now.AddDays(-entitlement.OperationalLogRetentionDays));
            return count;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<TenantId>> ListMaintenanceTenantsAsync(
        CancellationToken cancellationToken = default) =>
        _store.InTenantTransactionAsync(new TenantId(Guid.Parse("00000000-0000-0000-0000-000000000001")),
            async (connection, transaction) =>
            {
                await using var command = new NpgsqlCommand(
                    "SELECT tenant_id FROM casemesh.pilot_maintenance_tenants() ORDER BY tenant_id;",
                    connection, transaction);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                var tenants = new List<TenantId>();
                while (await reader.ReadAsync(cancellationToken)) tenants.Add(new TenantId(reader.GetGuid(0)));
                return (IReadOnlyList<TenantId>)tenants;
            }, cancellationToken);

    public Task<PilotQueueSnapshot> GetQueueSnapshotAsync(TenantId tenantId,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        return _store.InTenantTransactionAsync(tenantId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                SELECT
                  (SELECT count(*) FROM casemesh.web_processing_jobs WHERE tenant_id=$1 AND status IN (1,2)),
                  COALESCE((SELECT extract(epoch FROM ($2-min(created_at)))
                            FROM casemesh.web_processing_jobs WHERE tenant_id=$1 AND status IN (1,2)),0),
                  (SELECT count(*) FROM casemesh.privacy_deletion_jobs WHERE tenant_id=$1 AND status IN (1,2,4)),
                  COALESCE((SELECT extract(epoch FROM ($2-min(requested_at)))
                            FROM casemesh.privacy_deletion_jobs WHERE tenant_id=$1 AND status IN (1,2,4)),0);
                """, connection, transaction);
            PostgresMatterStore.AddParameters(command, tenantId.Value, now);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            return new PilotQueueSnapshot(reader.GetInt64(0), Convert.ToDouble(reader.GetDecimal(1)),
                reader.GetInt64(2), Convert.ToDouble(reader.GetDecimal(3)));
        }, cancellationToken);
    }

    public Task<PrivacyDeletionJob> EnqueueDeletionAsync(
        Guid userId, TenantId tenantId, Guid matterId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty) throw new ArgumentException("User id is required.", nameof(userId));
        RequireMatter(matterId);
        var now = _timeProvider.GetUtcNow();
        return _store.InTenantTransactionAsync(tenantId, async (connection, transaction) =>
        {
            await using (var userContext = new NpgsqlCommand(
                "SELECT set_config('casemesh.user_id',$1,true);", connection, transaction))
            {
                PostgresMatterStore.AddParameters(userContext, userId.ToString());
                await userContext.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var membership = new NpgsqlCommand("""
                SELECT EXISTS (SELECT 1 FROM casemesh.tenant_memberships
                    WHERE tenant_id=$1 AND user_id=$2);
                """, connection, transaction))
            {
                PostgresMatterStore.AddParameters(membership, tenantId.Value, userId);
                if (await membership.ExecuteScalarAsync(cancellationToken) is not true)
                    throw new UnauthorizedAccessException("No workspace membership was found.");
            }
            await using (var existing = new NpgsqlCommand("""
                SELECT tenant_id,deletion_id,matter_id,requested_by_user_id,status,attempts,
                       failure_category,requested_at,completed_at
                FROM casemesh.privacy_deletion_jobs
                WHERE tenant_id=$1 AND matter_id=$2 AND status IN (1,2,4)
                FOR UPDATE;
                """, connection, transaction))
            {
                PostgresMatterStore.AddParameters(existing, tenantId.Value, matterId);
                await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken)) return ReadDeletion(reader);
            }
            if (!await ScalarBoolAsync(connection, transaction,
                    "SELECT EXISTS (SELECT 1 FROM casemesh.matters WHERE tenant_id=$1 AND matter_id=$2);",
                    cancellationToken, tenantId.Value, matterId))
                throw new UnauthorizedAccessException("The Matter was not found.");
            var deletionId = Guid.NewGuid();
            await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                INSERT INTO casemesh.privacy_deletion_jobs
                    (tenant_id,deletion_id,matter_id,requested_by_user_id,status,attempts,
                     available_at,requested_at)
                VALUES ($1,$2,$3,$4,1,0,$5,$5);
                """, cancellationToken, tenantId.Value, deletionId, matterId, userId, now);
            return new PrivacyDeletionJob(tenantId, deletionId, matterId, userId,
                PrivacyDeletionStatus.Pending, 0, null, now, null);
        }, cancellationToken);
    }

    public Task<PrivacyDeletionJob?> GetDeletionAsync(TenantId tenantId, Guid matterId, Guid deletionId,
        CancellationToken cancellationToken = default) =>
        _store.InTenantTransactionAsync(tenantId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                SELECT tenant_id,deletion_id,matter_id,requested_by_user_id,status,attempts,
                       failure_category,requested_at,completed_at
                FROM casemesh.privacy_deletion_jobs
                WHERE tenant_id=$1 AND matter_id=$2 AND deletion_id=$3;
                """, connection, transaction);
            PostgresMatterStore.AddParameters(command, tenantId.Value, matterId, deletionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadDeletion(reader) : null;
        }, cancellationToken);

    public async Task<IReadOnlyList<PrivacyDeletionScope>> ListPendingDeletionScopesAsync(
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        return await _store.InTenantTransactionAsync(
            new TenantId(Guid.Parse("00000000-0000-0000-0000-000000000001")),
            async (connection, transaction) =>
            {
                await using var command = new NpgsqlCommand(
                    "SELECT tenant_id,user_id FROM casemesh.pending_privacy_deletion_scopes($1) ORDER BY tenant_id,user_id;",
                    connection, transaction);
                PostgresMatterStore.AddParameters(command, now);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                var scopes = new List<PrivacyDeletionScope>();
                while (await reader.ReadAsync(cancellationToken))
                    scopes.Add(new PrivacyDeletionScope(reader.GetGuid(1), new TenantId(reader.GetGuid(0))));
                return (IReadOnlyList<PrivacyDeletionScope>)scopes;
            }, cancellationToken);
    }

    public Task<PrivacyDeletionJob?> ClaimDeletionAsync(Guid userId, TenantId tenantId, Guid workerId,
        TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        return _store.InTenantTransactionAsync(tenantId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                WITH candidate AS (
                    SELECT deletion_id FROM casemesh.privacy_deletion_jobs AS queued
                    WHERE tenant_id=$1 AND requested_by_user_id=$2 AND available_at <= $3
                      AND (status IN (1,4) OR (status=2 AND lease_expires_at <= $3))
                    ORDER BY requested_at,deletion_id FOR UPDATE SKIP LOCKED LIMIT 1
                )
                UPDATE casemesh.privacy_deletion_jobs job
                SET status=2,attempts=attempts+1,lease_owner=$4,lease_expires_at=$5,
                    failure_category=NULL,completed_at=NULL
                FROM candidate WHERE job.tenant_id=$1 AND job.deletion_id=candidate.deletion_id
                RETURNING job.tenant_id,job.deletion_id,job.matter_id,job.requested_by_user_id,
                          job.status,job.attempts,job.failure_category,job.requested_at,job.completed_at;
                """, connection, transaction);
            PostgresMatterStore.AddParameters(command, tenantId.Value, userId, now, workerId, now + leaseDuration);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadDeletion(reader) : null;
        }, cancellationToken);
    }

    public Task CompleteDeletionAsync(PrivacyDeletionJob job, Guid workerId,
        CancellationToken cancellationToken = default) => FinishDeletionAsync(job, workerId, true, null, cancellationToken);

    public Task RetryDeletionAsync(PrivacyDeletionJob job, Guid workerId, string failureCategory,
        CancellationToken cancellationToken = default) => FinishDeletionAsync(job, workerId, false,
            failureCategory, cancellationToken);

    private Task FinishDeletionAsync(PrivacyDeletionJob job, Guid workerId, bool completed,
        string? failureCategory, CancellationToken cancellationToken)
    {
        if (!completed && (string.IsNullOrWhiteSpace(failureCategory) || failureCategory.Length > 80 ||
            failureCategory.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-'))))
            throw new ArgumentException("A bounded failure category is required.", nameof(failureCategory));
        var now = _timeProvider.GetUtcNow();
        return _store.InTenantTransactionAsync(job.TenantId, async (connection, transaction) =>
        {
            var count = await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                UPDATE casemesh.privacy_deletion_jobs
                SET status=$7,lease_owner=NULL,lease_expires_at=NULL,failure_category=$8,
                    completed_at=CASE WHEN $7=3 THEN $6 ELSE NULL END,
                    available_at=CASE WHEN $7=4 THEN $6 + interval '5 minutes' ELSE available_at END
                WHERE tenant_id=$1 AND deletion_id=$2 AND matter_id=$3
                  AND lease_owner=$4 AND attempts=$5 AND status=2;
                """, cancellationToken, job.TenantId.Value, job.DeletionId, job.MatterId,
                workerId, job.Attempts, now, (short)(completed ? PrivacyDeletionStatus.Completed : PrivacyDeletionStatus.Retry),
                failureCategory?.ToLowerInvariant());
            if (count != 1) throw new InvalidOperationException("The deletion lease is no longer current.");
            return true;
        }, cancellationToken);
    }

    public async ValueTask DisposeAsync() => await _store.DisposeAsync();

    private static async Task<PilotEntitlements> ReadEntitlementsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TenantId tenantId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT tier_code,active_matter_limit,matter_original_bytes_limit,tenant_original_bytes_limit,
                   matter_evidence_item_limit,tenant_evidence_item_limit,ingestion_attempt_limit,
                   qa_daily_request_limit,qa_context_byte_limit,export_daily_limit,conversation_history_limit,
                   failed_job_retention_days,qa_metadata_retention_days,export_artifact_retention_hours,
                   operational_log_retention_days,configured_at,configured_by
            FROM casemesh.pilot_entitlements WHERE tenant_id=$1;
            """, connection, transaction);
        command.Parameters.AddWithValue(tenantId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("The tenant has no configured pilot entitlement.");
        return new PilotEntitlements(tenantId, reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2),
            reader.GetInt64(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7),
            reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11), reader.GetInt32(12),
            reader.GetInt32(13), reader.GetInt32(14), reader.GetFieldValue<DateTimeOffset>(15), reader.GetString(16));
    }

    private static async Task LockQuotaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TenantId tenantId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended($1,0));", connection, transaction);
        command.Parameters.AddWithValue($"casemesh-pilot-quota:{tenantId.Value:D}");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<PilotQuotaReservation> InsertReservationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TenantId tenantId,
        Guid matterId,
        PilotQuotaResource resource,
        long amount,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var reservation = new PilotQuotaReservation(tenantId, Guid.NewGuid(), matterId, resource, amount,
            now.Add(ReservationLifetime));
        await PostgresMatterStore.ExecuteAsync(connection, transaction, """
            INSERT INTO casemesh.pilot_quota_reservations
                (tenant_id,reservation_id,matter_id,resource_kind,amount,created_at,expires_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7);
            """, cancellationToken, tenantId.Value, reservation.ReservationId, matterId, (short)resource,
            amount, now, reservation.ExpiresAt);
        return reservation;
    }

    private static async Task<long> DailyUsageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TenantId tenantId,
        DateOnly date,
        PilotDailyUsageKind kind,
        CancellationToken cancellationToken) =>
        await ScalarInt64Async(connection, transaction, """
            SELECT COALESCE((SELECT quantity FROM casemesh.pilot_usage_daily
                             WHERE tenant_id=$1 AND usage_date=$2 AND usage_kind=$3),0);
            """, cancellationToken, tenantId.Value, date, (short)kind);

    private static PilotQuotaExceededException DailyLimit(PilotDailyUsageKind kind) =>
        new(kind == PilotDailyUsageKind.QaRequest ? "qa-daily-request-limit" : "export-daily-limit");

    private static async Task<long> ScalarInt64Async(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params object?[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        PostgresMatterStore.AddParameters(command, values);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<bool> ScalarBoolAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params object?[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        PostgresMatterStore.AddParameters(command, values);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static void RequireMatter(Guid matterId)
    {
        if (matterId == Guid.Empty) throw new ArgumentException("Matter id is required.", nameof(matterId));
    }

    private static PrivacyDeletionJob ReadDeletion(NpgsqlDataReader reader) => new(
        new TenantId(reader.GetGuid(0)), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3),
        (PrivacyDeletionStatus)reader.GetInt16(4), reader.GetInt32(5),
        reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetFieldValue<DateTimeOffset>(7),
        reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8));
}
