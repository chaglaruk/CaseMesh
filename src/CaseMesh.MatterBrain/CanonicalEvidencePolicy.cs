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
    private const string HumanCorrectionNumericMismatchRuleDetector = "rule:human-correction-numeric-mismatch:v1";

    private readonly Guid _matterId;
    private readonly IReadOnlyDictionary<Guid, ExtractionCandidateRecord> _candidatesById;
    private readonly IReadOnlyDictionary<(CanonicalRecordKind Kind, Guid Id), Guid[]> _candidateIdsByCanonicalRecord;
    private readonly HashSet<(CanonicalRecordKind Kind, Guid Id, Guid CandidateId)> _dependencyCandidateKeys;
    private readonly IReadOnlyDictionary<(CanonicalRecordKind Kind, Guid Id, Guid CandidateId), HashSet<Guid>>
        _activeSourceIdsByCanonicalCandidate;
    private readonly HashSet<(CanonicalRecordKind Kind, Guid Id)> _extractedCanonicalRecords;
    private readonly HashSet<(CanonicalRecordKind Kind, Guid Id)> _activeCanonicalRecords;

    public CanonicalEvidencePolicy(MatterBrainState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _matterId = state.MatterId;
        _candidatesById = state.Candidates.ToDictionary(item => item.Id);

        var dependencies = state.Dependencies.ToArray();
        var activeDependencies = state.ActiveDependencies.ToArray();
        _candidateIdsByCanonicalRecord = dependencies
            .GroupBy(item => (item.CanonicalKind, item.CanonicalId))
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.CandidateId).Distinct().ToArray());
        _dependencyCandidateKeys = dependencies
            .Select(item => (item.CanonicalKind, item.CanonicalId, item.CandidateId))
            .ToHashSet();
        _activeSourceIdsByCanonicalCandidate = activeDependencies
            .GroupBy(item => (item.CanonicalKind, item.CanonicalId, item.CandidateId))
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.SourceSpanId).ToHashSet());
        _extractedCanonicalRecords = dependencies
            .Select(item => (item.CanonicalKind, item.CanonicalId))
            .ToHashSet();
        _activeCanonicalRecords = activeDependencies
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

        var key = (canonicalKind, canonicalId, candidate.Id);
        if (!_dependencyCandidateKeys.Contains(key) ||
            !_activeSourceIdsByCanonicalCandidate.TryGetValue(key, out var activeSources))
        {
            return false;
        }

        return candidateSources.All(activeSources.Contains);
    }

    public bool HasCompleteValidatedCandidateProvenance(
        CanonicalRecordKind canonicalKind,
        Guid canonicalId,
        ExtractionCandidateKind candidateKind)
    {
        if (!_candidateIdsByCanonicalRecord.ContainsKey((canonicalKind, canonicalId)))
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
        if (IsTrustedHumanCorrectionRuleContradiction(contradiction))
        {
            return ContradictionDetectionOrigin.DeterministicRule;
        }

        if (!_candidateIdsByCanonicalRecord.ContainsKey((CanonicalRecordKind.Contradiction, contradiction.Id)))
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
        ExtractionCandidateKind candidateKind)
    {
        if (!_candidateIdsByCanonicalRecord.TryGetValue((canonicalKind, canonicalId), out var candidateIds))
        {
            return [];
        }

        return candidateIds
            .Where(_candidatesById.ContainsKey)
            .Select(candidateId => _candidatesById[candidateId])
            .Where(candidate => candidate.Kind == candidateKind &&
                                HasCompleteValidatedCandidateProvenance(candidate, canonicalKind, canonicalId));
    }

    private bool IsTrustedHumanCorrectionRuleContradiction(Contradiction contradiction)
    {
        if (contradiction.Type != ContradictionType.NumericMismatch ||
            !string.Equals(contradiction.DetectedBy, HumanCorrectionNumericMismatchRuleDetector, StringComparison.Ordinal))
        {
            return false;
        }

        var first = contradiction.AssertionAId.CompareTo(contradiction.AssertionBId) <= 0
            ? contradiction.AssertionAId
            : contradiction.AssertionBId;
        var second = first == contradiction.AssertionAId
            ? contradiction.AssertionBId
            : contradiction.AssertionAId;
        var expectedId = MatterBrainState.DeterministicId(
            "correction-numeric-contradiction",
            _matterId,
            first,
            second);
        return contradiction.Id == expectedId;
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
