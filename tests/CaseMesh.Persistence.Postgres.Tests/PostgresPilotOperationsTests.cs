using System.Diagnostics;
using System.Security.Cryptography;
using CaseMesh.Api;
using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Core.Workplace;
using CaseMesh.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit.Abstractions;

namespace CaseMesh.Persistence.Postgres.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresPilotOperationsTests(PostgresFixture database, ITestOutputHelper output)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 8, 0, 0, TimeSpan.Zero);

    [PostgresFact]
    public async Task Concurrent_active_Matter_reservations_cannot_oversubscribe_the_tenant_limit()
    {
        var tenant = new TenantId(Guid.NewGuid());
        await SeedTenantAndMatterAsync(tenant, Guid.NewGuid());
        await SetLimitsAsync(tenant, "active_matter_limit=2");
        await using var first = Operations();
        await using var second = Operations();

        var attempts = new[]
        {
            CaptureAsync(() => first.ReserveActiveMatterAsync(tenant, Guid.NewGuid())),
            CaptureAsync(() => second.ReserveActiveMatterAsync(tenant, Guid.NewGuid()))
        };
        var outcomes = await Task.WhenAll(attempts);

        Assert.Single(outcomes, item => item is PilotQuotaReservation);
        var rejected = Assert.Single(outcomes, item => item is PilotQuotaExceededException);
        Assert.Equal("active-matter-limit", ((PilotQuotaExceededException)rejected).Code);
    }

    [PostgresFact]
    public async Task Daily_QA_usage_is_atomic_and_never_exceeds_entitlement()
    {
        var tenant = new TenantId(Guid.NewGuid());
        await SeedTenantAndMatterAsync(tenant, Guid.NewGuid());
        await SetLimitsAsync(tenant, "qa_daily_request_limit=1");
        await using var first = Operations();
        await using var second = Operations();

        var outcomes = await Task.WhenAll(
            CaptureAsync(async () => await first.ConsumeDailyAsync(tenant, PilotDailyUsageKind.QaRequest)),
            CaptureAsync(async () => await second.ConsumeDailyAsync(tenant, PilotDailyUsageKind.QaRequest)));

        Assert.Single(outcomes, item => item is long);
        Assert.Equal("qa-daily-request-limit",
            Assert.IsType<PilotQuotaExceededException>(Assert.Single(outcomes, item => item is Exception)).Code);
    }

    [PostgresFact]
    public async Task Concurrent_evidence_reservations_enforce_bytes_and_items_before_canonical_rows_exist()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var matterId = Guid.NewGuid();
        await SeedTenantAndMatterAsync(tenant, matterId);
        await SetLimitsAsync(tenant,
            "matter_original_bytes_limit=10,tenant_original_bytes_limit=10,matter_evidence_item_limit=1,tenant_evidence_item_limit=1");
        await using var first = Operations();
        await using var second = Operations();
        var outcomes = await Task.WhenAll(
            CaptureAsync(() => first.ReserveEvidenceAsync(tenant, matterId, new string('A', 64), 8)),
            CaptureAsync(() => second.ReserveEvidenceAsync(tenant, matterId, new string('B', 64), 8)));

        var accepted = Assert.IsType<PilotEvidenceReservation>(
            Assert.Single(outcomes, item => item is PilotEvidenceReservation));
        Assert.IsType<PilotQuotaExceededException>(Assert.Single(outcomes, item => item is Exception));
        await using (var store = new PostgresMatterStore(database.AppConnectionString))
            Assert.Empty(Assert.IsType<PersistedMatter>(await store.LoadAsync(tenant, matterId)).Evidence.DocumentVersions);
        await first.ReleaseReservationsAsync(tenant,
            accepted.Reservations.Select(item => item.ReservationId));
    }

    [PostgresFact]
    public async Task Tenant_QA_usage_is_independent_and_cannot_read_or_consume_another_tenants_budget()
    {
        var tenantA = new TenantId(Guid.NewGuid());
        var tenantB = new TenantId(Guid.NewGuid());
        var matterA = Guid.NewGuid();
        var matterB = Guid.NewGuid();
        await SeedTenantAndMatterAsync(tenantA, matterA);
        await SeedTenantAndMatterAsync(tenantB, matterB);
        await SetLimitsAsync(tenantA, "qa_daily_request_limit=1");
        await SetLimitsAsync(tenantB, "qa_daily_request_limit=1");
        await using var operations = Operations();

        Assert.Equal(1, await operations.ConsumeDailyAsync(tenantA, PilotDailyUsageKind.QaRequest));
        Assert.Equal(0, (await operations.GetUsageAsync(tenantB, matterB)).QaRequestsToday);
        Assert.Equal(1, await operations.ConsumeDailyAsync(tenantB, PilotDailyUsageKind.QaRequest));
        await Assert.ThrowsAsync<PilotQuotaExceededException>(() =>
            operations.ConsumeDailyAsync(tenantA, PilotDailyUsageKind.QaRequest));
    }

    [Fact]
    public void Pilot_admin_scope_rejects_a_different_or_missing_tenant_before_database_access()
    {
        var tenant = new TenantId(Guid.NewGuid());
        PilotAdminTenantScope.Require(tenant, tenant.Value.ToString());
        Assert.Throws<UnauthorizedAccessException>(() => PilotAdminTenantScope.Require(tenant, null));
        Assert.Throws<UnauthorizedAccessException>(() =>
            PilotAdminTenantScope.Require(tenant, Guid.NewGuid().ToString()));
    }

    [PostgresFact]
    public async Task Usage_events_store_only_bounded_typed_operational_fields()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var matterId = Guid.NewGuid();
        await SeedTenantAndMatterAsync(tenant, matterId);
        await using var operations = Operations();

        await operations.RecordUsageEventAsync(tenant, matterId, PilotUsageEventKind.ApiRequest,
            "accepted", 12, 34);
        await using var connection = new NpgsqlConnection(database.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT column_name FROM information_schema.columns
            WHERE table_schema='casemesh' AND table_name='pilot_usage_events'
            ORDER BY ordinal_position;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(0));

        Assert.Equal(new[] { "tenant_id", "usage_event_id", "matter_id", "usage_kind", "outcome_code",
            "quantity", "duration_ms", "occurred_at" }, columns);
        Assert.DoesNotContain(columns, name => name.Contains("text") || name.Contains("prompt") ||
            name.Contains("file") || name.Contains("hash") || name.Contains("name"));
    }

    [PostgresFact]
    public async Task Deletion_queue_is_idempotent_and_stale_leases_are_recoverable_with_fencing()
    {
        var user = await CreateUserAsync();
        var tenant = new TenantId(Guid.NewGuid());
        await using (var web = new PostgresWebWorkspaceRepository(database.AppConnectionString))
            await web.CreateWorkspaceAsync(user, tenant, "Synthetic pilot", Now);
        var matterId = Guid.NewGuid();
        await SaveMatterAsync(tenant, matterId);
        await using var operations = Operations();

        var first = await operations.EnqueueDeletionAsync(user.Id, tenant, matterId);
        var retry = await operations.EnqueueDeletionAsync(user.Id, tenant, matterId);
        Assert.Equal(first.DeletionId, retry.DeletionId);
        var worker = Guid.NewGuid();
        var claimed = Assert.IsType<PrivacyDeletionJob>(await operations.ClaimDeletionAsync(
            user.Id, tenant, worker, TimeSpan.FromMinutes(1)));
        await ForceLeaseExpiryAsync(tenant, claimed.DeletionId);
        var recovered = Assert.IsType<PrivacyDeletionJob>(await operations.ClaimDeletionAsync(
            user.Id, tenant, Guid.NewGuid(), TimeSpan.FromMinutes(1)));
        Assert.Equal(claimed.Attempts + 1, recovered.Attempts);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            operations.CompleteDeletionAsync(claimed, worker));
    }

    [PostgresFact]
    public async Task Concurrent_deletion_enqueue_is_idempotent_without_unique_constraint_leakage()
    {
        var user = await CreateUserAsync();
        var tenant = new TenantId(Guid.NewGuid());
        await using (var web = new PostgresWebWorkspaceRepository(database.AppConnectionString))
            await web.CreateWorkspaceAsync(user, tenant, "Synthetic concurrent deletion", Now);
        var matterId = Guid.NewGuid();
        await SaveMatterAsync(tenant, matterId);
        await using var first = Operations();
        await using var second = Operations();

        var jobs = await Task.WhenAll(
            first.EnqueueDeletionAsync(user.Id, tenant, matterId),
            second.EnqueueDeletionAsync(user.Id, tenant, matterId));

        Assert.Equal(jobs[0].DeletionId, jobs[1].DeletionId);
    }

    [PostgresFact]
    public async Task Deletion_retries_back_off_and_enter_an_alertable_terminal_state()
    {
        var clock = new MutableTimeProvider(Now);
        var user = await CreateUserAsync();
        var tenant = new TenantId(Guid.NewGuid());
        await using (var web = new PostgresWebWorkspaceRepository(database.AppConnectionString))
            await web.CreateWorkspaceAsync(user, tenant, "Synthetic terminal deletion", Now);
        var matterId = Guid.NewGuid();
        await SaveMatterAsync(tenant, matterId);
        await using var operations = new PostgresPilotOperationsRepository(database.AppConnectionString, clock);
        var queued = await operations.EnqueueDeletionAsync(user.Id, tenant, matterId);

        PrivacyDeletionJob? current = queued;
        for (var attempt = 1; attempt <= PostgresPilotOperationsRepository.MaximumDeletionAttempts; attempt++)
        {
            var worker = Guid.NewGuid();
            current = Assert.IsType<PrivacyDeletionJob>(await operations.ClaimDeletionAsync(
                user.Id, tenant, worker, TimeSpan.FromMinutes(1)));
            Assert.Equal(attempt, current.Attempts);
            await operations.RetryDeletionAsync(current, worker, "synthetic-storage-failure");
            current = await operations.GetDeletionAsync(tenant, matterId, queued.DeletionId);
            if (attempt < PostgresPilotOperationsRepository.MaximumDeletionAttempts)
            {
                Assert.Equal(PrivacyDeletionStatus.Retry, current?.Status);
                clock.Advance(TimeSpan.FromMinutes(5 * Math.Pow(2, attempt - 1) + 1));
            }
        }

        Assert.Equal(PrivacyDeletionStatus.TerminalFailure, current?.Status);
        Assert.DoesNotContain(await operations.ListPendingDeletionScopesAsync(),
            scope => scope.TenantId == tenant && scope.UserId == user.Id);
        var queue = await operations.GetQueueSnapshotAsync(tenant);
        Assert.Equal(0, queue.PendingDeletions);
        Assert.Equal(1, queue.TerminalDeletionFailures);
    }

    [PostgresFact]
    public async Task Completed_deletion_receipt_survives_routine_operational_pruning()
    {
        var clock = new MutableTimeProvider(Now);
        var user = await CreateUserAsync();
        var tenant = new TenantId(Guid.NewGuid());
        await using (var web = new PostgresWebWorkspaceRepository(database.AppConnectionString))
            await web.CreateWorkspaceAsync(user, tenant, "Synthetic erasure receipt", Now);
        var matterId = Guid.NewGuid();
        await SaveMatterAsync(tenant, matterId);
        await using var operations = new PostgresPilotOperationsRepository(database.AppConnectionString, clock);
        var queued = await operations.EnqueueDeletionAsync(user.Id, tenant, matterId);
        var worker = Guid.NewGuid();
        var claimed = Assert.IsType<PrivacyDeletionJob>(await operations.ClaimDeletionAsync(
            user.Id, tenant, worker, TimeSpan.FromMinutes(1)));
        await operations.CompleteDeletionAsync(claimed, worker);

        clock.Advance(TimeSpan.FromDays(31));
        await operations.PruneOperationalMetadataAsync(tenant);

        var receipt = await operations.GetDeletionAsync(tenant, matterId, queued.DeletionId);
        Assert.Equal(PrivacyDeletionStatus.Completed, receipt?.Status);
        Assert.NotNull(receipt?.CompletedAt);
    }

    [Fact]
    public async Task Pilot_validation_reports_the_correct_argument_names_without_database_access()
    {
        await using var operations = new PostgresPilotOperationsRepository("Host=invalid", new FixedTimeProvider(Now));
        var tenant = new TenantId(Guid.NewGuid());
        var matterId = Guid.NewGuid();

        var hash = await Assert.ThrowsAsync<ArgumentException>(() =>
            operations.ReserveEvidenceAsync(tenant, matterId, null!, 1));
        Assert.Equal("contentSha256", hash.ParamName);
        var duration = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            operations.RecordUsageEventAsync(tenant, matterId, PilotUsageEventKind.ApiRequest,
                "synthetic", durationMilliseconds: -1));
        Assert.Equal("durationMilliseconds", duration.ParamName);
    }

    [PostgresFact]
    public async Task Partial_storage_deletion_failure_remains_retryable_and_reconciliation_completes_idempotently()
    {
        var clock = new MutableTimeProvider(Now);
        var user = await CreateUserAsync();
        var tenant = new TenantId(Guid.NewGuid());
        await using var web = new PostgresWebWorkspaceRepository(database.AppConnectionString);
        await web.CreateWorkspaceAsync(user, tenant, "Synthetic deletion pilot", Now);
        var matterId = Guid.NewGuid();
        await SaveMatterAsync(tenant, matterId);
        await using var operations = new PostgresPilotOperationsRepository(database.AppConnectionString, clock);
        await using var originals = new RelationalDeletionStore(database.AppConnectionString);
        await using var generated = new FailFirstGeneratedDeletionStore();
        var coordinator = new PrivacyDeletionCoordinator(operations, web, generated, originals, clock,
            new PilotRuntimeHealth(clock), NullLogger<PrivacyDeletionCoordinator>.Instance);
        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            var job = await operations.EnqueueDeletionAsync(user.Id, tenant, matterId);
            coordinator.Signal(user.Id, tenant);
            var retry = await WaitForDeletionAsync(operations, tenant, matterId, job.DeletionId,
                PrivacyDeletionStatus.Retry);
            Assert.Equal(1, retry.Attempts);
            await using (var store = new PostgresMatterStore(database.AppConnectionString))
                Assert.NotNull(await store.LoadAsync(tenant, matterId));

            clock.Advance(TimeSpan.FromMinutes(6));
            coordinator.Signal(user.Id, tenant);
            var completed = await WaitForDeletionAsync(operations, tenant, matterId, job.DeletionId,
                PrivacyDeletionStatus.Completed);
            Assert.Equal(2, completed.Attempts);
            Assert.Equal(2, generated.DeleteAttempts);
            await using (var store = new PostgresMatterStore(database.AppConnectionString))
                Assert.Null(await store.LoadAsync(tenant, matterId));
        }
        finally { await coordinator.StopAsync(CancellationToken.None); }
    }

    [PostgresFact]
    public async Task Incomplete_storage_deletion_result_remains_retryable_instead_of_claiming_completion()
    {
        var clock = new MutableTimeProvider(Now);
        var user = await CreateUserAsync();
        var tenant = new TenantId(Guid.NewGuid());
        await using var web = new PostgresWebWorkspaceRepository(database.AppConnectionString);
        await web.CreateWorkspaceAsync(user, tenant, "Synthetic incomplete deletion pilot", Now);
        var matterId = Guid.NewGuid();
        await SaveMatterAsync(tenant, matterId);
        await using var operations = new PostgresPilotOperationsRepository(database.AppConnectionString, clock);
        await using var originals = new RelationalDeletionStore(database.AppConnectionString);
        await using var generated = new IncompleteGeneratedDeletionStore();
        var coordinator = new PrivacyDeletionCoordinator(operations, web, generated, originals, clock,
            new PilotRuntimeHealth(clock), NullLogger<PrivacyDeletionCoordinator>.Instance);
        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            var job = await operations.EnqueueDeletionAsync(user.Id, tenant, matterId);
            coordinator.Signal(user.Id, tenant);
            var retry = await WaitForDeletionAsync(operations, tenant, matterId, job.DeletionId,
                PrivacyDeletionStatus.Retry);

            Assert.Equal(1, retry.Attempts);
            await using var store = new PostgresMatterStore(database.AppConnectionString);
            Assert.NotNull(await store.LoadAsync(tenant, matterId));
        }
        finally { await coordinator.StopAsync(CancellationToken.None); }
    }

    [PostgresFact]
    public async Task Representative_closed_pilot_Matter_write_benchmark_records_real_Postgres_latency()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var matterId = Guid.NewGuid();
        var matter = new Matter(matterId, tenant, "workplace-dispute", "Synthetic 100-document benchmark",
            "active", Now, Now);
        var evidence = new MatterEvidenceGraph(matter);
        for (var index = 0; index < 100; index++)
        {
            var hash = Convert.ToHexString(SHA256.HashData(BitConverter.GetBytes(index)));
            var version = evidence.RegisterDocumentVersion(Guid.NewGuid(), Guid.NewGuid(), hash, Guid.NewGuid());
            var text = $"Synthetic benchmark record {index} attributes one workplace statement.";
            var span = evidence.AddSourceSpan(Guid.NewGuid(), version, text, "synthetic-benchmark/1", .99m,
                textStart: 0, textEnd: text.Length);
            evidence.AddAssertion(Guid.NewGuid(), "synthetic-subject", "records", index.ToString(),
                "synthetic-document-author", Now, EvidenceOriginClass.OriginalContemporaneousRecord,
                AssertionClass.AttributedAssertion, DisputeState.Unverified, IntegrityState.OriginalHashVerified,
                VerificationState.NotReviewed, span.Id, extractionConfidence: .99m);
        }
        var workplace = new WorkplaceMatter(evidence);
        await using var store = new PostgresMatterStore(database.AppConnectionString);
        await store.CreateTenantAsync(tenant, "Synthetic benchmark tenant", Now);
        await store.SaveAsync(evidence, workplace);
        var timings = new List<double>();
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var started = Stopwatch.GetTimestamp();
            await store.SaveAsync(evidence, workplace);
            timings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        timings.Sort();
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            documents = evidence.DocumentVersions.Count,
            sourceSpans = evidence.SourceSpans.Count,
            assertions = evidence.Assertions.Count,
            iterations = timings.Count,
            medianMilliseconds = (timings[9] + timings[10]) / 2,
            p95Milliseconds = timings[(int)Math.Ceiling(timings.Count * .95) - 1]
        }));
        Assert.Equal(100, evidence.DocumentVersions.Count);
        Assert.All(timings, value => Assert.True(value > 0));
    }

    private PostgresPilotOperationsRepository Operations() =>
        new(database.AppConnectionString, new FixedTimeProvider(Now));

    private async Task<WebUser> CreateUserAsync()
    {
        await using var web = new PostgresWebWorkspaceRepository(database.AppConnectionString);
        return await web.UpsertUserAsync("https://synthetic.invalid", Guid.NewGuid().ToString("N"),
            "Synthetic pilot user", Now);
    }

    private async Task SeedTenantAndMatterAsync(TenantId tenant, Guid matterId)
    {
        await using var store = new PostgresMatterStore(database.AppConnectionString);
        await store.CreateTenantAsync(tenant, "Synthetic pilot tenant", Now);
        await SaveMatterAsync(tenant, matterId);
    }

    private async Task SaveMatterAsync(TenantId tenant, Guid matterId)
    {
        var matter = new Matter(matterId, tenant, "workplace-dispute", "Synthetic Matter", "active", Now, Now);
        var evidence = new MatterEvidenceGraph(matter);
        await using var store = new PostgresMatterStore(database.AppConnectionString);
        await store.SaveAsync(evidence, new WorkplaceMatter(evidence));
    }

    private async Task SetLimitsAsync(TenantId tenant, string assignment)
    {
        await using var connection = new NpgsqlConnection(database.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"UPDATE casemesh.pilot_entitlements SET {assignment} WHERE tenant_id=$1;", connection);
        command.Parameters.AddWithValue(tenant.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task ForceLeaseExpiryAsync(TenantId tenant, Guid deletionId)
    {
        await using var connection = new NpgsqlConnection(database.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            UPDATE casemesh.privacy_deletion_jobs SET lease_expires_at=$3
            WHERE tenant_id=$1 AND deletion_id=$2;
            """, connection);
        PostgresMatterStore.AddParameters(command, tenant.Value, deletionId, Now.AddMinutes(-1));
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<PrivacyDeletionJob> WaitForDeletionAsync(
        PostgresPilotOperationsRepository operations, TenantId tenant, Guid matterId,
        Guid deletionId, PrivacyDeletionStatus status)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var job = await operations.GetDeletionAsync(tenant, matterId, deletionId);
            if (job?.Status == status) return job;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Deletion did not reach {status}.");
    }

    private static async Task<object> CaptureAsync(Func<Task> operation)
    {
        try { await operation(); return 1L; }
        catch (Exception exception) { return exception; }
    }

    private static async Task<object> CaptureAsync<T>(Func<Task<T>> operation)
    {
        try { return (object?)await operation() ?? new object(); }
        catch (Exception exception) { return exception; }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class FailFirstGeneratedDeletionStore : IGeneratedArtifactStore
    {
        public int DeleteAttempts { get; private set; }
        public Task<bool> DeleteMatterAsync(TenantId tenantId, Guid matterId,
            CancellationToken cancellationToken = default)
        {
            DeleteAttempts++;
            if (DeleteAttempts == 1) throw new IOException("Synthetic object provider failure.");
            return Task.FromResult(true);
        }
        public Task<StoredGeneratedArtifact> StoreAsync(GeneratedArtifactIdentity identity, Stream content,
            DateTimeOffset expiresAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StoredGeneratedArtifact?> GetMetadataAsync(GeneratedArtifactIdentity identity,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StoredGeneratedArtifact> ReadVerifiedAsync(GeneratedArtifactIdentity identity, Stream destination,
            DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteTenantAsync(TenantId tenantId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> DeleteExpiredAsync(TenantId tenantId, DateTimeOffset now,
            CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<bool> CheckReadinessAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class IncompleteGeneratedDeletionStore : IGeneratedArtifactStore
    {
        public Task<bool> DeleteMatterAsync(TenantId tenantId, Guid matterId,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<StoredGeneratedArtifact> StoreAsync(GeneratedArtifactIdentity identity, Stream content,
            DateTimeOffset expiresAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StoredGeneratedArtifact?> GetMetadataAsync(GeneratedArtifactIdentity identity,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StoredGeneratedArtifact> ReadVerifiedAsync(GeneratedArtifactIdentity identity, Stream destination,
            DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteTenantAsync(TenantId tenantId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> DeleteExpiredAsync(TenantId tenantId, DateTimeOffset now,
            CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<bool> CheckReadinessAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RelationalDeletionStore(string connectionString) : IOriginalEvidenceStore
    {
        private readonly PostgresMatterStore _store = new(connectionString);
        public Task<bool> DeleteMatterAsync(TenantId tenantId, Guid matterId,
            CancellationToken cancellationToken = default) => _store.DeleteMatterAsync(tenantId, matterId, cancellationToken);
        public Task<StoredOriginalEvidence> StoreAsync(TenantId tenantId, Guid matterId, Guid originalObjectId,
            Stream content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StoredOriginalEvidence?> GetMetadataAsync(TenantId tenantId, Guid matterId, Guid originalObjectId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StoredOriginalEvidence> ReadVerifiedAsync(TenantId tenantId, Guid matterId, Guid originalObjectId,
            Stream destination, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StoredOriginalEvidence> VerifyIntegrityAsync(TenantId tenantId, Guid matterId, Guid originalObjectId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteOriginalAsync(TenantId tenantId, Guid matterId, Guid originalObjectId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteTenantAsync(TenantId tenantId,
            CancellationToken cancellationToken = default) => _store.DeleteTenantAsync(tenantId, cancellationToken);
        public async ValueTask DisposeAsync() => await _store.DisposeAsync();
    }
}
