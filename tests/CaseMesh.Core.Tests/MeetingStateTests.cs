using CaseMesh.Core.Models;

namespace CaseMesh.Core.Tests;

public sealed class MeetingStateTests
{
    [Fact]
    public void RecentTurns_PreservesActualSpeakerOwnership()
    {
        var meetingId = Guid.NewGuid();
        var state = new MeetingState(meetingId, "Synthetic", DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        state.AddTurn(TranscriptTurn.Final(meetingId, SpeakerRole.Hr, "Question", now, now, "teams"));
        state.AddTurn(TranscriptTurn.Final(meetingId, SpeakerRole.User, "Actual spoken answer", now, now, "microphone"));

        Assert.Equal(SpeakerRole.Hr, state.Turns[0].Speaker);
        Assert.Equal(SpeakerRole.User, state.Turns[1].Speaker);
        Assert.Equal("Actual spoken answer", state.Turns[1].Text);
    }

    [Fact]
    public void AddTurn_OrdersBySpeechStart_WhenFinalEventsArriveOutOfOrder()
    {
        var meetingId = Guid.NewGuid();
        var state = new MeetingState(meetingId, "Synthetic", DateTimeOffset.UtcNow);
        var firstStart = DateTimeOffset.UtcNow;
        var secondStart = firstStart.AddSeconds(2);

        state.AddTurn(TranscriptTurn.Final(meetingId, SpeakerRole.Hr, "Second completion arrived first", secondStart, secondStart.AddSeconds(1), "teams"));
        state.AddTurn(TranscriptTurn.Final(meetingId, SpeakerRole.Hr, "First speech", firstStart, firstStart.AddSeconds(5), "teams"));

        Assert.Equal("First speech", state.Turns[0].Text);
        Assert.Equal("Second completion arrived first", state.Turns[1].Text);
    }

    [Fact]
    public void LongMeeting_PreservesOlderTurnsAsCompactedActualContext()
    {
        var meetingId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var state = new MeetingState(meetingId, "Synthetic", startedAt);

        for (var i = 0; i < 34; i++)
        {
            var speaker = i % 2 == 0 ? SpeakerRole.Hr : SpeakerRole.User;
            var at = startedAt.AddSeconds(i);
            state.AddTurn(TranscriptTurn.Final(meetingId, speaker, $"Turn {i}", at, at, "synthetic"));
        }

        Assert.Equal(32, state.RecentTurns().Count);
        Assert.Contains("HR_SAID: Turn 0", state.RollingSummary, StringComparison.Ordinal);
        Assert.Contains("USER_ACTUALLY_SAID: Turn 1", state.RollingSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("Turn 2", state.RollingSummary, StringComparison.Ordinal);
    }
}
