using HRCompanion.Infrastructure.OpenAI;

namespace HRCompanion.Infrastructure.Tests;

public sealed class RealtimeSpeechTimingTests
{
    [Fact]
    public void SpeechStoppedTimestamp_IsCarriedIntoFinalTranscriptUpdate()
    {
        var parser = new RealtimeTranscriptionEventParser();
        var started = DateTimeOffset.Parse("2026-08-11T12:00:00Z");
        var stopped = started.AddSeconds(3);
        var completed = stopped.AddMilliseconds(700);

        parser.Parse("""{"type":"input_audio_buffer.speech_started","item_id":"turn-a"}""", started);
        var stopUpdate = parser.Parse("""{"type":"input_audio_buffer.speech_stopped","item_id":"turn-a"}""", stopped);
        Assert.True(Assert.Single(stopUpdate.Updates).IsSpeechStopped);
        parser.Parse("""{"type":"input_audio_buffer.committed","item_id":"turn-a","previous_item_id":null}""", stopped.AddMilliseconds(10));

        var final = parser.Parse(
            """{"type":"conversation.item.input_audio_transcription.completed","item_id":"turn-a","transcript":"Can you explain that?"}""",
            completed);

        var update = Assert.Single(final.Updates);
        Assert.True(update.IsFinal);
        Assert.Equal(started, update.StartedAt);
        Assert.Equal(stopped, update.EndedAt);
        Assert.Equal(completed, update.OccurredAt);
    }
}
