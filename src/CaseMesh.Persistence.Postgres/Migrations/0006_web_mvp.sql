CREATE TABLE casemesh.web_users (
    user_id uuid PRIMARY KEY,
    issuer text NOT NULL CHECK (btrim(issuer) <> ''),
    subject text NOT NULL CHECK (btrim(subject) <> ''),
    display_name text NOT NULL CHECK (btrim(display_name) <> ''),
    created_at timestamptz NOT NULL,
    UNIQUE (issuer, subject)
);

CREATE TABLE casemesh.tenant_memberships (
    tenant_id uuid NOT NULL,
    user_id uuid NOT NULL,
    membership_role smallint NOT NULL CHECK (membership_role BETWEEN 1 AND 2),
    created_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, user_id),
    FOREIGN KEY (tenant_id) REFERENCES casemesh.tenants (tenant_id) ON DELETE CASCADE,
    FOREIGN KEY (user_id) REFERENCES casemesh.web_users (user_id) ON DELETE CASCADE
);

CREATE TABLE casemesh.web_document_metadata (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    document_id uuid NOT NULL,
    original_file_name text NOT NULL CHECK (
        btrim(original_file_name) <> '' AND length(original_file_name) <= 255 AND
        original_file_name !~ '[\\/]' AND original_file_name !~ '\.\.'),
    uploaded_by_user_id uuid NOT NULL,
    uploaded_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, matter_id, document_id),
    FOREIGN KEY (tenant_id, matter_id, document_id)
        REFERENCES casemesh.documents (tenant_id, matter_id, document_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, uploaded_by_user_id)
        REFERENCES casemesh.tenant_memberships (tenant_id, user_id)
);

ALTER TABLE casemesh.document_versions
    ADD CONSTRAINT document_versions_document_version_uq
    UNIQUE (tenant_id, matter_id, document_id, document_version_id);

CREATE TABLE casemesh.web_processing_jobs (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    job_id uuid NOT NULL,
    document_id uuid NOT NULL,
    document_version_id uuid NOT NULL,
    original_object_id uuid NOT NULL,
    requested_by_user_id uuid NOT NULL,
    status smallint NOT NULL CHECK (status BETWEEN 1 AND 4),
    attempts integer NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    available_at timestamptz NOT NULL,
    lease_owner uuid NULL,
    lease_expires_at timestamptz NULL,
    failure_code text NULL CHECK (failure_code IS NULL OR btrim(failure_code) <> ''),
    created_at timestamptz NOT NULL,
    completed_at timestamptz NULL,
    PRIMARY KEY (tenant_id, matter_id, job_id),
    UNIQUE (tenant_id, matter_id, document_version_id),
    FOREIGN KEY (tenant_id, matter_id, document_id)
        REFERENCES casemesh.documents (tenant_id, matter_id, document_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, document_id, document_version_id)
        REFERENCES casemesh.document_versions (tenant_id, matter_id, document_id, document_version_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, original_object_id)
        REFERENCES casemesh.original_objects (tenant_id, matter_id, original_object_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, requested_by_user_id)
        REFERENCES casemesh.tenant_memberships (tenant_id, user_id),
    CHECK ((lease_owner IS NULL) = (lease_expires_at IS NULL)),
    CHECK ((status = 2) = (lease_owner IS NOT NULL)),
    CHECK ((status = 3) = (completed_at IS NOT NULL))
);

CREATE INDEX web_processing_jobs_claim_ix
    ON casemesh.web_processing_jobs (available_at, created_at)
    WHERE status IN (1, 2);

ALTER TABLE casemesh.tenant_memberships ENABLE ROW LEVEL SECURITY;
ALTER TABLE casemesh.tenant_memberships FORCE ROW LEVEL SECURITY;
CREATE POLICY member_self_access ON casemesh.tenant_memberships
    FOR SELECT
    USING (user_id = NULLIF(current_setting('casemesh.user_id', true), '')::uuid);

DO $$
DECLARE
    table_name text;
BEGIN
    FOREACH table_name IN ARRAY ARRAY['web_document_metadata', 'web_processing_jobs']
    LOOP
        EXECUTE format('ALTER TABLE casemesh.%I ENABLE ROW LEVEL SECURITY', table_name);
        EXECUTE format('ALTER TABLE casemesh.%I FORCE ROW LEVEL SECURITY', table_name);
        EXECUTE format(
            'CREATE POLICY tenant_isolation ON casemesh.%I USING (tenant_id = NULLIF(current_setting(''casemesh.tenant_id'', true), '''')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting(''casemesh.tenant_id'', true), '''')::uuid)',
            table_name);
    END LOOP;
END;
$$;

CREATE FUNCTION casemesh.create_owned_workspace(
    requested_user_id uuid,
    requested_tenant_id uuid,
    requested_display_name text,
    requested_created_at timestamptz)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, casemesh
AS $$
BEGIN
    IF NULLIF(current_setting('casemesh.user_id', true), '')::uuid IS DISTINCT FROM requested_user_id THEN
        RAISE EXCEPTION 'The authenticated user does not match the workspace owner.'
            USING ERRCODE = '42501';
    END IF;
    INSERT INTO casemesh.tenants (tenant_id, display_name, created_at)
    VALUES (requested_tenant_id, requested_display_name, requested_created_at);
    INSERT INTO casemesh.tenant_memberships (tenant_id, user_id, membership_role, created_at)
    VALUES (requested_tenant_id, requested_user_id, 1, requested_created_at);
END;
$$;

CREATE FUNCTION casemesh.pending_web_job_scopes(requested_now timestamptz)
RETURNS TABLE (tenant_id uuid, user_id uuid)
LANGUAGE sql
SECURITY DEFINER
SET search_path = pg_catalog, casemesh
AS $$
    SELECT DISTINCT job.tenant_id, job.requested_by_user_id
    FROM casemesh.web_processing_jobs AS job
    WHERE job.available_at <= requested_now
      AND (job.status = 1 OR (job.status = 2 AND job.lease_expires_at <= requested_now));
$$;

REVOKE ALL ON FUNCTION casemesh.create_owned_workspace(uuid, uuid, text, timestamptz) FROM PUBLIC;
REVOKE ALL ON FUNCTION casemesh.pending_web_job_scopes(timestamptz) FROM PUBLIC;

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
        EXECUTE format('GRANT SELECT, INSERT, UPDATE ON TABLE casemesh.web_users TO %I', runtime_role);
        EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE casemesh.web_document_metadata, casemesh.web_processing_jobs TO %I', runtime_role);
        EXECUTE format('GRANT SELECT ON TABLE casemesh.tenant_memberships TO %I', runtime_role);
        EXECUTE format('REVOKE INSERT, UPDATE, DELETE ON TABLE casemesh.tenant_memberships FROM %I', runtime_role);
        EXECUTE format('GRANT EXECUTE ON FUNCTION casemesh.create_owned_workspace(uuid, uuid, text, timestamptz), casemesh.pending_web_job_scopes(timestamptz) TO %I', runtime_role);
    END LOOP;
END;
$$;
