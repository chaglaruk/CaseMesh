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
}
