CREATE TABLE casemesh.uploaded_meeting_reviews (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    meeting_id uuid NOT NULL,
    created_by_user_id uuid NOT NULL,
    context_currentness smallint NOT NULL CHECK (context_currentness BETWEEN 0 AND 1),
    created_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, matter_id, meeting_id),
    FOREIGN KEY (tenant_id, matter_id)
        REFERENCES casemesh.matters (tenant_id, matter_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, created_by_user_id)
        REFERENCES casemesh.tenant_memberships (tenant_id, user_id)
);

CREATE TABLE casemesh.uploaded_meeting_review_items (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    meeting_id uuid NOT NULL,
    item_id uuid NOT NULL,
    ordinal integer NOT NULL CHECK (ordinal >= 0),
    origin smallint NOT NULL CHECK (origin BETWEEN 0 AND 2),
    transcript_text text NOT NULL CHECK (btrim(transcript_text) <> '' AND length(transcript_text) <= 8000),
    started_at timestamptz NOT NULL,
    ended_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, matter_id, meeting_id, item_id),
    UNIQUE (tenant_id, matter_id, meeting_id, ordinal),
    FOREIGN KEY (tenant_id, matter_id, meeting_id)
        REFERENCES casemesh.uploaded_meeting_reviews (tenant_id, matter_id, meeting_id) ON DELETE CASCADE,
    CHECK (ended_at >= started_at)
);

CREATE TABLE casemesh.uploaded_meeting_review_context_citations (
    tenant_id uuid NOT NULL,
    matter_id uuid NOT NULL,
    meeting_id uuid NOT NULL,
    item_id uuid NOT NULL,
    source_span_id uuid NOT NULL,
    ordinal integer NOT NULL CHECK (ordinal >= 0),
    PRIMARY KEY (tenant_id, matter_id, meeting_id, item_id, source_span_id),
    UNIQUE (tenant_id, matter_id, meeting_id, item_id, ordinal),
    FOREIGN KEY (tenant_id, matter_id, meeting_id, item_id)
        REFERENCES casemesh.uploaded_meeting_review_items
            (tenant_id, matter_id, meeting_id, item_id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, matter_id, source_span_id)
        REFERENCES casemesh.source_spans (tenant_id, matter_id, source_span_id) ON DELETE CASCADE
);

CREATE INDEX uploaded_meeting_reviews_recent_ix
    ON casemesh.uploaded_meeting_reviews (tenant_id, matter_id, created_at DESC, meeting_id);

DO $$
DECLARE
    table_name text;
BEGIN
    FOREACH table_name IN ARRAY ARRAY[
        'uploaded_meeting_reviews',
        'uploaded_meeting_review_items',
        'uploaded_meeting_review_context_citations'
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
        EXECUTE format(
            'GRANT SELECT, INSERT ON TABLE casemesh.uploaded_meeting_reviews, casemesh.uploaded_meeting_review_items, casemesh.uploaded_meeting_review_context_citations TO %I',
            runtime_role);
    END LOOP;
END;
$$;