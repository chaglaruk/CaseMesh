using HRCompanion.Core.Abstractions;
using HRCompanion.Core.Models;

namespace HRCompanion.Core.Services;

public sealed class MeetingAssistantOrchestrator
{
    private readonly ICaseRepository _repository;
    private readonly IMeetingAiService _ai;
    private readonly DeterministicCueEngine _cues;

    public MeetingAssistantOrchestrator(
        ICaseRepository repository,
        IMeetingAiService ai,
        DeterministicCueEngine cues)
    {
        _repository = repository;
        _ai = ai;
        _cues = cues;
    }

    public async Task<AssistantResponse> AcceptFinalTurnAsync(
        MeetingState state,
        TranscriptTurn turn,
        CancellationToken cancellationToken = default) =>
        (await AcceptFinalTurnWithTimingAsync(state, turn, null, cancellationToken).ConfigureAwait(false)).Response;

    public async Task<AssistanceRunResult> AcceptFinalTurnWithTimingAsync(
        MeetingState state,
        TranscriptTurn turn,
        Action? onPersisted = null,
        CancellationToken cancellationToken = default)
    {
        if (!turn.IsFinal)
        {
            throw new ArgumentException("Only final transcript turns may enter the durable meeting state.", nameof(turn));
        }

        state.AddTurn(turn);
        await _repository.SaveTranscriptTurnAsync(turn, cancellationToken).ConfigureAwait(false);
        onPersisted?.Invoke();

        if (turn.Speaker != SpeakerRole.Hr)
        {
            return new(AssistantResponse.NoAction(), null);
        }

        var deterministic = _cues.Analyze(turn.Text);
        MeetingAnalysis analysis = deterministic;

        // Keep the common live path to one model round-trip. Use Luna only when local intent/retrieval is genuinely ambiguous.
        var ambiguous = deterministic.Intent == MeetingIntent.Unknown ||
                        (deterministic.NeedsAssistant && deterministic.RetrievalTerms.Count < 2);
        if (ambiguous && turn.Text.Length >= 20)
        {
            var aiAnalysis = await _ai.AnalyzeTurnAsync(state, turn, cancellationToken).ConfigureAwait(false);
            analysis = Merge(deterministic, aiAnalysis);
        }

        // High-risk informational turns such as capability/settlement/resignation language still need
        // a WATCH/ASK opportunity even when they are not phrased as a direct question or request.
        if (!analysis.NeedsAssistant && !analysis.PotentialWrittenFollowUp && !analysis.PotentialCommitment)
        {
            return new(AssistantResponse.NoAction(analysis.Intent), null);
        }

        var query = BuildRetrievalQuery(turn.Text, analysis.RetrievalTerms);
        var retrievalStartedAt = DateTimeOffset.UtcNow;
        var evidenceTask = _repository.SearchAsync(query, 8, cancellationToken);
        var factsTask = _repository.GetFactsAsync(cancellationToken);
        await Task.WhenAll(evidenceTask, factsTask).ConfigureAwait(false);
        var retrievalCompletedAt = DateTimeOffset.UtcNow;

        var answerRequestStartedAt = DateTimeOffset.UtcNow;
        var response = await _ai.CreateAssistantResponseAsync(
            state,
            turn,
            analysis,
            await factsTask.ConfigureAwait(false),
            await evidenceTask.ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        var responseCompletedAt = DateTimeOffset.UtcNow;
        return new(response, new(
            turn.Id,
            turn.EndedAt,
            retrievalStartedAt,
            retrievalCompletedAt,
            answerRequestStartedAt,
            responseCompletedAt));
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
