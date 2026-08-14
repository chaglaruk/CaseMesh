using CaseMesh.Core.Models;
using CaseMesh.Core.Snapshots;

namespace CaseMesh.Core.Services;

public sealed partial class MatterEvidenceGraph
{
    public MatterEvidenceSnapshot CaptureSnapshot() => new(
        Matter,
        DocumentVersions.Select(version => new DocumentVersionSnapshot(
            version.DocumentId,
            version.DocumentVersionId,
            version.OriginalObjectId,
            version.ContentSha256)).ToArray(),
        SourceSpans.Select(span => new SourceSpanSnapshot(
            span.Id,
            span.DocumentVersion.DocumentVersionId,
            span.PageNumber,
            span.TextStart,
            span.TextEnd,
            span.ExtractedText,
            span.ExtractedTextDigest,
            span.ParserVersion,
            span.ExtractionConfidence)).ToArray(),
        Assertions.Select(assertion => new AssertionSnapshot(
            assertion.Id,
            assertion.SubjectReference,
            assertion.Predicate,
            assertion.Value,
            assertion.AssertedBy,
            assertion.EventTime,
            assertion.AssertedAt,
            assertion.SourceSpanId,
            assertion.OriginClass,
            assertion.AssertionClass,
            assertion.DisputeState,
            assertion.IntegrityState,
            assertion.VerificationState,
            assertion.ExtractionConfidence,
            assertion.CreatedByModel,
            assertion.SupersededByAssertionId)).ToArray(),
        Events.Select(matterEvent => new MatterEventSnapshot(
            matterEvent.Id,
            matterEvent.EventType,
            matterEvent.StartTime,
            matterEvent.EndTime,
            matterEvent.ParticipantIds,
            matterEvent.Label,
            matterEvent.Status,
            matterEvent.VerificationState,
            matterEvent.SupersedesEventId,
            matterEvent.SupersededByEventId)).ToArray(),
        AssertionEventLinks.Select(link => new AssertionEventLinkSnapshot(
            link.Id,
            link.AssertionId,
            link.EventId,
            link.Relation)).ToArray(),
        Contradictions.Select(contradiction => new ContradictionSnapshot(
            contradiction.Id,
            contradiction.AssertionAId,
            contradiction.AssertionBId,
            contradiction.Type,
            contradiction.DetectedBy,
            contradiction.ResolutionState,
            contradiction.ResolutionNote,
            contradiction.CreatedAt,
            contradiction.ResolvedAt)).ToArray(),
        AnalysisNodes.Select(node => new AnalysisNodeSnapshot(
            node.Id,
            node.AnalysisType,
            node.SourceSpanIds,
            node.Provider,
            node.Model,
            node.PromptVersion,
            node.Output,
            node.GeneratedAt,
            node.VerificationState,
            node.SupersededByAnalysisNodeId)).ToArray(),
        AuditEvents.Select(auditEvent => new AuditEventSnapshot(
            auditEvent.Id,
            auditEvent.Kind,
            auditEvent.EntityType,
            auditEvent.EntityId,
            auditEvent.ReplacementEntityId,
            auditEvent.Actor,
            auditEvent.ChangeSummary,
            auditEvent.OccurredAt)).ToArray());

    public static MatterEvidenceGraph Rehydrate(MatterEvidenceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Matter);
        RequireSnapshotLists(snapshot);
        if (snapshot.Matter.TenantId.Value == Guid.Empty)
        {
            throw new InvalidOperationException("A persisted Matter requires tenant ownership.");
        }

        var graph = new MatterEvidenceGraph(snapshot.Matter);
        RequireUniqueIds(snapshot.DocumentVersions.Select(item => item.DocumentVersionId), "document version");
        foreach (var version in snapshot.DocumentVersions)
        {
            var registered = graph.RegisterDocumentVersion(
                version.DocumentId,
                version.DocumentVersionId,
                version.ContentSha256,
                version.OriginalObjectId);
            if (registered.OriginalObjectId != version.OriginalObjectId)
            {
                throw new InvalidOperationException("Persisted duplicate hashes must use the canonical logical-original identity.");
            }
        }

        RequireUniqueIds(snapshot.SourceSpans.Select(item => item.Id), "source span");
        foreach (var span in snapshot.SourceSpans)
        {
            if (!graph._documentVersions.TryGetValue(span.DocumentVersionId, out var version))
            {
                throw new InvalidOperationException("A persisted source span references an unknown document version.");
            }

            var rehydrated = graph.AddSourceSpan(
                span.Id,
                version,
                span.ExtractedText,
                span.ParserVersion,
                span.ExtractionConfidence,
                span.PageNumber,
                span.TextStart,
                span.TextEnd);
            if (!string.Equals(rehydrated.ExtractedTextDigest, span.ExtractedTextDigest, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Persisted source-span text does not match its digest.");
            }
        }

        RequireUniqueIds(snapshot.Assertions.Select(item => item.Id), "assertion");
        foreach (var assertion in snapshot.Assertions)
        {
            graph.AddAssertion(
                assertion.Id,
                assertion.SubjectReference,
                assertion.Predicate,
                assertion.Value,
                assertion.AssertedBy,
                assertion.AssertedAt,
                assertion.OriginClass,
                assertion.AssertionClass,
                assertion.DisputeState,
                assertion.IntegrityState,
                assertion.VerificationState,
                assertion.SourceSpanId,
                assertion.EventTime,
                assertion.ExtractionConfidence,
                assertion.CreatedByModel);
        }

        foreach (var assertion in snapshot.Assertions.Where(item => item.SupersededByAssertionId.HasValue))
        {
            if (!graph._assertions.ContainsKey(assertion.SupersededByAssertionId!.Value) ||
                assertion.DisputeState != DisputeState.Superseded)
            {
                throw new InvalidOperationException("Persisted assertion supersession is invalid.");
            }

            var current = graph._assertions[assertion.Id];
            graph._assertions[assertion.Id] = new Assertion(
                current.Id,
                current.MatterId,
                current.SubjectReference,
                current.Predicate,
                current.Value,
                current.AssertedBy,
                current.EventTime,
                current.AssertedAt,
                current.SourceSpanId,
                current.OriginClass,
                current.AssertionClass,
                current.DisputeState,
                current.IntegrityState,
                current.VerificationState,
                current.ExtractionConfidence,
                current.CreatedByModel,
                assertion.SupersededByAssertionId);
        }

        RequireUniqueIds(snapshot.Events.Select(item => item.Id), "event");
        foreach (var matterEvent in snapshot.Events)
        {
            graph.AddEvent(
                matterEvent.Id,
                matterEvent.EventType,
                matterEvent.Label,
                matterEvent.Status,
                matterEvent.VerificationState,
                matterEvent.StartTime,
                matterEvent.EndTime,
                matterEvent.ParticipantIds);
        }

        foreach (var matterEvent in snapshot.Events)
        {
            ValidateEventSupersession(snapshot.Events, matterEvent);
            graph._events[matterEvent.Id] = new MatterEvent(
                matterEvent.Id,
                snapshot.Matter.Id,
                matterEvent.EventType,
                matterEvent.StartTime,
                matterEvent.EndTime,
                Array.AsReadOnly(matterEvent.ParticipantIds.Distinct().ToArray()),
                matterEvent.Label,
                matterEvent.Status,
                matterEvent.VerificationState,
                matterEvent.SupersedesEventId,
                matterEvent.SupersededByEventId);
        }

        RequireUniqueIds(snapshot.AssertionEventLinks.Select(item => item.Id), "assertion/event link");
        foreach (var link in snapshot.AssertionEventLinks)
        {
            graph.AddAssertionEventLink(link.Id, link.AssertionId, link.EventId, link.Relation);
        }

        RequireUniqueIds(snapshot.Contradictions.Select(item => item.Id), "contradiction");
        var contradictionPairs = new HashSet<(Guid, Guid)>();
        foreach (var contradiction in snapshot.Contradictions)
        {
            RequireDefinedEnum(contradiction.Type);
            RequireDefinedEnum(contradiction.ResolutionState);
            ArgumentException.ThrowIfNullOrWhiteSpace(contradiction.DetectedBy);
            if (contradiction.AssertionAId == contradiction.AssertionBId ||
                !graph._assertions.ContainsKey(contradiction.AssertionAId) ||
                !graph._assertions.ContainsKey(contradiction.AssertionBId))
            {
                throw new InvalidOperationException("Persisted contradiction assertions are invalid.");
            }

            var orderedPair = contradiction.AssertionAId.CompareTo(contradiction.AssertionBId) < 0
                ? (contradiction.AssertionAId, contradiction.AssertionBId)
                : (contradiction.AssertionBId, contradiction.AssertionAId);
            if (!contradictionPairs.Add(orderedPair))
            {
                throw new InvalidOperationException("A persisted contradiction pair is duplicated.");
            }

            if (contradiction.ResolutionState == ContradictionResolutionState.Unresolved)
            {
                if (contradiction.ResolutionNote is not null || contradiction.ResolvedAt.HasValue ||
                    graph._assertions[contradiction.AssertionAId].DisputeState != DisputeState.Contradicted ||
                    graph._assertions[contradiction.AssertionBId].DisputeState != DisputeState.Contradicted)
                {
                    throw new InvalidOperationException("An unresolved persisted contradiction has inconsistent state.");
                }
            }
            else if (!contradiction.ResolvedAt.HasValue)
            {
                throw new InvalidOperationException("A resolved persisted contradiction requires a resolution timestamp.");
            }

            graph._contradictions.Add(contradiction.Id, new Contradiction(
                contradiction.Id,
                snapshot.Matter.Id,
                contradiction.AssertionAId,
                contradiction.AssertionBId,
                contradiction.Type,
                contradiction.DetectedBy,
                contradiction.ResolutionState,
                contradiction.ResolutionNote,
                contradiction.CreatedAt,
                contradiction.ResolvedAt));
        }

        RequireUniqueIds(snapshot.AnalysisNodes.Select(item => item.Id), "analysis node");
        foreach (var node in snapshot.AnalysisNodes)
        {
            graph.AddAnalysisNode(
                node.Id,
                node.AnalysisType,
                node.SourceSpanIds,
                node.Provider,
                node.Model,
                node.PromptVersion,
                node.Output,
                node.GeneratedAt,
                node.VerificationState);
        }

        foreach (var node in snapshot.AnalysisNodes.Where(item => item.SupersededByAnalysisNodeId.HasValue))
        {
            if (!graph._analysisNodes.ContainsKey(node.SupersededByAnalysisNodeId!.Value))
            {
                throw new InvalidOperationException("Persisted analysis supersession references an unknown node.");
            }

            var current = graph._analysisNodes[node.Id];
            graph._analysisNodes[node.Id] = new AnalysisNode(
                current.Id,
                current.MatterId,
                current.AnalysisType,
                current.SourceSpanIds,
                current.Provider,
                current.Model,
                current.PromptVersion,
                current.Output,
                current.GeneratedAt,
                current.VerificationState,
                node.SupersededByAnalysisNodeId);
        }

        RequireUniqueIds(snapshot.AuditEvents.Select(item => item.Id), "audit event");
        foreach (var auditEvent in snapshot.AuditEvents.OrderBy(item => item.OccurredAt))
        {
            RequireDefinedEnum(auditEvent.Kind);
            RequireId(auditEvent.EntityId, nameof(auditEvent.EntityId));
            ArgumentException.ThrowIfNullOrWhiteSpace(auditEvent.EntityType);
            ArgumentException.ThrowIfNullOrWhiteSpace(auditEvent.Actor);
            ArgumentException.ThrowIfNullOrWhiteSpace(auditEvent.ChangeSummary);
            ValidateCorrectionAudit(graph, auditEvent);
            graph._auditEvents.Add(new AuditEvent(
                auditEvent.Id,
                snapshot.Matter.Id,
                auditEvent.Kind,
                auditEvent.EntityType,
                auditEvent.EntityId,
                auditEvent.ReplacementEntityId,
                auditEvent.Actor,
                auditEvent.ChangeSummary,
                auditEvent.OccurredAt));
        }

        return graph;
    }

    private static void RequireSnapshotLists(MatterEvidenceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot.DocumentVersions);
        ArgumentNullException.ThrowIfNull(snapshot.SourceSpans);
        ArgumentNullException.ThrowIfNull(snapshot.Assertions);
        ArgumentNullException.ThrowIfNull(snapshot.Events);
        ArgumentNullException.ThrowIfNull(snapshot.AssertionEventLinks);
        ArgumentNullException.ThrowIfNull(snapshot.Contradictions);
        ArgumentNullException.ThrowIfNull(snapshot.AnalysisNodes);
        ArgumentNullException.ThrowIfNull(snapshot.AuditEvents);
    }

    private static void RequireUniqueIds(IEnumerable<Guid> ids, string label)
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

    private static void ValidateEventSupersession(
        IReadOnlyList<MatterEventSnapshot> events,
        MatterEventSnapshot matterEvent)
    {
        var byId = events.ToDictionary(item => item.Id);
        if (matterEvent.SupersedesEventId.HasValue)
        {
            if (!byId.TryGetValue(matterEvent.SupersedesEventId.Value, out var previous) ||
                previous.SupersededByEventId != matterEvent.Id)
            {
                throw new InvalidOperationException("Persisted event supersession is not bidirectional.");
            }
        }

        if (matterEvent.SupersededByEventId.HasValue)
        {
            if (matterEvent.Status != EventStatus.Superseded ||
                !byId.TryGetValue(matterEvent.SupersededByEventId.Value, out var replacement) ||
                replacement.SupersedesEventId != matterEvent.Id)
            {
                throw new InvalidOperationException("Persisted superseded event state is invalid.");
            }
        }
    }

    private static void ValidateCorrectionAudit(MatterEvidenceGraph graph, AuditEventSnapshot auditEvent)
    {
        if (auditEvent.Kind != AuditEventKind.EventCorrected)
        {
            return;
        }

        if (!graph._events.TryGetValue(auditEvent.EntityId, out var original) ||
            !auditEvent.ReplacementEntityId.HasValue ||
            !graph._events.TryGetValue(auditEvent.ReplacementEntityId.Value, out var replacement) ||
            original.SupersededByEventId != replacement.Id ||
            replacement.SupersedesEventId != original.Id)
        {
            throw new InvalidOperationException("Persisted event-correction audit does not match event history.");
        }
    }
}
