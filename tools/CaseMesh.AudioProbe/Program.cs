using System.Buffers.Binary;
using CaseMesh.Audio.Windows;
using CaseMesh.Core.Abstractions;

Console.WriteLine("CaseMesh Audio Probe");
Console.WriteLine("No audio is saved. The probe reports PCM frame counts, bytes and RMS only.");
Console.WriteLine();

var requestedPid = ReadIntOption(args, "--pid");
var seconds = Math.Clamp(ReadIntOption(args, "--seconds") ?? 5, 1, 60);
var useSystemFallback = args.Contains("--system-fallback", StringComparer.OrdinalIgnoreCase);
var invalidOnly = args.Contains("--invalid-only", StringComparer.OrdinalIgnoreCase);
var teams = TeamsProcessLocator.Find();
Console.WriteLine($"Teams process trees: {teams.Count}");
foreach (var process in teams)
{
    Console.WriteLine($"  {process.DisplayLabel}");
}
Console.WriteLine();

if (invalidOnly)
{
    await VerifyInvalidPidFailsClearlyAsync();
    return 0;
}

if (!useSystemFallback && requestedPid is null)
{
    if (teams.Count == 1)
    {
        requestedPid = teams[0].ProcessId;
        Console.WriteLine($"Automatically selected Teams PID {requestedPid}.");
    }
    else
    {
        Console.Error.WriteLine(teams.Count == 0
            ? "No running Microsoft Teams process was found. Start Teams, then rerun this probe."
            : "Multiple Teams process trees are plausible. Rerun with --pid <PID> after choosing from the list above.");
        return 2;
    }
}

if (useSystemFallback)
{
    Console.WriteLine("EXPLICIT FALLBACK: capturing all default system output. This cannot verify Teams isolation or Gate 1.");
}
else
{
    await VerifyInvalidPidFailsClearlyAsync();
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

await using IAudioCaptureSource remote = useSystemFallback
    ? new SystemLoopbackCaptureSource()
    : new TeamsProcessLoopbackCaptureSource(requestedPid!.Value);
await using IAudioCaptureSource microphone = new MicrophoneCaptureSource();
var remoteMetrics = new AudioMetrics();
var microphoneMetrics = new AudioMetrics();
remote.FrameReady += (_, frame) => remoteMetrics.Add(frame);
microphone.FrameReady += (_, frame) => microphoneMetrics.Add(frame);
remote.CaptureFailed += (_, exception) => Console.Error.WriteLine($"Remote capture failure: {exception.Message}");
microphone.CaptureFailed += (_, exception) => Console.Error.WriteLine($"Microphone capture failure: {exception.Message}");

Console.WriteLine($"Remote: {remote.DisplayName}");
Console.WriteLine($"User:   {microphone.DisplayName}");
Console.WriteLine("Cycle 1: Start -> measure -> repeated Stop.");
await RunCycleAsync(remote, microphone, remoteMetrics, microphoneMetrics, seconds, cancellation.Token);
await remote.StopAsync(cancellation.Token);
await remote.StopAsync(cancellation.Token);
await microphone.StopAsync(cancellation.Token);
await microphone.StopAsync(cancellation.Token);

Console.WriteLine();
Console.WriteLine("Cycle 2: restart the same sources, then dispose while active.");
remoteMetrics.Reset();
microphoneMetrics.Reset();
await RunCycleAsync(remote, microphone, remoteMetrics, microphoneMetrics, seconds, cancellation.Token);
await remote.DisposeAsync();
await remote.DisposeAsync();
await microphone.DisposeAsync();
await microphone.DisposeAsync();

Console.WriteLine();
Console.WriteLine("Lifecycle probe completed without storing raw audio.");
if (!useSystemFallback)
{
    Console.WriteLine("A non-zero Teams RMS proves frames arrived, but non-Teams exclusion still requires controlled playback testing.");
}
return 0;

static async Task RunCycleAsync(
    IAudioCaptureSource remote,
    IAudioCaptureSource microphone,
    AudioMetrics remoteMetrics,
    AudioMetrics microphoneMetrics,
    int seconds,
    CancellationToken cancellationToken)
{
    await remote.StartAsync(cancellationToken);
    await microphone.StartAsync(cancellationToken);
    for (var second = 1; second <= seconds; second++)
    {
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        Console.WriteLine(
            $"{second,2}s  remote={PacketCount(remote),5} packets/{remoteMetrics.FrameCount,5} frames {remoteMetrics.Kibibytes,8:F1} KiB RMS={remoteMetrics.Rms,7:F1}" +
            $"  user={PacketCount(microphone),5} packets/{microphoneMetrics.FrameCount,5} frames {microphoneMetrics.Kibibytes,8:F1} KiB RMS={microphoneMetrics.Rms,7:F1}");
    }
}

static long PacketCount(IAudioCaptureSource source) => source switch
{
    TeamsProcessLoopbackCaptureSource teams => teams.CapturedPacketCount,
    SystemLoopbackCaptureSource system => system.CapturedPacketCount,
    MicrophoneCaptureSource microphone => microphone.CapturedPacketCount,
    _ => 0
};

static async Task VerifyInvalidPidFailsClearlyAsync()
{
    await using var invalid = new TeamsProcessLoopbackCaptureSource(int.MaxValue);
    try
    {
        await invalid.StartAsync();
        throw new InvalidOperationException("Invalid PID probe unexpectedly started capture.");
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"Invalid PID check: PASS — {ex.Message}");
    }
}

static int? ReadIntOption(string[] arguments, string option)
{
    var index = Array.FindIndex(arguments, value => string.Equals(value, option, StringComparison.OrdinalIgnoreCase));
    if (index < 0) return null;
    if (index + 1 >= arguments.Length || !int.TryParse(arguments[index + 1], out var value))
        throw new ArgumentException($"{option} requires an integer value.");
    return value;
}

internal sealed class AudioMetrics
{
    private readonly object _sync = new();
    private long _frameCount;
    private long _bytes;
    private long _sampleCount;
    private double _sumSquares;

    public long FrameCount { get { lock (_sync) return _frameCount; } }
    public double Kibibytes { get { lock (_sync) return _bytes / 1024.0; } }
    public double Rms { get { lock (_sync) return _sampleCount == 0 ? 0 : Math.Sqrt(_sumSquares / _sampleCount); } }

    public void Add(AudioFrame frame)
    {
        var pcm = frame.Pcm16Bit24KhzMono.Span;
        double sumSquares = 0;
        var samples = pcm.Length / sizeof(short);
        for (var offset = 0; offset + 1 < pcm.Length; offset += sizeof(short))
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm[offset..]);
            sumSquares += (double)sample * sample;
        }

        lock (_sync)
        {
            _frameCount++;
            _bytes += pcm.Length;
            _sampleCount += samples;
            _sumSquares += sumSquares;
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _frameCount = 0;
            _bytes = 0;
            _sampleCount = 0;
            _sumSquares = 0;
        }
    }
}
