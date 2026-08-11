using HRCompanion.Core.Abstractions;
using HRCompanion.Core.Models;

namespace HRCompanion.Core.Services;

public sealed class MeetingAssistantOrchestrator
{
    private const int MaximumHrFloorSegments = 6;
    private const int MaximumHrFloorCharacters = 3000;
    private static readonly TimeSpan MaximumInterSegmentGap = TimeSpan.FromSeconds(5);

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

        var persistence = await PersistFinalTurnAsync(state, turn, cancellationToken).ConfigureAwait(false);
        if (!persistence.WasInserted)
        {
            return new(AssistantResponse.NoAction(), null);
        }
        onPersisted?.Invoke();

        return await GenerateAssistanceWithTimingAsync(
            state,
            turn,
            persistence.PersistedAt!.Value,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptPersistenceResult> PersistFinalTurnAsync(
        MeetingState state,
        TranscriptTurn turn,
        CancellationToken cancellationToken = default)
    {
        if (!turn.IsFinal)
        {
            throw new ArgumentException("Only final transcript turns may enter the durable meeting state.", nameof(turn));
        }
        if (turn.MeetingId != state.MeetingId)
        {
            throw new ArgumentException("Transcript turn belongs to a different meeting.", nameof(turn));
        }

        var result = await _repository.SaveTranscriptTurnAsync(turn, cancellationToken).ConfigureAwait(false);
        if (result.WasInserted) state.AddTurn(turn);
        return result;
    }

    public async Task<AssistanceRunResult> GenerateAssistanceWithTimingAsync(
        MeetingState state,
        TranscriptTurn turn,
        DateTimeOffset persistedAt,
        CancellationToken cancellationToken = default)
    {
        if (turn.Speaker != SpeakerRole.Hr)
        {
            return new(AssistantResponse.NoAction(), null);
        }

        // Realtime VAD can split one natural HR speaking turn at a thinking pause. Recombine the
        // immediately consecutive HR floor (until the user speaks or a real gap occurs) so a question
        // asked in the middle is not lost merely because HR continues with explanatory sentences.
        var effectiveTurn = BuildCurrentHrFloor(state, turn);
        var deterministic = _cues.Analyze(effectiveTurn.Text);
        MeetingAnalysis analysis = deterministic;
        DateTimeOffset? analysisStartedAt = null;
        DateTimeOffset? analysisCompletedAt = null;

        // Keep the common live path to one model round-trip. Use Luna only when local intent/retrieval is genuinely ambiguous.
        var ambiguous = deterministic.Intent == MeetingIntent.Unknown ||
                        (deterministic.NeedsAssistant && deterministic.RetrievalTerms.Count < 2);
        if (ambiguous && effectiveTurn.Text.Length >= 20)
        {
            analysisStartedAt = DateTimeOffset.UtcNow;
            var aiAnalysis = await _ai.AnalyzeTurnAsync(state, effectiveTurn, cancellationToken).ConfigureAwait(false);
            analysisCompletedAt = DateTimeOffset.UtcNow;
            analysis = Merge(deterministic, aiAnalysis);
        }

        // High-risk informational turns such as capability/settlement/resignation language still need
        // a WATCH/ASK opportunity even when they are not phrased as a direct question or request.
        if (!analysis.NeedsAssistant && !analysis.PotentialWrittenFollowUp && !analysis.PotentialCommitment)
        {
            return new(AssistantResponse.NoAction(analysis.Intent), null);
        }

        var query = BuildRetrievalQuery(effectiveTurn.Text, analysis.RetrievalTerms);
        var retrievalStartedAt = DateTimeOffset.UtcNow;
        var evidenceTask = _repository.SearchAsync(query, 8, cancellationToken);
        var factsTask = _repository.GetFactsAsync(cancellationToken);
        await Task.WhenAll(evidenceTask, factsTask).ConfigureAwait(false);
        var retrievalCompletedAt = DateTimeOffset.UtcNow;

        var answerRequestStartedAt = DateTimeOffset.UtcNow;
        var response = await _ai.CreateAssistantResponseAsync(
            state,
            effectiveTurn,
            analysis,
            await factsTask.ConfigureAwait(false),
            await evidenceTask.ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        var responseCompletedAt = DateTimeOffset.UtcNow;
        return new(response, new(
            turn.Id,
            turn.EndedAt,
            persistedAt,
            retrievalStartedAt,
            retrievalCompletedAt,
            analysisStartedAt,
            analysisCompletedAt,
            answerRequestStartedAt,
            responseCompletedAt));
    }

    private static TranscriptTurn BuildCurrentHrFloor(MeetingState state, TranscriptTurn latest)
    {
        if (latest.Speaker != SpeakerRole.Hr || state.Turns.Count < 2) return latest;

        var latestIndex = -1;
        for (var index = state.Turns.Count - 1; index >= 0; index--)
        {
            if (state.Turns[index].Id != latest.Id) continue;
            latestIndex = index;
            break;
        }
        if (latestIndex < 0) return latest;

        var segments = new List<TranscriptTurn> { latest };
        var characterCount = latest.Text.Length;
        var next = latest;
        for (var index = latestIndex - 1; index >= 0 && segments.Count < MaximumHrFloorSegments; index--)
        {
            var candidate = state.Turns[index];
            if (candidate.Speaker != SpeakerRole.Hr) break;
            if (next.StartedAt - candidate.EndedAt > MaximumInterSegmentGap) break;
            if (characterCount + candidate.Text.Length + 1 > MaximumHrFloorCharacters) break;

            segments.Add(candidate);
            characterCount += candidate.Text.Length + 1;
            next = candidate;
        }

        if (segments.Count == 1) return latest;
        segments.Reverse();
        var combined = string.Join(' ', segments.Select(segment => segment.Text));
        return latest with
        {
            Text = combined,
            StartedAt = segments[0].StartedAt,
            Source = "hr-floor"
        };
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
