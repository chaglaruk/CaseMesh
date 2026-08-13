using CaseMesh.Core.Abstractions;
using CaseMesh.Core.Models;

namespace CaseMesh.Infrastructure.Documents;

/// <summary>
/// Imports a private starter-context Markdown/TXT file into the fact ledger without treating it as source evidence.
/// Only explicitly labelled bullets are accepted so arbitrary documents cannot silently become facts.
/// </summary>
public sealed class WorkingContextImporter : IContextImporter
{
    private const string UserPositionLabel = "USER_POSITION";
    private const string WorkingContextLabel = "WORKING_CONTEXT";
    private const string DocumentedReportedLabel = "DOCUMENTED / REPORTED FROM SOURCE";
    private readonly ICaseRepository _repository;

    public WorkingContextImporter(ICaseRepository repository) => _repository = repository;

    public async Task<ContextImportResult> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path)) throw new FileNotFoundException("Working-context file was not found.", path);
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".hrcontext", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".md", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Working context must be an .hrcontext, Markdown (.md), or text (.txt) file.");
        }

        var existing = (await _repository.GetFactsAsync(cancellationToken).ConfigureAwait(false))
            .Select(fact => NormalizeStatement(fact.Statement))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        var parsed = new List<(string Statement, FactStatus Status, string Locator)>();
        var section = "context";

        foreach (var raw in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = raw.Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                section = line[3..].Trim();
                continue;
            }

            if (!line.StartsWith("- `", StringComparison.Ordinal)) continue;
            var closing = line.IndexOf('`', 3);
            if (closing <= 3) continue;

            var label = line[3..closing].Trim();
            var statement = line[(closing + 1)..].Trim();
            if (statement.Length == 0) continue;

            var status = label switch
            {
                UserPositionLabel => FactStatus.UserPosition,
                WorkingContextLabel => FactStatus.Unverified,
                DocumentedReportedLabel => FactStatus.Unverified,
                _ => (FactStatus?)null
            };
            if (status is null) continue;

            parsed.Add((statement, status.Value, $"{Path.GetFileName(path)} — {section}"));
        }

        var imported = 0;
        var duplicate = 0;
        var errors = new List<string>();
        foreach (var item in parsed)
        {
            var normalized = NormalizeStatement(item.Statement);
            if (!existing.Add(normalized))
            {
                duplicate++;
                continue;
            }

            try
            {
                await _repository.SaveFactAsync(new CaseFact(
                    Guid.NewGuid(),
                    item.Statement,
                    item.Status,
                    SourceDocumentId: null,
                    SourceLocator: item.Locator,
                    EffectiveDate: null,
                    CreatedAt: DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
                imported++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{item.Locator}: {ex.Message}");
            }
        }

        return new(parsed.Count, imported, duplicate, errors);
    }

    private static string NormalizeStatement(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
}
