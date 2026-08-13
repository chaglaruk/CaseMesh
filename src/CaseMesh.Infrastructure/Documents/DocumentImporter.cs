using System.Security.Cryptography;
using CaseMesh.Core.Abstractions;
using CaseMesh.Core.Models;

namespace CaseMesh.Infrastructure.Documents;

public sealed class DocumentImporter : IDocumentImporter
{
    private readonly ICaseRepository _repository;
    private readonly TextChunker _chunker;
    private readonly IReadOnlyList<ITextExtractor> _extractors;

    public DocumentImporter(ICaseRepository repository, TextChunker? chunker = null)
    {
        _repository = repository;
        _chunker = chunker ?? new TextChunker();
        _extractors =
        [
            new PlainTextExtractor(),
            new DocxTextExtractor(),
            new EmlTextExtractor(),
            new PdfTextExtractor()
        ];
    }

    public async Task<DocumentImportResult> ImportPathsAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        var files = ExpandPaths(paths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var imported = 0;
        var duplicate = 0;
        var unsupported = 0;
        var errors = new List<string>();

        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extractor = _extractors.FirstOrDefault(x => x.CanHandle(path));
            if (extractor is null)
            {
                unsupported++;
                continue;
            }

            try
            {
                var hash = await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
                if (await _repository.HasDocumentHashAsync(hash, cancellationToken).ConfigureAwait(false))
                {
                    duplicate++;
                    continue;
                }

                var extracted = await extractor.ExtractAsync(path, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(extracted.Text))
                {
                    throw new InvalidDataException(
                        "No extractable text was found. If this is a scanned/image-only document, create a searchable/OCR copy before importing it.");
                }

                var documentId = Guid.NewGuid();
                var sections = extracted.LocatedSections is { Count: > 0 }
                    ? extracted.LocatedSections
                    : new[] { new LocatedText(extracted.Text, null) };
                var chunks = _chunker.Chunk(documentId, sections);
                if (chunks.Count == 0)
                {
                    throw new InvalidDataException("The document did not produce any searchable text chunks.");
                }

                var document = new DocumentRecord(
                    documentId,
                    extracted.DisplayName,
                    path,
                    hash,
                    extracted.MediaType,
                    DateTimeOffset.UtcNow,
                    extracted.SourceDate,
                    chunks.Count);

                await _repository.SaveDocumentAsync(document, chunks, cancellationToken).ConfigureAwait(false);
                imported++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Imports are best-effort per file. One malformed or password-protected document
                // must not prevent the rest of a case folder from being indexed.
                errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        return new(files.Length, imported, duplicate, unsupported, errors);
    }

    private static IEnumerable<string> ExpandPaths(IEnumerable<string> paths)
    {
        foreach (var input in paths.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var full = Path.GetFullPath(input);
            if (File.Exists(full))
            {
                yield return full;
            }
            else if (Directory.Exists(full))
            {
                foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                {
                    yield return file;
                }
            }
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }
}
