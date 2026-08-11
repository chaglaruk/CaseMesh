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
    public void ClientSecretRequest_HrUsesDigitalLoopbackVadWithoutNoiseReduction()
    {
        var transcriber = new OpenAiRealtimeTranscriber(
            SpeakerRole.Hr,
            new EmptyKeyStore(),
            Options.Create(new OpenAiOptions()));
        var json = System.Text.Json.JsonSerializer.Serialize(transcriber.CreateClientSecretRequest());
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        var session = root.GetProperty("session");
        var input = session.GetProperty("audio").GetProperty("input");
        var transcription = input.GetProperty("transcription");
        var turnDetection = input.GetProperty("turn_detection");

        Assert.Equal("created_at", root.GetProperty("expires_after").GetProperty("anchor").GetString());
        Assert.Equal(120, root.GetProperty("expires_after").GetProperty("seconds").GetInt32());
        Assert.Equal("transcription", session.GetProperty("type").GetString());
        Assert.Equal("audio/pcm", input.GetProperty("format").GetProperty("type").GetString());
        Assert.Equal(24000, input.GetProperty("format").GetProperty("rate").GetInt32());
        Assert.Equal("gpt-4o-mini-transcribe", transcription.GetProperty("model").GetString());
        Assert.Single(transcription.EnumerateObject());
        Assert.Equal("server_vad", turnDetection.GetProperty("type").GetString());
        Assert.Equal(0.5, turnDetection.GetProperty("threshold").GetDouble(), 3);
        Assert.Equal(300, turnDetection.GetProperty("prefix_padding_ms").GetInt32());
        Assert.Equal(500, turnDetection.GetProperty("silence_duration_ms").GetInt32());
        Assert.False(turnDetection.GetProperty("create_response").GetBoolean());
        Assert.False(turnDetection.GetProperty("interrupt_response").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, input.GetProperty("noise_reduction").ValueKind);
    }

    [Fact]
    public void ClientSecretRequest_UserUsesFarFieldNoiseReductionAndDefaultVadThreshold()
    {
        var transcriber = new OpenAiRealtimeTranscriber(
            SpeakerRole.User,
            new EmptyKeyStore(),
            Options.Create(new OpenAiOptions()));
        var json = System.Text.Json.JsonSerializer.Serialize(transcriber.CreateClientSecretRequest());
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var input = document.RootElement.GetProperty("session").GetProperty("audio").GetProperty("input");
        var turnDetection = input.GetProperty("turn_detection");
        var noiseReduction = input.GetProperty("noise_reduction");

        Assert.Equal("far_field", noiseReduction.GetProperty("type").GetString());
        Assert.Equal("server_vad", turnDetection.GetProperty("type").GetString());
        Assert.Equal(0.5, turnDetection.GetProperty("threshold").GetDouble(), 3);
        Assert.Equal(300, turnDetection.GetProperty("prefix_padding_ms").GetInt32());
        Assert.Equal(500, turnDetection.GetProperty("silence_duration_ms").GetInt32());
        Assert.False(turnDetection.GetProperty("create_response").GetBoolean());
        Assert.False(turnDetection.GetProperty("interrupt_response").GetBoolean());
    }

    [Fact]
    public void ConnectionEndpoints_DoNotBootstrapARealtimeVoiceModel()
    {
        var transcriber = new OpenAiRealtimeTranscriber(
            SpeakerRole.Hr,
            new EmptyKeyStore(),
            Options.Create(new OpenAiOptions()));

        Assert.Equal(
            "https://api.openai.com/v1/realtime/client_secrets",
            transcriber.CreateClientSecretUri().AbsoluteUri);
        Assert.Equal(
            "wss://api.openai.com/v1/realtime",
            transcriber.CreateWebSocketUri().AbsoluteUri);
        Assert.DoesNotContain("model=", transcriber.CreateWebSocketUri().Query, StringComparison.OrdinalIgnoreCase);
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