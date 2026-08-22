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
        foreach (var contradiction in evidence.Contradictions
                     .Where(item => item.ResolutionState == ContradictionResolutionState.Unresolved)
                     .OrderBy(item => item.Id))
        {
            gaps.Add(new FactualGap("unresolved-contradiction",
                "Conflicting attributed statements remain unresolved; further source evidence may clarify the record.",
                "disputed", [contradiction.Id, contradiction.AssertionAId, contradiction.AssertionBId],
                SourceIds(assertions, contradiction.AssertionAId, contradiction.AssertionBId)));
        }

        foreach (var assertion in evidence.Assertions.Where(item => item.SourceSpanId is null &&
                     item.VerificationState != VerificationState.Rejected).OrderBy(item => item.Id))
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

        var linksByEvent = evidence.AssertionEventLinks.GroupBy(item => item.EventId);
        foreach (var links in linksByEvent)
        {
            var linked = links.Select(item => assertions[item.AssertionId]).Where(item => item.EventTime.HasValue).ToArray();
            if (linked.Select(item => item.EventTime).Distinct().Skip(1).Any())
                gaps.Add(new FactualGap("chronology-date-conflict",
                    "Linked attributed statements contain different dates for the same chronology item.",
                    "timeline", [links.Key, .. linked.Select(item => item.Id)],
                    linked.Where(item => item.SourceSpanId.HasValue).Select(item => item.SourceSpanId!.Value).Distinct().ToArray()));
        }

        var completedProposals = brain.EntityResolutionActions.Where(item =>
                item.Kind is EntityResolutionActionKind.Accepted or EntityResolutionActionKind.Rejected or EntityResolutionActionKind.Reversed)
            .Select(item => item.ProposalId).ToHashSet();
        foreach (var proposal in brain.EntityResolutionActions.Where(item =>
                     item.Kind == EntityResolutionActionKind.Proposed && !completedProposals.Contains(item.ProposalId)).OrderBy(item => item.Id))
            gaps.Add(new FactualGap("entity-ambiguity",
                "A similar-name entity match remains a proposal and requires confirmation before identities are treated as the same.",
                "people", [proposal.Id, proposal.SourceEntityId, proposal.TargetEntityId], proposal.EvidenceSourceSpanIds));

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
