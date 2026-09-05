using System.Text.Json;
using CaseMesh.Core.Abstractions;
using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Infrastructure.Data;
using CaseMesh.Infrastructure.OpenAI;
using CaseMesh.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace CaseMesh.Infrastructure.Tests;

public sealed class OpenAiContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
    public void RealtimeTranscriptionContract_UsesTranscriptionIntentAndVadCompatibleModel()
    {
        var options = new OpenAiOptions();

        var uri = OpenAiRealtimeTranscriber.BuildConnectionUri(options);
        var update = JsonSerializer.SerializeToElement(OpenAiRealtimeTranscriber.CreateSessionUpdate(options));
        var input = update.GetProperty("session").GetProperty("audio").GetProperty("input");
        var transcription = input.GetProperty("transcription");

        Assert.Equal("wss://api.openai.com/v1/realtime?intent=transcription", uri.AbsoluteUri);
        Assert.Equal("transcription", update.GetProperty("session").GetProperty("type").GetString());
        Assert.Equal("gpt-4o-mini-transcribe", transcription.GetProperty("model").GetString());
        Assert.Equal("en", transcription.GetProperty("language").GetString());
        Assert.Equal("server_vad", input.GetProperty("turn_detection").GetProperty("type").GetString());
        Assert.False(transcription.TryGetProperty("languages", out _));
        Assert.False(transcription.TryGetProperty("keywords", out _));
        Assert.False(transcription.TryGetProperty("delay", out _));
    }

    [Fact]
    public void ParseServerMessage_PreservesActionableErrorWithoutWholePayload()
    {
        const string payload = """
            {
              "type": "error",
              "error": {
                "type": "invalid_request_error",
                "code": "invalid_model",
                "param": "model",
                "message": "The selected model cannot start this session."
              },
              "unrelated": "must not be included"
            }
            """;

        var serverEvent = OpenAiRealtimeTranscriber.ParseServerMessage(payload);

        Assert.Equal("error", serverEvent.Type);
        Assert.Contains("code=invalid_model", serverEvent.Error, StringComparison.Ordinal);
        Assert.Contains("parameter=model", serverEvent.Error, StringComparison.Ordinal);
        Assert.Contains("cannot start", serverEvent.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("unrelated", serverEvent.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ItemTranscriptionFailure_IsReportedWithoutSuppressingLaterCompletion()
    {
        await using var transcriber = new OpenAiRealtimeTranscriber(
            SpeakerRole.User,
            new EnvironmentApiKeyStore("unused"),
            Options.Create(new OpenAiOptions()));
        var failures = new List<Exception>();
        var updates = new List<TranscriptionUpdate>();
        transcriber.Failed += (_, exception) => failures.Add(exception);
        transcriber.Updated += (_, update) => updates.Add(update);

        transcriber.ProcessServerMessage("""
            {
              "type": "conversation.item.input_audio_transcription.failed",
              "error": { "code": "audio_unintelligible", "message": "Could not transcribe this item." }
            }
            """);
        transcriber.ProcessServerMessage("""
            {
              "type": "conversation.item.input_audio_transcription.completed",
              "item_id": "later-item",
              "transcript": "A later turn still succeeds."
            }
            """);

        Assert.Single(failures);
        var update = Assert.Single(updates);
        Assert.True(update.IsFinal);
        Assert.Equal("later-item", update.ItemId);
        Assert.Equal("A later turn still succeeds.", update.Text);
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
    public void CommitmentEvalCases_HaveExecutableSayConstraints()
    {
        var cases = LoadLiveEvalCases();
        var commitmentCases = cases.Where(item => item.ExpectedPotentialCommitment).ToArray();

        Assert.NotEmpty(commitmentCases);
        foreach (var item in commitmentCases)
        {
            Assert.NotEmpty(item.ForbiddenSayPhrases);
            Assert.InRange(item.MaxSayWords, 1, 60);
        }
    }

    [Fact]
    public async Task LiveHrExchangeCorpus_WhenExplicitlyEnabled_AvoidsKnownUnsafePhrases()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CASEMESH_RUN_LIVE_EVALS"), "1", StringComparison.Ordinal))
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
            "Save the API key in CaseMesh's Windows Credential Manager entry or set OPENAI_API_KEY before running live evals.");

        var options = new OpenAiOptions();
        var modelOverride = Environment.GetEnvironmentVariable("CASEMESH_ANSWER_MODEL");
        if (!string.IsNullOrWhiteSpace(modelOverride)) options.AnswerModel = modelOverride.Trim();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var service = new OpenAiMeetingAiService(http, keyStore, Options.Create(options));
        var cueEngine = new DeterministicCueEngine();
        var cases = LoadLiveEvalCases();

        foreach (var item in cases)
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

    private static LiveEvalCase[] LoadLiveEvalCases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "evals", "hr-exchanges.json");
        var cases = JsonSerializer.Deserialize<LiveEvalCase[]>(File.ReadAllText(path), JsonOptions);
        Assert.NotNull(cases);
        Assert.NotEmpty(cases!);
        return cases!;
    }

    private sealed record LiveEvalCase(
        string Id,
        string Hr,
        bool ExpectedPotentialCommitment,
        string[] ForbiddenSayPhrases,
        int MaxSayWords);

    private sealed class EnvironmentApiKeyStore(string apiKey) : IApiKeyStore
    {
        public Task SaveAsync(string value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(apiKey);
        public Task DeleteAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
