using System.Net;
using System.Text.RegularExpressions;

namespace CaseMesh.Infrastructure.Documents;

internal sealed partial class PlainTextExtractor : ITextExtractor
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".html", ".htm"
    };

    public bool CanHandle(string path) => Extensions.Contains(Path.GetExtension(path));

    public async Task<ExtractedDocument> ExtractAsync(string path, CancellationToken cancellationToken = default)
    {
        var raw = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var extension = Path.GetExtension(path);
        var text = extension.Equals(".html", StringComparison.OrdinalIgnoreCase) || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase)
            ? WebUtility.HtmlDecode(HtmlTagRegex().Replace(raw, " "))
            : raw;
        return new(Path.GetFileName(path), extension is ".html" or ".htm" ? "text/html" : "text/plain", text, File.GetLastWriteTimeUtc(path));
    }

    [GeneratedRegex("<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex HtmlTagRegex();
}
