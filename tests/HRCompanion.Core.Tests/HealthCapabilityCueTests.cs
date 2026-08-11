using HRCompanion.Core.Models;
using HRCompanion.Core.Services;

namespace HRCompanion.Core.Tests;

public sealed class HealthCapabilityCueTests
{
    private readonly DeterministicCueEngine _engine = new();

    [Fact]
    public void ResumingCurrentRole_RetrievesMedicalAndSafeReturnEvidence()
    {
        var analysis = _engine.Analyze("When will you be resuming your role as Breakfast Manager?");

        Assert.True(analysis.NeedsAssistant);
        Assert.Contains(analysis.RetrievalTerms, term => term.Equals("fit note", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(analysis.RetrievalTerms, term => term.Equals("Occupational Health", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(analysis.RetrievalTerms, term => term.Equals("safe return", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConditionalCapabilityDismissal_IsHighRiskAndRetrievesSupportOptions()
    {
        var analysis = _engine.Analyze("If you do not intend to return, we may begin a capability process that could result in dismissal. What is your position?");

        Assert.Equal(AssistantImportance.High, analysis.Importance);
        Assert.True(analysis.PotentialCommitment);
        Assert.Contains(analysis.RetrievalTerms, term => term.Equals("health related capability", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(analysis.RetrievalTerms, term => term.Equals("reasonable adjustments", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(analysis.RetrievalTerms, term => term.Equals("redeployment", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LatestFitNote_RetrievesCurrentMedicalEvidence()
    {
        var analysis = _engine.Analyze("What does your latest fit note say about whether you are fit for work?");

        Assert.Equal(MeetingIntent.Question, analysis.Intent);
        Assert.Contains(analysis.RetrievalTerms, term => term.Equals("not fit for work", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(analysis.RetrievalTerms, term => term.Equals("medical advice", StringComparison.OrdinalIgnoreCase));
    }
}
