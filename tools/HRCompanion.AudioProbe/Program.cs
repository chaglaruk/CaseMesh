using HRCompanion.Audio.Windows;
using HRCompanion.Core.Abstractions;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Diagnostics;

Console.WriteLine("HR Companion Audio Probe");
Console.WriteLine("No audio is saved. The probe reports PCM frame activity only.");
Console.WriteLine();

var teams = TeamsProcessLocator.Find();
Console.WriteLine($"Teams candidate processes: {teams.Count}");
foreach (var process in teams)
{
    Console.WriteLine($"  {(process.IsLikelyRoot ? "ROOT?" : "child")}  {process.DisplayName}");
}

var microphones = MicrophoneCaptureSource.GetDevices();
Console.WriteLine($"Microphone devices: {microphones.Count}");
foreach (var device in microphones)
{
    Console.WriteLine($"  [{device.DeviceNumber}] {device.DisplayName}");
}

if (args.Contains("--list", StringComparer.OrdinalIgnoreCase)) return;

var selfTest = args.Contains("--self-test", StringComparer.OrdinalIgnoreCase);
var isolationSelfTest = args.Contains("--isolation-self-test", StringComparer.OrdinalIgnoreCase);
var discoverAudioSessions = args.Contains("--audio-sessions", StringComparer.OrdinalIgnoreCase);
var echoLeakageTest = args.Contains("--echo-leakage", StringComparer.OrdinalIgnoreCase);
var useGuardedSystem = echoLeakageTest || args.Contains("--guarded-system", StringComparer.OrdinalIgnoreCase);
var durationSeconds = ReadIntArgument("--seconds", selfTest || isolationSelfTest ? 5 : echoLeakageTest ? 30 : 15);
var microphoneNumber = ReadIntArgument("--microphone", 0);
var useSystemFallback = args.Contains("--system-fallback", StringComparer.OrdinalIgnoreCase);
var requestedProcessId = ReadNullableIntArgument("--teams");

if (discoverAudioSessions)
{
    await DiscoverRenderAudioSessionsAsync(durationSeconds);
    return;
}

if (echoLeakageTest && teams.Count == 0)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("Echo-leakage test requires an active Teams meeting on this PC.");
    Environment.ExitCode = 2;
    return;
}

var selectedProcess = requestedProcessId is null
    ? teams.FirstOrDefault()
    : teams.FirstOrDefault(process => process.ProcessId == requestedProcessId.Value);

if (!useSystemFallback && !useGuardedSystem && !selfTest && !isolationSelfTest && selectedProcess is null)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("No selected Teams process is running. Start Teams, then run:");
    Console.Error.WriteLine("  dotnet run --project .\\tools\\HRCompanion.AudioProbe\\HRCompanion.AudioProbe.csproj -- --teams <PID> --seconds 30");
    Environment.ExitCode = 2;
    return;
}

using var silentTarget = isolationSelfTest ? StartSilentTarget(durationSeconds + 5) : null;
await using IAudioCaptureSource microphone = new MicrophoneCaptureSource(microphoneNumber);
await using IAudioCaptureSource remote = useGuardedSystem
    ? new TeamsAwareSystemLoopbackCaptureSource()
    : useSystemFallback
        ? new SystemLoopbackCaptureSource()
        : new TeamsProcessLoopbackCaptureSource(
            selfTest ? Environment.ProcessId : isolationSelfTest ? silentTarget!.Id : selectedProcess!.ProcessId);

if (remote is TeamsAwareSystemLoopbackCaptureSource guarded)
{
    guarded.ContaminationChanged += (_, change) =>
    {
        if (change.IsBlocked)
        {
            Console.WriteLine($"AUDIO GUARD: BLOCKED by {change.ProcessName ?? "unknown"} PID={change.ProcessId} peak={change.Peak:F6}");
        }
        else
        {
            Console.WriteLine("AUDIO GUARD: RESUMED — no meaningful non-Teams render audio detected.");
        }
    };
}

long microphoneBytes = 0;
long remoteBytes = 0;
long microphonePeak = 0;
long remotePeak = 0;
long microphoneWindowPeak = 0;
long remoteWindowPeak = 0;
Exception? captureFailure = null;
microphone.FrameReady += (_, frame) =>
{
    Interlocked.Add(ref microphoneBytes, frame.Pcm16Bit24KhzMono.Length);
    var span = frame.Pcm16Bit24KhzMono.Span;
    for (var index = 0; index + 1 < span.Length; index += 2)
    {
        var sample = Math.Abs((int)(short)(span[index] | span[index + 1] << 8));
        InterlockedMax(ref microphonePeak, sample);
        InterlockedMax(ref microphoneWindowPeak, sample);
    }
};
remote.FrameReady += (_, frame) =>
{
    Interlocked.Add(ref remoteBytes, frame.Pcm16Bit24KhzMono.Length);
    var span = frame.Pcm16Bit24KhzMono.Span;
    for (var index = 0; index + 1 < span.Length; index += 2)
    {
        var sample = Math.Abs((int)(short)(span[index] | span[index + 1] << 8));
        InterlockedMax(ref remotePeak, sample);
        InterlockedMax(ref remoteWindowPeak, sample);
    }
};
microphone.Faulted += (_, error) => captureFailure ??= error;
remote.Faulted += (_, error) => captureFailure ??= error;

Console.WriteLine();
Console.WriteLine($"Microphone/USER: {microphone.DisplayName}");
Console.WriteLine($"Remote/HR:       {remote.DisplayName}");
if (echoLeakageTest)
{
    Console.WriteLine("ECHO LEAKAGE MODE: keep the laptop microphone enabled; do not speak during the remote-only segment.");
    Console.WriteLine("Suggested 30s sequence: 0-10s silence, 10-20s remote phone speaks continuously, 20-30s you speak normally.");
    Console.WriteLine("Compare per-second USER_peak with HR_peak; remote-only speech should not create a speech-level USER peak.");
}
else
{
    Console.WriteLine(useGuardedSystem
        ? "GUARDED SYSTEM MODE: non-Teams render activity blocks HR frames to prevent misattribution."
        : useSystemFallback
            ? "DEGRADED: system loopback includes unrelated audio and cannot verify Gate 1."
            : "ISOLATED MODE: Windows includes only the selected process tree; verify this with unrelated audio playing.");
}
Console.WriteLine($"Capturing for {durationSeconds} seconds...");

try
{
    await microphone.StartAsync();
    await remote.StartAsync();
    using var tone = selfTest || isolationSelfTest ? StartSyntheticTone() : null;
    for (var second = 1; second <= durationSeconds && captureFailure is null; second++)
    {
        await Task.Delay(1000);
        var userSecondPeak = Interlocked.Exchange(ref microphoneWindowPeak, 0);
        var hrSecondPeak = Interlocked.Exchange(ref remoteWindowPeak, 0);
        Console.WriteLine(echoLeakageTest
            ? $"{second,3}s  USER_peak={userSecondPeak,5}  HR_peak={hrSecondPeak,5}  USER={Interlocked.Read(ref microphoneBytes) / 1024.0:F1} KiB  HR={Interlocked.Read(ref remoteBytes) / 1024.0:F1} KiB"
            : $"{second,3}s  USER={Interlocked.Read(ref microphoneBytes) / 1024.0:F1} KiB  HR={Interlocked.Read(ref remoteBytes) / 1024.0:F1} KiB");
    }
}
finally
{
    using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await microphone.StopAsync(stopCts.Token);
    await remote.StopAsync(stopCts.Token);
}

Console.WriteLine();
if (captureFailure is not null)
{
    Console.Error.WriteLine($"Capture failed: {captureFailure.GetType().Name}: {captureFailure.Message}");
    Environment.ExitCode = 1;
}
else
{
    var userPeak = Interlocked.Read(ref microphonePeak);
    var peak = Interlocked.Read(ref remotePeak);
    if (echoLeakageTest) Console.WriteLine($"Microphone/USER peak sample: {userPeak}");
    Console.WriteLine($"Remote peak sample: {peak}");
    if (selfTest && peak < 100)
    {
        Console.Error.WriteLine("Self-test failed: target-process tone was not present in process-loopback capture.");
        Environment.ExitCode = 1;
    }
    else if (isolationSelfTest && peak >= 100)
    {
        Console.Error.WriteLine("Isolation self-test failed: unrelated parent-process tone appeared in silent target capture.");
        Environment.ExitCode = 1;
    }
    else
    {
        Console.WriteLine("Probe completed. Perform the Teams/headset checklist in docs/GATES.md before marking Gate 1 verified.");
    }
}

async Task DiscoverRenderAudioSessionsAsync(int seconds)
{
    Console.WriteLine();
    Console.WriteLine("AUDIO SESSION DISCOVERY: polling all active Windows render endpoints.");
    Console.WriteLine("Speak from the remote Teams participant during this window. No audio is captured or saved.");
    Console.WriteLine($"Polling for {seconds} seconds...");

    var observed = new Dictionary<string, SessionObservation>(StringComparer.Ordinal);
    using var enumerator = new MMDeviceEnumerator();

    for (var tick = 0; tick < seconds * 4; tick++)
    {
        var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        for (var endpointIndex = 0; endpointIndex < endpoints.Count; endpointIndex++)
        {
            using var endpoint = endpoints[endpointIndex];
            try
            {
                endpoint.AudioSessionManager.RefreshSessions();
                var sessions = endpoint.AudioSessionManager.Sessions;
                for (var sessionIndex = 0; sessionIndex < sessions.Count; sessionIndex++)
                {
                    using var session = sessions[sessionIndex];
                    uint pid;
                    float peak;
                    try
                    {
                        pid = session.GetProcessID;
                        peak = session.AudioMeterInformation?.MasterPeakValue ?? 0f;
                    }
                    catch
                    {
                        continue;
                    }

                    var processName = ResolveProcessName(pid);
                    var key = $"{endpoint.ID}|{pid}|{processName}";
                    if (!observed.TryGetValue(key, out var observation))
                    {
                        observation = new SessionObservation(endpoint.FriendlyName, pid, processName, 0f);
                    }

                    if (peak > observation.MaxPeak)
                    {
                        observed[key] = observation with { MaxPeak = peak };
                    }
                    else if (!observed.ContainsKey(key))
                    {
                        observed[key] = observation;
                    }
                }
            }
            catch
            {
                // Endpoint/session may disappear while Teams changes devices; retry on the next poll.
            }
        }

        await Task.Delay(250);
        if ((tick + 1) % 4 == 0)
        {
            var elapsed = (tick + 1) / 4;
            var loudest = observed.Values.OrderByDescending(item => item.MaxPeak).FirstOrDefault();
            Console.WriteLine(loudest is null
                ? $"{elapsed,3}s  no render sessions observed"
                : $"{elapsed,3}s  loudest={loudest.ProcessName} PID={loudest.ProcessId} peak={loudest.MaxPeak:F6}");
        }
    }

    Console.WriteLine();
    Console.WriteLine("Observed render sessions ranked by maximum peak:");
    var ranked = observed.Values
        .OrderByDescending(item => item.MaxPeak)
        .ThenBy(item => item.ProcessName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => item.ProcessId)
        .ToArray();

    if (ranked.Length == 0)
    {
        Console.WriteLine("  <none>");
        return;
    }

    foreach (var item in ranked)
    {
        Console.WriteLine($"  peak={item.MaxPeak:F6}  PID={item.ProcessId,-7} process={item.ProcessName}  endpoint={item.EndpointName}");
    }

    Console.WriteLine();
    Console.WriteLine("Use the PID with a clear peak while the remote Teams participant is speaking as the next process-loopback candidate.");
}

string ResolveProcessName(uint processId)
{
    if (processId == 0) return "System Sounds";
    try
    {
        using var process = Process.GetProcessById(checked((int)processId));
        return process.ProcessName;
    }
    catch
    {
        return "<exited-or-unavailable>";
    }
}

int ReadIntArgument(string name, int fallback) => ReadNullableIntArgument(name) ?? fallback;

int? ReadNullableIntArgument(string name)
{
    var index = Array.FindIndex(args, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
    if (index < 0) return null;
    if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out var value) || value < 0)
    {
        throw new ArgumentException($"{name} requires a non-negative integer value.");
    }
    return value;
}

WaveOutEvent StartSyntheticTone()
{
    var tone = new SignalGenerator(44100, 1)
    {
        Gain = 0.08,
        Frequency = 440,
        Type = SignalGeneratorType.Sin
    };
    var output = new WaveOutEvent { DesiredLatency = 80 };
    output.Init(tone);
    output.Play();
    Console.WriteLine("Synthetic self-test tone is playing from the target process.");
    return output;
}

Process StartSilentTarget(int seconds)
{
    var process = Process.Start(new ProcessStartInfo
    {
        FileName = "powershell.exe",
        Arguments = $"-NoProfile -NonInteractive -Command Start-Sleep -Seconds {seconds}",
        UseShellExecute = false,
        CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden
    });
    return process ?? throw new InvalidOperationException("Could not start the silent isolation target process.");
}

void InterlockedMax(ref long target, long value)
{
    long current;
    do
    {
        current = Interlocked.Read(ref target);
        if (value <= current) return;
    } while (Interlocked.CompareExchange(ref target, value, current) != current);
}

internal sealed record SessionObservation(string EndpointName, uint ProcessId, string ProcessName, float MaxPeak);
