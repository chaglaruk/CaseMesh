using System.Net.Http;
using System.Windows;
using HRCompanion.Core.Services;
using HRCompanion.Infrastructure.Data;
using HRCompanion.Infrastructure.Documents;
using HRCompanion.Infrastructure.OpenAI;
using HRCompanion.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace HRCompanion.App;

public partial class App : Application
{
    private HttpClient? _httpClient;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var paths = new AppPaths();
            var repository = new SqliteCaseRepository(paths);
            await repository.InitializeAsync();
            var importer = new DocumentImporter(repository);
            var contextImporter = new WorkingContextImporter(repository);
            var keyStore = new WindowsCredentialApiKeyStore();
            var options = Options.Create(new OpenAiOptions());
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var ai = new OpenAiMeetingAiService(_httpClient, keyStore, options);
            var orchestrator = new MeetingAssistantOrchestrator(repository, ai, new DeterministicCueEngine());

            var window = new MainWindow(repository, importer, contextImporter, keyStore, orchestrator);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "HR Companion failed to start", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _httpClient?.Dispose();
        base.OnExit(e);
    }
}
