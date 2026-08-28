using System.Text;
using CaseMesh.Core.Models;
using CaseMesh.Live;
using Npgsql;

namespace CaseMesh.Persistence.Postgres;

public sealed class PostgresUploadedMeetingReviewRepository
{
    private readonly PostgresMatterStore _matterStore;

    public PostgresUploadedMeetingReviewRepository(PostgresMatterStore matterStore) =>
        _matterStore = matterStore ?? throw new ArgumentNullException(nameof(matterStore));

    public Task SaveAsync(
        Guid createdByUserId,
        UploadedMeetingReview review,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        if (createdByUserId == Guid.Empty) throw new ArgumentException("User id is required.", nameof(createdByUserId));
        if (review.MatterId == Guid.Empty || review.MeetingId == Guid.Empty)
            throw new ArgumentException("Matter and meeting ids are required.", nameof(review));
        if (review.Items.Count == 0) throw new ArgumentException("A persisted meeting review requires transcript items.", nameof(review));
        if (createdAt == default) throw new ArgumentException("Created timestamp is required.", nameof(createdAt));

        return _matterStore.InTenantTransactionAsync(review.TenantId, async (connection, transaction) =>
        {
            await EnsureQuotaAsync(connection, transaction, review, cancellationToken);

            await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                INSERT INTO casemesh.uploaded_meeting_reviews
                    (tenant_id, matter_id, meeting_id, created_by_user_id, context_currentness, created_at)
                VALUES ($1,$2,$3,$4,$5,$6);
                """, cancellationToken,
                review.TenantId.Value, review.MatterId, review.MeetingId, createdByUserId,
                (short)review.ContextCurrentness, createdAt);

            for (var itemOrdinal = 0; itemOrdinal < review.Items.Count; itemOrdinal++)
            {
                var item = review.Items[itemOrdinal];
                await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                    INSERT INTO casemesh.uploaded_meeting_review_items
                        (tenant_id, matter_id, meeting_id, item_id, ordinal, origin,
                         transcript_text, started_at, ended_at)
                    VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9);
                    """, cancellationToken,
                    review.TenantId.Value, review.MatterId, review.MeetingId, item.Id, itemOrdinal,
                    (short)item.Origin, item.Text, item.StartedAt, item.EndedAt);

                for (var citationOrdinal = 0; citationOrdinal < item.ContextCitationSourceSpanIds.Count; citationOrdinal++)
                {
                    await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                        INSERT INTO casemesh.uploaded_meeting_review_context_citations
                            (tenant_id, matter_id, meeting_id, item_id, source_span_id, ordinal)
                        VALUES ($1,$2,$3,$4,$5,$6);
                        """, cancellationToken,
                        review.TenantId.Value, review.MatterId, review.MeetingId, item.Id,
                        item.ContextCitationSourceSpanIds[citationOrdinal], citationOrdinal);
                }
            }

            return true;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<UploadedMeetingReviewSummary>> ListAsync(
        TenantId tenantId,
        Guid matterId,
        CancellationToken cancellationToken = default)
    {
        RequireId(matterId, nameof(matterId));
        return _matterStore.InTenantTransactionAsync(tenantId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                SELECT r.meeting_id, r.context_currentness, r.created_at,
                       MIN(i.started_at), MAX(i.ended_at), COUNT(*)
                FROM casemesh.uploaded_meeting_reviews r
                JOIN casemesh.uploaded_meeting_review_items i
                  ON i.tenant_id=r.tenant_id AND i.matter_id=r.matter_id AND i.meeting_id=r.meeting_id
                WHERE r.tenant_id=$1 AND r.matter_id=$2
                GROUP BY r.meeting_id, r.context_currentness, r.created_at
                ORDER BY r.created_at DESC, r.meeting_id;
                """, connection, transaction);
            PostgresMatterStore.AddParameters(command, tenantId.Value, matterId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var result = new List<UploadedMeetingReviewSummary>();
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(new UploadedMeetingReviewSummary(
                    reader.GetGuid(0),
                    ReadCurrentness(reader.GetInt16(1)),
                    reader.GetFieldValue<DateTimeOffset>(2),
                    reader.GetFieldValue<DateTimeOffset>(3),
                    reader.GetFieldValue<DateTimeOffset>(4),
                    checked((int)reader.GetInt64(5))));
            }
            return (IReadOnlyList<UploadedMeetingReviewSummary>)result;
        }, cancellationToken);
    }

    public Task<UploadedMeetingReviewRecord?> LoadAsync(
        TenantId tenantId,
        Guid matterId,
        Guid meetingId,
        CancellationToken cancellationToken = default)
    {
        RequireId(matterId, nameof(matterId));
        RequireId(meetingId, nameof(meetingId));
        return _matterStore.InTenantTransactionAsync(tenantId, async (connection, transaction) =>
        {
            CanonicalLiveCurrentness contextCurrentness;
            DateTimeOffset createdAt;
            await using (var header = new NpgsqlCommand("""
                SELECT context_currentness, created_at
                FROM casemesh.uploaded_meeting_reviews
                WHERE tenant_id=$1 AND matter_id=$2 AND meeting_id=$3;
                """, connection, transaction))
            {
                PostgresMatterStore.AddParameters(header, tenantId.Value, matterId, meetingId);
                await using var reader = await header.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) return null;
                contextCurrentness = ReadCurrentness(reader.GetInt16(0));
                createdAt = reader.GetFieldValue<DateTimeOffset>(1);
            }

            var ordered = new List<ItemAccumulator>();
            var byId = new Dictionary<Guid, ItemAccumulator>();
            await using (var command = new NpgsqlCommand("""
                SELECT i.item_id, i.origin, i.transcript_text, i.started_at, i.ended_at,
                       c.source_span_id
                FROM casemesh.uploaded_meeting_review_items i
                LEFT JOIN casemesh.uploaded_meeting_review_context_citations c
                  ON c.tenant_id=i.tenant_id AND c.matter_id=i.matter_id
                 AND c.meeting_id=i.meeting_id AND c.item_id=i.item_id
                WHERE i.tenant_id=$1 AND i.matter_id=$2 AND i.meeting_id=$3
                ORDER BY i.ordinal, c.ordinal;
                """, connection, transaction))
            {
                PostgresMatterStore.AddParameters(command, tenantId.Value, matterId, meetingId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var itemId = reader.GetGuid(0);
                    if (!byId.TryGetValue(itemId, out var item))
                    {
                        item = new ItemAccumulator(
                            itemId,
                            ReadOrigin(reader.GetInt16(1)),
                            reader.GetString(2),
                            reader.GetFieldValue<DateTimeOffset>(3),
                            reader.GetFieldValue<DateTimeOffset>(4));
                        byId.Add(itemId, item);
                        ordered.Add(item);
                    }

                    if (!reader.IsDBNull(5)) item.Citations.Add(reader.GetGuid(5));
                }
            }

            if (ordered.Count == 0)
                throw new InvalidOperationException("Persisted uploaded meeting review contains no transcript items.");

            var review = new UploadedMeetingReview(
                tenantId,
                matterId,
                meetingId,
                contextCurrentness,
                ordered.Select(item => new LiveConversationItem(
                    item.Id,
                    item.Origin,
                    item.Text,
                    item.StartedAt,
                    item.EndedAt,
                    item.Citations.ToArray())).ToArray());
            return new UploadedMeetingReviewRecord(review, createdAt);
        }, cancellationToken);
    }

    private static async Task EnsureQuotaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        UploadedMeetingReview review,
        CancellationToken cancellationToken)
    {
        long additionalBytes = 0;
        foreach (var item in review.Items)
            additionalBytes = checked(additionalBytes + Encoding.UTF8.GetByteCount(item.Text));

        await using (var quotaLock = new NpgsqlCommand(
                         "SELECT pg_advisory_xact_lock(hashtextextended($1,0));",
                         connection,
                         transaction))
        {
            quotaLock.Parameters.AddWithValue($"casemesh-review-quota:{review.TenantId.Value:D}");
            await quotaLock.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = new NpgsqlCommand("""
            SELECT entitlement.matter_review_session_limit,
                   entitlement.tenant_review_session_limit,
                   entitlement.matter_review_bytes_limit,
                   entitlement.tenant_review_bytes_limit,
                   (SELECT count(*) FROM casemesh.uploaded_meeting_reviews
                    WHERE tenant_id=$1 AND matter_id=$2),
                   (SELECT count(*) FROM casemesh.uploaded_meeting_reviews
                    WHERE tenant_id=$1),
                   (SELECT COALESCE(sum(octet_length(item.transcript_text)),0)
                    FROM casemesh.uploaded_meeting_review_items item
                    WHERE item.tenant_id=$1 AND item.matter_id=$2),
                   (SELECT COALESCE(sum(octet_length(item.transcript_text)),0)
                    FROM casemesh.uploaded_meeting_review_items item
                    WHERE item.tenant_id=$1)
            FROM casemesh.pilot_entitlements entitlement
            WHERE entitlement.tenant_id=$1;
            """, connection, transaction);
        PostgresMatterStore.AddParameters(command, review.TenantId.Value, review.MatterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("The tenant has no configured pilot entitlement.");

        var matterSessionLimit = reader.GetInt32(0);
        var tenantSessionLimit = reader.GetInt32(1);
        var matterBytesLimit = reader.GetInt64(2);
        var tenantBytesLimit = reader.GetInt64(3);
        var matterSessions = reader.GetInt64(4);
        var tenantSessions = reader.GetInt64(5);
        var matterBytes = reader.GetInt64(6);
        var tenantBytes = reader.GetInt64(7);

        if (matterSessions + 1 > matterSessionLimit)
            throw new PilotQuotaExceededException("matter-review-session-limit");
        if (tenantSessions + 1 > tenantSessionLimit)
            throw new PilotQuotaExceededException("tenant-review-session-limit");
        if (checked(matterBytes + additionalBytes) > matterBytesLimit)
            throw new PilotQuotaExceededException("matter-review-bytes-limit");
        if (checked(tenantBytes + additionalBytes) > tenantBytesLimit)
            throw new PilotQuotaExceededException("tenant-review-bytes-limit");
    }

    private static CanonicalLiveCurrentness ReadCurrentness(short value) =>
        Enum.IsDefined((CanonicalLiveCurrentness)value)
            ? (CanonicalLiveCurrentness)value
            : throw new InvalidOperationException("Persisted meeting review currentness is invalid.");

    private static LiveConversationOrigin ReadOrigin(short value) =>
        Enum.IsDefined((LiveConversationOrigin)value)
            ? (LiveConversationOrigin)value
            : throw new InvalidOperationException("Persisted meeting review origin is invalid.");

    private static void RequireId(Guid id, string parameterName)
    {
        if (id == Guid.Empty) throw new ArgumentException("A non-empty id is required.", parameterName);
    }

    private sealed class ItemAccumulator(
        Guid id,
        LiveConversationOrigin origin,
        string text,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt)
    {
        public Guid Id { get; } = id;
        public LiveConversationOrigin Origin { get; } = origin;
        public string Text { get; } = text;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public DateTimeOffset EndedAt { get; } = endedAt;
        public List<Guid> Citations { get; } = [];
    }
}