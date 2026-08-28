-- Existing rows intentionally remain NULL. Pre-migration execution order cannot be recovered
-- truthfully when legacy runs share a generated_at timestamp, so CaseMesh must not fabricate
-- history by ranking deterministic IDs. Runtime currentness handles that legacy tie fail-safe.
ALTER TABLE casemesh.extraction_runs
    ADD COLUMN run_sequence bigint NULL,
    ADD CONSTRAINT extraction_runs_run_sequence_check
        CHECK (run_sequence IS NULL OR run_sequence > 0);

CREATE UNIQUE INDEX extraction_runs_run_sequence_uq
    ON casemesh.extraction_runs (tenant_id, matter_id, run_sequence)
    WHERE run_sequence IS NOT NULL;
