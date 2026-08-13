using HRCompanion.Core.Abstractions;
using HRCompanion.Core.Models;

namespace HRCompanion.Core.Services;

public sealed class MeetingAssistantOrchestrator
{
    private static readonly TimeSpan OptionalAnalysisBudget = TimeSpan.FromSeconds(1.5);

    private readonly ICaseRepository _repository;
    private readonly IMeetingAiService _ai;
    private readonly DeterministicCueEngine _cues;
    private readonly TimeSpan _optionalAnalysisBudget;

    public MeetingAssistantOrchestrator(
        ICaseRepository repository,
        IMeetingAiService ai,
        DeterministicCueEngine cues)
        : this(repository, ai, cues, OptionalAnalysisBudget)
    {
    }

    internal MeetingAssistantOrchestrator(
        ICaseRepository repository,
        IMeetingAiService ai,
        DeterministicCueEngine cues,
        TimeSpan optionalAnalysisBudget)
    {
        if (optionalAnalysisBudget <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(optionalAnalysisBudget));
        _repository = repository;
        _ai = ai;
        _cues = cues;
        _optionalAnalysisBudget = optionalAnalysisBudget;
    }

    public async Task<AssistantResponse> AcceptFinalTurnAsync(
        MeetingState state,
        TranscriptTurn turn,
        CancellationToken cancellationToken = default)
    {
        await RecordFinalTurnAsync(state, turn, cancellationToken).ConfigureAwait(false);
        return await CreateAssistanceForRecordedTurnAsync(state, turn, cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordFinalTurnAsync(
        MeetingState state,
        TranscriptTurn turn,
        CancellationToken cancellationToken = default)
    {
        ValidateFinalTurn(turn);
        state.AddTurn(turn);
        await _repository.SaveTranscriptTurnAsync(turn, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AssistantResponse> CreateAssistanceForRecordedTurnAsync(
        MeetingState state,
        TranscriptTurn turn,
        CancellationToken cancellationToken = default)
    {
        ValidateFinalTurn(turn);
        cancellationToken.ThrowIfCancellationRequested();

        if (turn.Speaker != SpeakerRole.Hr)
        {
            return AssistantResponse.NoAction();
        }

        var deterministic = _cues.Analyze(turn.Text);
        MeetingAnalysis analysis = deterministic;

        var ambiguous = !deterministic.PotentialCommitment &&
                        (deterministic.Intent == MeetingIntent.Unknown ||
                         (deterministic.NeedsAssistant && deterministic.RetrievalTerms.Count < 2));
        if (ambiguous && turn.Text.Length >= 20)
        {
            using var analysisCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            analysisCts.CancelAfter(_optionalAnalysisBudget);
            try
            {
                var aiAnalysis = await _ai.AnalyzeTurnAsync(state, turn, analysisCts.Token).ConfigureAwait(false);
                analysis = Merge(deterministic, aiAnalysis);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && analysisCts.IsCancellationRequested)
            {
                // Optional analysis timed out; keep deterministic classification for the answer path.
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!analysis.NeedsAssistant && !analysis.PotentialWrittenFollowUp)
        {
            return AssistantResponse.NoAction(analysis.Intent);
        }

        var query = BuildRetrievalQuery(turn.Text, analysis.RetrievalTerms);
        var evidenceTask = _repository.SearchAsync(query, 8, cancellationToken);
        var factsTask = _repository.GetFactsAsync(cancellationToken);
        await Task.WhenAll(evidenceTask, factsTask).ConfigureAwait(false);

        return await _ai.CreateAssistantResponseAsync(
            state,
            turn,
            analysis,
            await factsTask.ConfigureAwait(false),
            await evidenceTask.ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateFinalTurn(TranscriptTurn turn)
    {
        if (!turn.IsFinal)
        {
            throw new ArgumentException("Only final transcript turns may enter the durable meeting state.", nameof(turn));
        }
    }

    private static MeetingAnalysis Merge(MeetingAnalysis local, MeetingAnalysis ai)
    {
        var terms = local.RetrievalTerms
            .Concat(ai.RetrievalTerms)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();

        var intent = local.PotentialCommitment ? MeetingIntent.CommitmentRequest : ai.Intent != MeetingIntent.Unknown ? ai.Intent : local.Intent;
        var importance = (AssistantImportance)Math.Max((int)local.Importance, (int)ai.Importance);

        return new(
            intent,
            importance,
            local.NeedsAssistant || ai.NeedsAssistant,
            local.PotentialCommitment || ai.PotentialCommitment,
            local.PotentialWrittenFollowUp || ai.PotentialWrittenFollowUp,
            terms);
    }

    private static string BuildRetrievalQuery(string turnText, IReadOnlyList<string> terms) =>
        terms.Count > 0 ? string.Join(' ', terms) : turnText;
}
