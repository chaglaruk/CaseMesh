using CaseMesh.Core.Models;
using CaseMesh.Persistence.Postgres;
using CaseMesh.Qa;

namespace CaseMesh.Api;

internal static class WorkspaceProjection
{
    internal static object Matter(Matter matter) => new
    {
        matter.Id, tenantId = matter.TenantId.Value, matter.MatterType, matter.Title, matter.Status,
        matter.Jurisdiction, matter.CreatedAt, matter.UpdatedAt
    };

    internal static object Overview(PersistedMatterBrain loaded) => new
    {
        matter = Matter(loaded.Evidence.Matter),
        counts = new
        {
            documents = loaded.Evidence.DocumentVersions.Count,
            assertions = loaded.Evidence.Assertions.Count,
            events = loaded.Evidence.Events.Count,
            contradictions = loaded.Evidence.Contradictions.Count,
            people = loaded.Brain.People.Count
        },
        warning = "Structured records preserve who asserted each statement; they are not automatically established facts."
    };

    internal static object Timeline(PersistedMatterBrain loaded)
    {
        var events = loaded.Evidence.Events
            .OrderBy(item => item.StartTime).ThenBy(item => item.Id)
            .Select(item => new
            {
                item.Id, item.EventType, item.Label, item.StartTime, item.EndTime,
                status = item.Status.ToString(), verification = item.VerificationState.ToString(),
                sourceSpanIds = loaded.Evidence.AssertionEventLinks.Where(link => link.EventId == item.Id)
                    .Join(loaded.Evidence.Assertions, link => link.AssertionId, assertion => assertion.Id,
                        (_, assertion) => assertion.SourceSpanId).Where(id => id.HasValue)
                    .Select(id => id!.Value).Distinct().ToArray()
            }).ToArray();
        var sourceSpanIds = events.SelectMany(item => item.sourceSpanIds).ToHashSet();
        return new
        {
            events,
            sourceSpans = loaded.Evidence.SourceSpans.Where(item => sourceSpanIds.Contains(item.Id))
                .Select(SourceSpanProjection).ToArray()
        };
    }

    internal static object Evidence(PersistedMatterBrain loaded) => new
    {
        documentVersions = loaded.Evidence.DocumentVersions.Select(item => new
        {
            item.DocumentId, item.DocumentVersionId, item.OriginalObjectId, item.ContentSha256
        }).ToArray(),
        sourceSpans = loaded.Evidence.SourceSpans.Select(SourceSpanProjection).ToArray(),
        assertions = loaded.Evidence.Assertions.Select(Assertion).ToArray()
    };

    internal static object People(PersistedMatterBrain loaded) => new
    {
        people = loaded.Brain.People.Select(item => new { item.Id, item.DisplayName, item.RoleLabels }),
        organisations = loaded.Brain.Organisations.Select(item => new { item.Id, item.Name, item.TypeLabel })
    };

    internal static object Disputed(PersistedMatterBrain loaded)
    {
        var assertions = loaded.Evidence.Assertions
            .Where(item => item.DisputeState is DisputeState.Disputed or DisputeState.Contradicted)
            .ToArray();
        var sourceSpanIds = assertions.Where(item => item.SourceSpanId.HasValue)
            .Select(item => item.SourceSpanId!.Value).ToHashSet();
        return new
        {
            contradictions = loaded.Evidence.Contradictions.Select(item => new
            {
                item.Id, item.AssertionAId, item.AssertionBId, type = item.Type.ToString(),
                resolutionState = item.ResolutionState.ToString(), item.ResolutionNote
            }),
            disputedAssertions = assertions.Select(Assertion),
            sourceSpans = loaded.Evidence.SourceSpans.Where(item => sourceSpanIds.Contains(item.Id))
                .Select(SourceSpanProjection)
        };
    }

    internal static object Workplace(PersistedMatterBrain loaded) => new
    {
        employmentProfiles = loaded.Workplace.EmploymentProfiles,
        employmentTerms = loaded.Workplace.EmploymentTerms,
        healthAbsenceRecords = loaded.Workplace.HealthAbsenceRecords,
        adjustmentRequests = loaded.Workplace.AdjustmentRequests,
        workplaceProcesses = loaded.Workplace.WorkplaceProcesses,
        acasProcessStates = loaded.Workplace.AcasProcessStates
    };

    internal static object Questions(PersistedMatterBrain loaded, bool processing) => new
    {
        processing,
        gaps = FactualGapAnalyzer.Analyze(loaded.Evidence, loaded.Workplace, loaded.Brain),
        notice = "These are factual evidence gaps, not legal findings, accusations or advice."
    };

    internal static object QuestionAnswer(PersistedMatterBrain loaded, MatterQaAnswer answer)
    {
        var sourceIds = answer.Citations.Select(item => item.SourceSpanId).ToHashSet();
        return new
        {
            answer,
            sourceSpans = loaded.Evidence.SourceSpans.Where(item => sourceIds.Contains(item.Id))
                .Select(SourceSpanProjection).ToArray(),
            gaps = FactualGapAnalyzer.Analyze(loaded.Evidence, loaded.Workplace, loaded.Brain),
            currentnessNotice = "This answer reflects the Matter evidence state at generation time. Start a new thread after evidence changes."
        };
    }

    private static object Assertion(Assertion item) => new
    {
        item.Id, item.SubjectReference, item.Predicate, item.Value, item.AssertedBy, item.EventTime,
        item.AssertedAt, item.SourceSpanId, origin = item.OriginClass.ToString(),
        assertionClass = item.AssertionClass.ToString(), dispute = item.DisputeState.ToString(),
        integrity = item.IntegrityState.ToString(), verification = item.VerificationState.ToString(),
        item.ExtractionConfidence, item.SupersededByAssertionId,
        epistemicNotice = "This is an attributed assertion, not an established fact."
    };

    private static object SourceSpanProjection(SourceSpan item) => new
    {
        item.Id, item.DocumentVersion.DocumentVersionId, item.PageNumber, item.TextStart, item.TextEnd,
        item.ExtractedText, item.ExtractedTextDigest, item.ParserVersion, item.ExtractionConfidence
    };
}
