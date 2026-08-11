using HRCompanion.Core.Models;
using HRCompanion.Infrastructure.Data;

namespace HRCompanion.Infrastructure.Tests;

public sealed class EvidenceIsolationTests
{
    [Fact]
    public async Task Search_UsesOnlyOrdinaryCurrentEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "HRCompanion.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new SqliteCaseRepository(new AppPaths(root));
            await repository.InitializeAsync();

            var ordinary = await SaveAsync(repository, "ordinary.txt", "shared marker ordinary evidence", EvidenceChannel.OrdinaryHr, EvidenceAuthority.CurrentFinal);
            await SaveAsync(repository, "restricted.txt", "shared marker restricted secret", EvidenceChannel.AcasWithoutPrejudice, EvidenceAuthority.CurrentFinal);
            await SaveAsync(repository, "historical.txt", "shared marker historical old", EvidenceChannel.OrdinaryHr, EvidenceAuthority.Historical);

            var results = await repository.SearchAsync("shared marker", 8);

            var result = Assert.Single(results);
            Assert.Equal(ordinary.Id, result.DocumentId);
            Assert.Contains("ordinary evidence", result.Text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("restricted", result.Text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("historical", result.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task ReclassifyingExistingSource_RemovesItFromOrdinaryMeetingRetrieval()
    {
        var root = Path.Combine(Path.GetTempPath(), "HRCompanion.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new SqliteCaseRepository(new AppPaths(root));
            await repository.InitializeAsync();
            var document = await SaveAsync(repository, "existing.txt", "migration marker evidence", EvidenceChannel.OrdinaryHr, EvidenceAuthority.CurrentFinal);
            Assert.Single(await repository.SearchAsync("migration marker"));

            await repository.UpdateDocumentClassificationAsync(
                document.Id,
                EvidenceChannel.AcasWithoutPrejudice,
                EvidenceAuthority.CurrentFinal);

            Assert.Empty(await repository.SearchAsync("migration marker"));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task FactsSourcedToRestrictedDocuments_AreExcludedButUnsourcedUserPositionRemains()
    {
        var root = Path.Combine(Path.GetTempPath(), "HRCompanion.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new SqliteCaseRepository(new AppPaths(root));
            await repository.InitializeAsync();
            var ordinary = await SaveAsync(repository, "ordinary.txt", "ordinary fact source", EvidenceChannel.OrdinaryHr, EvidenceAuthority.CurrentFinal);
            var restricted = await SaveAsync(repository, "restricted.txt", "restricted fact source", EvidenceChannel.AcasWithoutPrejudice, EvidenceAuthority.CurrentFinal);
            var now = DateTimeOffset.UtcNow;
            await repository.SaveFactAsync(new(Guid.NewGuid(), "ordinary verified", FactStatus.Verified, ordinary.Id, "p.1", null, now));
            await repository.SaveFactAsync(new(Guid.NewGuid(), "restricted verified", FactStatus.Verified, restricted.Id, "p.1", null, now));
            await repository.SaveFactAsync(new(Guid.NewGuid(), "meeting preference", FactStatus.UserPosition, null, "context", null, now));

            var facts = await repository.GetFactsAsync();

            Assert.Contains(facts, fact => fact.Statement == "ordinary verified");
            Assert.Contains(facts, fact => fact.Statement == "meeting preference");
            Assert.DoesNotContain(facts, fact => fact.Statement == "restricted verified");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static async Task<DocumentRecord> SaveAsync(
        SqliteCaseRepository repository,
        string name,
        string text,
        EvidenceChannel channel,
        EvidenceAuthority authority)
    {
        var id = Guid.NewGuid();
        var document = new DocumentRecord(
            id,
            name,
            name,
            Guid.NewGuid().ToString("N"),
            "text/plain",
            DateTimeOffset.UtcNow,
            null,
            1,
            channel,
            authority);
        await repository.SaveDocumentAsync(document, [new(Guid.NewGuid(), id, 0, text, "p.1")]);
        return document;
    }
}
