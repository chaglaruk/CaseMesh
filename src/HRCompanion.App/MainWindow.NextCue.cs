using System.Windows.Threading;

namespace HRCompanion.App;

public partial class MainWindow
{
    private readonly DispatcherTimer _nextCueSyncTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(150)
    };
    private bool _nextCueSyncStarted;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_nextCueSyncStarted) return;
        _nextCueSyncStarted = true;

        _nextCueSyncTimer.Tick += (_, _) =>
        {
            var text = _latest.Next ?? "—";
            if (!string.Equals(NextCueText.Text, text, StringComparison.Ordinal))
                NextCueText.Text = text;
        };
        _nextCueSyncTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _nextCueSyncTimer.Stop();
        base.OnClosed(e);
    }
}
