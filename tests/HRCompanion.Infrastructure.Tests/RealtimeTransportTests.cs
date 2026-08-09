using HRCompanion.Core.Abstractions;
using HRCompanion.Core.Models;
using HRCompanion.Infrastructure.OpenAI;
using Microsoft.Extensions.Options;

namespace HRCompanion.Infrastructure.Tests;

public sealed class RealtimeTransportTests
{
    [Fact]
    public async Task SlowSender_UsesFixedDepthDropsOldestAndRunsOneSendAtATime()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var concurrent = 0;
        var maximumConcurrent = 0;
        await using var pump = new AudioFrameSendPump(3, async (_, cancellationToken) =>
        {
            var active = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref maximumConcurrent, active);
            firstStarted.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            Interlocked.Decrement(ref concurrent);
            return true;
        });
        pump.Start();

        Assert.True(pump.TryEnqueue(Frame(0)));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (var index = 1; index <= 10; index++) Assert.True(pump.TryEnqueue(Frame(index)));

        var saturated = pump.Diagnostics;
        Assert.Equal(11, saturated.FramesAccepted);
        Assert.Equal(7, saturated.FramesDropped);
        Assert.Equal(3, saturated.QueueDepth);
        Assert.Equal(3, saturated.QueueHighWaterMark);
        Assert.True(saturated.HasTranscriptionGap);

        release.TrySetResult();
        await WaitForAsync(() => pump.Diagnostics.FramesSent == 4);
        await pump.StopAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, maximumConcurrent);
        Assert.Equal(0, pump.Diagnostics.QueueDepth);
        Assert.Equal(4, pump.Diagnostics.FramesSent);
    }

    [Fact]
    public async Task Stop_CancelsAStuckSenderWithinBound()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pump = new AudioFrameSendPump(2, async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return true;
        });
        pump.Start();
        pump.TryEnqueue(Frame(0));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stop = pump.StopAsync(TimeSpan.FromMilliseconds(50));
        await stop.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ReconnectBudget_ResetsAfterEachSuccessfulOutageRecovery()
    {
        var budget = new ReconnectRetryBudget(3);
        Assert.True(budget.TryUseAttempt());
        Assert.True(budget.TryUseAttempt());
        Assert.True(budget.TryUseAttempt());
        Assert.False(budget.TryUseAttempt());

        budget.Reset();

        Assert.True(budget.TryUseAttempt());
        Assert.True(budget.TryUseAttempt());
        Assert.True(budget.TryUseAttempt());
        Assert.False(budget.TryUseAttempt());
    }

    [Fact]
    public void SessionUpdate_UsesCurrentDedicatedLiveTranscriptionShape()
    {
        var transcriber = new OpenAiRealtimeTranscriber(
            SpeakerRole.Hr,
            new EmptyKeyStore(),
            Options.Create(new OpenAiOptions()));
        var json = System.Text.Json.JsonSerializer.Serialize(transcriber.CreateSessionUpdate());
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var input = document.RootElement
            .GetProperty("session").GetProperty("audio").GetProperty("input");
        var transcription = input.GetProperty("transcription");

        Assert.Equal("gpt-live-transcribe", transcription.GetProperty("model").GetString());
        Assert.Equal("en", transcription.GetProperty("languages")[0].GetString());
        Assert.False(transcription.TryGetProperty("language", out _));
        Assert.Equal("low", transcription.GetProperty("delay").GetString());
        Assert.Contains(transcription.GetProperty("keywords").EnumerateArray(),
            item => item.GetString() == "Occupational Health");
        Assert.False(input.TryGetProperty("turn_detection", out _));
    }

    [Fact]
    public void WebSocketEndpoint_UsesDedicatedConnectionModelRatherThanTranscriptionModel()
    {
        var transcriber = new OpenAiRealtimeTranscriber(
            SpeakerRole.Hr,
            new EmptyKeyStore(),
            Options.Create(new OpenAiOptions()));

        Assert.Equal(
            "wss://api.openai.com/v1/realtime?model=gpt-realtime-2.1",
            transcriber.CreateWebSocketUri().AbsoluteUri);
    }

    private static AudioFrame Frame(int value) => new(new byte[] { (byte)value, 0 }, DateTimeOffset.UtcNow);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current) return;
        } while (Interlocked.CompareExchange(ref target, value, current) != current);
    }

    private sealed class EmptyKeyStore : IApiKeyStore
    {
        public Task<string?> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task SaveAsync(string apiKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}