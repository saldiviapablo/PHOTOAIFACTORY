DROP TRIGGER job_checkpoints_no_update;
DROP TRIGGER job_checkpoints_no_delete;

ALTER TABLE job_checkpoints RENAME TO job_checkpoints_v5;

CREATE TABLE job_checkpoints (
    checkpoint_id TEXT PRIMARY KEY NOT NULL CHECK (length(trim(checkpoint_id)) > 0),
    job_id TEXT NOT NULL REFERENCES jobs(job_id) ON DELETE RESTRICT,
    stage_name TEXT NOT NULL CHECK (
        stage_name IN (
            'ANALYSIS_COMPLETE',
            'PRESELECTION_COMPLETE',
            'BASIC_REVEAL_COMPLETE',
            'DARKTABLE_PASS1_COMPLETE',
            'FEEDBACK_INSPECTION_COMPLETE',
            'RAW_DENOISE_COMPLETE',
            'DARKTABLE_PASS2_COMPLETE'
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
FROM job_checkpoints_v5;

DROP TABLE job_checkpoints_v5;

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

CREATE TABLE feedback_passes (
    feedback_pass_id TEXT PRIMARY KEY NOT NULL CHECK (length(trim(feedback_pass_id)) > 0),
    job_id TEXT NOT NULL REFERENCES jobs(job_id) ON DELETE RESTRICT,
    pass_number INTEGER NOT NULL CHECK (pass_number IN (1,2)),
    attempt_id TEXT NOT NULL CHECK (length(trim(attempt_id)) > 0),
    input_asset_id TEXT NOT NULL REFERENCES assets(asset_id) ON DELETE RESTRICT,
    input_sha256 TEXT NOT NULL CHECK (length(input_sha256)=64),
    input_kind TEXT NOT NULL CHECK (input_kind IN ('RAW','JPEG')),
    darktable_version TEXT NOT NULL CHECK (length(trim(darktable_version)) > 0),
    control_plan_json TEXT NOT NULL CHECK (length(trim(control_plan_json)) > 2),
    image_path TEXT NOT NULL CHECK (length(trim(image_path)) > 0),
    image_sha256 TEXT NOT NULL CHECK (length(image_sha256)=64),
    image_size_bytes INTEGER NOT NULL CHECK (image_size_bytes > 0),
    image_width INTEGER NOT NULL CHECK (image_width > 0),
    image_height INTEGER NOT NULL CHECK (image_height > 0),
    bits_per_sample INTEGER NOT NULL CHECK (bits_per_sample IN (8,16)),
    channels INTEGER NOT NULL CHECK (channels IN (3,4)),
    xmp_path TEXT NOT NULL CHECK (length(trim(xmp_path)) > 0),
    xmp_sha256 TEXT NOT NULL CHECK (length(xmp_sha256)=64),
    history_path TEXT NULL,
    completed_at_utc TEXT NOT NULL,
    UNIQUE(job_id, pass_number)
);

CREATE INDEX ix_feedback_passes_job
ON feedback_passes(job_id, pass_number);

CREATE TABLE feedback_inspections (
    feedback_inspection_id TEXT PRIMARY KEY NOT NULL CHECK (length(trim(feedback_inspection_id)) > 0),
    job_id TEXT NOT NULL UNIQUE REFERENCES jobs(job_id) ON DELETE RESTRICT,
    schema_version INTEGER NOT NULL CHECK (schema_version > 0),
    recipe_json TEXT NOT NULL CHECK (length(trim(recipe_json)) > 2),
    recipe_sha256 TEXT NOT NULL CHECK (length(recipe_sha256)=64),
    inspection_json TEXT NOT NULL CHECK (length(trim(inspection_json)) > 2),
    completed_at_utc TEXT NOT NULL
);

CREATE TRIGGER feedback_passes_no_update
BEFORE UPDATE ON feedback_passes
BEGIN
    SELECT RAISE(ABORT, 'FeedbackPass rows are immutable');
END;

CREATE TRIGGER feedback_passes_no_delete
BEFORE DELETE ON feedback_passes
BEGIN
    SELECT RAISE(ABORT, 'FeedbackPass rows are append-only');
END;

CREATE TRIGGER feedback_inspections_no_update
BEFORE UPDATE ON feedback_inspections
BEGIN
    SELECT RAISE(ABORT, 'FeedbackInspection rows are immutable');
END;

CREATE TRIGGER feedback_inspections_no_delete
BEFORE DELETE ON feedback_inspections
BEGIN
    SELECT RAISE(ABORT, 'FeedbackInspection rows are append-only');
END;
