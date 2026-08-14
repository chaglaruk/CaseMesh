using System.Diagnostics;
using System.Globalization;

namespace CaseMesh.Ingestion;

public sealed class ClamAvCliScanner : IMalwareScanner
{
    private readonly string _executable;
    private readonly TimeSpan _timeout;
    private readonly string? _databasePath;

    public ClamAvCliScanner(
        string version,
        TimeSpan timeout,
        string executable = "clamscan",
        string? databasePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        _executable = executable;
        _timeout = timeout;
        _databasePath = databasePath;
        Version = version;
    }

    public string Provider => "clamav-cli";
    public string Version { get; }

    public async Task<MalwareScanResult> ScanAsync(string filePath, CancellationToken cancellationToken)
    {
        ProcessResult result;
        try
        {
            var arguments = new List<string> { "--no-summary", "--infected" };
            if (!string.IsNullOrWhiteSpace(_databasePath))
            {
                arguments.Add("--database");
                arguments.Add(_databasePath);
            }
            arguments.Add("--");
            arguments.Add(filePath);
            result = await ExternalProcess.RunAsync(
                _executable,
                arguments,
                _timeout,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new EvidenceIngestionException(
                IngestionFailureKind.ScannerUnavailable,
                "scanner-unavailable",
                "The configured malware scanner could not be started.",
                exception);
        }

        return result.ExitCode switch
        {
            0 => new(true, false, Provider, Version, "clean"),
            1 => new(false, true, Provider, Version, "threat"),
            _ => throw new EvidenceIngestionException(
                IngestionFailureKind.ScannerUnavailable,
                "scanner-error",
                "The configured malware scanner did not complete successfully.")
        };
    }
}

public sealed class TesseractCliOcrEngine : IOcrEngine
{
    private readonly string _executable;
    private readonly TimeSpan _timeout;

    public TesseractCliOcrEngine(string version, TimeSpan timeout, string executable = "tesseract")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        Version = version;
        _timeout = timeout;
        _executable = executable;
    }

    public string Provider => "tesseract-cli";
    public string Version { get; }

    public async Task<IReadOnlyList<OcrWord>> RecognizeAsync(
        string imagePath,
        int pageNumber,
        CancellationToken cancellationToken)
    {
        ProcessResult result;
        try
        {
            result = await ExternalProcess.RunAsync(
                _executable,
                [imagePath, "stdout", "-l", "eng", "--psm", "6", "tsv"],
                _timeout,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new EvidenceIngestionException(
                IngestionFailureKind.OcrUnavailable,
                "ocr-unavailable",
                "The configured OCR engine could not be started.",
                exception);
        }

        if (result.ExitCode != 0)
        {
            throw new EvidenceIngestionException(
                IngestionFailureKind.OcrFailure,
                "ocr-failed",
                "The OCR engine did not complete successfully.");
        }

        var words = new List<OcrWord>();
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var fields = line.TrimEnd('\r').Split('\t');
            if (fields.Length < 12 || fields[0] != "5" || string.IsNullOrWhiteSpace(fields[11])) continue;
            if (!int.TryParse(fields[6], CultureInfo.InvariantCulture, out var left) ||
                !int.TryParse(fields[7], CultureInfo.InvariantCulture, out var top) ||
                !int.TryParse(fields[8], CultureInfo.InvariantCulture, out var width) ||
                !int.TryParse(fields[9], CultureInfo.InvariantCulture, out var height) ||
                !decimal.TryParse(fields[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var confidence))
            {
                continue;
            }

            words.Add(new OcrWord(fields[11], pageNumber, left, top, width, height,
                decimal.Clamp(confidence / 100m, 0m, 1m)));
        }

        return words;
    }
}

public sealed class PopplerPdfPageRasterizer : IPdfPageRasterizer
{
    private readonly string _executable;
    private readonly TimeSpan _timeout;

    public PopplerPdfPageRasterizer(string version, TimeSpan timeout, string executable = "pdftoppm")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        Version = version;
        _timeout = timeout;
        _executable = executable;
    }

    public string Provider => "poppler-pdftoppm";
    public string Version { get; }

    public async Task<IReadOnlyList<string>> RasterizeAsync(
        string pdfPath,
        string outputDirectory,
        IngestionLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limits);
        Directory.CreateDirectory(outputDirectory);
        var prefix = Path.Combine(outputDirectory, "page");
        var pixelDimension = (int)Math.Min(int.MaxValue, Math.Floor(Math.Sqrt(limits.MaximumImagePixels)));
        var scaleBound = Math.Min(limits.MaximumImageDimension, pixelDimension);
        if (scaleBound <= 0) throw new ArgumentOutOfRangeException(nameof(limits));
        ProcessResult result;
        try
        {
            result = await ExternalProcess.RunAsync(
                _executable,
                ["-png", "-scale-to", scaleBound.ToString(CultureInfo.InvariantCulture), "-f", "1", "-l", limits.MaximumPages.ToString(CultureInfo.InvariantCulture), pdfPath, prefix],
                _timeout,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new EvidenceIngestionException(
                IngestionFailureKind.OcrUnavailable,
                "pdf-rasterizer-unavailable",
                "The configured scanned-PDF rasterizer could not be started.",
                exception);
        }

        if (result.ExitCode != 0)
        {
            throw new EvidenceIngestionException(
                IngestionFailureKind.OcrFailure,
                "pdf-rasterization-failed",
                "The scanned PDF could not be rasterized for OCR.");
        }

        return Directory.GetFiles(outputDirectory, "page-*.png")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }
}

internal sealed record ProcessResult(int ExitCode, string StandardOutput);

internal static class ExternalProcess
{
    internal static async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The external process did not start.");
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(outputTask, errorTask);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("The external evidence process exceeded its configured time limit.");
        }

        var output = await outputTask;
        _ = await errorTask;
        return new ProcessResult(process.ExitCode, output);
    }
}
