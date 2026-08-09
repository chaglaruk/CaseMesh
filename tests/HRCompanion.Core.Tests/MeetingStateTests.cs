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

    [Fact]
    public void LatencySummary_CalculatesMedianAndNearestRankP95()
    {
        var origin = DateTimeOffset.Parse("2026-08-09T10:00:00Z");
        var values = new[] { 100d, 200d, 300d, 400d, 1000d };
        var samples = values.Select((milliseconds, index) => new PipelineTiming(
            Guid.NewGuid(),
            origin,
            origin.AddMilliseconds(5),
            origin.AddMilliseconds(10),
            origin.AddMilliseconds(20),
            null,
            null,
            origin.AddMilliseconds(30),
            origin.AddMilliseconds(milliseconds),
            origin.AddMilliseconds(milliseconds)));

        var summary = LatencySummary.Calculate(samples);
        Assert.NotNull(summary);
        Assert.Equal(300, summary.MedianMs);
        Assert.Equal(1000, summary.P95Ms);
    }

}
