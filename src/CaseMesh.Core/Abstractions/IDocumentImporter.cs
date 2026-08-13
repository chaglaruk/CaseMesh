using CaseMesh.Core.Models;

namespace CaseMesh.Core.Abstractions;

public interface IDocumentImporter
{
    Task<DocumentImportResult> ImportPathsAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default);
}
