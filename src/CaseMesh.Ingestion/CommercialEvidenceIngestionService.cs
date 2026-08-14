using CaseMesh.Storage;

namespace CaseMesh.Ingestion;

public sealed class CommercialEvidenceIngestionService
{
    private const int BufferSize = 64 * 1024;
    private readonly IOriginalEvidenceStore _storage;
    private readonly IIngestionRepository _repository;
    private readonly IMalwareScanner _scanner;
    private readonly IOcrEngine _ocr;
    private readonly IPdfPageRasterizer? _rasterizer;
    private readonly IngestionPipeline _pipeline;
    private readonly IngestionLimits _limits;
    private readonly TimeProvider _timeProvider;

    public CommercialEvidenceIngestionService(
        IOriginalEvidenceStore storage,
        IIngestionRepository repository,
        IMalwareScanner scanner,
        IOcrEngine ocr,
        IngestionPipeline pipeline,
        IngestionLimits? limits = null,
        IPdfPageRasterizer? rasterizer = null,
        TimeProvider? timeProvider = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _limits = limits ?? new IngestionLimits();
        _rasterizer = rasterizer;
        _timeProvider = timeProvider ?? TimeProvider.System;
        ValidatePipeline();
        ValidateLimits();
    }

    public async Task<CompletedIngestion> IngestAsync(
        IngestionDocument document,
        CancellationToken cancellationToken = default)
    {
        ValidateDocument(document);
        var startedAt = _timeProvider.GetUtcNow();
        var attemptId = Guid.NewGuid();
        var path = Path.Combine(Path.GetTempPath(), $"casemesh-ingestion-{Guid.NewGuid():N}.bin");
        var workDirectory = Path.Combine(Path.GetTempPath(), $"casemesh-ingestion-work-{Guid.NewGuid():N}");
        long byteLength = 0;
        EvidenceMediaType? detected = null;
        MalwareScanResult? scan = null;
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                BufferSize = BufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            await using (var file = new FileStream(path, options))
            await using (var bounded = new BoundedWriteStream(file, _limits.MaximumBytes))
            {
                await _storage.ReadVerifiedAsync(document.TenantId, document.MatterId,
                    document.OriginalObjectId, bounded, cancellationToken);
                await bounded.FlushAsync(cancellationToken);
                byteLength = bounded.BytesWritten;
            }

            var existing = await _repository.FindCompletedAsync(document, _pipeline.Fingerprint, cancellationToken);
            if (existing is not null) return existing with { WasAlreadyCompleted = true };

            scan = await _scanner.ScanAsync(path, cancellationToken);
            if (!scan.IsClean)
            {
                var kind = scan.IsThreat ? IngestionFailureKind.MalwareDetected : IngestionFailureKind.ScannerUnavailable;
                throw new EvidenceIngestionException(kind,
                    scan.IsThreat ? "malware-detected" : "scanner-not-clean",
                    scan.IsThreat
                        ? "The evidence was quarantined by the malware safety gate."
                        : "The malware safety gate did not return a clean result.");
            }

            detected = ContentTypeDetector.Detect(path, _limits);
            Directory.CreateDirectory(workDirectory);
            var regions = await EvidenceParsers.ParseAsync(path, detected.Value, document,
                _pipeline.Fingerprint, _pipeline, _limits, _ocr, _rasterizer, workDirectory, cancellationToken);
            if (regions.Count > _limits.MaximumRegions)
                throw new EvidenceIngestionException(IngestionFailureKind.ResourceLimit,
                    "region-limit", "The evidence produced too many source regions.");

            var completedAt = _timeProvider.GetUtcNow();
            var spanSetId = DeterministicSpanSetId(document, _pipeline.Fingerprint);
            var attempt = new IngestionAttempt(attemptId, document, _pipeline.Fingerprint, startedAt,
                completedAt, IngestionStatus.Completed, detected, byteLength, scan.Provider, scan.Version,
                scan.ResultCode, null, null, spanSetId);
            return await _repository.SaveCompletedAsync(attempt, detected.Value,
                regions.FirstOrDefault(region => region.Route == ExtractionRoute.Native)?.Provider ?? "none",
                _pipeline.ParserVersion,
                regions.Any(region => region.Route == ExtractionRoute.Ocr) ? _pipeline.OcrProvider : null,
                regions.Any(region => region.Route == ExtractionRoute.Ocr) ? _pipeline.OcrVersion : null,
                regions, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var failure = MapFailure(exception);
            var status = failure.Kind == IngestionFailureKind.MalwareDetected
                ? IngestionStatus.Quarantined
                : IngestionStatus.Failed;
            var attempt = new IngestionAttempt(attemptId, document, _pipeline.Fingerprint, startedAt,
                _timeProvider.GetUtcNow(), status, detected, byteLength,
                scan?.Provider ?? _scanner.Provider, scan?.Version ?? _scanner.Version, scan?.ResultCode,
                failure.Kind, failure.Code, null);
            try
            {
                await _repository.SaveFailureAsync(attempt, CancellationToken.None);
            }
            catch (Exception persistenceException)
            {
                throw new EvidenceIngestionException(IngestionFailureKind.PersistenceConflict,
                    "failure-state-persistence-failed",
                    "Evidence processing failed and its typed failure state could not be persisted.",
                    new AggregateException(exception, persistenceException));
            }

            throw failure;
        }
        finally
        {
            TryDeleteFile(path);
            TryDeleteDirectory(workDirectory);
        }
    }

    private EvidenceIngestionException MapFailure(Exception exception) => exception switch
    {
        EvidenceIngestionException ingestion => ingestion,
        OriginalEvidenceIntegrityException => new EvidenceIngestionException(
            IngestionFailureKind.Integrity, "storage-integrity-failed",
            "The verified original evidence failed its SHA-256 or length integrity check.", exception),
        OriginalEvidenceStorageException => new EvidenceIngestionException(
            IngestionFailureKind.Integrity, "storage-read-failed",
            "The verified original evidence could not be read.", exception),
        ResourceLimitException limit => new EvidenceIngestionException(
            IngestionFailureKind.ResourceLimit, limit.Code,
            "The evidence exceeds a configured ingestion resource limit.", exception),
        InvalidOperationException => new EvidenceIngestionException(
            IngestionFailureKind.PersistenceConflict, "ingestion-persistence-conflict",
            "The ingestion result conflicts with persisted tenant-scoped ingestion state.", exception),
        TimeoutException => new EvidenceIngestionException(
            IngestionFailureKind.ResourceLimit, "external-process-timeout",
            "An external evidence process exceeded its configured time limit.", exception),
        _ => new EvidenceIngestionException(IngestionFailureKind.ParserFailure,
            "parser-failed", "The evidence parser failed without exposing evidence content.", exception)
    };

    private static Guid DeterministicSpanSetId(IngestionDocument document, string fingerprint)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{document.TenantId.Value:D}\n{document.MatterId:D}\n{document.DocumentVersionId:D}\n{fingerprint}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private void ValidatePipeline()
    {
        foreach (var value in new[] { _pipeline.PipelineVersion, _pipeline.ScannerProvider,
                     _pipeline.ScannerVersion, _pipeline.ParserVersion, _pipeline.OcrProvider, _pipeline.OcrVersion })
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!string.Equals(_pipeline.ScannerProvider, _scanner.Provider, StringComparison.Ordinal) ||
            !string.Equals(_pipeline.ScannerVersion, _scanner.Version, StringComparison.Ordinal) ||
            !string.Equals(_pipeline.OcrProvider, _ocr.Provider, StringComparison.Ordinal) ||
            !string.Equals(_pipeline.OcrVersion, _ocr.Version, StringComparison.Ordinal))
            throw new ArgumentException("Pipeline provider identities must match the configured safety and OCR adapters.");
    }

    private void ValidateLimits()
    {
        if (_limits.MaximumBytes <= 0 || _limits.MaximumPages <= 0 || _limits.MaximumRegions <= 0 ||
            _limits.MaximumTextCharacters <= 0 || _limits.MaximumPackageEntries <= 0 ||
            _limits.MaximumExpandedPackageBytes <= 0 || _limits.MaximumPackageCompressionRatio < 1m ||
            _limits.MaximumImagePixels <= 0 || _limits.MaximumImageDimension <= 0 ||
            _limits.ProcessTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(_limits), "Every ingestion resource limit must be positive.");
    }

    private static void ValidateDocument(IngestionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.TenantId.Value == Guid.Empty || document.MatterId == Guid.Empty ||
            document.DocumentId == Guid.Empty || document.DocumentVersionId == Guid.Empty ||
            document.OriginalObjectId == Guid.Empty)
            throw new ArgumentException("Tenant, Matter, document, version and original-object identities are required.");
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class BoundedWriteStream(Stream inner, long maximumBytes) : Stream
    {
        public long BytesWritten { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            RequireCapacity(buffer.Length);
            inner.Write(buffer);
            BytesWritten += buffer.Length;
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            RequireCapacity(buffer.Length);
            await inner.WriteAsync(buffer, cancellationToken);
            BytesWritten += buffer.Length;
        }
        private void RequireCapacity(int count)
        {
            if (count < 0 || BytesWritten > maximumBytes - count)
                throw new ResourceLimitException("byte-limit");
        }
        protected override void Dispose(bool disposing) { }
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class ResourceLimitException(string code) : Exception
    {
        internal string Code { get; } = code;
    }
}
