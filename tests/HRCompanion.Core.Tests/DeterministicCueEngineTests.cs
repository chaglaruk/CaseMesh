using HRCompanion.Core.Models;
using HRCompanion.Core.Services;

namespace HRCompanion.Core.Tests;

public sealed class DeterministicCueEngineTests
{
    private readonly DeterministicCueEngine _sut = new();

    [Fact]
    public void ConfirmStartDate_IsCommitmentRequest()
    {
        var result = _sut.Analyze("Can you confirm today that you will start on Monday?");
        Assert.Equal(MeetingIntent.CommitmentRequest, result.Intent);
        Assert.True(result.PotentialCommitment);
        Assert.True(result.NeedsAssistant);
        Assert.Equal(AssistantImportance.High, result.Importance);
    }

    [Fact]
    public void WrittenFollowUp_IsCapturedWithoutForcingSpokenAnswer()
    {
        var result = _sut.Analyze("We'll check that and get back to you in writing.");
        Assert.Equal(MeetingIntent.Information, result.Intent);
        Assert.True(result.PotentialWrittenFollowUp);
    }

    [Fact]
    public void DirectQuestion_NeedsAssistant()
    {
        var result = _sut.Analyze("What would make an alternative role suitable for you?");
        Assert.Equal(MeetingIntent.Question, result.Intent);
        Assert.True(result.NeedsAssistant);
        Assert.Contains(result.RetrievalTerms, x => x.Equals("alternative", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AlternativeRole_ExpandsLocalHrRetrievalAliasesWithoutExtraModelCall()
    {
        var result = _sut.Analyze("Why haven't the alternative roles been suitable for you?");

        Assert.Contains(result.RetrievalTerms, x => x.Equals("redeployment", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.RetrievalTerms, x => x.Equals("Occupational Health", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VacancyQuestion_ExpandsRedeploymentAliases()
    {
        var result = _sut.Analyze("Why do you say applying for vacancies isn't enough support?");

        Assert.Equal(MeetingIntent.Question, result.Intent);
        Assert.Contains(result.RetrievalTerms, x => x.Equals("redeployment", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.RetrievalTerms, x => x.Equals("internal application", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CspQuestion_ExpandsSickPayAliases()
    {
        var result = _sut.Analyze("What is still unresolved about CSP and payroll deductions?");

        Assert.Equal(MeetingIntent.Question, result.Intent);
        Assert.Contains(result.RetrievalTerms, x => x.Equals("company sick pay", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.RetrievalTerms, x => x.Equals("service band", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.RetrievalTerms, x => x.Equals("entitlement", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.RetrievalTerms, x => x.Equals("payroll", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.RetrievalTerms, x => x.Equals("reconciliation", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.RetrievalTerms, x => x.Equals("payment discrepancy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RefusalToReturnQuestion_ExpandsSafeReturnAliases()
    {
        var result = _sut.Analyze("Are you refusing to return to the same site?");

        Assert.Equal(MeetingIntent.Question, result.Intent);
        Assert.Contains(result.RetrievalTerms, x => x.Equals("return", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.RetrievalTerms, x => x.Equals("safe return", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.RetrievalTerms, x => x.Equals("not refusing", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.RetrievalTerms, x => x.Equals("same environment", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.RetrievalTerms, x => x.Equals("Occupational Health", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.RetrievalTerms, x => x.Equals("reporting line", StringComparison.OrdinalIgnoreCase));
    }
}
