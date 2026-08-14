using CaseMesh.Core.Snapshots;
using Npgsql;

namespace CaseMesh.Persistence.Postgres;

internal static class PostgresMatterWriter
{
    internal static async Task WriteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MatterEvidenceSnapshot evidence,
        WorkplaceSnapshot workplace,
        CancellationToken cancellationToken)
    {
        var tenantId = evidence.Matter.TenantId.Value;
        var matterId = evidence.Matter.Id;
        var matterWriteCount = await PostgresMatterStore.ExecuteAsync(connection, transaction, """
            INSERT INTO casemesh.matters (
                tenant_id, matter_id, matter_type, title, status, jurisdiction, created_at, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            ON CONFLICT (tenant_id, matter_id) DO UPDATE
            SET matter_type = EXCLUDED.matter_type,
                title = EXCLUDED.title,
                status = EXCLUDED.status,
                jurisdiction = EXCLUDED.jurisdiction,
                updated_at = EXCLUDED.updated_at
            WHERE casemesh.matters.created_at = EXCLUDED.created_at
              AND (
                  casemesh.matters.updated_at < EXCLUDED.updated_at
                  OR (
                      casemesh.matters.updated_at = EXCLUDED.updated_at
                      AND casemesh.matters.matter_type = EXCLUDED.matter_type
                      AND casemesh.matters.title = EXCLUDED.title
                      AND casemesh.matters.status = EXCLUDED.status
                      AND casemesh.matters.jurisdiction IS NOT DISTINCT FROM EXCLUDED.jurisdiction));
            """, cancellationToken,
            tenantId, matterId, evidence.Matter.MatterType, evidence.Matter.Title, evidence.Matter.Status,
            evidence.Matter.Jurisdiction, evidence.Matter.CreatedAt, evidence.Matter.UpdatedAt);
        if (matterWriteCount != 1)
        {
            throw new InvalidOperationException(
                "A persisted Matter cannot change its creation time or move UpdatedAt backwards.");
        }

        foreach (var original in evidence.DocumentVersions
                     .GroupBy(item => new { item.OriginalObjectId, item.ContentSha256 })
                     .Select(group => group.Key))
        {
            var originalWriteCount = await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                INSERT INTO casemesh.original_objects (
                    tenant_id, matter_id, original_object_id, content_sha256)
                VALUES ($1, $2, $3, $4)
                ON CONFLICT (tenant_id, matter_id, original_object_id) DO UPDATE
                SET original_object_id = EXCLUDED.original_object_id
                WHERE casemesh.original_objects.content_sha256 = EXCLUDED.content_sha256;
                """, cancellationToken, tenantId, matterId, original.OriginalObjectId, original.ContentSha256);
            if (originalWriteCount != 1)
            {
                throw new InvalidOperationException(
                    "An original-object id cannot be reused for a different content hash.");
            }
        }

        foreach (var documentId in evidence.DocumentVersions.Select(item => item.DocumentId).Distinct())
        {
            await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                INSERT INTO casemesh.documents (tenant_id, matter_id, document_id)
                VALUES ($1, $2, $3)
                ON CONFLICT DO NOTHING;
                """, cancellationToken, tenantId, matterId, documentId);
        }

        foreach (var version in evidence.DocumentVersions)
        {
            var versionWriteCount = await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                INSERT INTO casemesh.document_versions (
                    tenant_id, matter_id, document_id, document_version_id, original_object_id, content_sha256)
                VALUES ($1, $2, $3, $4, $5, $6)
                ON CONFLICT (tenant_id, matter_id, document_version_id) DO UPDATE
                SET document_version_id = EXCLUDED.document_version_id
                WHERE casemesh.document_versions.document_id = EXCLUDED.document_id
                  AND casemesh.document_versions.original_object_id = EXCLUDED.original_object_id
                  AND casemesh.document_versions.content_sha256 = EXCLUDED.content_sha256;
                """, cancellationToken,
                tenantId, matterId, version.DocumentId, version.DocumentVersionId,
                version.OriginalObjectId, version.ContentSha256);
            if (versionWriteCount != 1)
            {
                throw new InvalidOperationException(
                    "A document-version id cannot be reused for different immutable provenance.");
            }
        }

        foreach (var span in evidence.SourceSpans)
        {
            var spanWriteCount = await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                INSERT INTO casemesh.source_spans (
                    tenant_id, matter_id, source_span_id, document_version_id, page_number,
                    text_start, text_end, extracted_text, extracted_text_digest, parser_version,
                    extraction_confidence)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
                ON CONFLICT (tenant_id, matter_id, source_span_id) DO UPDATE
                SET source_span_id = EXCLUDED.source_span_id
                WHERE casemesh.source_spans.document_version_id = EXCLUDED.document_version_id
                  AND casemesh.source_spans.page_number IS NOT DISTINCT FROM EXCLUDED.page_number
                  AND casemesh.source_spans.text_start IS NOT DISTINCT FROM EXCLUDED.text_start
                  AND casemesh.source_spans.text_end IS NOT DISTINCT FROM EXCLUDED.text_end
                  AND casemesh.source_spans.extracted_text = EXCLUDED.extracted_text
                  AND casemesh.source_spans.extracted_text_digest = EXCLUDED.extracted_text_digest
                  AND casemesh.source_spans.parser_version = EXCLUDED.parser_version
                  AND casemesh.source_spans.extraction_confidence IS NOT DISTINCT FROM EXCLUDED.extraction_confidence;
                """, cancellationToken,
                tenantId, matterId, span.Id, span.DocumentVersionId, span.PageNumber,
                span.TextStart, span.TextEnd, span.ExtractedText, span.ExtractedTextDigest,
                span.ParserVersion, span.ExtractionConfidence);
            if (spanWriteCount != 1)
            {
                throw new InvalidOperationException(
                    "A source span id cannot be reused for different immutable provenance.");
            }
        }

        foreach (var assertion in evidence.Assertions)
        {
            var assertionWriteCount = await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                INSERT INTO casemesh.assertions (
                    tenant_id, matter_id, assertion_id, subject_reference, predicate, value,
                    asserted_by, event_time, asserted_at, source_span_id, origin_class,
                    assertion_class, dispute_state, integrity_state, verification_state,
                    extraction_confidence, created_by_model, superseded_by_assertion_id)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18)
                ON CONFLICT (tenant_id, matter_id, assertion_id) DO UPDATE
                SET dispute_state = EXCLUDED.dispute_state,
                    verification_state = EXCLUDED.verification_state,
                    superseded_by_assertion_id = EXCLUDED.superseded_by_assertion_id
                WHERE casemesh.assertions.subject_reference = EXCLUDED.subject_reference
                  AND casemesh.assertions.predicate = EXCLUDED.predicate
                  AND casemesh.assertions.value = EXCLUDED.value
                  AND casemesh.assertions.asserted_by = EXCLUDED.asserted_by
                  AND casemesh.assertions.event_time IS NOT DISTINCT FROM EXCLUDED.event_time
                  AND casemesh.assertions.asserted_at = EXCLUDED.asserted_at
                  AND casemesh.assertions.source_span_id IS NOT DISTINCT FROM EXCLUDED.source_span_id
                  AND casemesh.assertions.origin_class = EXCLUDED.origin_class
                  AND casemesh.assertions.assertion_class = EXCLUDED.assertion_class
                  AND casemesh.assertions.integrity_state = EXCLUDED.integrity_state
                  AND casemesh.assertions.extraction_confidence IS NOT DISTINCT FROM EXCLUDED.extraction_confidence
                  AND casemesh.assertions.created_by_model IS NOT DISTINCT FROM EXCLUDED.created_by_model
                  AND (casemesh.assertions.superseded_by_assertion_id IS NULL
                       OR casemesh.assertions.superseded_by_assertion_id = EXCLUDED.superseded_by_assertion_id);
                """, cancellationToken,
                tenantId, matterId, assertion.Id, assertion.SubjectReference, assertion.Predicate,
                assertion.Value, assertion.AssertedBy, assertion.EventTime, assertion.AssertedAt,
                assertion.SourceSpanId, (short)assertion.OriginClass, (short)assertion.AssertionClass,
                (short)assertion.DisputeState, (short)assertion.IntegrityState,
                (short)assertion.VerificationState, assertion.ExtractionConfidence, assertion.CreatedByModel,
                assertion.SupersededByAssertionId);
            if (assertionWriteCount != 1)
            {
                throw new InvalidOperationException(
                    "An assertion id cannot overwrite immutable evidence or reverse supersession.");
            }
        }

        foreach (var matterEvent in evidence.Events)
        {
            var eventWriteCount = await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                INSERT INTO casemesh.matter_events (
                    tenant_id, matter_id, event_id, event_type, start_time, end_time, label,
                    event_status, verification_state, supersedes_event_id, superseded_by_event_id)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
                ON CONFLICT (tenant_id, matter_id, event_id) DO UPDATE
                SET event_status = EXCLUDED.event_status,
                    verification_state = EXCLUDED.verification_state,
                    supersedes_event_id = EXCLUDED.supersedes_event_id,
                    superseded_by_event_id = EXCLUDED.superseded_by_event_id
                WHERE casemesh.matter_events.event_type = EXCLUDED.event_type
                  AND casemesh.matter_events.start_time IS NOT DISTINCT FROM EXCLUDED.start_time
                  AND casemesh.matter_events.end_time IS NOT DISTINCT FROM EXCLUDED.end_time
                  AND casemesh.matter_events.label = EXCLUDED.label
                  AND (casemesh.matter_events.supersedes_event_id IS NULL
                       OR casemesh.matter_events.supersedes_event_id = EXCLUDED.supersedes_event_id)
                  AND (casemesh.matter_events.superseded_by_event_id IS NULL
                       OR casemesh.matter_events.superseded_by_event_id = EXCLUDED.superseded_by_event_id);
                """, cancellationToken,
                tenantId, matterId, matterEvent.Id, matterEvent.EventType, matterEvent.StartTime,
                matterEvent.EndTime, matterEvent.Label, (short)matterEvent.Status,
                (short)matterEvent.VerificationState, matterEvent.SupersedesEventId,
                matterEvent.SupersededByEventId);
            if (eventWriteCount != 1)
            {
                throw new InvalidOperationException(
                    "An event id cannot overwrite immutable history or reverse supersession.");
            }

            for (var ordinal = 0; ordinal < matterEvent.ParticipantIds.Count; ordinal++)
            {
                await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                    INSERT INTO casemesh.matter_event_participants (
                        tenant_id, matter_id, event_id, participant_id, ordinal)
                    VALUES ($1, $2, $3, $4, $5)
                    ON CONFLICT DO NOTHING;
                    """, cancellationToken,
                    tenantId, matterId, matterEvent.Id, matterEvent.ParticipantIds[ordinal], ordinal);
            }
        }

        foreach (var link in evidence.AssertionEventLinks)
        {
            await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                INSERT INTO casemesh.assertion_event_links (
                    tenant_id, matter_id, link_id, assertion_id, event_id, relation)
                VALUES ($1, $2, $3, $4, $5, $6)
                ON CONFLICT DO NOTHING;
                """, cancellationToken,
                tenantId, matterId, link.Id, link.AssertionId, link.EventId, (short)link.Relation);
        }

        foreach (var contradiction in evidence.Contradictions)
        {
            var contradictionWriteCount = await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                INSERT INTO casemesh.contradictions (
                    tenant_id, matter_id, contradiction_id, assertion_a_id, assertion_b_id,
                    contradiction_type, detected_by, resolution_state, resolution_note, created_at, resolved_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
                ON CONFLICT (tenant_id, matter_id, contradiction_id) DO UPDATE
                SET resolution_state = EXCLUDED.resolution_state,
                    resolution_note = EXCLUDED.resolution_note,
                    resolved_at = EXCLUDED.resolved_at
                WHERE casemesh.contradictions.assertion_a_id = EXCLUDED.assertion_a_id
                  AND casemesh.contradictions.assertion_b_id = EXCLUDED.assertion_b_id
                  AND casemesh.contradictions.contradiction_type = EXCLUDED.contradiction_type
                  AND casemesh.contradictions.detected_by = EXCLUDED.detected_by
                  AND casemesh.contradictions.created_at = EXCLUDED.created_at
                  AND (
                      (casemesh.contradictions.resolution_state = 0 AND EXCLUDED.resolution_state <> 0)
                      OR (
                          casemesh.contradictions.resolution_state = EXCLUDED.resolution_state
                          AND casemesh.contradictions.resolution_note IS NOT DISTINCT FROM EXCLUDED.resolution_note
                          AND casemesh.contradictions.resolved_at IS NOT DISTINCT FROM EXCLUDED.resolved_at));
                """, cancellationToken,
                tenantId, matterId, contradiction.Id, contradiction.AssertionAId,
                contradiction.AssertionBId, (short)contradiction.Type, contradiction.DetectedBy,
                (short)contradiction.ResolutionState, contradiction.ResolutionNote,
                contradiction.CreatedAt, contradiction.ResolvedAt);
            if (contradictionWriteCount != 1)
            {
                throw new InvalidOperationException(
                    "A contradiction id cannot overwrite its immutable evidence or reverse a resolution.");
            }
        }

        foreach (var node in evidence.AnalysisNodes)
        {
            await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                INSERT INTO casemesh.analysis_nodes (
                    tenant_id, matter_id, analysis_node_id, analysis_type, provider, model,
                    prompt_version, output, generated_at, verification_state, superseded_by_analysis_node_id)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
                ON CONFLICT DO NOTHING;
                """, cancellationToken,
                tenantId, matterId, node.Id, node.AnalysisType, node.Provider, node.Model,
                node.PromptVersion, node.Output, node.GeneratedAt, (short)node.VerificationState,
                node.SupersededByAnalysisNodeId);

            for (var ordinal = 0; ordinal < node.SourceSpanIds.Count; ordinal++)
            {
                await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                    INSERT INTO casemesh.analysis_node_sources (
                        tenant_id, matter_id, analysis_node_id, source_span_id, ordinal)
                    VALUES ($1, $2, $3, $4, $5)
                    ON CONFLICT DO NOTHING;
                    """, cancellationToken,
                    tenantId, matterId, node.Id, node.SourceSpanIds[ordinal], ordinal);
            }
        }

        foreach (var auditEvent in evidence.AuditEvents)
        {
            await EnsureAuditEventAsync(connection, transaction, tenantId, matterId, auditEvent, cancellationToken);
        }

        await WriteWorkplaceAsync(connection, transaction, tenantId, matterId, workplace, cancellationToken);
    }

    private static async Task WriteWorkplaceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid tenantId,
        Guid matterId,
        WorkplaceSnapshot workplace,
        CancellationToken cancellationToken)
    {
        foreach (var profile in workplace.EmploymentProfiles)
        {
            await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                INSERT INTO casemesh.employment_profiles (
                    tenant_id, matter_id, employment_profile_id, employer_reference, role_title,
                    employment_started_on, employment_ended_on, evidence_review_state)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
                ON CONFLICT DO NOTHING;
                """, cancellationToken,
                tenantId, matterId, profile.Id, profile.EmployerReference, profile.RoleTitle,
                profile.EmploymentStartedOn, profile.EmploymentEndedOn, (short)profile.EvidenceReviewState);
            await WriteAssertionLinksAsync(connection, transaction, "employment_profile_assertions",
                "employment_profile_id", tenantId, matterId, profile.Id, profile.SupportingAssertionIds, cancellationToken);
        }

        foreach (var term in workplace.EmploymentTerms)
        {
            await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                INSERT INTO casemesh.employment_terms (
                    tenant_id, matter_id, employment_term_id, term_kind, term_value,
                    effective_from, effective_to, supersedes_employment_term_id)
                VALUES ($1, $2, $3, $4, $5, $6, $7, NULL)
                ON CONFLICT DO NOTHING;
                """, cancellationToken,
                tenantId, matterId, term.Id, (short)term.Kind, term.Value, term.EffectiveFrom, term.EffectiveTo);
            await WriteAssertionLinksAsync(connection, transaction, "employment_term_assertions",
                "employment_term_id", tenantId, matterId, term.Id, term.SupportingAssertionIds, cancellationToken);
        }

        foreach (var term in workplace.EmploymentTerms.Where(item => item.SupersedesEmploymentTermId.HasValue))
        {
            await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                UPDATE casemesh.employment_terms
                SET supersedes_employment_term_id = $4
                WHERE tenant_id = $1 AND matter_id = $2 AND employment_term_id = $3;
                """, cancellationToken, tenantId, matterId, term.Id, term.SupersedesEmploymentTermId);
        }

        foreach (var record in workplace.HealthAbsenceRecords)
        {
            await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                INSERT INTO casemesh.health_absence_records (
                    tenant_id, matter_id, health_absence_record_id, record_kind, neutral_label, evidence_review_state)
                VALUES ($1, $2, $3, $4, $5, $6)
                ON CONFLICT DO NOTHING;
                """, cancellationToken,
                tenantId, matterId, record.Id, (short)record.Kind, record.NeutralLabel,
                (short)record.EvidenceReviewState);
            await WriteAssertionLinksAsync(connection, transaction, "health_absence_assertions",
                "health_absence_record_id", tenantId, matterId, record.Id, record.AssertionIds, cancellationToken);
            await WriteEventLinksAsync(connection, transaction, "health_absence_events",
                "health_absence_record_id", tenantId, matterId, record.Id, record.EventIds, cancellationToken);
        }

        foreach (var request in workplace.AdjustmentRequests)
        {
            await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                INSERT INTO casemesh.adjustment_requests (
                    tenant_id, matter_id, adjustment_request_id, neutral_label, response_status)
                VALUES ($1, $2, $3, $4, $5)
                ON CONFLICT DO NOTHING;
                """, cancellationToken,
                tenantId, matterId, request.Id, request.NeutralLabel, (short)request.ResponseStatus);
            await WriteAdjustmentEvidenceAsync(connection, transaction, tenantId, matterId, request.Id,
                0, request.RequestAssertionIds, cancellationToken);
            await WriteAdjustmentEvidenceAsync(connection, transaction, tenantId, matterId, request.Id,
                1, request.ResponseAssertionIds, cancellationToken);
            await WriteAdjustmentEvidenceAsync(connection, transaction, tenantId, matterId, request.Id,
                2, request.ImplementationAssertionIds, cancellationToken);
        }

        foreach (var process in workplace.WorkplaceProcesses)
        {
            await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                INSERT INTO casemesh.workplace_processes (
                    tenant_id, matter_id, workplace_process_id, process_kind, stage_label,
                    process_status, supersedes_workplace_process_id, supersession_audit_event_id)
                VALUES ($1, $2, $3, $4, $5, $6, NULL, NULL)
                ON CONFLICT DO NOTHING;
                """, cancellationToken,
                tenantId, matterId, process.Id, (short)process.Kind, process.StageLabel, (short)process.Status);
            await WriteAssertionLinksAsync(connection, transaction, "workplace_process_assertions",
                "workplace_process_id", tenantId, matterId, process.Id, process.AssertionIds, cancellationToken);
            await WriteEventLinksAsync(connection, transaction, "workplace_process_events",
                "workplace_process_id", tenantId, matterId, process.Id, process.EventIds, cancellationToken);
        }

        foreach (var process in workplace.WorkplaceProcesses.Where(item => item.SupersedesWorkplaceProcessId.HasValue))
        {
            await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                UPDATE casemesh.workplace_processes
                SET supersedes_workplace_process_id = $4, supersession_audit_event_id = $5
                WHERE tenant_id = $1 AND matter_id = $2 AND workplace_process_id = $3;
                """, cancellationToken,
                tenantId, matterId, process.Id, process.SupersedesWorkplaceProcessId, process.SupersessionAuditEventId);
        }

        foreach (var state in workplace.AcasProcessStates)
        {
            await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                INSERT INTO casemesh.acas_process_states (
                    tenant_id, matter_id, acas_process_state_id, acas_stage)
                VALUES ($1, $2, $3, $4)
                ON CONFLICT DO NOTHING;
                """, cancellationToken, tenantId, matterId, state.Id, (short)state.Stage);
            await WriteAssertionLinksAsync(connection, transaction, "acas_process_assertions",
                "acas_process_state_id", tenantId, matterId, state.Id, state.AssertionIds, cancellationToken);
            await WriteEventLinksAsync(connection, transaction, "acas_process_events",
                "acas_process_state_id", tenantId, matterId, state.Id, state.EventIds, cancellationToken);
        }
    }

    private static async Task EnsureAuditEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid tenantId,
        Guid matterId,
        AuditEventSnapshot auditEvent,
        CancellationToken cancellationToken)
    {
        var inserted = await PostgresMatterStore.ExecuteAsync(connection, transaction, """
            INSERT INTO casemesh.audit_events (
                tenant_id, matter_id, audit_event_id, audit_kind, entity_type, entity_id,
                replacement_entity_id, actor, change_summary, occurred_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
            ON CONFLICT DO NOTHING;
            """, cancellationToken,
            tenantId, matterId, auditEvent.Id, (short)auditEvent.Kind, auditEvent.EntityType,
            auditEvent.EntityId, auditEvent.ReplacementEntityId, auditEvent.Actor,
            auditEvent.ChangeSummary, auditEvent.OccurredAt);
        if (inserted == 1)
        {
            return;
        }

        await using var command = new NpgsqlCommand("""
            SELECT COUNT(*)
            FROM casemesh.audit_events
            WHERE tenant_id = $1 AND matter_id = $2 AND audit_event_id = $3
              AND audit_kind = $4 AND entity_type = $5 AND entity_id = $6
              AND replacement_entity_id IS NOT DISTINCT FROM $7
              AND actor = $8 AND change_summary = $9 AND occurred_at = $10;
            """, connection, transaction);
        PostgresMatterStore.AddParameters(command,
            tenantId, matterId, auditEvent.Id, (short)auditEvent.Kind, auditEvent.EntityType,
            auditEvent.EntityId, auditEvent.ReplacementEntityId, auditEvent.Actor,
            auditEvent.ChangeSummary, auditEvent.OccurredAt);
        var count = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
        if (count != 1)
        {
            throw new InvalidOperationException("An audit event id cannot be overwritten with different history.");
        }
    }

    private static Task WriteAssertionLinksAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string table,
        string ownerColumn,
        Guid tenantId,
        Guid matterId,
        Guid ownerId,
        IReadOnlyList<Guid> assertionIds,
        CancellationToken cancellationToken) =>
        WriteLinksAsync(connection, transaction, table, ownerColumn, "assertion_id",
            tenantId, matterId, ownerId, assertionIds, cancellationToken);

    private static Task WriteEventLinksAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string table,
        string ownerColumn,
        Guid tenantId,
        Guid matterId,
        Guid ownerId,
        IReadOnlyList<Guid> eventIds,
        CancellationToken cancellationToken) =>
        WriteLinksAsync(connection, transaction, table, ownerColumn, "event_id",
            tenantId, matterId, ownerId, eventIds, cancellationToken);

    private static async Task WriteLinksAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string table,
        string ownerColumn,
        string targetColumn,
        Guid tenantId,
        Guid matterId,
        Guid ownerId,
        IReadOnlyList<Guid> targetIds,
        CancellationToken cancellationToken)
    {
        foreach (var targetId in targetIds)
        {
            await PostgresMatterStore.ExecuteAsync(connection, transaction, $"""
                INSERT INTO casemesh.{table} (tenant_id, matter_id, {ownerColumn}, {targetColumn})
                VALUES ($1, $2, $3, $4)
                ON CONFLICT DO NOTHING;
                """, cancellationToken, tenantId, matterId, ownerId, targetId);
        }
    }

    private static async Task WriteAdjustmentEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid tenantId,
        Guid matterId,
        Guid requestId,
        short role,
        IReadOnlyList<Guid> assertionIds,
        CancellationToken cancellationToken)
    {
        foreach (var assertionId in assertionIds)
        {
            await PostgresMatterStore.ExecuteAsync(connection, transaction, """
                INSERT INTO casemesh.adjustment_request_assertions (
                    tenant_id, matter_id, adjustment_request_id, evidence_role, assertion_id)
                VALUES ($1, $2, $3, $4, $5)
                ON CONFLICT DO NOTHING;
                """, cancellationToken, tenantId, matterId, requestId, role, assertionId);
        }
    }
}
