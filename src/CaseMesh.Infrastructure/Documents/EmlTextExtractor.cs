using System.Text;
using MimeKit;

namespace CaseMesh.Infrastructure.Documents;

internal sealed class EmlTextExtractor : ITextExtractor
{
    public bool CanHandle(string path) => Path.GetExtension(path).Equals(".eml", StringComparison.OrdinalIgnoreCase);

    public async Task<ExtractedDocument> ExtractAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var message = await MimeMessage.LoadAsync(stream, cancellationToken).ConfigureAwait(false);
        var builder = new StringBuilder();
        builder.AppendLine($"From: {message.From}");
        builder.AppendLine($"To: {message.To}");
        if (message.Cc.Count > 0) builder.AppendLine($"Cc: {message.Cc}");
        builder.AppendLine($"Date: {message.Date:O}");
        builder.AppendLine($"Subject: {message.Subject}");
        builder.AppendLine();
        builder.AppendLine(message.TextBody ?? StripHtml(message.HtmlBody) ?? string.Empty);

        var attachmentNames = message.Attachments
            .Select(part => part.ContentDisposition?.FileName ?? part.ContentType.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (attachmentNames.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Attachments (names only; import exported files separately for content):");
            foreach (var name in attachmentNames) builder.AppendLine($"- {name}");
        }

        return new(
            string.IsNullOrWhiteSpace(message.Subject) ? Path.GetFileName(path) : $"{message.Subject} ({Path.GetFileName(path)})",
            "message/rfc822",
            builder.ToString(),
            message.Date == DateTimeOffset.MinValue ? File.GetLastWriteTimeUtc(path) : message.Date);
    }

    private static string? StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var span = html.AsSpan();
        var sb = new StringBuilder(html.Length);
        var insideTag = false;
        foreach (var ch in span)
        {
            if (ch == '<') { insideTag = true; continue; }
            if (ch == '>') { insideTag = false; sb.Append(' '); continue; }
            if (!insideTag) sb.Append(ch);
        }
        return System.Net.WebUtility.HtmlDecode(sb.ToString());
    }
}
