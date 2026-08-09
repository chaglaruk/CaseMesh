using HRCompanion.Core.Abstractions;
using HRCompanion.Core.Models;
using HRCompanion.Core.Services;

namespace HRCompanion.Core.Tests;

public sealed class MeetingAssistantOrchestratorTests
{
    [Fact]
    public async Task HighRiskInformationalTurn_StillGeneratesAssistance()
    {
        var repository = new FakeRepository();
        var ai = new FakeAi();
        var orchestrator = new MeetingAssistantOrchestrator(repository, ai, new DeterministicCueEngine());
        var meeting = new MeetingState(Guid.NewGuid(), "Synthetic", DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var turn = TranscriptTurn.Final(
            meeting.MeetingId,
            SpeakerRole.Hr,
            "We're now moving to a capability process.",
            now,
            now,
            "synthetic");

        var response = await orchestrator.AcceptFinalTurnAsync(meeting, turn);

        Assert.Single(repository.SavedTurns);
        Assert.Equal(0, ai.AnalysisCalls);
        Assert.Equal(1, ai.ResponseCalls);
        Assert.Equal(AssistantImportance.High, response.Importance);
        Assert.NotNull(response.Watch);
    }

    [Fact]
    public async Task PersistenceFailure_DoesNotMutateMeetingState()
    {
        var repository = new FakeRepository { Failure = new IOException("synthetic database failure") };
        var orchestrator = new MeetingAssistantOrchestrator(repository, new FakeAi(), new DeterministicCueEngine());
        var meeting = new MeetingState(Guid.NewGuid(), "Synthetic", DateTimeOffset.UtcNow);
        var turn = TranscriptTurn.Final(
            meeting.MeetingId,
            SpeakerRole.Hr,
            "Can you confirm the synthetic proposal?",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "synthetic");

        await Assert.ThrowsAsync<IOException>(() => orchestrator.PersistFinalTurnAsync(meeting, turn));

        Assert.Empty(meeting.Turns);
        Assert.Empty(repository.SavedTurns);
    }

    [Fact]
    public async Task NonFinalTurn_IsRejectedBeforeRepositoryAccess()
    {
        var repository = new FakeRepository();
        var orchestrator = new MeetingAssistantOrchestrator(repository, new FakeAi(), new DeterministicCueEngine());
        var meeting = new MeetingState(Guid.NewGuid(), "Synthetic", DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var turn = new TranscriptTurn(
            Guid.NewGuid(), meeting.MeetingId, SpeakerRole.Hr, "Synthetic partial", now, now, false, "synthetic");

        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.PersistFinalTurnAsync(meeting, turn));

        Assert.Empty(repository.SavedTurns);
        Assert.Empty(meeting.Turns);
    }

    [Fact]
    public async Task WrongMeetingTurn_IsRejectedBeforeRepositoryAccess()
    {
        var repository = new FakeRepository();
        var orchestrator = new MeetingAssistantOrchestrator(repository, new FakeAi(), new DeterministicCueEngine());
        var meeting = new MeetingState(Guid.NewGuid(), "Synthetic", DateTimeOffset.UtcNow);
        var turn = TranscriptTurn.Final(
            Guid.NewGuid(), SpeakerRole.Hr, "Synthetic final", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "synthetic");

        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.PersistFinalTurnAsync(meeting, turn));

        Assert.Empty(repository.SavedTurns);
        Assert.Empty(meeting.Turns);
    }

    private sealed class FakeAi : IMeetingAiService
    {
        public int AnalysisCalls { get; private set; }
        public int ResponseCalls { get; private set; }

        public Task<MeetingAnalysis> AnalyzeTurnAsync(
            MeetingState state,
            TranscriptTurn latestHrTurn,
            CancellationToken cancellationToken = default)
        {
            AnalysisCalls++;
            return Task.FromResult(new MeetingAnalysis(
                MeetingIntent.Information,
                AssistantImportance.High,
                false,
                true,
                false,
                ["capability"]));
        }

        public Task<AssistantResponse> CreateAssistantResponseAsync(
            MeetingState state,
            TranscriptTurn latestHrTurn,
            MeetingAnalysis analysis,
            IReadOnlyList<CaseFact> facts,
            IReadOnlyList<EvidenceSnippet> evidence,
            CancellationToken cancellationToken = default)
        {
            ResponseCalls++;
            return Task.FromResult(new AssistantResponse(
                analysis.Intent,
                AssistantImportance.High,
                null,
                "Treat this as a significant process change; avoid agreeing to assumptions that have not been clarified.",
                "Could you explain what process you are proposing and what happens next?",
                true,
                0.9,
                []));
        }
    }

    private sealed class FakeRepository : ICaseRepository
    {
        public List<TranscriptTurn> SavedTurns { get; } = [];
        public Exception? Failure { get; init; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> HasDocumentHashAsync(string sha256, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task SaveDocumentAsync(DocumentRecord document, IReadOnlyList<DocumentChunk> chunks, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<DocumentRecord>> GetDocumentsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DocumentRecord>>([]);
        public Task<IReadOnlyList<EvidenceSnippet>> SearchAsync(string query, int limit = 8, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<EvidenceSnippet>>([]);
        public Task<IReadOnlyList<CaseFact>> GetFactsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CaseFact>>([]);
        public Task SaveFactAsync(CaseFact fact, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<TranscriptPersistenceResult> SaveTranscriptTurnAsync(TranscriptTurn turn, CancellationToken cancellationToken = default)
        {
            if (Failure is not null) return Task.FromException<TranscriptPersistenceResult>(Failure);
            SavedTurns.Add(turn);
            return Task.FromResult(TranscriptPersistenceResult.Inserted(DateTimeOffset.UtcNow));
        }

        public Task<IReadOnlyList<TranscriptTurn>> GetMeetingTurnsAsync(Guid meetingId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TranscriptTurn>>([]);
        public Task StartMeetingAsync(MeetingState meeting, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CompleteMeetingAsync(Guid meetingId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<MeetingState?> GetUnfinishedMeetingAsync(CancellationToken cancellationToken = default) => Task.FromResult<MeetingState?>(null);
    }
}
