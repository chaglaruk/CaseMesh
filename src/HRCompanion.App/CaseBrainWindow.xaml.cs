using System.Windows;
using HRCompanion.Core.Abstractions;
using HRCompanion.Core.Models;

namespace HRCompanion.App;

public partial class CaseBrainWindow : Window
{
    private readonly ICaseRepository _repository;

    public CaseBrainWindow(ICaseRepository repository)
    {
        InitializeComponent();
        _repository = repository;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private SourceRow? Selected => SourcesList.SelectedItem as SourceRow;

    private async Task RefreshAsync()
    {
        var documents = await _repository.GetDocumentsAsync();
        SourcesList.ItemsSource = documents
            .OrderBy(document => document.Channel)
            .ThenBy(document => document.Authority)
            .ThenBy(document => document.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(document => new SourceRow(document))
            .ToArray();
        StatusText.Text = $"{documents.Count} source document(s). Existing sources migrated as ORDINARY + CURRENT until you reclassify them here.";
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void MarkOrdinary_Click(object sender, RoutedEventArgs e) =>
        await ReclassifyAsync(EvidenceChannel.OrdinaryHr, null);

    private async void MarkRestricted_Click(object sender, RoutedEventArgs e) =>
        await ReclassifyAsync(EvidenceChannel.AcasWithoutPrejudice, null);

    private async void MarkCurrent_Click(object sender, RoutedEventArgs e) =>
        await ReclassifyAsync(null, EvidenceAuthority.CurrentFinal);

    private async void MarkHistorical_Click(object sender, RoutedEventArgs e) =>
        await ReclassifyAsync(null, EvidenceAuthority.Historical);

    private async Task ReclassifyAsync(EvidenceChannel? channel, EvidenceAuthority? authority)
    {
        var selected = Selected;
        if (selected is null)
        {
            StatusText.Text = "Select a source first.";
            return;
        }

        var document = selected.Document;
        await _repository.UpdateDocumentClassificationAsync(
            document.Id,
            channel ?? document.Channel,
            authority ?? document.Authority);
        await RefreshAsync();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var selected = Selected;
        if (selected is null)
        {
            StatusText.Text = "Select a source first.";
            return;
        }

        if (MessageBox.Show(
                $"Delete this source and its searchable chunks from Case Brain?\n\n{selected.Document.DisplayName}",
                "Delete Case Brain source",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await _repository.DeleteDocumentAsync(selected.Document.Id);
        await RefreshAsync();
    }

    private sealed record SourceRow(DocumentRecord Document)
    {
        public string Display =>
            $"[{(Document.Channel == EvidenceChannel.OrdinaryHr ? "ORDINARY" : "ACAS/WP")}] " +
            $"[{(Document.Authority == EvidenceAuthority.CurrentFinal ? "CURRENT" : "HISTORICAL")}] " +
            Document.DisplayName;
    }
}
