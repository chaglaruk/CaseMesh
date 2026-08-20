using CaseMesh.Core.Models;

namespace CaseMesh.Core.Services;

public sealed partial class MatterEvidenceGraph
{
    public AssertionReviewResult ReviewAssertion(
        Guid assertionId,
        VerificationState verificationState,
        Guid auditEventId,
        string actor,
        DateTimeOffset reviewedAt)
    {
        if (verificationState == VerificationState.NotReviewed || !Enum.IsDefined(verificationState))
        {
            throw new ArgumentOutOfRangeException(
                nameof(verificationState),
                "A review must be Confirmed, Rejected, or NeedsContext.");
        }

        RequireId(auditEventId, nameof(auditEventId));
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var assertion = RequireOwned(_assertions, assertionId, "assertion");
        if (assertion.SupersededByAssertionId.HasValue)
        {
            throw new InvalidOperationException("A superseded assertion cannot be reviewed as current.");
        }

        if (assertion.VerificationState != VerificationState.NotReviewed)
        {
            throw new InvalidOperationException("An assertion review is append-only and cannot overwrite an earlier review.");
        }

        if (_auditEvents.Any(item => item.Id == auditEventId))
        {
            throw new InvalidOperationException("Audit event id already exists.");
        }

        ValidateContradictionResolutionTime(assertionId, reviewedAt);
        var reviewed = assertion.WithReview(verificationState, assertion.DisputeState);
        _assertions[assertionId] = reviewed;
        IReadOnlyList<Guid> dismissedContradictions = [];
        if (verificationState == VerificationState.Rejected)
        {
            dismissedContradictions = RecalculateRejectedAssertion(
                assertionId, reviewedAt, rejectUnsupportedEvents: true);
        }

        var audit = new AuditEvent(
            auditEventId,
            Matter.Id,
            verificationState == VerificationState.Rejected
                ? AuditEventKind.AssertionRejected
                : AuditEventKind.AssertionReviewed,
            nameof(Assertion),
            assertionId,
            null,
            actor,
            AppendDismissedContradictions(
                $"Assertion review changed from {assertion.VerificationState} to {verificationState}.",
                dismissedContradictions),
            reviewedAt);
        _auditEvents.Add(audit);
        return new AssertionReviewResult(reviewed, audit);
    }

    public AssertionCorrectionResult CorrectAssertion(
        Guid assertionId,
        Guid correctedAssertionId,
        string correctedValue,
        DateTimeOffset? correctedEventTime,
        Guid auditEventId,
        string actor,
        DateTimeOffset correctedAt)
    {
        RequireId(correctedAssertionId, nameof(correctedAssertionId));
        RequireId(auditEventId, nameof(auditEventId));
        ArgumentException.ThrowIfNullOrWhiteSpace(correctedValue);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var original = RequireOwned(_assertions, assertionId, "assertion");
        if (original.SupersededByAssertionId.HasValue)
        {
            throw new InvalidOperationException("A superseded assertion cannot be corrected again.");
        }

        EnsureAvailable(_assertions, correctedAssertionId, "corrected assertion");
        if (_auditEvents.Any(item => item.Id == auditEventId))
        {
            throw new InvalidOperationException("Audit event id already exists.");
        }

        ValidateContradictionResolutionTime(assertionId, correctedAt);
        var corrected = AddAssertion(
            correctedAssertionId,
            original.SubjectReference,
            original.Predicate,
            correctedValue,
            actor,
            correctedAt,
            EvidenceOriginClass.RetrospectiveNote,
            AssertionClass.AttributedAssertion,
            DisputeState.Unverified,
            IntegrityState.MetadataUncertain,
            VerificationState.NotReviewed,
            null,
            correctedEventTime,
            null,
            null);
        var superseded = original.WithReview(
            VerificationState.Rejected,
            DisputeState.Superseded,
            correctedAssertionId);
        _assertions[assertionId] = superseded;
        var dismissedContradictions = RecalculateRejectedAssertion(
            assertionId, correctedAt, rejectUnsupportedEvents: false);

        var audit = new AuditEvent(
            auditEventId,
            Matter.Id,
            AuditEventKind.AssertionCorrected,
            nameof(Assertion),
            assertionId,
            correctedAssertionId,
            actor,
            AppendDismissedContradictions(
                "Assertion corrected with an append-only human-attributed replacement; the original documentary source remains attached only to the superseded assertion.",
                dismissedContradictions),
            correctedAt);
        _auditEvents.Add(audit);
        return new AssertionCorrectionResult(superseded, corrected, audit);
    }

    private IReadOnlyList<Guid> RecalculateRejectedAssertion(
        Guid assertionId,
        DateTimeOffset changedAt,
        bool rejectUnsupportedEvents)
    {
        var contradictions = LinkedUnresolvedContradictions(assertionId);
        foreach (var contradiction in contradictions)
        {
            _contradictions[contradiction.Id] = contradiction.Resolve(
                ContradictionResolutionState.Dismissed,
                "Dismissed because a linked assertion was rejected or superseded.",
                changedAt);
        }

        if (!rejectUnsupportedEvents)
        {
            return contradictions.Select(item => item.Id).ToArray();
        }

        var affectedEventIds = _links.Values
            .Where(item => item.AssertionId == assertionId)
            .Select(item => item.EventId)
            .Distinct()
            .ToArray();
        foreach (var eventId in affectedEventIds)
        {
            var matterEvent = _events[eventId];
            if (matterEvent.Status == EventStatus.Superseded || matterEvent.SupersededByEventId.HasValue)
            {
                continue;
            }

            var supportingAssertionIds = _links.Values
                .Where(item => item.EventId == eventId && item.Relation is
                    AssertionEventRelation.Supports or AssertionEventRelation.Qualifies or AssertionEventRelation.Contextualizes)
                .Select(item => item.AssertionId)
                .Distinct()
                .ToArray();
            if (supportingAssertionIds.Length > 0 && supportingAssertionIds.All(id =>
                    _assertions[id].VerificationState == VerificationState.Rejected ||
                    _assertions[id].DisputeState == DisputeState.Superseded))
            {
                _events[eventId] = matterEvent.WithReview(EventStatus.Rejected, VerificationState.Rejected);
            }
        }

        return contradictions.Select(item => item.Id).ToArray();
    }

    private Contradiction[] LinkedUnresolvedContradictions(Guid assertionId) =>
        _contradictions.Values
            .Where(item => item.ResolutionState == ContradictionResolutionState.Unresolved &&
                           (item.AssertionAId == assertionId || item.AssertionBId == assertionId))
            .OrderBy(item => item.Id)
            .ToArray();

    private void ValidateContradictionResolutionTime(Guid assertionId, DateTimeOffset changedAt)
    {
        if (LinkedUnresolvedContradictions(assertionId).Any(item => changedAt < item.CreatedAt))
        {
            throw new ArgumentOutOfRangeException(
                nameof(changedAt),
                "An assertion review or correction cannot dismiss a contradiction before it was detected.");
        }
    }

    private static string AppendDismissedContradictions(string summary, IReadOnlyList<Guid> contradictionIds) =>
        contradictionIds.Count == 0
            ? summary
            : $"{summary} Dismissed contradiction ids: {string.Join(", ", contradictionIds.Select(item => item.ToString("N")))}.";
}
