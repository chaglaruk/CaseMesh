using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace CaseMesh.Infrastructure.Documents;

internal sealed class DocxTextExtractor : ITextExtractor
{
    public bool CanHandle(string path) => Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase);

    public Task<ExtractedDocument> ExtractAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        var paragraphs = body is null
            ? []
            : body.Descendants<Paragraph>()
                .Select(p => p.InnerText.Trim())
                .Where(x => x.Length > 0)
                .ToArray();
        var text = string.Join(Environment.NewLine, paragraphs);
        return Task.FromResult(new ExtractedDocument(Path.GetFileName(path), "application/vnd.openxmlformats-officedocument.wordprocessingml.document", text, File.GetLastWriteTimeUtc(path)));
    }
}
