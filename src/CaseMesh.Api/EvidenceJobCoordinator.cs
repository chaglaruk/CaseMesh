using System.Text.Json;
using System.Threading.Channels;
using CaseMesh.Core.Models;
using CaseMesh.Ingestion;
using CaseMesh.MatterBrain;
using CaseMesh.Persistence.Postgres;
using CaseMesh.Storage;

namespace CaseMesh.Api;

public sealed class EvidenceJobCoordinator(
    PostgresWebWorkspaceRepository jobs,
    PostgresMatterBrainStore brains,
    IOriginalEvidenceStore originals,
    IIngestionRepository ingestionRepository,
    IMalwareScanner scanner,
    IOcrEngine ocr,
    IPdfPageRasterizer rasterizer,
    IngestionPipeline pipeline,
    CaseMeshApiOptions options,
    IHostEnvironment environment,
    TimeProvider timeProvider,
    ILogger<EvidenceJobCoordinator> logger) : BackgroundService
{
    private readonly Channel<JobSignal> _signals = Channel.CreateBounded<JobSignal>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false
    });
    private readonly Guid _workerId = Guid.NewGuid();

    public bool Signal(Guid userId, TenantId tenantId) => _signals.Writer.TryWrite(new JobSignal(userId, tenantId));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var signal in _signals.Reader.ReadAllAsync(stoppingToken))
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                WebProcessingJob? job;
                try
                {
                    job = await jobs.ClaimAsync(signal.UserId, signal.TenantId, _workerId,
                        timeProvider.GetUtcNow(), TimeSpan.FromMinutes(5), stoppingToken);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Evidence job lease acquisition failed.");
                    break;
                }
                if (job is null) break;
                await ProcessAsync(signal.UserId, job, stoppingToken);
            }
        }
    }

    private async Task ProcessAsync(Guid userId, WebProcessingJob job, CancellationToken cancellationToken)
    {
        try
        {
            var service = new CommercialEvidenceIngestionService(originals, ingestionRepository, scanner, ocr,
                pipeline, new IngestionLimits(MaximumBytes: options.MaximumUploadBytes), rasterizer, timeProvider);
            await service.IngestAsync(new IngestionDocument(job.TenantId, job.MatterId, job.DocumentId,
                job.DocumentVersionId, job.OriginalObjectId), cancellationToken);
            var loaded = await brains.LoadAsync(job.TenantId, job.MatterId, cancellationToken)
                         ?? throw new InvalidOperationException("The Matter disappeared during processing.");
            var spans = loaded.Evidence.SourceSpans
                .Where(span => span.DocumentVersion.DocumentVersionId == job.DocumentVersionId)
                .Select(span => span.Id).ToArray();
            if (options.EnableTestAuthentication && environment.IsEnvironment("Testing") && spans.Length > 0)
                await new MatterBrainMergeService(timeProvider).ExtractAndMergeAsync(
                    loaded.Brain, spans, new SyntheticWorkplaceExtractionProvider(timeProvider), cancellationToken);
            await brains.SaveAsync(loaded.Brain, loaded.Workplace, cancellationToken);
            await jobs.CompleteAsync(userId, job.TenantId, job.MatterId, job.JobId, _workerId,
                timeProvider.GetUtcNow(), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Evidence processing failed with code {FailureCode}.", exception.GetType().Name);
            await jobs.FailAsync(userId, job.TenantId, job.MatterId, job.JobId, _workerId,
                timeProvider.GetUtcNow(), exception is IngestionException ingestion ? ingestion.Code : "processing-failed",
                CancellationToken.None);
        }
    }

    private sealed record JobSignal(Guid UserId, TenantId TenantId);
}

internal sealed class SyntheticCleanScanner : IMalwareScanner
{
    public string Provider => "synthetic-clean";
    public string Version => "runtime-configured";
    public Task<MalwareScanResult> ScanAsync(string filePath, CancellationToken cancellationToken) =>
        Task.FromResult(new MalwareScanResult(true, false, Provider, Version, "clean"));
}

internal sealed class SyntheticOcrEngine : IOcrEngine
{
    public string Provider => "synthetic-ocr";
    public string Version => "runtime-configured";
    public Task<IReadOnlyList<OcrWord>> RecognizeAsync(string imagePath, int pageNumber, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OcrWord>>([]);
}

internal sealed class SyntheticWorkplaceExtractionProvider(TimeProvider timeProvider) : IStructuredExtractionProvider
{
    public StructuredExtractionProviderDescriptor Descriptor { get; } =
        new("casemesh-test", "deterministic-synthetic", "1", "1", "1");

    public Task<StructuredExtractionOutput> ExtractAsync(StructuredExtractionInput input,
        CancellationToken cancellationToken = default)
    {
        var span = input.SourceSpans.OrderBy(item => item.Id).First();
        var value = span.Text.Length <= 160 ? span.Text : span.Text[..160];
        var assertedAt = timeProvider.GetUtcNow();
        var candidates = new StructuredCandidateBatch([], [],
            [new AssertionCandidate("synthetic-source-assertion", "Uploaded document", "states", value,
                "Document author", assertedAt, null, span.Id, EvidenceOriginClass.OriginalContemporaneousRecord,
                AssertionClass.AttributedAssertion, IntegrityState.OriginalHashVerified, [span.Id], 1m)],
            [], [], [], []);
        return Task.FromResult(new StructuredExtractionOutput(
            JsonSerializer.Serialize(new { source = span.Id, mode = "synthetic-test" }), candidates));
    }
}
