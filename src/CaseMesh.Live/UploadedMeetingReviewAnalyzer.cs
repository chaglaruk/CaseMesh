namespace CaseMesh.Live;

public sealed class UploadedMeetingReviewAnalyzer
{
    public UploadedMeetingReviewAnalysis Analyze(
        UploadedMeetingReview review,
        CanonicalLiveContext currentContext)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(currentContext);
        if (review.TenantId != currentContext.TenantId || review.MatterId != currentContext.MatterId)
        {
            throw new UnauthorizedAccessException("The uploaded meeting review does not match the canonical Matter context.");
        }

        var citedSourceIds = review.Items
            .SelectMany(item => item.ContextCitationSourceSpanIds)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        var sourceIdsPresent = currentContext.SourceSpans.Select(item => item.SourceSpanId).ToHashSet();
        var currentSourceIds = currentContext.Evidence
            .Where(item => item.RecordStatus == LiveEvidenceRecordStatus.Current)
            .Select(item => item.SourceSpanId)
            .ToHashSet();
        var historicalSourceIds = currentContext.Evidence
            .Where(item => item.RecordStatus == LiveEvidenceRecordStatus.Historical)
            .Select(item => item.SourceSpanId)
            .ToHashSet();

        var references = citedSourceIds
            .Select(id =>
            {
                if (currentSourceIds.Contains(id))
                {
                    return new UploadedMeetingContextReference(
                        id,
                        UploadedMeetingContextReferenceStatus.Current,
                        "This source is still current canonical Matter context. It is context for review only, not provenance for spoken wording.");
                }

                if (historicalSourceIds.Contains(id) || sourceIdsPresent.Contains(id))
                {
                    return new UploadedMeetingContextReference(
                        id,
                        UploadedMeetingContextReferenceStatus.Historical,
                        "This source remains auditable but is no longer current canonical Matter context. It is not provenance for spoken wording.");
                }

                return new UploadedMeetingContextReference(
                    id,
                    UploadedMeetingContextReferenceStatus.Missing,
                    "This previously attached context source is not available in the current canonical Matter projection; review current evidence before relying on it.");
            })
            .ToArray();

        var assertionSourceIds = currentContext.Evidence
            .GroupBy(item => item.AssertionId)
            .ToDictionary(group => group.Key, group => group.Select(item => item.SourceSpanId).ToHashSet());
        var relevantContradictions = currentContext.UnresolvedContradictions
            .Where(contradiction =>
                HasCitedSource(contradiction.AssertionAId, citedSourceIds, assertionSourceIds) ||
                HasCitedSource(contradiction.AssertionBId, citedSourceIds, assertionSourceIds))
            .OrderBy(item => item.ContradictionId)
            .ToArray();

        var prompts = new List<string>();
        if (review.Items.Any(item => item.Origin == LiveConversationOrigin.HrSaid &&
                                     item.ContextCitationSourceSpanIds.Count == 0))
        {
            prompts.Add("Review HR statements without attached Matter context and decide whether supporting documentary evidence should be located.");
        }

        if (references.Any(item => item.Status is UploadedMeetingContextReferenceStatus.Historical or UploadedMeetingContextReferenceStatus.Missing))
        {
            prompts.Add("Some meeting context is no longer current; compare it with the current canonical Matter before relying on it.");
        }

        if (relevantContradictions.Length > 0)
        {
            prompts.Add("Review unresolved canonical contradictions alongside the cited meeting context; CaseMesh does not choose which account is true.");
        }

        if (review.Items.Any(item => item.Origin == LiveConversationOrigin.AiSuggested))
        {
            prompts.Add("Treat AI suggestions as analysis only and verify them against the Matter before relying on them.");
        }

        return new UploadedMeetingReviewAnalysis(references, relevantContradictions, prompts);
    }

    private static bool HasCitedSource(
        Guid assertionId,
        IReadOnlyCollection<Guid> citedSourceIds,
        IReadOnlyDictionary<Guid, HashSet<Guid>> assertionSourceIds) =>
        assertionSourceIds.TryGetValue(assertionId, out var sourceIds) &&
        sourceIds.Overlaps(citedSourceIds);
}