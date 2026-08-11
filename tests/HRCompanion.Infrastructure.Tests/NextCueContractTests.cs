using System.Net;
using System.Text;
using HRCompanion.Core.Abstractions;
using HRCompanion.Core.Models;
using HRCompanion.Infrastructure.OpenAI;
using Microsoft.Extensions.Options;

namespace HRCompanion.Infrastructure.Tests;

public sealed class NextCueContractTests
{
    [Fact]
    public async Task SignpostedUpcomingTopic_CanReturnNextCueWithoutPollutingSay()
    {
        var payload = """
            {"output_text":"{\"intent\":\"Question\",\"importance\":\"Normal\",\"say\":\"I don't currently feel able to return to the same set-up without a safe plan and suitable changes.\",\"next\":\"Fit note is likely next — wait for the specific question and be ready to explain the current note.\",\"watch\":null,\"ask\":null,\"needsWrittenFollowUp\":false,\"confidence\":0.9,\"sourceIds\":[]}"}
            """;
        var handler = new StaticResponseHandler(payload);
        using var http = new HttpClient(handler);
        var service = new OpenAiMeetingAiService(http, new StaticKeyStore(), Options.Create(new OpenAiOptions()));
        var meetingId = Guid.NewGuid();
        var state = new MeetingState(meetingId, "Synthetic", DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var turn = TranscriptTurn.Final(
            meetingId,
            SpeakerRole.Hr,
            "We've reviewed your absence. Can you explain why you don't feel able to return to your current role? We also need to talk about your fit note.",
            now,
            now,
            "synthetic");

        var response = await service.CreateAssistantResponseAsync(
            state,
            turn,
            new(MeetingIntent.Question, AssistantImportance.Normal, true, false, false, ["return", "fit note"]),
            [],
            []);

        Assert.Contains("return", response.Say!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fit note", response.Say!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Fit note", response.Next!, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(handler.RequestBody);
        Assert.Contains("\"next\"", handler.RequestBody!, StringComparison.Ordinal);
        Assert.Contains("private preparation cue", handler.RequestBody!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Prompt_DistinguishesSayNowFromMerelySignpostedNextTopic()
    {
        Assert.Contains("CURRENT ANSWER VS WHAT MAY COME NEXT", MeetingPromptBuilder.SpokenStyle, StringComparison.Ordinal);
        Assert.Contains("not something the user should automatically speak", MeetingPromptBuilder.SpokenStyle, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not encourage the user to volunteer unnecessary detail", MeetingPromptBuilder.SpokenStyle, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StaticKeyStore : IApiKeyStore
    {
        public Task<string?> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("synthetic-key");
        public Task SaveAsync(string apiKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StaticResponseHandler(string payload) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        }
    }
}
