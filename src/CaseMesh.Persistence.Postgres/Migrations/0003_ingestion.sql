ALTER TABLE casemesh.document_versions
    ADD CONSTRAINT document_versions_ingestion_document_uq
        UNIQUE (tenant_id, matter_id, document_id, document_version_id),
    ADD CONSTRAINT document_versions_ingestion_identity_uq
        UNIQUE (tenant_id, matter_id, document_id, document_version_id, original_object_id);

CREATE TABLE casemesh.ingestion_span_sets (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    document_id uuid NOT NULL,
    document_version_id uuid NOT NULL,
    span_set_id uuid NOT NULL,
    pipeline_fingerprint char(64) NOT NULL CHECK (pipeline_fingerprint ~ '^[0-9A-F]{64}$'),
    detected_media_type smallint NOT NULL CHECK (detected_media_type BETWEEN 1 AND 6),
    parser_provider text NOT NULL CHECK (btrim(parser_provider) <> ''),
    parser_version text NOT NULL CHECK (btrim(parser_version) <> ''),
    ocr_provider text NULL CHECK (ocr_provider IS NULL OR btrim(ocr_provider) <> ''),
    ocr_version text NULL CHECK (ocr_version IS NULL OR btrim(ocr_version) <> ''),
    created_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, matter_id, span_set_id),
    UNIQUE (tenant_id, matter_id, document_version_id, pipeline_fingerprint),
    UNIQUE (tenant_id, matter_id, document_version_id, span_set_id),
    FOREIGN KEY (tenant_id, matter_id, document_id, document_version_id)
        REFERENCES casemesh.document_versions (
            tenant_id, matter_id, document_id, document_version_id) ON DELETE CASCADE,
    CHECK ((ocr_provider IS NULL) = (ocr_version IS NULL))
);

CREATE TABLE casemesh.ingestion_attempts (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    document_id uuid NOT NULL,
    document_version_id uuid NOT NULL,
    original_object_id uuid NOT NULL,
    attempt_id uuid NOT NULL,
    pipeline_fingerprint char(64) NOT NULL CHECK (pipeline_fingerprint ~ '^[0-9A-F]{64}$'),
    started_at timestamptz NOT NULL,
    completed_at timestamptz NOT NULL CHECK (completed_at >= started_at),
    status smallint NOT NULL CHECK (status BETWEEN 1 AND 4),
    detected_media_type smallint NULL CHECK (detected_media_type BETWEEN 1 AND 6),
    byte_length bigint NOT NULL CHECK (byte_length >= 0),
    scanner_provider text NULL CHECK (scanner_provider IS NULL OR btrim(scanner_provider) <> ''),
    scanner_version text NULL CHECK (scanner_version IS NULL OR btrim(scanner_version) <> ''),
    scanner_result text NULL CHECK (scanner_result IS NULL OR btrim(scanner_result) <> ''),
    failure_kind smallint NULL CHECK (failure_kind BETWEEN 1 AND 10),
    failure_code text NULL CHECK (failure_code IS NULL OR btrim(failure_code) <> ''),
    span_set_id uuid NULL,
    PRIMARY KEY (tenant_id, matter_id, attempt_id),
    UNIQUE (tenant_id, matter_id, document_version_id, attempt_id),
    FOREIGN KEY (tenant_id, matter_id, document_id, document_version_id, original_object_id)
        REFERENCES casemesh.document_versions (
            tenant_id, matter_id, document_id, document_version_id, original_object_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, document_version_id, span_set_id)
        REFERENCES casemesh.ingestion_span_sets (
            tenant_id, matter_id, document_version_id, span_set_id) DEFERRABLE INITIALLY DEFERRED,
    CHECK ((status = 2) = (span_set_id IS NOT NULL)),
    CHECK ((status IN (3, 4)) = (failure_kind IS NOT NULL AND failure_code IS NOT NULL)),
    CHECK ((status = 3) = (failure_kind = 4))
);

CREATE TABLE casemesh.document_ingestion_state (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    document_id uuid NOT NULL,
    document_version_id uuid NOT NULL,
    original_object_id uuid NOT NULL,
    detected_media_type smallint NULL CHECK (detected_media_type BETWEEN 1 AND 6),
    byte_length bigint NOT NULL CHECK (byte_length >= 0),
    status smallint NOT NULL CHECK (status BETWEEN 1 AND 4),
    quarantined boolean NOT NULL,
    latest_attempt_id uuid NOT NULL,
    current_span_set_id uuid NULL,
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, matter_id, document_version_id),
    FOREIGN KEY (tenant_id, matter_id, document_id, document_version_id, original_object_id)
        REFERENCES casemesh.document_versions (
            tenant_id, matter_id, document_id, document_version_id, original_object_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, document_version_id, latest_attempt_id)
        REFERENCES casemesh.ingestion_attempts (
            tenant_id, matter_id, document_version_id, attempt_id),
    FOREIGN KEY (tenant_id, matter_id, document_version_id, current_span_set_id)
        REFERENCES casemesh.ingestion_span_sets (
            tenant_id, matter_id, document_version_id, span_set_id),
    CHECK (quarantined = (status = 3)),
    CHECK (status <> 2 OR current_span_set_id IS NOT NULL),
    CHECK (current_span_set_id IS NULL OR status IN (2, 4))
);

ALTER TABLE casemesh.source_spans
    ADD COLUMN span_set_id uuid NULL,
    ADD COLUMN span_ordinal integer NULL CHECK (span_ordinal >= 0),
    ADD COLUMN locator_kind smallint NULL CHECK (locator_kind BETWEEN 1 AND 8),
    ADD COLUMN stable_locator text NULL CHECK (stable_locator IS NULL OR btrim(stable_locator) <> ''),
    ADD COLUMN extraction_route smallint NULL CHECK (extraction_route BETWEEN 1 AND 2),
    ADD COLUMN extraction_provider text NULL CHECK (extraction_provider IS NULL OR btrim(extraction_provider) <> ''),
    ADD COLUMN extraction_provider_version text NULL CHECK (extraction_provider_version IS NULL OR btrim(extraction_provider_version) <> ''),
    ADD COLUMN bbox_left integer NULL CHECK (bbox_left >= 0),
    ADD COLUMN bbox_top integer NULL CHECK (bbox_top >= 0),
    ADD COLUMN bbox_width integer NULL CHECK (bbox_width > 0),
    ADD COLUMN bbox_height integer NULL CHECK (bbox_height > 0),
    ADD CONSTRAINT source_spans_ingestion_metadata_complete CHECK (
        (span_set_id IS NULL AND span_ordinal IS NULL AND locator_kind IS NULL AND stable_locator IS NULL
            AND extraction_route IS NULL AND extraction_provider IS NULL AND extraction_provider_version IS NULL
            AND bbox_left IS NULL AND bbox_top IS NULL AND bbox_width IS NULL AND bbox_height IS NULL)
        OR
        (span_set_id IS NOT NULL AND span_ordinal IS NOT NULL AND locator_kind IS NOT NULL AND stable_locator IS NOT NULL
            AND extraction_route IS NOT NULL AND extraction_provider IS NOT NULL AND extraction_provider_version IS NOT NULL)
    ),
    ADD CONSTRAINT source_spans_bbox_complete CHECK (
        (bbox_left IS NULL AND bbox_top IS NULL AND bbox_width IS NULL AND bbox_height IS NULL)
        OR
        (bbox_left IS NOT NULL AND bbox_top IS NOT NULL AND bbox_width IS NOT NULL AND bbox_height IS NOT NULL
            AND extraction_route = 2 AND locator_kind = 8)
    ),
    ADD CONSTRAINT source_spans_span_set_fk FOREIGN KEY (
        tenant_id, matter_id, document_version_id, span_set_id)
        REFERENCES casemesh.ingestion_span_sets (
            tenant_id, matter_id, document_version_id, span_set_id) ON DELETE CASCADE;

CREATE UNIQUE INDEX source_spans_ingestion_ordinal_uq
    ON casemesh.source_spans (tenant_id, matter_id, span_set_id, span_ordinal)
    WHERE span_set_id IS NOT NULL;

CREATE FUNCTION casemesh.reject_ingestion_source_span_mutation() RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF OLD.span_set_id IS NULL THEN
        RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
    END IF;
    IF TG_OP = 'UPDATE' AND NEW IS NOT DISTINCT FROM OLD THEN
        RETURN OLD;
    END IF;
    IF TG_OP = 'DELETE' AND NOT EXISTS (
        SELECT 1 FROM casemesh.matters
        WHERE tenant_id = OLD.tenant_id AND matter_id = OLD.matter_id)
    THEN
        RETURN OLD;
    END IF;
    RAISE EXCEPTION 'ingestion source spans are immutable';
END;
$$;

CREATE TRIGGER source_spans_ingestion_immutable
BEFORE UPDATE OR DELETE ON casemesh.source_spans
FOR EACH ROW EXECUTE FUNCTION casemesh.reject_ingestion_source_span_mutation();

CREATE INDEX ingestion_attempts_document_ix
    ON casemesh.ingestion_attempts (tenant_id, matter_id, document_version_id, started_at);
CREATE INDEX document_ingestion_state_status_ix
    ON casemesh.document_ingestion_state (tenant_id, matter_id, status);

CREATE FUNCTION casemesh.reject_ingestion_history_mutation() RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'DELETE' AND NOT EXISTS (
        SELECT 1 FROM casemesh.matters
        WHERE tenant_id = OLD.tenant_id AND matter_id = OLD.matter_id)
    THEN
        RETURN OLD;
    END IF;
    RAISE EXCEPTION 'ingestion attempts and span sets are append-only';
END;
$$;

CREATE TRIGGER ingestion_attempts_append_only
BEFORE UPDATE OR DELETE ON casemesh.ingestion_attempts
FOR EACH ROW EXECUTE FUNCTION casemesh.reject_ingestion_history_mutation();
CREATE TRIGGER ingestion_span_sets_append_only
BEFORE UPDATE OR DELETE ON casemesh.ingestion_span_sets
FOR EACH ROW EXECUTE FUNCTION casemesh.reject_ingestion_history_mutation();

DO $$
DECLARE
    table_name text;
BEGIN
    FOREACH table_name IN ARRAY ARRAY[
        'ingestion_span_sets', 'ingestion_attempts', 'document_ingestion_state'
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

-- Preserve restricted runtime privileges provisioned against the existing Matter schema.
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
            'GRANT SELECT, INSERT ON TABLE casemesh.ingestion_span_sets, casemesh.ingestion_attempts TO %I',
            runtime_role);
        EXECUTE format(
            'GRANT SELECT, INSERT, UPDATE ON TABLE casemesh.document_ingestion_state TO %I',
            runtime_role);
        EXECUTE format(
            'GRANT SELECT, INSERT ON TABLE casemesh.source_spans TO %I',
            runtime_role);
    END LOOP;
END;
$$;
