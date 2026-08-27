ALTER TABLE casemesh.extraction_runs
    ADD COLUMN run_sequence bigint NULL,
    ADD CONSTRAINT extraction_runs_run_sequence_check
        CHECK (run_sequence IS NULL OR run_sequence > 0);

CREATE UNIQUE INDEX extraction_runs_run_sequence_uq
    ON casemesh.extraction_runs (tenant_id, matter_id, run_sequence)
    WHERE run_sequence IS NOT NULL;
