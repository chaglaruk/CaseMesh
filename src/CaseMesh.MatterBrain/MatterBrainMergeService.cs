using System.Globalization;
using System.Text;
using System.Text.Json;
using CaseMesh.Core.Models;

namespace CaseMesh.MatterBrain;

public sealed class MatterBrainMergeService(TimeProvider timeProvider)
{
    private const int MaximumRawResultCharacters = 1_000_000;
    private const int MaximumSelectedSourceSpans = 128;
    private const int MaximumSelectedSourceBytes = 2_000_000;
    private const int MaximumCandidates = 2_000;
    private const int MaximumAggregateCandidateBytes = 4_000_000;
    private const int MaximumRuleContradictions = 2_000;
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<MatterBrainMergeResult> ExtractAndMergeAsync(
        MatterBrainState state,
        IReadOnlyList<Guid> sourceSpanIds,
        IStructuredExtractionProvider provider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(sourceSpanIds);
        ArgumentNullException.ThrowIfNull(provider);
        var descriptor = provider.Descriptor;
        MatterBrainIntegrity.ValidateDescriptor(descriptor);
        if (sourceSpanIds.Count == 0 || sourceSpanIds.Count > MaximumSelectedSourceSpans ||
            sourceSpanIds.Any(id => id == Guid.Empty))
        {
            throw new ArgumentException(
                $"Between one and {MaximumSelectedSourceSpans} non-empty source-span ids are required.",
                nameof(sourceSpanIds));
        }

        var selectedIds = sourceSpanIds.Distinct().Order().ToArray();
        var spans = selectedIds.Select(id => state.Evidence.SourceSpans.SingleOrDefault(item => item.Id == id)
                ?? throw new InvalidOperationException("Every extraction input span must belong to the selected Matter."))
            .ToArray();
        if (spans.Sum(span => (long)Encoding.UTF8.GetByteCount(span.ExtractedText)) > MaximumSelectedSourceBytes)
        {
            throw new InvalidOperationException("The extraction selection exceeds the source-text byte limit.");
        }
        var fingerprint = MatterBrainIntegrity.Fingerprint(descriptor, selectedIds);
        var existing = state.FindRun(fingerprint);
        if (existing is not null)
        {
            return new MatterBrainMergeResult(
                existing,
                state.Candidates.Where(item => item.RunId == existing.Id).ToArray(),
                [],
                true);
        }

        var input = new StructuredExtractionInput(
            state.Evidence.Matter.TenantId,
            state.MatterId,
            spans.Select(span => new StructuredSourceSpan(span.Id, span.ExtractedText, span.ExtractedTextDigest)).ToArray());
        var output = await provider.ExtractAsync(input, cancellationToken)
            ?? throw new InvalidOperationException("The structured extraction provider returned no result.");
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(output.Candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(output.RawStructuredResult);
        if (output.RawStructuredResult.Length > MaximumRawResultCharacters)
        {
            throw new InvalidOperationException("The structured extraction result exceeds the bounded metadata limit.");
        }

        var allCandidates = PrepareCandidates(output.Candidates);
        var generatedAt = _timeProvider.GetUtcNow();
        var runId = MatterBrainState.DeterministicId("extraction-run", state.MatterId, fingerprint);
        var runSequence = checked(state.Runs.Select(item => item.Sequence ?? 0L).DefaultIfEmpty(0L).Max() + 1L);
        var run = new ExtractionRun(
            runId,
            state.MatterId,
            fingerprint,
            descriptor,
            Array.AsReadOnly(selectedIds),
            generatedAt,
            MatterBrainIntegrity.Digest(output.RawStructuredResult),
            runSequence);
        state.AddRun(run);
        state.InvalidateDependencies(selectedIds, runId, generatedAt);

        var candidates = new List<ExtractionCandidateRecord>();
        var changed = new HashSet<Guid>();
        var canonicalByKey = new Dictionary<string, (CanonicalRecordKind Kind, Guid Id)>(StringComparer.Ordinal);
        var inputSet = selectedIds.ToHashSet();
        var duplicateKeys = allCandidates.GroupBy(item => item.Candidate.Key, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (kind, candidate, payloadJson) in allCandidates)
        {
            var rejection = ValidateCandidate(candidate, kind, inputSet, duplicateKeys);
            var candidateId = MatterBrainState.DeterministicId("extraction-candidate", runId, kind, candidate.Key);
            (CanonicalRecordKind Kind, Guid Id)? canonical = null;
            if (rejection is null)
            {
                try
                {
                    canonical = MergeCandidate(
                        state, run, kind, candidate, canonicalByKey, changed);
                }
                catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
                {
                    rejection = "invalid-candidate-reference";
                }
            }

            var record = new ExtractionCandidateRecord(
                candidateId,
                state.MatterId,
                runId,
                candidate.Key,
                kind,
                rejection is null ? CandidateDisposition.Validated : CandidateDisposition.Rejected,
                rejection,
                Array.AsReadOnly((candidate.SourceSpanIds ?? []).Distinct().Order().ToArray()),
                candidate.ExtractionConfidence,
                canonical?.Kind,
                canonical?.Id,
                payloadJson,
                CandidateDigest(kind, payloadJson));
            state.AddCandidate(record);
            candidates.Add(record);
            if (canonical.HasValue)
            {
                canonicalByKey[candidate.Key] = canonical.Value;
                foreach (var sourceId in record.SourceSpanIds)
                {
                    var dependency = new MatterBrainDependency(
                        MatterBrainState.DeterministicId("matter-brain-dependency", runId, sourceId, candidateId, canonical.Value.Id),
                        state.MatterId,
                        runId,
                        sourceId,
                        candidateId,
                        canonical.Value.Kind,
                        canonical.Value.Id);
                    state.AddDependency(dependency);
                }
            }
        }

        AddRuleContradictions(state, run, candidates, changed);
        return new MatterBrainMergeResult(run, candidates.AsReadOnly(), changed.ToArray(), false);
    }

    private static (CanonicalRecordKind Kind, Guid Id)? MergeCandidate(
        MatterBrainState state,
        ExtractionRun run,
        ExtractionCandidateKind kind,
        IStructuredCandidate candidate,
        IReadOnlyDictionary<string, (CanonicalRecordKind Kind, Guid Id)> canonicalByKey,
        ISet<Guid> changed)
    {
        switch (kind)
        {
            case ExtractionCandidateKind.Person:
            case ExtractionCandidateKind.Organisation:
            {
                var entity = (EntityCandidate)candidate;
                var names = entity.Aliases.Append(entity.DisplayName).ToArray();
                var existingId = state.FindExactEntity(entity.Kind, names);
                var id = existingId ?? MatterBrainState.DeterministicId("canonical-entity", run.Id, entity.Key);
                if (!existingId.HasValue && entity.Kind == CanonicalEntityKind.Person)
                {
                    state.AddPerson(id, entity.DisplayName, entity.RoleLabels);
                }
                else if (!existingId.HasValue)
                {
                    state.AddOrganisation(id, entity.DisplayName, entity.TypeLabel);
                }

                foreach (var value in names
                             .GroupBy(MatterBrainState.NormalizeAlias, StringComparer.Ordinal)
                             .Select(group => group.First()))
                {
                    var source = entity.SourceSpanIds.Count == 1 ? entity.SourceSpanIds[0] : (Guid?)null;
                    if (!state.HasAlias(entity.Kind, id, value))
                    {
                        state.AddAlias(
                            MatterBrainState.DeterministicId("entity-alias", id, MatterBrainState.NormalizeAlias(value), source ?? Guid.Empty),
                            entity.Kind,
                            id,
                            value,
                            source);
                    }
                }

                changed.Add(id);
                return (entity.Kind == CanonicalEntityKind.Person ? CanonicalRecordKind.Person : CanonicalRecordKind.Organisation, id);
            }
            case ExtractionCandidateKind.Communication:
            {
                var communication = (CommunicationCandidate)candidate;
                var sender = ResolveEntityKey(communication.SenderEntityKey, canonicalByKey);
                var participants = communication.ParticipantEntityKeys
                    .Select(key => ResolveEntityKey(key, canonicalByKey) ?? throw new InvalidOperationException())
                    .ToArray();
                var id = MatterBrainState.DeterministicId("canonical-communication", run.Id, communication.Key);
                state.AddCommunication(id, communication.Kind, communication.NeutralLabel, communication.OccurredAt,
                    sender, participants, communication.SourceSpanIds);
                changed.Add(id);
                return (CanonicalRecordKind.Communication, id);
            }
            case ExtractionCandidateKind.Assertion:
            {
                var assertion = (AssertionCandidate)candidate;
                var id = MatterBrainState.DeterministicId("canonical-assertion", run.Id, assertion.Key);
                state.Evidence.AddAssertion(
                    id,
                    assertion.SubjectReference,
                    assertion.Predicate,
                    assertion.Value,
                    assertion.AssertedBy,
                    assertion.AssertedAt,
                    assertion.OriginClass,
                    assertion.AssertionClass,
                    DisputeState.Unverified,
                    assertion.IntegrityState,
                    VerificationState.NotReviewed,
                    assertion.SourceSpanId,
                    assertion.EventTime,
                    assertion.ExtractionConfidence,
                    assertion.AssertionClass == AssertionClass.AiInference ? run.Provider.Model : null);
                changed.Add(id);
                return (CanonicalRecordKind.Assertion, id);
            }
            case ExtractionCandidateKind.Event:
            {
                var matterEvent = (EventCandidate)candidate;
                var participantIds = matterEvent.ParticipantEntityKeys
                    .Select(key => ResolveEntityKey(key, canonicalByKey) ?? throw new InvalidOperationException())
                    .ToArray();
                var id = MatterBrainState.DeterministicId("canonical-event", run.Id, matterEvent.Key);
                state.Evidence.AddEvent(id, matterEvent.EventType, matterEvent.NeutralLabel,
                    EventStatus.Candidate, VerificationState.NotReviewed,
                    matterEvent.StartTime, matterEvent.EndTime, participantIds);
                changed.Add(id);
                return (CanonicalRecordKind.Event, id);
            }
            case ExtractionCandidateKind.AssertionEventLink:
            {
                var link = (AssertionEventLinkCandidate)candidate;
                var assertionId = ResolveKey(link.AssertionKey, CanonicalRecordKind.Assertion, canonicalByKey);
                var eventId = ResolveKey(link.EventKey, CanonicalRecordKind.Event, canonicalByKey);
                var id = MatterBrainState.DeterministicId("canonical-assertion-event-link", run.Id, link.Key);
                state.Evidence.AddAssertionEventLink(id, assertionId, eventId, link.Relation);
                changed.Add(id);
                return (CanonicalRecordKind.AssertionEventLink, id);
            }
            case ExtractionCandidateKind.EntityMatch:
            {
                var match = (EntityMatchCandidate)candidate;
                var sourceId = ResolveEntityKey(match.SourceEntityKey, canonicalByKey) ?? throw new InvalidOperationException();
                var targetId = ResolveEntityKey(match.TargetEntityKey, canonicalByKey) ?? throw new InvalidOperationException();
                if (sourceId == targetId)
                {
                    throw new InvalidOperationException("An entity match must reference two distinct canonical identities.");
                }

                var proposalId = MatterBrainState.DeterministicId("entity-merge-proposal", run.Id, match.Key);
                state.ProposeEntityMerge(proposalId, match.Kind, sourceId, targetId,
                    match.SourceSpanIds, match.MatchScore, "structured-extraction", run.GeneratedAt);
                changed.Add(proposalId);
                return null;
            }
            case ExtractionCandidateKind.Contradiction:
            {
                var contradiction = (ContradictionCandidate)candidate;
                var first = ResolveKey(contradiction.AssertionAKey, CanonicalRecordKind.Assertion, canonicalByKey);
                var second = ResolveKey(contradiction.AssertionBKey, CanonicalRecordKind.Assertion, canonicalByKey);
                var id = MatterBrainState.DeterministicId("canonical-contradiction", run.Id, contradiction.Key);
                state.Evidence.AddContradiction(id, first, second, contradiction.Type,
                    contradiction.DetectedBy, run.GeneratedAt);
                changed.Add(id);
                return (CanonicalRecordKind.Contradiction, id);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static void AddRuleContradictions(
        MatterBrainState state,
        ExtractionRun run,
        ICollection<ExtractionCandidateRecord> candidates,
        ISet<Guid> changed)
    {
        var generatedCount = 0;
        var assertionDependencyIds = state.Dependencies
            .Where(item => item.CanonicalKind == CanonicalRecordKind.Assertion)
            .Select(item => item.CanonicalId)
            .ToHashSet();
        var activeAssertionIds = state.ActiveDependencies
            .Where(item => item.CanonicalKind == CanonicalRecordKind.Assertion)
            .Select(item => item.CanonicalId)
            .ToHashSet();
        var existingPairs = state.Evidence.Contradictions
            .Select(item => OrderedPair(item.AssertionAId, item.AssertionBId))
            .ToHashSet();
        var assertions = state.Evidence.Assertions
            .Where(item => item.VerificationState != VerificationState.Rejected &&
                           item.DisputeState != DisputeState.Superseded && item.IsSourceBacked &&
                           (!assertionDependencyIds.Contains(item.Id) || activeAssertionIds.Contains(item.Id)))
            .ToArray();
        foreach (var group in assertions.GroupBy(item => new
                 {
                     Subject = item.SubjectReference.Trim().ToUpperInvariant(),
                     Predicate = item.Predicate.Trim().ToUpperInvariant(),
                     item.EventTime
                 }))
        {
            var values = group.GroupBy(item => item.Value.Trim(), StringComparer.OrdinalIgnoreCase).ToArray();
            if (values.Length < 2)
            {
                continue;
            }

            foreach (var pair in Pair(values.Select(item => item.First()).ToArray()))
            {
                if (!decimal.TryParse(pair.First.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var firstNumber) ||
                    !decimal.TryParse(pair.Second.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var secondNumber) ||
                    firstNumber == secondNumber ||
                    !existingPairs.Add(OrderedPair(pair.First.Id, pair.Second.Id)))
                {
                    continue;
                }

                if (generatedCount >= MaximumRuleContradictions)
                {
                    AddRuleLimitCandidate(state, run, candidates);
                    return;
                }

                var key = $"rule:numeric-mismatch:{pair.First.Id:N}:{pair.Second.Id:N}";
                var candidateId = MatterBrainState.DeterministicId("extraction-candidate", run.Id, ExtractionCandidateKind.Contradiction, key);
                var sourceIds = new[] { pair.First.SourceSpanId, pair.Second.SourceSpanId }
                    .OfType<Guid>().Distinct().Order().ToArray();
                var contradictionId = MatterBrainState.DeterministicId("canonical-contradiction", run.Id, key);
                var payload = MatterBrainIntegrity.CanonicalizeJson(JsonSerializer.Serialize(new
                {
                    assertionAId = pair.First.Id,
                    assertionBId = pair.Second.Id,
                    detectedBy = "rule:same-subject-predicate-time-numeric-mismatch:v1"
                }));
                var candidate = new ExtractionCandidateRecord(
                    candidateId, state.MatterId, run.Id, key, ExtractionCandidateKind.Contradiction,
                    CandidateDisposition.Validated, null, sourceIds, null,
                    CanonicalRecordKind.Contradiction, contradictionId,
                    payload,
                    MatterBrainIntegrity.Digest($"{ExtractionCandidateKind.Contradiction}|{payload}"));
                state.Evidence.AddContradiction(
                    contradictionId,
                    pair.First.Id,
                    pair.Second.Id,
                    ContradictionType.NumericMismatch,
                    "rule:same-subject-predicate-time-numeric-mismatch:v1",
                    run.GeneratedAt);
                state.AddCandidate(candidate);
                candidates.Add(candidate);
                foreach (var sourceId in sourceIds)
                {
                    state.AddDependency(new MatterBrainDependency(
                        MatterBrainState.DeterministicId("matter-brain-dependency", run.Id, sourceId, candidateId, contradictionId),
                        state.MatterId, run.Id, sourceId, candidateId,
                        CanonicalRecordKind.Contradiction, contradictionId));
                }

                changed.Add(contradictionId);
                generatedCount++;
            }
        }
    }

    private static void AddRuleLimitCandidate(
        MatterBrainState state,
        ExtractionRun run,
        ICollection<ExtractionCandidateRecord> candidates)
    {
        const string key = "rule:contradiction-limit-reached";
        var candidateId = MatterBrainState.DeterministicId(
            "extraction-candidate", run.Id, ExtractionCandidateKind.Contradiction, key);
        var payload = MatterBrainIntegrity.CanonicalizeJson(
            JsonSerializer.Serialize(new { maximumGeneratedContradictions = MaximumRuleContradictions }));
        var candidate = new ExtractionCandidateRecord(
            candidateId, state.MatterId, run.Id, key, ExtractionCandidateKind.Contradiction,
            CandidateDisposition.Rejected, "rule-contradiction-limit-reached",
            run.SourceSpanIds, null, null, null, payload,
            MatterBrainIntegrity.Digest($"{ExtractionCandidateKind.Contradiction}|{payload}"));
        state.AddCandidate(candidate);
        candidates.Add(candidate);
    }

    private static IEnumerable<(T First, T Second)> Pair<T>(IReadOnlyList<T> items)
    {
        for (var first = 0; first < items.Count; first++)
        for (var second = first + 1; second < items.Count; second++)
        {
            yield return (items[first], items[second]);
        }
    }

    private static string? ValidateCandidate(
        IStructuredCandidate candidate,
        ExtractionCandidateKind kind,
        IReadOnlySet<Guid> inputSpanIds,
        IReadOnlySet<string> duplicateKeys)
    {
        if (candidate is null || string.IsNullOrWhiteSpace(candidate.Key) || duplicateKeys.Contains(candidate.Key))
        {
            return "invalid-or-duplicate-key";
        }

        if (candidate.ExtractionConfidence is < 0 or > 1)
        {
            return "invalid-extraction-confidence";
        }

        if (candidate.SourceSpanIds is null || candidate.SourceSpanIds.Any(id => !inputSpanIds.Contains(id)))
        {
            return "source-span-not-in-extraction-input";
        }

        if (candidate.SourceSpanIds.Count == 0 && candidate is not AssertionCandidate
            {
                OriginClass: EvidenceOriginClass.AiGeneratedInference,
                AssertionClass: AssertionClass.AiInference
            })
        {
            return "documentary-candidate-requires-selected-source";
        }

        if (!Enum.IsDefined(kind))
        {
            return "undefined-candidate-kind";
        }

        return candidate switch
        {
            EntityCandidate entity when !Enum.IsDefined(entity.Kind) || string.IsNullOrWhiteSpace(entity.DisplayName) ||
                                        entity.Aliases is null || entity.RoleLabels is null ||
                                        entity.Aliases.Any(string.IsNullOrWhiteSpace) ||
                                        entity.RoleLabels.Any(string.IsNullOrWhiteSpace) ||
                                        (entity.Kind == CanonicalEntityKind.Organisation && string.IsNullOrWhiteSpace(entity.TypeLabel)) => "invalid-entity-candidate",
            CommunicationCandidate communication when !Enum.IsDefined(communication.Kind) ||
                                                       string.IsNullOrWhiteSpace(communication.NeutralLabel) ||
                                                       communication.ParticipantEntityKeys is null ||
                                                       communication.ParticipantEntityKeys.Any(string.IsNullOrWhiteSpace) ||
                                                       communication.SourceSpanIds.Count == 0 => "invalid-communication-candidate",
            AssertionCandidate assertion => ValidateAssertion(assertion, inputSpanIds),
            EventCandidate matterEvent when string.IsNullOrWhiteSpace(matterEvent.EventType) ||
                                            string.IsNullOrWhiteSpace(matterEvent.NeutralLabel) ||
                                            matterEvent.EndTime < matterEvent.StartTime ||
                                            matterEvent.ParticipantEntityKeys is null ||
                                            matterEvent.ParticipantEntityKeys.Any(string.IsNullOrWhiteSpace) => "invalid-event-candidate",
            AssertionEventLinkCandidate link when string.IsNullOrWhiteSpace(link.AssertionKey) ||
                                                  string.IsNullOrWhiteSpace(link.EventKey) || !Enum.IsDefined(link.Relation) => "invalid-link-candidate",
            EntityMatchCandidate match when !Enum.IsDefined(match.Kind) || string.IsNullOrWhiteSpace(match.SourceEntityKey) ||
                                            string.IsNullOrWhiteSpace(match.TargetEntityKey) || match.SourceEntityKey == match.TargetEntityKey ||
                                            match.MatchScore is < 0 or > 1 => "invalid-entity-match-candidate",
            ContradictionCandidate contradiction when string.IsNullOrWhiteSpace(contradiction.AssertionAKey) ||
                                                       string.IsNullOrWhiteSpace(contradiction.AssertionBKey) ||
                                                       contradiction.AssertionAKey == contradiction.AssertionBKey ||
                                                       !Enum.IsDefined(contradiction.Type) || string.IsNullOrWhiteSpace(contradiction.DetectedBy) => "invalid-contradiction-candidate",
            _ => null
        };
    }

    private static string? ValidateAssertion(AssertionCandidate assertion, IReadOnlySet<Guid> inputSpanIds)
    {
        if (string.IsNullOrWhiteSpace(assertion.SubjectReference) || string.IsNullOrWhiteSpace(assertion.Predicate) ||
            string.IsNullOrWhiteSpace(assertion.Value) || string.IsNullOrWhiteSpace(assertion.AssertedBy) ||
            !Enum.IsDefined(assertion.OriginClass) || !Enum.IsDefined(assertion.AssertionClass) ||
            !Enum.IsDefined(assertion.IntegrityState))
        {
            return "invalid-assertion-candidate";
        }

        var ai = assertion.OriginClass == EvidenceOriginClass.AiGeneratedInference &&
                 assertion.AssertionClass == AssertionClass.AiInference;
        if ((assertion.OriginClass == EvidenceOriginClass.AiGeneratedInference) !=
            (assertion.AssertionClass == AssertionClass.AiInference))
        {
            return "mixed-ai-documentary-classification";
        }

        if (ai)
        {
            return assertion.SourceSpanId.HasValue || assertion.SourceSpanIds.Count != 0
                ? "ai-inference-cannot-claim-documentary-source"
                : null;
        }

        if (!assertion.SourceSpanId.HasValue || !inputSpanIds.Contains(assertion.SourceSpanId.Value) ||
            assertion.SourceSpanIds.Count != 1 || assertion.SourceSpanIds[0] != assertion.SourceSpanId.Value)
        {
            return "documentary-assertion-requires-selected-source";
        }

        return null;
    }

    private static IEnumerable<(ExtractionCandidateKind Kind, IStructuredCandidate Candidate)> Enumerate(StructuredCandidateBatch batch)
    {
        foreach (var item in batch.Entities) yield return (item.Kind == CanonicalEntityKind.Person ? ExtractionCandidateKind.Person : ExtractionCandidateKind.Organisation, item);
        foreach (var item in batch.Communications) yield return (ExtractionCandidateKind.Communication, item);
        foreach (var item in batch.Assertions) yield return (ExtractionCandidateKind.Assertion, item);
        foreach (var item in batch.Events) yield return (ExtractionCandidateKind.Event, item);
        foreach (var item in batch.AssertionEventLinks) yield return (ExtractionCandidateKind.AssertionEventLink, item);
        foreach (var item in batch.EntityMatches) yield return (ExtractionCandidateKind.EntityMatch, item);
        foreach (var item in batch.Contradictions) yield return (ExtractionCandidateKind.Contradiction, item);
    }

    private static (ExtractionCandidateKind Kind, IStructuredCandidate Candidate, string PayloadJson)[] PrepareCandidates(
        StructuredCandidateBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(batch.Entities);
        ArgumentNullException.ThrowIfNull(batch.Communications);
        ArgumentNullException.ThrowIfNull(batch.Assertions);
        ArgumentNullException.ThrowIfNull(batch.Events);
        ArgumentNullException.ThrowIfNull(batch.AssertionEventLinks);
        ArgumentNullException.ThrowIfNull(batch.EntityMatches);
        ArgumentNullException.ThrowIfNull(batch.Contradictions);

        var candidateCount = (long)batch.Entities.Count + batch.Communications.Count + batch.Assertions.Count +
                             batch.Events.Count + batch.AssertionEventLinks.Count + batch.EntityMatches.Count +
                             batch.Contradictions.Count;
        if (candidateCount > MaximumCandidates)
        {
            throw new InvalidOperationException("The structured candidate count exceeds the bounded batch limit.");
        }

        if (batch.Entities.Any(item => item is null) ||
            batch.Communications.Any(item => item is null) ||
            batch.Assertions.Any(item => item is null) ||
            batch.Events.Any(item => item is null) ||
            batch.AssertionEventLinks.Any(item => item is null) ||
            batch.EntityMatches.Any(item => item is null) ||
            batch.Contradictions.Any(item => item is null))
        {
            throw new InvalidOperationException("Structured candidate lists cannot contain null records.");
        }

        var candidates = Enumerate(batch).ToArray();
        if (candidates.Any(item => string.IsNullOrWhiteSpace(item.Candidate.Key)))
        {
            throw new InvalidOperationException("Structured candidates require non-empty external keys.");
        }
        long aggregateBytes = 0;
        return candidates.Select(item =>
        {
            var payloadJson = CandidateJson(item.Candidate);
            var payloadBytes = Encoding.UTF8.GetByteCount(payloadJson);
            if (payloadBytes > MatterBrainIntegrity.MaximumCandidatePayloadBytes)
            {
                throw new InvalidOperationException("A structured candidate exceeds the bounded metadata limit.");
            }

            aggregateBytes += payloadBytes;
            if (aggregateBytes > MaximumAggregateCandidateBytes)
            {
                throw new InvalidOperationException("The structured candidate batch exceeds the aggregate metadata limit.");
            }

            return (item.Kind, item.Candidate, payloadJson);
        }).ToArray();
    }

    private static Guid ResolveKey(
        string key,
        CanonicalRecordKind kind,
        IReadOnlyDictionary<string, (CanonicalRecordKind Kind, Guid Id)> canonicalByKey)
    {
        if (!canonicalByKey.TryGetValue(key, out var value) || value.Kind != kind)
        {
            throw new InvalidOperationException("A candidate references an unavailable canonical record.");
        }

        return value.Id;
    }

    private static Guid? ResolveEntityKey(
        string? key,
        IReadOnlyDictionary<string, (CanonicalRecordKind Kind, Guid Id)> canonicalByKey)
    {
        if (key is null)
        {
            return null;
        }

        if (!canonicalByKey.TryGetValue(key, out var value) ||
            value.Kind is not (CanonicalRecordKind.Person or CanonicalRecordKind.Organisation))
        {
            throw new InvalidOperationException("A candidate references an unavailable entity.");
        }

        return value.Id;
    }

    private static string CandidateDigest(ExtractionCandidateKind kind, string payloadJson) =>
        MatterBrainIntegrity.Digest($"{kind}|{payloadJson}");

    private static string CandidateJson(IStructuredCandidate candidate) =>
        MatterBrainIntegrity.CanonicalizeJson(JsonSerializer.Serialize(candidate, candidate.GetType()));

    private static (Guid First, Guid Second) OrderedPair(Guid first, Guid second) =>
        first.CompareTo(second) <= 0 ? (first, second) : (second, first);

}
