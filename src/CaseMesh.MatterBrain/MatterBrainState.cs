using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CaseMesh.Core.Models;
using CaseMesh.Core.Services;

namespace CaseMesh.MatterBrain;

public sealed class MatterBrainState
{
    private readonly Dictionary<Guid, Person> _people = [];
    private readonly Dictionary<Guid, Organisation> _organisations = [];
    private readonly Dictionary<Guid, EntityAlias> _aliases = [];
    private readonly Dictionary<Guid, Communication> _communications = [];
    private readonly Dictionary<Guid, ExtractionRun> _runs = [];
    private readonly Dictionary<string, Guid> _runIdsByFingerprint = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, ExtractionCandidateRecord> _candidates = [];
    private readonly Dictionary<Guid, MatterBrainDependency> _dependencies = [];
    private readonly Dictionary<Guid, DependencyInvalidation> _invalidations = [];
    private readonly List<EntityResolutionAction> _entityResolutionActions = [];

    public MatterBrainState(MatterEvidenceGraph evidence)
    {
        Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
    }

    public MatterEvidenceGraph Evidence { get; }
    public Guid MatterId => Evidence.Matter.Id;
    public IReadOnlyCollection<Person> People => _people.Values.ToArray();
    public IReadOnlyCollection<Organisation> Organisations => _organisations.Values.ToArray();
    public IReadOnlyCollection<EntityAlias> Aliases => _aliases.Values.ToArray();
    public IReadOnlyCollection<Communication> Communications => _communications.Values.ToArray();
    public IReadOnlyCollection<ExtractionRun> Runs => _runs.Values.ToArray();
    public IReadOnlyCollection<ExtractionCandidateRecord> Candidates => _candidates.Values.ToArray();
    public IReadOnlyCollection<MatterBrainDependency> Dependencies => _dependencies.Values.ToArray();
    public IReadOnlyCollection<DependencyInvalidation> DependencyInvalidations => _invalidations.Values.ToArray();
    public IReadOnlyList<EntityResolutionAction> EntityResolutionActions => _entityResolutionActions.ToArray();

    public IReadOnlyCollection<MatterBrainDependency> ActiveDependencies => _dependencies.Values
        .Where(dependency => _invalidations.Values.All(item => item.DependencyId != dependency.Id))
        .ToArray();

    internal ExtractionRun? FindRun(string fingerprint) =>
        _runIdsByFingerprint.TryGetValue(fingerprint, out var id) ? _runs[id] : null;

    internal void AddRun(ExtractionRun run)
    {
        RequireMatter(run.MatterId);
        RequireId(run.Id, nameof(run.Id));
        ArgumentException.ThrowIfNullOrWhiteSpace(run.Fingerprint);
        MatterBrainIntegrity.ValidateDescriptor(run.Provider);
        ValidateDigest(run.RawResultDigest, nameof(run.RawResultDigest));
        ValidateSources(run.SourceSpanIds);
        if (run.SourceSpanIds.Count == 0 || run.SourceSpanIds.Distinct().Count() != run.SourceSpanIds.Count)
        {
            throw new InvalidOperationException("An extraction run requires a non-empty set of distinct source spans.");
        }
        if (run.Fingerprint != MatterBrainIntegrity.Fingerprint(run.Provider, run.SourceSpanIds) ||
            run.Id != DeterministicId("extraction-run", MatterId, run.Fingerprint))
        {
            throw new InvalidOperationException("Persisted extraction-run fingerprint or identity is invalid.");
        }
        if (_runs.TryGetValue(run.Id, out var existing))
        {
            if (!SameRun(existing, run))
            {
                throw new InvalidOperationException("An extraction-run id cannot be reused with different metadata.");
            }

            return;
        }

        if (_runIdsByFingerprint.ContainsKey(run.Fingerprint))
        {
            throw new InvalidOperationException("An extraction fingerprint may identify only one run.");
        }

        _runs.Add(run.Id, run);
        _runIdsByFingerprint.Add(run.Fingerprint, run.Id);
    }

    internal void AddCandidate(ExtractionCandidateRecord candidate)
    {
        RequireMatter(candidate.MatterId);
        RequireId(candidate.Id, nameof(candidate.Id));
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.ExternalKey);
        RequireDefined(candidate.Kind);
        RequireDefined(candidate.Disposition);
        ValidateScore(candidate.ExtractionConfidence);
        ValidateDigest(candidate.PayloadDigest, nameof(candidate.PayloadDigest));
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.PayloadJson);
        if (Encoding.UTF8.GetByteCount(candidate.PayloadJson) > MatterBrainIntegrity.MaximumCandidatePayloadBytes)
        {
            throw new InvalidOperationException("Candidate payload metadata exceeds its bounded size.");
        }

        string canonicalPayload;
        try
        {
            canonicalPayload = MatterBrainIntegrity.CanonicalizeJson(candidate.PayloadJson);
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new InvalidOperationException("Candidate payload metadata is not valid bounded JSON.", exception);
        }

        if (candidate.PayloadDigest != MatterBrainIntegrity.Digest($"{candidate.Kind}|{canonicalPayload}"))
        {
            throw new InvalidOperationException("Candidate payload metadata does not match its digest.");
        }

        if ((candidate.Disposition == CandidateDisposition.Rejected) != (candidate.RejectionCode is not null) ||
            (candidate.RejectionCode is not null && string.IsNullOrWhiteSpace(candidate.RejectionCode)) ||
            (candidate.CanonicalKind.HasValue != candidate.CanonicalId.HasValue) ||
            (candidate.Disposition == CandidateDisposition.Rejected && candidate.CanonicalId.HasValue) ||
            (candidate.Disposition == CandidateDisposition.Validated &&
             candidate.Kind != ExtractionCandidateKind.EntityMatch && !candidate.CanonicalId.HasValue) ||
            (candidate.Kind == ExtractionCandidateKind.EntityMatch && candidate.CanonicalId.HasValue) ||
            !HasValidCandidateCanonicalKind(candidate.Kind, candidate.CanonicalKind))
        {
            throw new InvalidOperationException("Candidate disposition and canonical linkage are inconsistent.");
        }

        if (candidate.Id != DeterministicId(
                "extraction-candidate", candidate.RunId, candidate.Kind, candidate.ExternalKey))
        {
            throw new InvalidOperationException("Persisted candidate identity is inconsistent with its run and key.");
        }

        if (candidate.CanonicalKind.HasValue)
        {
            RequireDefined(candidate.CanonicalKind.Value);
            RequireCanonical(candidate.CanonicalKind.Value, candidate.CanonicalId!.Value);
        }

        ValidateSources(candidate.SourceSpanIds);
        if (!_runs.ContainsKey(candidate.RunId))
        {
            throw new InvalidOperationException("A candidate requires a registered extraction run.");
        }

        if (candidate.Disposition == CandidateDisposition.Validated && candidate.SourceSpanIds.Count == 0)
        {
            var isAiInference = candidate.CanonicalKind == CanonicalRecordKind.Assertion &&
                                Evidence.Assertions.Single(item => item.Id == candidate.CanonicalId).AssertionClass ==
                                AssertionClass.AiInference;
            if (!isAiInference)
            {
                throw new InvalidOperationException("Validated documentary candidates require source provenance.");
            }
        }

        if (_candidates.TryGetValue(candidate.Id, out var existing))
        {
            if (!SameCandidate(existing, candidate))
            {
                throw new InvalidOperationException("A candidate id cannot overwrite prior model output.");
            }

            return;
        }

        _candidates.Add(candidate.Id, candidate);
    }

    private static bool HasValidCandidateCanonicalKind(
        ExtractionCandidateKind candidateKind,
        CanonicalRecordKind? canonicalKind) => candidateKind switch
        {
            ExtractionCandidateKind.Person => canonicalKind is null or CanonicalRecordKind.Person,
            ExtractionCandidateKind.Organisation => canonicalKind is null or CanonicalRecordKind.Organisation,
            ExtractionCandidateKind.Communication => canonicalKind is null or CanonicalRecordKind.Communication,
            ExtractionCandidateKind.Assertion => canonicalKind is null or CanonicalRecordKind.Assertion,
            ExtractionCandidateKind.Event => canonicalKind is null or CanonicalRecordKind.Event,
            ExtractionCandidateKind.AssertionEventLink => canonicalKind is null or CanonicalRecordKind.AssertionEventLink,
            ExtractionCandidateKind.EntityMatch => canonicalKind is null,
            ExtractionCandidateKind.Contradiction => canonicalKind is null or CanonicalRecordKind.Contradiction,
            _ => false
        };

    internal Person AddPerson(Guid id, string displayName, IReadOnlyList<string> roleLabels)
    {
        RequireId(id, nameof(id));
        if (_organisations.ContainsKey(id))
        {
            throw new InvalidOperationException("A canonical id cannot identify both a person and an organisation.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(roleLabels);
        var normalizedRoles = roleLabels.Select(RequireText).Distinct(StringComparer.Ordinal).ToArray();
        if (_people.TryGetValue(id, out var existing))
        {
            if (existing.DisplayName != displayName || !existing.RoleLabels.SequenceEqual(normalizedRoles))
            {
                throw new InvalidOperationException("A person id cannot overwrite canonical identity history.");
            }

            return existing;
        }

        var person = new Person(id, MatterId, displayName, Array.AsReadOnly(normalizedRoles));
        _people.Add(id, person);
        return person;
    }

    internal Organisation AddOrganisation(Guid id, string name, string typeLabel)
    {
        RequireId(id, nameof(id));
        if (_people.ContainsKey(id))
        {
            throw new InvalidOperationException("A canonical id cannot identify both a person and an organisation.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(typeLabel);
        if (_organisations.TryGetValue(id, out var existing))
        {
            if (existing.Name != name || existing.TypeLabel != typeLabel)
            {
                throw new InvalidOperationException("An organisation id cannot overwrite canonical identity history.");
            }

            return existing;
        }

        var organisation = new Organisation(id, MatterId, name, typeLabel);
        _organisations.Add(id, organisation);
        return organisation;
    }

    internal EntityAlias AddAlias(
        Guid id,
        CanonicalEntityKind kind,
        Guid entityId,
        string value,
        Guid? sourceSpanId)
    {
        RequireId(id, nameof(id));
        RequireEntity(kind, entityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (sourceSpanId.HasValue && Evidence.SourceSpans.All(item => item.Id != sourceSpanId.Value))
        {
            throw new InvalidOperationException("An entity alias source must belong to the same Matter.");
        }

        var normalized = NormalizeAlias(value);
        if (_aliases.TryGetValue(id, out var existing))
        {
            if (existing.EntityKind != kind || existing.EntityId != entityId ||
                existing.Value != value || existing.NormalizedValue != normalized ||
                existing.SourceSpanId != sourceSpanId)
            {
                throw new InvalidOperationException("An alias id cannot overwrite identity provenance.");
            }

            return existing;
        }

        var alias = new EntityAlias(id, MatterId, kind, entityId, value, normalized, sourceSpanId);
        _aliases.Add(id, alias);
        return alias;
    }

    internal bool HasAlias(CanonicalEntityKind kind, Guid entityId, string value)
    {
        var normalized = NormalizeAlias(value);
        return _aliases.Values.Any(item =>
            item.EntityKind == kind && item.EntityId == entityId && item.NormalizedValue == normalized);
    }

    internal Communication AddCommunication(
        Guid id,
        CommunicationKind kind,
        string neutralLabel,
        DateTimeOffset? occurredAt,
        Guid? senderEntityId,
        IReadOnlyList<Guid> participantEntityIds,
        IReadOnlyList<Guid> sourceSpanIds)
    {
        RequireId(id, nameof(id));
        RequireDefined(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(neutralLabel);
        ArgumentNullException.ThrowIfNull(participantEntityIds);
        ArgumentNullException.ThrowIfNull(sourceSpanIds);
        if (senderEntityId.HasValue)
        {
            RequireAnyEntity(senderEntityId.Value);
        }

        var participants = participantEntityIds.Distinct().ToArray();
        foreach (var participant in participants)
        {
            RequireAnyEntity(participant);
        }

        var sources = sourceSpanIds.Distinct().ToArray();
        foreach (var source in sources)
        {
            if (Evidence.SourceSpans.All(item => item.Id != source))
            {
                throw new InvalidOperationException("A communication source must belong to the same Matter.");
            }
        }

        if (_communications.TryGetValue(id, out var existing))
        {
            if (existing.Kind != kind || existing.NeutralLabel != neutralLabel || existing.OccurredAt != occurredAt ||
                existing.SenderEntityId != senderEntityId || !existing.ParticipantEntityIds.SequenceEqual(participants) ||
                !existing.SourceSpanIds.SequenceEqual(sources))
            {
                throw new InvalidOperationException("A communication id cannot overwrite canonical history.");
            }

            return existing;
        }

        var communication = new Communication(
            id,
            MatterId,
            kind,
            neutralLabel,
            occurredAt,
            senderEntityId,
            Array.AsReadOnly(participants),
            Array.AsReadOnly(sources),
            VerificationState.NotReviewed);
        _communications.Add(id, communication);
        return communication;
    }

    internal Guid? FindExactEntity(CanonicalEntityKind kind, IEnumerable<string> names)
    {
        var normalized = names.Select(NormalizeAlias).ToHashSet(StringComparer.Ordinal);
        var matches = _aliases.Values
            .Where(item => item.EntityKind == kind && normalized.Contains(item.NormalizedValue))
            .Select(item => item.EntityId)
            .Distinct()
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal void AddDependency(MatterBrainDependency dependency)
    {
        RequireMatter(dependency.MatterId);
        RequireId(dependency.Id, nameof(dependency.Id));
        RequireDefined(dependency.CanonicalKind);
        RequireCanonical(dependency.CanonicalKind, dependency.CanonicalId);
        if (!_runs.ContainsKey(dependency.RunId) ||
            !_candidates.TryGetValue(dependency.CandidateId, out var candidate) ||
            candidate.RunId != dependency.RunId ||
            !candidate.SourceSpanIds.Contains(dependency.SourceSpanId) ||
            Evidence.SourceSpans.All(item => item.Id != dependency.SourceSpanId))
        {
            throw new InvalidOperationException("A dependency requires same-Matter run, candidate, and source records.");
        }

        if (_dependencies.TryGetValue(dependency.Id, out var existing) && existing != dependency)
        {
            throw new InvalidOperationException("A dependency id cannot be rewritten.");
        }

        _dependencies.TryAdd(dependency.Id, dependency);
    }

    public MatterBrainDependency RegisterAnalysisDependency(
        Guid dependencyId,
        Guid runId,
        Guid sourceSpanId,
        Guid candidateId,
        Guid analysisNodeId)
    {
        var dependency = new MatterBrainDependency(
            dependencyId,
            MatterId,
            runId,
            sourceSpanId,
            candidateId,
            CanonicalRecordKind.AnalysisNode,
            analysisNodeId);
        AddDependency(dependency);
        return dependency;
    }

    internal void InvalidateDependencies(
        IReadOnlyCollection<Guid> sourceSpanIds,
        Guid runId,
        DateTimeOffset invalidatedAt)
    {
        foreach (var dependency in ActiveDependencies.Where(item => sourceSpanIds.Contains(item.SourceSpanId)))
        {
            var id = DeterministicId("dependency-invalidation", dependency.Id, runId);
            _invalidations.TryAdd(id, new DependencyInvalidation(
                id, MatterId, dependency.Id, runId, null, invalidatedAt));
        }
    }

    public MatterBrainSnapshot CaptureSnapshot() => new(
        MatterId,
        People.OrderBy(item => item.Id).ToArray(),
        Organisations.OrderBy(item => item.Id).ToArray(),
        Aliases.OrderBy(item => item.Id).ToArray(),
        Communications.OrderBy(item => item.Id).ToArray(),
        Runs.OrderBy(item => item.GeneratedAt).ThenBy(item => item.Id).ToArray(),
        Candidates.OrderBy(item => item.RunId).ThenBy(item => item.Id).ToArray(),
        Dependencies.OrderBy(item => item.Id).ToArray(),
        DependencyInvalidations.OrderBy(item => item.InvalidatedAt).ThenBy(item => item.Id).ToArray(),
        EntityResolutionActions.OrderBy(item => item.OccurredAt).ThenBy(item => item.Id).ToArray());

    public static MatterBrainState Rehydrate(MatterEvidenceGraph evidence, MatterBrainSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.MatterId != evidence.Matter.Id)
        {
            throw new InvalidOperationException("Persisted Matter Brain state belongs to another Matter.");
        }

        RequireSnapshotLists(snapshot);
        var state = new MatterBrainState(evidence);
        RequireUnique(snapshot.People.Select(item => item.Id), "person");
        foreach (var person in snapshot.People)
        {
            if (person.MatterId != snapshot.MatterId)
            {
                throw new InvalidOperationException("A persisted person crossed a Matter boundary.");
            }

            state.AddPerson(person.Id, person.DisplayName, person.RoleLabels);
        }

        RequireUnique(snapshot.Organisations.Select(item => item.Id), "organisation");
        foreach (var organisation in snapshot.Organisations)
        {
            if (organisation.MatterId != snapshot.MatterId)
            {
                throw new InvalidOperationException("A persisted organisation crossed a Matter boundary.");
            }

            state.AddOrganisation(organisation.Id, organisation.Name, organisation.TypeLabel);
        }

        RequireUnique(snapshot.Aliases.Select(item => item.Id), "alias");
        if (snapshot.Aliases.GroupBy(item => new
            {
                item.EntityKind,
                item.EntityId,
                item.NormalizedValue
            }).Any(group => group.Count() > 1))
        {
            throw new InvalidOperationException("Persisted aliases contain duplicate normalized entity values.");
        }
        foreach (var alias in snapshot.Aliases)
        {
            if (alias.MatterId != snapshot.MatterId || alias.NormalizedValue != NormalizeAlias(alias.Value))
            {
                throw new InvalidOperationException("Persisted alias provenance is invalid.");
            }

            state.AddAlias(alias.Id, alias.EntityKind, alias.EntityId, alias.Value, alias.SourceSpanId);
        }

        RequireUnique(snapshot.Communications.Select(item => item.Id), "communication");
        foreach (var communication in snapshot.Communications)
        {
            if (communication.MatterId != snapshot.MatterId ||
                communication.VerificationState != VerificationState.NotReviewed)
            {
                throw new InvalidOperationException("Persisted communication state is invalid.");
            }

            state.AddCommunication(
                communication.Id, communication.Kind, communication.NeutralLabel, communication.OccurredAt,
                communication.SenderEntityId, communication.ParticipantEntityIds, communication.SourceSpanIds);
        }

        RequireUnique(snapshot.Runs.Select(item => item.Id), "extraction run");
        foreach (var run in snapshot.Runs)
        {
            state.AddRun(run);
        }

        RequireUnique(snapshot.Candidates.Select(item => item.Id), "candidate");
        foreach (var candidate in snapshot.Candidates)
        {
            state.AddCandidate(candidate);
        }

        RequireUnique(snapshot.Dependencies.Select(item => item.Id), "dependency");
        foreach (var dependency in snapshot.Dependencies)
        {
            state.AddDependency(dependency);
        }

        RequireUnique(snapshot.DependencyInvalidations.Select(item => item.Id), "dependency invalidation");
        foreach (var invalidation in snapshot.DependencyInvalidations)
        {
            state.AddInvalidation(invalidation);
        }

        RequireUnique(snapshot.EntityResolutionActions.Select(item => item.Id), "entity-resolution action");
        foreach (var action in snapshot.EntityResolutionActions
                     .OrderBy(item => item.Kind == EntityResolutionActionKind.Proposed ? 0 :
                         item.Kind is EntityResolutionActionKind.Accepted or EntityResolutionActionKind.Rejected ? 1 : 2)
                     .ThenBy(item => item.OccurredAt)
                     .ThenBy(item => item.Id))
        {
            state.RehydrateResolutionAction(action);
        }

        return state;
    }

    private void AddInvalidation(DependencyInvalidation invalidation)
    {
        RequireMatter(invalidation.MatterId);
        RequireId(invalidation.Id, nameof(invalidation.Id));
        if (!_dependencies.ContainsKey(invalidation.DependencyId) ||
            (invalidation.InvalidatedByRunId.HasValue == invalidation.InvalidatedByAuditEventId.HasValue) ||
            (invalidation.InvalidatedByRunId.HasValue && !_runs.ContainsKey(invalidation.InvalidatedByRunId.Value)) ||
            (invalidation.InvalidatedByAuditEventId.HasValue &&
             Evidence.AuditEvents.All(item => item.Id != invalidation.InvalidatedByAuditEventId.Value)) ||
            _invalidations.Values.Any(item => item.DependencyId == invalidation.DependencyId))
        {
            throw new InvalidOperationException("Persisted dependency invalidation is inconsistent.");
        }

        _invalidations.Add(invalidation.Id, invalidation);
    }

    private void RehydrateResolutionAction(EntityResolutionAction action)
    {
        RequireMatter(action.MatterId);
        RequireDefined(action.Kind);
        RequireEntity(action.EntityKind, action.SourceEntityId);
        RequireEntity(action.EntityKind, action.TargetEntityId);
        ValidateSources(action.EvidenceSourceSpanIds);
        ValidateScore(action.MatchScore);
        ArgumentException.ThrowIfNullOrWhiteSpace(action.Actor);
        if (action.Kind == EntityResolutionActionKind.Proposed)
        {
            if (action.ProposalId != action.Id || action.ReversesActionId.HasValue)
            {
                throw new InvalidOperationException("Persisted entity proposal is invalid.");
            }
        }
        else if (action.Kind is EntityResolutionActionKind.Accepted or EntityResolutionActionKind.Rejected)
        {
            var proposal = _entityResolutionActions.SingleOrDefault(item =>
                item.Id == action.ProposalId && item.Kind == EntityResolutionActionKind.Proposed);
            if (proposal is null || action.OccurredAt < proposal.OccurredAt || action.ReversesActionId.HasValue ||
                !HasSameResolutionSubject(action, proposal) ||
                _entityResolutionActions.Any(item => item.ProposalId == action.ProposalId &&
                    item.Kind is EntityResolutionActionKind.Accepted or EntityResolutionActionKind.Rejected))
            {
                throw new InvalidOperationException("Persisted entity decision has no valid proposal.");
            }

            if (action.Kind == EntityResolutionActionKind.Accepted && WouldCreateEntityMergeCycle(action))
            {
                throw new InvalidOperationException("Persisted entity decisions contain a merge cycle.");
            }
        }
        else
        {
            var accepted = action.ReversesActionId.HasValue
                ? _entityResolutionActions.SingleOrDefault(item =>
                    item.Id == action.ReversesActionId.Value && item.Kind == EntityResolutionActionKind.Accepted)
                : null;
            if (accepted is null || action.ProposalId != accepted.ProposalId ||
                action.OccurredAt < accepted.OccurredAt || !HasSameResolutionSubject(action, accepted) ||
                _entityResolutionActions.Any(item =>
                    item.Kind == EntityResolutionActionKind.Reversed && item.ReversesActionId == action.ReversesActionId))
            {
                throw new InvalidOperationException("Persisted entity reversal has no valid accepted action.");
            }
        }

        AddResolutionAction(action);
    }

    private static bool HasSameResolutionSubject(EntityResolutionAction candidate, EntityResolutionAction original) =>
        candidate.EntityKind == original.EntityKind &&
        candidate.SourceEntityId == original.SourceEntityId &&
        candidate.TargetEntityId == original.TargetEntityId &&
        candidate.MatchScore == original.MatchScore &&
        candidate.EvidenceSourceSpanIds.SequenceEqual(original.EvidenceSourceSpanIds);

    public EntityResolutionAction ProposeEntityMerge(
        Guid actionId,
        CanonicalEntityKind kind,
        Guid sourceEntityId,
        Guid targetEntityId,
        IReadOnlyList<Guid> evidenceSourceSpanIds,
        decimal? matchScore,
        string actor,
        DateTimeOffset proposedAt)
    {
        RequireId(actionId, nameof(actionId));
        RequireEntity(kind, sourceEntityId);
        RequireEntity(kind, targetEntityId);
        if (sourceEntityId == targetEntityId)
        {
            throw new ArgumentException("An entity merge requires two distinct identities.");
        }

        ValidateScore(matchScore);
        ValidateSources(evidenceSourceSpanIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var action = new EntityResolutionAction(
            actionId, MatterId, actionId, EntityResolutionActionKind.Proposed, kind,
            sourceEntityId, targetEntityId, Array.AsReadOnly(evidenceSourceSpanIds.Distinct().ToArray()),
            matchScore, actor, proposedAt, null);
        AddResolutionAction(action);
        return action;
    }

    public EntityResolutionAction AcceptEntityMerge(
        Guid actionId,
        Guid proposalId,
        string actor,
        DateTimeOffset acceptedAt) =>
        DecideEntityMerge(actionId, proposalId, EntityResolutionActionKind.Accepted, actor, acceptedAt);

    public EntityResolutionAction RejectEntityMerge(
        Guid actionId,
        Guid proposalId,
        string actor,
        DateTimeOffset rejectedAt) =>
        DecideEntityMerge(actionId, proposalId, EntityResolutionActionKind.Rejected, actor, rejectedAt);

    public EntityResolutionAction ReverseEntityMerge(
        Guid actionId,
        Guid acceptedActionId,
        string actor,
        DateTimeOffset reversedAt)
    {
        RequireId(actionId, nameof(actionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var accepted = _entityResolutionActions.SingleOrDefault(item => item.Id == acceptedActionId)
            ?? throw new InvalidOperationException("The accepted entity-merge action is not registered.");
        if (accepted.Kind != EntityResolutionActionKind.Accepted ||
            _entityResolutionActions.Any(item => item.Kind == EntityResolutionActionKind.Reversed && item.ReversesActionId == acceptedActionId))
        {
            throw new InvalidOperationException("Only an active accepted merge can be reversed.");
        }

        if (reversedAt < accepted.OccurredAt)
        {
            throw new ArgumentOutOfRangeException(nameof(reversedAt), "A reversal cannot precede its accepted merge.");
        }

        var action = accepted with
        {
            Id = actionId,
            Kind = EntityResolutionActionKind.Reversed,
            Actor = actor,
            OccurredAt = reversedAt,
            ReversesActionId = acceptedActionId
        };
        AddResolutionAction(action);
        return action;
    }

    public Guid ResolveEntityId(CanonicalEntityKind kind, Guid entityId)
    {
        RequireEntity(kind, entityId);
        var current = entityId;
        var visited = new HashSet<Guid> { current };
        while (ActiveAcceptedMerge(kind, current) is { } accepted)
        {
            if (!visited.Add(accepted.TargetEntityId))
            {
                throw new InvalidOperationException("Entity-resolution history contains a merge cycle.");
            }

            current = accepted.TargetEntityId;
        }

        return current;
    }

    public AssertionReviewResult ReviewAssertion(
        Guid assertionId,
        VerificationState verificationState,
        Guid auditEventId,
        string actor,
        DateTimeOffset reviewedAt)
    {
        var result = Evidence.ReviewAssertion(
            assertionId, verificationState, auditEventId, actor, reviewedAt);
        if (verificationState == VerificationState.Rejected)
        {
            InvalidateCanonicalDependencies(CanonicalRecordKind.Assertion, assertionId, auditEventId, reviewedAt);
        }

        return result;
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
        var linkedEvents = Evidence.AssertionEventLinks
            .Where(item => item.AssertionId == assertionId)
            .ToArray();
        var result = Evidence.CorrectAssertion(
            assertionId, correctedAssertionId, correctedValue, correctedEventTime,
            auditEventId, actor, correctedAt);
        InvalidateCanonicalDependencies(CanonicalRecordKind.Assertion, assertionId, auditEventId, correctedAt);

        foreach (var link in linkedEvents)
        {
            Evidence.AddAssertionEventLink(
                DeterministicId("corrected-assertion-event-link", link.Id, correctedAssertionId),
                correctedAssertionId,
                link.EventId,
                link.Relation);
        }

        AddNumericCorrectionContradictions(result.CorrectedAssertion, correctedAt);

        return result;
    }

    private void InvalidateCanonicalDependencies(
        CanonicalRecordKind kind,
        Guid canonicalId,
        Guid auditEventId,
        DateTimeOffset invalidatedAt)
    {
        var direct = ActiveDependencies
            .Where(item => item.CanonicalKind == kind && item.CanonicalId == canonicalId)
            .ToArray();
        var sourceIds = direct.Select(item => item.SourceSpanId).ToHashSet();
        var affected = direct.Concat(ActiveDependencies.Where(item =>
                item.CanonicalKind == CanonicalRecordKind.AnalysisNode && sourceIds.Contains(item.SourceSpanId)))
            .DistinctBy(item => item.Id)
            .ToArray();
        foreach (var dependency in affected)
        {
            AddInvalidation(new DependencyInvalidation(
                DeterministicId("dependency-correction-invalidation", dependency.Id, auditEventId),
                MatterId,
                dependency.Id,
                null,
                auditEventId,
                invalidatedAt));
        }
    }

    private EntityResolutionAction DecideEntityMerge(
        Guid actionId,
        Guid proposalId,
        EntityResolutionActionKind decision,
        string actor,
        DateTimeOffset occurredAt)
    {
        RequireId(actionId, nameof(actionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var proposal = _entityResolutionActions.SingleOrDefault(item => item.Id == proposalId)
            ?? throw new InvalidOperationException("The entity-merge proposal is not registered.");
        if (proposal.Kind != EntityResolutionActionKind.Proposed ||
            _entityResolutionActions.Any(item => item.ProposalId == proposalId && item.Kind is EntityResolutionActionKind.Accepted or EntityResolutionActionKind.Rejected))
        {
            throw new InvalidOperationException("An entity-merge proposal can be decided only once.");
        }
        if (occurredAt < proposal.OccurredAt)
        {
            throw new ArgumentOutOfRangeException(nameof(occurredAt), "An entity decision cannot precede its proposal.");
        }

        var action = proposal with { Id = actionId, Kind = decision, Actor = actor, OccurredAt = occurredAt };
        if (decision == EntityResolutionActionKind.Accepted && WouldCreateEntityMergeCycle(action))
        {
            throw new InvalidOperationException("Accepting this entity merge would create a cycle.");
        }

        AddResolutionAction(action);
        return action;
    }

    private EntityResolutionAction? ActiveAcceptedMerge(CanonicalEntityKind kind, Guid sourceEntityId) =>
        _entityResolutionActions
            .Where(item => item.Kind == EntityResolutionActionKind.Accepted &&
                           item.EntityKind == kind && item.SourceEntityId == sourceEntityId)
            .Where(item => _entityResolutionActions.All(reversal =>
                reversal.Kind != EntityResolutionActionKind.Reversed || reversal.ReversesActionId != item.Id))
            .OrderBy(item => item.OccurredAt)
            .ThenBy(item => item.Id)
            .LastOrDefault();

    private bool WouldCreateEntityMergeCycle(EntityResolutionAction action)
    {
        var current = action.TargetEntityId;
        var visited = new HashSet<Guid> { action.SourceEntityId };
        while (true)
        {
            if (!visited.Add(current))
            {
                return true;
            }

            var accepted = ActiveAcceptedMerge(action.EntityKind, current);
            if (accepted is null)
            {
                return false;
            }

            current = accepted.TargetEntityId;
        }
    }

    private void AddNumericCorrectionContradictions(Assertion corrected, DateTimeOffset detectedAt)
    {
        if (!decimal.TryParse(corrected.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var correctedNumber))
        {
            return;
        }

        foreach (var other in Evidence.Assertions.Where(item =>
                     item.Id != corrected.Id && item.VerificationState != VerificationState.Rejected &&
                     item.DisputeState != DisputeState.Superseded &&
                     string.Equals(item.SubjectReference.Trim(), corrected.SubjectReference.Trim(), StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(item.Predicate.Trim(), corrected.Predicate.Trim(), StringComparison.OrdinalIgnoreCase) &&
                     item.EventTime == corrected.EventTime))
        {
            if (!decimal.TryParse(other.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var otherNumber) ||
                otherNumber == correctedNumber || Evidence.Contradictions.Any(item =>
                    (item.AssertionAId == corrected.Id && item.AssertionBId == other.Id) ||
                    (item.AssertionAId == other.Id && item.AssertionBId == corrected.Id)))
            {
                continue;
            }

            var ordered = corrected.Id.CompareTo(other.Id) <= 0
                ? (First: corrected.Id, Second: other.Id)
                : (First: other.Id, Second: corrected.Id);
            Evidence.AddContradiction(
                DeterministicId("correction-numeric-contradiction", MatterId, ordered.First, ordered.Second),
                ordered.First,
                ordered.Second,
                ContradictionType.NumericMismatch,
                "rule:human-correction-numeric-mismatch:v1",
                detectedAt);
        }
    }

    private void AddResolutionAction(EntityResolutionAction action)
    {
        if (_entityResolutionActions.Any(item => item.Id == action.Id))
        {
            throw new InvalidOperationException("An entity-resolution action id already exists.");
        }

        _entityResolutionActions.Add(action);
    }

    private void RequireEntity(CanonicalEntityKind kind, Guid entityId)
    {
        RequireDefined(kind);
        RequireId(entityId, nameof(entityId));
        var exists = kind switch
        {
            CanonicalEntityKind.Person => _people.ContainsKey(entityId),
            CanonicalEntityKind.Organisation => _organisations.ContainsKey(entityId),
            _ => false
        };
        if (!exists)
        {
            throw new InvalidOperationException("The canonical entity is not registered to this Matter.");
        }
    }

    private void RequireAnyEntity(Guid entityId)
    {
        RequireId(entityId, nameof(entityId));
        if (!_people.ContainsKey(entityId) && !_organisations.ContainsKey(entityId))
        {
            throw new InvalidOperationException("The communication entity is not registered to this Matter.");
        }
    }

    private void RequireCanonical(CanonicalRecordKind kind, Guid id)
    {
        RequireId(id, nameof(id));
        var exists = kind switch
        {
            CanonicalRecordKind.Person => _people.ContainsKey(id),
            CanonicalRecordKind.Organisation => _organisations.ContainsKey(id),
            CanonicalRecordKind.Communication => _communications.ContainsKey(id),
            CanonicalRecordKind.Assertion => Evidence.Assertions.Any(item => item.Id == id),
            CanonicalRecordKind.Event => Evidence.Events.Any(item => item.Id == id),
            CanonicalRecordKind.AssertionEventLink => Evidence.AssertionEventLinks.Any(item => item.Id == id),
            CanonicalRecordKind.Contradiction => Evidence.Contradictions.Any(item => item.Id == id),
            CanonicalRecordKind.AnalysisNode => Evidence.AnalysisNodes.Any(item => item.Id == id),
            _ => false
        };
        if (!exists)
        {
            throw new InvalidOperationException("A candidate or dependency references unavailable canonical state.");
        }
    }

    private void ValidateSources(IEnumerable<Guid> sourceSpanIds)
    {
        ArgumentNullException.ThrowIfNull(sourceSpanIds);
        foreach (var id in sourceSpanIds.Distinct())
        {
            if (Evidence.SourceSpans.All(item => item.Id != id))
            {
                throw new InvalidOperationException("Entity-resolution evidence must belong to the same Matter.");
            }
        }
    }

    private void RequireMatter(Guid matterId)
    {
        if (matterId != MatterId)
        {
            throw new InvalidOperationException("Matter Brain records cannot cross Matter boundaries.");
        }
    }

    internal static string NormalizeAlias(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();
    }

    internal static Guid DeterministicId(string scope, params object[] values)
    {
        var text = scope + "\u001f" + string.Join("\u001f", values.Select(item =>
            item switch
            {
                DateTimeOffset timestamp => timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => item.ToString()
            }));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return new Guid(bytes[..16]);
    }

    private static string RequireText(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value;
    }

    private static void RequireId(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A non-empty id is required.", parameterName);
        }
    }

    private static void RequireDefined<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "A defined enum value is required.");
        }
    }

    private static void ValidateScore(decimal? score)
    {
        if (score is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(score), "A score must be between zero and one.");
        }
    }

    private static bool SameRun(ExtractionRun first, ExtractionRun second) =>
        first.Id == second.Id && first.MatterId == second.MatterId &&
        first.Fingerprint == second.Fingerprint && first.Provider == second.Provider &&
        first.SourceSpanIds.SequenceEqual(second.SourceSpanIds) &&
        first.GeneratedAt == second.GeneratedAt && first.RawResultDigest == second.RawResultDigest;

    private static bool SameCandidate(ExtractionCandidateRecord first, ExtractionCandidateRecord second) =>
        first.Id == second.Id && first.MatterId == second.MatterId && first.RunId == second.RunId &&
        first.ExternalKey == second.ExternalKey && first.Kind == second.Kind &&
        first.Disposition == second.Disposition && first.RejectionCode == second.RejectionCode &&
        first.SourceSpanIds.SequenceEqual(second.SourceSpanIds) &&
        first.ExtractionConfidence == second.ExtractionConfidence &&
        first.CanonicalKind == second.CanonicalKind && first.CanonicalId == second.CanonicalId &&
        first.PayloadJson == second.PayloadJson && first.PayloadDigest == second.PayloadDigest;

    private static void ValidateDigest(string digest, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(digest) || digest.Length != 64 || digest.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("A 64-character SHA-256 digest is required.", parameterName);
        }
    }

    private static void RequireSnapshotLists(MatterBrainSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot.People);
        ArgumentNullException.ThrowIfNull(snapshot.Organisations);
        ArgumentNullException.ThrowIfNull(snapshot.Aliases);
        ArgumentNullException.ThrowIfNull(snapshot.Communications);
        ArgumentNullException.ThrowIfNull(snapshot.Runs);
        ArgumentNullException.ThrowIfNull(snapshot.Candidates);
        ArgumentNullException.ThrowIfNull(snapshot.Dependencies);
        ArgumentNullException.ThrowIfNull(snapshot.DependencyInvalidations);
        ArgumentNullException.ThrowIfNull(snapshot.EntityResolutionActions);
    }

    private static void RequireUnique(IEnumerable<Guid> ids, string label)
    {
        var seen = new HashSet<Guid>();
        foreach (var id in ids)
        {
            RequireId(id, label);
            if (!seen.Add(id))
            {
                throw new InvalidOperationException($"Persisted {label} ids must be unique.");
            }
        }
    }
}
