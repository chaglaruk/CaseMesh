using System.Text.Json;

namespace CaseMesh.Qa;

public sealed record MatterQaEvaluationCase(
    string Name,
    MatterQaAnswer Answer,
    bool ExpectedInsufficientEvidence,
    IReadOnlyList<string> RequiredTerms);

public sealed record MatterQaEvaluationReport(
    int Cases,
    int PassedCases,
    int SourceBackedClaims,
    int ValidSourceBackedClaims,
    decimal CitationValidityPercent,
    int ProhibitedOutputCount,
    bool TenantIsolationPassed,
    bool Passed)
{
    public string ToDeterministicJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    });
}

public static class MatterQaEvaluation
{
    private static readonly string[] ProhibitedTerms =
    ["liable", "win probability", "compensation estimate", "statutory deadline", "you should file"];

    public static MatterQaEvaluationReport Evaluate(
        IReadOnlyList<MatterQaEvaluationCase> cases,
        bool tenantIsolationPassed)
    {
        ArgumentNullException.ThrowIfNull(cases);
        var sourceClaims = cases.SelectMany(item => item.Answer.Claims)
            .Where(item => item.Kind == MatterClaimKind.Evidence).ToArray();
        var validClaims = sourceClaims.Count(item => item.CitationResultIds.Count > 0 &&
            item.CitationResultIds.All(id => cases.SelectMany(entry => entry.Answer.Citations)
                .Any(citation => citation.RetrievalResultId == id)));
        var prohibited = cases.Sum(item => ProhibitedTerms.Count(term =>
            (item.Answer.Summary + " " + string.Join(' ', item.Answer.Claims.Select(claim => claim.Text)))
            .Contains(term, StringComparison.OrdinalIgnoreCase)));
        var passedCases = cases.Count(item =>
            (item.ExpectedInsufficientEvidence == (item.Answer.Status == MatterAnswerStatus.InsufficientEvidence)) &&
            item.RequiredTerms.All(term =>
                (item.Answer.Summary + " " + string.Join(' ', item.Answer.Claims.Select(claim => claim.Text)))
                .Contains(term, StringComparison.OrdinalIgnoreCase)));
        var validity = sourceClaims.Length == 0 ? 100m : decimal.Round(validClaims * 100m / sourceClaims.Length, 4);
        var passed = cases.Count > 0 && passedCases == cases.Count && validity == 100m && prohibited == 0 && tenantIsolationPassed;
        return new MatterQaEvaluationReport(cases.Count, passedCases, sourceClaims.Length, validClaims,
            validity, prohibited, tenantIsolationPassed, passed);
    }
}
