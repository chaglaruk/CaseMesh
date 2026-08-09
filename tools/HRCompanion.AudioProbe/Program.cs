using HRCompanion.Audio.Windows;
using HRCompanion.Core.Abstractions;

Console.WriteLine("HR Companion Audio Probe");
Console.WriteLine("This probe does not save audio. It measures received PCM frame activity only.");
Console.WriteLine();

var teams = TeamsProcessLocator.Find();
Console.WriteLine($"Teams candidate processes: {teams.Count}");
foreach (var process in teams)
{
    Console.WriteLine($"  PID {process.ProcessId}: {process.ProcessName} {process.MainWindowTitle}");
}
Console.WriteLine();

await using IAudioCaptureSource mic = new MicrophoneCaptureSource();
await using IAudioCaptureSource loopback = new SystemLoopbackCaptureSource();
long micBytes = 0;
long remoteBytes = 0;
mic.FrameReady += (_, frame) => Interlocked.Add(ref micBytes, frame.Pcm16Bit24KhzMono.Length);
loopback.FrameReady += (_, frame) => Interlocked.Add(ref remoteBytes, frame.Pcm16Bit24KhzMono.Length);

Console.WriteLine($"Microphone: {mic.DisplayName}");
Console.WriteLine($"Remote fallback: {loopback.DisplayName}");
Console.WriteLine("Capturing activity for 10 seconds. Speak locally and play Teams/other output if available...");

await mic.StartAsync();
await loopback.StartAsync();
for (var second = 1; second <= 10; second++)
{
    await Task.Delay(1000);
    Console.WriteLine($"{second,2}s  mic={Interlocked.Read(ref micBytes) / 1024.0:F1} KiB  remote={Interlocked.Read(ref remoteBytes) / 1024.0:F1} KiB");
}
await mic.StopAsync();
await loopback.StopAsync();

Console.WriteLine();
Console.WriteLine("Result is diagnostic only. System loopback is not Teams-isolated and cannot verify Gate 1.");
