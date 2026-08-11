using System.Windows;
using HRCompanion.Core.Models;
using Microsoft.Win32;

namespace HRCompanion.App;

public partial class MainWindow
{
    private async void ImportRestricted_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Supported files|*.pdf;*.docx;*.eml;*.txt;*.md;*.html;*.htm|All files|*.*",
            Title = "Import restricted ACAS / Without Prejudice material"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            StatusText.Text = "Importing restricted ACAS / Without Prejudice material...";
            var result = await _importer.ImportPathsAsync(
                dialog.FileNames,
                DocumentImportOptions.RestrictedAcasWithoutPrejudice);
            StatusText.Text = $"Restricted import: imported {result.Imported}; duplicates {result.SkippedDuplicate}; errors {result.Errors.Count}. Normal HR retrieval will not use these sources.";
            if (result.Errors.Count > 0)
                MessageBox.Show(string.Join(Environment.NewLine, result.Errors.Take(10)), "Some restricted files could not be imported", MessageBoxButton.OK, MessageBoxImage.Warning);
            await RefreshPreflightAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Restricted import failed";
            MessageBox.Show(ex.Message, "Restricted import failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ReviewSources_Click(object sender, RoutedEventArgs e)
    {
        var window = new CaseBrainWindow(_repository) { Owner = this };
        window.ShowDialog();
        await RefreshPreflightAsync();
    }
}
