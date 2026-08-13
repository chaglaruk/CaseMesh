using CaseMesh.Infrastructure.Data;

namespace CaseMesh.Infrastructure.Tests;

public sealed class AppPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "CaseMesh.AppPaths.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Constructor_MigratesLegacyDatabaseAndSidecars()
    {
        Directory.CreateDirectory(_root);
        foreach (var suffix in new[] { string.Empty, "-shm", "-wal" })
        {
            File.WriteAllText(Path.Combine(_root, "hrcompanion.db" + suffix), "synthetic");
        }

        var paths = new AppPaths(_root);

        Assert.Equal(Path.Combine(_root, "casemesh.db"), paths.Database);
        foreach (var suffix in new[] { string.Empty, "-shm", "-wal" })
        {
            Assert.False(File.Exists(Path.Combine(_root, "hrcompanion.db" + suffix)));
            Assert.Equal("synthetic", File.ReadAllText(Path.Combine(_root, "casemesh.db" + suffix)));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
