using CaseMesh.Core.Models;

namespace CaseMesh.Ingestion;

public enum EvidenceMediaType
{
    Pdf = 1,
    Docx = 2,
    Eml = 3,
    PlainText = 4,
    Png = 5,
    Jpeg = 6
}

public enum IngestionStatus
{
    Pending = 1,
    Completed = 2,
    Quarantined = 3,
    Failed = 4
}

public enum IngestionFailureKind
{
    Integrity = 1,
    UnsupportedMedia = 2,
    MalformedMedia = 3,
    MalwareDetected = 4,
    ScannerUnavailable = 5,
    ParserFailure = 6,
    OcrUnavailable = 7,
    OcrFailure = 8,
    ResourceLimit = 9,
    PersistenceConflict = 10
}

public enum ExtractionRoute
{
    Native = 1,
    Ocr = 2
}

public enum SourceLocatorKind
{
    PdfPage = 1,
    DocxParagraph = 2,
    DocxTableCell = 3,
    EmailHeader = 4,
    EmailBody = 5,
    EmailAttachment = 6,
    TextCharacters = 7,
    ImageBoundingBox = 8
}

public sealed record IngestionDocument(
    TenantId TenantId,
    Guid MatterId,
    Guid DocumentId,
    Guid DocumentVersionId,
    Guid OriginalObjectId);

public sealed record IngestionPipeline(
    string PipelineVersion,
    string ScannerProvider,
    string ScannerVersion,
    string ParserVersion,
    string OcrProvider,
    string OcrVersion)
{
    public string Fingerprint => IngestionDigests.Sha256(
        string.Join('\n', PipelineVersion, ScannerProvider, ScannerVersion,
            ParserVersion, OcrProvider, OcrVersion));
}

public sealed record IngestionLimits(
    long MaximumBytes = 25 * 1024 * 1024,
    int MaximumPages = 250,
    int MaximumRegions = 20_000,
    int MaximumTextCharacters = 2_000_000,
    int MaximumPackageEntries = 2_000,
    long MaximumExpandedPackageBytes = 100 * 1024 * 1024,
    decimal MaximumPackageCompressionRatio = 100m,
    long MaximumImagePixels = 40_000_000,
    int MaximumImageDimension = 12_000,
    TimeSpan? ExternalProcessTimeout = null)
{
    public TimeSpan ProcessTimeout => ExternalProcessTimeout ?? TimeSpan.FromSeconds(30);
}

public sealed record MalwareScanResult(
    bool IsClean,
    bool IsThreat,
    string Provider,
    string Version,
    string ResultCode);

public interface IMalwareScanner
{
    string Provider { get; }
    string Version { get; }
    Task<MalwareScanResult> ScanAsync(string filePath, CancellationToken cancellationToken);
}

public sealed record OcrWord(
    string Text,
    int PageNumber,
    int Left,
    int Top,
    int Width,
    int Height,
    decimal Confidence);

public interface IOcrEngine
{
    string Provider { get; }
    string Version { get; }
    Task<IReadOnlyList<OcrWord>> RecognizeAsync(string imagePath, int pageNumber, CancellationToken cancellationToken);
}

public interface IPdfPageRasterizer
{
    string Provider { get; }
    string Version { get; }
    Task<IReadOnlyList<string>> RasterizeAsync(
        string pdfPath,
        string outputDirectory,
        int maximumPages,
        CancellationToken cancellationToken);
}

public sealed record ExtractedRegion(
    Guid SourceSpanId,
    int Ordinal,
    SourceLocatorKind LocatorKind,
    string Locator,
    string Text,
    string TextDigest,
    ExtractionRoute Route,
    string Provider,
    string ProviderVersion,
    int? PageNumber = null,
    int? TextStart = null,
    int? TextEnd = null,
    decimal? Confidence = null,
    int? BoundingBoxLeft = null,
    int? BoundingBoxTop = null,
    int? BoundingBoxWidth = null,
    int? BoundingBoxHeight = null);

public sealed record IngestionAttempt(
    Guid AttemptId,
    IngestionDocument Document,
    string PipelineFingerprint,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IngestionStatus Status,
    EvidenceMediaType? DetectedMediaType,
    long ByteLength,
    string? ScannerProvider,
    string? ScannerVersion,
    string? ScannerResult,
    IngestionFailureKind? FailureKind,
    string? FailureCode,
    Guid? SpanSetId);

public sealed record CompletedIngestion(
    Guid AttemptId,
    Guid SpanSetId,
    EvidenceMediaType MediaType,
    long ByteLength,
    string PipelineFingerprint,
    IReadOnlyList<ExtractedRegion> Regions,
    bool WasAlreadyCompleted);

public interface IIngestionRepository
{
    Task<CompletedIngestion?> FindCompletedAsync(
        IngestionDocument document,
        string pipelineFingerprint,
        CancellationToken cancellationToken);

    Task<CompletedIngestion> SaveCompletedAsync(
        IngestionAttempt attempt,
        EvidenceMediaType mediaType,
        string parserProvider,
        string parserVersion,
        string? ocrProvider,
        string? ocrVersion,
        IReadOnlyList<ExtractedRegion> regions,
        CancellationToken cancellationToken);

    Task SaveFailureAsync(IngestionAttempt attempt, CancellationToken cancellationToken);

    Task<IReadOnlyList<IngestionAttempt>> ListAttemptsAsync(
        IngestionDocument document,
        CancellationToken cancellationToken);
}

public abstract class IngestionException : Exception
{
    protected IngestionException(IngestionFailureKind kind, string code, string message, Exception? inner = null)
        : base(message, inner) { Kind = kind; Code = code; }

    public IngestionFailureKind Kind { get; }
    public string Code { get; }
}

public sealed class EvidenceIngestionException : IngestionException
{
    public EvidenceIngestionException(IngestionFailureKind kind, string code, string message, Exception? inner = null)
        : base(kind, code, message, inner) { }
}

public static class IngestionDigests
{
    public static string Sha256(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value)));
}
