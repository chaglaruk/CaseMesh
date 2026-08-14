using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Core.Snapshots;
using CaseMesh.Core.Workplace;
using Npgsql;

namespace CaseMesh.Persistence.Postgres;

internal static class PostgresMatterReader
{
    internal static async Task<PersistedMatter?> ReadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TenantId tenantId,
        Guid matterId,
        CancellationToken cancellationToken)
    {
        await using var batch = new NpgsqlBatch(connection, transaction);
        foreach (var statement in ReadStatements)
        {
            var batchCommand = new NpgsqlBatchCommand(statement);
            batchCommand.Parameters.AddWithValue(tenantId.Value);
            batchCommand.Parameters.AddWithValue(matterId);
            batch.BatchCommands.Add(batchCommand);
        }

        await using var reader = await batch.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var matter = new Matter(
            reader.GetGuid(0),
            new TenantId(reader.GetGuid(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetFieldValue<DateTimeOffset>(7),
            reader.IsDBNull(5) ? null : reader.GetString(5));

        await reader.NextResultAsync(cancellationToken);
        var versions = new List<DocumentVersionSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            versions.Add(new DocumentVersionSnapshot(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3)));
        }

        await reader.NextResultAsync(cancellationToken);
        var spans = new List<SourceSpanSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            spans.Add(new SourceSpanSnapshot(
                reader.GetGuid(0),
                reader.GetGuid(1),
                GetNullable<int>(reader, 2),
                GetNullable<int>(reader, 3),
                GetNullable<int>(reader, 4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                GetNullable<decimal>(reader, 8)));
        }

        await reader.NextResultAsync(cancellationToken);
        var assertions = new List<AssertionSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            assertions.Add(new AssertionSnapshot(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                GetNullable<DateTimeOffset>(reader, 5),
                reader.GetFieldValue<DateTimeOffset>(6),
                GetNullable<Guid>(reader, 7),
                (EvidenceOriginClass)reader.GetInt16(8),
                (AssertionClass)reader.GetInt16(9),
                (DisputeState)reader.GetInt16(10),
                (IntegrityState)reader.GetInt16(11),
                (VerificationState)reader.GetInt16(12),
                GetNullable<decimal>(reader, 13),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                GetNullable<Guid>(reader, 15)));
        }

        await reader.NextResultAsync(cancellationToken);
        var participantIds = await ReadOrderedGroupsAsync(reader, cancellationToken);

        await reader.NextResultAsync(cancellationToken);
        var events = new List<MatterEventSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var eventId = reader.GetGuid(0);
            events.Add(new MatterEventSnapshot(
                eventId,
                reader.GetString(1),
                GetNullable<DateTimeOffset>(reader, 2),
                GetNullable<DateTimeOffset>(reader, 3),
                participantIds.GetValueOrDefault(eventId) ?? [],
                reader.GetString(4),
                (EventStatus)reader.GetInt16(5),
                (VerificationState)reader.GetInt16(6),
                GetNullable<Guid>(reader, 7),
                GetNullable<Guid>(reader, 8)));
        }

        await reader.NextResultAsync(cancellationToken);
        var links = new List<AssertionEventLinkSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            links.Add(new AssertionEventLinkSnapshot(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2),
                (AssertionEventRelation)reader.GetInt16(3)));
        }

        await reader.NextResultAsync(cancellationToken);
        var contradictions = new List<ContradictionSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            contradictions.Add(new ContradictionSnapshot(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                (ContradictionType)reader.GetInt16(3),
                reader.GetString(4),
                (ContradictionResolutionState)reader.GetInt16(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetFieldValue<DateTimeOffset>(7),
                GetNullable<DateTimeOffset>(reader, 8)));
        }

        await reader.NextResultAsync(cancellationToken);
        var analysisSources = await ReadOrderedGroupsAsync(reader, cancellationToken);

        await reader.NextResultAsync(cancellationToken);
        var analysisNodes = new List<AnalysisNodeSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var nodeId = reader.GetGuid(0);
            analysisNodes.Add(new AnalysisNodeSnapshot(
                nodeId,
                reader.GetString(1),
                analysisSources.GetValueOrDefault(nodeId) ?? [],
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetFieldValue<DateTimeOffset>(6),
                (VerificationState)reader.GetInt16(7),
                GetNullable<Guid>(reader, 8)));
        }

        await reader.NextResultAsync(cancellationToken);
        var auditEvents = new List<AuditEventSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            auditEvents.Add(new AuditEventSnapshot(
                reader.GetGuid(0),
                (AuditEventKind)reader.GetInt16(1),
                reader.GetString(2),
                reader.GetGuid(3),
                GetNullable<Guid>(reader, 4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetFieldValue<DateTimeOffset>(7)));
        }

        await reader.NextResultAsync(cancellationToken);
        var profileAssertions = await ReadGroupsAsync(reader, cancellationToken);
        await reader.NextResultAsync(cancellationToken);
        var profiles = new List<EmploymentProfileSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetGuid(0);
            profiles.Add(new EmploymentProfileSnapshot(
                id, reader.GetString(1), reader.GetString(2), GetNullable<DateOnly>(reader, 3),
                GetNullable<DateOnly>(reader, 4), profileAssertions.GetValueOrDefault(id) ?? [],
                (VerificationState)reader.GetInt16(5)));
        }

        await reader.NextResultAsync(cancellationToken);
        var termAssertions = await ReadGroupsAsync(reader, cancellationToken);
        await reader.NextResultAsync(cancellationToken);
        var terms = new List<EmploymentTermSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetGuid(0);
            terms.Add(new EmploymentTermSnapshot(
                id, (EmploymentTermKind)reader.GetInt16(1), reader.GetString(2),
                GetNullable<DateOnly>(reader, 3), GetNullable<DateOnly>(reader, 4),
                termAssertions.GetValueOrDefault(id) ?? [], GetNullable<Guid>(reader, 5)));
        }

        await reader.NextResultAsync(cancellationToken);
        var healthAssertions = await ReadGroupsAsync(reader, cancellationToken);
        await reader.NextResultAsync(cancellationToken);
        var healthEvents = await ReadGroupsAsync(reader, cancellationToken);
        await reader.NextResultAsync(cancellationToken);
        var healthRecords = new List<HealthAbsenceSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetGuid(0);
            healthRecords.Add(new HealthAbsenceSnapshot(
                id, (HealthAbsenceKind)reader.GetInt16(1), reader.GetString(2),
                healthAssertions.GetValueOrDefault(id) ?? [], healthEvents.GetValueOrDefault(id) ?? [],
                (VerificationState)reader.GetInt16(3)));
        }

        await reader.NextResultAsync(cancellationToken);
        var adjustmentEvidence = await ReadAdjustmentGroupsAsync(reader, cancellationToken);
        await reader.NextResultAsync(cancellationToken);
        var requests = new List<AdjustmentRequestSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetGuid(0);
            requests.Add(new AdjustmentRequestSnapshot(
                id,
                reader.GetString(1),
                adjustmentEvidence.GetValueOrDefault((id, (short)0)) ?? [],
                (AdjustmentResponseStatus)reader.GetInt16(2),
                adjustmentEvidence.GetValueOrDefault((id, (short)1)) ?? [],
                adjustmentEvidence.GetValueOrDefault((id, (short)2)) ?? []));
        }

        await reader.NextResultAsync(cancellationToken);
        var processAssertions = await ReadGroupsAsync(reader, cancellationToken);
        await reader.NextResultAsync(cancellationToken);
        var processEvents = await ReadGroupsAsync(reader, cancellationToken);
        await reader.NextResultAsync(cancellationToken);
        var processes = new List<WorkplaceProcessSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetGuid(0);
            processes.Add(new WorkplaceProcessSnapshot(
                id, (WorkplaceProcessKind)reader.GetInt16(1), reader.GetString(2),
                (WorkplaceProcessStatus)reader.GetInt16(3), processAssertions.GetValueOrDefault(id) ?? [],
                processEvents.GetValueOrDefault(id) ?? [], GetNullable<Guid>(reader, 4), GetNullable<Guid>(reader, 5)));
        }

        await reader.NextResultAsync(cancellationToken);
        var acasAssertions = await ReadGroupsAsync(reader, cancellationToken);
        await reader.NextResultAsync(cancellationToken);
        var acasEvents = await ReadGroupsAsync(reader, cancellationToken);
        await reader.NextResultAsync(cancellationToken);
        var acasStates = new List<AcasProcessStateSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetGuid(0);
            acasStates.Add(new AcasProcessStateSnapshot(
                id, (AcasStage)reader.GetInt16(1), acasAssertions.GetValueOrDefault(id) ?? [],
                acasEvents.GetValueOrDefault(id) ?? []));
        }

        var evidenceSnapshot = new MatterEvidenceSnapshot(
            matter, versions, spans, assertions, events, links, contradictions, analysisNodes, auditEvents);
        var graph = MatterEvidenceGraph.Rehydrate(evidenceSnapshot);
        var workplaceSnapshot = new WorkplaceSnapshot(profiles, terms, healthRecords, requests, processes, acasStates);
        return new PersistedMatter(graph, WorkplaceMatter.Rehydrate(graph, workplaceSnapshot));
    }

    private static T? GetNullable<T>(NpgsqlDataReader reader, int ordinal)
        where T : struct => reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<T>(ordinal);

    private static async Task<Dictionary<Guid, IReadOnlyList<Guid>>> ReadGroupsAsync(
        NpgsqlDataReader reader,
        CancellationToken cancellationToken)
    {
        var groups = new Dictionary<Guid, List<Guid>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var ownerId = reader.GetGuid(0);
            if (!groups.TryGetValue(ownerId, out var ids))
            {
                ids = [];
                groups.Add(ownerId, ids);
            }

            ids.Add(reader.GetGuid(1));
        }

        return groups.ToDictionary(item => item.Key, item => (IReadOnlyList<Guid>)item.Value.AsReadOnly());
    }

    private static Task<Dictionary<Guid, IReadOnlyList<Guid>>> ReadOrderedGroupsAsync(
        NpgsqlDataReader reader,
        CancellationToken cancellationToken) => ReadGroupsAsync(reader, cancellationToken);

    private static async Task<Dictionary<(Guid, short), IReadOnlyList<Guid>>> ReadAdjustmentGroupsAsync(
        NpgsqlDataReader reader,
        CancellationToken cancellationToken)
    {
        var groups = new Dictionary<(Guid, short), List<Guid>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = (reader.GetGuid(0), reader.GetInt16(1));
            if (!groups.TryGetValue(key, out var ids))
            {
                ids = [];
                groups.Add(key, ids);
            }

            ids.Add(reader.GetGuid(2));
        }

        return groups.ToDictionary(item => item.Key, item => (IReadOnlyList<Guid>)item.Value.AsReadOnly());
    }

    private static readonly string[] ReadStatements =
    [
        """SELECT matter_id, tenant_id, matter_type, title, status, jurisdiction, created_at, updated_at FROM casemesh.matters WHERE tenant_id = $1 AND matter_id = $2""",
        """SELECT document_id, document_version_id, original_object_id, content_sha256 FROM casemesh.document_versions WHERE tenant_id = $1 AND matter_id = $2 ORDER BY document_version_id""",
        """SELECT source_span_id, document_version_id, page_number, text_start, text_end, extracted_text, extracted_text_digest, parser_version, extraction_confidence FROM casemesh.source_spans WHERE tenant_id = $1 AND matter_id = $2 ORDER BY source_span_id""",
        """SELECT assertion_id, subject_reference, predicate, value, asserted_by, event_time, asserted_at, source_span_id, origin_class, assertion_class, dispute_state, integrity_state, verification_state, extraction_confidence, created_by_model, superseded_by_assertion_id FROM casemesh.assertions WHERE tenant_id = $1 AND matter_id = $2 ORDER BY assertion_id""",
        """SELECT event_id, participant_id FROM casemesh.matter_event_participants WHERE tenant_id = $1 AND matter_id = $2 ORDER BY event_id, ordinal""",
        """SELECT event_id, event_type, start_time, end_time, label, event_status, verification_state, supersedes_event_id, superseded_by_event_id FROM casemesh.matter_events WHERE tenant_id = $1 AND matter_id = $2 ORDER BY event_id""",
        """SELECT link_id, assertion_id, event_id, relation FROM casemesh.assertion_event_links WHERE tenant_id = $1 AND matter_id = $2 ORDER BY link_id""",
        """SELECT contradiction_id, assertion_a_id, assertion_b_id, contradiction_type, detected_by, resolution_state, resolution_note, created_at, resolved_at FROM casemesh.contradictions WHERE tenant_id = $1 AND matter_id = $2 ORDER BY contradiction_id""",
        """SELECT analysis_node_id, source_span_id FROM casemesh.analysis_node_sources WHERE tenant_id = $1 AND matter_id = $2 ORDER BY analysis_node_id, ordinal""",
        """SELECT analysis_node_id, analysis_type, provider, model, prompt_version, output, generated_at, verification_state, superseded_by_analysis_node_id FROM casemesh.analysis_nodes WHERE tenant_id = $1 AND matter_id = $2 ORDER BY analysis_node_id""",
        """SELECT audit_event_id, audit_kind, entity_type, entity_id, replacement_entity_id, actor, change_summary, occurred_at FROM casemesh.audit_events WHERE tenant_id = $1 AND matter_id = $2 ORDER BY occurred_at, audit_event_id""",
        """SELECT employment_profile_id, assertion_id FROM casemesh.employment_profile_assertions WHERE tenant_id = $1 AND matter_id = $2 ORDER BY employment_profile_id, assertion_id""",
        """SELECT employment_profile_id, employer_reference, role_title, employment_started_on, employment_ended_on, evidence_review_state FROM casemesh.employment_profiles WHERE tenant_id = $1 AND matter_id = $2 ORDER BY employment_profile_id""",
        """SELECT employment_term_id, assertion_id FROM casemesh.employment_term_assertions WHERE tenant_id = $1 AND matter_id = $2 ORDER BY employment_term_id, assertion_id""",
        """SELECT employment_term_id, term_kind, term_value, effective_from, effective_to, supersedes_employment_term_id FROM casemesh.employment_terms WHERE tenant_id = $1 AND matter_id = $2 ORDER BY employment_term_id""",
        """SELECT health_absence_record_id, assertion_id FROM casemesh.health_absence_assertions WHERE tenant_id = $1 AND matter_id = $2 ORDER BY health_absence_record_id, assertion_id""",
        """SELECT health_absence_record_id, event_id FROM casemesh.health_absence_events WHERE tenant_id = $1 AND matter_id = $2 ORDER BY health_absence_record_id, event_id""",
        """SELECT health_absence_record_id, record_kind, neutral_label, evidence_review_state FROM casemesh.health_absence_records WHERE tenant_id = $1 AND matter_id = $2 ORDER BY health_absence_record_id""",
        """SELECT adjustment_request_id, evidence_role, assertion_id FROM casemesh.adjustment_request_assertions WHERE tenant_id = $1 AND matter_id = $2 ORDER BY adjustment_request_id, evidence_role, assertion_id""",
        """SELECT adjustment_request_id, neutral_label, response_status FROM casemesh.adjustment_requests WHERE tenant_id = $1 AND matter_id = $2 ORDER BY adjustment_request_id""",
        """SELECT workplace_process_id, assertion_id FROM casemesh.workplace_process_assertions WHERE tenant_id = $1 AND matter_id = $2 ORDER BY workplace_process_id, assertion_id""",
        """SELECT workplace_process_id, event_id FROM casemesh.workplace_process_events WHERE tenant_id = $1 AND matter_id = $2 ORDER BY workplace_process_id, event_id""",
        """SELECT workplace_process_id, process_kind, stage_label, process_status, supersedes_workplace_process_id, supersession_audit_event_id FROM casemesh.workplace_processes WHERE tenant_id = $1 AND matter_id = $2 ORDER BY workplace_process_id""",
        """SELECT acas_process_state_id, assertion_id FROM casemesh.acas_process_assertions WHERE tenant_id = $1 AND matter_id = $2 ORDER BY acas_process_state_id, assertion_id""",
        """SELECT acas_process_state_id, event_id FROM casemesh.acas_process_events WHERE tenant_id = $1 AND matter_id = $2 ORDER BY acas_process_state_id, event_id""",
        """SELECT acas_process_state_id, acas_stage FROM casemesh.acas_process_states WHERE tenant_id = $1 AND matter_id = $2 ORDER BY acas_process_state_id"""
    ];
}
