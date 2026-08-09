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
    }

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
