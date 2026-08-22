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
        var sourceClaims = cases.SelectMany(entry =>
        {
            var citedIds = entry.Answer.Citations.Select(citation => citation.RetrievalResultId).ToHashSet();
            return entry.Answer.Claims.Where(claim => claim.Kind == MatterClaimKind.Evidence)
                .Select(claim => (Claim: claim, CitedIds: citedIds));
        }).ToArray();
        var validClaims = sourceClaims.Count(item => item.Claim.CitationResultIds.Count > 0 &&
            item.Claim.CitationResultIds.All(item.CitedIds.Contains));
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
