using HRCompanion.Core.Models;

namespace HRCompanion.Core.Abstractions;

public interface IDocumentImporter
{
    Task<DocumentImportResult> ImportPathsAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default);
    Task<DocumentImportResult> ImportPathsAsync(
        IEnumerable<string> paths,
        DocumentImportOptions options,
        CancellationToken cancellationToken = default);
}
