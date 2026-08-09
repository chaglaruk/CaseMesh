using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HRCompanion.Core.Abstractions;
using HRCompanion.Core.Models;
using Microsoft.Extensions.Options;

namespace HRCompanion.Infrastructure.OpenAI;

public sealed class OpenAiMeetingAiService : IMeetingAiService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly IApiKeyStore _keys;
    private readonly OpenAiOptions _options;

    public OpenAiMeetingAiService(HttpClient http, IApiKeyStore keys, IOptions<OpenAiOptions> options)
    {
        _http = http;
        _keys = keys;
        _options = options.Value;
    }

    public async Task<MeetingAnalysis> AnalyzeTurnAsync(MeetingState state, TranscriptTurn latestHrTurn, CancellationToken cancellationToken = default)
    {
        var schema = new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                intent = new { type = "string", @enum = Enum.GetNames<MeetingIntent>() },
                importance = new { type = "string", @enum = Enum.GetNames<AssistantImportance>() },
                needsAssistant = new { type = "boolean" },
                potentialCommitment = new { type = "boolean" },
                potentialWrittenFollowUp = new { type = "boolean" },
                retrievalTerms = new { type = "array", items = new { type = "string" }, maxItems = 12 }
            },
            required = new[] { "intent", "importance", "needsAssistant", "potentialCommitment", "potentialWrittenFollowUp", "retrievalTerms" }
        };

        var json = await SendStructuredAsync(
            _options.FastModel,
            MeetingPromptBuilder.AnalysisInstructions,
            MeetingPromptBuilder.BuildAnalysisInput(state, latestHrTurn),
            "meeting_analysis",
            schema,
            reasoningEffort: "none",
            cancellationToken).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Enum.TryParse<MeetingIntent>(root.GetProperty("intent").GetString(), true, out var intent);
        Enum.TryParse<AssistantImportance>(root.GetProperty("importance").GetString(), true, out var importance);
        var terms = root.GetProperty("retrievalTerms").EnumerateArray()
            .Select(x => x.GetString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToArray();
        return new(
            intent,
            importance,
            root.GetProperty("needsAssistant").GetBoolean(),
            root.GetProperty("potentialCommitment").GetBoolean(),
            root.GetProperty("potentialWrittenFollowUp").GetBoolean(),
            terms);
    }

    public async Task<AssistantResponse> CreateAssistantResponseAsync(
        MeetingState state,
        TranscriptTurn latestHrTurn,
        MeetingAnalysis analysis,
        IReadOnlyList<CaseFact> facts,
        IReadOnlyList<EvidenceSnippet> evidence,
        CancellationToken cancellationToken = default)
    {
        var allowedIds = evidence.Select(x => x.EvidenceId).ToHashSet(StringComparer.Ordinal);
        object sourceIdItemSchema;
        if (allowedIds.Count == 0)
        {
            sourceIdItemSchema = new { type = "string" };
        }
        else
        {
            sourceIdItemSchema = new { type = "string", @enum = allowedIds.OrderBy(x => x, StringComparer.Ordinal).ToArray() };
        }
        var schema = new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                intent = new { type = "string", @enum = Enum.GetNames<MeetingIntent>() },
                importance = new { type = "string", @enum = Enum.GetNames<AssistantImportance>() },
                say = new { type = new[] { "string", "null" }, maxLength = 700 },
                watch = new { type = new[] { "string", "null" }, maxLength = 350 },
                ask = new { type = new[] { "string", "null" }, maxLength = 350 },
                needsWrittenFollowUp = new { type = "boolean" },
                confidence = new { type = "number", minimum = 0, maximum = 1 },
                sourceIds = new { type = "array", items = sourceIdItemSchema, maxItems = Math.Min(8, allowedIds.Count) }
            },
            required = new[] { "intent", "importance", "say", "watch", "ask", "needsWrittenFollowUp", "confidence", "sourceIds" }
        };

        var json = await SendStructuredAsync(
            _options.AnswerModel,
            MeetingPromptBuilder.SpokenStyle,
            MeetingPromptBuilder.BuildAnswerInput(state, latestHrTurn, analysis, facts, evidence),
            "meeting_assistance",
            schema,
            reasoningEffort: "none",
            cancellationToken).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Enum.TryParse<MeetingIntent>(root.GetProperty("intent").GetString(), true, out var intent);
        Enum.TryParse<AssistantImportance>(root.GetProperty("importance").GetString(), true, out var importance);
        var sourceIds = root.GetProperty("sourceIds").EnumerateArray()
            .Select(x => x.GetString())
            .Where(x => x is not null && allowedIds.Contains(x))
            .Select(x => x!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var sourceMap = evidence.ToDictionary(x => x.EvidenceId, StringComparer.Ordinal);
        var sources = sourceIds.Select(id => sourceMap[id])
            .Select(x => new AssistantSource(x.EvidenceId, x.SourceName, x.SourceLocator))
            .ToArray();

        return new(
            intent,
            importance,
            GetNullableString(root, "say"),
            GetNullableString(root, "watch"),
            GetNullableString(root, "ask"),
            root.GetProperty("needsWrittenFollowUp").GetBoolean(),
            root.GetProperty("confidence").GetDouble(),
            sources);
    }

    private async Task<string> SendStructuredAsync(
        string model,
        string instructions,
        string input,
        string schemaName,
        object schema,
        string reasoningEffort,
        CancellationToken cancellationToken)
    {
        var key = await _keys.GetAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("OpenAI API key is not configured.");
        }

        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["instructions"] = instructions,
            ["input"] = input,
            ["reasoning"] = new { effort = reasoningEffort },
            ["text"] = new
            {
                verbosity = "low",
                format = new
                {
                    type = "json_schema",
                    name = schemaName,
                    strict = true,
                    schema
                }
            }
        };
        body["service_tier"] = _options.ServiceTier;
        body["safety_identifier"] = "hrcompanion-local-user";

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl.TrimEnd('/')}/responses")
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"OpenAI Responses API returned HTTP {(int)response.StatusCode}. Response content was suppressed to protect meeting data.",
                null,
                response.StatusCode);
        }
        return ExtractOutputText(payload);
    }

    internal static string ExtractOutputText(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (doc.RootElement.TryGetProperty("output_text", out var shortcut) && shortcut.ValueKind == JsonValueKind.String)
        {
            return shortcut.GetString()!;
        }

        if (doc.RootElement.TryGetProperty("output", out var output))
        {
            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("content", out var content)) continue;
                foreach (var part in content.EnumerateArray())
                {
                    if (part.TryGetProperty("type", out var type) && type.GetString() == "output_text" && part.TryGetProperty("text", out var text))
                    {
                        return text.GetString() ?? string.Empty;
                    }
                }
            }
        }
        throw new InvalidDataException("Responses API payload did not contain output_text.");
    }

    private static string? GetNullableString(JsonElement root, string name)
    {
        var element = root.GetProperty(name);
        return element.ValueKind == JsonValueKind.Null ? null : element.GetString();
    }
}
