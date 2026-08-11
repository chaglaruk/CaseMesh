using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using HRCompanion.Infrastructure.Data;

namespace HRCompanion.App;

public partial class MainWindow
{
    private readonly DispatcherTimer _readableTranscriptTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1)
    };

    private ReadableMeetingStore? _readableMeetingStore;
    private string? _lastReadableTranscriptFingerprint;
    private string _appliedMeetingObjective = string.Empty;
    private bool _meetingUtilitiesLoaded;

    private void MeetingUtilities_Loaded(object sender, RoutedEventArgs e)
    {
        if (_meetingUtilitiesLoaded) return;
        _meetingUtilitiesLoaded = true;

        try
        {
            _readableMeetingStore = new ReadableMeetingStore(new AppPaths());
            _appliedMeetingObjective = _readableMeetingStore.LoadMeetingObjective() ?? string.Empty;
            if (_appliedMeetingObjective.Length > 0)
            {
                ContextBox.Text = _appliedMeetingObjective;
                if (string.IsNullOrWhiteSpace(_meeting.MeetingObjective))
                    _meeting.SetMeetingObjective(_appliedMeetingObjective);
            }

            TranscriptAutoSaveText.Text = $"Readable transcript auto-save: ON · {_readableMeetingStore.TranscriptFolder}";
            _readableTranscriptTimer.Tick += ReadableTranscriptTimer_Tick;
            _readableTranscriptTimer.Start();
        }
        catch (Exception ex)
        {
            TranscriptAutoSaveText.Text = $"Readable transcript auto-save unavailable ({ex.GetType().Name}); SQLite transcript persistence remains active.";
        }
    }

    private async void ReadableTranscriptTimer_Tick(object? sender, EventArgs e)
    {
        // Existing startup recovery can replace _meeting after the Window.Loaded event. Re-apply only
        // the explicitly saved meeting-scoped objective; it is never imported into Case Brain evidence.
        if (_appliedMeetingObjective.Length > 0 && string.IsNullOrWhiteSpace(_meeting.MeetingObjective))
            _meeting.SetMeetingObjective(_appliedMeetingObjective);

        await SaveReadableTranscriptSnapshotAsync();
    }

    private async Task SaveReadableTranscriptSnapshotAsync()
    {
        var store = _readableMeetingStore;
        if (store is null) return;

        var meeting = _meeting;
        var finalTurns = meeting.Turns.Where(turn => turn.IsFinal).ToArray();
        var last = finalTurns.LastOrDefault();
        var fingerprint = $"{meeting.MeetingId:N}|{finalTurns.Length}|{last?.Id:N}|{last?.Text.Length ?? 0}";
        if (string.Equals(fingerprint, _lastReadableTranscriptFingerprint, StringComparison.Ordinal)) return;

        try
        {
            await store.WriteTranscriptSnapshotAsync(meeting);
            _lastReadableTranscriptFingerprint = fingerprint;
            TranscriptAutoSaveText.Text = $"Readable transcript auto-save: ON · {finalTurns.Length} final turn(s) · {store.TranscriptFolder}";
        }
        catch (Exception ex)
        {
            // The human-readable copy is deliberately best-effort and never participates in the
            // realtime/OpenAI pipeline. Durable SQLite transcript persistence remains the source of truth.
            TranscriptAutoSaveText.Text = $"Readable transcript copy failed ({ex.GetType().Name}); SQLite transcript persistence remains active.";
        }
    }

    private async void SaveMeetingContext_Click(object sender, RoutedEventArgs e)
    {
        var text = ContextBox.Text.Trim();
        if (text.Length == 0) return;

        _meeting.SetMeetingObjective(text);
        _appliedMeetingObjective = text;
        ContextBox.Text = text; // Keep it visible so the user can verify what is active.

        try
        {
            if (_readableMeetingStore is not null)
                await _readableMeetingStore.SaveMeetingObjectiveAsync(text);
            StatusText.Text = "Meeting objective is active, remains visible, and is stored locally for continuity. It is not Case Brain evidence.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Meeting objective is active for this session, but local continuity save failed ({ex.GetType().Name}).";
        }
    }

    private void ClearMeetingContext_Click(object sender, RoutedEventArgs e)
    {
        _meeting.SetMeetingObjective(string.Empty);
        _appliedMeetingObjective = string.Empty;
        ContextBox.Clear();
        try { _readableMeetingStore?.ClearMeetingObjective(); } catch { }
        StatusText.Text = "Meeting objective cleared. Documentary Case Brain evidence was not changed.";
    }

    private void OpenTranscripts_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = _readableMeetingStore?.TranscriptFolder
                         ?? Path.Combine(new AppPaths().Logs, "transcripts");
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true
            });
            StatusText.Text = $"Opened readable transcript folder: {folder}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Could not open transcript folder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
