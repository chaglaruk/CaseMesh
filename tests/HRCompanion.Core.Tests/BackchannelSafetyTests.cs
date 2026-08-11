using System.Reflection;
using HRCompanion.Core.Services;

namespace HRCompanion.Core.Tests;

public sealed class BackchannelSafetyTests
{
    [Theory]
    [InlineData("yeah")]
    [InlineData("Mm-hmm.")]
    [InlineData("right")]
    [InlineData("I see")]
    [InlineData("thank you")]
    public void ShortAcknowledgements_AreBackchannels(string text)
    {
        Assert.True(InvokeClassifier(text));
    }

    [Theory]
    [InlineData("Right, can you explain why you cannot return?")]
    [InlineData("Yeah, but what adjustment are you asking for?")]
    [InlineData("I see that you have another fit note and we need to discuss it.")]
    public void SubstantiveHrTurns_AreNotBackchannels(string text)
    {
        Assert.False(InvokeClassifier(text));
    }

    private static bool InvokeClassifier(string text)
    {
        var method = typeof(LiveMeetingCoordinator).GetMethod(
            "IsLikelyBackchannel",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<bool>(method!.Invoke(null, [text]));
    }
}
