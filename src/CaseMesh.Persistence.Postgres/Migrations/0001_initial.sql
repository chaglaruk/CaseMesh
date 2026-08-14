CREATE SCHEMA IF NOT EXISTS casemesh;

CREATE TABLE casemesh.tenants (
    tenant_id uuid PRIMARY KEY,
    display_name text NOT NULL CHECK (btrim(display_name) <> ''),
    created_at timestamptz NOT NULL
);

CREATE TABLE casemesh.matters (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    matter_type text NOT NULL CHECK (btrim(matter_type) <> ''),
    title text NOT NULL CHECK (btrim(title) <> ''),
    status text NOT NULL CHECK (btrim(status) <> ''),
    jurisdiction text NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, matter_id),
    FOREIGN KEY (tenant_id) REFERENCES casemesh.tenants (tenant_id) ON DELETE CASCADE,
    CHECK (updated_at >= created_at)
);

CREATE TABLE casemesh.original_objects (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    original_object_id uuid NOT NULL,
    content_sha256 char(64) NOT NULL CHECK (content_sha256 ~ '^[0-9A-F]{64}$'),
    PRIMARY KEY (tenant_id, matter_id, original_object_id),
    UNIQUE (tenant_id, matter_id, content_sha256),
    UNIQUE (tenant_id, matter_id, original_object_id, content_sha256),
    FOREIGN KEY (tenant_id, matter_id) REFERENCES casemesh.matters (tenant_id, matter_id) ON DELETE CASCADE
);

CREATE TABLE casemesh.documents (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    document_id uuid NOT NULL,
    PRIMARY KEY (tenant_id, matter_id, document_id),
    FOREIGN KEY (tenant_id, matter_id) REFERENCES casemesh.matters (tenant_id, matter_id) ON DELETE CASCADE
);

CREATE TABLE casemesh.document_versions (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    document_id uuid NOT NULL,
    document_version_id uuid NOT NULL,
    original_object_id uuid NOT NULL,
    content_sha256 char(64) NOT NULL CHECK (content_sha256 ~ '^[0-9A-F]{64}$'),
    PRIMARY KEY (tenant_id, matter_id, document_version_id),
    FOREIGN KEY (tenant_id, matter_id, document_id)
        REFERENCES casemesh.documents (tenant_id, matter_id, document_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, original_object_id, content_sha256)
        REFERENCES casemesh.original_objects (tenant_id, matter_id, original_object_id, content_sha256) ON DELETE CASCADE
);

CREATE TABLE casemesh.source_spans (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    source_span_id uuid NOT NULL,
    document_version_id uuid NOT NULL,
    page_number integer NULL CHECK (page_number > 0),
    text_start integer NULL CHECK (text_start >= 0),
    text_end integer NULL CHECK (text_end >= text_start),
    extracted_text text NOT NULL CHECK (btrim(extracted_text) <> ''),
    extracted_text_digest char(64) NOT NULL CHECK (extracted_text_digest ~ '^[0-9A-F]{64}$'),
    parser_version text NOT NULL CHECK (btrim(parser_version) <> ''),
    extraction_confidence numeric NULL CHECK (extraction_confidence BETWEEN 0 AND 1),
    PRIMARY KEY (tenant_id, matter_id, source_span_id),
    FOREIGN KEY (tenant_id, matter_id, document_version_id)
        REFERENCES casemesh.document_versions (tenant_id, matter_id, document_version_id) ON DELETE CASCADE,
    CHECK ((page_number IS NOT NULL) OR (text_start IS NOT NULL AND text_end IS NOT NULL)),
    CHECK ((text_start IS NULL) = (text_end IS NULL))
);

CREATE TABLE casemesh.assertions (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    assertion_id uuid NOT NULL,
    subject_reference text NOT NULL CHECK (btrim(subject_reference) <> ''),
    predicate text NOT NULL CHECK (btrim(predicate) <> ''),
    value text NOT NULL CHECK (btrim(value) <> ''),
    asserted_by text NOT NULL CHECK (btrim(asserted_by) <> ''),
    event_time timestamptz NULL,
    asserted_at timestamptz NOT NULL,
    source_span_id uuid NULL,
    origin_class smallint NOT NULL CHECK (origin_class BETWEEN 0 AND 8),
    assertion_class smallint NOT NULL CHECK (assertion_class BETWEEN 0 AND 7),
    dispute_state smallint NOT NULL CHECK (dispute_state BETWEEN 0 AND 6),
    integrity_state smallint NOT NULL CHECK (integrity_state BETWEEN 0 AND 5),
    verification_state smallint NOT NULL CHECK (verification_state BETWEEN 0 AND 3),
    extraction_confidence numeric NULL CHECK (extraction_confidence BETWEEN 0 AND 1),
    created_by_model text NULL,
    superseded_by_assertion_id uuid NULL,
    PRIMARY KEY (tenant_id, matter_id, assertion_id),
    FOREIGN KEY (tenant_id, matter_id) REFERENCES casemesh.matters (tenant_id, matter_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, source_span_id)
        REFERENCES casemesh.source_spans (tenant_id, matter_id, source_span_id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, matter_id, superseded_by_assertion_id)
        REFERENCES casemesh.assertions (tenant_id, matter_id, assertion_id) DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE casemesh.matter_events (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    event_id uuid NOT NULL,
    event_type text NOT NULL CHECK (btrim(event_type) <> ''),
    start_time timestamptz NULL,
    end_time timestamptz NULL,
    label text NOT NULL CHECK (btrim(label) <> ''),
    event_status smallint NOT NULL CHECK (event_status BETWEEN 0 AND 4),
    verification_state smallint NOT NULL CHECK (verification_state BETWEEN 0 AND 3),
    supersedes_event_id uuid NULL,
    superseded_by_event_id uuid NULL,
    PRIMARY KEY (tenant_id, matter_id, event_id),
    FOREIGN KEY (tenant_id, matter_id) REFERENCES casemesh.matters (tenant_id, matter_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, supersedes_event_id)
        REFERENCES casemesh.matter_events (tenant_id, matter_id, event_id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, matter_id, superseded_by_event_id)
        REFERENCES casemesh.matter_events (tenant_id, matter_id, event_id) DEFERRABLE INITIALLY DEFERRED,
    CHECK (end_time IS NULL OR start_time IS NULL OR end_time >= start_time)
);

CREATE TABLE casemesh.matter_event_participants (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    event_id uuid NOT NULL,
    participant_id uuid NOT NULL,
    ordinal integer NOT NULL CHECK (ordinal >= 0),
    PRIMARY KEY (tenant_id, matter_id, event_id, participant_id),
    UNIQUE (tenant_id, matter_id, event_id, ordinal),
    FOREIGN KEY (tenant_id, matter_id, event_id)
        REFERENCES casemesh.matter_events (tenant_id, matter_id, event_id) ON DELETE CASCADE
);

CREATE TABLE casemesh.assertion_event_links (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    link_id uuid NOT NULL,
    assertion_id uuid NOT NULL,
    event_id uuid NOT NULL,
    relation smallint NOT NULL CHECK (relation BETWEEN 0 AND 4),
    PRIMARY KEY (tenant_id, matter_id, link_id),
    UNIQUE (tenant_id, matter_id, assertion_id, event_id, relation),
    FOREIGN KEY (tenant_id, matter_id, assertion_id)
        REFERENCES casemesh.assertions (tenant_id, matter_id, assertion_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, event_id)
        REFERENCES casemesh.matter_events (tenant_id, matter_id, event_id) ON DELETE CASCADE
);

CREATE TABLE casemesh.contradictions (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    contradiction_id uuid NOT NULL,
    assertion_a_id uuid NOT NULL,
    assertion_b_id uuid NOT NULL,
    contradiction_type smallint NOT NULL CHECK (contradiction_type BETWEEN 0 AND 4),
    detected_by text NOT NULL CHECK (btrim(detected_by) <> ''),
    resolution_state smallint NOT NULL CHECK (resolution_state BETWEEN 0 AND 2),
    resolution_note text NULL,
    created_at timestamptz NOT NULL,
    resolved_at timestamptz NULL,
    PRIMARY KEY (tenant_id, matter_id, contradiction_id),
    FOREIGN KEY (tenant_id, matter_id) REFERENCES casemesh.matters (tenant_id, matter_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, assertion_a_id)
        REFERENCES casemesh.assertions (tenant_id, matter_id, assertion_id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, matter_id, assertion_b_id)
        REFERENCES casemesh.assertions (tenant_id, matter_id, assertion_id) DEFERRABLE INITIALLY DEFERRED,
    CHECK (assertion_a_id <> assertion_b_id),
    CHECK ((resolution_state = 0 AND resolved_at IS NULL) OR (resolution_state <> 0 AND resolved_at IS NOT NULL))
);

CREATE UNIQUE INDEX contradictions_assertion_pair_uq
    ON casemesh.contradictions (
        tenant_id,
        matter_id,
        LEAST(assertion_a_id, assertion_b_id),
        GREATEST(assertion_a_id, assertion_b_id));

CREATE TABLE casemesh.analysis_nodes (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    analysis_node_id uuid NOT NULL,
    analysis_type text NOT NULL CHECK (btrim(analysis_type) <> ''),
    provider text NOT NULL CHECK (btrim(provider) <> ''),
    model text NOT NULL CHECK (btrim(model) <> ''),
    prompt_version text NOT NULL CHECK (btrim(prompt_version) <> ''),
    output text NOT NULL CHECK (btrim(output) <> ''),
    generated_at timestamptz NOT NULL,
    verification_state smallint NOT NULL CHECK (verification_state BETWEEN 0 AND 3),
    superseded_by_analysis_node_id uuid NULL,
    PRIMARY KEY (tenant_id, matter_id, analysis_node_id),
    FOREIGN KEY (tenant_id, matter_id) REFERENCES casemesh.matters (tenant_id, matter_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, superseded_by_analysis_node_id)
        REFERENCES casemesh.analysis_nodes (tenant_id, matter_id, analysis_node_id) DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE casemesh.analysis_node_sources (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    analysis_node_id uuid NOT NULL,
    source_span_id uuid NOT NULL,
    ordinal integer NOT NULL CHECK (ordinal >= 0),
    PRIMARY KEY (tenant_id, matter_id, analysis_node_id, source_span_id),
    UNIQUE (tenant_id, matter_id, analysis_node_id, ordinal),
    FOREIGN KEY (tenant_id, matter_id, analysis_node_id)
        REFERENCES casemesh.analysis_nodes (tenant_id, matter_id, analysis_node_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, source_span_id)
        REFERENCES casemesh.source_spans (tenant_id, matter_id, source_span_id) DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE casemesh.audit_events (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    audit_event_id uuid NOT NULL,
    audit_kind smallint NOT NULL CHECK (audit_kind BETWEEN 0 AND 4),
    entity_type text NOT NULL CHECK (btrim(entity_type) <> ''),
    entity_id uuid NOT NULL,
    replacement_entity_id uuid NULL,
    actor text NOT NULL CHECK (btrim(actor) <> ''),
    change_summary text NOT NULL CHECK (btrim(change_summary) <> ''),
    occurred_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, matter_id, audit_event_id),
    FOREIGN KEY (tenant_id, matter_id) REFERENCES casemesh.matters (tenant_id, matter_id) ON DELETE CASCADE
);

CREATE TABLE casemesh.employment_profiles (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    employment_profile_id uuid NOT NULL,
    employer_reference text NOT NULL CHECK (btrim(employer_reference) <> ''),
    role_title text NOT NULL CHECK (btrim(role_title) <> ''),
    employment_started_on date NULL,
    employment_ended_on date NULL,
    evidence_review_state smallint NOT NULL CHECK (evidence_review_state BETWEEN 0 AND 3),
    PRIMARY KEY (tenant_id, matter_id, employment_profile_id),
    FOREIGN KEY (tenant_id, matter_id) REFERENCES casemesh.matters (tenant_id, matter_id) ON DELETE CASCADE,
    CHECK (employment_ended_on IS NULL OR employment_started_on IS NULL OR employment_ended_on >= employment_started_on)
);

CREATE TABLE casemesh.employment_profile_assertions (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    employment_profile_id uuid NOT NULL,
    assertion_id uuid NOT NULL,
    PRIMARY KEY (tenant_id, matter_id, employment_profile_id, assertion_id),
    FOREIGN KEY (tenant_id, matter_id, employment_profile_id)
        REFERENCES casemesh.employment_profiles (tenant_id, matter_id, employment_profile_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, assertion_id)
        REFERENCES casemesh.assertions (tenant_id, matter_id, assertion_id) DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE casemesh.employment_terms (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    employment_term_id uuid NOT NULL,
    term_kind smallint NOT NULL CHECK (term_kind BETWEEN 0 AND 6),
    term_value text NOT NULL CHECK (btrim(term_value) <> ''),
    effective_from date NULL,
    effective_to date NULL,
    supersedes_employment_term_id uuid NULL,
    PRIMARY KEY (tenant_id, matter_id, employment_term_id),
    FOREIGN KEY (tenant_id, matter_id) REFERENCES casemesh.matters (tenant_id, matter_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, supersedes_employment_term_id)
        REFERENCES casemesh.employment_terms (tenant_id, matter_id, employment_term_id) DEFERRABLE INITIALLY DEFERRED,
    CHECK (effective_to IS NULL OR effective_from IS NULL OR effective_to >= effective_from)
);

CREATE TABLE casemesh.employment_term_assertions (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    employment_term_id uuid NOT NULL,
    assertion_id uuid NOT NULL,
    PRIMARY KEY (tenant_id, matter_id, employment_term_id, assertion_id),
    FOREIGN KEY (tenant_id, matter_id, employment_term_id)
        REFERENCES casemesh.employment_terms (tenant_id, matter_id, employment_term_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, assertion_id)
        REFERENCES casemesh.assertions (tenant_id, matter_id, assertion_id) DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE casemesh.health_absence_records (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    health_absence_record_id uuid NOT NULL,
    record_kind smallint NOT NULL CHECK (record_kind BETWEEN 0 AND 4),
    neutral_label text NOT NULL CHECK (btrim(neutral_label) <> ''),
    evidence_review_state smallint NOT NULL CHECK (evidence_review_state BETWEEN 0 AND 3),
    PRIMARY KEY (tenant_id, matter_id, health_absence_record_id),
    FOREIGN KEY (tenant_id, matter_id) REFERENCES casemesh.matters (tenant_id, matter_id) ON DELETE CASCADE
);

CREATE TABLE casemesh.health_absence_assertions (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    health_absence_record_id uuid NOT NULL,
    assertion_id uuid NOT NULL,
    PRIMARY KEY (tenant_id, matter_id, health_absence_record_id, assertion_id),
    FOREIGN KEY (tenant_id, matter_id, health_absence_record_id)
        REFERENCES casemesh.health_absence_records (tenant_id, matter_id, health_absence_record_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, assertion_id)
        REFERENCES casemesh.assertions (tenant_id, matter_id, assertion_id) DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE casemesh.health_absence_events (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    health_absence_record_id uuid NOT NULL,
    event_id uuid NOT NULL,
    PRIMARY KEY (tenant_id, matter_id, health_absence_record_id, event_id),
    FOREIGN KEY (tenant_id, matter_id, health_absence_record_id)
        REFERENCES casemesh.health_absence_records (tenant_id, matter_id, health_absence_record_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, event_id)
        REFERENCES casemesh.matter_events (tenant_id, matter_id, event_id) DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE casemesh.adjustment_requests (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    adjustment_request_id uuid NOT NULL,
    neutral_label text NOT NULL CHECK (btrim(neutral_label) <> ''),
    response_status smallint NOT NULL CHECK (response_status BETWEEN 0 AND 3),
    PRIMARY KEY (tenant_id, matter_id, adjustment_request_id),
    FOREIGN KEY (tenant_id, matter_id) REFERENCES casemesh.matters (tenant_id, matter_id) ON DELETE CASCADE
);

CREATE TABLE casemesh.adjustment_request_assertions (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    adjustment_request_id uuid NOT NULL,
    evidence_role smallint NOT NULL CHECK (evidence_role BETWEEN 0 AND 2),
    assertion_id uuid NOT NULL,
    PRIMARY KEY (tenant_id, matter_id, adjustment_request_id, evidence_role, assertion_id),
    UNIQUE (tenant_id, matter_id, adjustment_request_id, assertion_id),
    FOREIGN KEY (tenant_id, matter_id, adjustment_request_id)
        REFERENCES casemesh.adjustment_requests (tenant_id, matter_id, adjustment_request_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, assertion_id)
        REFERENCES casemesh.assertions (tenant_id, matter_id, assertion_id) DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE casemesh.workplace_processes (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    workplace_process_id uuid NOT NULL,
    process_kind smallint NOT NULL CHECK (process_kind BETWEEN 0 AND 6),
    stage_label text NOT NULL CHECK (btrim(stage_label) <> ''),
    process_status smallint NOT NULL CHECK (process_status BETWEEN 0 AND 4),
    supersedes_workplace_process_id uuid NULL,
    supersession_audit_event_id uuid NULL,
    PRIMARY KEY (tenant_id, matter_id, workplace_process_id),
    FOREIGN KEY (tenant_id, matter_id) REFERENCES casemesh.matters (tenant_id, matter_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, supersedes_workplace_process_id)
        REFERENCES casemesh.workplace_processes (tenant_id, matter_id, workplace_process_id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, matter_id, supersession_audit_event_id)
        REFERENCES casemesh.audit_events (tenant_id, matter_id, audit_event_id) DEFERRABLE INITIALLY DEFERRED,
    CHECK ((supersedes_workplace_process_id IS NULL) = (supersession_audit_event_id IS NULL))
);

CREATE TABLE casemesh.workplace_process_assertions (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    workplace_process_id uuid NOT NULL,
    assertion_id uuid NOT NULL,
    PRIMARY KEY (tenant_id, matter_id, workplace_process_id, assertion_id),
    FOREIGN KEY (tenant_id, matter_id, workplace_process_id)
        REFERENCES casemesh.workplace_processes (tenant_id, matter_id, workplace_process_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, assertion_id)
        REFERENCES casemesh.assertions (tenant_id, matter_id, assertion_id) DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE casemesh.workplace_process_events (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    workplace_process_id uuid NOT NULL,
    event_id uuid NOT NULL,
    PRIMARY KEY (tenant_id, matter_id, workplace_process_id, event_id),
    FOREIGN KEY (tenant_id, matter_id, workplace_process_id)
        REFERENCES casemesh.workplace_processes (tenant_id, matter_id, workplace_process_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, event_id)
        REFERENCES casemesh.matter_events (tenant_id, matter_id, event_id) DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE casemesh.acas_process_states (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    acas_process_state_id uuid NOT NULL,
    acas_stage smallint NOT NULL CHECK (acas_stage BETWEEN 0 AND 3),
    PRIMARY KEY (tenant_id, matter_id, acas_process_state_id),
    FOREIGN KEY (tenant_id, matter_id) REFERENCES casemesh.matters (tenant_id, matter_id) ON DELETE CASCADE
);

CREATE TABLE casemesh.acas_process_assertions (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    acas_process_state_id uuid NOT NULL,
    assertion_id uuid NOT NULL,
    PRIMARY KEY (tenant_id, matter_id, acas_process_state_id, assertion_id),
    FOREIGN KEY (tenant_id, matter_id, acas_process_state_id)
        REFERENCES casemesh.acas_process_states (tenant_id, matter_id, acas_process_state_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, assertion_id)
        REFERENCES casemesh.assertions (tenant_id, matter_id, assertion_id) DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE casemesh.acas_process_events (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    acas_process_state_id uuid NOT NULL,
    event_id uuid NOT NULL,
    PRIMARY KEY (tenant_id, matter_id, acas_process_state_id, event_id),
    FOREIGN KEY (tenant_id, matter_id, acas_process_state_id)
        REFERENCES casemesh.acas_process_states (tenant_id, matter_id, acas_process_state_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, event_id)
        REFERENCES casemesh.matter_events (tenant_id, matter_id, event_id) DEFERRABLE INITIALLY DEFERRED
);

CREATE INDEX document_versions_document_ix
    ON casemesh.document_versions (tenant_id, matter_id, document_id);
CREATE INDEX document_versions_original_object_ix
    ON casemesh.document_versions (tenant_id, matter_id, original_object_id, content_sha256);
CREATE INDEX source_spans_document_version_ix
    ON casemesh.source_spans (tenant_id, matter_id, document_version_id);
CREATE INDEX assertions_source_span_ix
    ON casemesh.assertions (tenant_id, matter_id, source_span_id);
CREATE INDEX assertions_superseded_by_ix
    ON casemesh.assertions (tenant_id, matter_id, superseded_by_assertion_id);
CREATE INDEX matter_events_supersedes_ix
    ON casemesh.matter_events (tenant_id, matter_id, supersedes_event_id);
CREATE INDEX matter_events_superseded_by_ix
    ON casemesh.matter_events (tenant_id, matter_id, superseded_by_event_id);
CREATE INDEX assertion_event_links_assertion_ix
    ON casemesh.assertion_event_links (tenant_id, matter_id, assertion_id);
CREATE INDEX assertion_event_links_event_ix
    ON casemesh.assertion_event_links (tenant_id, matter_id, event_id);
CREATE INDEX contradictions_assertion_a_ix
    ON casemesh.contradictions (tenant_id, matter_id, assertion_a_id);
CREATE INDEX contradictions_assertion_b_ix
    ON casemesh.contradictions (tenant_id, matter_id, assertion_b_id);
CREATE INDEX analysis_nodes_superseded_by_ix
    ON casemesh.analysis_nodes (tenant_id, matter_id, superseded_by_analysis_node_id);
CREATE INDEX analysis_node_sources_source_span_ix
    ON casemesh.analysis_node_sources (tenant_id, matter_id, source_span_id);
CREATE INDEX employment_profile_assertions_assertion_ix
    ON casemesh.employment_profile_assertions (tenant_id, matter_id, assertion_id);
CREATE INDEX employment_terms_supersedes_ix
    ON casemesh.employment_terms (tenant_id, matter_id, supersedes_employment_term_id);
CREATE INDEX employment_term_assertions_assertion_ix
    ON casemesh.employment_term_assertions (tenant_id, matter_id, assertion_id);
CREATE INDEX health_absence_assertions_assertion_ix
    ON casemesh.health_absence_assertions (tenant_id, matter_id, assertion_id);
CREATE INDEX health_absence_events_event_ix
    ON casemesh.health_absence_events (tenant_id, matter_id, event_id);
CREATE INDEX adjustment_request_assertions_assertion_ix
    ON casemesh.adjustment_request_assertions (tenant_id, matter_id, assertion_id);
CREATE INDEX workplace_processes_supersedes_ix
    ON casemesh.workplace_processes (tenant_id, matter_id, supersedes_workplace_process_id);
CREATE INDEX workplace_processes_audit_ix
    ON casemesh.workplace_processes (tenant_id, matter_id, supersession_audit_event_id);
CREATE INDEX workplace_process_assertions_assertion_ix
    ON casemesh.workplace_process_assertions (tenant_id, matter_id, assertion_id);
CREATE INDEX workplace_process_events_event_ix
    ON casemesh.workplace_process_events (tenant_id, matter_id, event_id);
CREATE INDEX acas_process_assertions_assertion_ix
    ON casemesh.acas_process_assertions (tenant_id, matter_id, assertion_id);
CREATE INDEX acas_process_events_event_ix
    ON casemesh.acas_process_events (tenant_id, matter_id, event_id);

CREATE FUNCTION casemesh.reject_audit_mutation() RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'DELETE' AND NOT EXISTS (
        SELECT 1 FROM casemesh.matters
        WHERE tenant_id = OLD.tenant_id AND matter_id = OLD.matter_id)
    THEN
        RETURN OLD;
    END IF;

    RAISE EXCEPTION 'audit events are append-only';
END;
$$;

CREATE TRIGGER audit_events_append_only
BEFORE UPDATE OR DELETE ON casemesh.audit_events
FOR EACH ROW EXECUTE FUNCTION casemesh.reject_audit_mutation();

CREATE FUNCTION casemesh.reject_audit_truncate() RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'audit events are append-only';
END;
$$;

CREATE TRIGGER audit_events_no_truncate
BEFORE TRUNCATE ON casemesh.audit_events
FOR EACH STATEMENT EXECUTE FUNCTION casemesh.reject_audit_truncate();

DO $$
DECLARE
    table_name text;
BEGIN
    FOREACH table_name IN ARRAY ARRAY[
        'tenants', 'matters', 'original_objects', 'documents', 'document_versions',
        'source_spans', 'assertions', 'matter_events', 'matter_event_participants',
        'assertion_event_links', 'contradictions', 'analysis_nodes', 'analysis_node_sources',
        'audit_events', 'employment_profiles', 'employment_profile_assertions',
        'employment_terms', 'employment_term_assertions', 'health_absence_records',
        'health_absence_assertions', 'health_absence_events', 'adjustment_requests',
        'adjustment_request_assertions', 'workplace_processes', 'workplace_process_assertions',
        'workplace_process_events', 'acas_process_states', 'acas_process_assertions',
        'acas_process_events'
    ]
    LOOP
        EXECUTE format('ALTER TABLE casemesh.%I ENABLE ROW LEVEL SECURITY', table_name);
        EXECUTE format('ALTER TABLE casemesh.%I FORCE ROW LEVEL SECURITY', table_name);
        EXECUTE format(
            'CREATE POLICY tenant_isolation ON casemesh.%I USING (tenant_id = NULLIF(current_setting(''casemesh.tenant_id'', true), '''')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting(''casemesh.tenant_id'', true), '''')::uuid)',
            table_name);
    END LOOP;
END;
$$;
