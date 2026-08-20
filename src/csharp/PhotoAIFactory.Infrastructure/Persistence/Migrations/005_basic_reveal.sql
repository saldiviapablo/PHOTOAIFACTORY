ALTER TABLE jobs
ADD COLUMN reveal_retry_count INTEGER NOT NULL DEFAULT 0
CHECK (reveal_retry_count BETWEEN 0 AND 2);

CREATE UNIQUE INDEX ux_jobs_single_processing_per_project
ON jobs(project_id)
WHERE state='PROCESSING';

DROP TRIGGER job_checkpoints_no_update;

ALTER TABLE job_checkpoints RENAME TO job_checkpoints_v4;

CREATE TABLE job_checkpoints (
    checkpoint_id TEXT PRIMARY KEY NOT NULL CHECK (length(trim(checkpoint_id)) > 0),
    job_id TEXT NOT NULL REFERENCES jobs(job_id) ON DELETE RESTRICT,
    stage_name TEXT NOT NULL CHECK (
        stage_name IN (
            'ANALYSIS_COMPLETE',
            'PRESELECTION_COMPLETE',
            'BASIC_REVEAL_COMPLETE'
        )
    ),
    attempt_id TEXT NOT NULL CHECK (length(trim(attempt_id)) > 0),
    input_fingerprint TEXT NOT NULL CHECK (length(trim(input_fingerprint)) > 0),
    created_at_utc TEXT NOT NULL,
    UNIQUE(job_id, stage_name)
);

INSERT INTO job_checkpoints(
    checkpoint_id,
    job_id,
    stage_name,
    attempt_id,
    input_fingerprint,
    created_at_utc)
SELECT
    checkpoint_id,
    job_id,
    stage_name,
    attempt_id,
    input_fingerprint,
    created_at_utc
FROM job_checkpoints_v4;

DROP TABLE job_checkpoints_v4;

CREATE TRIGGER job_checkpoints_no_update
BEFORE UPDATE ON job_checkpoints
BEGIN
    SELECT RAISE(ABORT, 'Checkpoint rows are immutable');
END;

CREATE TRIGGER job_checkpoints_no_delete
BEFORE DELETE ON job_checkpoints
BEGIN
    SELECT RAISE(ABORT, 'Checkpoint rows are append-only');
END;

CREATE TABLE processing_recipes (
    recipe_id TEXT PRIMARY KEY NOT NULL CHECK (length(trim(recipe_id)) > 0),
    job_id TEXT NOT NULL UNIQUE REFERENCES jobs(job_id) ON DELETE RESTRICT,
    schema_version INTEGER NOT NULL CHECK (schema_version > 0),
    reveal_mode TEXT NOT NULL CHECK (reveal_mode IN ('PRE_AI','DT_AUTO')),
    recipe_json TEXT NOT NULL CHECK (length(trim(recipe_json)) > 2),
    recipe_sha256 TEXT NOT NULL CHECK (length(recipe_sha256)=64),
    created_at_utc TEXT NOT NULL
);

CREATE TABLE outputs (
    output_id TEXT PRIMARY KEY NOT NULL CHECK (length(trim(output_id)) > 0),
    job_id TEXT NOT NULL REFERENCES jobs(job_id) ON DELETE RESTRICT,
    attempt_id TEXT NOT NULL CHECK (length(trim(attempt_id)) > 0),
    stage TEXT NOT NULL CHECK (stage='BASIC_REVEAL'),
    role TEXT NOT NULL CHECK (role='BASIC_REVEAL_STAGING'),
    path TEXT NOT NULL CHECK (length(trim(path)) > 0),
    sha256 TEXT NOT NULL CHECK (length(sha256)=64),
    size_bytes INTEGER NOT NULL CHECK (size_bytes > 0),
    width INTEGER NOT NULL CHECK (width > 0),
    height INTEGER NOT NULL CHECK (height > 0),
    validated INTEGER NOT NULL CHECK (validated=1),
    permanent INTEGER NOT NULL CHECK (permanent IN (0,1)),
    created_at_utc TEXT NOT NULL,
    UNIQUE(job_id, stage, role)
);

CREATE INDEX ix_outputs_job
ON outputs(job_id);

CREATE TABLE processing_passes (
    processing_pass_id TEXT PRIMARY KEY NOT NULL CHECK (length(trim(processing_pass_id)) > 0),
    job_id TEXT NOT NULL UNIQUE REFERENCES jobs(job_id) ON DELETE RESTRICT,
    attempt_id TEXT NOT NULL CHECK (length(trim(attempt_id)) > 0),
    reveal_mode TEXT NOT NULL CHECK (reveal_mode IN ('PRE_AI','DT_AUTO')),
    input_asset_id TEXT NOT NULL REFERENCES assets(asset_id) ON DELETE RESTRICT,
    input_sha256 TEXT NOT NULL CHECK (length(input_sha256)=64),
    recipe_id TEXT NULL REFERENCES processing_recipes(recipe_id) ON DELETE RESTRICT,
    darktable_version TEXT NOT NULL CHECK (length(trim(darktable_version)) > 0),
    control_plan_json TEXT NOT NULL CHECK (length(trim(control_plan_json)) > 2),
    output_id TEXT NOT NULL REFERENCES outputs(output_id) ON DELETE RESTRICT,
    history_path TEXT NOT NULL CHECK (length(trim(history_path)) > 0),
    xmp_history_path TEXT NULL,
    completed_at_utc TEXT NOT NULL
);

CREATE INDEX ix_processing_passes_job
ON processing_passes(job_id);

CREATE TRIGGER processing_recipes_no_update
BEFORE UPDATE ON processing_recipes
BEGIN
    SELECT RAISE(ABORT, 'ProcessingRecipe rows are immutable');
END;

CREATE TRIGGER processing_recipes_no_delete
BEFORE DELETE ON processing_recipes
BEGIN
    SELECT RAISE(ABORT, 'ProcessingRecipe rows are append-only');
END;

CREATE TRIGGER outputs_no_update
BEFORE UPDATE ON outputs
BEGIN
    SELECT RAISE(ABORT, 'Output rows are immutable');
END;

CREATE TRIGGER outputs_no_delete
BEFORE DELETE ON outputs
BEGIN
    SELECT RAISE(ABORT, 'Output rows are append-only');
END;

CREATE TRIGGER processing_passes_no_update
BEFORE UPDATE ON processing_passes
BEGIN
    SELECT RAISE(ABORT, 'ProcessingPass rows are immutable');
END;

CREATE TRIGGER processing_passes_no_delete
BEFORE DELETE ON processing_passes
BEGIN
    SELECT RAISE(ABORT, 'ProcessingPass rows are append-only');
END;
