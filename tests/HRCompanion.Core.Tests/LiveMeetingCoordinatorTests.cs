using System.Collections.Concurrent;
using HRCompanion.Core.Abstractions;
using HRCompanion.Core.Models;
using HRCompanion.Core.Services;

namespace HRCompanion.Core.Tests;

public sealed class LiveMeetingCoordinatorTests
{
    [Fact]
    public async Task NewHrFinalTurn_CancelsOldAnswer_PublishesLatest_AndPersistsBoth()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        var published = new ConcurrentQueue<AssistantResponse>();
        var latestPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.AssistantUpdated += (_, response) =>
        {
            published.Enqueue(response);
            if (response.Say == "Answer B") latestPublished.TrySetResult();
        };

        await coordinator.StartAsync();
        fixture.HrTranscriber.EmitFinal("Can you confirm the first return date?", "A");
        await fixture.Ai.AnswerAStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        fixture.HrTranscriber.EmitFinal("Can you confirm the second return date?", "B");
        await fixture.Ai.AnswerACancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await latestPublished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.StopAsync();

        Assert.Collection(published, response => Assert.Equal("Answer B", response.Say));
        Assert.Equal(
            ["Can you confirm the first return date?", "Can you confirm the second return date?"],
            fixture.Repository.SavedTurns.Select(turn => turn.Text));
        Assert.Equal(0, fixture.Ai.ActiveAnswerCalls);
        fixture.AssertStoppedCleanly();
    }

    [Fact]
    public async Task UserFinalSpeech_CancelsOldHrSuggestion_AndNothingStaleIsPublished()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        var published = new ConcurrentQueue<AssistantResponse>();
        coordinator.AssistantUpdated += (_, response) => published.Enqueue(response);

        await coordinator.StartAsync();
        fixture.HrTranscriber.EmitFinal("Can you confirm the first return date?", "A");
        await fixture.Ai.AnswerAStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        fixture.UserTranscriber.EmitFinal("I need to check that before I answer.", "USER");
        await fixture.Ai.AnswerACancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.StopAsync();

        Assert.Empty(published);
        Assert.Equal(2, fixture.Repository.SavedTurns.Count);
        Assert.Equal(SpeakerRole.Hr, fixture.Repository.SavedTurns[0].Speaker);
        Assert.Equal(SpeakerRole.User, fixture.Repository.SavedTurns[1].Speaker);
        Assert.Equal(0, fixture.Ai.ActiveAnswerCalls);
        fixture.AssertStoppedCleanly();
    }

    [Fact]
    public async Task CurrentGenerationTimeout_IsReportedWithoutPublishing_AndStopsCleanly()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator(TimeSpan.FromMilliseconds(50));
        var timeoutReported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new ConcurrentQueue<AssistantResponse>();
        coordinator.NonFatalError += (_, exception) =>
        {
            if (exception is TimeoutException) timeoutReported.TrySetResult(exception);
        };
        coordinator.AssistantUpdated += (_, response) => published.Enqueue(response);

        await coordinator.StartAsync();
        fixture.HrTranscriber.EmitFinal("Can you confirm the first return date?", "A");
        await fixture.Ai.AnswerAStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var timeout = await timeoutReported.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.StopAsync();

        Assert.Contains("latency budget", timeout.Message, StringComparison.Ordinal);
        Assert.Empty(published);
        Assert.Single(fixture.Repository.SavedTurns);
        Assert.True(fixture.Ai.AnswerACancelled.Task.IsCompletedSuccessfully);
        Assert.Equal(0, fixture.Ai.ActiveAnswerCalls);
        fixture.AssertStoppedCleanly();
    }

    private sealed class CoordinatorFixture
    {
        public FakeAudioSource HrAudio { get; } = new(SpeakerRole.Hr);
        public FakeAudioSource UserAudio { get; } = new(SpeakerRole.User);
        public FakeTranscriber HrTranscriber { get; } = new(SpeakerRole.Hr);
        public FakeTranscriber UserTranscriber { get; } = new(SpeakerRole.User);
        public FakeRepository Repository { get; } = new();
        public BlockingAiService Ai { get; } = new();

        public LiveMeetingCoordinator CreateCoordinator(TimeSpan? budget = null)
        {
            var state = new MeetingState(Guid.NewGuid(), "Synthetic", DateTimeOffset.UtcNow);
            var orchestrator = new MeetingAssistantOrchestrator(Repository, Ai, new DeterministicCueEngine());
            return budget is null
                ? new(state, orchestrator, HrAudio, UserAudio, HrTranscriber, UserTranscriber)
                : new(state, orchestrator, HrAudio, UserAudio, HrTranscriber, UserTranscriber, budget.Value);
        }

        public void AssertStoppedCleanly()
        {
            Assert.Equal(1, HrAudio.StopCalls);
            Assert.Equal(1, UserAudio.StopCalls);
            Assert.Equal(1, HrTranscriber.StopCalls);
            Assert.Equal(1, UserTranscriber.StopCalls);
        }
    }

    private sealed class FakeAudioSource(SpeakerRole speaker) : IAudioCaptureSource
    {
        public SpeakerRole Speaker { get; } = speaker;
        public string DisplayName => $"Fake {Speaker}";
        public int StopCalls { get; private set; }
        public event EventHandler<AudioFrame>? FrameReady;
        public event EventHandler<Exception>? CaptureFailed;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCalls++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void EmitFrame(AudioFrame frame) => FrameReady?.Invoke(this, frame);
        public void Fail(Exception exception) => CaptureFailed?.Invoke(this, exception);
    }

    private sealed class FakeTranscriber(SpeakerRole speaker) : IRealtimeTranscriber
    {
        public SpeakerRole Speaker { get; } = speaker;
        public int StopCalls { get; private set; }
        public event EventHandler<TranscriptionUpdate>? Updated;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public ValueTask SendAsync(AudioFrame frame, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCalls++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void EmitFinal(string text, string itemId) =>
            Updated?.Invoke(this, new TranscriptionUpdate(text, true, DateTimeOffset.UtcNow, itemId));
    }

    private sealed class BlockingAiService : IMeetingAiService
    {
        private int _activeAnswerCalls;
        public int ActiveAnswerCalls => Volatile.Read(ref _activeAnswerCalls);
        public TaskCompletionSource AnswerAStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AnswerACancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<MeetingAnalysis> AnalyzeTurnAsync(
            MeetingState state,
            TranscriptTurn latestHrTurn,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MeetingAnalysis(MeetingIntent.Question, AssistantImportance.Normal, true, false, false, []));

        public async Task<AssistantResponse> CreateAssistantResponseAsync(
            MeetingState state,
            TranscriptTurn latestHrTurn,
            MeetingAnalysis analysis,
            IReadOnlyList<CaseFact> facts,
            IReadOnlyList<EvidenceSnippet> evidence,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _activeAnswerCalls);
            try
            {
                if (latestHrTurn.Text.Contains("first", StringComparison.OrdinalIgnoreCase))
                {
                    AnswerAStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        AnswerACancelled.TrySetResult();
                        throw;
                    }
                }

                return new(
                    analysis.Intent,
                    analysis.Importance,
                    "Answer B",
                    null,
                    null,
                    false,
                    1.0,
                    []);
            }
            finally
            {
                Interlocked.Decrement(ref _activeAnswerCalls);
            }
        }
    }

    private sealed class FakeRepository : ICaseRepository
    {
        private readonly object _sync = new();
        private readonly List<TranscriptTurn> _savedTurns = [];

        public IReadOnlyList<TranscriptTurn> SavedTurns
        {
            get
            {
                lock (_sync) return _savedTurns.ToArray();
            }
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> HasDocumentHashAsync(string sha256, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task SaveDocumentAsync(DocumentRecord document, IReadOnlyList<DocumentChunk> chunks, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<DocumentRecord>> GetDocumentsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DocumentRecord>>([]);
        public Task<IReadOnlyList<EvidenceSnippet>> SearchAsync(string query, int limit = 8, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<EvidenceSnippet>>([]);
        public Task<IReadOnlyList<CaseFact>> GetFactsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CaseFact>>([]);
        public Task SaveFactAsync(CaseFact fact, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveTranscriptTurnAsync(TranscriptTurn turn, CancellationToken cancellationToken = default)
        {
            lock (_sync) _savedTurns.Add(turn);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TranscriptTurn>> GetMeetingTurnsAsync(Guid meetingId, CancellationToken cancellationToken = default) =>
            Task.FromResult(SavedTurns);
    }
}
