namespace CaseMesh.Live;

public sealed class UploadedMeetingReviewBuilder
{
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

        var currentSourceSpanIds = context.Evidence
            .Where(item => item.RecordStatus == LiveEvidenceRecordStatus.Current)
            .Select(item => item.Citation.SourceSpanId)
            .ToHashSet();

        var ids = new HashSet<Guid>();
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

            if (string.IsNullOrWhiteSpace(item.Text))
            {
                throw new InvalidOperationException("Meeting review item text is required.");
            }

            if (item.EndedAt < item.StartedAt)
            {
                throw new InvalidOperationException("Meeting review item end time cannot precede its start time.");
            }

            if (item.ContextCitationSourceSpanIds.Count != item.ContextCitationSourceSpanIds.Distinct().Count())
            {
                throw new InvalidOperationException("Context citations must be distinct.");
            }

            if (item.ContextCitationSourceSpanIds.Any(id => !currentSourceSpanIds.Contains(id)))
            {
                throw new InvalidOperationException("Meeting review context citations must resolve to current canonical Matter evidence.");
            }
        }

        var normalized = items
            .OrderBy(item => item.StartedAt)
            .ThenBy(item => item.EndedAt)
            .ThenBy(item => item.Id)
            .Select(item => item with
            {
                Text = item.Text.Trim(),
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
