using System.Windows;

namespace HRCompanion.App;

public partial class MainWindow
{
    private bool _stopButtonInProgress;

    private async void StopMeetingSafe_Click(object sender, RoutedEventArgs e)
    {
        if (_stopButtonInProgress) return;
        var coordinator = _coordinator;
        if (coordinator is null)
        {
            StopMeetingButton.IsEnabled = false;
            StartMeetingButton.IsEnabled = true;
            return;
        }

        _stopButtonInProgress = true;
        _coordinator = null;
        StopMeetingButton.IsEnabled = false;
        StopMeetingButton.Content = "Stopping...";
        StartMeetingButton.IsEnabled = false;
        SetLiveStatus("STOPPING", "Stopping live audio and transcription...");

        var stopped = false;
        try
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(7));
            try
            {
                await coordinator.StopAsync(stopCts.Token).WaitAsync(TimeSpan.FromSeconds(8));
                stopped = true;
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                StatusText.Text = "Live stop exceeded its safety bound. Close and reopen HR Companion before starting another live session.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Live stop failed ({ex.GetType().Name}). Close and reopen HR Companion before starting another live session.";
            }

            try
            {
                await _repository.CompleteMeetingAsync(_meeting.MeetingId);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Live capture stopped, but meeting finalization reported {ex.GetType().Name}. Persisted final transcript turns remain local.";
            }

            if (stopped)
            {
                try
                {
                    await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
                {
                    StatusText.Text = "Live capture stopped. Resource cleanup is still finishing; restart the app before starting another live session if Start does not behave normally.";
                }
                catch
                {
                    StatusText.Text = "Live capture stopped. Some cleanup reported an error; restart the app before another live session if needed.";
                }
            }
        }
        finally
        {
            StopMeetingButton.Content = "Stop";
            StopMeetingButton.IsEnabled = false;
            StartMeetingButton.IsEnabled = stopped;
            _stopButtonInProgress = false;

            if (stopped)
            {
                SetLiveStatus("MANUAL", "Meeting stopped. The local transcript and manual assistance remain available.");
            }
            else
            {
                SetLiveStatus("STOP FAILED", "Live stop did not complete within the safety bound. Close and reopen HR Companion before another live session.");
            }
        }
    }
}
