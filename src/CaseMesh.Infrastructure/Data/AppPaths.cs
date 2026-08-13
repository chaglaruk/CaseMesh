namespace CaseMesh.Infrastructure.Data;

public sealed class AppPaths
{
    private const string ProductDirectoryName = "CaseMesh";
    private const string LegacyProductDirectoryName = "HRCompanion";
    private const string DatabaseFileName = "casemesh.db";
    private const string LegacyDatabaseFileName = "hrcompanion.db";

    public AppPaths(string? rootOverride = null)
    {
        Root = rootOverride ?? ResolveDefaultRoot();

        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Imports);
        Directory.CreateDirectory(Logs);
        MigrateLegacyDatabaseFiles();
    }

    public string Root { get; }
    public string Database => Path.Combine(Root, DatabaseFileName);
    public string Imports => Path.Combine(Root, "imports");
    public string Logs => Path.Combine(Root, "logs");

    private static string ResolveDefaultRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var currentRoot = Path.Combine(localAppData, ProductDirectoryName);
        var legacyRoot = Path.Combine(localAppData, LegacyProductDirectoryName);

        if (!Directory.Exists(currentRoot) && Directory.Exists(legacyRoot))
        {
            try
            {
                Directory.Move(legacyRoot, currentRoot);
            }
            catch (IOException)
            {
                return legacyRoot;
            }
            catch (UnauthorizedAccessException)
            {
                return legacyRoot;
            }
        }

        return currentRoot;
    }

    private void MigrateLegacyDatabaseFiles()
    {
        foreach (var suffix in new[] { string.Empty, "-shm", "-wal" })
        {
            var legacyPath = Path.Combine(Root, LegacyDatabaseFileName + suffix);
            var currentPath = Path.Combine(Root, DatabaseFileName + suffix);
            if (File.Exists(legacyPath) && !File.Exists(currentPath))
            {
                File.Move(legacyPath, currentPath);
            }
        }
    }
}
