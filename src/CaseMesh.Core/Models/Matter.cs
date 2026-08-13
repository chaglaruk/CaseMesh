namespace CaseMesh.Core.Models;

public sealed record Matter
{
    public Matter(
        Guid id,
        string matterType,
        string title,
        string status,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        string? jurisdiction = null)
    {
        if (id == Guid.Empty) throw new ArgumentException("Matter id is required.", nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(matterType);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        if (updatedAt < createdAt) throw new ArgumentOutOfRangeException(nameof(updatedAt), "Updated time cannot precede creation time.");

        Id = id;
        MatterType = matterType;
        Title = title;
        Status = status;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        Jurisdiction = jurisdiction;
    }

    public Guid Id { get; }
    public string MatterType { get; }
    public string Title { get; }
    public string Status { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; }
    public string? Jurisdiction { get; }
}
