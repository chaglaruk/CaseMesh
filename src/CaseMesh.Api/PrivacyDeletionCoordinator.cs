using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using CaseMesh.Core.Models;
using CaseMesh.Persistence.Postgres;
using CaseMesh.Storage;

namespace CaseMesh.Api;

public sealed class PrivacyDeletionCoordinator(
    PostgresPilotOperationsRepository operations,
    PostgresWebWorkspaceRepository workspaces,
    IGeneratedArtifactStore generated,
    IOriginalEvidenceStore originals,
    TimeProvider timeProvider,
    PilotRuntimeHealth runtimeHealth,
    ILogger<PrivacyDeletionCoordinator> logger) : BackgroundService
{
    private readonly Channel<PrivacyDeletionScope> _signals = Channel.CreateBounded<PrivacyDeletionScope>(
        new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly Guid _workerId = Guid.NewGuid();

    public bool Signal(Guid userId, TenantId tenantId) =>
        _signals.Writer.TryWrite(new PrivacyDeletionScope(userId, tenantId));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        runtimeHealth.MarkDeletionWorker();
        var sweeper = SweepAsync(stoppingToken);
        try
        {
            await foreach (var scope in _signals.Reader.ReadAllAsync(stoppingToken))
            {
                runtimeHealth.MarkDeletionWorker();
                while (!stoppingToken.IsCancellationRequested)
                {
                    PrivacyDeletionJob? job;
                    try
                    {
                        job = await operations.ClaimDeletionAsync(scope.UserId, scope.TenantId, _workerId,
                            TimeSpan.FromMinutes(10), stoppingToken);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        logger.LogError("Privacy deletion lease acquisition failed with type {ExceptionType}.",
                            exception.GetType().Name);
                        break;
                    }
                    if (job is null) break;
                    await ProcessAsync(job, stoppingToken);
                }
            }
        }
        finally { await sweeper; }
    }

    private async Task ProcessAsync(PrivacyDeletionJob job, CancellationToken cancellationToken)
    {
        try
        {
            await using var matterLock = await workspaces.AcquireMatterStateLockAsync(
                job.TenantId, job.MatterId, cancellationToken);
            if (!await generated.DeleteMatterAsync(job.TenantId, job.MatterId, cancellationToken))
                throw new InvalidOperationException("Generated artifact deletion did not reconcile all metadata.");
            var originalDeletionCompleted = await originals.DeleteMatterAsync(
                job.TenantId, job.MatterId, cancellationToken);
            if (!originalDeletionCompleted && await workspaces.GetMatterAsync(
                    job.RequestedByUserId, job.TenantId, job.MatterId, cancellationToken) is not null)
                throw new InvalidOperationException("Original evidence deletion did not complete the Matter cascade.");
            await operations.CompleteDeletionAsync(job, _workerId, cancellationToken);
            await RecordUsageSafelyAsync(() => operations.RecordUsageEventAsync(job.TenantId, job.MatterId,
                PilotUsageEventKind.Deletion, "completed", cancellationToken: cancellationToken));
            PilotOperationsTelemetry.DeletionOutcomes.Add(1, new TagList { { "outcome", "completed" } });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Privacy deletion {DeletionId} will retry after failure type {ExceptionType}.",
                job.DeletionId, exception.GetType().Name);
            PilotOperationsTelemetry.DeletionOutcomes.Add(1, new TagList { { "outcome", "retry" } });
            try
            {
                await operations.RetryDeletionAsync(job, _workerId, "storage-or-database", CancellationToken.None);
            }
            catch (Exception recordingFailure)
            {
                logger.LogError("Privacy deletion retry state could not be recorded for {DeletionId}: {ExceptionType}.",
                    job.DeletionId, recordingFailure.GetType().Name);
            }
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), timeProvider);
        do
        {
            runtimeHealth.MarkDeletionWorker();
            try
            {
                foreach (var scope in await operations.ListPendingDeletionScopesAsync(cancellationToken))
                    await _signals.Writer.WriteAsync(scope, cancellationToken);
                foreach (var tenantId in await operations.ListMaintenanceTenantsAsync(cancellationToken))
                {
                    try
                    {
                        await generated.DeleteExpiredAsync(tenantId, timeProvider.GetUtcNow(), cancellationToken);
                        await operations.PruneOperationalMetadataAsync(tenantId, cancellationToken);
                        var queue = await operations.GetQueueSnapshotAsync(tenantId, cancellationToken);
                        PilotOperationsTelemetry.QueueDepth.Record(queue.PendingJobs,
                            new TagList { { "queue", "ingestion" } });
                        PilotOperationsTelemetry.OldestJobAge.Record(queue.OldestJobAgeSeconds,
                            new TagList { { "queue", "ingestion" } });
                        PilotOperationsTelemetry.QueueDepth.Record(queue.PendingDeletions,
                            new TagList { { "queue", "deletion" } });
                        PilotOperationsTelemetry.OldestJobAge.Record(queue.OldestDeletionAgeSeconds,
                            new TagList { { "queue", "deletion" } });
                        PilotOperationsTelemetry.ReconciliationOutcomes.Add(1,
                            new TagList { { "outcome", "completed" } });
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        logger.LogWarning("Tenant maintenance failed with type {ExceptionType}.",
                            exception.GetType().Name);
                        PilotOperationsTelemetry.ReconciliationOutcomes.Add(1,
                            new TagList { { "outcome", "failed" } });
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError("Privacy deletion sweep failed with type {ExceptionType}.", exception.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    private async Task RecordUsageSafelyAsync(Func<Task> record)
    {
        try { await record(); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Deletion usage metadata could not be recorded: {ExceptionType}.",
                exception.GetType().Name);
        }
    }
}
