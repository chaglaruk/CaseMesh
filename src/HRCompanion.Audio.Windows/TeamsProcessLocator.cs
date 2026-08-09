using System.Diagnostics;

namespace HRCompanion.Audio.Windows;

public sealed record TeamsProcessInfo(int ProcessId, string ProcessName, string? MainWindowTitle);

public static class TeamsProcessLocator
{
    private static readonly HashSet<string> CandidateNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ms-teams", "Teams", "MSTeams"
    };

    public static IReadOnlyList<TeamsProcessInfo> Find()
    {
        var matches = new List<TeamsProcessInfo>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (!CandidateNames.Contains(process.ProcessName)) continue;
                    matches.Add(new(process.Id, process.ProcessName, string.IsNullOrWhiteSpace(process.MainWindowTitle) ? null : process.MainWindowTitle));
                }
                catch (InvalidOperationException)
                {
                    // Process exited while enumerating.
                }
            }
        }
        return matches.OrderBy(x => x.ProcessId).ToArray();
    }
}
