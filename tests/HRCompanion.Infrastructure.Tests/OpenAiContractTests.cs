using HRCompanion.Infrastructure.Data;
using HRCompanion.Infrastructure.OpenAI;

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
}
