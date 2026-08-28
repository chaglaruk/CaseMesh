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
        var runsById = state.Runs.ToDictionary(item => item.Id);

        var evidence = state.Evidence.Assertions
            .Where(assertion => assertion.SourceSpanId.HasValue &&
                                assertion.OriginClass != EvidenceOriginClass.AiGeneratedInference &&
                                assertion.AssertionClass != AssertionClass.AiInference)
            .Select(assertion => ProjectEvidence(assertion, policy, spansById))
            .OrderBy(item => item.RecordStatus)
            .ThenBy(item => item.EventTime ?? item.AssertedAt)
            .ThenBy(item => item.AssertionId)
            .ToArray();

        var unsupportedStatements = state.Evidence.Assertions
            .Where(assertion => !assertion.SourceSpanId.HasValue &&
                                assertion.OriginClass != EvidenceOriginClass.AiGeneratedInference &&
                                assertion.AssertionClass != AssertionClass.AiInference &&
                                policy.IsCurrentAttributedAssertion(assertion, requireDocumentarySource: false))
            .OrderBy(item => item.EventTime ?? item.AssertedAt)
            .ThenBy(item => item.Id)
            .Select(assertion => new CanonicalLiveUnsupportedStatement(
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
                assertion.IntegrityState,
                assertion.VerificationState,
                assertion.ExtractionConfidence,
                "Current attributed Matter statement without documentary SourceSpan provenance; do not present it as source-backed evidence."))
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
                assertion.EventTime,
                assertion.AssertedAt,
                assertion.OriginClass,
                assertion.AssertionClass,
                assertion.DisputeState,
                assertion.IntegrityState,
                assertion.VerificationState,
                assertion.CreatedByModel ?? throw new InvalidOperationException("Canonical AI analysis requires model provenance.")))
            .ToArray();

        var contradictions = state.Evidence.Contradictions
            .Where(contradiction => contradiction.ResolutionState == ContradictionResolutionState.Unresolved &&
                                    policy.IsCurrentContradiction(contradiction))
            .OrderBy(item => item.Id)
            .Select(contradiction => ProjectContradiction(contradiction, state, policy, runsById))
            .ToArray();

        var referencedSourceSpanIds = evidence.Select(item => item.SourceSpanId)
            .Concat(contradictions.SelectMany(item => item.AnalysisProvenance.SelectMany(provenance => provenance.SourceSpanIds)))
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        var sourceSpans = referencedSourceSpanIds
            .Select(id => spansById.TryGetValue(id, out var sourceSpan)
                ? ProjectSource(sourceSpan)
                : throw new InvalidOperationException("Canonical Live provenance references an unavailable source span."))
            .ToArray();

        return new CanonicalLiveContext(
            matter.TenantId,
            matter.Id,
            matter.Title,
            evidenceProcessingActive ? CanonicalLiveCurrentness.Processing : CanonicalLiveCurrentness.Current,
            sourceSpans,
            evidence,
            unsupportedStatements,
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
        if (!spansById.ContainsKey(sourceSpanId))
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
            assertion.IntegrityState,
            assertion.VerificationState,
            assertion.ExtractionConfidence,
            isCurrent ? LiveEvidenceRecordStatus.Current : LiveEvidenceRecordStatus.Historical,
            isCurrent ? null : HistoricalReason(assertion, policy),
            isCurrent
                ? "Current attributed documentary evidence; not automatically an established fact."
                : "Historical documentary evidence retained for correction/audit context; not current and not automatically an established fact.",
            sourceSpanId);
    }

    private static CanonicalLiveContradiction ProjectContradiction(
        Contradiction contradiction,
        MatterBrainState state,
        CanonicalEvidencePolicy policy,
        IReadOnlyDictionary<Guid, ExtractionRun> runsById)
    {
        var detectionOrigin = policy.GetContradictionDetectionOrigin(contradiction);
        var analysisProvenance = detectionOrigin == ContradictionDetectionOrigin.StructuredExtractionAnalysis
            ? state.Candidates
                .Where(candidate => candidate.Kind == ExtractionCandidateKind.Contradiction &&
                                    policy.HasCompleteValidatedCandidateProvenance(
                                        candidate,
                                        CanonicalRecordKind.Contradiction,
                                        contradiction.Id))
                .OrderBy(candidate => candidate.RunId)
                .ThenBy(candidate => candidate.Id)
                .Select(candidate =>
                {
                    if (!runsById.TryGetValue(candidate.RunId, out var run))
                    {
                        throw new InvalidOperationException("Canonical contradiction analysis references an unavailable extraction run.");
                    }

                    return new LiveAnalysisRunProvenance(
                        candidate.Id,
                        candidate.SourceSpanIds.Distinct().OrderBy(id => id).ToArray(),
                        candidate.ExtractionConfidence,
                        candidate.PayloadDigest,
                        run.Id,
                        run.Provider.Provider,
                        run.Provider.Model,
                        run.Provider.ExtractionVersion,
                        run.Provider.PromptVersion,
                        run.Provider.SchemaVersion,
                        run.GeneratedAt,
                        run.RawResultDigest);
                })
                .ToArray()
            : [];

        return new CanonicalLiveContradiction(
            contradiction.Id,
            contradiction.AssertionAId,
            contradiction.AssertionBId,
            contradiction.Type,
            detectionOrigin.ToString(),
            analysisProvenance);
    }

    private static LiveSourceCitation ProjectSource(SourceSpan sourceSpan) => new(
        sourceSpan.Id,
        sourceSpan.DocumentVersion.DocumentId,
        sourceSpan.DocumentVersion.DocumentVersionId,
        sourceSpan.DocumentVersion.OriginalObjectId,
        sourceSpan.DocumentVersion.ContentSha256,
        sourceSpan.PageNumber,
        sourceSpan.TextStart,
        sourceSpan.TextEnd,
        sourceSpan.ExtractedText,
        sourceSpan.ExtractedTextDigest);

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
