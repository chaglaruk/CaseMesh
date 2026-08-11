using HRCompanion.Core.Models;

namespace HRCompanion.App;

public partial class MainWindow
{
    private bool _emergencyAssistInProgress;

    internal async void RetryLastHrFromOverlay()
    {
        if (_emergencyAssistInProgress) return;

        var turn = _meeting.Turns.LastOrDefault(item => item.IsFinal && item.Speaker == SpeakerRole.Hr);
        if (turn is null)
        {
            StatusText.Text = "No transcribed HR turn is available to retry yet.";
            return;
        }

        _emergencyAssistInProgress = true;
        _overlay?.SetRetryEnabled(false);
        StatusText.Text = "Retrying assistance from the latest transcribed HR turn — no typing required...";

        try
        {
            using var retryCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await _orchestrator.GenerateAssistanceWithTimingAsync(
                _meeting,
                turn,
                DateTimeOffset.UtcNow,
                retryCts.Token);

            LatestHrText.Text = turn.Text;
            Render(result.Response);
            StatusText.Text = result.Response.Say is null && result.Response.Next is null &&
                              result.Response.Watch is null && result.Response.Ask is null
                ? "The latest HR turn was treated as informational/small-talk. Wait for the next substantive HR turn."
                : "Retry assistance ready from the latest HR turn.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Emergency retry timed out. The live transcript remains available; try the button once more if needed.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Emergency retry failed ({ex.GetType().Name}). Live transcription is unaffected.";
        }
        finally
        {
            _emergencyAssistInProgress = false;
            _overlay?.SetRetryEnabled(true);
        }
    }
}
