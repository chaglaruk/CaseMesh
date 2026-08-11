using UglyToad.PdfPig;

namespace HRCompanion.Infrastructure.Documents;

internal sealed class PdfTextExtractor : ITextExtractor
{
    public bool CanHandle(string path) => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public Task<ExtractedDocument> ExtractAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sections = new List<LocatedText>();
        using var document = PdfDocument.Open(path);
        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(page.Text))
            {
                sections.Add(new(page.Text, $"p.{page.Number}"));
            }
        }
        var text = string.Join(Environment.NewLine, sections.Select(x => x.Text));
        return Task.FromResult(new ExtractedDocument(Path.GetFileName(path), "application/pdf", text, null, sections));
    }
}
