using HRCompanion.Infrastructure.OpenAI;

namespace HRCompanion.Infrastructure.Tests;

public sealed class HealthCapabilityPromptContractTests
{
    [Fact]
    public void Prompt_SeparatesCurrentFitnessFromIntentToReturn()
    {
        Assert.Contains("Never collapse “currently not fit” into “does not intend to return”", MeetingPromptBuilder.SpokenStyle, StringComparison.Ordinal);
        Assert.Contains("do not invent or promise a return date", MeetingPromptBuilder.SpokenStyle, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("temporary medical evidence", MeetingPromptBuilder.SpokenStyle, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Prompt_ProtectsCapabilityAnswersWithoutInventingProcedure()
    {
        Assert.Contains("do not casually accept the premise", MeetingPromptBuilder.SpokenStyle, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("what medical evidence will be reviewed", MeetingPromptBuilder.SpokenStyle, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not invent procedural rights or stages", MeetingPromptBuilder.SpokenStyle, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Prompt_AnswersConnectedQuestionsAndKeepsAcasRestricted()
    {
        Assert.Contains("answer all of them briefly", MeetingPromptBuilder.SpokenStyle, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not authorise you to volunteer settlement figures", MeetingPromptBuilder.SpokenStyle, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NEXT must not be used for a question HR has already asked", MeetingPromptBuilder.SpokenStyle, StringComparison.OrdinalIgnoreCase);
    }
}
