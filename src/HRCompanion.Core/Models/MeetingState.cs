namespace HRCompanion.Core.Models;

public sealed class MeetingState
{
    private const int DefaultRecentTurnWindow = 32;
    private const int MaxRollingContextCharacters = 12000;
    private readonly object _sync = new();
    private readonly List<TranscriptTurn> _turns = [];
    private readonly List<string> _openQuestions = [];
    private readonly List<string> _commitments = [];
    private readonly List<string> _writtenFollowUps = [];
    private string _rollingSummary = string.Empty;

    public MeetingState(Guid meetingId, string caseName, DateTimeOffset startedAt)
    {
        MeetingId = meetingId;
        CaseName = caseName;
        StartedAt = startedAt;
    }

    public Guid MeetingId { get; }
    public string CaseName { get; }
    public DateTimeOffset StartedAt { get; }

    public IReadOnlyList<TranscriptTurn> Turns
    {
        get
        {
            lock (_sync) return _turns.ToArray();
        }
    }

    public string RollingSummary
    {
        get
        {
            lock (_sync) return _rollingSummary;
        }
    }

    public IReadOnlyList<string> OpenQuestions
    {
        get
        {
            lock (_sync) return _openQuestions.ToArray();
        }
    }

    public IReadOnlyList<string> Commitments
    {
        get
        {
            lock (_sync) return _commitments.ToArray();
        }
    }

    public IReadOnlyList<string> WrittenFollowUps
    {
        get
        {
            lock (_sync) return _writtenFollowUps.ToArray();
        }
    }

    public void AddTurn(TranscriptTurn turn)
    {
        if (turn.MeetingId != MeetingId)
        {
            throw new InvalidOperationException("Transcript turn belongs to a different meeting.");
        }

        lock (_sync)
        {
            var insertAt = _turns.FindIndex(existing =>
                existing.StartedAt > turn.StartedAt ||
                (existing.StartedAt == turn.StartedAt && existing.EndedAt > turn.EndedAt));
            if (insertAt < 0)
            {
                _turns.Add(turn);
            }
            else
            {
                _turns.Insert(insertAt, turn);
            }

            RebuildRollingContextLocked();
        }
    }

    public void SetRollingSummary(string summary)
    {
        lock (_sync) _rollingSummary = summary.Trim();
    }

    public void ReplaceOpenQuestions(IEnumerable<string> items)
    {
        lock (_sync) Replace(_openQuestions, items);
    }

    public void ReplaceCommitments(IEnumerable<string> items)
    {
        lock (_sync) Replace(_commitments, items);
    }

    public void ReplaceWrittenFollowUps(IEnumerable<string> items)
    {
        lock (_sync) Replace(_writtenFollowUps, items);
    }

    public IReadOnlyList<TranscriptTurn> RecentTurns(int max = DefaultRecentTurnWindow)
    {
        if (max <= 0) return [];
        lock (_sync)
        {
            var start = Math.Max(0, _turns.Count - max);
            return _turns.GetRange(start, _turns.Count - start).ToArray();
        }
    }

    private void RebuildRollingContextLocked()
    {
        var olderTurnCount = Math.Max(0, _turns.Count - DefaultRecentTurnWindow);
        if (olderTurnCount == 0)
        {
            _rollingSummary = string.Empty;
            return;
        }

        var lines = _turns.Take(olderTurnCount).Select(turn =>
            $"[{turn.StartedAt:HH:mm:ss}] {SpeakerLabel(turn.Speaker)}: {CollapseWhitespace(turn.Text)}");
        var context = string.Join(Environment.NewLine, lines);
        if (context.Length > MaxRollingContextCharacters)
        {
            context = "… earlier compacted context omitted …" + Environment.NewLine + context[^MaxRollingContextCharacters..];
        }

        _rollingSummary = context;
    }

    private static string SpeakerLabel(SpeakerRole speaker) => speaker switch
    {
        SpeakerRole.User => "USER_ACTUALLY_SAID",
        SpeakerRole.Hr => "HR_SAID",
        _ => "UNKNOWN"
    };

    private static string CollapseWhitespace(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static void Replace(List<string> target, IEnumerable<string> source)
    {
        target.Clear();
        target.AddRange(source.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
    }
}
