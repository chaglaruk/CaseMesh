using System.Text.Json;
using CaseMesh.Core.Models;

namespace CaseMesh.MatterBrain;

public enum ContradictionDetectionOrigin
{
    CanonicalRecord = 0,
    DeterministicRule = 1,
    StructuredExtractionAnalysis = 2
}

public sealed class CanonicalEvidencePolicy
{
    private const string NumericMismatchRuleDetector = "rule:same-subject-predicate-time-numeric-mismatch:v1";

    private readonly MatterBrainState _state;
    private readonly IReadOnlyDictionary<Guid, ExtractionCandidateRecord> _candidatesById;
    private readonly MatterBrainDependency[] _activeDependencies;
    private readonly HashSet<(CanonicalRecordKind Kind, Guid Id)> _extractedCanonicalRecords;
    private readonly HashSet<(CanonicalRecordKind Kind, Guid Id)> _activeCanonicalRecords;

    public CanonicalEvidencePolicy(MatterBrainState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        _candidatesById = state.Candidates.ToDictionary(item => item.Id);
        _activeDependencies = state.ActiveDependencies.ToArray();
        _extractedCanonicalRecords = state.Dependencies
            .Select(item => (item.CanonicalKind, item.CanonicalId))
            .ToHashSet();
        _activeCanonicalRecords = _activeDependencies
            .Select(item => (item.CanonicalKind, item.CanonicalId))
            .ToHashSet();
    }

    public bool IsCanonicalCurrent(CanonicalRecordKind kind, Guid id) =>
        !_extractedCanonicalRecords.Contains((kind, id)) ||
        _activeCanonicalRecords.Contains((kind, id));

    public bool HasCompleteValidatedCandidateProvenance(
        ExtractionCandidateRecord candidate,
        CanonicalRecordKind canonicalKind,
        Guid canonicalId)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Disposition != CandidateDisposition.Validated ||
            candidate.CanonicalKind != canonicalKind ||
            candidate.CanonicalId != canonicalId)
        {
            return false;
        }

        var candidateSources = candidate.SourceSpanIds.Distinct().ToArray();
        if (candidateSources.Length == 0)
        {
            return false;
        }

        var exactDependencies = _state.Dependencies
            .Where(dependency => dependency.CanonicalKind == canonicalKind &&
                                 dependency.CanonicalId == canonicalId &&
                                 dependency.CandidateId == candidate.Id)
            .ToArray();
        if (exactDependencies.Length == 0)
        {
            return false;
        }

        var activeSources = _activeDependencies
            .Where(dependency => dependency.CanonicalKind == canonicalKind &&
                                 dependency.CanonicalId == canonicalId &&
                                 dependency.CandidateId == candidate.Id)
            .Select(dependency => dependency.SourceSpanId)
            .ToHashSet();
        return candidateSources.All(activeSources.Contains);
    }

    public bool HasCompleteValidatedCandidateProvenance(
        CanonicalRecordKind canonicalKind,
        Guid canonicalId,
        ExtractionCandidateKind candidateKind)
    {
        var dependencies = _state.Dependencies
            .Where(dependency => dependency.CanonicalKind == canonicalKind &&
                                 dependency.CanonicalId == canonicalId)
            .ToArray();
        if (dependencies.Length == 0)
        {
            return true;
        }

        return CompleteCandidates(canonicalKind, canonicalId, candidateKind).Any();
    }

    public IReadOnlyList<Guid> CompleteCandidateSourceIds(
        CanonicalRecordKind canonicalKind,
        Guid canonicalId,
        ExtractionCandidateKind candidateKind) =>
        CompleteCandidates(canonicalKind, canonicalId, candidateKind)
            .SelectMany(candidate => candidate.SourceSpanIds)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

    public bool IsCurrentExtractedRecord(
        CanonicalRecordKind canonicalKind,
        Guid canonicalId,
        ExtractionCandidateKind candidateKind) =>
        IsCanonicalCurrent(canonicalKind, canonicalId) &&
        HasCompleteValidatedCandidateProvenance(canonicalKind, canonicalId, candidateKind);

    public bool IsCurrentAssertionEventLink(Guid id) =>
        IsCurrentExtractedRecord(
            CanonicalRecordKind.AssertionEventLink,
            id,
            ExtractionCandidateKind.AssertionEventLink);

    public bool IsCurrentContradiction(Contradiction contradiction)
    {
        ArgumentNullException.ThrowIfNull(contradiction);
        return IsCurrentExtractedRecord(
            CanonicalRecordKind.Contradiction,
            contradiction.Id,
            ExtractionCandidateKind.Contradiction);
    }

    public bool IsCurrentEvent(MatterEvent matterEvent)
    {
        ArgumentNullException.ThrowIfNull(matterEvent);
        return matterEvent.Status is not (EventStatus.Superseded or EventStatus.Rejected) &&
               matterEvent.VerificationState != VerificationState.Rejected &&
               IsCurrentExtractedRecord(
                   CanonicalRecordKind.Event,
                   matterEvent.Id,
                   ExtractionCandidateKind.Event);
    }

    public bool IsCurrentAssertionRecord(Assertion assertion)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        return IsCurrentExtractedRecord(
            CanonicalRecordKind.Assertion,
            assertion.Id,
            ExtractionCandidateKind.Assertion);
    }

    public bool IsCurrentAttributedAssertion(Assertion assertion, bool requireDocumentarySource)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        if (requireDocumentarySource && !assertion.SourceSpanId.HasValue)
        {
            return false;
        }

        return assertion.SupersededByAssertionId is null &&
               assertion.DisputeState != DisputeState.Superseded &&
               assertion.VerificationState != VerificationState.Rejected &&
               assertion.OriginClass != EvidenceOriginClass.AiGeneratedInference &&
               assertion.AssertionClass != AssertionClass.AiInference &&
               IsCurrentAssertionRecord(assertion);
    }

    public ContradictionDetectionOrigin GetContradictionDetectionOrigin(Contradiction contradiction)
    {
        ArgumentNullException.ThrowIfNull(contradiction);
        var contradictionDependencies = _state.Dependencies
            .Where(dependency => dependency.CanonicalKind == CanonicalRecordKind.Contradiction &&
                                 dependency.CanonicalId == contradiction.Id)
            .ToArray();
        if (contradictionDependencies.Length == 0)
        {
            return ContradictionDetectionOrigin.CanonicalRecord;
        }

        var currentCandidates = CompleteCandidates(
            CanonicalRecordKind.Contradiction,
            contradiction.Id,
            ExtractionCandidateKind.Contradiction).ToArray();
        if (currentCandidates.Length == 0)
        {
            return ContradictionDetectionOrigin.StructuredExtractionAnalysis;
        }

        return currentCandidates.Any(candidate => IsTrustedDeterministicRuleCandidate(candidate, contradiction))
            ? ContradictionDetectionOrigin.DeterministicRule
            : ContradictionDetectionOrigin.StructuredExtractionAnalysis;
    }

    private IEnumerable<ExtractionCandidateRecord> CompleteCandidates(
        CanonicalRecordKind canonicalKind,
        Guid canonicalId,
        ExtractionCandidateKind candidateKind) =>
        _state.Dependencies
            .Where(dependency => dependency.CanonicalKind == canonicalKind &&
                                 dependency.CanonicalId == canonicalId)
            .Select(dependency => dependency.CandidateId)
            .Distinct()
            .Where(_candidatesById.ContainsKey)
            .Select(candidateId => _candidatesById[candidateId])
            .Where(candidate => candidate.Kind == candidateKind &&
                                HasCompleteValidatedCandidateProvenance(candidate, canonicalKind, canonicalId));

    private static bool IsTrustedDeterministicRuleCandidate(
        ExtractionCandidateRecord candidate,
        Contradiction contradiction)
    {
        var expectedKey = $"rule:numeric-mismatch:{contradiction.AssertionAId:N}:{contradiction.AssertionBId:N}";
        if (!string.Equals(candidate.ExternalKey, expectedKey, StringComparison.Ordinal) ||
            !string.Equals(contradiction.DetectedBy, NumericMismatchRuleDetector, StringComparison.Ordinal))
        {
            return false;
        }

        var expectedPayload = MatterBrainIntegrity.CanonicalizeJson(JsonSerializer.Serialize(new
        {
            assertionAId = contradiction.AssertionAId,
            assertionBId = contradiction.AssertionBId,
            detectedBy = NumericMismatchRuleDetector
        }));
        return string.Equals(candidate.PayloadJson, expectedPayload, StringComparison.Ordinal);
    }
}
