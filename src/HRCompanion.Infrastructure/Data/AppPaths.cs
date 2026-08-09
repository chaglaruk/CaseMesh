namespace HRCompanion.Infrastructure.Data;

public sealed class AppPaths
{
    public AppPaths(string? rootOverride = null)
    {
        Root = rootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HRCompanion");

        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Imports);
        Directory.CreateDirectory(Logs);
    }

    public string Root { get; }
    public string Database => Path.Combine(Root, "hrcompanion.db");
    public string Imports => Path.Combine(Root, "imports");
    public string Logs => Path.Combine(Root, "logs");
}
