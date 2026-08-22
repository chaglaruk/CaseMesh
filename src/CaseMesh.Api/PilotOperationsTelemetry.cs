using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace CaseMesh.Api;

public sealed class PilotRuntimeHealth(TimeProvider timeProvider)
{
    private long _evidenceHeartbeat;
    private long _deletionHeartbeat;
    public void MarkEvidenceWorker() => Interlocked.Exchange(ref _evidenceHeartbeat, timeProvider.GetUtcNow().Ticks);
    public void MarkDeletionWorker() => Interlocked.Exchange(ref _deletionHeartbeat, timeProvider.GetUtcNow().Ticks);
    public bool EvidenceWorkerReady(TimeSpan maximumAge) => IsFresh(_evidenceHeartbeat, maximumAge);
    public bool DeletionWorkerReady(TimeSpan maximumAge) => IsFresh(_deletionHeartbeat, maximumAge);
    private bool IsFresh(long ticks, TimeSpan maximumAge) => ticks > 0 &&
        timeProvider.GetUtcNow() - new DateTimeOffset(ticks, TimeSpan.Zero) <= maximumAge;
}

public static class PilotOperationsTelemetry
{
    public const string MeterName = "CaseMesh.Pilot";
    private static readonly Meter Meter = new(MeterName, "1.0.0");
    internal static readonly Counter<long> ApiRequests = Meter.CreateCounter<long>("casemesh.api.requests");
    internal static readonly Histogram<double> ApiDuration = Meter.CreateHistogram<double>("casemesh.api.duration", "ms");
    internal static readonly Counter<long> QuotaRejections = Meter.CreateCounter<long>("casemesh.pilot.quota_rejections");
    internal static readonly Histogram<double> QueueDepth = Meter.CreateHistogram<double>("casemesh.jobs.queue_depth", "jobs");
    internal static readonly Histogram<double> OldestJobAge = Meter.CreateHistogram<double>("casemesh.jobs.oldest_age", "s");
    internal static readonly Histogram<double> IngestionDuration = Meter.CreateHistogram<double>("casemesh.ingestion.duration", "ms");
    internal static readonly Counter<long> IngestionOutcomes = Meter.CreateCounter<long>("casemesh.ingestion.outcomes");
    internal static readonly Histogram<double> QaDuration = Meter.CreateHistogram<double>("casemesh.qa.duration", "ms");
    internal static readonly Counter<long> QaOutcomes = Meter.CreateCounter<long>("casemesh.qa.outcomes");
    internal static readonly Histogram<double> ExportDuration = Meter.CreateHistogram<double>("casemesh.export.duration", "ms");
    internal static readonly Counter<long> ExportOutcomes = Meter.CreateCounter<long>("casemesh.export.outcomes");
    internal static readonly Counter<long> DeletionOutcomes = Meter.CreateCounter<long>("casemesh.deletion.outcomes");
    internal static readonly Counter<long> ReconciliationOutcomes = Meter.CreateCounter<long>("casemesh.reconciliation.outcomes");
}

public sealed class PilotTelemetryMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        try { await next(context); }
        finally
        {
            var route = (context.GetEndpoint() as Microsoft.AspNetCore.Routing.RouteEndpoint)
                ?.RoutePattern.RawText ?? "unmatched";
            var tags = new TagList
            {
                { "http.request.method", context.Request.Method },
                { "http.route", route },
                { "http.response.status_class", $"{context.Response.StatusCode / 100}xx" }
            };
            PilotOperationsTelemetry.ApiRequests.Add(1, tags);
            PilotOperationsTelemetry.ApiDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, tags);
        }
    }
}
