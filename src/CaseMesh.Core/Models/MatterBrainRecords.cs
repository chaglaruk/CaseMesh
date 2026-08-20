namespace CaseMesh.Core.Models;

public enum CanonicalEntityKind
{
    Person = 0,
    Organisation = 1
}

public enum CommunicationKind
{
    Email = 0,
    Letter,
    Message,
    Meeting,
    Transcript,
    Other
}

public sealed record Person
{
    internal Person(Guid id, Guid matterId, string displayName, IReadOnlyList<string> roleLabels)
    {
        Id = id;
        MatterId = matterId;
        DisplayName = displayName;
        RoleLabels = roleLabels;
    }

    public Guid Id { get; }
    public Guid MatterId { get; }
    public string DisplayName { get; }
    public IReadOnlyList<string> RoleLabels { get; }
}

public sealed record Organisation
{
    internal Organisation(Guid id, Guid matterId, string name, string typeLabel)
    {
        Id = id;
        MatterId = matterId;
        Name = name;
        TypeLabel = typeLabel;
    }

    public Guid Id { get; }
    public Guid MatterId { get; }
    public string Name { get; }
    public string TypeLabel { get; }
}

public sealed record EntityAlias
{
    internal EntityAlias(
        Guid id,
        Guid matterId,
        CanonicalEntityKind entityKind,
        Guid entityId,
        string value,
        string normalizedValue,
        Guid? sourceSpanId)
    {
        Id = id;
        MatterId = matterId;
        EntityKind = entityKind;
        EntityId = entityId;
        Value = value;
        NormalizedValue = normalizedValue;
        SourceSpanId = sourceSpanId;
    }

    public Guid Id { get; }
    public Guid MatterId { get; }
    public CanonicalEntityKind EntityKind { get; }
    public Guid EntityId { get; }
    public string Value { get; }
    public string NormalizedValue { get; }
    public Guid? SourceSpanId { get; }
}

public sealed record Communication
{
    internal Communication(
        Guid id,
        Guid matterId,
        CommunicationKind kind,
        string neutralLabel,
        DateTimeOffset? occurredAt,
        Guid? senderEntityId,
        IReadOnlyList<Guid> participantEntityIds,
        IReadOnlyList<Guid> sourceSpanIds,
        VerificationState verificationState)
    {
        Id = id;
        MatterId = matterId;
        Kind = kind;
        NeutralLabel = neutralLabel;
        OccurredAt = occurredAt;
        SenderEntityId = senderEntityId;
        ParticipantEntityIds = participantEntityIds;
        SourceSpanIds = sourceSpanIds;
        VerificationState = verificationState;
    }

    public Guid Id { get; }
    public Guid MatterId { get; }
    public CommunicationKind Kind { get; }
    public string NeutralLabel { get; }
    public DateTimeOffset? OccurredAt { get; }
    public Guid? SenderEntityId { get; }
    public IReadOnlyList<Guid> ParticipantEntityIds { get; }
    public IReadOnlyList<Guid> SourceSpanIds { get; }
    public VerificationState VerificationState { get; }
}
