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
        var policy = new CanonicalEvidencePolicy(loaded.Brain);

        var correctedAssertionIds = loaded.Evidence.AuditEvents
            .Where(item => item.Kind == AuditEventKind.AssertionCorrected && item.ReplacementEntityId.HasValue)
            .Select(item => item.ReplacementEntityId!.Value)
            .ToHashSet();
        var correctedEventIds = loaded.Evidence.AuditEvents
            .Where(item => item.Kind == AuditEventKind.EventCorrected && item.ReplacementEntityId.HasValue)
            .Select(item => item.ReplacementEntityId!.Value)
            .ToHashSet();
        bool HasCurrentCorrectionDateConflict(MatterEvent matterEvent) => loaded.Evidence.AssertionEventLinks
            .Where(link => link.EventId == matterEvent.Id &&
                           policy.IsCurrentAssertionEventLink(link.Id) &&
                           link.Relation == AssertionEventRelation.Supports &&
                           correctedAssertionIds.Contains(link.AssertionId))
            .Select(link => assertionsById.TryGetValue(link.AssertionId, out var assertion) ? assertion : null)
            .Where(assertion => assertion is not null &&
                                policy.IsCurrentAttributedAssertion(assertion, requireDocumentarySource: false))
            .Any(assertion => assertion!.EventTime.HasValue && assertion.EventTime != matterEvent.StartTime);

        var currentAssertions = loaded.Evidence.Assertions
            .Where(item => policy.IsCurrentAttributedAssertion(item, requireDocumentarySource: true))
            .OrderBy(item => item.EventTime ?? item.AssertedAt)
            .ThenBy(item => item.Id)
            .ToArray();
        var currentAssertionIds = currentAssertions.Select(item => item.Id).ToHashSet();
        var currentSupersedingAssertionIds = loaded.Evidence.AssertionEventLinks
            .Where(link => policy.IsCurrentAssertionEventLink(link.Id) &&
                           link.Relation == AssertionEventRelation.Supersedes &&
                           currentAssertionIds.Contains(link.AssertionId))
            .Select(link => link.AssertionId)
            .ToHashSet();
        bool HasCurrentSupersedingEvidence(Guid eventId) => loaded.Evidence.AssertionEventLinks.Any(link =>
            link.EventId == eventId &&
            policy.IsCurrentAssertionEventLink(link.Id) &&
            link.Relation == AssertionEventRelation.Supersedes &&
            currentAssertionIds.Contains(link.AssertionId));

        Guid[] CurrentLinkedSources(MatterEvent matterEvent, params AssertionEventRelation[] relations)
        {
            var relationSet = relations.ToHashSet();
            return loaded.Evidence.AssertionEventLinks
                .Where(link => link.EventId == matterEvent.Id &&
                               policy.IsCurrentAssertionEventLink(link.Id) &&
                               currentAssertionIds.Contains(link.AssertionId) &&
                               relationSet.Contains(link.Relation))
                .Join(currentAssertions, link => link.AssertionId, assertion => assertion.Id,
                    (link, assertion) => new { link, assertion })
                .Where(item => item.link.Relation != AssertionEventRelation.Supports ||
                               !matterEvent.StartTime.HasValue ||
                               (item.assertion.EventTime.HasValue &&
                                item.assertion.EventTime == matterEvent.StartTime))
                .Select(item => item.assertion.SourceSpanId!.Value)
                .Where(sourceSpansById.ContainsKey)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();
        }

        Guid[] UndatedSupportingSources(MatterEvent matterEvent)
        {
            if (!matterEvent.StartTime.HasValue) return [];
            return loaded.Evidence.AssertionEventLinks
                .Where(link => link.EventId == matterEvent.Id &&
                               policy.IsCurrentAssertionEventLink(link.Id) &&
                               link.Relation == AssertionEventRelation.Supports &&
                               currentAssertionIds.Contains(link.AssertionId))
                .Join(currentAssertions, link => link.AssertionId, assertion => assertion.Id,
                    (_, assertion) => assertion)
                .Where(assertion => !assertion.EventTime.HasValue)
                .Select(assertion => assertion.SourceSpanId!.Value)
                .Where(sourceSpansById.ContainsKey)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();
        }

        Guid[] MismatchedSupportingSources(MatterEvent matterEvent)
        {
            if (!matterEvent.StartTime.HasValue) return [];
            return loaded.Evidence.AssertionEventLinks
                .Where(link => link.EventId == matterEvent.Id &&
                               policy.IsCurrentAssertionEventLink(link.Id) &&
                               link.Relation == AssertionEventRelation.Supports &&
                               currentAssertionIds.Contains(link.AssertionId))
                .Join(currentAssertions, link => link.AssertionId, assertion => assertion.Id,
                    (_, assertion) => assertion)
                .Where(assertion => assertion.EventTime.HasValue &&
                                    assertion.EventTime != matterEvent.StartTime)
                .Select(assertion => assertion.SourceSpanId!.Value)
                .Where(sourceSpansById.ContainsKey)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();
        }

        HistoricalEventProvenance HistoricalEventSources(MatterEvent matterEvent)
        {
            var linked = loaded.Evidence.AssertionEventLinks
                .Where(link => link.EventId == matterEvent.Id)
                .Select(link => new
                {
                    link.Relation,
                    Assertion = assertionsById.TryGetValue(link.AssertionId, out var assertion) ? assertion : null
                })
                .Where(item => item.Assertion?.SourceSpanId is not null &&
                               sourceSpansById.ContainsKey(item.Assertion.SourceSpanId.Value))
                .ToArray();
            var directEventSources = loaded.Brain.Dependencies
                .Where(dependency => dependency.CanonicalKind == CanonicalRecordKind.Event &&
                                     dependency.CanonicalId == matterEvent.Id)
                .Select(dependency => dependency.SourceSpanId);
            var supporting = linked
                .Where(item => item.Relation == AssertionEventRelation.Supports &&
                               (!matterEvent.StartTime.HasValue ||
                                (item.Assertion!.EventTime.HasValue &&
                                 item.Assertion.EventTime == matterEvent.StartTime)))
                .Select(item => item.Assertion!.SourceSpanId!.Value)
                .Concat(directEventSources)
                .Where(sourceSpansById.ContainsKey)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();
            var qualifying = linked
                .Where(item => item.Relation is AssertionEventRelation.Qualifies or AssertionEventRelation.Contextualizes ||
                               (item.Relation == AssertionEventRelation.Supports &&
                                matterEvent.StartTime.HasValue &&
                                !item.Assertion!.EventTime.HasValue))
                .Select(item => item.Assertion!.SourceSpanId!.Value)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();
            var contradicting = linked
                .Where(item => item.Relation == AssertionEventRelation.Contradicts ||
                               (item.Relation == AssertionEventRelation.Supports &&
                                matterEvent.StartTime.HasValue &&
                                item.Assertion!.EventTime.HasValue &&
                                item.Assertion.EventTime != matterEvent.StartTime))
                .Select(item => item.Assertion!.SourceSpanId!.Value)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();
            return new HistoricalEventProvenance(supporting, qualifying, contradicting);
        }

        var priorityAssertionIds = currentAssertions
            .Take(MaximumPriorityItems)
            .Select(item => item.Id)
            .ToHashSet();
        var evidencePoints = currentAssertions
            .Where(item => priorityAssertionIds.Contains(item.Id) || currentSupersedingAssertionIds.Contains(item.Id))
            .Select(item => new
            {
                item.Id,
                item.SubjectReference,
                item.Predicate,
                item.Value,
                item.AssertedBy,
                item.AssertedAt,
                item.EventTime,
                sourceSpanIds = new[] { item.SourceSpanId!.Value },
                origin = item.OriginClass.ToString(),
                assertionClass = item.AssertionClass.ToString(),
                integrity = item.IntegrityState.ToString(),
                item.ExtractionConfidence,
                dispute = item.DisputeState.ToString(),
                verification = item.VerificationState.ToString(),
                epistemicNotice = currentSupersedingAssertionIds.Contains(item.Id)
                    ? "Attributed superseding evidence kept visible even when it falls outside the capped priority list; not automatically an established fact."
                    : "Attributed evidence point; not automatically an established fact."
            }).ToArray();

        var chronology = loaded.Evidence.Events
            .Where(item => policy.IsCurrentEvent(item) &&
                           !correctedEventIds.Contains(item.Id) &&
                           !HasCurrentSupersedingEvidence(item.Id) &&
                           !HasCurrentCorrectionDateConflict(item))
            .Select(item =>
            {
                var supportingAssertionSourceIds = CurrentLinkedSources(item, AssertionEventRelation.Supports);
                var qualifyingSourceSpanIds = CurrentLinkedSources(item,
                        AssertionEventRelation.Qualifies, AssertionEventRelation.Contextualizes)
                    .Concat(UndatedSupportingSources(item))
                    .Distinct()
                    .OrderBy(id => id)
                    .ToArray();
                var contradictingSourceSpanIds = CurrentLinkedSources(item, AssertionEventRelation.Contradicts)
                    .Concat(MismatchedSupportingSources(item))
                    .Distinct()
                    .OrderBy(id => id)
                    .ToArray();
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
                    provenanceNotice = "Primary chronology citations are complete direct event extraction or supporting statements with explicit alleged time agreeing with a displayed event date; undated supporting statements are contextual, and qualifying/context plus contradicting or date-mismatched evidence are labelled separately."
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
                    var originalIsAiInference =
                        originalAssertion.OriginClass == EvidenceOriginClass.AiGeneratedInference ||
                        originalAssertion.AssertionClass == AssertionClass.AiInference;
                    return new CorrectionHistoryItem(
                        audit.Id,
                        audit.Kind.ToString(),
                        audit.Actor,
                        audit.OccurredAt,
                        audit.ChangeSummary,
                        AssertionSnapshot(originalAssertion),
                        AssertionSnapshot(correctedAssertion),
                        historicalSourceSpanIds,
                        [],
                        [],
                        originalIsAiInference
                            ? "The historical record was AI inference, not documentary evidence. It has no documentary citation to transfer; the replacement is a separately attributed human correction."
                            : "Historical documentary citations support only the original attributed statement. The corrected replacement is a separately attributed human correction and is not promoted to documentary fact.");
                }

                if (audit.Kind == AuditEventKind.EventCorrected &&
                    eventsById.TryGetValue(audit.EntityId, out var originalEvent) &&
                    eventsById.TryGetValue(audit.ReplacementEntityId!.Value, out var correctedEvent))
                {
                    var historical = HistoricalEventSources(originalEvent);
                    return new CorrectionHistoryItem(
                        audit.Id,
                        audit.Kind.ToString(),
                        audit.Actor,
                        audit.OccurredAt,
                        audit.ChangeSummary,
                        EventSnapshot(originalEvent),
                        EventSnapshot(correctedEvent),
                        historical.SupportingSourceSpanIds,
                        historical.QualifyingSourceSpanIds,
                        historical.ContradictingSourceSpanIds,
                        "Historical supporting citations apply only to the original event record. Qualifying/context and contradicting or date-mismatched sources are labelled separately. The corrected date or label is an audited human correction and is intentionally not cited as if old evidence stated the corrected fields.");
                }

                return null;
            })
            .Where(item => item is not null)
            .Select(item => item!)
            .ToArray();

        var unresolvedDisputes = loaded.Evidence.Contradictions
            .Where(item => item.ResolutionState == ContradictionResolutionState.Unresolved &&
                           policy.IsCurrentContradiction(item) &&
                           assertionsById.TryGetValue(item.AssertionAId, out var first) &&
                           policy.IsCurrentAttributedAssertion(first, requireDocumentarySource: false) &&
                           assertionsById.TryGetValue(item.AssertionBId, out var second) &&
                           policy.IsCurrentAttributedAssertion(second, requireDocumentarySource: false))
            .OrderBy(item => item.Id)
            .Select(item =>
            {
                var first = assertionsById[item.AssertionAId];
                var second = assertionsById[item.AssertionBId];
                var sourceSpanIds = new[] { first.SourceSpanId, second.SourceSpanId }
                    .Where(id => id.HasValue)
                    .Select(id => id!.Value)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToArray();
                var detectionOrigin = policy.GetContradictionDetectionOrigin(item);
                var aiAnalysis = detectionOrigin == ContradictionDetectionOrigin.StructuredExtractionAnalysis;
                return new
                {
                    item.Id,
                    type = item.Type.ToString(),
                    resolutionState = item.ResolutionState.ToString(),
                    detectionOrigin = detectionOrigin.ToString(),
                    aiAnalysis,
                    assertions = new[]
                    {
                        DisputedAssertion(first),
                        DisputedAssertion(second)
                    }.Where(assertion => assertion is not null).ToArray(),
                    sourceSpanIds,
                    notice = aiAnalysis
                        ? "AI analysis detected a possible conflict between current attributed statements. Review the cited documentary evidence; the analysis is not itself documentary fact."
                        : detectionOrigin == ContradictionDetectionOrigin.DeterministicRule
                            ? "A deterministic evidence rule detected conflicting current attributed statements. The rule does not decide which account is true."
                            : "Conflicting attributed statements remain unresolved."
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
            .Where(item => policy.IsCanonicalCurrent(CanonicalRecordKind.Person, item.Id))
            .ToArray();
        var participants = currentPeople
            .GroupBy(item => loaded.Brain.ResolveEntityId(CanonicalEntityKind.Person, item.Id))
            .Select(group =>
            {
                var currentMembers = group.OrderBy(item => item.Id).ToArray();
                var resolvedId = group.Key;
                var representative = currentMembers.FirstOrDefault(item => item.Id == resolvedId) ?? currentMembers[0];
                var mergedIdentityIds = currentMembers.Select(item => item.Id).ToArray();
                var mergedIdentityIdSet = mergedIdentityIds.ToHashSet();
                var identityMembers = currentMembers
                    .Select(member =>
                    {
                        var activeCandidateIds = activeDependencies
                            .Where(dependency => dependency.CanonicalKind == CanonicalRecordKind.Person &&
                                                 dependency.CanonicalId == member.Id)
                            .Select(dependency => dependency.CandidateId)
                            .Distinct()
                            .ToHashSet();
                        var activeCandidates = activeCandidateIds
                            .Where(candidatesById.ContainsKey)
                            .Select(id => candidatesById[id])
                            .Where(candidate => candidate.Kind == ExtractionCandidateKind.Person &&
                                                candidate.Disposition == CandidateDisposition.Validated)
                            .ToArray();
                        var completeActiveCandidates = activeCandidates
                            .Where(candidate => policy.HasCompleteValidatedCandidateProvenance(
                                CanonicalRecordKind.Person, member.Id, ExtractionCandidateKind.Person))
                            .ToArray();
                        var fieldsSourceBacked = completeActiveCandidates.Length > 0 &&
                                                 completeActiveCandidates.All(candidate => CandidateSupportsParticipant(candidate, member));
                        var sourceBackedCandidateIds = completeActiveCandidates.Select(candidate => candidate.Id).ToHashSet();
                        var sourceSpanIds = fieldsSourceBacked
                            ? activeDependencies
                                .Where(dependency => dependency.CanonicalKind == CanonicalRecordKind.Person &&
                                                     dependency.CanonicalId == member.Id &&
                                                     sourceBackedCandidateIds.Contains(dependency.CandidateId))
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
                        return new
                        {
                            member.Id,
                            member.DisplayName,
                            member.RoleLabels,
                            sourceSpanIds,
                            documentVersionIds,
                            provenanceStatus = fieldsSourceBacked ? "SourceBackedExtraction" : "Unsupported",
                            provenanceNotice = fieldsSourceBacked
                                ? "Current merged identity member fields are backed by the complete cited extraction source set; identity and role labels still require review."
                                : activeCandidates.Length == 0
                                    ? "Current merged identity member has no active documentary provenance; verify its name and role labels before relying on them."
                                    : completeActiveCandidates.Length == 0
                                        ? "Active extraction provenance is incomplete after source invalidation; no surviving span is promoted as field-level support."
                                        : "Active extraction no longer exactly supports this member's stored name and role labels; verify these fields before relying on them."
                        };
                    })
                    .ToArray();
                var representativeMember = identityMembers.Single(item => item.Id == representative.Id);
                var identityAliases = loaded.Brain.Aliases
                    .Where(alias => alias.EntityKind == CanonicalEntityKind.Person &&
                                    mergedIdentityIdSet.Contains(alias.EntityId))
                    .GroupBy(alias => alias.NormalizedValue, StringComparer.Ordinal)
                    .Select(aliasGroup =>
                    {
                        var alias = aliasGroup.OrderBy(item => item.Id).First();
                        var activeAliasCandidateIds = activeDependencies
                            .Where(dependency => dependency.CanonicalKind == CanonicalRecordKind.Person &&
                                                 mergedIdentityIdSet.Contains(dependency.CanonicalId))
                            .Select(dependency => new { dependency.CandidateId, dependency.CanonicalId })
                            .Distinct()
                            .Where(item => candidatesById.ContainsKey(item.CandidateId))
                            .Where(item => CandidateSupportsAlias(candidatesById[item.CandidateId], alias.NormalizedValue))
                            .Where(item => policy.HasCompleteValidatedCandidateProvenance(
                                CanonicalRecordKind.Person, item.CanonicalId, ExtractionCandidateKind.Person))
                            .Select(item => item.CandidateId)
                            .ToHashSet();
                        var sourceSpanIds = activeDependencies
                            .Where(dependency => dependency.CanonicalKind == CanonicalRecordKind.Person &&
                                                 mergedIdentityIdSet.Contains(dependency.CanonicalId) &&
                                                 activeAliasCandidateIds.Contains(dependency.CandidateId))
                            .Select(dependency => dependency.SourceSpanId)
                            .Where(sourceSpansById.ContainsKey)
                            .Distinct()
                            .OrderBy(id => id)
                            .ToArray();
                        var documentVersionIds = sourceSpanIds
                            .Select(id => sourceSpansById[id].DocumentVersion.DocumentVersionId)
                            .Distinct()
                            .OrderBy(id => id)
                            .ToArray();
                        var sourceBacked = sourceSpanIds.Length > 0;
                        return new
                        {
                            alias.Value,
                            sourceSpanIds,
                            documentVersionIds,
                            provenanceStatus = sourceBacked ? "SourceBackedExtraction" : "Unsupported",
                            provenanceNotice = sourceBacked
                                ? "Alias is backed by the complete cited active extraction source set."
                                : "Alias has no complete current documentary provenance; verify it before relying on it."
                        };
                    })
                    .OrderBy(alias => alias.Value, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var merged = currentMembers.Length > 1;
                return new
                {
                    Id = representative.Id,
                    representative.DisplayName,
                    representative.RoleLabels,
                    mergedIdentityIds,
                    identityMembers,
                    identityAliases,
                    sourceSpanIds = representativeMember.sourceSpanIds,
                    documentVersionIds = representativeMember.documentVersionIds,
                    representativeMember.provenanceStatus,
                    identityNotice = merged
                        ? "User-confirmed identity merge collapsed current participant records; each current member's recorded roles and exact provenance are listed separately for review."
                        : representativeMember.provenanceNotice
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
            .Concat(correctionHistory.SelectMany(item => item.HistoricalQualifyingSourceSpanIds))
            .Concat(correctionHistory.SelectMany(item => item.HistoricalContradictingSourceSpanIds))
            .Concat(unresolvedDisputes.SelectMany(item => item.sourceSpanIds))
            .Concat(gaps.SelectMany(item => item.SourceSpanIds))
            .Concat(participants.SelectMany(item => item.sourceSpanIds))
            .Concat(participants.SelectMany(item => item.identityMembers)
                .SelectMany(member => member.sourceSpanIds))
            .Concat(participants.SelectMany(item => item.identityAliases)
                .SelectMany(alias => alias.sourceSpanIds))
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
                "AI-derived contradiction detection is analysis and is explicitly separated from documentary evidence.",
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
        assertion.AssertedAt,
        assertion.EventTime,
        null,
        null,
        assertion.OriginClass.ToString(),
        assertion.AssertionClass.ToString(),
        assertion.IntegrityState.ToString(),
        assertion.ExtractionConfidence,
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
        null,
        matterEvent.StartTime,
        matterEvent.EndTime,
        null,
        null,
        null,
        null,
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

            var candidateRoles = entity.RoleLabels
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            var participantRoles = participant.RoleLabels
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            return candidateRoles.SequenceEqual(participantRoles, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool CandidateSupportsAlias(ExtractionCandidateRecord candidate, string normalizedAlias)
    {
        if (candidate.Kind != ExtractionCandidateKind.Person || candidate.Disposition != CandidateDisposition.Validated)
        {
            return false;
        }

        try
        {
            var entity = JsonSerializer.Deserialize<EntityCandidate>(candidate.PayloadJson);
            return entity is { Kind: CanonicalEntityKind.Person } &&
                   entity.Aliases.Append(entity.DisplayName)
                       .Any(value => string.Equals(NormalizeAlias(value), normalizedAlias, StringComparison.Ordinal));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string NormalizeAlias(string value) =>
        string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();

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
            assertion.AssertedAt,
            assertion.EventTime,
            assertion.SourceSpanId,
            origin = assertion.OriginClass.ToString(),
            assertionClass = assertion.AssertionClass.ToString(),
            integrity = assertion.IntegrityState.ToString(),
            assertion.ExtractionConfidence,
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

    private sealed record HistoricalEventProvenance(
        IReadOnlyList<Guid> SupportingSourceSpanIds,
        IReadOnlyList<Guid> QualifyingSourceSpanIds,
        IReadOnlyList<Guid> ContradictingSourceSpanIds);

    private sealed record CorrectionHistoryItem(
        Guid Id,
        string Kind,
        string Actor,
        DateTimeOffset OccurredAt,
        string ChangeSummary,
        CorrectionRecordSnapshot Original,
        CorrectionRecordSnapshot Replacement,
        IReadOnlyList<Guid> HistoricalSourceSpanIds,
        IReadOnlyList<Guid> HistoricalQualifyingSourceSpanIds,
        IReadOnlyList<Guid> HistoricalContradictingSourceSpanIds,
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
        DateTimeOffset? AssertedAt,
        DateTimeOffset? EventTime,
        DateTimeOffset? StartTime,
        DateTimeOffset? EndTime,
        string? Origin,
        string? AssertionClass,
        string? Integrity,
        decimal? ExtractionConfidence,
        string Status,
        string Verification);
}
