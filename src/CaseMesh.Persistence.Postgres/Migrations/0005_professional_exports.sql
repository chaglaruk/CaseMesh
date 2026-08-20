CREATE TABLE casemesh.professional_export_runs (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    export_id uuid NOT NULL,
    snapshot_digest char(64) NOT NULL CHECK (snapshot_digest ~ '^[0-9A-F]{64}$'),
    schema_version text NOT NULL CHECK (btrim(schema_version) <> ''),
    template_version text NOT NULL CHECK (btrim(template_version) <> ''),
    generated_at timestamptz NOT NULL,
    artifact_manifest_digest char(64) NOT NULL CHECK (artifact_manifest_digest ~ '^[0-9A-F]{64}$'),
    PRIMARY KEY (tenant_id, matter_id, export_id),
    FOREIGN KEY (tenant_id, matter_id)
        REFERENCES casemesh.matters (tenant_id, matter_id) ON DELETE CASCADE
);

CREATE TABLE casemesh.professional_export_inclusions (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    export_id uuid NOT NULL,
    inclusion_kind smallint NOT NULL CHECK (inclusion_kind BETWEEN 0 AND 4),
    ordinal integer NOT NULL CHECK (ordinal >= 0),
    document_version_id uuid NULL,
    source_span_id uuid NULL,
    assertion_id uuid NULL,
    event_id uuid NULL,
    contradiction_id uuid NULL,
    PRIMARY KEY (tenant_id, matter_id, export_id, inclusion_kind, ordinal),
    FOREIGN KEY (tenant_id, matter_id, export_id)
        REFERENCES casemesh.professional_export_runs (tenant_id, matter_id, export_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, document_version_id)
        REFERENCES casemesh.document_versions (tenant_id, matter_id, document_version_id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, matter_id, source_span_id)
        REFERENCES casemesh.source_spans (tenant_id, matter_id, source_span_id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, matter_id, assertion_id)
        REFERENCES casemesh.assertions (tenant_id, matter_id, assertion_id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, matter_id, event_id)
        REFERENCES casemesh.matter_events (tenant_id, matter_id, event_id) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (tenant_id, matter_id, contradiction_id)
        REFERENCES casemesh.contradictions (tenant_id, matter_id, contradiction_id) DEFERRABLE INITIALLY DEFERRED,
    CHECK (num_nonnulls(document_version_id, source_span_id, assertion_id, event_id, contradiction_id) = 1),
    CHECK ((inclusion_kind = 0) = (document_version_id IS NOT NULL)),
    CHECK ((inclusion_kind = 1) = (source_span_id IS NOT NULL)),
    CHECK ((inclusion_kind = 2) = (assertion_id IS NOT NULL)),
    CHECK ((inclusion_kind = 3) = (event_id IS NOT NULL)),
    CHECK ((inclusion_kind = 4) = (contradiction_id IS NOT NULL))
);

CREATE TABLE casemesh.professional_export_artifacts (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    export_id uuid NOT NULL,
    artifact_kind smallint NOT NULL CHECK (artifact_kind BETWEEN 0 AND 7),
    file_name text NOT NULL CHECK (
        btrim(file_name) <> '' AND file_name !~ '[\\/]' AND file_name !~ '\.\.'),
    content_sha256 char(64) NOT NULL CHECK (content_sha256 ~ '^[0-9A-F]{64}$'),
    byte_length bigint NOT NULL CHECK (byte_length >= 0),
    PRIMARY KEY (tenant_id, matter_id, export_id, artifact_kind),
    UNIQUE (tenant_id, matter_id, export_id, file_name),
    FOREIGN KEY (tenant_id, matter_id, export_id)
        REFERENCES casemesh.professional_export_runs (tenant_id, matter_id, export_id) ON DELETE CASCADE
);

CREATE INDEX professional_export_runs_generated_ix
    ON casemesh.professional_export_runs (tenant_id, matter_id, generated_at, export_id);

CREATE FUNCTION casemesh.reject_professional_export_mutation() RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'DELETE' AND NOT EXISTS (
        SELECT 1 FROM casemesh.matters
        WHERE tenant_id = OLD.tenant_id AND matter_id = OLD.matter_id)
    THEN
        RETURN OLD;
    END IF;
    RAISE EXCEPTION 'professional export audit rows are append-only';
END;
$$;

DO $$
DECLARE
    table_name text;
BEGIN
    FOREACH table_name IN ARRAY ARRAY[
        'professional_export_runs', 'professional_export_inclusions', 'professional_export_artifacts'
    ]
    LOOP
        EXECUTE format(
            'CREATE TRIGGER %I BEFORE UPDATE OR DELETE ON casemesh.%I FOR EACH ROW EXECUTE FUNCTION casemesh.reject_professional_export_mutation()',
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
            'GRANT SELECT, INSERT ON TABLE casemesh.professional_export_runs, casemesh.professional_export_inclusions, casemesh.professional_export_artifacts TO %I',
            runtime_role);
    END LOOP;
END;
$$;
