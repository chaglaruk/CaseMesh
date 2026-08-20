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
        "legal-liability", "liability", "win-probability", "winprobability",
        "compensation-estimate", "compensationestimate", "merits-score", "meritsscore"
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

        var assertionsById = state.Evidence.Assertions.ToDictionary(item => item.Id);
        var linksByEvent = state.Evidence.AssertionEventLinks
            .GroupBy(item => item.EventId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var matterEvent in state.Evidence.Events)
        {
            if (!linksByEvent.TryGetValue(matterEvent.Id, out var links) || links.Length == 0)
            {
                continue;
            }

            total++;
            if (links.Any(link => assertionsById.TryGetValue(link.AssertionId, out var assertion) &&
                                  assertion.SourceSpanId.HasValue && spans.ContainsKey(assertion.SourceSpanId.Value)))
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
