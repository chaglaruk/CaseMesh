using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CaseMesh.Audio.Windows;

public sealed record TeamsProcessInfo(
    int ProcessId,
    string ProcessName,
    string? MainWindowTitle,
    int ProcessTreeSize)
{
    public string DisplayLabel =>
        $"{ProcessName} — PID {ProcessId}" +
        (string.IsNullOrWhiteSpace(MainWindowTitle) ? string.Empty : $" — {MainWindowTitle}") +
        (ProcessTreeSize > 1 ? $" ({ProcessTreeSize} Teams processes)" : string.Empty);
}

public static class TeamsProcessLocator
{
    private static readonly HashSet<string> CandidateNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ms-teams", "Teams", "MSTeams"
    };

    public static IReadOnlyList<TeamsProcessInfo> Find()
    {
        var parentIds = SnapshotParentProcessIds();
        var processes = new Dictionary<int, ProcessSnapshot>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (!IsTeamsProcessName(process.ProcessName)) continue;
                    processes[process.Id] = new(
                        process.Id,
                        process.ProcessName,
                        string.IsNullOrWhiteSpace(process.MainWindowTitle) ? null : process.MainWindowTitle,
                        parentIds.GetValueOrDefault(process.Id));
                }
                catch (InvalidOperationException)
                {
                    // Process exited while enumerating.
                }
            }
        }

        return processes.Values
            .GroupBy(process => FindCandidateRoot(process, processes))
            .Select(group => CreateInfo(group.Key, group.ToArray(), processes))
            .OrderByDescending(process => !string.IsNullOrWhiteSpace(process.MainWindowTitle))
            .ThenBy(process => process.ProcessId)
            .ToArray();
    }

    public static bool IsTeamsProcessName(string processName) => CandidateNames.Contains(processName);

    private static int FindCandidateRoot(ProcessSnapshot process, IReadOnlyDictionary<int, ProcessSnapshot> processes)
    {
        var current = process;
        var visited = new HashSet<int> { current.ProcessId };
        while (current.ParentProcessId is int parentId &&
               processes.TryGetValue(parentId, out var parent) &&
               visited.Add(parentId))
        {
            current = parent;
        }
        return current.ProcessId;
    }

    private static TeamsProcessInfo CreateInfo(
        int rootProcessId,
        IReadOnlyList<ProcessSnapshot> tree,
        IReadOnlyDictionary<int, ProcessSnapshot> processes)
    {
        var root = processes[rootProcessId];
        var title = root.MainWindowTitle ?? tree
            .Where(process => !string.IsNullOrWhiteSpace(process.MainWindowTitle))
            .OrderBy(process => process.ProcessId)
            .Select(process => process.MainWindowTitle)
            .FirstOrDefault();
        return new(root.ProcessId, root.ProcessName, title, tree.Count);
    }

    private static Dictionary<int, int> SnapshotParentProcessIds()
    {
        const uint snapshotProcesses = 0x00000002;
        var result = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(snapshotProcesses, 0);
        if (snapshot == new IntPtr(-1)) return result;

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry)) return result;
            do
            {
                result[checked((int)entry.ProcessId)] = checked((int)entry.ParentProcessId);
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            }
            while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return result;
    }

    private sealed record ProcessSnapshot(int ProcessId, string ProcessName, string? MainWindowTitle, int? ParentProcessId);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint ThreadCount;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
