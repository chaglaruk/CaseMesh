using System.Text;
using System.Text.RegularExpressions;
using CaseMesh.Core.Models;
using CaseMesh.Qa;
using Npgsql;

namespace CaseMesh.Persistence.Postgres;

public sealed partial class PostgresMatterEvidenceRetriever : IMatterEvidenceRetriever, IAsyncDisposable
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "at", "be", "do", "does", "evidence", "for", "from", "had", "has",
        "have", "i", "in", "is", "it", "me", "mentions", "my", "of", "on", "or", "that", "the", "this",
        "to", "was", "were", "what", "when", "where", "which", "who", "with"
    };

    private readonly PostgresMatterStore _store;

    public PostgresMatterEvidenceRetriever(string connectionString) => _store = new PostgresMatterStore(connectionString);

    public Task<IReadOnlyList<MatterRetrievalResult>> RetrieveAsync(
        MatterRetrievalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TenantId.Value == Guid.Empty || request.MatterId == Guid.Empty)
            throw new ArgumentException("A non-empty tenant and Matter identity is required.", nameof(request));
        if (request.MaximumResults is < 1 or > 25 || request.MaximumContextBytes is < 1 or > 64 * 1024)
            throw new ArgumentOutOfRangeException(nameof(request));
        var terms = QueryToken().Matches(request.Question ?? string.Empty).Select(match => match.Value.ToLowerInvariant())
            .Where(term => term.Length > 1 && !StopWords.Contains(term)).Distinct(StringComparer.Ordinal).Take(20).ToArray();
        if (terms.Length == 0)
            return Task.FromResult<IReadOnlyList<MatterRetrievalResult>>([]);

        return _store.InTenantTransactionAsync(request.TenantId, async (connection, transaction) =>
            await ReadAsync(connection, transaction, request, terms, cancellationToken), cancellationToken);
    }

    public Task<bool> VerifyCanonicalAsync(
        TenantId tenantId,
        Guid matterId,
        IReadOnlyList<MatterRetrievalResult> results,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (tenantId.Value == Guid.Empty || matterId == Guid.Empty)
            throw new ArgumentException("A non-empty tenant and Matter identity is required.");
        return _store.InTenantTransactionAsync(tenantId, async (connection, transaction) =>
        {
            foreach (var result in results)
            {
                await using var command = new NpgsqlCommand("""
                    SELECT EXISTS (
                        SELECT 1 FROM casemesh.source_spans s
                        JOIN casemesh.document_versions dv USING (tenant_id,matter_id,document_version_id)
                        LEFT JOIN casemesh.document_ingestion_state istate
                          USING (tenant_id,matter_id,document_version_id)
                        WHERE s.tenant_id=$1 AND s.matter_id=$2 AND s.source_span_id=$3
                          AND dv.document_version_id=$4 AND dv.original_object_id=$5 AND dv.content_sha256=$6
                          AND s.extracted_text=$9
                          AND CASE $7::smallint
                            WHEN 0 THEN $8::uuid = s.source_span_id
                              AND $10 = (s.span_set_id IS NOT NULL AND
                                s.span_set_id IS DISTINCT FROM istate.current_span_set_id)
                            WHEN 1 THEN EXISTS (SELECT 1 FROM casemesh.assertions a
                                WHERE a.tenant_id=s.tenant_id AND a.matter_id=s.matter_id
                                  AND a.assertion_id=$8 AND a.source_span_id=s.source_span_id
                                  AND $10 = (a.superseded_by_assertion_id IS NOT NULL OR
                                    a.verification_state=2 OR a.dispute_state=4 OR
                                    (s.span_set_id IS NOT NULL AND
                                     s.span_set_id IS DISTINCT FROM istate.current_span_set_id)))
                            WHEN 2 THEN EXISTS (SELECT 1 FROM casemesh.assertion_event_links l
                                JOIN casemesh.assertions a USING (tenant_id,matter_id,assertion_id)
                                JOIN casemesh.matter_events e USING (tenant_id,matter_id,event_id)
                                WHERE l.tenant_id=s.tenant_id AND l.matter_id=s.matter_id
                                  AND l.event_id=$8 AND a.source_span_id=s.source_span_id
                                  AND $10 = (e.superseded_by_event_id IS NOT NULL OR e.event_status IN (3,4) OR
                                    (s.span_set_id IS NOT NULL AND
                                     s.span_set_id IS DISTINCT FROM istate.current_span_set_id)))
                            WHEN 6 THEN EXISTS (SELECT 1 FROM casemesh.employment_term_assertions link
                                JOIN casemesh.assertions a USING (tenant_id,matter_id,assertion_id)
                                WHERE link.tenant_id=s.tenant_id AND link.matter_id=s.matter_id
                                  AND link.employment_term_id=$8 AND a.source_span_id=s.source_span_id
                                  AND $10 = ((a.superseded_by_assertion_id IS NOT NULL OR a.verification_state=2) OR
                                    EXISTS (SELECT 1 FROM casemesh.employment_terms later
                                      WHERE later.tenant_id=link.tenant_id AND later.matter_id=link.matter_id
                                        AND later.supersedes_employment_term_id=link.employment_term_id) OR
                                    (s.span_set_id IS NOT NULL AND
                                     s.span_set_id IS DISTINCT FROM istate.current_span_set_id)))
                            WHEN 7 THEN EXISTS (SELECT 1 FROM casemesh.health_absence_assertions link
                                JOIN casemesh.assertions a USING (tenant_id,matter_id,assertion_id)
                                WHERE link.tenant_id=s.tenant_id AND link.matter_id=s.matter_id
                                  AND link.health_absence_record_id=$8 AND a.source_span_id=s.source_span_id
                                  AND $10 = (a.superseded_by_assertion_id IS NOT NULL OR a.verification_state=2 OR
                                    (s.span_set_id IS NOT NULL AND
                                     s.span_set_id IS DISTINCT FROM istate.current_span_set_id)))
                            WHEN 8 THEN EXISTS (SELECT 1 FROM casemesh.adjustment_request_assertions link
                                JOIN casemesh.assertions a USING (tenant_id,matter_id,assertion_id)
                                WHERE link.tenant_id=s.tenant_id AND link.matter_id=s.matter_id
                                  AND link.adjustment_request_id=$8 AND a.source_span_id=s.source_span_id
                                  AND $10 = (a.superseded_by_assertion_id IS NOT NULL OR a.verification_state=2 OR
                                    (s.span_set_id IS NOT NULL AND
                                     s.span_set_id IS DISTINCT FROM istate.current_span_set_id)))
                            ELSE false END);
                    """, connection, transaction);
                PostgresMatterStore.AddParameters(command, tenantId.Value, matterId, result.SourceSpanId,
                    result.DocumentVersionId, result.OriginalObjectId, result.OriginalSha256,
                    (short)result.Kind, result.CanonicalId, result.ContextText, result.IsHistorical);
                if (await command.ExecuteScalarAsync(cancellationToken) is not true)
                    return false;
            }
            return true;
        }, cancellationToken);
    }

    public async ValueTask DisposeAsync() => await _store.DisposeAsync();

    private static async Task<IReadOnlyList<MatterRetrievalResult>> ReadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MatterRetrievalRequest request,
        string[] terms,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(Sql, connection, transaction);
        command.Parameters.AddWithValue(request.TenantId.Value);
        command.Parameters.AddWithValue(request.MatterId);
        command.Parameters.AddWithValue(terms);
        command.Parameters.AddWithValue(request.MaximumResults * 4);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<MatterRetrievalResult>();
        var resultIds = new HashSet<Guid>();
        var contextBytes = 0;
        while (await reader.ReadAsync(cancellationToken) && results.Count < request.MaximumResults)
        {
            var kind = (RetrievalMaterialKind)reader.GetInt16(0);
            var canonicalId = reader.GetGuid(1);
            var sourceSpanId = reader.GetGuid(2);
            var context = reader.GetString(7);
            var label = reader.GetString(6);
            var nextBytes = Encoding.UTF8.GetByteCount(context) + Encoding.UTF8.GetByteCount(label);
            if (checked(contextBytes + nextBytes) > request.MaximumContextBytes)
                continue;
            var resultId = MatterRetrievalIdentity.Create(request.TenantId, request.MatterId, kind, canonicalId, sourceSpanId);
            if (!resultIds.Add(resultId))
                continue;
            contextBytes += nextBytes;
            results.Add(new MatterRetrievalResult(
                resultId, kind, canonicalId, sourceSpanId, reader.GetGuid(3), reader.GetGuid(4),
                reader.GetString(5), label, context, reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetBoolean(10),
                decimal.Round((decimal)reader.GetFloat(11), 6)));
        }
        return results;
    }

    [GeneratedRegex("[\\p{L}\\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex QueryToken();

    private const string Sql = """
        WITH query AS (
            SELECT to_tsquery('simple', array_to_string($3::text[], ' | ')) AS value
        ), candidates AS (
            SELECT 1::smallint AS material_kind, a.assertion_id AS canonical_id, s.source_span_id,
                   dv.document_version_id, dv.original_object_id, dv.content_sha256,
                   a.asserted_by || ' asserted: ' || a.subject_reference || ' ' || a.predicate || ' ' || a.value AS label,
                   s.extracted_text AS context_text, a.asserted_by AS attribution,
                   CASE a.dispute_state
                     WHEN 2 THEN 'Disputed' WHEN 3 THEN 'Contradicted' WHEN 4 THEN 'Superseded'
                     WHEN 5 THEN 'Incomplete' WHEN 6 THEN 'Unverified' ELSE NULL END AS dispute_state,
                   (a.superseded_by_assertion_id IS NOT NULL OR a.verification_state = 2 OR a.dispute_state = 4 OR
                    (s.span_set_id IS NOT NULL AND s.span_set_id IS DISTINCT FROM istate.current_span_set_id)) AS historical,
                   GREATEST(
                     ts_rank_cd(to_tsvector('simple', a.subject_reference || ' ' || a.predicate || ' ' || a.value || ' ' || a.asserted_by), q.value),
                     ts_rank_cd(to_tsvector('simple', s.extracted_text), q.value)) +
                     CASE WHEN a.dispute_state IN (2, 3) THEN 0.20 ELSE 0 END AS score
            FROM casemesh.assertions a
            JOIN casemesh.source_spans s USING (tenant_id, matter_id, source_span_id)
            JOIN casemesh.document_versions dv USING (tenant_id, matter_id, document_version_id)
            LEFT JOIN casemesh.document_ingestion_state istate USING (tenant_id,matter_id,document_version_id)
            CROSS JOIN query q
            WHERE a.tenant_id = $1 AND a.matter_id = $2 AND
              (to_tsvector('simple', a.subject_reference || ' ' || a.predicate || ' ' || a.value || ' ' || a.asserted_by) @@ q.value
               OR to_tsvector('simple', s.extracted_text) @@ q.value)

            UNION ALL
            SELECT 2::smallint, e.event_id, s.source_span_id, dv.document_version_id,
                   dv.original_object_id, dv.content_sha256, e.label, s.extracted_text, a.asserted_by,
                   CASE WHEN e.event_status = 2 THEN 'Disputed' WHEN a.dispute_state = 3 THEN 'Contradicted' ELSE NULL END,
                   (e.superseded_by_event_id IS NOT NULL OR e.event_status IN (3, 4) OR
                    (s.span_set_id IS NOT NULL AND s.span_set_id IS DISTINCT FROM istate.current_span_set_id)),
                   GREATEST(ts_rank_cd(to_tsvector('simple', e.event_type || ' ' || e.label), q.value),
                            ts_rank_cd(to_tsvector('simple', s.extracted_text), q.value)) + 0.05
            FROM casemesh.matter_events e
            JOIN casemesh.assertion_event_links l USING (tenant_id, matter_id, event_id)
            JOIN casemesh.assertions a USING (tenant_id, matter_id, assertion_id)
            JOIN casemesh.source_spans s USING (tenant_id, matter_id, source_span_id)
            JOIN casemesh.document_versions dv USING (tenant_id, matter_id, document_version_id)
            LEFT JOIN casemesh.document_ingestion_state istate USING (tenant_id,matter_id,document_version_id)
            CROSS JOIN query q
            WHERE e.tenant_id = $1 AND e.matter_id = $2 AND
              (to_tsvector('simple', e.event_type || ' ' || e.label) @@ q.value
               OR to_tsvector('simple', s.extracted_text) @@ q.value)

            UNION ALL
            SELECT 6::smallint, t.employment_term_id, s.source_span_id, dv.document_version_id,
                   dv.original_object_id, dv.content_sha256, 'Employment term: ' || t.term_value,
                   s.extracted_text, a.asserted_by,
                   CASE WHEN a.dispute_state IN (2,3) THEN 'Disputed' ELSE NULL END,
                   (EXISTS (SELECT 1 FROM casemesh.employment_terms later
                           WHERE later.tenant_id=t.tenant_id AND later.matter_id=t.matter_id
                             AND later.supersedes_employment_term_id=t.employment_term_id) OR
                    a.superseded_by_assertion_id IS NOT NULL OR a.verification_state=2 OR
                    (s.span_set_id IS NOT NULL AND s.span_set_id IS DISTINCT FROM istate.current_span_set_id)),
                   GREATEST(ts_rank_cd(to_tsvector('simple', 'employment term ' || t.term_value), q.value),
                            ts_rank_cd(to_tsvector('simple', s.extracted_text), q.value)) + 0.10
            FROM casemesh.employment_terms t
            JOIN casemesh.employment_term_assertions ta USING (tenant_id, matter_id, employment_term_id)
            JOIN casemesh.assertions a USING (tenant_id, matter_id, assertion_id)
            JOIN casemesh.source_spans s USING (tenant_id, matter_id, source_span_id)
            JOIN casemesh.document_versions dv USING (tenant_id, matter_id, document_version_id)
            LEFT JOIN casemesh.document_ingestion_state istate USING (tenant_id,matter_id,document_version_id)
            CROSS JOIN query q
            WHERE t.tenant_id=$1 AND t.matter_id=$2 AND
              (to_tsvector('simple', 'employment term ' || t.term_value) @@ q.value
               OR to_tsvector('simple', s.extracted_text) @@ q.value)

            UNION ALL
            SELECT 7::smallint, h.health_absence_record_id, s.source_span_id, dv.document_version_id,
                   dv.original_object_id, dv.content_sha256, h.neutral_label, s.extracted_text, a.asserted_by,
                   CASE WHEN a.dispute_state IN (2,3) THEN 'Disputed' ELSE NULL END,
                   (a.superseded_by_assertion_id IS NOT NULL OR a.verification_state=2 OR
                    (s.span_set_id IS NOT NULL AND s.span_set_id IS DISTINCT FROM istate.current_span_set_id)),
                   GREATEST(ts_rank_cd(to_tsvector('simple', h.neutral_label), q.value),
                            ts_rank_cd(to_tsvector('simple', s.extracted_text), q.value)) + 0.10
            FROM casemesh.health_absence_records h
            JOIN casemesh.health_absence_assertions ha USING (tenant_id, matter_id, health_absence_record_id)
            JOIN casemesh.assertions a USING (tenant_id, matter_id, assertion_id)
            JOIN casemesh.source_spans s USING (tenant_id, matter_id, source_span_id)
            JOIN casemesh.document_versions dv USING (tenant_id, matter_id, document_version_id)
            LEFT JOIN casemesh.document_ingestion_state istate USING (tenant_id,matter_id,document_version_id)
            CROSS JOIN query q
            WHERE h.tenant_id=$1 AND h.matter_id=$2 AND
              (to_tsvector('simple', h.neutral_label) @@ q.value OR to_tsvector('simple', s.extracted_text) @@ q.value)

            UNION ALL
            SELECT 8::smallint, r.adjustment_request_id, s.source_span_id, dv.document_version_id,
                   dv.original_object_id, dv.content_sha256,
                   CASE ra.evidence_role WHEN 0 THEN 'Adjustment request: ' WHEN 1 THEN 'Employer response: '
                        ELSE 'Implementation evidence: ' END || r.neutral_label,
                   s.extracted_text, a.asserted_by,
                   CASE WHEN a.dispute_state IN (2,3) THEN 'Disputed' ELSE NULL END,
                   (a.superseded_by_assertion_id IS NOT NULL OR a.verification_state=2 OR
                    (s.span_set_id IS NOT NULL AND s.span_set_id IS DISTINCT FROM istate.current_span_set_id)),
                   GREATEST(ts_rank_cd(to_tsvector('simple', 'adjustment request response implementation ' || r.neutral_label), q.value),
                            ts_rank_cd(to_tsvector('simple', s.extracted_text), q.value)) + 0.10
            FROM casemesh.adjustment_requests r
            JOIN casemesh.adjustment_request_assertions ra USING (tenant_id, matter_id, adjustment_request_id)
            JOIN casemesh.assertions a USING (tenant_id, matter_id, assertion_id)
            JOIN casemesh.source_spans s USING (tenant_id, matter_id, source_span_id)
            JOIN casemesh.document_versions dv USING (tenant_id, matter_id, document_version_id)
            LEFT JOIN casemesh.document_ingestion_state istate USING (tenant_id,matter_id,document_version_id)
            CROSS JOIN query q
            WHERE r.tenant_id=$1 AND r.matter_id=$2 AND
              (to_tsvector('simple', 'adjustment request response implementation ' || r.neutral_label) @@ q.value
               OR to_tsvector('simple', s.extracted_text) @@ q.value)

            UNION ALL
            SELECT 0::smallint, s.source_span_id, s.source_span_id, dv.document_version_id,
                   dv.original_object_id, dv.content_sha256, 'Exact documentary source', s.extracted_text,
                   'Documentary record', NULL,
                   (s.span_set_id IS NOT NULL AND s.span_set_id IS DISTINCT FROM istate.current_span_set_id),
                   ts_rank_cd(to_tsvector('simple', s.extracted_text), q.value)
            FROM casemesh.source_spans s
            JOIN casemesh.document_versions dv USING (tenant_id, matter_id, document_version_id)
            LEFT JOIN casemesh.document_ingestion_state istate USING (tenant_id,matter_id,document_version_id)
            CROSS JOIN query q
            WHERE s.tenant_id=$1 AND s.matter_id=$2
              AND to_tsvector('simple', s.extracted_text) @@ q.value
        )
        SELECT material_kind, canonical_id, source_span_id, document_version_id, original_object_id,
               content_sha256, label, context_text, attribution, dispute_state, historical, score
        FROM candidates
        ORDER BY score DESC, material_kind, canonical_id, source_span_id
        LIMIT $4;
        """;
}
