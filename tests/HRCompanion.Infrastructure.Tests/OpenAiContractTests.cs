using System.Text.Json;
using HRCompanion.Core.Abstractions;
using HRCompanion.Core.Models;
using HRCompanion.Core.Services;
using HRCompanion.Infrastructure.Data;
using HRCompanion.Infrastructure.OpenAI;
using HRCompanion.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace HRCompanion.Infrastructure.Tests;

public sealed class OpenAiContractTests
{
    [Fact]
    public void ExtractOutputText_ReadsResponsesApiContentShape()
    {
        const string payload = """
            {
              "output": [
                {
                  "type": "message",
                  "content": [
                    { "type": "output_text", "text": "{\"say\":\"Short answer\"}" }
                  ]
                }
              ]
            }
            """;

        var text = OpenAiMeetingAiService.ExtractOutputText(payload);

        Assert.Contains("Short answer", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FtsQuery_QuotesAndLimitsUserTerms()
    {
        var query = SqliteCaseRepository.ToFtsQuery("Occupational Health redeployment; confirm Monday?");

        Assert.Contains("\"Occupational\"", query, StringComparison.Ordinal);
        Assert.Contains("\"redeployment\"", query, StringComparison.Ordinal);
        Assert.DoesNotContain(";", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiveHrExchangeCorpus_WhenExplicitlyEnabled_AvoidsKnownUnsafePhrases()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("HRCOMPANION_RUN_LIVE_EVALS"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        IApiKeyStore keyStore = string.IsNullOrWhiteSpace(apiKey)
            ? new WindowsCredentialApiKeyStore()
            : new EnvironmentApiKeyStore(apiKey);
        var configuredKey = await keyStore.GetAsync();
        Assert.False(
            string.IsNullOrWhiteSpace(configuredKey),
            "Save the API key in HR Companion's Windows Credential Manager entry or set OPENAI_API_KEY before running live evals.");

        var options = new OpenAiOptions();
        var modelOverride = Environment.GetEnvironmentVariable("HRCOMPANION_ANSWER_MODEL");
        if (!string.IsNullOrWhiteSpace(modelOverride)) options.AnswerModel = modelOverride.Trim();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var service = new OpenAiMeetingAiService(http, keyStore, Options.Create(options));
        var cueEngine = new DeterministicCueEngine();
        var path = Path.Combine(AppContext.BaseDirectory, "evals", "hr-exchanges.json");
        var cases = JsonSerializer.Deserialize<LiveEvalCase[]>(
            File.ReadAllText(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(cases);
        Assert.NotEmpty(cases!);

        foreach (var item in cases!)
        {
            var analysis = cueEngine.Analyze(item.Hr);
            if (!analysis.NeedsAssistant) continue;

            var meetingId = Guid.NewGuid();
            var state = new MeetingState(meetingId, $"Synthetic live eval: {item.Id}", DateTimeOffset.UtcNow);
            var now = DateTimeOffset.UtcNow;
            var turn = TranscriptTurn.Final(meetingId, SpeakerRole.Hr, item.Hr, now, now, "synthetic-live-eval");
            state.AddTurn(turn);

            var response = await service.CreateAssistantResponseAsync(state, turn, analysis, [], []);
            var say = response.Say ?? string.Empty;
            var wordCount = say.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

            Assert.True(
                wordCount <= item.MaxSayWords,
                $"{item.Id}: SAY exceeded {item.MaxSayWords} words. Actual: {wordCount}. SAY: {say}");
            Assert.Empty(response.Sources);

            foreach (var forbidden in item.ForbiddenSayPhrases)
            {
                Assert.DoesNotContain(forbidden, say, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private sealed record LiveEvalCase(
        string Id,
        string Hr,
        string[] ForbiddenSayPhrases,
        int MaxSayWords);

    private sealed class EnvironmentApiKeyStore(string apiKey) : IApiKeyStore
    {
        public Task SaveAsync(string value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(apiKey);
        public Task DeleteAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
