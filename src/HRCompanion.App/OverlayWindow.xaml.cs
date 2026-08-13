using System.Windows;
using HRCompanion.Core.Models;

namespace HRCompanion.App;

public partial class OverlayWindow : Window
{
    public OverlayWindow() => InitializeComponent();

    public void Render(AssistantResponse response)
    {
        SayText.Text = response.Say ?? "—";
        WatchText.Text = response.Watch ?? "—";
        AskText.Text = response.Ask ?? "—";
        EvidenceStatusText.Text = response.Sources.Count == 0 ? "NO CASE EVIDENCE" : "CASE SOURCES AVAILABLE";
        SourcesText.Text = response.Sources.Count == 0
            ? "—"
            : string.Join(Environment.NewLine, response.Sources.Select(x => $"{x.SourceName}{(x.Locator is null ? string.Empty : " — " + x.Locator)}"));
        ConfidenceText.Text = $"Model self-rating: {response.Confidence:P0}";
    }
}
