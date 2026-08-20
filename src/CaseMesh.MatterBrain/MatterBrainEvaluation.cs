using System.Text.Json;
using CaseMesh.Core.Models;

namespace CaseMesh.MatterBrain;

public sealed record MatterBrainEvaluationReport(
    int CanonicalSourceLinkedRecords,
    int ValidCanonicalSourceLinks,
    int InvalidCanonicalSourceLinks,
    decimal SourceLinkValidityPercent,
    int RejectedCandidates,
    int ForbiddenConclusionCount)
{
    public string ToDeterministicJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    });
}

public static class MatterBrainEvaluation
{
    private static readonly string[] ForbiddenConclusionTerms =
    [
        "legal-liability", "win-probability", "compensation-estimate", "merits-score"
    ];

    public static MatterBrainEvaluationReport Evaluate(MatterBrainState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var spans = state.Evidence.SourceSpans.ToDictionary(item => item.Id);
        var versions = state.Evidence.DocumentVersions.ToDictionary(item => item.DocumentVersionId);
        var total = 0;
        var valid = 0;

        foreach (var assertion in state.Evidence.Assertions.Where(item => item.SourceSpanId.HasValue))
        {
            total++;
            if (spans.TryGetValue(assertion.SourceSpanId!.Value, out var span) &&
                span.MatterId == state.MatterId &&
                versions.TryGetValue(span.DocumentVersion.DocumentVersionId, out var version) &&
                version.MatterId == state.MatterId && version.OriginalObjectId != Guid.Empty &&
                version.ContentSha256.Length == 64)
            {
                valid++;
            }
        }

        foreach (var communication in state.Communications.Where(item => item.SourceSpanIds.Count > 0))
        {
            total++;
            if (communication.SourceSpanIds.All(id => spans.ContainsKey(id)))
            {
                valid++;
            }
        }

        foreach (var matterEvent in state.Evidence.Events)
        {
            var linkedAssertions = state.Evidence.AssertionEventLinks
                .Where(item => item.EventId == matterEvent.Id)
                .Select(item => state.Evidence.Assertions.Single(assertion => assertion.Id == item.AssertionId))
                .ToArray();
            if (linkedAssertions.Length == 0)
            {
                continue;
            }

            total++;
            if (linkedAssertions.Any(item => item.SourceSpanId.HasValue && spans.ContainsKey(item.SourceSpanId.Value)))
            {
                valid++;
            }
        }

        var forbidden = state.Evidence.Assertions.Count(assertion => ForbiddenConclusionTerms.Any(term =>
                            assertion.Predicate.Contains(term, StringComparison.OrdinalIgnoreCase))) +
                        state.Evidence.AnalysisNodes.Count(node => ForbiddenConclusionTerms.Any(term =>
                            node.AnalysisType.Contains(term, StringComparison.OrdinalIgnoreCase)));
        var invalid = total - valid;
        return new MatterBrainEvaluationReport(
            total,
            valid,
            invalid,
            total == 0 ? 100m : decimal.Round(valid * 100m / total, 4),
            state.Candidates.Count(item => item.Disposition == CandidateDisposition.Rejected),
            forbidden);
    }
}
