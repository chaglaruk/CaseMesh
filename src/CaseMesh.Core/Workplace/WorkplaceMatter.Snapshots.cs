using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Core.Snapshots;

namespace CaseMesh.Core.Workplace;

public sealed partial class WorkplaceMatter
{
    public WorkplaceSnapshot CaptureSnapshot() => new(
        EmploymentProfiles.Select(profile => new EmploymentProfileSnapshot(
            profile.Id,
            profile.EmployerReference,
            profile.RoleTitle,
            profile.EmploymentStartedOn,
            profile.EmploymentEndedOn,
            profile.SupportingAssertionIds,
            profile.EvidenceReviewState)).ToArray(),
        EmploymentTerms.Select(term => new EmploymentTermSnapshot(
            term.Id,
            term.Kind,
            term.Value,
            term.EffectiveFrom,
            term.EffectiveTo,
            term.SupportingAssertionIds,
            term.SupersedesEmploymentTermId)).ToArray(),
        HealthAbsenceRecords.Select(record => new HealthAbsenceSnapshot(
            record.Id,
            record.Kind,
            record.NeutralLabel,
            record.AssertionIds,
            record.EventIds,
            record.EvidenceReviewState)).ToArray(),
        AdjustmentRequests.Select(request => new AdjustmentRequestSnapshot(
            request.Id,
            request.NeutralLabel,
            request.RequestAssertionIds,
            request.ResponseStatus,
            request.ResponseAssertionIds,
            request.ImplementationAssertionIds)).ToArray(),
        WorkplaceProcesses.Select(process => new WorkplaceProcessSnapshot(
            process.Id,
            process.Kind,
            process.StageLabel,
            process.Status,
            process.AssertionIds,
            process.EventIds,
            process.SupersedesWorkplaceProcessId,
            process.SupersessionAuditEventId)).ToArray(),
        AcasProcessStates.Select(state => new AcasProcessStateSnapshot(
            state.Id,
            state.Stage,
            state.AssertionIds,
            state.EventIds)).ToArray());

    public static WorkplaceMatter Rehydrate(MatterEvidenceGraph evidence, WorkplaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(snapshot);
        RequireSnapshotLists(snapshot);
        var workplace = new WorkplaceMatter(evidence);
        var assertions = evidence.Assertions.ToDictionary(assertion => assertion.Id);
        var events = evidence.Events.ToDictionary(matterEvent => matterEvent.Id);

        RequireUniqueIds(snapshot.EmploymentProfiles.Select(item => item.Id), "employment profile");
        foreach (var profile in snapshot.EmploymentProfiles)
        {
            RequireDefinedEnum(profile.EvidenceReviewState);
            RequireId(profile.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(profile.EmployerReference);
            ArgumentException.ThrowIfNullOrWhiteSpace(profile.RoleTitle);
            ValidatePeriod(profile.EmploymentStartedOn, profile.EmploymentEndedOn, nameof(profile.EmploymentEndedOn));
            var assertionIds = ValidateAssertions(profile.SupportingAssertionIds, assertions);
            if (profile.EvidenceReviewState == VerificationState.Confirmed && assertionIds.Count == 0)
            {
                throw new InvalidOperationException("A persisted confirmed employment profile requires supporting assertions.");
            }

            workplace._employmentProfiles.Add(profile.Id, new EmploymentProfile(
                profile.Id,
                evidence.Matter.Id,
                profile.EmployerReference,
                profile.RoleTitle,
                profile.EmploymentStartedOn,
                profile.EmploymentEndedOn,
                assertionIds,
                profile.EvidenceReviewState));
        }

        RequireUniqueIds(snapshot.EmploymentTerms.Select(item => item.Id), "employment term");
        foreach (var term in snapshot.EmploymentTerms)
        {
            RequireDefinedEnum(term.Kind);
            RequireId(term.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(term.Value);
            ValidatePeriod(term.EffectiveFrom, term.EffectiveTo, nameof(term.EffectiveTo));
            var assertionIds = ValidateAssertions(term.SupportingAssertionIds, assertions, requireAny: true, requireSourceBacking: true);
            workplace._employmentTerms.Add(term.Id, new EmploymentTerm(
                term.Id,
                evidence.Matter.Id,
                term.Kind,
                term.Value,
                term.EffectiveFrom,
                term.EffectiveTo,
                assertionIds,
                term.SupersedesEmploymentTermId));
        }

        foreach (var term in workplace._employmentTerms.Values.Where(item => item.SupersedesEmploymentTermId.HasValue))
        {
            if (!workplace._employmentTerms.TryGetValue(term.SupersedesEmploymentTermId!.Value, out var previous) ||
                previous.Kind != term.Kind)
            {
                throw new InvalidOperationException("Persisted employment-term supersession is invalid.");
            }
        }

        RequireUniqueIds(snapshot.HealthAbsenceRecords.Select(item => item.Id), "health/absence record");
        foreach (var record in snapshot.HealthAbsenceRecords)
        {
            RequireDefinedEnum(record.Kind);
            RequireDefinedEnum(record.EvidenceReviewState);
            RequireId(record.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(record.NeutralLabel);
            var assertionIds = ValidateAssertions(record.AssertionIds, assertions);
            var eventIds = ValidateEvents(record.EventIds, events);
            RequireEvidenceReference(assertionIds, eventIds);
            workplace._healthAbsenceRecords.Add(record.Id, new HealthAbsenceRecord(
                record.Id,
                evidence.Matter.Id,
                record.Kind,
                record.NeutralLabel,
                assertionIds,
                eventIds,
                record.EvidenceReviewState));
        }

        RequireUniqueIds(snapshot.AdjustmentRequests.Select(item => item.Id), "adjustment request");
        foreach (var request in snapshot.AdjustmentRequests)
        {
            RequireDefinedEnum(request.ResponseStatus);
            RequireId(request.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.NeutralLabel);
            var requests = ValidateAssertions(request.RequestAssertionIds, assertions, requireAny: true);
            var responses = ValidateAssertions(request.ResponseAssertionIds, assertions);
            var implementations = ValidateAssertions(request.ImplementationAssertionIds, assertions);
            if (request.ResponseStatus == AdjustmentResponseStatus.NotRecorded && responses.Count > 0 ||
                request.ResponseStatus != AdjustmentResponseStatus.NotRecorded && responses.Count == 0)
            {
                throw new InvalidOperationException("Persisted adjustment response state is inconsistent.");
            }

            EnsureDistinctEvidenceGroups(requests, responses, implementations);
            workplace._adjustmentRequests.Add(request.Id, new AdjustmentRequest(
                request.Id,
                evidence.Matter.Id,
                request.NeutralLabel,
                requests,
                request.ResponseStatus,
                responses,
                implementations));
        }

        RequireUniqueIds(snapshot.WorkplaceProcesses.Select(item => item.Id), "workplace process");
        foreach (var process in snapshot.WorkplaceProcesses)
        {
            RequireDefinedEnum(process.Kind);
            RequireDefinedEnum(process.Status);
            RequireId(process.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(process.StageLabel);
            var assertionIds = ValidateAssertions(process.AssertionIds, assertions);
            var eventIds = ValidateEvents(process.EventIds, events);
            RequireEvidenceReference(assertionIds, eventIds);
            workplace._workplaceProcesses.Add(process.Id, new WorkplaceProcess(
                process.Id,
                evidence.Matter.Id,
                process.Kind,
                process.StageLabel,
                process.Status,
                assertionIds,
                eventIds,
                process.SupersedesWorkplaceProcessId,
                process.SupersessionAuditEventId));
        }

        foreach (var process in workplace._workplaceProcesses.Values)
        {
            if (process.SupersedesWorkplaceProcessId.HasValue)
            {
                if (!workplace._workplaceProcesses.TryGetValue(process.SupersedesWorkplaceProcessId.Value, out var previous) ||
                    previous.Kind != process.Kind)
                {
                    throw new InvalidOperationException("Persisted workplace-process supersession is invalid.");
                }

                workplace.RequireMatchingSupersessionAudit(previous.EventIds, process.EventIds, process.SupersessionAuditEventId);
            }
            else if (process.SupersessionAuditEventId.HasValue)
            {
                throw new InvalidOperationException("Persisted process audit lacks a superseded process.");
            }
        }

        RequireUniqueIds(snapshot.AcasProcessStates.Select(item => item.Id), "Acas process state");
        foreach (var state in snapshot.AcasProcessStates)
        {
            RequireDefinedEnum(state.Stage);
            RequireId(state.Id);
            var assertionIds = ValidateAssertions(state.AssertionIds, assertions);
            var eventIds = ValidateEvents(state.EventIds, events);
            if (state.Stage != AcasStage.NotRecorded)
            {
                RequireEvidenceReference(assertionIds, eventIds);
            }

            workplace._acasProcessStates.Add(state.Id, new AcasProcessState(
                state.Id,
                evidence.Matter.Id,
                state.Stage,
                assertionIds,
                eventIds));
        }

        return workplace;
    }

    private static IReadOnlyList<Guid> ValidateAssertions(
        IReadOnlyList<Guid> ids,
        IReadOnlyDictionary<Guid, Assertion> assertions,
        bool requireAny = false,
        bool requireSourceBacking = false)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var distinct = ids.Distinct().ToArray();
        if (requireAny && distinct.Length == 0)
        {
            throw new InvalidOperationException("Persisted workplace record requires supporting assertions.");
        }

        foreach (var id in distinct)
        {
            RequireId(id);
            if (!assertions.TryGetValue(id, out var assertion) || requireSourceBacking && !assertion.IsSourceBacked)
            {
                throw new InvalidOperationException("Persisted workplace assertion reference is invalid.");
            }
        }

        return Array.AsReadOnly(distinct);
    }

    private static IReadOnlyList<Guid> ValidateEvents(
        IReadOnlyList<Guid> ids,
        IReadOnlyDictionary<Guid, MatterEvent> events)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var distinct = ids.Distinct().ToArray();
        foreach (var id in distinct)
        {
            RequireId(id);
            if (!events.ContainsKey(id))
            {
                throw new InvalidOperationException("Persisted workplace event reference is invalid.");
            }
        }

        return Array.AsReadOnly(distinct);
    }

    private static void RequireUniqueIds(IEnumerable<Guid> ids, string label)
    {
        var seen = new HashSet<Guid>();
        foreach (var id in ids)
        {
            RequireId(id);
            if (!seen.Add(id))
            {
                throw new InvalidOperationException($"Persisted {label} ids must be unique.");
            }
        }
    }

    private static void RequireSnapshotLists(WorkplaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot.EmploymentProfiles);
        ArgumentNullException.ThrowIfNull(snapshot.EmploymentTerms);
        ArgumentNullException.ThrowIfNull(snapshot.HealthAbsenceRecords);
        ArgumentNullException.ThrowIfNull(snapshot.AdjustmentRequests);
        ArgumentNullException.ThrowIfNull(snapshot.WorkplaceProcesses);
        ArgumentNullException.ThrowIfNull(snapshot.AcasProcessStates);
    }
}
