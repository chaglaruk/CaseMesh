using System.Text;
using HRCompanion.Core.Models;

namespace HRCompanion.Infrastructure.Data;

public sealed class ReadableMeetingStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ReadableMeetingStore(AppPaths paths)
    {
        TranscriptFolder = Path.Combine(paths.Logs, "transcripts");
        MeetingObjectivePath = Path.Combine(paths.Root, "meeting-objective.txt");
        Directory.CreateDirectory(TranscriptFolder);
    }

    public string TranscriptFolder { get; }
    public string MeetingObjectivePath { get; }

    public async Task<string> WriteTranscriptSnapshotAsync(
        MeetingState meeting,
        CancellationToken cancellationToken = default)
    {
        var turns = meeting.Turns
            .Where(turn => turn.IsFinal)
            .OrderBy(turn => turn.StartedAt)
            .ThenBy(turn => turn.EndedAt)
            .ToArray();

        var localStart = meeting.StartedAt.ToLocalTime();
        var shortId = meeting.MeetingId.ToString("N")[..8];
        var path = Path.Combine(
            TranscriptFolder,
            $"{localStart:yyyy-MM-dd_HH-mm-ss}_HRCompanion_{shortId}.txt");

        var text = BuildReadableTranscript(meeting, turns);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(TranscriptFolder);
            var temporaryPath = path + ".tmp";
            await File.WriteAllTextAsync(
                temporaryPath,
                text,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
            return path;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveMeetingObjectiveAsync(string objective, CancellationToken cancellationToken = default)
    {
        var normalized = objective.Trim();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (normalized.Length == 0)
            {
                if (File.Exists(MeetingObjectivePath)) File.Delete(MeetingObjectivePath);
                return;
            }

            await File.WriteAllTextAsync(
                MeetingObjectivePath,
                normalized,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public string? LoadMeetingObjective()
    {
        if (!File.Exists(MeetingObjectivePath)) return null;
        var text = File.ReadAllText(MeetingObjectivePath).Trim();
        return text.Length == 0 ? null : text;
    }

    public void ClearMeetingObjective()
    {
        if (File.Exists(MeetingObjectivePath)) File.Delete(MeetingObjectivePath);
    }

    internal static string BuildReadableTranscript(MeetingState meeting, IReadOnlyList<TranscriptTurn> turns)
    {
        var builder = new StringBuilder();
        builder.AppendLine("HR Companion — readable meeting transcript");
        builder.Append("Meeting started: ")
            .AppendLine(meeting.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"));
        builder.AppendLine("Generated locally from final transcription segments. Transcription may contain errors. No raw audio is stored in this text file.");
        builder.AppendLine();

        foreach (var turn in turns)
        {
            var speaker = turn.Speaker == SpeakerRole.Hr ? "HR" : "YOU";
            builder.Append('[')
                .Append(turn.StartedAt.ToLocalTime().ToString("HH:mm:ss"))
                .Append("] ")
                .Append(speaker)
                .Append(": ")
                .AppendLine(turn.Text.Trim());
        }

        return builder.ToString();
    }
}
