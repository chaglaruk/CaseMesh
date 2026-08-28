using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Core.Workplace;
using CaseMesh.MatterBrain;

namespace CaseMesh.Qa;

public static class FactualGapAnalyzer
{
    public static IReadOnlyList<FactualGap> Analyze(
        MatterEvidenceGraph evidence,
        WorkplaceMatter workplace,
        MatterBrainState brain)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(workplace);
        ArgumentNullException.ThrowIfNull(brain);
        if (!ReferenceEquals(evidence, workplace.Evidence) || !ReferenceEquals(evidence, brain.Evidence))
            throw new InvalidOperationException("Factual-gap inputs must share one canonical Matter state.");

        var gaps = new List<FactualGap>();
        var assertions = evidence.Assertions.ToDictionary(item => item.Id);
        var events = evidence.Events.ToDictionary(item => item.Id);
        var candidatesById = brain.Candidates.ToDictionary(item => item.Id);
        var activeDependencies = brain.ActiveDependencies.ToArray();
        var extractedCanonicalRecords = brain.Dependencies
            .Select(item => (item.CanonicalKind, item.CanonicalId))
            .ToHashSet();
        var activeCanonicalRecords = activeDependencies
            .Select(item => (item.CanonicalKind, item.CanonicalId))
            .ToHashSet();
        bool IsCurrentCanonical(CanonicalRecordKind kind, Guid id) =>
            !extractedCanonicalRecords.Contains((kind, id)) || activeCanonicalRecords.Contains((kind, id));
        bool HasCompleteCurrentCandidateProvenance(
            CanonicalRecordKind canonicalKind,
            Guid canonicalId,
            ExtractionCandidateKind candidateKind)
        {
            var dependencies = brain.Dependencies
                .Where(dependency => dependency.CanonicalKind == canonicalKind &&
                                     dependency.CanonicalId == canonicalId)
                .ToArray();
            if (dependencies.Length == 0) return true;

            return dependencies
                .GroupBy(dependency => dependency.CandidateId)
                .Any(group =>
                {
                    if (!candidatesById.TryGetValue(group.Key, out var candidate) ||
                        candidate.Kind != candidateKind ||
                        candidate.Disposition != CandidateDisposition.Validated)
                    {
                        return false;
                    }

                    var candidateSources = candidate.SourceSpanIds.Distinct().ToArray();
                    if (candidateSources.Length == 0) return false;
                    var activeSources = activeDependencies
                        .Where(dependency => dependency.CanonicalKind == canonicalKind &&
                                             dependency.CanonicalId == canonicalId &&
                                             dependency.CandidateId == candidate.Id)
                        .Select(dependency => dependency.SourceSpanId)
                        .ToHashSet();
                    return candidateSources.All(activeSources.Contains);
                });
        }
        bool IsCurrentAssertionEventLink(Guid id) =>
            IsCurrentCanonical(CanonicalRecordKind.AssertionEventLink, id) &&
            HasCompleteCurrentCandidateProvenance(
                CanonicalRecordKind.AssertionEventLink,
                id,
                ExtractionCandidateKind.AssertionEventLink);
        bool IsCurrentEvent(MatterEvent matterEvent) =>
            matterEvent.Status is not (EventStatus.Superseded or EventStatus.Rejected) &&
            IsCurrentCanonical(CanonicalRecordKind.Event, matterEvent.Id) &&
            HasCompleteCurrentCandidateProvenance(
                CanonicalRecordKind.Event,
                matterEvent.Id,
                ExtractionCandidateKind.Event);

        foreach (var contradiction in evidence.Contradictions
                     .Where(item => item.ResolutionState == ContradictionResolutionState.Unresolved &&
                                    IsCurrentCanonical(CanonicalRecordKind.Contradiction, item.Id) &&
                                    IsCurrentCanonical(CanonicalRecordKind.Assertion, item.AssertionAId) &&
                                    IsCurrentCanonical(CanonicalRecordKind.Assertion, item.AssertionBId))
                     .OrderBy(item => item.Id))
        {
            gaps.Add(new FactualGap("unresolved-contradiction",
                "Conflicting attributed statements remain unresolved; further source evidence may clarify the record.",
                "disputed", [contradiction.Id, contradiction.AssertionAId, contradiction.AssertionBId],
                SourceIds(assertions, contradiction.AssertionAId, contradiction.AssertionBId)));
        }

        foreach (var assertion in evidence.Assertions.Where(item => item.SourceSpanId is null &&
                     item.SupersededByAssertionId is null &&
                     item.DisputeState != DisputeState.Superseded &&
                     item.VerificationState != VerificationState.Rejected &&
                     item.OriginClass != EvidenceOriginClass.AiGeneratedInference &&
                     item.AssertionClass != AssertionClass.AiInference &&
                     IsCurrentCanonical(CanonicalRecordKind.Assertion, item.Id)).OrderBy(item => item.Id))
        {
            gaps.Add(new FactualGap("assertion-without-documentary-source",
                "An attributed statement has no linked documentary source and requires supporting evidence or review.",
                "evidence", [assertion.Id], []));
        }

        foreach (var request in workplace.AdjustmentRequests.OrderBy(item => item.Id))
        {
            if (request.ResponseAssertionIds.Count == 0)
                gaps.Add(new FactualGap("adjustment-response-not-recorded",
                    "An adjustment request is recorded, but no employer response evidence is linked.",
                    "workplace", [request.Id, .. request.RequestAssertionIds],
                    SourceIds(assertions, request.RequestAssertionIds)));
            else if (request.ImplementationAssertionIds.Count == 0)
                gaps.Add(new FactualGap("adjustment-implementation-not-recorded",
                    "A response is recorded, but no separate evidence of implementation is linked.",
                    "workplace", [request.Id, .. request.ResponseAssertionIds],
                    SourceIds(assertions, request.ResponseAssertionIds)));
        }

        foreach (var audit in evidence.AuditEvents.Where(item => item.ReplacementEntityId.HasValue).OrderBy(item => item.Id))
            gaps.Add(new FactualGap("corrected-history-review",
                "A corrected or superseded record has historical evidence that should remain visible during review.",
                "timeline", [audit.EntityId, audit.ReplacementEntityId!.Value, audit.Id], []));

        var linksByEvent = evidence.AssertionEventLinks
            .Where(item => IsCurrentAssertionEventLink(item.Id) &&
                           events.TryGetValue(item.EventId, out var matterEvent) &&
                           IsCurrentEvent(matterEvent))
            .GroupBy(item => item.EventId);
        foreach (var links in linksByEvent)
        {
            var linked = links.Select(item => assertions[item.AssertionId])
                .Where(item => item.EventTime.HasValue &&
                               item.SupersededByAssertionId is null &&
                               item.DisputeState != DisputeState.Superseded &&
                               item.VerificationState != VerificationState.Rejected &&
                               IsCurrentCanonical(CanonicalRecordKind.Assertion, item.Id))
                .ToArray();
            if (linked.Select(item => item.EventTime).Distinct().Skip(1).Any())
                gaps.Add(new FactualGap("chronology-date-conflict",
                    "Linked attributed statements contain different dates for the same chronology item.",
                    "timeline", [links.Key, .. linked.Select(item => item.Id)],
                    linked.Where(item => item.SourceSpanId.HasValue).Select(item => item.SourceSpanId!.Value).Distinct().ToArray()));
        }

        var completedProposals = brain.EntityResolutionActions.Where(item =>
                item.Kind is EntityResolutionActionKind.Accepted or EntityResolutionActionKind.Rejected or EntityResolutionActionKind.Reversed)
            .Select(item => item.ProposalId).ToHashSet();
        var runsById = brain.Runs.ToDictionary(item => item.Id);
        var entityMatchCandidates = brain.Candidates
            .Where(item => item.Kind == ExtractionCandidateKind.EntityMatch &&
                           item.Disposition == CandidateDisposition.Validated)
            .ToDictionary(
                item => MatterBrainIdentity.EntityMergeProposalId(item.RunId, item.ExternalKey),
                item => item);
        bool IsLaterRun(ExtractionRun run, ExtractionRun candidateRun)
        {
            if (run.Id == candidateRun.Id) return false;
            if (run.Sequence.HasValue && candidateRun.Sequence.HasValue)
                return run.Sequence.Value > candidateRun.Sequence.Value;
            if (run.Sequence.HasValue) return true;
            if (candidateRun.Sequence.HasValue) return false;
            return run.GeneratedAt > candidateRun.GeneratedAt;
        }
        bool HasLegacyOrderingAmbiguity(EntityResolutionAction action)
        {
            if (!string.Equals(action.Actor, "structured-extraction", StringComparison.Ordinal) ||
                !entityMatchCandidates.TryGetValue(action.ProposalId, out var candidate) ||
                !runsById.TryGetValue(candidate.RunId, out var candidateRun) ||
                candidateRun.Sequence.HasValue)
                return false;

            var candidateSources = candidate.SourceSpanIds.ToHashSet();
            return brain.Runs.Any(run =>
                run.Id != candidateRun.Id &&
                !run.Sequence.HasValue &&
                run.GeneratedAt == candidateRun.GeneratedAt &&
                candidateSources.All(sourceId => run.SourceSpanIds.Contains(sourceId)));
        }
        bool IsCurrentProposal(EntityResolutionAction action)
        {
            if (!string.Equals(action.Actor, "structured-extraction", StringComparison.Ordinal)) return true;
            if (!entityMatchCandidates.TryGetValue(action.ProposalId, out var candidate) ||
                !runsById.TryGetValue(candidate.RunId, out var candidateRun)) return true;

            var candidateSources = candidate.SourceSpanIds.ToHashSet();
            return !brain.Runs.Any(run =>
                IsLaterRun(run, candidateRun) &&
                candidateSources.All(sourceId => run.SourceSpanIds.Contains(sourceId)));
        }

        foreach (var proposal in brain.EntityResolutionActions.Where(item =>
                     item.Kind == EntityResolutionActionKind.Proposed &&
                     !completedProposals.Contains(item.ProposalId) &&
                     IsCurrentProposal(item)).OrderBy(item => item.Id))
        {
            var summary = HasLegacyOrderingAmbiguity(proposal)
                ? "A similar-name entity match remains unresolved because legacy extraction runs share the same timestamp and no truthful execution sequence was recorded; confirm the identity manually."
                : "A similar-name entity match remains a proposal and requires confirmation before identities are treated as the same.";
            gaps.Add(new FactualGap("entity-ambiguity", summary,
                "people", [proposal.Id, proposal.SourceEntityId, proposal.TargetEntityId], proposal.EvidenceSourceSpanIds));
        }

        return gaps.OrderBy(item => item.Code, StringComparer.Ordinal)
            .ThenBy(item => item.RelatedRecordIds.FirstOrDefault()).ToArray();
    }

    private static IReadOnlyList<Guid> SourceIds(
        IReadOnlyDictionary<Guid, Assertion> assertions,
        params Guid[] ids) => SourceIds(assertions, (IEnumerable<Guid>)ids);

    private static IReadOnlyList<Guid> SourceIds(
        IReadOnlyDictionary<Guid, Assertion> assertions,
        IEnumerable<Guid> ids) => ids.Where(assertions.ContainsKey).Select(id => assertions[id].SourceSpanId)
        .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
}
