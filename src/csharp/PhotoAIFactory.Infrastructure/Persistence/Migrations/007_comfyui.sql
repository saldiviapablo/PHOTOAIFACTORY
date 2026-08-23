DROP TRIGGER job_checkpoints_no_update;
DROP TRIGGER job_checkpoints_no_delete;

ALTER TABLE job_checkpoints RENAME TO job_checkpoints_v6;
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
            'DARKTABLE_PASS2_COMPLETE',
            'COMFYUI_COMPLETE'
        )
    ),
    attempt_id TEXT NOT NULL CHECK (length(trim(attempt_id)) > 0),
    input_fingerprint TEXT NOT NULL CHECK (length(trim(input_fingerprint)) > 0),
    created_at_utc TEXT NOT NULL,
    UNIQUE(job_id, stage_name)
);
INSERT INTO job_checkpoints(
    checkpoint_id, job_id, stage_name, attempt_id, input_fingerprint, created_at_utc)
SELECT
    checkpoint_id, job_id, stage_name, attempt_id, input_fingerprint, created_at_utc
FROM job_checkpoints_v6;
DROP TABLE job_checkpoints_v6;

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

ALTER TABLE jobs
ADD COLUMN comfy_retry_count INTEGER NOT NULL DEFAULT 0
CHECK (comfy_retry_count BETWEEN 0 AND 2);

CREATE TABLE comfy_plans (
    comfy_plan_id TEXT PRIMARY KEY NOT NULL CHECK (length(trim(comfy_plan_id)) > 0),
    job_id TEXT NOT NULL UNIQUE REFERENCES jobs(job_id) ON DELETE RESTRICT,
    schema_version INTEGER NOT NULL CHECK (schema_version > 0),
    mode TEXT NOT NULL CHECK (mode IN ('OFF','ON','AUTO')),
    plan_json TEXT NOT NULL CHECK (length(trim(plan_json)) > 2),
    plan_sha256 TEXT NOT NULL CHECK (length(plan_sha256)=64),
    created_at_utc TEXT NOT NULL
);

CREATE TABLE comfy_executions (
    comfy_execution_id TEXT PRIMARY KEY NOT NULL CHECK (length(trim(comfy_execution_id)) > 0),
    job_id TEXT NOT NULL UNIQUE REFERENCES jobs(job_id) ON DELETE RESTRICT,
    attempt_id TEXT NOT NULL CHECK (length(trim(attempt_id)) > 0),
    status TEXT NOT NULL CHECK (status IN ('SKIPPED','COMPLETED')),
    input_path TEXT NOT NULL CHECK (length(trim(input_path)) > 0),
    input_sha256 TEXT NOT NULL CHECK (length(input_sha256)=64),
    output_path TEXT NOT NULL CHECK (length(trim(output_path)) > 0),
    output_sha256 TEXT NOT NULL CHECK (length(output_sha256)=64),
    output_size_bytes INTEGER NOT NULL CHECK (output_size_bytes > 0),
    task_manifest_json TEXT NOT NULL CHECK (length(trim(task_manifest_json)) > 1),
    workflow_manifest_json TEXT NOT NULL CHECK (length(trim(workflow_manifest_json)) > 1),
    prompt_ids_json TEXT NOT NULL CHECK (length(trim(prompt_ids_json)) > 1),
    history_path TEXT NOT NULL CHECK (length(trim(history_path)) > 0),
    completed_at_utc TEXT NOT NULL
);

CREATE TRIGGER comfy_plans_no_update
BEFORE UPDATE ON comfy_plans
BEGIN
    SELECT RAISE(ABORT, 'ComfyPlan rows are immutable');
END;
CREATE TRIGGER comfy_plans_no_delete
BEFORE DELETE ON comfy_plans
BEGIN
    SELECT RAISE(ABORT, 'ComfyPlan rows are append-only');
END;
CREATE TRIGGER comfy_executions_no_update
BEFORE UPDATE ON comfy_executions
BEGIN
    SELECT RAISE(ABORT, 'ComfyExecution rows are immutable');
END;
CREATE TRIGGER comfy_executions_no_delete
BEFORE DELETE ON comfy_executions
BEGIN
    SELECT RAISE(ABORT, 'ComfyExecution rows are append-only');
END;
