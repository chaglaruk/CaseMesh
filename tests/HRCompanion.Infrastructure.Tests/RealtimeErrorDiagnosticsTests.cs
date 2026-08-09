using HRCompanion.Infrastructure.OpenAI;

namespace HRCompanion.Infrastructure.Tests;

public sealed class RealtimeErrorDiagnosticsTests
{
    [Fact]
    public void ErrorEvent_IncludesSafeParameterPathWithoutRawMessage()
    {
        var parser = new RealtimeTranscriptionEventParser();
        var result = parser.Parse(
            """{"type":"error","error":{"type":"invalid_request_error","code":"invalid_value","param":"session.audio.input.turn_detection.type","message":"sensitive free-form server text"}}""",
            DateTimeOffset.UtcNow);

        Assert.NotNull(result.Error);
        Assert.Equal("invalid_request_error", result.Error!.Type);
        Assert.Equal("invalid_value@param=session.audio.input.turn_detection.type", result.Error.Code);
        Assert.DoesNotContain("sensitive", result.Error.Code, StringComparison.OrdinalIgnoreCase);
    }
}
