using HRCompanion.Core.Abstractions;
using HRCompanion.Core.Models;
using HRCompanion.Core.Services;

namespace HRCompanion.Core.Tests;

public sealed class MeetingAssistantOrchestratorTests
{
    [Fact]
    public async Task CommitmentSensitiveTurn_SkipsOptionalAiAnalysis()
    {
        var repository = new EmptyRepository();
        var ai = new RecordingAiService();
        var sut = new MeetingAssistantOrchestrator(repository, ai, new DeterministicCueEngine());
        var meetingId = Guid.NewGuid();
        var state = new MeetingState(meetingId, "Synthetic", DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var turn = TranscriptTurn.Final(
            meetingId,
            SpeakerRole.Hr,
            "So you're saying you are refusing to return to work?",
            now,
            now,
            "synthetic");
        state.AddTurn(turn);

        await sut.CreateAssistanceForRecordedTurnAsync(state, turn);

        Assert.Equal(0, ai.AnalysisCalls);
        Assert.Equal(1, ai.AnswerCalls);
        Assert.Equal(MeetingIntent.CommitmentRequest, ai.LastAnswerAnalysis?.Intent);
        Assert.True(ai.LastAnswerAnalysis?.PotentialCommitment);
    }

    [Fact]
    public async Task OptionalAnalysisTimeout_FallsBackToDeterministicAnswerPath()
    {
        var repository = new EmptyRepository();
        var ai = new RecordingAiService { BlockAnalysisUntilCancelled = true };
        var sut = new MeetingAssistantOrchestrator(repository, ai, new DeterministicCueEngine());
        var meetingId = Guid.NewGuid();
        var state = new MeetingState(meetingId, "Synthetic", DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var turn = TranscriptTurn.Final(
            meetingId,
            SpeakerRole.Hr,
            "Are you comfortable with that?",
            now,
            now,
            "synthetic");
        state.AddTurn(turn);

        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await sut.CreateAssistanceForRecordedTurnAsync(state, turn, overall.Token);

        Assert.Equal(1, ai.AnalysisCalls);
        Assert.Equal(1, ai.AnswerCalls);
        Assert.Equal(MeetingIntent.Question, ai.LastAnswerAnalysis?.Intent);
        Assert.False(overall.IsCancellationRequested);
    }

    private sealed class RecordingAiService : IMeetingAiService
    {
        public int AnalysisCalls { get; private set; }
        public int AnswerCalls { get; private set; }
        public MeetingAnalysis? LastAnswerAnalysis { get; private set; }
        public bool BlockAnalysisUntilCancelled { get; init; }

        public async Task<MeetingAnalysis> AnalyzeTurnAsync(
            MeetingState state,
            TranscriptTurn latestHrTurn,
            CancellationToken cancellationToken = default)
        {
            AnalysisCalls++;
            if (BlockAnalysisUntilCancelled)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new(MeetingIntent.Question, AssistantImportance.Normal, true, false, false, ["analysis"]);
        }

        public Task<AssistantResponse> CreateAssistantResponseAsync(
            MeetingState state,
            TranscriptTurn latestHrTurn,
            MeetingAnalysis analysis,
            IReadOnlyList<CaseFact> facts,
            IReadOnlyList<EvidenceSnippet> evidence,
            CancellationToken cancellationToken = default)
        {
            AnswerCalls++;
            LastAnswerAnalysis = analysis;
            return Task.FromResult(AssistantResponse.NoAction(analysis.Intent));
        }
    }

    private sealed class EmptyRepository : ICaseRepository
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> HasDocumentHashAsync(string sha256, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task SaveDocumentAsync(DocumentRecord document, IReadOnlyList<DocumentChunk> chunks, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<DocumentRecord>> GetDocumentsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DocumentRecord>>([]);
        public Task<IReadOnlyList<EvidenceSnippet>> SearchAsync(string query, int limit = 8, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<EvidenceSnippet>>([]);
        public Task<IReadOnlyList<CaseFact>> GetFactsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CaseFact>>([]);
        public Task SaveFactAsync(CaseFact fact, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveTranscriptTurnAsync(TranscriptTurn turn, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<TranscriptTurn>> GetMeetingTurnsAsync(Guid meetingId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TranscriptTurn>>([]);
    }
}
