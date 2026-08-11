using System.Windows;
using HRCompanion.Core.Models;

namespace HRCompanion.App;

public partial class OverlayWindow : Window
{
    private const int HistoryLimit = 5;
    private readonly List<AssistantResponse> _history = [];
    private AssistantResponse _current = AssistantResponse.NoAction();
    private int _historyIndex = -1;

    public event EventHandler? RetryLastHrRequested;

    public OverlayWindow() => InitializeComponent();

    public void Render(AssistantResponse response)
    {
        if (ResponseHasContent(_current) && !Equivalent(_current, response)) Archive(_current);
        _current = response;
        _historyIndex = -1;
        RenderResponse(_current);
        UpdateNavigation();
    }

    public void SetRetryEnabled(bool enabled)
    {
        RetryLastHrButton.IsEnabled = enabled;
    }

    private void RetryLastHr_Click(object sender, RoutedEventArgs e) =>
        RetryLastHrRequested?.Invoke(this, EventArgs.Empty);

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (_history.Count == 0) return;
        if (_historyIndex < _history.Count - 1) _historyIndex++;
        RenderResponse(_history[_historyIndex]);
        UpdateNavigation();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_historyIndex < 0) return;
        if (_historyIndex == 0)
        {
            _historyIndex = -1;
            RenderResponse(_current);
        }
        else
        {
            _historyIndex--;
            RenderResponse(_history[_historyIndex]);
        }
        UpdateNavigation();
    }

    private void Archive(AssistantResponse response)
    {
        if (!ResponseHasContent(response)) return;
        if (_history.Count > 0 && Equivalent(_history[0], response)) return;
        _history.Insert(0, response);
        if (_history.Count > HistoryLimit) _history.RemoveAt(_history.Count - 1);
    }

    private void RenderResponse(AssistantResponse response)
    {
        SayText.Text = response.Say ?? "—";
        WatchText.Text = response.Watch ?? "—";
        AskText.Text = response.Ask ?? "—";
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
        !string.IsNullOrWhiteSpace(response.Watch) ||
        !string.IsNullOrWhiteSpace(response.Ask);

    private static bool Equivalent(AssistantResponse left, AssistantResponse right) =>
        string.Equals(left.Say, right.Say, StringComparison.Ordinal) &&
        string.Equals(left.Watch, right.Watch, StringComparison.Ordinal) &&
        string.Equals(left.Ask, right.Ask, StringComparison.Ordinal);

    public void RenderStatus(string status)
    {
        StatusIndicator.Text = status;
        StatusIndicator.Foreground = status switch
        {
            "LISTENING" => System.Windows.Media.Brushes.SeaGreen,
            "RECONNECTING" => System.Windows.Media.Brushes.DarkOrange,
            "TRANSCRIPT ONLY" => System.Windows.Media.Brushes.DarkOrange,
            _ => System.Windows.Media.Brushes.DimGray
        };
    }
}
