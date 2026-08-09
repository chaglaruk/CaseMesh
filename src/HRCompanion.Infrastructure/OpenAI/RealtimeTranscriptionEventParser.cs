using System.Text.Json;
using HRCompanion.Core.Abstractions;

namespace HRCompanion.Infrastructure.OpenAI;

internal sealed record RealtimeProtocolError(string Type, string? Code);
internal sealed record RealtimeParseResult(
    IReadOnlyList<TranscriptionUpdate> Updates,
    RealtimeProtocolError? Error = null);

internal sealed class RealtimeTranscriptionEventParser
{
    private readonly List<string> _commitOrder = [];
    private readonly Dictionary<string, PendingCompletion> _completed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _started = new(StringComparer.Ordinal);
    private readonly HashSet<string> _emitted = new(StringComparer.Ordinal);
    private int _nextToEmit;

    public RealtimeParseResult Parse(string json, DateTimeOffset receivedAt)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!TryGetString(root, "type", out var type)) return new([]);

        return type switch
        {
            "input_audio_buffer.speech_started" => OnSpeechStarted(root, receivedAt),
            "input_audio_buffer.committed" => OnCommitted(root, receivedAt),
            "conversation.item.input_audio_transcription.delta" => OnDelta(root, receivedAt),
            "conversation.item.input_audio_transcription.completed" => OnCompleted(root, receivedAt),
            "conversation.item.input_audio_transcription.failed" => OnFailure(root),
            "error" => OnError(root),
            _ => new([])
        };
    }

    public void ResetConnection()
    {
        _commitOrder.Clear();
        _completed.Clear();
        _started.Clear();
        _nextToEmit = 0;
    }

    private RealtimeParseResult OnSpeechStarted(JsonElement root, DateTimeOffset receivedAt)
    {
        if (!TryGetString(root, "item_id", out var itemId)) return new([]);
        _started.TryAdd(itemId, receivedAt);
        return new([new(string.Empty, false, receivedAt, itemId, receivedAt, null, true)]);
    }

    private RealtimeParseResult OnCommitted(JsonElement root, DateTimeOffset receivedAt)
    {
        if (!TryGetString(root, "item_id", out var itemId) || _emitted.Contains(itemId)) return new([]);
        _started.TryAdd(itemId, receivedAt);
        if (!_commitOrder.Contains(itemId, StringComparer.Ordinal))
        {
            var previous = TryGetString(root, "previous_item_id", out var previousItemId) ? previousItemId : null;
            var previousIndex = previous is null ? -1 : _commitOrder.FindIndex(value => value.Equals(previous, StringComparison.Ordinal));
            if (previous is not null && previousIndex >= 0)
                _commitOrder.Insert(previousIndex + 1, itemId);
            else
                _commitOrder.Add(itemId);
        }
        return new(DrainCompleted());
    }

    private RealtimeParseResult OnDelta(JsonElement root, DateTimeOffset receivedAt)
    {
        if (!TryGetString(root, "item_id", out var itemId) ||
            !TryGetString(root, "delta", out var delta) ||
            _emitted.Contains(itemId)) return new([]);
        _started.TryAdd(itemId, receivedAt);
        return new([new(delta, false, receivedAt, itemId, _started[itemId])]);
    }

    private RealtimeParseResult OnCompleted(JsonElement root, DateTimeOffset receivedAt)
    {
        if (!TryGetString(root, "item_id", out var itemId) || _emitted.Contains(itemId)) return new([]);
        _started.TryAdd(itemId, receivedAt);
        var transcript = TryGetString(root, "transcript", out var text) ? text : string.Empty;
        _completed.TryAdd(itemId, new(transcript, receivedAt));
        return new(DrainCompleted());
    }

    private RealtimeParseResult OnFailure(JsonElement root)
    {
        IReadOnlyList<TranscriptionUpdate> updates = [];
        if (TryGetString(root, "item_id", out var itemId) && !_emitted.Contains(itemId))
        {
            // A failed item is terminal. Mark it resolved so one failed transcription cannot
            // permanently block later completed items that are waiting behind it in commit order.
            _completed.Remove(itemId);
            _started.Remove(itemId);
            _emitted.Add(itemId);
            updates = DrainCompleted();
        }

        if (!root.TryGetProperty("error", out var error))
            return new(updates, new("transcription_error", null));

        return new(updates, new(
            TryGetString(error, "type", out var type) ? type : "transcription_error",
            TryGetString(error, "code", out var code) ? code : null));
    }

    private static RealtimeParseResult OnError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error)) return new([], new("realtime_error", null));
        return new([], new(
            TryGetString(error, "type", out var type) ? type : "realtime_error",
            TryGetString(error, "code", out var code) ? code : null));
    }

    private IReadOnlyList<TranscriptionUpdate> DrainCompleted()
    {
        var updates = new List<TranscriptionUpdate>();
        while (_nextToEmit < _commitOrder.Count)
        {
            var itemId = _commitOrder[_nextToEmit];
            if (_emitted.Contains(itemId))
            {
                _nextToEmit++;
                continue;
            }
            if (!_completed.Remove(itemId, out var completion)) break;
            _nextToEmit++;
            _emitted.Add(itemId);
            if (!string.IsNullOrWhiteSpace(completion.Text))
            {
                updates.Add(new(
                    completion.Text,
                    true,
                    completion.CompletedAt,
                    itemId,
                    _started.GetValueOrDefault(itemId, completion.CompletedAt),
                    _nextToEmit > 1 ? _commitOrder[_nextToEmit - 2] : null));
            }
        }
        return updates;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private sealed record PendingCompletion(string Text, DateTimeOffset CompletedAt);
}
