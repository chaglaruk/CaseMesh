namespace HRCompanion.Core.Models;

public sealed class MeetingState
{
    private const int DefaultRecentTurnWindow = 32;
    private const int MaxRollingContextCharacters = 12000;
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
    public IReadOnlyList<TranscriptTurn> Turns => _turns;
    public string RollingSummary { get; private set; } = string.Empty;
    public IReadOnlyList<string> OpenQuestions => _openQuestions;
    public IReadOnlyList<string> Commitments => _commitments;
    public IReadOnlyList<string> WrittenFollowUps => _writtenFollowUps;

    private readonly List<string> _openQuestions = [];
    private readonly List<string> _commitments = [];
    private readonly List<string> _writtenFollowUps = [];

    public void AddTurn(TranscriptTurn turn)
    {
        if (turn.MeetingId != MeetingId)
        {
            throw new InvalidOperationException("Transcript turn belongs to a different meeting.");
        }

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

        RebuildRollingContext();
    }

    public void SetRollingSummary(string summary) => RollingSummary = summary.Trim();

    public void ReplaceOpenQuestions(IEnumerable<string> items) => Replace(_openQuestions, items);
    public void ReplaceCommitments(IEnumerable<string> items) => Replace(_commitments, items);
    public void ReplaceWrittenFollowUps(IEnumerable<string> items) => Replace(_writtenFollowUps, items);

    public IReadOnlyList<TranscriptTurn> RecentTurns(int max = DefaultRecentTurnWindow) =>
        _turns.Count <= max ? _turns : _turns.Skip(_turns.Count - max).ToArray();

    private void RebuildRollingContext()
    {
        var olderTurnCount = Math.Max(0, _turns.Count - DefaultRecentTurnWindow);
        if (olderTurnCount == 0)
        {
            RollingSummary = string.Empty;
            return;
        }

        var lines = _turns.Take(olderTurnCount).Select(turn =>
            $"[{turn.StartedAt:HH:mm:ss}] {SpeakerLabel(turn.Speaker)}: {CollapseWhitespace(turn.Text)}");
        var context = string.Join(Environment.NewLine, lines);
        if (context.Length > MaxRollingContextCharacters)
        {
            context = "… earlier compacted context omitted …" + Environment.NewLine + context[^MaxRollingContextCharacters..];
        }

        RollingSummary = context;
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
