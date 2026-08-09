using System.Windows;
using HRCompanion.Core.Abstractions;
using HRCompanion.Core.Models;
using HRCompanion.Core.Services;
using Microsoft.Win32;

namespace HRCompanion.App;

public partial class MainWindow : Window
{
    private readonly ICaseRepository _repository;
    private readonly IDocumentImporter _importer;
    private readonly IContextImporter _contextImporter;
    private readonly IApiKeyStore _keyStore;
    private readonly MeetingAssistantOrchestrator _orchestrator;
    private readonly MeetingState _meeting = new(Guid.NewGuid(), "HR Case", DateTimeOffset.UtcNow);
    private OverlayWindow? _overlay;
    private AssistantResponse _latest = AssistantResponse.NoAction();

    public MainWindow(ICaseRepository repository, IDocumentImporter importer, IContextImporter contextImporter, IApiKeyStore keyStore, MeetingAssistantOrchestrator orchestrator)
    {
        InitializeComponent();
        _repository = repository;
        _importer = importer;
        _contextImporter = contextImporter;
        _keyStore = keyStore;
        _orchestrator = orchestrator;
        Loaded += async (_, _) => await RefreshDocumentCountAsync();
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
}
