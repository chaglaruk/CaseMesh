using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MimeKit;
using UglyToad.PdfPig;

namespace CaseMesh.Ingestion;

internal static class EvidenceParsers
{
    internal static async Task<IReadOnlyList<ExtractedRegion>> ParseAsync(
        string path,
        EvidenceMediaType mediaType,
        IngestionDocument document,
        string pipelineFingerprint,
        IngestionPipeline pipeline,
        IngestionLimits limits,
        IOcrEngine ocr,
        IPdfPageRasterizer? rasterizer,
        string temporaryDirectory,
        CancellationToken cancellationToken) => mediaType switch
        {
            EvidenceMediaType.Pdf => await ParsePdfAsync(path, document, pipelineFingerprint, pipeline,
                limits, ocr, rasterizer, temporaryDirectory, cancellationToken),
            EvidenceMediaType.Docx => ParseDocx(path, document, pipelineFingerprint, pipeline, limits),
            EvidenceMediaType.Eml => await ParseEmlAsync(path, document, pipelineFingerprint, pipeline, limits, cancellationToken),
            EvidenceMediaType.PlainText => await ParseTextAsync(path, document, pipelineFingerprint, pipeline, limits, cancellationToken),
            EvidenceMediaType.Png or EvidenceMediaType.Jpeg => await ParseImageAsync(path, 1, document,
                pipelineFingerprint, pipeline, limits, ocr, cancellationToken),
            _ => throw new EvidenceIngestionException(IngestionFailureKind.UnsupportedMedia,
                "unsupported-media", "The detected media type is not supported.")
        };

    private static async Task<IReadOnlyList<ExtractedRegion>> ParsePdfAsync(
        string path,
        IngestionDocument document,
        string fingerprint,
        IngestionPipeline pipeline,
        IngestionLimits limits,
        IOcrEngine ocr,
        IPdfPageRasterizer? rasterizer,
        string temporaryDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            using var pdf = PdfDocument.Open(path);
            if (pdf.NumberOfPages > limits.MaximumPages) throw Limit("page-limit");
            var native = new List<ExtractedRegion>();
            var totalCharacters = 0;
            foreach (var page in pdf.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var text = page.Text;
                if (string.IsNullOrWhiteSpace(text)) continue;
                totalCharacters = checked(totalCharacters + text.Length);
                if (totalCharacters > limits.MaximumTextCharacters) throw Limit("text-character-limit");
                native.Add(CreateRegion(document, fingerprint, native.Count, SourceLocatorKind.PdfPage,
                    $"pdf:page:{page.Number}", text, ExtractionRoute.Native, "pdfpig", pipeline.ParserVersion,
                    page.Number, 0, text.Length));
            }

            if (native.Count > 0) return native;
        }
        catch (EvidenceIngestionException) { throw; }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new EvidenceIngestionException(IngestionFailureKind.MalformedMedia,
                "malformed-pdf", "The PDF could not be parsed safely.", exception);
        }

        if (rasterizer is null)
        {
            throw new EvidenceIngestionException(IngestionFailureKind.OcrUnavailable,
                "scanned-pdf-rasterizer-unavailable", "The image-only PDF requires the configured OCR rasterizer.");
        }

        var images = await rasterizer.RasterizeAsync(path, temporaryDirectory, limits, cancellationToken);
        if (images.Count == 0) throw new EvidenceIngestionException(IngestionFailureKind.OcrFailure,
            "scanned-pdf-empty", "The image-only PDF produced no OCR pages.");
        var regions = new List<ExtractedRegion>();
        for (var index = 0; index < images.Count; index++)
        {
            if (ContentTypeDetector.Detect(images[index], limits) != EvidenceMediaType.Png)
                throw new EvidenceIngestionException(IngestionFailureKind.MalformedMedia,
                    "invalid-raster-output", "The PDF rasterizer returned an unexpected image type.");
            var words = await ocr.RecognizeAsync(images[index], index + 1, cancellationToken);
            AddOcrWords(regions, words, document, fingerprint, pipeline, limits);
        }

        return regions.Count == 0
            ? throw new EvidenceIngestionException(IngestionFailureKind.OcrFailure,
                "ocr-no-text", "OCR produced no source-addressable text.")
            : regions;
    }

    private static IReadOnlyList<ExtractedRegion> ParseDocx(
        string path,
        IngestionDocument document,
        string fingerprint,
        IngestionPipeline pipeline,
        IngestionLimits limits)
    {
        try
        {
            using var package = WordprocessingDocument.Open(path, false);
            var body = package.MainDocumentPart?.Document?.Body
                ?? throw new InvalidDataException("DOCX has no document body.");
            var regions = new List<ExtractedRegion>();
            var tables = body.Descendants<Table>().ToList();
            var cellCoordinates = new Dictionary<TableCell, (int Table, int Row, int Cell)>();
            for (var tableIndex = 0; tableIndex < tables.Count; tableIndex++)
            {
                var rows = tables[tableIndex].Elements<TableRow>().ToList();
                for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
                {
                    var cells = rows[rowIndex].Elements<TableCell>().ToList();
                    for (var cellIndex = 0; cellIndex < cells.Count; cellIndex++)
                        cellCoordinates[cells[cellIndex]] = (tableIndex, rowIndex, cellIndex);
                }
            }
            var documentOffset = 0;
            var paragraphOrdinal = 0;
            foreach (var paragraph in body.Descendants<Paragraph>())
            {
                var text = paragraph.InnerText;
                if (string.IsNullOrWhiteSpace(text)) continue;
                var cell = paragraph.Ancestors<TableCell>().FirstOrDefault();
                var kind = cell is null ? SourceLocatorKind.DocxParagraph : SourceLocatorKind.DocxTableCell;
                string locator;
                if (cell is null)
                {
                    locator = $"docx:paragraph:{paragraphOrdinal}";
                }
                else
                {
                    var coordinates = cellCoordinates[cell];
                    locator = $"docx:table:{coordinates.Table}:row:{coordinates.Row}:cell:{coordinates.Cell}:paragraph:{paragraphOrdinal}";
                }
                regions.Add(CreateRegion(document, fingerprint, regions.Count, kind, locator, text,
                    ExtractionRoute.Native, "openxml", pipeline.ParserVersion, null,
                    documentOffset, documentOffset + text.Length));
                documentOffset = checked(documentOffset + text.Length + 1);
                paragraphOrdinal++;
                EnforceRegions(regions, documentOffset, limits);
            }

            return regions;
        }
        catch (EvidenceIngestionException) { throw; }
        catch (Exception exception)
        {
            throw new EvidenceIngestionException(IngestionFailureKind.MalformedMedia,
                "malformed-docx", "The DOCX document could not be parsed safely.", exception);
        }
    }

    private static async Task<IReadOnlyList<ExtractedRegion>> ParseEmlAsync(
        string path,
        IngestionDocument document,
        string fingerprint,
        IngestionPipeline pipeline,
        IngestionLimits limits,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var message = await MimeMessage.LoadAsync(stream, cancellationToken);
            var regions = new List<ExtractedRegion>();
            var offset = 0;
            foreach (var header in message.Headers)
            {
                var text = $"{header.Field}: {header.Value}";
                regions.Add(CreateRegion(document, fingerprint, regions.Count, SourceLocatorKind.EmailHeader,
                    $"eml:header:{header.Id}:{regions.Count}", text, ExtractionRoute.Native, "mimekit",
                    pipeline.ParserVersion, null, offset, offset + text.Length));
                offset += text.Length + 1;
                EnforceRegions(regions, offset, limits);
            }

            var body = message.TextBody ?? message.HtmlBody;
            if (!string.IsNullOrWhiteSpace(body))
            {
                regions.Add(CreateRegion(document, fingerprint, regions.Count, SourceLocatorKind.EmailBody,
                    message.TextBody is null ? "eml:body:html-inert" : "eml:body:text",
                    body, ExtractionRoute.Native, "mimekit", pipeline.ParserVersion,
                    null, offset, offset + body.Length));
                offset += body.Length + 1;
                EnforceRegions(regions, offset, limits);
            }

            var attachmentOrdinal = 0;
            foreach (var attachment in message.Attachments)
            {
                var name = attachment.ContentDisposition?.FileName ?? attachment.ContentType.Name ?? "unnamed";
                var metadata = $"attachment-name={name}; content-type={attachment.ContentType.MimeType}";
                regions.Add(CreateRegion(document, fingerprint, regions.Count, SourceLocatorKind.EmailAttachment,
                    $"eml:attachment:{attachmentOrdinal++}", metadata, ExtractionRoute.Native, "mimekit",
                    pipeline.ParserVersion, null, offset, offset + metadata.Length));
                offset += metadata.Length + 1;
                EnforceRegions(regions, offset, limits);
            }

            return regions;
        }
        catch (EvidenceIngestionException) { throw; }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new EvidenceIngestionException(IngestionFailureKind.MalformedMedia,
                "malformed-eml", "The email container could not be parsed safely.", exception);
        }
    }

    private static async Task<IReadOnlyList<ExtractedRegion>> ParseTextAsync(
        string path,
        IngestionDocument document,
        string fingerprint,
        IngestionPipeline pipeline,
        IngestionLimits limits,
        CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
        if (text.Length > limits.MaximumTextCharacters) throw Limit("text-character-limit");
        if (string.IsNullOrWhiteSpace(text)) return [];
        return [CreateRegion(document, fingerprint, 0, SourceLocatorKind.TextCharacters,
            $"text:chars:0-{text.Length}", text, ExtractionRoute.Native, "dotnet-utf8",
            pipeline.ParserVersion, null, 0, text.Length)];
    }

    private static async Task<IReadOnlyList<ExtractedRegion>> ParseImageAsync(
        string path,
        int pageNumber,
        IngestionDocument document,
        string fingerprint,
        IngestionPipeline pipeline,
        IngestionLimits limits,
        IOcrEngine ocr,
        CancellationToken cancellationToken)
    {
        var words = await ocr.RecognizeAsync(path, pageNumber, cancellationToken);
        var regions = new List<ExtractedRegion>();
        AddOcrWords(regions, words, document, fingerprint, pipeline, limits);
        return regions.Count == 0
            ? throw new EvidenceIngestionException(IngestionFailureKind.OcrFailure,
                "ocr-no-text", "OCR produced no source-addressable text.")
            : regions;
    }

    private static void AddOcrWords(
        List<ExtractedRegion> regions,
        IReadOnlyList<OcrWord> words,
        IngestionDocument document,
        string fingerprint,
        IngestionPipeline pipeline,
        IngestionLimits limits)
    {
        var characters = regions.Sum(item => item.Text.Length);
        foreach (var word in words)
        {
            if (string.IsNullOrWhiteSpace(word.Text)) continue;
            regions.Add(CreateRegion(document, fingerprint, regions.Count, SourceLocatorKind.ImageBoundingBox,
                $"ocr:page:{word.PageNumber}:bbox:{word.Left},{word.Top},{word.Width},{word.Height}",
                word.Text, ExtractionRoute.Ocr, pipeline.OcrProvider, pipeline.OcrVersion,
                word.PageNumber, null, null, word.Confidence,
                word.Left, word.Top, word.Width, word.Height));
            characters = checked(characters + word.Text.Length);
            EnforceRegions(regions, characters, limits);
        }
    }

    private static ExtractedRegion CreateRegion(
        IngestionDocument document,
        string fingerprint,
        int ordinal,
        SourceLocatorKind kind,
        string locator,
        string text,
        ExtractionRoute route,
        string provider,
        string providerVersion,
        int? pageNumber = null,
        int? textStart = null,
        int? textEnd = null,
        decimal? confidence = null,
        int? left = null,
        int? top = null,
        int? width = null,
        int? height = null)
    {
        var idBytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{document.TenantId.Value:D}\n{document.MatterId:D}\n{document.DocumentVersionId:D}\n{fingerprint}\n{ordinal}"));
        var id = new Guid(idBytes.AsSpan(0, 16));
        return new ExtractedRegion(id, ordinal, kind, locator, text, IngestionDigests.Sha256(text),
            route, provider, providerVersion, pageNumber, textStart, textEnd, confidence,
            left, top, width, height);
    }

    private static void EnforceRegions(IReadOnlyCollection<ExtractedRegion> regions, int characters, IngestionLimits limits)
    {
        if (regions.Count > limits.MaximumRegions) throw Limit("region-limit");
        if (characters > limits.MaximumTextCharacters) throw Limit("text-character-limit");
    }

    private static EvidenceIngestionException Limit(string code) => new(
        IngestionFailureKind.ResourceLimit, code, "The evidence exceeds a configured parsing resource limit.");
}
