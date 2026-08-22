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
        var extractedAssertionIds = loaded.Brain.Dependencies
            .Where(item => item.CanonicalKind == CanonicalRecordKind.Assertion)
            .Select(item => item.CanonicalId)
            .ToHashSet();
        var activeExtractedAssertionIds = activeDependencies
            .Where(item => item.CanonicalKind == CanonicalRecordKind.Assertion)
            .Select(item => item.CanonicalId)
            .ToHashSet();
        var currentAssertions = loaded.Evidence.Assertions
            .Where(item => item.SourceSpanId.HasValue &&
                           item.SupersededByAssertionId is null &&
                           item.DisputeState != DisputeState.Superseded &&
                           item.VerificationState != VerificationState.Rejected &&
                           item.OriginClass != EvidenceOriginClass.AiGeneratedInference &&
                           item.AssertionClass != AssertionClass.AiInference &&
                           (!extractedAssertionIds.Contains(item.Id) || activeExtractedAssertionIds.Contains(item.Id)))
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
            .Where(item => item.Status is not (EventStatus.Superseded or EventStatus.Rejected))
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
            .Where(item => item.ResolutionState == ContradictionResolutionState.Unresolved)
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
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Id)
            .Select(item =>
            {
                var sourceSpanIds = activeDependencies
                    .Where(dependency => dependency.CanonicalKind == CanonicalRecordKind.Person &&
                                         dependency.CanonicalId == item.Id)
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
                    item.Id,
                    item.DisplayName,
                    item.RoleLabels,
                    sourceSpanIds,
                    documentVersionIds,
                    provenanceStatus = sourceBacked ? "SourceBackedExtraction" : "Unsupported",
                    identityNotice = sourceBacked
                        ? "Extracted participant record from cited documentary evidence; identity and role labels still require review."
                        : "Participant record has no active documentary provenance; verify the displayed name and role labels before relying on it."
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
