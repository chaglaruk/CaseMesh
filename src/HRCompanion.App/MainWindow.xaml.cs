using System.ComponentModel;
using System.Windows;
using HRCompanion.Audio.Windows;
using HRCompanion.Core.Abstractions;
using HRCompanion.Core.Models;
using HRCompanion.Core.Services;
using HRCompanion.Infrastructure.OpenAI;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace HRCompanion.App;

public partial class MainWindow : Window
{
    private readonly ICaseRepository _repository;
    private readonly IDocumentImporter _importer;
    private readonly IContextImporter _contextImporter;
    private readonly IApiKeyStore _keyStore;
    private readonly MeetingAssistantOrchestrator _orchestrator;
    private readonly IOptions<OpenAiOptions> _openAiOptions;
    private readonly List<PipelineTiming> _timings = [];
    private MeetingState _meeting = new(Guid.NewGuid(), "HR Case", DateTimeOffset.UtcNow);
    private LiveMeetingCoordinator? _coordinator;
    private OverlayWindow? _overlay;
    private AssistantResponse _latest = AssistantResponse.NoAction();
    private bool _closingAfterStop;

    public MainWindow(
        ICaseRepository repository,
        IDocumentImporter importer,
        IContextImporter contextImporter,
        IApiKeyStore keyStore,
        MeetingAssistantOrchestrator orchestrator,
        IOptions<OpenAiOptions> openAiOptions)
    {
        InitializeComponent();
        _repository = repository;
        _importer = importer;
        _contextImporter = contextImporter;
        _keyStore = keyStore;
        _orchestrator = orchestrator;
        _openAiOptions = openAiOptions;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshDevices();
        await RefreshPreflightAsync();
        var recovered = await _repository.GetUnfinishedMeetingAsync();
        if (recovered is not null)
        {
            _meeting = recovered;
            RenderRecentTranscript();
            SetLiveStatus("MANUAL", $"Recovered {recovered.Turns.Count} locally persisted turn(s). Manual assistance is available.");
        }
    }

    private async void RefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        RefreshDevices();
        await RefreshPreflightAsync();
    }

    private void RefreshDevices()
    {
        var selectedProcessId = (TeamsProcessBox.SelectedItem as TeamsProcessInfo)?.ProcessId;
        var teams = TeamsProcessLocator.Find();
        TeamsProcessBox.ItemsSource = teams;
        TeamsProcessBox.SelectedItem = teams.FirstOrDefault(item => item.ProcessId == selectedProcessId) ?? teams.FirstOrDefault();

        var selectedMicrophone = (MicrophoneBox.SelectedItem as MicrophoneDeviceInfo)?.DeviceNumber;
        var microphones = MicrophoneCaptureSource.GetDevices();
        MicrophoneBox.ItemsSource = microphones;
        MicrophoneBox.SelectedItem = microphones.FirstOrDefault(item => item.DeviceNumber == selectedMicrophone) ?? microphones.FirstOrDefault();
        StatusText.Text = teams.Count == 0 ? "Teams is not currently detected. Manual mode remains available." : $"Detected {teams.Count} Teams process candidate(s).";
    }

    private async void StartMeeting_Click(object sender, RoutedEventArgs e)
    {
        if (_coordinator is not null) return;
        if (TeamsProcessBox.SelectedItem is not TeamsProcessInfo teams)
        {
            SetLiveStatus("MANUAL", "Start Teams and refresh process detection, or use manual assistance.");
            return;
        }
        if (MicrophoneBox.SelectedItem is not MicrophoneDeviceInfo microphone)
        {
            SetLiveStatus("MANUAL", "No microphone is available. Manual assistance remains available.");
            return;
        }
        string? apiKey;
        try { apiKey = await _keyStore.GetAsync(); }
        catch (Exception ex)
        {
            SetLiveStatus("MANUAL", $"Windows Credential Manager is unavailable ({ex.GetType().Name}). Manual assistance remains available.");
            await RefreshPreflightAsync();
            return;
        }
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            SetLiveStatus("MANUAL", "OpenAI API key is missing. Save it locally before starting live transcription.");
            await RefreshPreflightAsync();
            return;
        }

        StartMeetingButton.IsEnabled = false;
        StopMeetingButton.IsEnabled = true;
        _timings.Clear();
        LatencyText.Text = string.Empty;
        SetLiveStatus("LISTENING", "Connecting separate Teams and microphone transcription sessions...");

        var previousMeeting = _meeting;
        _meeting = new MeetingState(Guid.NewGuid(), "HR Case", DateTimeOffset.UtcNow);
        await _repository.CompleteMeetingAsync(previousMeeting.MeetingId);
        await _repository.StartMeetingAsync(_meeting);

        var remoteAudio = new TeamsProcessLoopbackCaptureSource(teams.ProcessId);
        var userAudio = new MicrophoneCaptureSource(microphone.DeviceNumber);
        var remoteTranscriber = new OpenAiRealtimeTranscriber(SpeakerRole.Hr, _keyStore, _openAiOptions);
        var userTranscriber = new OpenAiRealtimeTranscriber(SpeakerRole.User, _keyStore, _openAiOptions);
        var coordinator = new LiveMeetingCoordinator(
            _meeting,
            _orchestrator,
            remoteAudio,
            userAudio,
            remoteTranscriber,
            userTranscriber);
        Attach(coordinator);
        _coordinator = coordinator;

        try
        {
            using var startCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await coordinator.StartAsync(startCts.Token);
            SetLiveStatus("LISTENING", "Teams/HR and microphone/USER are live as separate sources.");
            EnsureOverlay();
            _overlay?.RenderStatus("LISTENING");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Live start failed ({ex.GetType().Name}). Manual mode remains available.";
            SetLiveStatus("MANUAL", "Live audio is unavailable. Paste the latest HR turn below.");
            await DisposeCoordinatorAsync(coordinator);
            _coordinator = null;
            await _repository.CompleteMeetingAsync(_meeting.MeetingId);
            StartMeetingButton.IsEnabled = true;
            StopMeetingButton.IsEnabled = false;
        }
    }

    private async void StopMeeting_Click(object sender, RoutedEventArgs e) => await StopMeetingCoreAsync();

    private async Task StopMeetingCoreAsync()
    {
        var coordinator = _coordinator;
        if (coordinator is null) return;
        _coordinator = null;
        StopMeetingButton.IsEnabled = false;
        SetLiveStatus("MANUAL", "Stopping live capture and preserving the transcript...");
        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            await coordinator.StopAsync(stopCts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            StatusText.Text = "Live stop reached its time limit; persisted final transcript turns remain local.";
        }
        finally
        {
            await _repository.CompleteMeetingAsync(_meeting.MeetingId);
            await DisposeCoordinatorAsync(coordinator);
            StartMeetingButton.IsEnabled = true;
            SetLiveStatus("MANUAL", "Meeting stopped. The local transcript and manual assistance remain available.");
        }
    }

    private void Attach(LiveMeetingCoordinator coordinator)
    {
        coordinator.FinalTurn += (_, _) => Dispatcher.Invoke(RenderRecentTranscript);
        coordinator.AssistantUpdated += (_, response) => Dispatcher.Invoke(() => Render(response));
        coordinator.AssistanceInvalidated += (_, _) => Dispatcher.Invoke(() => Render(AssistantResponse.NoAction()));
        coordinator.HealthChanged += (_, health) => Dispatcher.Invoke(() => OnHealthChanged(health));
        coordinator.NonFatalError += (_, error) => Dispatcher.Invoke(() =>
        {
            StatusText.Text = $"Non-fatal live error: {error.GetType().Name}. No transcript content was logged.";
        });
        coordinator.LatencyMeasured += (_, timing) => Dispatcher.Invoke(() =>
        {
            _timings.Add(timing);
            var summary = LatencySummary.Calculate(_timings);
            if (summary is not null) LatencyText.Text = $"median {summary.MedianMs:F0} ms · p95 {summary.P95Ms:F0} ms";
        });
    }

    private void OnHealthChanged(LiveMeetingHealth health)
    {
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

    private void SetLiveStatus(string status, string detail)
    {
        LiveStatusText.Text = status;
        StatusText.Text = detail;
        _overlay?.RenderStatus(status);
    }

    private void RenderRecentTranscript()
    {
        RecentTranscriptText.Text = _meeting.Turns.Count == 0
            ? "—"
            : string.Join("   ", _meeting.RecentTurns(4).Select(turn => $"{(turn.Speaker == SpeakerRole.Hr ? "HR" : "YOU")}: {turn.Text}"));
    }

    private async Task DisposeCoordinatorAsync(LiveMeetingCoordinator coordinator)
    {
        try { await coordinator.DisposeAsync(); } catch { }
    }

    private async void ImportFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Supported files|*.pdf;*.docx;*.eml;*.txt;*.md;*.html;*.htm|All files|*.*"
        };
        if (dialog.ShowDialog(this) == true) await ImportAsync(dialog.FileNames);
    }

    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Import HR case folder", Multiselect = false };
        if (dialog.ShowDialog(this) == true) await ImportAsync([dialog.FolderName]);
    }

    private async void ImportWorkingContext_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = false,
            Filter = "HR Companion context|*.hrcontext;*.md;*.txt|All files|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            StatusText.Text = "Importing working context...";
            var result = await _contextImporter.ImportAsync(dialog.FileName);
            StatusText.Text = $"Working context: imported {result.Imported}; duplicates {result.SkippedDuplicate}; errors {result.Errors.Count}.";
            if (result.Errors.Count > 0)
                MessageBox.Show(string.Join(Environment.NewLine, result.Errors.Take(10)), "Some context records could not be imported", MessageBoxButton.OK, MessageBoxImage.Warning);
            await RefreshPreflightAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Working-context import failed";
            MessageBox.Show(ex.Message, "Context import failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ImportAsync(IEnumerable<string> paths)
    {
        try
        {
            StatusText.Text = "Importing case material...";
            var result = await _importer.ImportPathsAsync(paths);
            StatusText.Text = $"Imported {result.Imported}; duplicates {result.SkippedDuplicate}; unsupported {result.Unsupported}; errors {result.Errors.Count}.";
            if (result.Errors.Count > 0)
                MessageBox.Show(string.Join(Environment.NewLine, result.Errors.Take(10)), "Some files could not be imported", MessageBoxButton.OK, MessageBoxImage.Warning);
            await RefreshPreflightAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Import failed";
            MessageBox.Show(ex.Message, "Import failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _keyStore.SaveAsync(ApiKeyBox.Password);
            ApiKeyBox.Clear();
            StatusText.Text = "API key saved in Windows Credential Manager.";
            await RefreshPreflightAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Could not save API key", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SaveContext_Click(object sender, RoutedEventArgs e)
    {
        var text = ContextBox.Text.Trim();
        if (text.Length == 0) return;
        try
        {
            await _repository.SaveFactAsync(new(
                Guid.NewGuid(), text, FactStatus.UserPosition, null, "manual context", null, DateTimeOffset.UtcNow));
            ContextBox.Clear();
            StatusText.Text = "Case context saved locally as USER_POSITION.";
            await RefreshPreflightAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Could not save context", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Assist_Click(object sender, RoutedEventArgs e)
    {
        var text = HrTurnBox.Text.Trim();
        if (text.Length == 0) return;
        AssistButton.IsEnabled = false;
        var durable = false;
        try
        {
            await _repository.StartMeetingAsync(_meeting);
            if (_coordinator is not null)
            {
                StatusText.Text = "Storing manual live HR turn...";
                var liveResult = await _coordinator.SubmitManualHrTurnAsync(text);
                durable = liveResult.WasInserted || liveResult.Status == TranscriptPersistenceStatus.AlreadyDurable;
                StatusText.Text = liveResult.Status switch
                {
                    TranscriptPersistenceStatus.Inserted => "Manual live HR turn stored; current assistance is being prepared.",
                    TranscriptPersistenceStatus.AlreadyDurable => "Manual live HR turn was already stored.",
                    _ => "Manual live HR turn could not be stored; it was not added to the transcript."
                };
                return;
            }

            StatusText.Text = "Retrieving context and drafting...";
            var now = DateTimeOffset.UtcNow;
            var turn = TranscriptTurn.Final(_meeting.MeetingId, SpeakerRole.Hr, text, now, now, "manual");
            var result = await _orchestrator.AcceptFinalTurnWithTimingAsync(_meeting, turn, () => durable = true);
            _latest = result.Response;
            Render(_latest);
            RenderRecentTranscript();
            StatusText.Text = "Assistance ready.";
        }
        catch (Exception ex)
        {
            SetLiveStatus("MANUAL", durable
                ? "Assistant unavailable; the manual HR turn is stored locally."
                : "Manual HR turn persistence failed; it was not added to the durable transcript.");
            MessageBox.Show(ex.Message, "Assistant error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            AssistButton.IsEnabled = true;
        }
    }

    private void Render(AssistantResponse response)
    {
        _latest = response;
        SayText.Text = response.Say ?? "—";
        WatchText.Text = response.Watch ?? "—";
        AskText.Text = response.Ask ?? "—";
        SourcesText.Text = response.Sources.Count == 0
            ? "—"
            : string.Join(Environment.NewLine, response.Sources.Select(source => $"{source.SourceName}{(source.Locator is null ? string.Empty : " — " + source.Locator)}"));
        _overlay?.Render(response);
    }

    private void ShowOverlay_Click(object sender, RoutedEventArgs e) => EnsureOverlay();

    private void EnsureOverlay()
    {
        if (_overlay is null || !_overlay.IsLoaded)
        {
            _overlay = new OverlayWindow();
            _overlay.Closed += (_, _) => _overlay = null;
            _overlay.Show();
        }
        _overlay.Render(_latest);
    }

    private async Task RefreshPreflightAsync()
    {
        var keyPresent = false;
        var databaseAccessible = false;
        var sourceCount = 0;
        var factCount = 0;
        try { keyPresent = !string.IsNullOrWhiteSpace(await _keyStore.GetAsync()); } catch { }
        try
        {
            var documentsTask = _repository.GetDocumentsAsync();
            var factsTask = _repository.GetFactsAsync();
            await Task.WhenAll(documentsTask, factsTask);
            sourceCount = (await documentsTask).Count;
            factCount = (await factsTask).Count;
            databaseAccessible = true;
            DocumentCountText.Text = $"{sourceCount} source document(s); {factCount} context/fact record(s) stored locally";
        }
        catch
        {
            DocumentCountText.Text = "Case Brain database is unavailable";
        }

        PreflightText.Text = string.Join("  |  ",
            $"API key {(keyPresent ? "OK" : "MISSING")}",
            $"Teams {(TeamsProcessBox.SelectedItem is null ? "MISSING" : "OK")}",
            $"microphone {(MicrophoneBox.SelectedItem is null ? "MISSING" : "OK")}",
            $"Case Brain {(databaseAccessible ? "OK" : "ERROR")}",
            sourceCount + factCount == 0 ? "sources EMPTY (allowed)" : $"sources {sourceCount + factCount}");
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_coordinator is not null && !_closingAfterStop)
        {
            e.Cancel = true;
            _closingAfterStop = true;
            await StopMeetingCoreAsync();
            Close();
            return;
        }
        base.OnClosing(e);
    }
}
