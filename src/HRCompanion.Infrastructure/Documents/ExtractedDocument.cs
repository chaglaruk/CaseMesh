namespace HRCompanion.Infrastructure.Documents;

internal sealed record ExtractedDocument(
    string DisplayName,
    string MediaType,
    string Text,
    DateTimeOffset? SourceDate,
    IReadOnlyList<LocatedText>? LocatedSections = null);

public sealed record LocatedText(string Text, string? Locator);

internal interface ITextExtractor
{
    bool CanHandle(string path);
    Task<ExtractedDocument> ExtractAsync(string path, CancellationToken cancellationToken = default);
}
