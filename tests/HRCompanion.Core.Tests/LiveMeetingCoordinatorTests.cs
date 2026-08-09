using System.Collections.Concurrent;
using System.Diagnostics;
using HRCompanion.Core.Abstractions;
using HRCompanion.Core.Models;
using HRCompanion.Core.Services;

namespace HRCompanion.Core.Tests;

public sealed class LiveMeetingCoordinatorTests
{
    [Fact]
    public async Task UserFinal_PersistsWhileHrAssistanceIsStillBlocked()
    {
        await using var harness = new Harness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Ai.Handler = async (_, _) =>
        {
            harness.Ai.Started.TrySetResult();
            await release.Task;
            return Response("old HR advice");
        };
        await harness.StartAsync();

        harness.Hr.EmitFinal("Can you confirm that today?", harness.Origin);
        await harness.Ai.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        harness.User.EmitFinal("I need to check that first.", harness.Origin.AddSeconds(1));

        await harness.Repository.WaitForSavedCountAsync(2);
        Assert.Contains(harness.State.Turns, turn => turn.Speaker == SpeakerRole.User);
        Assert.False(release.Task.IsCompleted);
        release.TrySetResult();
    }

    [Fact]
    public async Task UserSpeechStarted_InvalidatesBlockedAssistanceBeforeItCanRender()
    {
        await using var harness = new Harness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Ai.Handler = async (_, _) =>
        {
            harness.Ai.Started.TrySetResult();
            await release.Task;
            harness.Ai.Returned.TrySetResult();
            return Response("stale advice");
        };
        var rendered = new ConcurrentQueue<AssistantResponse>();
        harness.Coordinator.AssistantUpdated += (_, response) => rendered.Enqueue(response);
        await harness.StartAsync();

        harness.Hr.EmitFinal("Will you agree to that today?", harness.Origin);
        await harness.Ai.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        harness.User.EmitSpeechStarted(harness.Origin.AddSeconds(1));
        release.TrySetResult();
        await harness.Ai.Returned.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);

        Assert.Empty(rendered);
    }

    [Fact]
    public async Task UserSpeechStarted_CancelsTheInFlightAssistantToken()
    {
        await using var harness = new Harness();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Ai.Handler = async (_, cancellationToken) =>
        {
            harness.Ai.Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Response("unreachable");
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
        };
        await harness.StartAsync();
        harness.Hr.EmitFinal("Will you confirm that?", harness.Origin);
        await harness.Ai.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        harness.User.EmitSpeechStarted(harness.Origin.AddSeconds(1));

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task NewerHrTurn_SupersedesSlowerOlderResponse()
    {
        await using var harness = new Harness();
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Ai.Handler = async (turn, _) =>
        {
            if (turn.Text.Contains("first", StringComparison.OrdinalIgnoreCase))
            {
                startedA.TrySetResult();
                await releaseA.Task;
                return Response("response A");
            }
            return Response("response B");
        };
        var rendered = new ConcurrentQueue<string>();
        var renderedB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Coordinator.AssistantUpdated += (_, response) =>
        {
            rendered.Enqueue(response.Say!);
            if (response.Say == "response B") renderedB.TrySetResult();
        };
        await harness.StartAsync();

        harness.Hr.EmitFinal("What is your first answer?", harness.Origin);
        await startedA.Task.WaitAsync(TimeSpan.FromSeconds(2));
        harness.Hr.EmitFinal("What is your newer answer?", harness.Origin.AddSeconds(1));
        await renderedB.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseA.TrySetResult();
        await Task.Delay(50);

        Assert.Equal(["response B"], rendered);
    }

    [Fact]
    public async Task DelayedOlderHrFinal_PersistsChronologicallyWithoutObsoleteAssistance()
    {
        await using var harness = new Harness();
        await harness.StartAsync();

        harness.User.EmitFinal("The exchange has already moved on.", harness.Origin.AddSeconds(2));
        await harness.Repository.WaitForSavedCountAsync(1);
        harness.Hr.EmitFinal("Will you agree to the older proposal?", harness.Origin);
        await harness.Repository.WaitForSavedCountAsync(2);

        Assert.Equal(SpeakerRole.Hr, harness.State.Turns[0].Speaker);
        Assert.Equal(SpeakerRole.User, harness.State.Turns[1].Speaker);
        Assert.Equal(0, harness.Ai.ResponseCalls);
    }

    [Fact]
    public async Task StopDuringActiveAssistance_PreservesFinalAndNeverRendersLateResponse()
    {
        await using var harness = new Harness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Ai.Handler = async (_, _) =>
        {
            harness.Ai.Started.TrySetResult();
            await release.Task;
            harness.Ai.Returned.TrySetResult();
            return Response("too late");
        };
        var rendered = new ConcurrentQueue<AssistantResponse>();
        harness.Coordinator.AssistantUpdated += (_, response) => rendered.Enqueue(response);
        await harness.StartAsync();
        harness.Hr.EmitFinal("Can you confirm that?", harness.Origin);
        await harness.Ai.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await harness.Repository.WaitForSavedCountAsync(1);

        var stopwatch = Stopwatch.StartNew();
        await harness.Coordinator.StopAsync();
        stopwatch.Stop();
        release.TrySetResult();
        await harness.Ai.Returned.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);

        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        Assert.Single(harness.Repository.SavedTurns);
        Assert.Empty(rendered);
    }

    [Fact]
    public async Task HealthAggregation_RemainsSourceAwareAndGapOutranksListening()
    {
        await using var harness = new Harness();
        var states = new ConcurrentQueue<LiveMeetingHealth>();
        harness.Coordinator.HealthChanged += (_, health) => states.Enqueue(health);
        await harness.StartAsync();
        Assert.Equal(LiveMeetingHealthState.FullListening, states.Last().State);

        harness.Hr.SetState(TranscriberConnectionState.Reconnecting);
        harness.User.SetState(TranscriberConnectionState.Listening);
        Assert.Equal(LiveMeetingHealthState.HrReconnecting, states.Last().State);

        harness.Hr.SetState(TranscriberConnectionState.Listening);
        harness.User.SetState(TranscriberConnectionState.Reconnecting);
        Assert.Equal(LiveMeetingHealthState.UserReconnecting, states.Last().State);

        harness.User.SetState(TranscriberConnectionState.Listening);
        harness.Hr.MarkGap();
        Assert.Equal(LiveMeetingHealthState.TranscriptionGap, states.Last().State);
        Assert.True(states.Last().HasTranscriptionGap);
    }

    [Fact]
    public async Task AssistanceTimeout_DiscardsLateResultAndDoesNotDegradeTranscriptionHealth()
    {
        await using var harness = new Harness(TimeSpan.FromMilliseconds(50));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Ai.Handler = async (_, _) =>
        {
            harness.Ai.Started.TrySetResult();
            await release.Task;
            harness.Ai.Returned.TrySetResult();
            return Response("late timeout response");
        };
        var rendered = new ConcurrentQueue<AssistantResponse>();
        var health = new ConcurrentQueue<LiveMeetingHealth>();
        harness.Coordinator.AssistantUpdated += (_, response) => rendered.Enqueue(response);
        harness.Coordinator.HealthChanged += (_, value) => health.Enqueue(value);
        await harness.StartAsync();
        harness.Hr.EmitFinal("Can you confirm that?", harness.Origin);
        await harness.Ai.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await Task.Delay(100);
        release.TrySetResult();
        await harness.Ai.Returned.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForAsync(() => health.Any(item => item.State == LiveMeetingHealthState.AssistantDegraded));

        Assert.Empty(rendered);
        var degraded = health.Last(item => item.State == LiveMeetingHealthState.AssistantDegraded);
        Assert.Equal(TranscriberConnectionState.Listening, degraded.HrTranscriber);
        Assert.Equal(TranscriberConnectionState.Listening, degraded.UserTranscriber);
    }

    private static AssistantResponse Response(string say) => new(
        MeetingIntent.Question, AssistantImportance.Normal, say, null, null, false, 0.8, []);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(TimeSpan? assistanceTimeout = null)
        {
            State = new MeetingState(Guid.NewGuid(), "Synthetic", Origin);
            Repository = new FakeRepository();
            Ai = new ControlledAi();
            Hr = new FakeTranscriber(SpeakerRole.Hr);
            User = new FakeTranscriber(SpeakerRole.User);
            Coordinator = new LiveMeetingCoordinator(
                State,
                new MeetingAssistantOrchestrator(Repository, Ai, new DeterministicCueEngine()),
                new FakeAudio(SpeakerRole.Hr),
                new FakeAudio(SpeakerRole.User),
                Hr,
                User,
                assistanceTimeout ?? TimeSpan.FromSeconds(5));
        }

        public DateTimeOffset Origin { get; } = DateTimeOffset.Parse("2026-08-09T12:00:00Z");
        public MeetingState State { get; }
        public FakeRepository Repository { get; }
        public ControlledAi Ai { get; }
        public FakeTranscriber Hr { get; }
        public FakeTranscriber User { get; }
        public LiveMeetingCoordinator Coordinator { get; }

        public Task StartAsync() => Coordinator.StartAsync();
        public ValueTask DisposeAsync() => Coordinator.DisposeAsync();
    }

    private sealed class FakeAudio(SpeakerRole speaker) : IAudioCaptureSource
    {
        public SpeakerRole Speaker { get; } = speaker;
        public string DisplayName => "Synthetic audio";
        public event EventHandler<AudioFrame>? FrameReady { add { } remove { } }
        public event EventHandler<Exception>? Faulted { add { } remove { } }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTranscriber(SpeakerRole speaker) : IRealtimeTranscriber
    {
        private TranscriberDiagnostics _diagnostics = new(0, 0, 0, 0, 0, false);
        public SpeakerRole Speaker { get; } = speaker;
        public TranscriberDiagnostics Diagnostics => _diagnostics;
        public event EventHandler<TranscriptionUpdate>? Updated;
        public event EventHandler<TranscriberConnectionState>? StateChanged;
        public event EventHandler<TranscriberDiagnostics>? DiagnosticsChanged;
        public event EventHandler<Exception>? Faulted { add { } remove { } }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            SetState(TranscriberConnectionState.Listening);
            return Task.CompletedTask;
        }

        public bool TryEnqueue(AudioFrame frame) => true;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void EmitFinal(string text, DateTimeOffset startedAt) => Updated?.Invoke(this,
            new(text, true, startedAt.AddMilliseconds(500), Guid.NewGuid().ToString("N"), startedAt));

        public void EmitSpeechStarted(DateTimeOffset startedAt) => Updated?.Invoke(this,
            new(string.Empty, false, startedAt, Guid.NewGuid().ToString("N"), startedAt, null, true));

        public void SetState(TranscriberConnectionState state) => StateChanged?.Invoke(this, state);

        public void MarkGap()
        {
            _diagnostics = _diagnostics with { FramesDropped = 1, HasTranscriptionGap = true };
            DiagnosticsChanged?.Invoke(this, _diagnostics);
        }
    }

    private sealed class ControlledAi : IMeetingAiService
    {
        public Func<TranscriptTurn, CancellationToken, Task<AssistantResponse>> Handler { get; set; } =
            (_, _) => Task.FromResult(Response("synthetic response"));
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ResponseCalls;

        public Task<MeetingAnalysis> AnalyzeTurnAsync(MeetingState state, TranscriptTurn latestHrTurn, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MeetingAnalysis(MeetingIntent.Question, AssistantImportance.Normal, true, false, false, ["synthetic"]));

        public Task<AssistantResponse> CreateAssistantResponseAsync(
            MeetingState state,
            TranscriptTurn latestHrTurn,
            MeetingAnalysis analysis,
            IReadOnlyList<CaseFact> facts,
            IReadOnlyList<EvidenceSnippet> evidence,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ResponseCalls);
            return Handler(latestHrTurn, cancellationToken);
        }
    }

    private sealed class FakeRepository : ICaseRepository
    {
        private readonly object _sync = new();
        public List<TranscriptTurn> SavedTurns { get; } = [];
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> HasDocumentHashAsync(string sha256, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task SaveDocumentAsync(DocumentRecord document, IReadOnlyList<DocumentChunk> chunks, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<DocumentRecord>> GetDocumentsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DocumentRecord>>([]);
        public Task<IReadOnlyList<EvidenceSnippet>> SearchAsync(string query, int limit = 8, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<EvidenceSnippet>>([]);
        public Task<IReadOnlyList<CaseFact>> GetFactsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CaseFact>>([]);
        public Task SaveFactAsync(CaseFact fact, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<TranscriptTurn>> GetMeetingTurnsAsync(Guid meetingId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TranscriptTurn>>([]);
        public Task StartMeetingAsync(MeetingState meeting, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CompleteMeetingAsync(Guid meetingId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<MeetingState?> GetUnfinishedMeetingAsync(CancellationToken cancellationToken = default) => Task.FromResult<MeetingState?>(null);

        public Task SaveTranscriptTurnAsync(TranscriptTurn turn, CancellationToken cancellationToken = default)
        {
            lock (_sync) SavedTurns.Add(turn);
            return Task.CompletedTask;
        }

        public async Task WaitForSavedCountAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (true)
            {
                lock (_sync)
                {
                    if (SavedTurns.Count >= count) return;
                }
                await Task.Delay(10, timeout.Token);
            }
        }
    }
}
