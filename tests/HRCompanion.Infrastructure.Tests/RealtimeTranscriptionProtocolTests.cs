using HRCompanion.Core.Abstractions;
using HRCompanion.Core.Models;
using HRCompanion.Infrastructure.Data;
using HRCompanion.Infrastructure.OpenAI;

namespace HRCompanion.Infrastructure.Tests;

public sealed class RealtimeTranscriptionProtocolTests
{
    [Fact]
    public async Task OutOfOrderCompletions_PersistInSpeechOrderWithProviderItemIds()
    {
        var root = Path.Combine(Path.GetTempPath(), "HRCompanion.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new SqliteCaseRepository(new AppPaths(root));
            await repository.InitializeAsync();
            var meetingId = Guid.NewGuid();
            var state = new MeetingState(meetingId, "Synthetic", DateTimeOffset.UtcNow);
            await repository.StartMeetingAsync(state);
            var parser = new RealtimeTranscriptionEventParser();
            var started = DateTimeOffset.Parse("2026-08-09T10:00:00Z");

            parser.Parse("""{"type":"input_audio_buffer.committed","item_id":"turn-a","previous_item_id":null}""", started);
            parser.Parse("""{"type":"input_audio_buffer.committed","item_id":"turn-b","previous_item_id":"turn-a"}""", started.AddSeconds(2));
            var secondFirst = parser.Parse("""{"type":"conversation.item.input_audio_transcription.completed","item_id":"turn-b","transcript":"Second spoken turn"}""", started.AddSeconds(4));
            Assert.Empty(secondFirst.Updates);

            var ordered = parser.Parse("""{"type":"conversation.item.input_audio_transcription.completed","item_id":"turn-a","transcript":"First spoken turn"}""", started.AddSeconds(5)).Updates;
            Assert.Equal(["turn-a", "turn-b"], ordered.Select(update => update.ItemId));

            foreach (var update in ordered)
            {
                var turn = TranscriptTurn.Final(
                    meetingId,
                    SpeakerRole.Hr,
                    update.Text,
                    update.StartedAt!.Value,
                    update.OccurredAt,
                    "teams",
                    update.ItemId);
                state.AddTurn(turn);
                await repository.SaveTranscriptTurnAsync(turn);
            }

            var durable = await repository.GetMeetingTurnsAsync(meetingId);
            Assert.Equal(["First spoken turn", "Second spoken turn"], durable.Select(turn => turn.Text));
            Assert.Equal(["turn-a", "turn-b"], durable.Select(turn => turn.ProviderItemId));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void AdjacentPartialDeltas_RemainBoundToTheirOwnItem()
    {
        var parser = new RealtimeTranscriptionEventParser();
        var now = DateTimeOffset.UtcNow;

        var first = parser.Parse("""{"type":"conversation.item.input_audio_transcription.delta","item_id":"turn-a","delta":"First "}""", now);
        var second = parser.Parse("""{"type":"conversation.item.input_audio_transcription.delta","item_id":"turn-b","delta":"Second "}""", now.AddMilliseconds(1));

        Assert.Equal("turn-a", Assert.Single(first.Updates).ItemId);
        Assert.Equal("First ", first.Updates[0].Text);
        Assert.Equal("turn-b", Assert.Single(second.Updates).ItemId);
        Assert.Equal("Second ", second.Updates[0].Text);
    }

    [Fact]
    public void DuplicateCompletedEvent_IsEmittedOnlyOnce()
    {
        var parser = new RealtimeTranscriptionEventParser();
        var now = DateTimeOffset.UtcNow;
        parser.Parse("""{"type":"input_audio_buffer.committed","item_id":"turn-a","previous_item_id":null}""", now);
        var first = parser.Parse("""{"type":"conversation.item.input_audio_transcription.completed","item_id":"turn-a","transcript":"One final"}""", now.AddSeconds(1));
        var duplicate = parser.Parse("""{"type":"conversation.item.input_audio_transcription.completed","item_id":"turn-a","transcript":"One final"}""", now.AddSeconds(2));

        Assert.Single(first.Updates);
        Assert.Empty(duplicate.Updates);
    }

    [Fact]
    public void FailedEarlierItem_DoesNotBlockLaterCompletedTurn()
    {
        var parser = new RealtimeTranscriptionEventParser();
        var now = DateTimeOffset.UtcNow;
        parser.Parse("""{"type":"input_audio_buffer.committed","item_id":"turn-a","previous_item_id":null}""", now);
        parser.Parse("""{"type":"input_audio_buffer.committed","item_id":"turn-b","previous_item_id":"turn-a"}""", now.AddSeconds(1));

        var waiting = parser.Parse(
            """{"type":"conversation.item.input_audio_transcription.completed","item_id":"turn-b","transcript":"later turn survives"}""",
            now.AddSeconds(2));
        Assert.Empty(waiting.Updates);

        var failure = parser.Parse(
            """{"type":"conversation.item.input_audio_transcription.failed","item_id":"turn-a","error":{"type":"transcription_error","code":"synthetic_failure"}}""",
            now.AddSeconds(3));

        Assert.NotNull(failure.Error);
        Assert.Equal("transcription_error", failure.Error!.Type);
        Assert.Equal("synthetic_failure", failure.Error!.Code);
        var released = Assert.Single(failure.Updates);
        Assert.Equal("turn-b", released.ItemId);
        Assert.Equal("later turn survives", released.Text);

        var lateFailedCompletion = parser.Parse(
            """{"type":"conversation.item.input_audio_transcription.completed","item_id":"turn-a","transcript":"must stay ignored"}""",
            now.AddSeconds(4));
        Assert.Empty(lateFailedCompletion.Updates);

        var duplicateFailure = parser.Parse(
            """{"type":"conversation.item.input_audio_transcription.failed","item_id":"turn-a","error":{"type":"transcription_error","code":"synthetic_failure"}}""",
            now.AddSeconds(5));
        Assert.Empty(duplicateFailure.Updates);
        Assert.Equal("synthetic_failure", duplicateFailure.Error!.Code);
    }

    [Fact]
    public void SpeechStarted_EmitsActivityWithoutInventingTranscriptText()
    {
        var parser = new RealtimeTranscriptionEventParser();
        var now = DateTimeOffset.UtcNow;

        var activity = parser.Parse(
            """{"type":"input_audio_buffer.speech_started","item_id":"turn-a"}""", now);

        var update = Assert.Single(activity.Updates);
        Assert.True(update.IsSpeechStarted);
        Assert.False(update.IsFinal);
        Assert.Empty(update.Text);
        Assert.Equal("turn-a", update.ItemId);
    }

    [Fact]
    public void MultipleConnectionResets_DiscardPartialsAndNeverDuplicateDurableFinals()
    {
        var parser = new RealtimeTranscriptionEventParser();
        var now = DateTimeOffset.UtcNow;
        parser.Parse("""{"type":"input_audio_buffer.committed","item_id":"turn-a","previous_item_id":null}""", now);
        parser.Parse("""{"type":"conversation.item.input_audio_transcription.delta","item_id":"turn-a","delta":"incomplete"}""", now);

        parser.ResetConnection();
        parser.Parse("""{"type":"input_audio_buffer.committed","item_id":"turn-b","previous_item_id":null}""", now.AddSeconds(1));
        var firstFinal = parser.Parse(
            """{"type":"conversation.item.input_audio_transcription.completed","item_id":"turn-b","transcript":"durable B"}""",
            now.AddSeconds(2));
        Assert.Equal("turn-b", Assert.Single(firstFinal.Updates).ItemId);

        parser.ResetConnection();
        parser.Parse("""{"type":"input_audio_buffer.committed","item_id":"turn-b","previous_item_id":null}""", now.AddSeconds(3));
        var duplicate = parser.Parse(
            """{"type":"conversation.item.input_audio_transcription.completed","item_id":"turn-b","transcript":"duplicate B"}""",
            now.AddSeconds(4));
        parser.Parse("""{"type":"input_audio_buffer.committed","item_id":"turn-c","previous_item_id":null}""", now.AddSeconds(5));
        var nextFinal = parser.Parse(
            """{"type":"conversation.item.input_audio_transcription.completed","item_id":"turn-c","transcript":"durable C"}""",
            now.AddSeconds(6));

        Assert.Empty(duplicate.Updates);
        Assert.Equal("turn-c", Assert.Single(nextFinal.Updates).ItemId);
    }
}
