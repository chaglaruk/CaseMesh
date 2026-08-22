CREATE TABLE casemesh.pilot_entitlements (
    tenant_id uuid PRIMARY KEY,
    tier_code text NOT NULL CHECK (tier_code ~ '^[a-z0-9][a-z0-9-]{0,39}$'),
    active_matter_limit integer NOT NULL CHECK (active_matter_limit > 0),
    matter_original_bytes_limit bigint NOT NULL CHECK (matter_original_bytes_limit > 0),
    tenant_original_bytes_limit bigint NOT NULL CHECK (tenant_original_bytes_limit > 0),
    matter_evidence_item_limit integer NOT NULL CHECK (matter_evidence_item_limit > 0),
    tenant_evidence_item_limit integer NOT NULL CHECK (tenant_evidence_item_limit > 0),
    ingestion_attempt_limit integer NOT NULL CHECK (ingestion_attempt_limit BETWEEN 1 AND 20),
    qa_daily_request_limit integer NOT NULL CHECK (qa_daily_request_limit > 0),
    qa_context_byte_limit integer NOT NULL CHECK (qa_context_byte_limit BETWEEN 1024 AND 65536),
    export_daily_limit integer NOT NULL CHECK (export_daily_limit > 0),
    conversation_history_limit integer NOT NULL CHECK (conversation_history_limit >= 0),
    failed_job_retention_days integer NOT NULL CHECK (failed_job_retention_days BETWEEN 1 AND 3650),
    qa_metadata_retention_days integer NOT NULL CHECK (qa_metadata_retention_days BETWEEN 1 AND 3650),
    export_artifact_retention_hours integer NOT NULL CHECK (export_artifact_retention_hours BETWEEN 1 AND 8760),
    operational_log_retention_days integer NOT NULL CHECK (operational_log_retention_days BETWEEN 1 AND 3650),
    configured_at timestamptz NOT NULL,
    configured_by text NOT NULL CHECK (configured_by ~ '^[a-z0-9][a-z0-9._:-]{0,119}$'),
    FOREIGN KEY (tenant_id) REFERENCES casemesh.tenants (tenant_id) ON DELETE CASCADE,
    CHECK (tenant_original_bytes_limit >= matter_original_bytes_limit),
    CHECK (tenant_evidence_item_limit >= matter_evidence_item_limit)
);

INSERT INTO casemesh.pilot_entitlements (
    tenant_id,tier_code,active_matter_limit,matter_original_bytes_limit,tenant_original_bytes_limit,
    matter_evidence_item_limit,tenant_evidence_item_limit,ingestion_attempt_limit,
    qa_daily_request_limit,qa_context_byte_limit,export_daily_limit,conversation_history_limit,
    failed_job_retention_days,qa_metadata_retention_days,export_artifact_retention_hours,
    operational_log_retention_days,configured_at,configured_by)
SELECT tenant_id,'closed-pilot',25,536870912,2147483648,200,1000,3,200,32768,20,0,
       30,30,168,30,created_at,'migration:0008'
FROM casemesh.tenants;

CREATE FUNCTION casemesh.seed_closed_pilot_entitlement() RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, casemesh
AS $$
BEGIN
    INSERT INTO casemesh.pilot_entitlements (
        tenant_id,tier_code,active_matter_limit,matter_original_bytes_limit,tenant_original_bytes_limit,
        matter_evidence_item_limit,tenant_evidence_item_limit,ingestion_attempt_limit,
        qa_daily_request_limit,qa_context_byte_limit,export_daily_limit,conversation_history_limit,
        failed_job_retention_days,qa_metadata_retention_days,export_artifact_retention_hours,
        operational_log_retention_days,configured_at,configured_by)
    VALUES (NEW.tenant_id,'closed-pilot',25,536870912,2147483648,200,1000,3,200,32768,20,0,
            30,30,168,30,NEW.created_at,'database-default');
    RETURN NEW;
END;
$$;

CREATE TRIGGER tenants_seed_closed_pilot_entitlement
AFTER INSERT ON casemesh.tenants
FOR EACH ROW EXECUTE FUNCTION casemesh.seed_closed_pilot_entitlement();

CREATE TABLE casemesh.pilot_quota_reservations (
    tenant_id uuid NOT NULL,
    reservation_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    resource_kind smallint NOT NULL CHECK (resource_kind BETWEEN 1 AND 3),
    amount bigint NOT NULL CHECK (amount > 0),
    created_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id,reservation_id),
    FOREIGN KEY (tenant_id) REFERENCES casemesh.tenants (tenant_id) ON DELETE CASCADE,
    CHECK (expires_at > created_at)
);

CREATE INDEX pilot_quota_reservations_active_ix
    ON casemesh.pilot_quota_reservations (tenant_id,resource_kind,matter_id,expires_at);

CREATE TABLE casemesh.pilot_usage_daily (
    tenant_id uuid NOT NULL,
    usage_date date NOT NULL,
    usage_kind smallint NOT NULL CHECK (usage_kind BETWEEN 1 AND 4),
    quantity bigint NOT NULL CHECK (quantity >= 0),
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id,usage_date,usage_kind),
    FOREIGN KEY (tenant_id) REFERENCES casemesh.tenants (tenant_id) ON DELETE CASCADE
);

CREATE TABLE casemesh.pilot_usage_events (
    tenant_id uuid NOT NULL,
    usage_event_id uuid NOT NULL,
    matter_id uuid NULL,
    usage_kind smallint NOT NULL CHECK (usage_kind BETWEEN 1 AND 10),
    outcome_code text NOT NULL CHECK (outcome_code ~ '^[a-z0-9][a-z0-9-]{0,79}$'),
    quantity bigint NOT NULL CHECK (quantity >= 0),
    duration_ms bigint NULL CHECK (duration_ms >= 0),
    occurred_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id,usage_event_id),
    FOREIGN KEY (tenant_id) REFERENCES casemesh.tenants (tenant_id) ON DELETE CASCADE
);

CREATE INDEX pilot_usage_events_retention_ix
    ON casemesh.pilot_usage_events (tenant_id,occurred_at,usage_event_id);

CREATE TABLE casemesh.generated_export_objects (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    export_id uuid NOT NULL,
    artifact_kind smallint NOT NULL CHECK (artifact_kind BETWEEN 0 AND 7),
    backend_kind text NOT NULL CHECK (backend_kind = 's3'),
    bucket_name text NOT NULL CHECK (btrim(bucket_name) <> ''),
    object_key text NOT NULL,
    content_sha256 char(64) NOT NULL CHECK (content_sha256 ~ '^[0-9A-F]{64}$'),
    byte_length bigint NOT NULL CHECK (byte_length >= 0),
    stored_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id,matter_id,export_id,artifact_kind),
    UNIQUE (backend_kind,bucket_name,object_key),
    FOREIGN KEY (tenant_id,matter_id,export_id,artifact_kind)
        REFERENCES casemesh.professional_export_artifacts
            (tenant_id,matter_id,export_id,artifact_kind),
    CHECK (expires_at > stored_at),
    CHECK (object_key = 'v1/tenants/' || tenant_id::text || '/matters/' || matter_id::text ||
        '/generated/exports/' || export_id::text || '/' || artifact_kind::text)
);

CREATE INDEX generated_export_objects_expiry_ix
    ON casemesh.generated_export_objects (tenant_id,expires_at,matter_id,export_id);

CREATE TABLE casemesh.privacy_deletion_jobs (
    tenant_id uuid NOT NULL,
    deletion_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    requested_by_user_id uuid NOT NULL,
    status smallint NOT NULL CHECK (status BETWEEN 1 AND 4),
    attempts integer NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    available_at timestamptz NOT NULL,
    lease_owner uuid NULL,
    lease_expires_at timestamptz NULL,
    failure_category text NULL CHECK (failure_category IS NULL OR failure_category ~ '^[a-z0-9][a-z0-9-]{0,79}$'),
    requested_at timestamptz NOT NULL,
    completed_at timestamptz NULL,
    PRIMARY KEY (tenant_id,deletion_id),
    FOREIGN KEY (tenant_id) REFERENCES casemesh.tenants (tenant_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id,requested_by_user_id)
        REFERENCES casemesh.tenant_memberships (tenant_id,user_id),
    CHECK ((status = 2) = (lease_owner IS NOT NULL)),
    CHECK ((lease_owner IS NULL) = (lease_expires_at IS NULL)),
    CHECK ((status = 3) = (completed_at IS NOT NULL))
);

CREATE UNIQUE INDEX privacy_deletion_jobs_active_matter_uq
    ON casemesh.privacy_deletion_jobs (tenant_id,matter_id)
    WHERE status IN (1,2,4);

CREATE INDEX privacy_deletion_jobs_claim_ix
    ON casemesh.privacy_deletion_jobs (available_at,requested_at)
    WHERE status IN (1,2,4);

CREATE FUNCTION casemesh.pending_privacy_deletion_scopes(p_now timestamptz)
RETURNS TABLE (tenant_id uuid, user_id uuid)
LANGUAGE sql
SECURITY DEFINER
SET search_path = pg_catalog, casemesh
AS $$
    SELECT DISTINCT job.tenant_id, job.requested_by_user_id
    FROM casemesh.privacy_deletion_jobs AS job
    WHERE job.available_at <= p_now
      AND (job.status IN (1,4) OR (job.status = 2 AND job.lease_expires_at <= p_now));
$$;

REVOKE ALL ON FUNCTION casemesh.pending_privacy_deletion_scopes(timestamptz) FROM PUBLIC;

CREATE FUNCTION casemesh.pilot_maintenance_tenants()
RETURNS TABLE (tenant_id uuid)
LANGUAGE sql
SECURITY DEFINER
SET search_path = pg_catalog, casemesh
AS $$
    SELECT entitlement.tenant_id FROM casemesh.pilot_entitlements AS entitlement;
$$;

REVOKE ALL ON FUNCTION casemesh.pilot_maintenance_tenants() FROM PUBLIC;

CREATE OR REPLACE FUNCTION casemesh.reject_matter_delete_with_storage() RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM casemesh.original_object_storage
        WHERE tenant_id = OLD.tenant_id AND matter_id = OLD.matter_id)
       OR EXISTS (
        SELECT 1 FROM casemesh.generated_export_objects
        WHERE tenant_id = OLD.tenant_id AND matter_id = OLD.matter_id)
    THEN
        RAISE EXCEPTION 'stored Matter objects must be deleted through the storage-aware deletion workflow';
    END IF;
    RETURN OLD;
END;
$$;

CREATE OR REPLACE FUNCTION casemesh.reject_tenant_delete_with_storage() RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM casemesh.original_object_storage
        WHERE tenant_id = OLD.tenant_id)
       OR EXISTS (
        SELECT 1 FROM casemesh.generated_export_objects
        WHERE tenant_id = OLD.tenant_id)
    THEN
        RAISE EXCEPTION 'stored tenant objects must be deleted through the storage-aware deletion workflow';
    END IF;
    RETURN OLD;
END;
$$;

DO $$
DECLARE
    table_name text;
BEGIN
    FOREACH table_name IN ARRAY ARRAY[
        'pilot_entitlements','pilot_quota_reservations','pilot_usage_daily',
        'pilot_usage_events','generated_export_objects','privacy_deletion_jobs'
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
        JOIN pg_roles role ON role.oid = grant_entry.grantee
        WHERE namespace.nspname = 'casemesh'
          AND relation.relname = 'matters'
          AND grant_entry.grantee <> 0
          AND grant_entry.grantee <> relation.relowner
          AND role.rolcanlogin AND NOT role.rolsuper AND NOT role.rolbypassrls
        GROUP BY grant_entry.grantee
        HAVING bool_or(grant_entry.privilege_type = 'SELECT')
           AND bool_or(grant_entry.privilege_type = 'INSERT')
           AND bool_or(grant_entry.privilege_type = 'UPDATE')
           AND bool_or(grant_entry.privilege_type = 'DELETE')
    LOOP
        EXECUTE format('GRANT SELECT ON TABLE casemesh.pilot_entitlements TO %I', runtime_role);
        EXECUTE format('GRANT SELECT,INSERT,DELETE ON TABLE casemesh.pilot_quota_reservations TO %I', runtime_role);
        EXECUTE format('GRANT SELECT,INSERT,UPDATE ON TABLE casemesh.pilot_usage_daily TO %I', runtime_role);
        EXECUTE format('GRANT SELECT,INSERT,DELETE ON TABLE casemesh.pilot_usage_events TO %I', runtime_role);
        EXECUTE format('GRANT SELECT,INSERT,DELETE ON TABLE casemesh.generated_export_objects TO %I', runtime_role);
        EXECUTE format('GRANT SELECT,INSERT,UPDATE,DELETE ON TABLE casemesh.privacy_deletion_jobs TO %I', runtime_role);
        EXECUTE format('GRANT EXECUTE ON FUNCTION casemesh.pending_privacy_deletion_scopes(timestamptz) TO %I', runtime_role);
        EXECUTE format('GRANT EXECUTE ON FUNCTION casemesh.pilot_maintenance_tenants() TO %I', runtime_role);
    END LOOP;
END;
$$;
