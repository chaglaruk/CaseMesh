using System.Text.Json;
using CaseMesh.Core.Models;
using CaseMesh.MatterBrain;
using CaseMesh.Persistence.Postgres;
using CaseMesh.Qa;

namespace CaseMesh.Api;

internal static class MeetingPreparationProjection
{
    private const int MaximumPriorityItems = 12;

    internal static object Create(PersistedMatterBrain loaded, bool processing)
    {
        ArgumentNullException.ThrowIfNull(loaded);

        var gaps = FactualGapAnalyzer.Analyze(loaded.Evidence, loaded.Workplace, loaded.Brain);
        var activeDependencies = loaded.Brain.ActiveDependencies.ToArray();
        var sourceSpansById = loaded.Evidence.SourceSpans.ToDictionary(item => item.Id);
        var candidatesById = loaded.Brain.Candidates.ToDictionary(item => item.Id);
        var assertionsById = loaded.Evidence.Assertions.ToDictionary(item => item.Id);
        var eventsById = loaded.Evidence.Events.ToDictionary(item => item.Id);
        var peopleById = loaded.Brain.People.ToDictionary(item => item.Id);
        var extractedCanonicalRecords = loaded.Brain.Dependencies
            .Select(item => (item.CanonicalKind, item.CanonicalId))
            .ToHashSet();
        var activeCanonicalRecords = activeDependencies
            .Select(item => (item.CanonicalKind, item.CanonicalId))
            .ToHashSet();
        bool IsCurrentCanonical(CanonicalRecordKind kind, Guid id) =>
            !extractedCanonicalRecords.Contains((kind, id)) || activeCanonicalRecords.Contains((kind, id));

        var correctedAssertionIds = loaded.Evidence.AuditEvents
            .Where(item => item.Kind == AuditEventKind.AssertionCorrected && item.ReplacementEntityId.HasValue)
            .Select(item => item.ReplacementEntityId!.Value)
            .ToHashSet();
        var correctedEventIds = loaded.Evidence.AuditEvents
            .Where(item => item.Kind == AuditEventKind.EventCorrected && item.ReplacementEntityId.HasValue)
            .Select(item => item.ReplacementEntityId!.Value)
            .ToHashSet();
        bool HasCurrentCorrectionDateConflict(MatterEvent matterEvent) => loaded.Evidence.AssertionEventLinks
            .Where(link => link.EventId == matterEvent.Id && correctedAssertionIds.Contains(link.AssertionId))
            .Select(link => assertionsById.TryGetValue(link.AssertionId, out var assertion) ? assertion : null)
            .Where(assertion => assertion is not null &&
                                assertion.SupersededByAssertionId is null &&
                                assertion.DisputeState != DisputeState.Superseded &&
                                assertion.VerificationState != VerificationState.Rejected)
            .Any(assertion => assertion!.EventTime != matterEvent.StartTime);

        var currentAssertions = loaded.Evidence.Assertions
            .Where(item => item.SourceSpanId.HasValue &&
                           item.SupersededByAssertionId is null &&
                           item.DisputeState != DisputeState.Superseded &&
                           item.VerificationState != VerificationState.Rejected &&
                           item.OriginClass != EvidenceOriginClass.AiGeneratedInference &&
                           item.AssertionClass != AssertionClass.AiInference &&
                           IsCurrentCanonical(CanonicalRecordKind.Assertion, item.Id))
            .OrderBy(item => item.EventTime ?? item.AssertedAt)
            .ThenBy(item => item.Id)
            .ToArray();
        var currentAssertionIds = currentAssertions.Select(item => item.Id).ToHashSet();

        Guid[] CurrentLinkedSources(Guid eventId, params AssertionEventRelation[] relations)
        {
            var relationSet = relations.ToHashSet();
            return loaded.Evidence.AssertionEventLinks
                .Where(link => link.EventId == eventId &&
                               currentAssertionIds.Contains(link.AssertionId) &&
                               relationSet.Contains(link.Relation))
                .Join(currentAssertions, link => link.AssertionId, assertion => assertion.Id,
                    (_, assertion) => assertion.SourceSpanId!.Value)
                .Where(sourceSpansById.ContainsKey)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();
        }

        Guid[] HistoricalEventSources(Guid eventId)
        {
            var linked = loaded.Evidence.AssertionEventLinks
                .Where(link => link.EventId == eventId && link.Relation is
                    AssertionEventRelation.Supports or
                    AssertionEventRelation.Qualifies or
                    AssertionEventRelation.Contextualizes)
                .Select(link => assertionsById.TryGetValue(link.AssertionId, out var assertion)
                    ? assertion.SourceSpanId
                    : null)
                .Where(id => id.HasValue)
                .Select(id => id!.Value);
            var dependencies = loaded.Brain.Dependencies
                .Where(dependency => dependency.CanonicalKind == CanonicalRecordKind.Event &&
                                     dependency.CanonicalId == eventId)
                .Select(dependency => dependency.SourceSpanId);
            return linked.Concat(dependencies)
                .Where(sourceSpansById.ContainsKey)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();
        }

        var evidencePoints = currentAssertions
            .Take(MaximumPriorityItems)
            .Select(item => new
            {
                item.Id,
                item.SubjectReference,
                item.Predicate,
                item.Value,
                item.AssertedBy,
                item.EventTime,
                sourceSpanIds = new[] { item.SourceSpanId!.Value },
                origin = item.OriginClass.ToString(),
                assertionClass = item.AssertionClass.ToString(),
                dispute = item.DisputeState.ToString(),
                verification = item.VerificationState.ToString(),
                epistemicNotice = "Attributed evidence point; not automatically an established fact."
            }).ToArray();

        var chronology = loaded.Evidence.Events
            .Where(item => item.Status is not (EventStatus.Superseded or EventStatus.Rejected) &&
                           !correctedEventIds.Contains(item.Id) &&
                           IsCurrentCanonical(CanonicalRecordKind.Event, item.Id) &&
                           !HasCurrentCorrectionDateConflict(item))
            .Select(item =>
            {
                var supportingAssertionSourceIds = CurrentLinkedSources(item.Id, AssertionEventRelation.Supports);
                var qualifyingSourceSpanIds = CurrentLinkedSources(item.Id,
                    AssertionEventRelation.Qualifies, AssertionEventRelation.Contextualizes);
                var contradictingSourceSpanIds = CurrentLinkedSources(item.Id, AssertionEventRelation.Contradicts);
                var eventDependencySourceIds = activeDependencies
                    .Where(dependency => dependency.CanonicalKind == CanonicalRecordKind.Event &&
                                         dependency.CanonicalId == item.Id)
                    .Select(dependency => dependency.SourceSpanId);
                var sourceSpanIds = supportingAssertionSourceIds
                    .Concat(eventDependencySourceIds)
                    .Where(sourceSpansById.ContainsKey)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToArray();
                return new
                {
                    item.Id,
                    item.EventType,
                    item.Label,
                    item.StartTime,
                    item.EndTime,
                    status = item.Status.ToString(),
                    verification = item.VerificationState.ToString(),
                    sourceSpanIds,
                    qualifyingSourceSpanIds,
                    contradictingSourceSpanIds,
                    provenanceNotice = "Primary chronology citations are direct event extraction or supporting statements; qualifying/context and contradicting evidence are labelled separately."
                };
            })
            .Where(item => item.sourceSpanIds.Length > 0)
            .OrderByDescending(item => item.StartTime.HasValue)
            .ThenBy(item => item.StartTime)
            .ThenBy(item => item.Id)
            .Take(MaximumPriorityItems)
            .ToArray();

        var correctionHistory = loaded.Evidence.AuditEvents
            .Where(item => item.ReplacementEntityId.HasValue &&
                           item.Kind is AuditEventKind.AssertionCorrected or AuditEventKind.EventCorrected)
            .OrderBy(item => item.OccurredAt)
            .ThenBy(item => item.Id)
            .Select(audit =>
            {
                if (audit.Kind == AuditEventKind.AssertionCorrected &&
                    assertionsById.TryGetValue(audit.EntityId, out var originalAssertion) &&
                    assertionsById.TryGetValue(audit.ReplacementEntityId!.Value, out var correctedAssertion))
                {
                    var historicalSourceSpanIds = originalAssertion.SourceSpanId.HasValue &&
                                                  sourceSpansById.ContainsKey(originalAssertion.SourceSpanId.Value)
                        ? new[] { originalAssertion.SourceSpanId.Value }
                        : [];
                    return new CorrectionHistoryItem(
                        audit.Id,
                        audit.Kind.ToString(),
                        audit.Actor,
                        audit.OccurredAt,
                        audit.ChangeSummary,
                        AssertionSnapshot(originalAssertion),
                        AssertionSnapshot(correctedAssertion),
                        historicalSourceSpanIds,
                        "Historical documentary citations support only the original attributed statement. The corrected replacement is a separately attributed human correction and is not promoted to documentary fact.");
                }

                if (audit.Kind == AuditEventKind.EventCorrected &&
                    eventsById.TryGetValue(audit.EntityId, out var originalEvent) &&
                    eventsById.TryGetValue(audit.ReplacementEntityId!.Value, out var correctedEvent))
                {
                    return new CorrectionHistoryItem(
                        audit.Id,
                        audit.Kind.ToString(),
                        audit.Actor,
                        audit.OccurredAt,
                        audit.ChangeSummary,
                        EventSnapshot(originalEvent),
                        EventSnapshot(correctedEvent),
                        HistoricalEventSources(originalEvent.Id),
                        "Historical documentary citations support only the original event record. The corrected date or label is an audited human correction and is intentionally not cited as if the old evidence stated the corrected fields.");
                }

                return null;
            })
            .Where(item => item is not null)
            .Select(item => item!)
            .ToArray();

        var unresolvedDisputes = loaded.Evidence.Contradictions
            .Where(item => item.ResolutionState == ContradictionResolutionState.Unresolved &&
                           IsCurrentCanonical(CanonicalRecordKind.Contradiction, item.Id) &&
                           IsCurrentCanonical(CanonicalRecordKind.Assertion, item.AssertionAId) &&
                           IsCurrentCanonical(CanonicalRecordKind.Assertion, item.AssertionBId))
            .OrderBy(item => item.Id)
            .Select(item =>
            {
                assertionsById.TryGetValue(item.AssertionAId, out var first);
                assertionsById.TryGetValue(item.AssertionBId, out var second);
                var sourceSpanIds = new[] { first?.SourceSpanId, second?.SourceSpanId }
                    .Where(id => id.HasValue)
                    .Select(id => id!.Value)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToArray();
                return new
                {
                    item.Id,
                    type = item.Type.ToString(),
                    resolutionState = item.ResolutionState.ToString(),
                    assertions = new[]
                    {
                        DisputedAssertion(first),
                        DisputedAssertion(second)
                    }.Where(assertion => assertion is not null).ToArray(),
                    sourceSpanIds,
                    notice = "Conflicting attributed statements remain unresolved."
                };
            }).ToArray();

        var questionsToClarify = gaps.Select(item => new
        {
            item.Code,
            item.Summary,
            route = item.Code == "corrected-history-review" ? "prepare" : item.Route,
            item.RelatedRecordIds,
            item.SourceSpanIds,
            notice = item.Code == "corrected-history-review"
                ? "Review the dedicated correction history below; documentary citations remain attached only to the historical record they actually support."
                : "Evidence-review prompt only; not an accusation, legal finding or legal duty."
        }).ToArray();

        var currentPeople = loaded.Brain.People
            .Where(item => IsCurrentCanonical(CanonicalRecordKind.Person, item.Id))
            .ToArray();
        var participants = currentPeople
            .GroupBy(item => loaded.Brain.ResolveEntityId(CanonicalEntityKind.Person, item.Id))
            .Select(group =>
            {
                var resolvedId = group.Key;
                var representative = peopleById[resolvedId];
                var mergedIdentityIds = group.Select(item => item.Id).Distinct().OrderBy(id => id).ToArray();
                var mergedIdentityIdSet = mergedIdentityIds.ToHashSet();
                var activeCandidateIds = activeDependencies
                    .Where(dependency => dependency.CanonicalKind == CanonicalRecordKind.Person &&
                                         dependency.CanonicalId == representative.Id)
                    .Select(dependency => dependency.CandidateId)
                    .Distinct()
                    .ToHashSet();
                var activeCandidates = activeCandidateIds
                    .Where(candidatesById.ContainsKey)
                    .Select(id => candidatesById[id])
                    .Where(candidate => candidate.Kind == ExtractionCandidateKind.Person &&
                                        candidate.Disposition == CandidateDisposition.Validated)
                    .ToArray();
                var fieldsSourceBacked = activeCandidates.Length > 0 &&
                                         activeCandidates.All(candidate => CandidateSupportsParticipant(candidate, representative));
                var sourceSpanIds = fieldsSourceBacked
                    ? activeDependencies
                        .Where(dependency => dependency.CanonicalKind == CanonicalRecordKind.Person &&
                                             dependency.CanonicalId == representative.Id &&
                                             activeCandidateIds.Contains(dependency.CandidateId))
                        .Select(dependency => dependency.SourceSpanId)
                        .Where(sourceSpansById.ContainsKey)
                        .Distinct()
                        .OrderBy(id => id)
                        .ToArray()
                    : [];
                var documentVersionIds = sourceSpanIds
                    .Select(id => sourceSpansById[id].DocumentVersion.DocumentVersionId)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToArray();
                var identityAliases = loaded.Brain.Aliases
                    .Where(alias => alias.EntityKind == CanonicalEntityKind.Person &&
                                    mergedIdentityIdSet.Contains(alias.EntityId))
                    .GroupBy(alias => new { alias.NormalizedValue, alias.SourceSpanId })
                    .Select(aliasGroup => aliasGroup.First())
                    .OrderBy(alias => alias.Value, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(alias => alias.Id)
                    .Select(alias => new
                    {
                        alias.Value,
                        alias.SourceSpanId
                    })
                    .ToArray();
                var merged = mergedIdentityIds.Length > 1;
                return new
                {
                    Id = representative.Id,
                    representative.DisplayName,
                    representative.RoleLabels,
                    mergedIdentityIds,
                    identityAliases,
                    sourceSpanIds,
                    documentVersionIds,
                    provenanceStatus = fieldsSourceBacked ? "SourceBackedExtraction" : "Unsupported",
                    identityNotice = merged
                        ? "User-confirmed identity merge collapsed duplicate participant records; cited aliases remain visible for review."
                        : fieldsSourceBacked
                            ? "Extracted participant record from cited documentary evidence; identity and role labels still require review."
                            : activeCandidates.Length == 0
                                ? "Participant record has no active documentary provenance; verify the displayed name and role labels before relying on it."
                                : "Active extraction no longer exactly supports the stored participant name and role labels; verify these fields before relying on them."
                };
            })
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Id)
            .ToArray();

        var referencedSourceIds = evidencePoints.SelectMany(item => item.sourceSpanIds)
            .Concat(chronology.SelectMany(item => item.sourceSpanIds))
            .Concat(chronology.SelectMany(item => item.qualifyingSourceSpanIds))
            .Concat(chronology.SelectMany(item => item.contradictingSourceSpanIds))
            .Concat(correctionHistory.SelectMany(item => item.HistoricalSourceSpanIds))
            .Concat(unresolvedDisputes.SelectMany(item => item.sourceSpanIds))
            .Concat(gaps.SelectMany(item => item.SourceSpanIds))
            .Concat(participants.SelectMany(item => item.sourceSpanIds))
            .Concat(participants.SelectMany(item => item.identityAliases)
                .Where(alias => alias.SourceSpanId.HasValue)
                .Select(alias => alias.SourceSpanId!.Value))
            .ToHashSet();
        var sourceSpans = loaded.Evidence.SourceSpans
            .Where(item => referencedSourceIds.Contains(item.Id))
            .OrderBy(item => item.DocumentVersion.DocumentVersionId)
            .ThenBy(item => item.PageNumber)
            .ThenBy(item => item.TextStart)
            .ThenBy(item => item.Id)
            .Select(SourceSpan)
            .ToArray();
        var evidenceToHaveReady = loaded.Evidence.SourceSpans
            .Where(item => referencedSourceIds.Contains(item.Id))
            .GroupBy(item => item.DocumentVersion.DocumentVersionId)
            .OrderBy(group => group.Key)
            .Select(group => new
            {
                documentVersionId = group.Key,
                sourceSpanIds = group.Select(item => item.Id).OrderBy(id => id).ToArray(),
                prompt = "Review the cited immutable document-version spans before the meeting."
            }).ToArray();

        return new
        {
            processing,
            currentnessNotice = processing
                ? "Evidence processing is still active. This preparation view may change when ingestion completes."
                : "Preparation reflects the canonical Matter evidence state at the time this view was loaded.",
            evidencePoints,
            chronology,
            correctionHistory,
            participants,
            unresolvedDisputes,
            questionsToClarify,
            evidenceToHaveReady,
            sourceSpans,
            notices = new[]
            {
                "Meeting preparation is evidence organisation, not legal advice or a prediction of outcome.",
                "Attributed statements and unresolved contradictions remain labelled; CaseMesh does not silently resolve them.",
                "Human corrections are audit history, not documentary evidence; historical citations never silently transfer to corrected wording or dates.",
                "External legal guidance and Live meeting assistance are separate surfaces."
            }
        };
    }

    private static CorrectionRecordSnapshot AssertionSnapshot(Assertion assertion) => new(
        assertion.Id,
        "Assertion",
        null,
        null,
        assertion.SubjectReference,
        assertion.Predicate,
        assertion.Value,
        assertion.AssertedBy,
        assertion.EventTime,
        null,
        null,
        assertion.DisputeState.ToString(),
        assertion.VerificationState.ToString());

    private static CorrectionRecordSnapshot EventSnapshot(MatterEvent matterEvent) => new(
        matterEvent.Id,
        "Event",
        matterEvent.Label,
        matterEvent.EventType,
        null,
        null,
        null,
        null,
        null,
        matterEvent.StartTime,
        matterEvent.EndTime,
        matterEvent.Status.ToString(),
        matterEvent.VerificationState.ToString());

    private static bool CandidateSupportsParticipant(ExtractionCandidateRecord candidate, Person participant)
    {
        try
        {
            var entity = JsonSerializer.Deserialize<EntityCandidate>(candidate.PayloadJson);
            if (entity is null || entity.Kind != CanonicalEntityKind.Person ||
                !string.Equals(entity.DisplayName, participant.DisplayName, StringComparison.Ordinal))
            {
                return false;
            }

            var candidateRoles = entity.RoleLabels.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            var participantRoles = participant.RoleLabels.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            return candidateRoles.SequenceEqual(participantRoles, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static object? DisputedAssertion(Assertion? assertion)
    {
        if (assertion is null) return null;
        var historical = assertion.SupersededByAssertionId.HasValue || assertion.DisputeState == DisputeState.Superseded;
        var rejected = assertion.VerificationState == VerificationState.Rejected;
        var aiInference = assertion.OriginClass == EvidenceOriginClass.AiGeneratedInference ||
                          assertion.AssertionClass == AssertionClass.AiInference;
        return new
        {
            assertion.Id,
            assertion.AssertedBy,
            assertion.SubjectReference,
            assertion.Predicate,
            assertion.Value,
            assertion.SourceSpanId,
            origin = assertion.OriginClass.ToString(),
            assertionClass = assertion.AssertionClass.ToString(),
            dispute = assertion.DisputeState.ToString(),
            verification = assertion.VerificationState.ToString(),
            historical,
            rejected,
            aiInference,
            current = !historical && !rejected && !aiInference
        };
    }

    private static object SourceSpan(SourceSpan item) => new
    {
        item.Id,
        item.DocumentVersion.DocumentVersionId,
        item.PageNumber,
        item.TextStart,
        item.TextEnd,
        item.ExtractedText,
        item.ExtractedTextDigest,
        item.ParserVersion,
        item.ExtractionConfidence
    };

    private sealed record CorrectionHistoryItem(
        Guid Id,
        string Kind,
        string Actor,
        DateTimeOffset OccurredAt,
        string ChangeSummary,
        CorrectionRecordSnapshot Original,
        CorrectionRecordSnapshot Replacement,
        IReadOnlyList<Guid> HistoricalSourceSpanIds,
        string Notice);

    private sealed record CorrectionRecordSnapshot(
        Guid Id,
        string RecordType,
        string? Label,
        string? EventType,
        string? SubjectReference,
        string? Predicate,
        string? Value,
        string? AssertedBy,
        DateTimeOffset? EventTime,
        DateTimeOffset? StartTime,
        DateTimeOffset? EndTime,
        string Status,
        string Verification);
}
