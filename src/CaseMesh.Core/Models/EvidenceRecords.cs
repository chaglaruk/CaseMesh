namespace CaseMesh.Core.Models;

public sealed record DocumentVersionIdentity
{
    internal DocumentVersionIdentity(
        Guid matterId,
        Guid documentId,
        Guid documentVersionId,
        Guid originalObjectId,
        string contentSha256)
    {
        MatterId = matterId;
        DocumentId = documentId;
        DocumentVersionId = documentVersionId;
        OriginalObjectId = originalObjectId;
        ContentSha256 = contentSha256;
    }

    public Guid MatterId { get; }
    public Guid DocumentId { get; }
    public Guid DocumentVersionId { get; }
    public Guid OriginalObjectId { get; }
    public string ContentSha256 { get; }
}

public sealed record SourceSpan
{
    internal SourceSpan(
        Guid id,
        DocumentVersionIdentity documentVersion,
        int? pageNumber,
        int? textStart,
        int? textEnd,
        string extractedText,
        string extractedTextDigest,
        string parserVersion,
        decimal? extractionConfidence)
    {
        Id = id;
        DocumentVersion = documentVersion;
        PageNumber = pageNumber;
        TextStart = textStart;
        TextEnd = textEnd;
        ExtractedText = extractedText;
        ExtractedTextDigest = extractedTextDigest;
        ParserVersion = parserVersion;
        ExtractionConfidence = extractionConfidence;
    }

    public Guid Id { get; }
    public Guid MatterId => DocumentVersion.MatterId;
    public DocumentVersionIdentity DocumentVersion { get; }
    public int? PageNumber { get; }
    public int? TextStart { get; }
    public int? TextEnd { get; }
    public string ExtractedText { get; }
    public string ExtractedTextDigest { get; }
    public string ParserVersion { get; }
    public decimal? ExtractionConfidence { get; }
}

public sealed record Assertion
{
    internal Assertion(
        Guid id,
        Guid matterId,
        string subjectReference,
        string predicate,
        string value,
        string assertedBy,
        DateTimeOffset? eventTime,
        DateTimeOffset assertedAt,
        Guid? sourceSpanId,
        EvidenceOriginClass originClass,
        AssertionClass assertionClass,
        DisputeState disputeState,
        IntegrityState integrityState,
        VerificationState verificationState,
        decimal? extractionConfidence,
        string? createdByModel,
        Guid? supersededByAssertionId = null)
    {
        Id = id;
        MatterId = matterId;
        SubjectReference = subjectReference;
        Predicate = predicate;
        Value = value;
        AssertedBy = assertedBy;
        EventTime = eventTime;
        AssertedAt = assertedAt;
        SourceSpanId = sourceSpanId;
        OriginClass = originClass;
        AssertionClass = assertionClass;
        DisputeState = disputeState;
        IntegrityState = integrityState;
        VerificationState = verificationState;
        ExtractionConfidence = extractionConfidence;
        CreatedByModel = createdByModel;
        SupersededByAssertionId = supersededByAssertionId;
    }

    public Guid Id { get; }
    public Guid MatterId { get; }
    public string SubjectReference { get; }
    public string Predicate { get; }
    public string Value { get; }
    public string AssertedBy { get; }
    public DateTimeOffset? EventTime { get; }
    public DateTimeOffset AssertedAt { get; }
    public Guid? SourceSpanId { get; }
    public EvidenceOriginClass OriginClass { get; }
    public AssertionClass AssertionClass { get; }
    public DisputeState DisputeState { get; }
    public IntegrityState IntegrityState { get; }
    public VerificationState VerificationState { get; }
    public decimal? ExtractionConfidence { get; }
    public string? CreatedByModel { get; }
    public Guid? SupersededByAssertionId { get; }
    public bool IsSourceBacked => SourceSpanId.HasValue;

    internal Assertion SupersededBy(Guid replacementId) => new(
        Id,
        MatterId,
        SubjectReference,
        Predicate,
        Value,
        AssertedBy,
        EventTime,
        AssertedAt,
        SourceSpanId,
        OriginClass,
        AssertionClass,
        DisputeState.Superseded,
        IntegrityState,
        VerificationState.Rejected,
        ExtractionConfidence,
        CreatedByModel,
        replacementId);
}

public sealed record MatterEvent
{
    internal MatterEvent(
        Guid id,
        Guid matterId,
        string eventType,
        DateTimeOffset? startTime,
        DateTimeOffset? endTime,
        IReadOnlyList<Guid> participantIds,
        string label,
        EventStatus status,
        VerificationState verificationState,
        Guid? supersedesEventId = null,
        Guid? supersededByEventId = null)
    {
        Id = id;
        MatterId = matterId;
        EventType = eventType;
        StartTime = startTime;
        EndTime = endTime;
        ParticipantIds = participantIds;
        Label = label;
        Status = status;
        VerificationState = verificationState;
        SupersedesEventId = supersedesEventId;
        SupersededByEventId = supersededByEventId;
    }

    public Guid Id { get; }
    public Guid MatterId { get; }
    public string EventType { get; }
    public DateTimeOffset? StartTime { get; }
    public DateTimeOffset? EndTime { get; }
    public IReadOnlyList<Guid> ParticipantIds { get; }
    public string Label { get; }
    public EventStatus Status { get; }
    public VerificationState VerificationState { get; }
    public Guid? SupersedesEventId { get; }
    public Guid? SupersededByEventId { get; }

    internal MatterEvent SupersededBy(Guid replacementId) => new(
        Id,
        MatterId,
        EventType,
        StartTime,
        EndTime,
        ParticipantIds,
        Label,
        EventStatus.Superseded,
        VerificationState.Rejected,
        SupersedesEventId,
        replacementId);
}

public sealed record AssertionEventLink
{
    internal AssertionEventLink(
        Guid id,
        Guid matterId,
        Guid assertionId,
        Guid eventId,
        AssertionEventRelation relation)
    {
        Id = id;
        MatterId = matterId;
        AssertionId = assertionId;
        EventId = eventId;
        Relation = relation;
    }

    public Guid Id { get; }
    public Guid MatterId { get; }
    public Guid AssertionId { get; }
    public Guid EventId { get; }
    public AssertionEventRelation Relation { get; }
}

public sealed record Contradiction
{
    internal Contradiction(
        Guid id,
        Guid matterId,
        Guid assertionAId,
        Guid assertionBId,
        ContradictionType type,
        string detectedBy,
        ContradictionResolutionState resolutionState,
        string? resolutionNote,
        DateTimeOffset createdAt,
        DateTimeOffset? resolvedAt)
    {
        Id = id;
        MatterId = matterId;
        AssertionAId = assertionAId;
        AssertionBId = assertionBId;
        Type = type;
        DetectedBy = detectedBy;
        ResolutionState = resolutionState;
        ResolutionNote = resolutionNote;
        CreatedAt = createdAt;
        ResolvedAt = resolvedAt;
    }

    public Guid Id { get; }
    public Guid MatterId { get; }
    public Guid AssertionAId { get; }
    public Guid AssertionBId { get; }
    public ContradictionType Type { get; }
    public string DetectedBy { get; }
    public ContradictionResolutionState ResolutionState { get; }
    public string? ResolutionNote { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? ResolvedAt { get; }
}

public sealed record AnalysisNode
{
    internal AnalysisNode(
        Guid id,
        Guid matterId,
        string analysisType,
        IReadOnlyList<Guid> sourceSpanIds,
        string provider,
        string model,
        string promptVersion,
        string output,
        DateTimeOffset generatedAt,
        VerificationState verificationState,
        Guid? supersededByAnalysisNodeId)
    {
        Id = id;
        MatterId = matterId;
        AnalysisType = analysisType;
        SourceSpanIds = sourceSpanIds;
        Provider = provider;
        Model = model;
        PromptVersion = promptVersion;
        Output = output;
        GeneratedAt = generatedAt;
        VerificationState = verificationState;
        SupersededByAnalysisNodeId = supersededByAnalysisNodeId;
    }

    public Guid Id { get; }
    public Guid MatterId { get; }
    public string AnalysisType { get; }
    public IReadOnlyList<Guid> SourceSpanIds { get; }
    public string Provider { get; }
    public string Model { get; }
    public string PromptVersion { get; }
    public string Output { get; }
    public DateTimeOffset GeneratedAt { get; }
    public VerificationState VerificationState { get; }
    public Guid? SupersededByAnalysisNodeId { get; }
}

public sealed record AuditEvent
{
    internal AuditEvent(
        Guid id,
        Guid matterId,
        AuditEventKind kind,
        string entityType,
        Guid entityId,
        Guid? replacementEntityId,
        string actor,
        string changeSummary,
        DateTimeOffset occurredAt)
    {
        Id = id;
        MatterId = matterId;
        Kind = kind;
        EntityType = entityType;
        EntityId = entityId;
        ReplacementEntityId = replacementEntityId;
        Actor = actor;
        ChangeSummary = changeSummary;
        OccurredAt = occurredAt;
    }

    public Guid Id { get; }
    public Guid MatterId { get; }
    public AuditEventKind Kind { get; }
    public string EntityType { get; }
    public Guid EntityId { get; }
    public Guid? ReplacementEntityId { get; }
    public string Actor { get; }
    public string ChangeSummary { get; }
    public DateTimeOffset OccurredAt { get; }
}

public sealed record EventCorrectionResult(
    MatterEvent SupersededEvent,
    MatterEvent CorrectedEvent,
    AuditEvent AuditEvent);
