using CaseMesh.Core.Models;

namespace CaseMesh.Core.Abstractions;

public interface IMeetingAiService
{
    Task<MeetingAnalysis> AnalyzeTurnAsync(
        MeetingState state,
        TranscriptTurn latestHrTurn,
        CancellationToken cancellationToken = default);

    Task<AssistantResponse> CreateAssistantResponseAsync(
        MeetingState state,
        TranscriptTurn latestHrTurn,
        MeetingAnalysis analysis,
        IReadOnlyList<CaseFact> facts,
        IReadOnlyList<EvidenceSnippet> evidence,
        CancellationToken cancellationToken = default);
}
