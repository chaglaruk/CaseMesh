namespace HRCompanion.Core.Models;

public sealed class MeetingState
{
    private readonly object _sync = new();
    private readonly List<TranscriptTurn> _turns = [];

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
        get { lock (_sync) return _turns.ToArray(); }
    }
    public string RollingSummary { get { lock (_sync) return _rollingSummary; } }
    public IReadOnlyList<string> OpenQuestions { get { lock (_sync) return _openQuestions.ToArray(); } }
    public IReadOnlyList<string> Commitments { get { lock (_sync) return _commitments.ToArray(); } }
    public IReadOnlyList<string> WrittenFollowUps { get { lock (_sync) return _writtenFollowUps.ToArray(); } }

    private string _rollingSummary = string.Empty;
    private readonly List<string> _openQuestions = [];
    private readonly List<string> _commitments = [];
    private readonly List<string> _writtenFollowUps = [];

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
        }
    }

    public void SetRollingSummary(string summary)
    {
        lock (_sync) _rollingSummary = summary.Trim();
    }

    public void ReplaceOpenQuestions(IEnumerable<string> items) { lock (_sync) Replace(_openQuestions, items); }
    public void ReplaceCommitments(IEnumerable<string> items) { lock (_sync) Replace(_commitments, items); }
    public void ReplaceWrittenFollowUps(IEnumerable<string> items) { lock (_sync) Replace(_writtenFollowUps, items); }

    public IReadOnlyList<TranscriptTurn> RecentTurns(int max = 32)
    {
        lock (_sync)
        {
            return _turns.Count <= max ? _turns.ToArray() : _turns.Skip(_turns.Count - max).ToArray();
        }
    }

    private static void Replace(List<string> target, IEnumerable<string> source)
    {
        target.Clear();
        target.AddRange(source.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
    }
}
