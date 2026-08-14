namespace CaseMesh.Ingestion.Tests;

public sealed class ExternalProcessTests
{
    [Fact]
    public async Task Caller_cancellation_terminates_the_child_process()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"casemesh-process-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var started = Path.Combine(directory, "started");
        var completed = Path.Combine(directory, "completed");
        try
        {
            var (executable, arguments) = Command(started, completed);
            using var cancellation = new CancellationTokenSource();
            var run = ExternalProcess.RunAsync(executable, arguments, TimeSpan.FromSeconds(10), cancellation.Token);
            await WaitForFileAsync(started, TimeSpan.FromSeconds(5));

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            await Task.Delay(TimeSpan.FromMilliseconds(1_250));
            Assert.False(File.Exists(completed));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static (string Executable, IReadOnlyList<string> Arguments) Command(
        string started,
        string completed)
    {
        if (OperatingSystem.IsWindows())
        {
            var script = $"[IO.File]::WriteAllText('{EscapePowerShell(started)}','started'); " +
                         $"Start-Sleep -Milliseconds 900; [IO.File]::WriteAllText('{EscapePowerShell(completed)}','completed')";
            return ("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script]);
        }

        return ("/bin/sh", ["-c", $"touch '{EscapeShell(started)}'; sleep 0.9; touch '{EscapeShell(completed)}'"]);
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var startedAt = DateTime.UtcNow;
        while (!File.Exists(path))
        {
            if (DateTime.UtcNow - startedAt > timeout) throw new TimeoutException("Synthetic child did not start.");
            await Task.Delay(25);
        }
    }

    private static string EscapePowerShell(string value) => value.Replace("'", "''", StringComparison.Ordinal);
    private static string EscapeShell(string value) => value.Replace("'", "'\"'\"'", StringComparison.Ordinal);
}
