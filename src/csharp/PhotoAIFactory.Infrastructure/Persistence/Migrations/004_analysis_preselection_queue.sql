CREATE TABLE jobs (
    job_id TEXT PRIMARY KEY NOT NULL CHECK (length(trim(job_id)) > 0),
    project_id TEXT NOT NULL REFERENCES projects(project_id) ON DELETE RESTRICT,
    photo_id TEXT NOT NULL REFERENCES photos(photo_id) ON DELETE RESTRICT,
    parent_job_id TEXT NULL REFERENCES jobs(job_id) ON DELETE RESTRICT,
    state TEXT NOT NULL CHECK (state IN (
        'RECEIVED','ANALYZING','REVIEW_PRE','REJECTED_PRE','QUEUED','PROCESSING','QA',
        'REVIEW_FINAL','REJECTED_FINAL','COMPLETED','ERROR','CANCEL_REQUESTED','CANCELLED',
        'RETRYING','INTERRUPTED'
    )),
    preselection_config_id TEXT NOT NULL REFERENCES project_config_versions(config_version_id) ON DELETE RESTRICT,
    processing_config_id TEXT NOT NULL REFERENCES project_config_versions(config_version_id) ON DELETE RESTRICT,
    analysis_source_asset_id TEXT NOT NULL REFERENCES assets(asset_id) ON DELETE RESTRICT,
    analysis_source_sha256 TEXT NOT NULL CHECK (length(analysis_source_sha256)=64),
    analysis_input_kind TEXT NOT NULL CHECK (analysis_input_kind IN ('JPEG_CAMERA','JPEG_MASTER','RAW_PREVIEW')),
    analysis_representation_path TEXT NOT NULL CHECK (length(trim(analysis_representation_path)) > 0),
    technical_retry_count INTEGER NOT NULL DEFAULT 0 CHECK (technical_retry_count BETWEEN 0 AND 2),
    quality_reprocess_count INTEGER NOT NULL DEFAULT 0 CHECK (quality_reprocess_count BETWEEN 0 AND 1),
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);

CREATE UNIQUE INDEX ux_jobs_initial_photo
ON jobs(photo_id)
WHERE parent_job_id IS NULL;

CREATE INDEX ix_jobs_project_state ON jobs(project_id, state);

CREATE TABLE job_state_transitions (
    transition_id TEXT PRIMARY KEY NOT NULL CHECK (length(trim(transition_id)) > 0),
    job_id TEXT NOT NULL REFERENCES jobs(job_id) ON DELETE RESTRICT,
    from_state TEXT NULL,
    to_state TEXT NOT NULL,
    reason TEXT NOT NULL CHECK (length(trim(reason)) > 0),
    operation_id TEXT NOT NULL CHECK (length(trim(operation_id)) > 0),
    occurred_at_utc TEXT NOT NULL,
    UNIQUE(job_id, operation_id)
);

CREATE INDEX ix_job_state_transitions_job ON job_state_transitions(job_id, occurred_at_utc);

CREATE TABLE analysis_results (
    analysis_id TEXT PRIMARY KEY NOT NULL CHECK (length(trim(analysis_id)) > 0),
    job_id TEXT NOT NULL UNIQUE REFERENCES jobs(job_id) ON DELETE RESTRICT,
    schema_version INTEGER NOT NULL CHECK (schema_version > 0),
    result_json TEXT NOT NULL CHECK (length(trim(result_json)) > 2),
    created_at_utc TEXT NOT NULL
);

CREATE TABLE model_executions (
    model_execution_id TEXT PRIMARY KEY NOT NULL CHECK (length(trim(model_execution_id)) > 0),
    job_id TEXT NOT NULL REFERENCES jobs(job_id) ON DELETE RESTRICT,
    stage TEXT NOT NULL CHECK (length(trim(stage)) > 0),
    model_id TEXT NOT NULL CHECK (length(trim(model_id)) > 0),
    model_version TEXT NOT NULL CHECK (length(trim(model_version)) > 0),
    artifact_set_sha256 TEXT NULL CHECK (artifact_set_sha256 IS NULL OR length(artifact_set_sha256)=64),
    parameters_json TEXT NOT NULL CHECK (length(trim(parameters_json)) > 1),
    timings_json TEXT NOT NULL CHECK (length(trim(timings_json)) > 1),
    created_at_utc TEXT NOT NULL
);

CREATE INDEX ix_model_executions_job ON model_executions(job_id);

CREATE TABLE preselection_results (
    preselection_id TEXT PRIMARY KEY NOT NULL CHECK (length(trim(preselection_id)) > 0),
    job_id TEXT NOT NULL UNIQUE REFERENCES jobs(job_id) ON DELETE RESTRICT,
    decision TEXT NOT NULL CHECK (decision IN ('APPROVED','REVIEW_PRE','REJECTED_PRE')),
    findings_json TEXT NOT NULL CHECK (length(trim(findings_json)) > 1),
    created_at_utc TEXT NOT NULL
);

CREATE TABLE job_checkpoints (
    checkpoint_id TEXT PRIMARY KEY NOT NULL CHECK (length(trim(checkpoint_id)) > 0),
    job_id TEXT NOT NULL REFERENCES jobs(job_id) ON DELETE RESTRICT,
    stage_name TEXT NOT NULL CHECK (stage_name IN ('ANALYSIS_COMPLETE','PRESELECTION_COMPLETE')),
    attempt_id TEXT NOT NULL CHECK (length(trim(attempt_id)) > 0),
    input_fingerprint TEXT NOT NULL CHECK (length(trim(input_fingerprint)) > 0),
    created_at_utc TEXT NOT NULL,
    UNIQUE(job_id, stage_name)
);

CREATE TABLE queue_entries (
    queue_entry_id TEXT PRIMARY KEY NOT NULL CHECK (length(trim(queue_entry_id)) > 0),
    project_id TEXT NOT NULL REFERENCES projects(project_id) ON DELETE RESTRICT,
    job_id TEXT NOT NULL UNIQUE REFERENCES jobs(job_id) ON DELETE RESTRICT,
    sequence_number INTEGER NOT NULL CHECK (sequence_number > 0),
    process_next INTEGER NOT NULL DEFAULT 0 CHECK (process_next IN (0,1)),
    enqueued_at_utc TEXT NOT NULL,
    process_next_requested_at_utc TEXT NULL
);

CREATE UNIQUE INDEX ux_queue_project_sequence ON queue_entries(project_id, sequence_number);
CREATE INDEX ix_queue_dispatch
ON queue_entries(project_id, process_next DESC, process_next_requested_at_utc, sequence_number);

CREATE TRIGGER job_state_transitions_no_update
BEFORE UPDATE ON job_state_transitions
BEGIN
    SELECT RAISE(ABORT, 'Job state audit is immutable');
END;

CREATE TRIGGER job_state_transitions_no_delete
BEFORE DELETE ON job_state_transitions
BEGIN
    SELECT RAISE(ABORT, 'Job state audit is append-only');
END;

CREATE TRIGGER analysis_results_no_update
BEFORE UPDATE ON analysis_results
BEGIN
    SELECT RAISE(ABORT, 'Analysis rows are immutable');
END;

CREATE TRIGGER preselection_results_no_update
BEFORE UPDATE ON preselection_results
BEGIN
    SELECT RAISE(ABORT, 'Preselection rows are immutable');
END;

CREATE TRIGGER model_executions_no_update
BEFORE UPDATE ON model_executions
BEGIN
    SELECT RAISE(ABORT, 'ModelExecution rows are immutable');
END;

CREATE TRIGGER job_checkpoints_no_update
BEFORE UPDATE ON job_checkpoints
BEGIN
    SELECT RAISE(ABORT, 'Checkpoint rows are immutable');
END;
