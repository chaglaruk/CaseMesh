using System.Windows;
using HRCompanion.Audio.Windows;
using HRCompanion.Core.Models;

namespace HRCompanion.App;

public partial class MainWindow
{
    private void AttachLiveSafety(TeamsAwareSystemLoopbackCaptureSource remoteAudio, MicrophoneCaptureSource microphone)
    {
        ResetLiveSafety();
        _liveRemoteAudio = remoteAudio;
        _liveMicrophone = microphone;
        _hrAudioBlocked = true;
        _hrAudioBlockDetail = "HR audio guard is starting; remote audio is blocked until the guard confirms the render path is clean.";
        remoteAudio.ContaminationChanged += OnRemoteAudioContaminationChanged;
        PauseUserMicButton.IsEnabled = true;
        PauseUserMicButton.Content = "Pause my mic";
        AudioGuardText.Text = _hrAudioBlockDetail;
    }

    private void ResetLiveSafety()
    {
        if (_liveRemoteAudio is not null)
            _liveRemoteAudio.ContaminationChanged -= OnRemoteAudioContaminationChanged;
        _liveRemoteAudio = null;
        if (_liveMicrophone is not null) _liveMicrophone.SetPaused(false);
        _liveMicrophone = null;
        _hrAudioBlocked = false;
        _hrAudioBlockDetail = null;
        _lastHealth = null;
        if (PauseUserMicButton is not null)
        {
            PauseUserMicButton.IsEnabled = false;
            PauseUserMicButton.Content = "Pause my mic";
        }
        if (AudioGuardText is not null) AudioGuardText.Text = string.Empty;
    }

    private void OnRemoteAudioContaminationChanged(object? sender, AudioContaminationChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            _hrAudioBlocked = e.IsBlocked;
            _hrAudioBlockDetail = e.IsBlocked
                ? $"HR AUDIO BLOCKED: another Windows audio session is active{(string.IsNullOrWhiteSpace(e.ProcessName) ? string.Empty : $" ({e.ProcessName})")}. Some HR speech may be missed until it stops."
                : null;
            AudioGuardText.Text = _hrAudioBlockDetail ?? string.Empty;
            RenderLiveHealth();
        });
    }

    private void PauseUserMic_Click(object sender, RoutedEventArgs e)
    {
        var microphone = _liveMicrophone;
        if (microphone is null) return;
        microphone.SetPaused(!microphone.IsPaused);
        PauseUserMicButton.Content = microphone.IsPaused ? "Resume my mic" : "Pause my mic";
        RenderLiveHealth();
    }

    private void RenderLiveHealth()
    {
        if (_hrAudioBlocked && _coordinator is not null)
        {
            SetLiveStatus("HR AUDIO BLOCKED", _hrAudioBlockDetail ?? "HR audio is temporarily blocked by the contamination guard.");
            return;
        }

        if (_liveMicrophone?.IsPaused == true && _coordinator is not null)
        {
            SetLiveStatus("USER MIC PAUSED", "HR Companion is not sending your microphone audio. Use this for Teams mute/private speech, then resume before answering HR.");
            return;
        }

        var health = _lastHealth;
        if (health is null)
        {
            if (_coordinator is not null) SetLiveStatus("LISTENING", "Live meeting capture is starting.");
            return;
        }

        var statusSuffix = health.HasTranscriptionGap ? " + GAP" : string.Empty;
        var gapDetail = health.HasTranscriptionGap
            ? $" Historical transcription gap: HR dropped {health.HrDiagnostics.FramesDropped}, " +
              $"USER dropped {health.UserDiagnostics.FramesDropped}; the transcript may be incomplete."
            : string.Empty;
        switch (health.State)
        {
            case LiveMeetingHealthState.FullListening:
                SetLiveStatus("LISTENING" + statusSuffix, "Teams/HR and microphone/USER transcription are currently healthy." + gapDetail);
                break;
            case LiveMeetingHealthState.HrReconnecting:
                SetLiveStatus("HR RECONNECTING" + statusSuffix, "Teams/HR transcription is reconnecting; USER transcription remains independently tracked." + gapDetail);
                break;
            case LiveMeetingHealthState.UserReconnecting:
                SetLiveStatus("USER RECONNECTING" + statusSuffix, "Microphone/USER transcription is reconnecting; HR transcription remains independently tracked." + gapDetail);
                break;
            case LiveMeetingHealthState.TranscriptionDegraded:
                SetLiveStatus("TRANSCRIPTION DEGRADED" + statusSuffix, "At least one actual-speech source is unavailable. Use manual fallback for missing turns." + gapDetail);
                break;
            case LiveMeetingHealthState.AssistantDegraded:
                SetLiveStatus("ASSISTANT DEGRADED" + statusSuffix, "Live transcription remains healthy, but SAY/WATCH/ASK generation is unavailable." + gapDetail);
                break;
            case LiveMeetingHealthState.Manual:
                SetLiveStatus("MANUAL", "Live capture is stopped. Manual assistance remains available.");
                break;
        }
    }
}
