using System.Windows;
using CaseMesh.Core.Models;

namespace CaseMesh.App;

public partial class OverlayWindow : Window
{
    public OverlayWindow() => InitializeComponent();

    public void Render(AssistantResponse response)
    {
        SayText.Text = response.Say ?? "—";
        WatchText.Text = response.Watch ?? "—";
        AskText.Text = response.Ask ?? "—";
        EvidenceStatusText.Text = response.Sources.Count == 0 ? "NO CASE EVIDENCE" : $"CASE EVIDENCE · {response.Sources.Count} SOURCE(S)";
        var visibleSources = response.Sources
            .DistinctBy(x => (x.SourceName, x.Locator))
            .Take(3)
            .Select(x => $"{x.SourceName}{(x.Locator is null ? string.Empty : " — " + x.Locator)}");
        SourcesText.Text = response.Sources.Count == 0 ? "—" : string.Join(Environment.NewLine, visibleSources);
        ConfidenceText.Text = $"Model self-rating only: {response.Confidence:P0}";
    }
}
