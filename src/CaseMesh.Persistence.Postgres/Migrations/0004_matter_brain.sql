CREATE TABLE casemesh.people (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    person_id uuid NOT NULL,
    display_name text NOT NULL CHECK (btrim(display_name) <> ''),
    PRIMARY KEY (tenant_id, matter_id, person_id),
    FOREIGN KEY (tenant_id, matter_id)
        REFERENCES casemesh.matters (tenant_id, matter_id) ON DELETE CASCADE
);

CREATE TABLE casemesh.person_roles (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    person_id uuid NOT NULL,
    ordinal integer NOT NULL CHECK (ordinal >= 0),
    role_label text NOT NULL CHECK (btrim(role_label) <> ''),
    PRIMARY KEY (tenant_id, matter_id, person_id, ordinal),
    UNIQUE (tenant_id, matter_id, person_id, role_label),
    FOREIGN KEY (tenant_id, matter_id, person_id)
        REFERENCES casemesh.people (tenant_id, matter_id, person_id) ON DELETE CASCADE
);

CREATE TABLE casemesh.organisations (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    organisation_id uuid NOT NULL,
    name text NOT NULL CHECK (btrim(name) <> ''),
    type_label text NOT NULL CHECK (btrim(type_label) <> ''),
    PRIMARY KEY (tenant_id, matter_id, organisation_id),
    FOREIGN KEY (tenant_id, matter_id)
        REFERENCES casemesh.matters (tenant_id, matter_id) ON DELETE CASCADE
);

CREATE TABLE casemesh.entity_aliases (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    alias_id uuid NOT NULL,
    entity_kind smallint NOT NULL CHECK (entity_kind BETWEEN 0 AND 1),
    person_id uuid NULL,
    organisation_id uuid NULL,
    alias_value text NOT NULL CHECK (btrim(alias_value) <> ''),
    normalized_value text NOT NULL CHECK (btrim(normalized_value) <> ''),
    source_span_id uuid NULL,
    PRIMARY KEY (tenant_id, matter_id, alias_id),
    FOREIGN KEY (tenant_id, matter_id, person_id)
        REFERENCES casemesh.people (tenant_id, matter_id, person_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, organisation_id)
        REFERENCES casemesh.organisations (tenant_id, matter_id, organisation_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, source_span_id)
        REFERENCES casemesh.source_spans (tenant_id, matter_id, source_span_id) DEFERRABLE INITIALLY DEFERRED,
    CHECK ((entity_kind = 0 AND person_id IS NOT NULL AND organisation_id IS NULL)
        OR (entity_kind = 1 AND organisation_id IS NOT NULL AND person_id IS NULL))
);

CREATE UNIQUE INDEX entity_aliases_person_value_uq
    ON casemesh.entity_aliases (tenant_id, matter_id, person_id, normalized_value)
    WHERE entity_kind = 0;
CREATE UNIQUE INDEX entity_aliases_organisation_value_uq
    ON casemesh.entity_aliases (tenant_id, matter_id, organisation_id, normalized_value)
    WHERE entity_kind = 1;

CREATE TABLE casemesh.communications (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    communication_id uuid NOT NULL,
    communication_kind smallint NOT NULL CHECK (communication_kind BETWEEN 0 AND 5),
    neutral_label text NOT NULL CHECK (btrim(neutral_label) <> ''),
    occurred_at timestamptz NULL,
    sender_person_id uuid NULL,
    sender_organisation_id uuid NULL,
    verification_state smallint NOT NULL CHECK (verification_state BETWEEN 0 AND 3),
    PRIMARY KEY (tenant_id, matter_id, communication_id),
    FOREIGN KEY (tenant_id, matter_id)
        REFERENCES casemesh.matters (tenant_id, matter_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, sender_person_id)
        REFERENCES casemesh.people (tenant_id, matter_id, person_id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, matter_id, sender_organisation_id)
        REFERENCES casemesh.organisations (tenant_id, matter_id, organisation_id) DEFERRABLE INITIALLY DEFERRED,
    CHECK (num_nonnulls(sender_person_id, sender_organisation_id) <= 1)
);

CREATE TABLE casemesh.communication_participants (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    communication_id uuid NOT NULL,
    participant_kind smallint NOT NULL CHECK (participant_kind BETWEEN 0 AND 1),
    person_id uuid NULL,
    organisation_id uuid NULL,
    ordinal integer NOT NULL CHECK (ordinal >= 0),
    PRIMARY KEY (tenant_id, matter_id, communication_id, ordinal),
    FOREIGN KEY (tenant_id, matter_id, communication_id)
        REFERENCES casemesh.communications (tenant_id, matter_id, communication_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, person_id)
        REFERENCES casemesh.people (tenant_id, matter_id, person_id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, matter_id, organisation_id)
        REFERENCES casemesh.organisations (tenant_id, matter_id, organisation_id) DEFERRABLE INITIALLY DEFERRED,
    CHECK ((participant_kind = 0 AND person_id IS NOT NULL AND organisation_id IS NULL)
        OR (participant_kind = 1 AND organisation_id IS NOT NULL AND person_id IS NULL))
);

CREATE UNIQUE INDEX communication_participants_person_uq
    ON casemesh.communication_participants (tenant_id, matter_id, communication_id, person_id)
    WHERE participant_kind = 0;
CREATE UNIQUE INDEX communication_participants_organisation_uq
    ON casemesh.communication_participants (tenant_id, matter_id, communication_id, organisation_id)
    WHERE participant_kind = 1;

CREATE TABLE casemesh.communication_sources (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    communication_id uuid NOT NULL,
    source_span_id uuid NOT NULL,
    ordinal integer NOT NULL CHECK (ordinal >= 0),
    PRIMARY KEY (tenant_id, matter_id, communication_id, source_span_id),
    UNIQUE (tenant_id, matter_id, communication_id, ordinal),
    FOREIGN KEY (tenant_id, matter_id, communication_id)
        REFERENCES casemesh.communications (tenant_id, matter_id, communication_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, source_span_id)
        REFERENCES casemesh.source_spans (tenant_id, matter_id, source_span_id) DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE casemesh.extraction_runs (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    extraction_run_id uuid NOT NULL,
    fingerprint char(64) NOT NULL CHECK (fingerprint ~ '^[0-9A-F]{64}$'),
    provider text NOT NULL CHECK (btrim(provider) <> ''),
    model text NOT NULL CHECK (btrim(model) <> ''),
    extraction_version text NOT NULL CHECK (btrim(extraction_version) <> ''),
    prompt_version text NOT NULL CHECK (btrim(prompt_version) <> ''),
    schema_version text NOT NULL CHECK (btrim(schema_version) <> ''),
    generated_at timestamptz NOT NULL,
    raw_result_digest char(64) NOT NULL CHECK (raw_result_digest ~ '^[0-9A-F]{64}$'),
    PRIMARY KEY (tenant_id, matter_id, extraction_run_id),
    UNIQUE (tenant_id, matter_id, fingerprint),
    FOREIGN KEY (tenant_id, matter_id)
        REFERENCES casemesh.matters (tenant_id, matter_id) ON DELETE CASCADE
);

CREATE TABLE casemesh.extraction_run_sources (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    extraction_run_id uuid NOT NULL,
    source_span_id uuid NOT NULL,
    ordinal integer NOT NULL CHECK (ordinal >= 0),
    PRIMARY KEY (tenant_id, matter_id, extraction_run_id, source_span_id),
    UNIQUE (tenant_id, matter_id, extraction_run_id, ordinal),
    FOREIGN KEY (tenant_id, matter_id, extraction_run_id)
        REFERENCES casemesh.extraction_runs (tenant_id, matter_id, extraction_run_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, source_span_id)
        REFERENCES casemesh.source_spans (tenant_id, matter_id, source_span_id) DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE casemesh.extraction_candidates (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    candidate_id uuid NOT NULL,
    extraction_run_id uuid NOT NULL,
    external_key text NOT NULL CHECK (btrim(external_key) <> ''),
    candidate_kind smallint NOT NULL CHECK (candidate_kind BETWEEN 0 AND 7),
    disposition smallint NOT NULL CHECK (disposition BETWEEN 0 AND 1),
    rejection_code text NULL CHECK (rejection_code IS NULL OR btrim(rejection_code) <> ''),
    extraction_confidence numeric NULL CHECK (extraction_confidence BETWEEN 0 AND 1),
    canonical_kind smallint NULL CHECK (canonical_kind BETWEEN 0 AND 6),
    person_id uuid NULL,
    organisation_id uuid NULL,
    communication_id uuid NULL,
    assertion_id uuid NULL,
    event_id uuid NULL,
    assertion_event_link_id uuid NULL,
    contradiction_id uuid NULL,
    payload_json jsonb NOT NULL CHECK (octet_length(payload_json::text) <= 1000000),
    payload_digest char(64) NOT NULL CHECK (payload_digest ~ '^[0-9A-F]{64}$'),
    PRIMARY KEY (tenant_id, matter_id, candidate_id),
    UNIQUE (tenant_id, matter_id, candidate_id, extraction_run_id),
    UNIQUE (tenant_id, matter_id, extraction_run_id, external_key),
    FOREIGN KEY (tenant_id, matter_id, extraction_run_id)
        REFERENCES casemesh.extraction_runs (tenant_id, matter_id, extraction_run_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, person_id)
        REFERENCES casemesh.people (tenant_id, matter_id, person_id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, matter_id, organisation_id)
        REFERENCES casemesh.organisations (tenant_id, matter_id, organisation_id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, matter_id, communication_id)
        REFERENCES casemesh.communications (tenant_id, matter_id, communication_id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, matter_id, assertion_id)
        REFERENCES casemesh.assertions (tenant_id, matter_id, assertion_id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, matter_id, event_id)
        REFERENCES casemesh.matter_events (tenant_id, matter_id, event_id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, matter_id, assertion_event_link_id)
        REFERENCES casemesh.assertion_event_links (tenant_id, matter_id, link_id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, matter_id, contradiction_id)
        REFERENCES casemesh.contradictions (tenant_id, matter_id, contradiction_id) DEFERRABLE INITIALLY DEFERRED,
    CHECK (
        (disposition = 1 AND rejection_code IS NOT NULL AND canonical_kind IS NULL
            AND num_nonnulls(person_id, organisation_id, communication_id, assertion_id, event_id,
                assertion_event_link_id, contradiction_id) = 0)
        OR
        (disposition = 0 AND rejection_code IS NULL AND candidate_kind = 6 AND canonical_kind IS NULL
            AND num_nonnulls(person_id, organisation_id, communication_id, assertion_id, event_id,
                assertion_event_link_id, contradiction_id) = 0)
        OR
        (disposition = 0 AND rejection_code IS NULL
            AND num_nonnulls(person_id, organisation_id, communication_id, assertion_id, event_id,
                assertion_event_link_id, contradiction_id) = 1
            AND ((candidate_kind BETWEEN 0 AND 5 AND canonical_kind = candidate_kind)
              OR (candidate_kind = 7 AND canonical_kind = 6))
            AND ((canonical_kind = 0 AND person_id IS NOT NULL)
              OR (canonical_kind = 1 AND organisation_id IS NOT NULL)
              OR (canonical_kind = 2 AND communication_id IS NOT NULL)
              OR (canonical_kind = 3 AND assertion_id IS NOT NULL)
              OR (canonical_kind = 4 AND event_id IS NOT NULL)
              OR (canonical_kind = 5 AND assertion_event_link_id IS NOT NULL)
              OR (canonical_kind = 6 AND contradiction_id IS NOT NULL)))
    )
);

CREATE TABLE casemesh.extraction_candidate_sources (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    candidate_id uuid NOT NULL,
    source_span_id uuid NOT NULL,
    ordinal integer NOT NULL CHECK (ordinal >= 0),
    PRIMARY KEY (tenant_id, matter_id, candidate_id, source_span_id),
    UNIQUE (tenant_id, matter_id, candidate_id, ordinal),
    FOREIGN KEY (tenant_id, matter_id, candidate_id)
        REFERENCES casemesh.extraction_candidates (tenant_id, matter_id, candidate_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, source_span_id)
        REFERENCES casemesh.source_spans (tenant_id, matter_id, source_span_id) DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE casemesh.matter_brain_dependencies (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    dependency_id uuid NOT NULL,
    extraction_run_id uuid NOT NULL,
    source_span_id uuid NOT NULL,
    candidate_id uuid NOT NULL,
    canonical_kind smallint NOT NULL CHECK (canonical_kind BETWEEN 0 AND 7),
    person_id uuid NULL,
    organisation_id uuid NULL,
    communication_id uuid NULL,
    assertion_id uuid NULL,
    event_id uuid NULL,
    assertion_event_link_id uuid NULL,
    contradiction_id uuid NULL,
    analysis_node_id uuid NULL,
    PRIMARY KEY (tenant_id, matter_id, dependency_id),
    FOREIGN KEY (tenant_id, matter_id, extraction_run_id)
        REFERENCES casemesh.extraction_runs (tenant_id, matter_id, extraction_run_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, source_span_id)
        REFERENCES casemesh.source_spans (tenant_id, matter_id, source_span_id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, matter_id, candidate_id, extraction_run_id)
        REFERENCES casemesh.extraction_candidates
            (tenant_id, matter_id, candidate_id, extraction_run_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, candidate_id, source_span_id)
        REFERENCES casemesh.extraction_candidate_sources
            (tenant_id, matter_id, candidate_id, source_span_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, person_id)
        REFERENCES casemesh.people (tenant_id, matter_id, person_id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, matter_id, organisation_id)
        REFERENCES casemesh.organisations (tenant_id, matter_id, organisation_id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, matter_id, communication_id)
        REFERENCES casemesh.communications (tenant_id, matter_id, communication_id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, matter_id, assertion_id)
        REFERENCES casemesh.assertions (tenant_id, matter_id, assertion_id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, matter_id, event_id)
        REFERENCES casemesh.matter_events (tenant_id, matter_id, event_id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, matter_id, assertion_event_link_id)
        REFERENCES casemesh.assertion_event_links (tenant_id, matter_id, link_id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, matter_id, contradiction_id)
        REFERENCES casemesh.contradictions (tenant_id, matter_id, contradiction_id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, matter_id, analysis_node_id)
        REFERENCES casemesh.analysis_nodes (tenant_id, matter_id, analysis_node_id) DEFERRABLE INITIALLY DEFERRED,
    CHECK (num_nonnulls(person_id, organisation_id, communication_id, assertion_id, event_id,
              assertion_event_link_id, contradiction_id, analysis_node_id) = 1
        AND ((canonical_kind = 0 AND person_id IS NOT NULL)
          OR (canonical_kind = 1 AND organisation_id IS NOT NULL)
          OR (canonical_kind = 2 AND communication_id IS NOT NULL)
          OR (canonical_kind = 3 AND assertion_id IS NOT NULL)
          OR (canonical_kind = 4 AND event_id IS NOT NULL)
          OR (canonical_kind = 5 AND assertion_event_link_id IS NOT NULL)
          OR (canonical_kind = 6 AND contradiction_id IS NOT NULL)
          OR (canonical_kind = 7 AND analysis_node_id IS NOT NULL)))
);

CREATE UNIQUE INDEX matter_brain_dependencies_target_uq
    ON casemesh.matter_brain_dependencies (
        tenant_id, matter_id, extraction_run_id, source_span_id, candidate_id, canonical_kind,
        COALESCE(person_id, organisation_id, communication_id, assertion_id, event_id,
                 assertion_event_link_id, contradiction_id, analysis_node_id));

CREATE TABLE casemesh.dependency_invalidations (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    invalidation_id uuid NOT NULL,
    dependency_id uuid NOT NULL,
    invalidated_by_run_id uuid NULL,
    invalidated_by_audit_event_id uuid NULL,
    invalidated_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, matter_id, invalidation_id),
    UNIQUE (tenant_id, matter_id, dependency_id),
    FOREIGN KEY (tenant_id, matter_id, dependency_id)
        REFERENCES casemesh.matter_brain_dependencies (tenant_id, matter_id, dependency_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, invalidated_by_run_id)
        REFERENCES casemesh.extraction_runs (tenant_id, matter_id, extraction_run_id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, matter_id, invalidated_by_audit_event_id)
        REFERENCES casemesh.audit_events (tenant_id, matter_id, audit_event_id) DEFERRABLE INITIALLY DEFERRED,
    CHECK (num_nonnulls(invalidated_by_run_id, invalidated_by_audit_event_id) = 1)
);

CREATE TABLE casemesh.entity_resolution_actions (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    action_id uuid NOT NULL,
    proposal_id uuid NOT NULL,
    action_kind smallint NOT NULL CHECK (action_kind BETWEEN 0 AND 3),
    entity_kind smallint NOT NULL CHECK (entity_kind BETWEEN 0 AND 1),
    source_person_id uuid NULL,
    target_person_id uuid NULL,
    source_organisation_id uuid NULL,
    target_organisation_id uuid NULL,
    match_score numeric NULL CHECK (match_score BETWEEN 0 AND 1),
    actor text NOT NULL CHECK (btrim(actor) <> ''),
    occurred_at timestamptz NOT NULL,
    reverses_action_id uuid NULL,
    PRIMARY KEY (tenant_id, matter_id, action_id),
    FOREIGN KEY (tenant_id, matter_id, proposal_id)
        REFERENCES casemesh.entity_resolution_actions (tenant_id, matter_id, action_id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, matter_id, reverses_action_id)
        REFERENCES casemesh.entity_resolution_actions (tenant_id, matter_id, action_id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, matter_id, source_person_id)
        REFERENCES casemesh.people (tenant_id, matter_id, person_id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, matter_id, target_person_id)
        REFERENCES casemesh.people (tenant_id, matter_id, person_id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, matter_id, source_organisation_id)
        REFERENCES casemesh.organisations (tenant_id, matter_id, organisation_id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, matter_id, target_organisation_id)
        REFERENCES casemesh.organisations (tenant_id, matter_id, organisation_id) DEFERRABLE INITIALLY DEFERRED,
    CHECK ((entity_kind = 0 AND source_person_id IS NOT NULL AND target_person_id IS NOT NULL
              AND source_organisation_id IS NULL AND target_organisation_id IS NULL)
        OR (entity_kind = 1 AND source_organisation_id IS NOT NULL AND target_organisation_id IS NOT NULL
              AND source_person_id IS NULL AND target_person_id IS NULL)),
    CHECK (entity_kind <> 0 OR source_person_id IS DISTINCT FROM target_person_id),
    CHECK (entity_kind <> 1 OR source_organisation_id IS DISTINCT FROM target_organisation_id),
    CHECK ((action_kind = 0 AND proposal_id = action_id AND reverses_action_id IS NULL)
        OR (action_kind IN (1, 2) AND proposal_id <> action_id AND reverses_action_id IS NULL)
        OR (action_kind = 3 AND proposal_id <> action_id AND reverses_action_id IS NOT NULL))
);

CREATE UNIQUE INDEX entity_resolution_decision_uq
    ON casemesh.entity_resolution_actions (tenant_id, matter_id, proposal_id)
    WHERE action_kind IN (1, 2);
CREATE UNIQUE INDEX entity_resolution_reversal_uq
    ON casemesh.entity_resolution_actions (tenant_id, matter_id, reverses_action_id)
    WHERE action_kind = 3;

CREATE TABLE casemesh.entity_resolution_sources (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    action_id uuid NOT NULL,
    source_span_id uuid NOT NULL,
    ordinal integer NOT NULL CHECK (ordinal >= 0),
    PRIMARY KEY (tenant_id, matter_id, action_id, source_span_id),
    UNIQUE (tenant_id, matter_id, action_id, ordinal),
    FOREIGN KEY (tenant_id, matter_id, action_id)
        REFERENCES casemesh.entity_resolution_actions (tenant_id, matter_id, action_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, source_span_id)
        REFERENCES casemesh.source_spans (tenant_id, matter_id, source_span_id) DEFERRABLE INITIALLY DEFERRED
);

CREATE INDEX entity_aliases_normalized_ix
    ON casemesh.entity_aliases (tenant_id, matter_id, entity_kind, normalized_value);
CREATE INDEX extraction_candidates_run_ix
    ON casemesh.extraction_candidates (tenant_id, matter_id, extraction_run_id);
CREATE INDEX matter_brain_dependencies_source_ix
    ON casemesh.matter_brain_dependencies (tenant_id, matter_id, source_span_id);

CREATE FUNCTION casemesh.reject_matter_brain_mutation() RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'DELETE' AND NOT EXISTS (
        SELECT 1 FROM casemesh.matters
        WHERE tenant_id = OLD.tenant_id AND matter_id = OLD.matter_id)
    THEN
        RETURN OLD;
    END IF;
    RAISE EXCEPTION 'Matter Brain canonical and history rows are append-only';
END;
$$;

DO $$
DECLARE
    table_name text;
BEGIN
    FOREACH table_name IN ARRAY ARRAY[
        'people', 'person_roles', 'organisations', 'entity_aliases', 'communications',
        'communication_participants', 'communication_sources',
        'extraction_runs', 'extraction_run_sources', 'extraction_candidates',
        'extraction_candidate_sources', 'matter_brain_dependencies', 'dependency_invalidations',
        'entity_resolution_actions', 'entity_resolution_sources'
    ]
    LOOP
        EXECUTE format(
            'CREATE TRIGGER %I BEFORE UPDATE OR DELETE ON casemesh.%I FOR EACH ROW EXECUTE FUNCTION casemesh.reject_matter_brain_mutation()',
            table_name || '_append_only', table_name);
        EXECUTE format(
            'CREATE TRIGGER %I BEFORE TRUNCATE ON casemesh.%I FOR EACH STATEMENT EXECUTE FUNCTION casemesh.reject_audit_truncate()',
            table_name || '_no_truncate', table_name);
        EXECUTE format('ALTER TABLE casemesh.%I ENABLE ROW LEVEL SECURITY', table_name);
        EXECUTE format('ALTER TABLE casemesh.%I FORCE ROW LEVEL SECURITY', table_name);
        EXECUTE format(
            'CREATE POLICY tenant_isolation ON casemesh.%I USING (tenant_id = NULLIF(current_setting(''casemesh.tenant_id'', true), '''')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting(''casemesh.tenant_id'', true), '''')::uuid)',
            table_name);
    END LOOP;
END;
$$;

DO $$
DECLARE
    runtime_role name;
BEGIN
    FOR runtime_role IN
        SELECT pg_get_userbyid(grant_entry.grantee)
        FROM pg_class relation
        JOIN pg_namespace namespace ON namespace.oid = relation.relnamespace
        CROSS JOIN LATERAL aclexplode(
            COALESCE(relation.relacl, acldefault('r', relation.relowner))) grant_entry
        WHERE namespace.nspname = 'casemesh'
          AND relation.relname = 'matters'
          AND grant_entry.grantee <> 0
        GROUP BY grant_entry.grantee
        HAVING bool_or(grant_entry.privilege_type = 'SELECT')
           AND bool_or(grant_entry.privilege_type = 'INSERT')
           AND bool_or(grant_entry.privilege_type = 'UPDATE')
    LOOP
        EXECUTE format(
            'GRANT SELECT, INSERT ON TABLE casemesh.people, casemesh.person_roles, casemesh.organisations, casemesh.entity_aliases, casemesh.communications, casemesh.communication_participants, casemesh.communication_sources, casemesh.extraction_runs, casemesh.extraction_run_sources, casemesh.extraction_candidates, casemesh.extraction_candidate_sources, casemesh.matter_brain_dependencies, casemesh.dependency_invalidations, casemesh.entity_resolution_actions, casemesh.entity_resolution_sources TO %I',
            runtime_role);
    END LOOP;
END;
$$;
