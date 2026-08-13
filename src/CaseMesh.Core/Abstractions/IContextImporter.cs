namespace CaseMesh.Core.Abstractions;

public sealed record ContextImportResult(
    int ParsedRecords,
    int Imported,
    int SkippedDuplicate,
    IReadOnlyList<string> Errors);

public interface IContextImporter
{
    Task<ContextImportResult> ImportAsync(string path, CancellationToken cancellationToken = default);
}
