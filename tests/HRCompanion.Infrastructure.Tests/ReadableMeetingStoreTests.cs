using HRCompanion.Core.Models;
using HRCompanion.Infrastructure.Data;

namespace HRCompanion.Infrastructure.Tests;

public sealed class ReadableMeetingStoreTests
{
    [Fact]
    public async Task Transcript_snapshot_contains_both_speakers_but_not_private_objective()
    {
        var root = Path.Combine(Path.GetTempPath(), "hrcompanion-readable-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ReadableMeetingStore(new AppPaths(root));
            var meeting = new MeetingState(Guid.NewGuid(), "HR Case", new DateTimeOffset(2026, 8, 12, 9, 0, 0, TimeSpan.Zero));
            meeting.SetMeetingObjective("PRIVATE OBJECTIVE MUST NOT APPEAR");
            meeting.AddTurn(TranscriptTurn.Final(
                meeting.MeetingId,
                SpeakerRole.Hr,
                "When do you expect to return?",
                meeting.StartedAt,
                meeting.StartedAt.AddSeconds(3),
                "test"));
            meeting.AddTurn(TranscriptTurn.Final(
                meeting.MeetingId,
                SpeakerRole.User,
                "I cannot responsibly give a fixed date today.",
                meeting.StartedAt.AddSeconds(5),
                meeting.StartedAt.AddSeconds(9),
                "test"));

            var path = await store.WriteTranscriptSnapshotAsync(meeting);
            var text = await File.ReadAllTextAsync(path);

            Assert.Contains("HR: When do you expect to return?", text, StringComparison.Ordinal);
            Assert.Contains("YOU: I cannot responsibly give a fixed date today.", text, StringComparison.Ordinal);
            Assert.DoesNotContain("PRIVATE OBJECTIVE MUST NOT APPEAR", text, StringComparison.Ordinal);
            Assert.Contains("Transcription may contain errors", text, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Meeting_objective_is_persisted_separately_and_can_be_cleared()
    {
        var root = Path.Combine(Path.GetTempPath(), "hrcompanion-objective-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ReadableMeetingStore(new AppPaths(root));
            const string objective = "Remain employed and pursue a medically safe return.";

            await store.SaveMeetingObjectiveAsync(objective);

            Assert.Equal(objective, store.LoadMeetingObjective());
            Assert.True(File.Exists(store.MeetingObjectivePath));

            store.ClearMeetingObjective();

            Assert.Null(store.LoadMeetingObjective());
            Assert.False(File.Exists(store.MeetingObjectivePath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
