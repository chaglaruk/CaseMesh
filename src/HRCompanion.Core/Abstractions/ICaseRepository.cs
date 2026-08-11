using HRCompanion.Core.Models;

namespace HRCompanion.Core.Abstractions;

public interface ICaseRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<bool> HasDocumentHashAsync(string sha256, CancellationToken cancellationToken = default);
    Task SaveDocumentAsync(DocumentRecord document, IReadOnlyList<DocumentChunk> chunks, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DocumentRecord>> GetDocumentsAsync(CancellationToken cancellationToken = default);
    Task UpdateDocumentClassificationAsync(
        Guid documentId,
        EvidenceChannel channel,
        EvidenceAuthority authority,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task DeleteDocumentAsync(Guid documentId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task<IReadOnlyList<EvidenceSnippet>> SearchAsync(string query, int limit = 8, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CaseFact>> GetFactsAsync(CancellationToken cancellationToken = default);
    Task SaveFactAsync(CaseFact fact, CancellationToken cancellationToken = default);
    Task<TranscriptPersistenceResult> SaveTranscriptTurnAsync(TranscriptTurn turn, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TranscriptTurn>> GetMeetingTurnsAsync(Guid meetingId, CancellationToken cancellationToken = default);
    Task StartMeetingAsync(MeetingState meeting, CancellationToken cancellationToken = default);
    Task CompleteMeetingAsync(Guid meetingId, CancellationToken cancellationToken = default);
    Task<MeetingState?> GetUnfinishedMeetingAsync(CancellationToken cancellationToken = default);
}
