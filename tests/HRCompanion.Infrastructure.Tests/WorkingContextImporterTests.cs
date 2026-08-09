using HRCompanion.Core.Models;
using HRCompanion.Infrastructure.Data;
using HRCompanion.Infrastructure.Documents;

namespace HRCompanion.Infrastructure.Tests;

public sealed class WorkingContextImporterTests : IAsyncLifetime
{
    private string _root = null!;
    private SqliteCaseRepository _repository = null!;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "HRCompanion.Context.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _repository = new SqliteCaseRepository(new AppPaths(_root));
        await _repository.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, true); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Import_MapsLabelsToFactTrustWithoutCreatingDocumentEvidence()
    {
        var path = Path.Combine(_root, "private.hrcontext");
        await File.WriteAllTextAsync(path, """
            ## Objectives
            - `USER_POSITION` I remain open to a suitable alternative role.
            - `WORKING_CONTEXT` A meeting was discussed on a synthetic date.
            - `DOCUMENTED / REPORTED FROM SOURCE` A source is expected to support this synthetic statement.
            - `UNKNOWN_LABEL` This must be ignored.
            """);

        var importer = new WorkingContextImporter(_repository);
        var result = await importer.ImportAsync(path);
        var facts = await _repository.GetFactsAsync();
        var documents = await _repository.GetDocumentsAsync();

        Assert.Equal(3, result.Imported);
        Assert.Empty(documents);
        Assert.Contains(facts, fact => fact.Status == FactStatus.UserPosition && fact.Statement.Contains("suitable alternative role", StringComparison.Ordinal));
        Assert.Equal(2, facts.Count(fact => fact.Status == FactStatus.Unverified));
    }

    [Fact]
    public async Task Import_DeduplicatesIdenticalContextStatements()
    {
        var path = Path.Combine(_root, "private.hrcontext");
        await File.WriteAllTextAsync(path, "- `USER_POSITION` Keep this answer short and natural.\n");
        var importer = new WorkingContextImporter(_repository);

        var first = await importer.ImportAsync(path);
        var second = await importer.ImportAsync(path);

        Assert.Equal(1, first.Imported);
        Assert.Equal(0, second.Imported);
        Assert.Equal(1, second.SkippedDuplicate);
    }
}
