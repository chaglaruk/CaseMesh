using CaseMesh.Core.Models;
using CaseMesh.MatterBrain;

namespace CaseMesh.Live;

public sealed class CanonicalLiveContextAdapter
{
    public CanonicalLiveContext Build(
        TenantId requestedTenantId,
        Guid requestedMatterId,
        MatterBrainState state,
        bool evidenceProcessingActive = false)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (requestedMatterId == Guid.Empty)
        {
            throw new ArgumentException("Matter id is required.", nameof(requestedMatterId));
        }

        var matter = state.Evidence.Matter;
        if (matter.TenantId != requestedTenantId || matter.Id != requestedMatterId || state.MatterId != requestedMatterId)
        {
            throw new UnauthorizedAccessException("The canonical Live context request does not match the tenant-scoped Matter.");
        }

        var policy = new CanonicalEvidencePolicy(state);
        var spansById = state.Evidence.SourceSpans.ToDictionary(item => item.Id);

        var evidence = state.Evidence.Assertions
            .Where(assertion => assertion.SourceSpanId.HasValue &&
                                assertion.OriginClass != EvidenceOriginClass.AiGeneratedInference &&
                                assertion.AssertionClass != AssertionClass.AiInference)
            .Select(assertion => ProjectEvidence(assertion, policy, spansById))
            .OrderBy(item => item.RecordStatus)
            .ThenBy(item => item.EventTime ?? item.AssertedAt)
            .ThenBy(item => item.AssertionId)
            .ToArray();

        var aiAnalysis = state.Evidence.Assertions
            .Where(assertion => assertion.OriginClass == EvidenceOriginClass.AiGeneratedInference &&
                                assertion.AssertionClass == AssertionClass.AiInference &&
                                assertion.SupersededByAssertionId is null &&
                                assertion.DisputeState != DisputeState.Superseded &&
                                assertion.VerificationState != VerificationState.Rejected &&
                                policy.IsCurrentAssertionRecord(assertion))
            .OrderBy(item => item.AssertedAt)
            .ThenBy(item => item.Id)
            .Select(assertion => new CanonicalLiveAnalysisItem(
                assertion.Id,
                assertion.SubjectReference,
                assertion.Predicate,
                assertion.Value,
                assertion.AssertedBy,
                assertion.AssertedAt,
                assertion.CreatedByModel ?? throw new InvalidOperationException("Canonical AI analysis requires model provenance.")))
            .ToArray();

        var contradictions = state.Evidence.Contradictions
            .Where(contradiction => contradiction.ResolutionState == ContradictionResolutionState.Unresolved &&
                                    policy.IsCurrentContradiction(contradiction))
            .OrderBy(item => item.Id)
            .Select(contradiction => new CanonicalLiveContradiction(
                contradiction.Id,
                contradiction.AssertionAId,
                contradiction.AssertionBId,
                contradiction.Type,
                policy.GetContradictionDetectionOrigin(contradiction).ToString()))
            .ToArray();

        return new CanonicalLiveContext(
            matter.TenantId,
            matter.Id,
            matter.Title,
            evidenceProcessingActive ? CanonicalLiveCurrentness.Processing : CanonicalLiveCurrentness.Current,
            evidence,
            aiAnalysis,
            contradictions);
    }

    private static CanonicalLiveEvidenceItem ProjectEvidence(
        Assertion assertion,
        CanonicalEvidencePolicy policy,
        IReadOnlyDictionary<Guid, SourceSpan> spansById)
    {
        var sourceSpanId = assertion.SourceSpanId ??
                           throw new InvalidOperationException("Canonical documentary evidence requires a source span.");
        if (!spansById.TryGetValue(sourceSpanId, out var sourceSpan))
        {
            throw new InvalidOperationException("Canonical documentary evidence references an unavailable source span.");
        }

        var isCurrent = policy.IsCurrentAttributedAssertion(assertion, requireDocumentarySource: true);
        return new CanonicalLiveEvidenceItem(
            assertion.Id,
            assertion.SubjectReference,
            assertion.Predicate,
            assertion.Value,
            assertion.AssertedBy,
            assertion.EventTime,
            assertion.AssertedAt,
            assertion.OriginClass,
            assertion.AssertionClass,
            assertion.DisputeState,
            assertion.VerificationState,
            isCurrent ? LiveEvidenceRecordStatus.Current : LiveEvidenceRecordStatus.Historical,
            isCurrent ? null : HistoricalReason(assertion, policy),
            new LiveSourceCitation(
                sourceSpan.Id,
                sourceSpan.DocumentVersion.DocumentId,
                sourceSpan.DocumentVersion.DocumentVersionId,
                sourceSpan.DocumentVersion.OriginalObjectId,
                sourceSpan.DocumentVersion.ContentSha256,
                sourceSpan.PageNumber,
                sourceSpan.TextStart,
                sourceSpan.TextEnd,
                sourceSpan.ExtractedText,
                sourceSpan.ExtractedTextDigest));
    }

    private static string HistoricalReason(Assertion assertion, CanonicalEvidencePolicy policy)
    {
        if (assertion.SupersededByAssertionId.HasValue || assertion.DisputeState == DisputeState.Superseded)
        {
            return "Superseded";
        }

        if (assertion.VerificationState == VerificationState.Rejected)
        {
            return "Rejected";
        }

        return policy.IsCurrentAssertionRecord(assertion) ? "NotCurrentEvidence" : "CanonicalDependencyInvalidated";
    }
}
