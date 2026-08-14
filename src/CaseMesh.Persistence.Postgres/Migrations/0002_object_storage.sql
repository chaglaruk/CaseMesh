CREATE TABLE casemesh.original_object_storage (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    original_object_id uuid NOT NULL,
    backend_kind text NOT NULL CHECK (backend_kind = 's3'),
    bucket_name text NOT NULL CHECK (btrim(bucket_name) <> ''),
    object_key text NOT NULL,
    content_sha256 char(64) NOT NULL CHECK (content_sha256 ~ '^[0-9A-F]{64}$'),
    byte_length bigint NOT NULL CHECK (byte_length >= 0),
    stored_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, matter_id, original_object_id),
    UNIQUE (backend_kind, bucket_name, object_key),
    FOREIGN KEY (tenant_id, matter_id, original_object_id, content_sha256)
        REFERENCES casemesh.original_objects (
            tenant_id, matter_id, original_object_id, content_sha256),
    CHECK (
        object_key = 'v1/tenants/' || tenant_id::text ||
                     '/matters/' || matter_id::text ||
                     '/originals/' || original_object_id::text)
);

CREATE INDEX original_object_storage_matter_ix
    ON casemesh.original_object_storage (tenant_id, matter_id);

CREATE FUNCTION casemesh.reject_original_storage_update() RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'original-object storage metadata is immutable';
END;
$$;

CREATE TRIGGER original_object_storage_no_update
BEFORE UPDATE ON casemesh.original_object_storage
FOR EACH ROW EXECUTE FUNCTION casemesh.reject_original_storage_update();

CREATE FUNCTION casemesh.reject_matter_delete_with_storage() RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM casemesh.original_object_storage
        WHERE tenant_id = OLD.tenant_id AND matter_id = OLD.matter_id)
    THEN
        RAISE EXCEPTION 'stored evidence must be deleted through the storage-aware Matter deletion workflow';
    END IF;
    RETURN OLD;
END;
$$;

CREATE TRIGGER matters_require_storage_aware_delete
BEFORE DELETE ON casemesh.matters
FOR EACH ROW EXECUTE FUNCTION casemesh.reject_matter_delete_with_storage();

CREATE FUNCTION casemesh.reject_tenant_delete_with_storage() RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM casemesh.original_object_storage
        WHERE tenant_id = OLD.tenant_id)
    THEN
        RAISE EXCEPTION 'stored evidence must be deleted through the storage-aware tenant deletion workflow';
    END IF;
    RETURN OLD;
END;
$$;

CREATE TRIGGER tenants_require_storage_aware_delete
BEFORE DELETE ON casemesh.tenants
FOR EACH ROW EXECUTE FUNCTION casemesh.reject_tenant_delete_with_storage();

ALTER TABLE casemesh.original_object_storage ENABLE ROW LEVEL SECURITY;
ALTER TABLE casemesh.original_object_storage FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON casemesh.original_object_storage
    USING (tenant_id = NULLIF(current_setting('casemesh.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = NULLIF(current_setting('casemesh.tenant_id', true), '')::uuid);

-- Preserve the privileges of restricted runtime roles that were provisioned against
-- 0001 before this table existed. Read-only roles do not gain write privileges, and
-- PUBLIC is deliberately excluded.
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
           AND bool_or(grant_entry.privilege_type = 'DELETE')
    LOOP
        EXECUTE format(
            'GRANT SELECT, INSERT, DELETE ON TABLE casemesh.original_object_storage TO %I',
            runtime_role);
    END LOOP;
END;
$$;
