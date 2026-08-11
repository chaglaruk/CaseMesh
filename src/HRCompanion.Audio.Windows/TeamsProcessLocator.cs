using System.Diagnostics;

namespace HRCompanion.Audio.Windows;

public sealed record TeamsProcessInfo(int ProcessId, string ProcessName, string? MainWindowTitle)
{
    public bool IsLikelyRoot => !string.IsNullOrWhiteSpace(MainWindowTitle);
    public string DisplayName => $"{ProcessName} (PID {ProcessId}){(MainWindowTitle is null ? string.Empty : " — " + MainWindowTitle)}";
}

public static class TeamsProcessLocator
{
    private static readonly HashSet<string> CandidateNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ms-teams", "Teams", "MSTeams"
    };

    public static bool IsTeamsProcessName(string processName) => CandidateNames.Contains(processName);

    public static IReadOnlyList<TeamsProcessInfo> Find()
    {
        var matches = new List<TeamsProcessInfo>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (!IsTeamsProcessName(process.ProcessName)) continue;
                    matches.Add(new(process.Id, process.ProcessName, string.IsNullOrWhiteSpace(process.MainWindowTitle) ? null : process.MainWindowTitle));
                }
                catch (InvalidOperationException)
                {
                    // Process exited while enumerating.
                }
            }
        }
        return matches
            .OrderByDescending(x => x.IsLikelyRoot)
            .ThenBy(x => x.ProcessId)
            .ToArray();
    }
}
