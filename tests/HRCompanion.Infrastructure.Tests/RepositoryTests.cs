using HRCompanion.Core.Models;
using HRCompanion.Infrastructure.Data;

namespace HRCompanion.Infrastructure.Tests;

public sealed class RepositoryTests : IAsyncLifetime
{
    private string _root = null!;
    private SqliteCaseRepository _repository = null!;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "HRCompanion.Tests", Guid.NewGuid().ToString("N"));
        _repository = new SqliteCaseRepository(new AppPaths(_root));
        await _repository.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, true); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Search_ReturnsMatchingSourceAndLocator()
    {
        var documentId = Guid.NewGuid();
        var chunk = new DocumentChunk(Guid.NewGuid(), documentId, 0, "Occupational Health recommended a phased return in this synthetic fixture.", "p.4");
        var document = new DocumentRecord(documentId, "synthetic.pdf", "synthetic.pdf", "ABC123", "application/pdf", DateTimeOffset.UtcNow, null, 1);
        await _repository.SaveDocumentAsync(document, [chunk]);

        var results = await _repository.SearchAsync("Occupational Health phased return");

        Assert.NotEmpty(results);
        Assert.Equal("synthetic.pdf", results[0].SourceName);
        Assert.Equal("p.4", results[0].SourceLocator);
    }

    [Fact]
    public async Task Transcript_RoundTripsSpeakerRole()
    {
        var meetingId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var turn = TranscriptTurn.Final(meetingId, SpeakerRole.User, "Synthetic actual answer", now, now, "microphone");
        await _repository.SaveTranscriptTurnAsync(turn);

        var read = await _repository.GetMeetingTurnsAsync(meetingId);
        Assert.Single(read);
        Assert.Equal(SpeakerRole.User, read[0].Speaker);
    }

    [Fact]
    public async Task Search_DeduplicatesRepeatedQuotedEvidenceText()
    {
        const string repeated = "The same quoted email says a suitable alternative role should be discussed.";
        foreach (var index in Enumerable.Range(0, 2))
        {
            var documentId = Guid.NewGuid();
            var chunk = new DocumentChunk(Guid.NewGuid(), documentId, 0, repeated, $"mail-{index}");
            var document = new DocumentRecord(documentId, $"synthetic-{index}.eml", $"synthetic-{index}.eml", $"HASH{index}", "message/rfc822", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(index), 1);
            await _repository.SaveDocumentAsync(document, [chunk]);
        }

        var results = await _repository.SearchAsync("suitable alternative role", 8);

        Assert.Single(results, x => x.Text == repeated);
    }

    [Fact]
    public async Task UnfinishedMeeting_RestoresActualTranscriptAndCanBeCompleted()
    {
        var meeting = new MeetingState(Guid.NewGuid(), "Synthetic case", DateTimeOffset.UtcNow.AddMinutes(-2));
        await _repository.StartMeetingAsync(meeting);
        await _repository.SaveTranscriptTurnAsync(TranscriptTurn.Final(
            meeting.MeetingId,
            SpeakerRole.User,
            "Actual synthetic microphone turn",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow,
            "microphone",
            "item-user-1"));

        var restored = await _repository.GetUnfinishedMeetingAsync();
        Assert.NotNull(restored);
        Assert.Equal("Actual synthetic microphone turn", Assert.Single(restored.Turns).Text);

        await _repository.CompleteMeetingAsync(meeting.MeetingId);
        Assert.Null(await _repository.GetUnfinishedMeetingAsync());
    }

    [Fact]
    public async Task Transcript_DeduplicatesSameProviderItemAcrossRetry()
    {
        var meetingId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var first = await _repository.SaveTranscriptTurnAsync(TranscriptTurn.Final(
            meetingId, SpeakerRole.Hr, "Original final", now, now, "teams", "provider-item-1"));
        var duplicate = await _repository.SaveTranscriptTurnAsync(TranscriptTurn.Final(
            meetingId, SpeakerRole.Hr, "Retried final", now, now, "teams", "provider-item-1"));

        var turns = await _repository.GetMeetingTurnsAsync(meetingId);
        Assert.Equal(TranscriptPersistenceStatus.Inserted, first.Status);
        Assert.Equal(TranscriptPersistenceStatus.AlreadyDurable, duplicate.Status);
        Assert.Single(turns);
        Assert.Equal("Original final", turns[0].Text);
    }
}
