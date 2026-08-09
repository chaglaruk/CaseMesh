using HRCompanion.Core.Models;

namespace HRCompanion.Core.Tests;

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

}
