using System.Runtime.CompilerServices;
using CaseMesh.Core.Models;
using CaseMesh.Core.Services;

namespace CaseMesh.Core.Workplace;

public sealed class WorkplaceMatter
{
    private readonly MatterEvidenceGraph _evidence;
    private readonly Dictionary<Guid, EmploymentProfile> _employmentProfiles = [];
    private readonly Dictionary<Guid, EmploymentTerm> _employmentTerms = [];
    private readonly Dictionary<Guid, HealthAbsenceRecord> _healthAbsenceRecords = [];
    private readonly Dictionary<Guid, AdjustmentRequest> _adjustmentRequests = [];
    private readonly Dictionary<Guid, WorkplaceProcess> _workplaceProcesses = [];
    private readonly Dictionary<Guid, AcasProcessState> _acasProcessStates = [];

    public WorkplaceMatter(MatterEvidenceGraph evidence)
    {
        _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
    }

    public Guid MatterId => _evidence.Matter.Id;
    public MatterEvidenceGraph Evidence => _evidence;
    public IReadOnlyCollection<EmploymentProfile> EmploymentProfiles => _employmentProfiles.Values.ToArray();
    public IReadOnlyCollection<EmploymentTerm> EmploymentTerms => _employmentTerms.Values.ToArray();
    public IReadOnlyCollection<HealthAbsenceRecord> HealthAbsenceRecords => _healthAbsenceRecords.Values.ToArray();
    public IReadOnlyCollection<AdjustmentRequest> AdjustmentRequests => _adjustmentRequests.Values.ToArray();
    public IReadOnlyCollection<WorkplaceProcess> WorkplaceProcesses => _workplaceProcesses.Values.ToArray();
    public IReadOnlyCollection<AcasProcessState> AcasProcessStates => _acasProcessStates.Values.ToArray();

    public EmploymentProfile AddEmploymentProfile(
        Guid id,
        string employerReference,
        string roleTitle,
        DateOnly? employmentStartedOn = null,
        DateOnly? employmentEndedOn = null,
        IReadOnlyList<Guid>? supportingAssertionIds = null,
        VerificationState evidenceReviewState = VerificationState.NotReviewed)
    {
        RequireId(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(employerReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(roleTitle);
        ValidatePeriod(employmentStartedOn, employmentEndedOn, nameof(employmentEndedOn));
        EnsureAvailable(_employmentProfiles, id, "employment profile");
        var assertionIds = RequireAssertions(supportingAssertionIds ?? [], requireSourceBacking: false);

        var profile = new EmploymentProfile(
            id,
            MatterId,
            employerReference,
            roleTitle,
            employmentStartedOn,
            employmentEndedOn,
            assertionIds,
            evidenceReviewState);
        _employmentProfiles.Add(id, profile);
        return profile;
    }

    public EmploymentTerm AddEmploymentTerm(
        Guid id,
        EmploymentTermKind kind,
        string value,
        IReadOnlyList<Guid> supportingAssertionIds,
        DateOnly? effectiveFrom = null,
        DateOnly? effectiveTo = null,
        Guid? supersedesEmploymentTermId = null)
    {
        RequireId(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        ValidatePeriod(effectiveFrom, effectiveTo, nameof(effectiveTo));
        EnsureAvailable(_employmentTerms, id, "employment term");
        var assertionIds = RequireAssertions(supportingAssertionIds, requireSourceBacking: true, requireAny: true);

        if (supersedesEmploymentTermId.HasValue)
        {
            var previous = RequireRecord(_employmentTerms, supersedesEmploymentTermId.Value, "employment term");
            if (previous.Kind != kind)
            {
                throw new InvalidOperationException("An employment term can supersede only a term of the same kind.");
            }
        }

        var term = new EmploymentTerm(
            id,
            MatterId,
            kind,
            value,
            effectiveFrom,
            effectiveTo,
            assertionIds,
            supersedesEmploymentTermId);
        _employmentTerms.Add(id, term);
        return term;
    }

    public HealthAbsenceRecord AddHealthAbsenceRecord(
        Guid id,
        HealthAbsenceKind kind,
        string neutralLabel,
        IReadOnlyList<Guid>? assertionIds = null,
        IReadOnlyList<Guid>? eventIds = null,
        VerificationState evidenceReviewState = VerificationState.NotReviewed)
    {
        RequireId(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(neutralLabel);
        EnsureAvailable(_healthAbsenceRecords, id, "health/absence record");
        var assertions = RequireAssertions(assertionIds ?? [], requireSourceBacking: false);
        var events = RequireEvents(eventIds ?? []);
        RequireEvidenceReference(assertions, events);

        var record = new HealthAbsenceRecord(
            id,
            MatterId,
            kind,
            neutralLabel,
            assertions,
            events,
            evidenceReviewState);
        _healthAbsenceRecords.Add(id, record);
        return record;
    }

    public AdjustmentRequest AddAdjustmentRequest(
        Guid id,
        string neutralLabel,
        IReadOnlyList<Guid> requestAssertionIds,
        AdjustmentResponseStatus responseStatus = AdjustmentResponseStatus.NotRecorded,
        IReadOnlyList<Guid>? responseAssertionIds = null,
        IReadOnlyList<Guid>? implementationAssertionIds = null)
    {
        RequireId(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(neutralLabel);
        EnsureAvailable(_adjustmentRequests, id, "adjustment request");
        var requests = RequireAssertions(requestAssertionIds, requireSourceBacking: false, requireAny: true);
        var responses = RequireAssertions(responseAssertionIds ?? [], requireSourceBacking: false);
        var implementations = RequireAssertions(implementationAssertionIds ?? [], requireSourceBacking: false);

        if (responseStatus == AdjustmentResponseStatus.NotRecorded && responses.Count > 0)
        {
            throw new InvalidOperationException("A recorded employer response requires a descriptive response status.");
        }

        if (responseStatus != AdjustmentResponseStatus.NotRecorded && responses.Count == 0)
        {
            throw new InvalidOperationException("A response status requires at least one separate response assertion.");
        }

        EnsureDistinctEvidenceGroups(requests, responses, implementations);
        var request = new AdjustmentRequest(
            id,
            MatterId,
            neutralLabel,
            requests,
            responseStatus,
            responses,
            implementations);
        _adjustmentRequests.Add(id, request);
        return request;
    }

    public WorkplaceProcess AddWorkplaceProcess(
        Guid id,
        WorkplaceProcessKind kind,
        string stageLabel,
        WorkplaceProcessStatus status,
        IReadOnlyList<Guid>? assertionIds = null,
        IReadOnlyList<Guid>? eventIds = null,
        Guid? supersedesWorkplaceProcessId = null)
    {
        RequireId(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(stageLabel);
        EnsureAvailable(_workplaceProcesses, id, "workplace process");
        var assertions = RequireAssertions(assertionIds ?? [], requireSourceBacking: false);
        var events = RequireEvents(eventIds ?? []);
        RequireEvidenceReference(assertions, events);

        if (supersedesWorkplaceProcessId.HasValue)
        {
            var previous = RequireRecord(_workplaceProcesses, supersedesWorkplaceProcessId.Value, "workplace process");
            if (previous.Kind != kind)
            {
                throw new InvalidOperationException("A workplace process can supersede only a process of the same kind.");
            }
        }

        var process = new WorkplaceProcess(
            id,
            MatterId,
            kind,
            stageLabel,
            status,
            assertions,
            events,
            supersedesWorkplaceProcessId);
        _workplaceProcesses.Add(id, process);
        return process;
    }

    public AcasProcessState AddAcasProcessState(
        Guid id,
        AcasStage stage,
        IReadOnlyList<Guid>? assertionIds = null,
        IReadOnlyList<Guid>? eventIds = null)
    {
        RequireId(id);
        EnsureAvailable(_acasProcessStates, id, "Acas process state");
        var assertions = RequireAssertions(assertionIds ?? [], requireSourceBacking: false);
        var events = RequireEvents(eventIds ?? []);
        if (stage != AcasStage.NotRecorded)
        {
            RequireEvidenceReference(assertions, events);
        }

        var state = new AcasProcessState(id, MatterId, stage, assertions, events);
        _acasProcessStates.Add(id, state);
        return state;
    }

    private IReadOnlyList<Guid> RequireAssertions(
        IReadOnlyList<Guid> assertionIds,
        bool requireSourceBacking,
        bool requireAny = false)
    {
        ArgumentNullException.ThrowIfNull(assertionIds);
        var ids = assertionIds.Distinct().ToArray();
        if (requireAny && ids.Length == 0)
        {
            throw new InvalidOperationException("At least one supporting assertion is required.");
        }

        var assertionsById = _evidence.Assertions.ToDictionary(assertion => assertion.Id);
        foreach (var id in ids)
        {
            RequireId(id);
            if (!assertionsById.TryGetValue(id, out var assertion) || assertion.MatterId != MatterId)
            {
                throw new InvalidOperationException("A workplace record cannot reference an assertion from another Matter.");
            }

            if (requireSourceBacking && !assertion.IsSourceBacked)
            {
                throw new InvalidOperationException("This workplace record requires source-backed assertions.");
            }
        }

        return Array.AsReadOnly(ids);
    }

    private IReadOnlyList<Guid> RequireEvents(IReadOnlyList<Guid> eventIds)
    {
        ArgumentNullException.ThrowIfNull(eventIds);
        var ids = eventIds.Distinct().ToArray();
        var eventsById = _evidence.Events.ToDictionary(matterEvent => matterEvent.Id);
        foreach (var id in ids)
        {
            RequireId(id);
            if (!eventsById.TryGetValue(id, out var matterEvent) || matterEvent.MatterId != MatterId)
            {
                throw new InvalidOperationException("A workplace record cannot reference an event from another Matter.");
            }
        }

        return Array.AsReadOnly(ids);
    }

    private static void RequireEvidenceReference(IReadOnlyCollection<Guid> assertionIds, IReadOnlyCollection<Guid> eventIds)
    {
        if (assertionIds.Count == 0 && eventIds.Count == 0)
        {
            throw new InvalidOperationException("At least one assertion or event reference is required.");
        }
    }

    private static void EnsureDistinctEvidenceGroups(params IReadOnlyCollection<Guid>[] groups)
    {
        var allIds = groups.SelectMany(group => group).ToArray();
        if (allIds.Length != allIds.Distinct().Count())
        {
            throw new InvalidOperationException("Request, response and implementation evidence must remain separate.");
        }
    }

    private static T RequireRecord<T>(
        IReadOnlyDictionary<Guid, T> records,
        Guid id,
        string label,
        [CallerArgumentExpression(nameof(id))] string? parameterName = null)
    {
        RequireId(id, parameterName);
        if (!records.TryGetValue(id, out var record))
        {
            throw new InvalidOperationException($"The {label} is not registered to this workplace Matter.");
        }

        return record;
    }

    private static void EnsureAvailable<T>(IReadOnlyDictionary<Guid, T> records, Guid id, string label)
    {
        if (records.ContainsKey(id))
        {
            throw new InvalidOperationException($"The {label} id already exists.");
        }
    }

    private static void RequireId(Guid id, [CallerArgumentExpression(nameof(id))] string? parameterName = null)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A non-empty id is required.", parameterName);
        }
    }

    private static void ValidatePeriod(DateOnly? start, DateOnly? end, string endParameterName)
    {
        if (end < start)
        {
            throw new ArgumentOutOfRangeException(endParameterName, "The end date cannot precede the start date.");
        }
    }
}
