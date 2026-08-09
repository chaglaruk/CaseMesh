using System.Collections.Concurrent;
using System.Diagnostics;
using HRCompanion.Core.Abstractions;
using HRCompanion.Core.Models;
using HRCompanion.Infrastructure.OpenAI;
using HRCompanion.Infrastructure.Security;
using Microsoft.Extensions.Options;
using NAudio.Wave;

var keyStore = new WindowsCredentialApiKeyStore();
if (string.IsNullOrWhiteSpace(await keyStore.GetAsync()))
{
    Console.WriteLine("BLOCKED: HRCompanion/OpenAI credential is absent. No API request was made.");
    return 2;
}
if (args.Contains("--credential-check", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine("Credential Manager preflight: OK. No API request was made.");
    return 0;
}
if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: HRCompanion.SmokeProbe <hr-24khz-mono.wav> <user-24khz-mono.wav>");
    return 64;
}

var options = Options.Create(new OpenAiOptions());
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
await using var hr = new OpenAiRealtimeTranscriber(SpeakerRole.Hr, keyStore, options);
await using var user = new OpenAiRealtimeTranscriber(SpeakerRole.User, keyStore, options);
var hrFinal = new TaskCompletionSource<TranscriptionUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
var userFinal = new TaskCompletionSource<TranscriptionUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
var protocolErrors = new ConcurrentQueue<string>();
hr.Updated += (_, update) => { if (update.IsFinal) hrFinal.TrySetResult(update); };
user.Updated += (_, update) => { if (update.IsFinal) userFinal.TrySetResult(update); };
hr.Faulted += OnFault;
user.Faulted += OnFault;

try
{
    await Task.WhenAll(hr.StartAsync(), user.StartAsync());
    var realtime = Stopwatch.StartNew();
    await Task.WhenAll(StreamWaveAndCommitAsync(hr, args[0]), StreamWaveAndCommitAsync(user, args[1]));
    var finals = await Task.WhenAll(
        hrFinal.Task.WaitAsync(TimeSpan.FromSeconds(30)),
        userFinal.Task.WaitAsync(TimeSpan.FromSeconds(30)));
    realtime.Stop();

    var meeting = new MeetingState(Guid.NewGuid(), "Synthetic smoke", DateTimeOffset.UtcNow);
    var hrTurn = TranscriptTurn.Final(
        meeting.MeetingId,
        SpeakerRole.Hr,
        finals[0].Text,
        finals[0].StartedAt ?? finals[0].OccurredAt,
        finals[0].OccurredAt,
        "synthetic-smoke",
        finals[0].ItemId);
    meeting.AddTurn(hrTurn);
    var documentId = Guid.NewGuid();
    var evidence = new EvidenceSnippet(
        "synthetic-evidence-1",
        documentId,
        "synthetic-process-note.txt",
        "line 1",
        "The synthetic process note says no decision is required during this rehearsal.",
        1.0);
    var fact = new CaseFact(
        Guid.NewGuid(),
        "The synthetic participant wants time to consider any proposal.",
        FactStatus.UserPosition,
        null,
        "synthetic smoke",
        null,
        DateTimeOffset.UtcNow);
    var analysis = new MeetingAnalysis(
        MeetingIntent.CommitmentRequest,
        AssistantImportance.High,
        true,
        true,
        false,
        ["synthetic", "proposal"]);
    var ai = new OpenAiMeetingAiService(http, keyStore, options);
    var sol = Stopwatch.StartNew();
    var response = await ai.CreateAssistantResponseAsync(meeting, hrTurn, analysis, [fact], [evidence]);
    sol.Stop();

    var sayWords = response.Say?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length ?? 0;
    var sourceIdsValid = response.Sources.All(source => source.EvidenceId == evidence.EvidenceId);
    Console.WriteLine("Synthetic live smoke: PASS");
    Console.WriteLine($"HR final item ID present: {!string.IsNullOrWhiteSpace(finals[0].ItemId)}");
    Console.WriteLine($"USER final item ID present: {!string.IsNullOrWhiteSpace(finals[1].ItemId)}");
    Console.WriteLine($"Realtime dual-final diagnostic wall-clock ms: {realtime.Elapsed.TotalMilliseconds:F0}");
    Console.WriteLine($"Sol structured-response diagnostic wall-clock ms: {sol.Elapsed.TotalMilliseconds:F0}");
    Console.WriteLine("These smoke timings are connectivity diagnostics only; they are not Gate 5 end-to-end meeting latency samples and must not be reported as median/p95.");
    Console.WriteLine($"SAY word count: {sayWords}; source IDs valid: {sourceIdsValid}; protocol errors: {protocolErrors.Count}");
    return protocolErrors.IsEmpty && sourceIdsValid ? 0 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Synthetic live smoke failed: {ex.GetType().Name}.");
    if (ex is RealtimeProtocolException protocol)
        protocolErrors.Enqueue($"{protocol.EventType}/{protocol.Code ?? "none"}");
    foreach (var error in protocolErrors.Distinct(StringComparer.Ordinal))
        Console.Error.WriteLine($"Realtime error: {error}");
    return 1;
}
finally
{
    using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    try { await Task.WhenAll(hr.StopAsync(stopCts.Token), user.StopAsync(stopCts.Token)); } catch { }
}

void OnFault(object? sender, Exception error)
{
    protocolErrors.Enqueue(error is RealtimeProtocolException protocol
        ? $"{protocol.EventType}/{protocol.Code ?? "none"}"
        : error.GetType().Name);
}

static async Task StreamWaveAndCommitAsync(OpenAiRealtimeTranscriber transcriber, string path)
{
    using var wave = new WaveFileReader(path);
    if (wave.WaveFormat.SampleRate != 24000 || wave.WaveFormat.BitsPerSample != 16 || wave.WaveFormat.Channels != 1)
        throw new InvalidDataException("Smoke audio must be 24 kHz, 16-bit, mono PCM.");

    const int frameBytes = 4800;
    var buffer = new byte[frameBytes];
    int read;
    while ((read = wave.Read(buffer, 0, buffer.Length)) > 0)
    {
        if (!transcriber.TryEnqueue(new AudioFrame(buffer[..read].ToArray(), DateTimeOffset.UtcNow)))
            throw new InvalidOperationException("Realtime sender rejected a smoke frame.");
        await Task.Delay(TimeSpan.FromMilliseconds(read * 1000d / 2 / 24000));
    }

    using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (true)
    {
        var diagnostics = transcriber.Diagnostics;
        if (diagnostics.FramesDropped > 0)
            throw new InvalidOperationException("Realtime sender dropped a smoke frame before commit.");
        if (diagnostics.FramesAccepted > 0 &&
            diagnostics.FramesSent == diagnostics.FramesAccepted &&
            diagnostics.QueueDepth == 0)
            break;
        await Task.Delay(20, drainCts.Token);
    }

    await transcriber.CommitInputAudioBufferAsync(drainCts.Token);
}
