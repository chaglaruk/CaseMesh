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
        var extractedCanonicalRecords = loaded.Brain.Dependencies
            .Select(item => (item.CanonicalKind, item.CanonicalId))
            .ToHashSet();
        var activeCanonicalRecords = activeDependencies
            .Select(item => (item.CanonicalKind, item.CanonicalId))
            .ToHashSet();
        bool IsCurrentCanonical(CanonicalRecordKind kind, Guid id) =>
            !extractedCanonicalRecords.Contains((kind, id)) || activeCanonicalRecords.Contains((kind, id));

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
        var assertionsById = loaded.Evidence.Assertions.ToDictionary(item => item.Id);

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
                           IsCurrentCanonical(CanonicalRecordKind.Event, item.Id))
            .Select(item =>
            {
                var linkedAssertionSourceIds = loaded.Evidence.AssertionEventLinks
                    .Where(link => link.EventId == item.Id && currentAssertionIds.Contains(link.AssertionId))
                    .Join(currentAssertions, link => link.AssertionId, assertion => assertion.Id,
                        (_, assertion) => assertion.SourceSpanId!.Value);
                var eventDependencySourceIds = activeDependencies
                    .Where(dependency => dependency.CanonicalKind == CanonicalRecordKind.Event &&
                                         dependency.CanonicalId == item.Id)
                    .Select(dependency => dependency.SourceSpanId);
                var sourceSpanIds = linkedAssertionSourceIds
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
                    sourceSpanIds
                };
            })
            .Where(item => item.sourceSpanIds.Length > 0)
            .OrderByDescending(item => item.StartTime.HasValue)
            .ThenBy(item => item.StartTime)
            .ThenBy(item => item.Id)
            .Take(MaximumPriorityItems)
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
            item.Route,
            item.RelatedRecordIds,
            item.SourceSpanIds,
            notice = "Evidence-review prompt only; not an accusation, legal finding or legal duty."
        }).ToArray();

        var participants = loaded.Brain.People
            .Where(item => IsCurrentCanonical(CanonicalRecordKind.Person, item.Id))
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Id)
            .Select(item =>
            {
                var activeCandidateIds = activeDependencies
                    .Where(dependency => dependency.CanonicalKind == CanonicalRecordKind.Person &&
                                         dependency.CanonicalId == item.Id)
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
                                         activeCandidates.All(candidate => CandidateSupportsParticipant(candidate, item));
                var sourceSpanIds = fieldsSourceBacked
                    ? activeDependencies
                        .Where(dependency => dependency.CanonicalKind == CanonicalRecordKind.Person &&
                                             dependency.CanonicalId == item.Id &&
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
                return new
                {
                    item.Id,
                    item.DisplayName,
                    item.RoleLabels,
                    sourceSpanIds,
                    documentVersionIds,
                    provenanceStatus = fieldsSourceBacked ? "SourceBackedExtraction" : "Unsupported",
                    identityNotice = fieldsSourceBacked
                        ? "Extracted participant record from cited documentary evidence; identity and role labels still require review."
                        : activeCandidates.Length == 0
                            ? "Participant record has no active documentary provenance; verify the displayed name and role labels before relying on it."
                            : "Active extraction no longer exactly supports the stored participant name and role labels; verify these fields before relying on them."
                };
            }).ToArray();

        var referencedSourceIds = evidencePoints.SelectMany(item => item.sourceSpanIds)
            .Concat(chronology.SelectMany(item => item.sourceSpanIds))
            .Concat(unresolvedDisputes.SelectMany(item => item.sourceSpanIds))
            .Concat(gaps.SelectMany(item => item.SourceSpanIds))
            .Concat(participants.SelectMany(item => item.sourceSpanIds))
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
            participants,
            unresolvedDisputes,
            questionsToClarify,
            evidenceToHaveReady,
            sourceSpans,
            notices = new[]
            {
                "Meeting preparation is evidence organisation, not legal advice or a prediction of outcome.",
                "Attributed statements and unresolved contradictions remain labelled; CaseMesh does not silently resolve them.",
                "External legal guidance and Live meeting assistance are separate surfaces."
            }
        };
    }

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
}
