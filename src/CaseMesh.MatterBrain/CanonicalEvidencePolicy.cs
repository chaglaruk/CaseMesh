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

        return dependencies
            .GroupBy(dependency => dependency.CandidateId)
            .Any(group =>
            {
                if (!_candidatesById.TryGetValue(group.Key, out var candidate) ||
                    candidate.Kind != candidateKind ||
                    candidate.Disposition != CandidateDisposition.Validated)
                {
                    return false;
                }

                var candidateSources = candidate.SourceSpanIds.Distinct().ToArray();
                if (candidateSources.Length == 0)
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
            });
    }

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
               IsCurrentExtractedRecord(
                   CanonicalRecordKind.Assertion,
                   assertion.Id,
                   ExtractionCandidateKind.Assertion);
    }

    public ContradictionDetectionOrigin GetContradictionDetectionOrigin(Contradiction contradiction)
    {
        ArgumentNullException.ThrowIfNull(contradiction);
        var candidateIds = _state.Dependencies
            .Where(dependency => dependency.CanonicalKind == CanonicalRecordKind.Contradiction &&
                                 dependency.CanonicalId == contradiction.Id)
            .Select(dependency => dependency.CandidateId)
            .Distinct()
            .ToArray();
        if (candidateIds.Length == 0)
        {
            return ContradictionDetectionOrigin.CanonicalRecord;
        }

        var candidates = candidateIds
            .Where(_candidatesById.ContainsKey)
            .Select(id => _candidatesById[id])
            .Where(candidate => candidate.Kind == ExtractionCandidateKind.Contradiction &&
                                candidate.Disposition == CandidateDisposition.Validated)
            .ToArray();
        return candidates.Any(candidate => IsTrustedDeterministicRuleCandidate(candidate, contradiction))
            ? ContradictionDetectionOrigin.DeterministicRule
            : ContradictionDetectionOrigin.StructuredExtractionAnalysis;
    }

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
