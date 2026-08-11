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
    public void EmbeddedQuestionWithFollowingStatements_NeedsAssistant()
    {
        var result = _sut.Analyze(
            "I want to cover a few points about your absence. What adjustment are you asking us to consider? " +
            "We also need to discuss how the process will work from here.");

        Assert.Equal(MeetingIntent.Question, result.Intent);
        Assert.True(result.NeedsAssistant);
        Assert.Contains(result.RetrievalTerms, x => x.Equals("reasonable adjustments", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EmbeddedQuestionWithoutQuestionMark_IsStillDetectedAtSentenceBoundary()
    {
        var result = _sut.Analyze(
            "There are several things I want to cover. Can you explain why you cannot return to the same role. " +
            "After that I will explain the next steps.");

        Assert.Equal(MeetingIntent.Question, result.Intent);
        Assert.True(result.NeedsAssistant);
        Assert.Contains(result.RetrievalTerms, x => x.Equals("same role", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FilledPauseBeforeDirectQuestion_IsStillDetected()
    {
        var result = _sut.Analyze(
            "Um, well, can you, uh, explain why you don't feel able to return to your current role");

        Assert.Equal(MeetingIntent.Question, result.Intent);
        Assert.True(result.NeedsAssistant);
    }

    [Fact]
    public void ConversationalIndirectQuestionWithFillers_IsStillDetected()
    {
        var result = _sut.Analyze(
            "I've reviewed the notes and I was just wondering, um, if you could explain why the current role still feels difficult. " +
            "We can come back to the fit note afterwards.");

        Assert.Equal(MeetingIntent.Question, result.Intent);
        Assert.True(result.NeedsAssistant);
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
        Assert.Contains(result.RetrievalTerms, x => x.Equals("payslip", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.RetrievalTerms, x => x.Equals("employer letter", StringComparison.OrdinalIgnoreCase));
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
