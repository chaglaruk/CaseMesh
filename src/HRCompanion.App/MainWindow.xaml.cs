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
    private readonly MeetingState _meeting = new(Guid.NewGuid(), "HR Case", DateTimeOffset.UtcNow);
    private readonly SemaphoreSlim _liveGate = new(1, 1);
    private OverlayWindow? _overlay;
    private AssistantResponse _latest = AssistantResponse.NoAction();
    private LiveSession? _liveSession;
    private int _savedHrTurns;
    private int _savedUserTurns;
    private bool _allowClose;

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
        Loaded += async (_, _) =>
        {
            await RefreshDocumentCountAsync();
            await RefreshTeamsProcessesAsync();
        };
        Closing += MainWindow_Closing;
    }

    private async void RefreshTeams_Click(object sender, RoutedEventArgs e) => await RefreshTeamsProcessesAsync();

    private void TeamsProcessCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        StartLiveButton.IsEnabled = _liveSession is null && TeamsProcessCombo.SelectedItem is TeamsProcessInfo;

    private async Task RefreshTeamsProcessesAsync()
    {
        var selectedPid = (TeamsProcessCombo.SelectedItem as TeamsProcessInfo)?.ProcessId;
        RefreshTeamsButton.IsEnabled = false;
        try
        {
            var processes = await Task.Run(TeamsProcessLocator.Find);
            TeamsProcessCombo.ItemsSource = processes;
            var previous = processes.FirstOrDefault(process => process.ProcessId == selectedPid);
            TeamsProcessCombo.SelectedItem = previous ?? (processes.Count == 1 ? processes[0] : null);
            LiveStatusText.Text = processes.Count switch
            {
                0 => "Microsoft Teams is not running. MANUAL mode remains available.",
                1 => "One Teams process tree found and selected. Ready to start isolated live capture.",
                _ => $"{processes.Count} Teams process trees found. Select the meeting window you want to capture."
            };
        }
        catch (Exception ex)
        {
            TeamsProcessCombo.ItemsSource = null;
            LiveStatusText.Text = $"Teams discovery failed: {ex.Message} MANUAL mode remains available.";
        }
        finally
        {
            RefreshTeamsButton.IsEnabled = _liveSession is null;
            StartLiveButton.IsEnabled = _liveSession is null && TeamsProcessCombo.SelectedItem is TeamsProcessInfo;
        }
    }

    private async void StartLive_Click(object sender, RoutedEventArgs e) => await StartLiveAsync();

    private async Task StartLiveAsync()
    {
        await _liveGate.WaitAsync();
        try
        {
            if (_liveSession is not null) return;
            if (TeamsProcessCombo.SelectedItem is not TeamsProcessInfo teamsProcess)
            {
                LiveStatusText.Text = "Select a Microsoft Teams process before starting. MANUAL mode remains available.";
                return;
            }

            SetLiveControls(isTransitioning: true, isLive: false);
            LiveStatusText.Text = $"Starting isolated Teams capture for PID {teamsProcess.ProcessId}…";

            var remoteAudio = new TeamsProcessLoopbackCaptureSource(teamsProcess.ProcessId);
            var userAudio = new MicrophoneCaptureSource();
            var hrTranscriber = new OpenAiRealtimeTranscriber(SpeakerRole.Hr, _keyStore, _openAiOptions);
            var userTranscriber = new OpenAiRealtimeTranscriber(SpeakerRole.User, _keyStore, _openAiOptions);
            var coordinator = new LiveMeetingCoordinator(
                _meeting,
                _orchestrator,
                remoteAudio,
                userAudio,
                hrTranscriber,
                userTranscriber);
            var session = new LiveSession(coordinator, remoteAudio, userAudio, hrTranscriber, userTranscriber);
            SubscribeLive(coordinator);
            try
            {
                await coordinator.StartAsync();
                _savedHrTurns = 0;
                _savedUserTurns = 0;
                _liveSession = session;
                SetLiveControls(isTransitioning: false, isLive: true);
                LiveStatusText.Text = $"LIVE — isolated Teams PID {teamsProcess.ProcessId}; HR and microphone transcripts are separate.";
                StatusText.Text = "Live meeting started. MANUAL assistance remains available.";
            }
            catch (Exception ex)
            {
                UnsubscribeLive(coordinator);
                await session.DisposeAsync();
                SetLiveControls(isTransitioning: false, isLive: false);
                LiveStatusText.Text = $"Live capture unavailable: {ex.Message} MANUAL mode remains available.";
                StatusText.Text = "Live start failed — MANUAL mode is ready.";
                MessageBox.Show(ex.Message, "Could not start isolated Teams capture", MessageBoxButton.OK, MessageBoxImage.Warning);
                await RefreshTeamsProcessesAsync();
            }
        }
        finally
        {
            _liveGate.Release();
        }
    }

    private async void StopLive_Click(object sender, RoutedEventArgs e) => await StopLiveAsync();

    private async Task StopLiveAsync(string? reason = null)
    {
        await _liveGate.WaitAsync();
        try
        {
            var session = _liveSession;
            if (session is null) return;
            _liveSession = null;
            SetLiveControls(isTransitioning: true, isLive: false);
            LiveStatusText.Text = reason ?? "Stopping live meeting and flushing final transcript work…";
            try
            {
                await session.Coordinator.StopAsync();
            }
            finally
            {
                UnsubscribeLive(session.Coordinator);
                await session.DisposeAsync();
            }
            SetLiveControls(isTransitioning: false, isLive: false);
            LiveStatusText.Text = reason ??
                $"Stopped cleanly. Saved final turns this run: HR {_savedHrTurns}; User {_savedUserTurns}. Ready to restart.";
            StatusText.Text = "Live meeting stopped. Imported case data is unchanged.";
            await RefreshTeamsProcessesAsync();
        }
        catch (Exception ex)
        {
            SetLiveControls(isTransitioning: false, isLive: false);
            LiveStatusText.Text = $"Live stop reported an error: {ex.Message} MANUAL mode remains available.";
        }
        finally
        {
            _liveGate.Release();
        }
    }

    private void SubscribeLive(LiveMeetingCoordinator coordinator)
    {
        coordinator.FinalTurn += LiveCoordinator_FinalTurn;
        coordinator.AssistantUpdated += LiveCoordinator_AssistantUpdated;
        coordinator.NonFatalError += LiveCoordinator_NonFatalError;
        coordinator.CaptureFailed += LiveCoordinator_CaptureFailed;
        coordinator.Diagnostic += LiveCoordinator_Diagnostic;
    }

    private void UnsubscribeLive(LiveMeetingCoordinator coordinator)
    {
        coordinator.FinalTurn -= LiveCoordinator_FinalTurn;
        coordinator.AssistantUpdated -= LiveCoordinator_AssistantUpdated;
        coordinator.NonFatalError -= LiveCoordinator_NonFatalError;
        coordinator.CaptureFailed -= LiveCoordinator_CaptureFailed;
        coordinator.Diagnostic -= LiveCoordinator_Diagnostic;
    }

    private void LiveCoordinator_FinalTurn(object? sender, TranscriptTurn turn) => Dispatcher.BeginInvoke(() =>
    {
        if (turn.Speaker == SpeakerRole.Hr) _savedHrTurns++;
        if (turn.Speaker == SpeakerRole.User) _savedUserTurns++;
        LiveStatusText.Text = $"LIVE — saved final turns: HR {_savedHrTurns}; User {_savedUserTurns}.";
    });

    private void LiveCoordinator_AssistantUpdated(object? sender, AssistantResponse response) => Dispatcher.BeginInvoke(() =>
    {
        _latest = response;
        Render(response);
        StatusText.Text = "Live assistance updated for the latest HR turn.";
    });

    private void LiveCoordinator_NonFatalError(object? sender, Exception exception) => Dispatcher.BeginInvoke(() =>
    {
        StatusText.Text = $"Live diagnostic: {exception.Message}";
    });

    private void LiveCoordinator_CaptureFailed(object? sender, Exception exception) => Dispatcher.BeginInvoke(() =>
    {
        _ = StopLiveAsync($"Live capture stopped: {exception.Message} MANUAL mode remains available.");
    });

    private void LiveCoordinator_Diagnostic(object? sender, LiveMeetingDiagnosticEventArgs diagnostic) => Dispatcher.BeginInvoke(() =>
    {
        if (diagnostic.Code is "STALE_ASSISTANCE_CANCELLED" or "ASSISTANCE_PUBLISHED" or "ASSISTANCE_TIMEOUT")
        {
            StatusText.Text = diagnostic.Message;
        }
    });

    private void SetLiveControls(bool isTransitioning, bool isLive)
    {
        TeamsProcessCombo.IsEnabled = !isTransitioning && !isLive;
        RefreshTeamsButton.IsEnabled = !isTransitioning && !isLive;
        StartLiveButton.IsEnabled = !isTransitioning && !isLive && TeamsProcessCombo.SelectedItem is TeamsProcessInfo;
        StopLiveButton.IsEnabled = !isTransitioning && isLive;
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || _liveSession is null) return;
        e.Cancel = true;
        await StopLiveAsync("Closing HR Companion; live capture stopped cleanly.");
        _allowClose = true;
        Close();
    }

    private async void ImportFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Supported files|*.pdf;*.docx;*.eml;*.txt;*.md;*.html;*.htm|All files|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        await ImportAsync(dialog.FileNames);
    }

    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Import HR case folder", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        await ImportAsync([dialog.FolderName]);
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
            StatusText.Text = "Importing working context…";
            var result = await _contextImporter.ImportAsync(dialog.FileName);
            StatusText.Text = $"Working context: imported {result.Imported}; duplicates {result.SkippedDuplicate}; errors {result.Errors.Count}.";
            if (result.Errors.Count > 0)
            {
                MessageBox.Show(string.Join(Environment.NewLine, result.Errors.Take(10)), "Some context records could not be imported", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            await RefreshDocumentCountAsync();
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
            StatusText.Text = "Importing case material…";
            var result = await _importer.ImportPathsAsync(paths);
            StatusText.Text = $"Imported {result.Imported}; duplicates {result.SkippedDuplicate}; unsupported {result.Unsupported}; errors {result.Errors.Count}.";
            if (result.Errors.Count > 0)
            {
                MessageBox.Show(string.Join(Environment.NewLine, result.Errors.Take(10)), "Some files could not be imported", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            await RefreshDocumentCountAsync();
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
            var fact = new CaseFact(
                Guid.NewGuid(),
                text,
                FactStatus.UserPosition,
                SourceDocumentId: null,
                SourceLocator: "manual context",
                EffectiveDate: null,
                CreatedAt: DateTimeOffset.UtcNow);
            await _repository.SaveFactAsync(fact);
            ContextBox.Clear();
            StatusText.Text = "Case context saved locally as USER_POSITION.";
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
        try
        {
            StatusText.Text = "Retrieving context and drafting…";
            var now = DateTimeOffset.UtcNow;
            var turn = TranscriptTurn.Final(_meeting.MeetingId, SpeakerRole.Hr, text, now, now, "manual");
            _latest = await _orchestrator.AcceptFinalTurnAsync(_meeting, turn);
            Render(_latest);
            StatusText.Text = "Assistance ready.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Assistant unavailable — manual transcript is still local.";
            MessageBox.Show(ex.Message, "Assistant error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            AssistButton.IsEnabled = true;
        }
    }

    private void Render(AssistantResponse response)
    {
        SayText.Text = response.Say ?? "—";
        WatchText.Text = response.Watch ?? "—";
        AskText.Text = response.Ask ?? "—";
        SourcesText.Text = response.Sources.Count == 0
            ? "—"
            : string.Join(Environment.NewLine, response.Sources.Select(x => $"{x.SourceName}{(x.Locator is null ? string.Empty : " — " + x.Locator)}"));
        _overlay?.Render(response);
    }

    private void ShowOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (_overlay is null || !_overlay.IsLoaded)
        {
            _overlay = new OverlayWindow();
            _overlay.Closed += (_, _) => _overlay = null;
            _overlay.Show();
        }
        _overlay.Render(_latest);
    }

    private async Task RefreshDocumentCountAsync()
    {
        var documentsTask = _repository.GetDocumentsAsync();
        var factsTask = _repository.GetFactsAsync();
        await Task.WhenAll(documentsTask, factsTask);
        var documents = await documentsTask;
        var facts = await factsTask;
        DocumentCountText.Text = $"{documents.Count} source document(s); {facts.Count} context/fact record(s) stored locally";
    }

    private sealed class LiveSession(
        LiveMeetingCoordinator coordinator,
        IAudioCaptureSource remoteAudio,
        IAudioCaptureSource userAudio,
        IRealtimeTranscriber hrTranscriber,
        IRealtimeTranscriber userTranscriber) : IAsyncDisposable
    {
        private int _disposeStarted;

        public LiveMeetingCoordinator Coordinator { get; } = coordinator;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
            Exception? firstError = null;
            try { await Coordinator.DisposeAsync(); } catch (Exception ex) { firstError = ex; }
            try { await remoteAudio.DisposeAsync(); } catch (Exception ex) { firstError ??= ex; }
            try { await userAudio.DisposeAsync(); } catch (Exception ex) { firstError ??= ex; }
            try { await hrTranscriber.DisposeAsync(); } catch (Exception ex) { firstError ??= ex; }
            try { await userTranscriber.DisposeAsync(); } catch (Exception ex) { firstError ??= ex; }
            if (firstError is not null) throw firstError;
        }
    }
}
