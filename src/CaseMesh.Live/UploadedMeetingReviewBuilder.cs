namespace CaseMesh.Live;

public sealed class UploadedMeetingReviewBuilder
{
    public const int MaximumItems = 500;
    public const int MaximumItemTextCharacters = 8_000;
    public const int MaximumTranscriptCharacters = 1_000_000;
    public const int MaximumContextCitationsPerItem = 16;
    public static readonly TimeSpan MaximumReviewDuration = TimeSpan.FromHours(24);

    public UploadedMeetingReview Build(
        CanonicalLiveContext context,
        Guid meetingId,
        IReadOnlyList<LiveConversationItem> items)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(items);
        if (meetingId == Guid.Empty)
        {
            throw new ArgumentException("Meeting id is required.", nameof(meetingId));
        }

        if (items.Count == 0 || items.Count > MaximumItems)
        {
            throw new InvalidOperationException($"Meeting review items must contain between 1 and {MaximumItems} entries.");
        }

        var currentSourceSpanIds = context.Evidence
            .Where(item => item.RecordStatus == LiveEvidenceRecordStatus.Current)
            .Select(item => item.SourceSpanId)
            .ToHashSet();

        var ids = new HashSet<Guid>();
        var totalCharacters = 0;
        DateTimeOffset? earliest = null;
        DateTimeOffset? latest = null;
        DateTimeOffset? previousStartedAt = null;
        foreach (var item in items)
        {
            if (item.Id == Guid.Empty || !ids.Add(item.Id))
            {
                throw new InvalidOperationException("Meeting review items require distinct non-empty ids.");
            }

            if (!Enum.IsDefined(item.Origin))
            {
                throw new InvalidOperationException("Meeting review item origin is invalid.");
            }

            var text = item.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumItemTextCharacters)
            {
                throw new InvalidOperationException(
                    $"Meeting review item text must contain between 1 and {MaximumItemTextCharacters} characters.");
            }

            totalCharacters = checked(totalCharacters + text.Length);
            if (totalCharacters > MaximumTranscriptCharacters)
            {
                throw new InvalidOperationException(
                    $"Meeting review transcript text cannot exceed {MaximumTranscriptCharacters} characters.");
            }

            if (item.StartedAt == default || item.EndedAt == default || item.EndedAt < item.StartedAt)
            {
                throw new InvalidOperationException("Meeting review item timestamps are invalid.");
            }

            if (previousStartedAt.HasValue && item.StartedAt < previousStartedAt.Value)
            {
                throw new InvalidOperationException("Meeting review items must be supplied in chronological order.");
            }
            previousStartedAt = item.StartedAt;

            earliest = earliest.HasValue && earliest.Value <= item.StartedAt ? earliest : item.StartedAt;
            latest = latest.HasValue && latest.Value >= item.EndedAt ? latest : item.EndedAt;

            if (item.ContextCitationSourceSpanIds is null)
            {
                throw new InvalidOperationException("Meeting review context citations are required as a collection.");
            }

            if (item.ContextCitationSourceSpanIds.Count > MaximumContextCitationsPerItem)
            {
                throw new InvalidOperationException(
                    $"A meeting review item cannot attach more than {MaximumContextCitationsPerItem} context citations.");
            }

            if (item.ContextCitationSourceSpanIds.Any(id => id == Guid.Empty) ||
                item.ContextCitationSourceSpanIds.Count != item.ContextCitationSourceSpanIds.Distinct().Count())
            {
                throw new InvalidOperationException("Context citations must be distinct non-empty ids.");
            }

            if (item.ContextCitationSourceSpanIds.Any(id => !currentSourceSpanIds.Contains(id)))
            {
                throw new InvalidOperationException("Meeting review context citations must resolve to current canonical Matter evidence.");
            }
        }

        if (earliest.HasValue && latest.HasValue && latest.Value - earliest.Value > MaximumReviewDuration)
        {
            throw new InvalidOperationException($"A meeting review cannot span more than {MaximumReviewDuration.TotalHours:0} hours.");
        }

        var normalized = items
            .Select(item => item with
            {
                ContextCitationSourceSpanIds = item.ContextCitationSourceSpanIds.OrderBy(id => id).ToArray()
            })
            .ToArray();

        return new UploadedMeetingReview(
            context.TenantId,
            context.MatterId,
            meetingId,
            context.Currentness,
            normalized);
    }
}