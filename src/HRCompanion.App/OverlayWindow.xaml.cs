using System.Windows;
using HRCompanion.Core.Models;

namespace HRCompanion.App;

public partial class OverlayWindow : Window
{
    private const int HistoryLimit = 5;
    private const double LowConfidenceThreshold = 0.60;
    private readonly List<OverlayEntry> _history = [];
    private AssistantResponse _current = AssistantResponse.NoAction();
    private string? _currentHeard;
    private int _historyIndex = -1;

    public OverlayWindow() => InitializeComponent();

    public void Render(AssistantResponse response, string? heard = null)
    {
        if (ResponseHasContent(_current) && !Equivalent(_current, response)) Archive(new(_current, _currentHeard));
        _current = response;
        _currentHeard = string.IsNullOrWhiteSpace(heard) ? _currentHeard : heard.Trim();
        _historyIndex = -1;
        RenderEntry(new(_current, _currentHeard));
        UpdateNavigation();
    }

    public void SetRetryEnabled(bool enabled)
    {
        RetryLastHrButton.IsEnabled = enabled;
    }

    private void RetryLastHr_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Windows.OfType<MainWindow>().FirstOrDefault()?.RetryLastHrFromOverlay();
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (_history.Count == 0) return;
        if (_historyIndex < _history.Count - 1) _historyIndex++;
        RenderEntry(_history[_historyIndex]);
        UpdateNavigation();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_historyIndex < 0) return;
        if (_historyIndex == 0)
        {
            _historyIndex = -1;
            RenderEntry(new(_current, _currentHeard));
        }
        else
        {
            _historyIndex--;
            RenderEntry(_history[_historyIndex]);
        }
        UpdateNavigation();
    }

    private void Archive(OverlayEntry entry)
    {
        if (!ResponseHasContent(entry.Response)) return;
        if (_history.Count > 0 && Equivalent(_history[0].Response, entry.Response) &&
            string.Equals(_history[0].Heard, entry.Heard, StringComparison.Ordinal)) return;
        _history.Insert(0, entry);
        if (_history.Count > HistoryLimit) _history.RemoveAt(_history.Count - 1);
    }

    private void RenderEntry(OverlayEntry entry)
    {
        HeardText.Text = entry.Heard ?? "—";
        SayText.Text = entry.Response.Say ?? "—";
        NextCueText.Text = entry.Response.Next ?? "—";
        WatchText.Text = entry.Response.Watch ?? "—";
        AskText.Text = entry.Response.Ask ?? "—";
        ConfidenceText.Text = ResponseHasContent(entry.Response) && entry.Response.Confidence < LowConfidenceThreshold
            ? "LOW CONFIDENCE — check HEARD before speaking; use Retry/manual correction if the transcript is wrong."
            : string.Empty;
    }

    private void UpdateNavigation()
    {
        PreviousButton.IsEnabled = _history.Count > 0 && _historyIndex < _history.Count - 1;
        NextButton.IsEnabled = _historyIndex >= 0;
        ViewIndicator.Text = _historyIndex < 0
            ? "LIVE"
            : $"HISTORY {_historyIndex + 1}/{_history.Count}";
    }

    private static bool ResponseHasContent(AssistantResponse response) =>
        !string.IsNullOrWhiteSpace(response.Say) ||
        !string.IsNullOrWhiteSpace(response.Next) ||
        !string.IsNullOrWhiteSpace(response.Watch) ||
        !string.IsNullOrWhiteSpace(response.Ask);

    private static bool Equivalent(AssistantResponse left, AssistantResponse right) =>
        string.Equals(left.Say, right.Say, StringComparison.Ordinal) &&
        string.Equals(left.Next, right.Next, StringComparison.Ordinal) &&
        string.Equals(left.Watch, right.Watch, StringComparison.Ordinal) &&
        string.Equals(left.Ask, right.Ask, StringComparison.Ordinal);

    public void RenderStatus(string status)
    {
        StatusIndicator.Text = status;
        StatusIndicator.Foreground = status switch
        {
            "LISTENING" => System.Windows.Media.Brushes.SeaGreen,
            "USER MIC PAUSED" => System.Windows.Media.Brushes.DarkOrange,
            "HR AUDIO BLOCKED" => System.Windows.Media.Brushes.Firebrick,
            "RECONNECTING" => System.Windows.Media.Brushes.DarkOrange,
            "TRANSCRIPT ONLY" => System.Windows.Media.Brushes.DarkOrange,
            _ => System.Windows.Media.Brushes.DimGray
        };
    }

    private sealed record OverlayEntry(AssistantResponse Response, string? Heard);
}
