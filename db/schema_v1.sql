PRAGMA foreign_keys = ON;
PRAGMA journal_mode = WAL;
PRAGMA synchronous = FULL;

CREATE TABLE IF NOT EXISTS projects (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    state TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    input_path TEXT NOT NULL,
    output_path TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS project_config_versions (
    id TEXT PRIMARY KEY,
    project_id TEXT NOT NULL REFERENCES projects(id),
    version_no INTEGER NOT NULL,
    config_hash TEXT NOT NULL,
    config_json TEXT NOT NULL,
    created_at TEXT NOT NULL,
    UNIQUE(project_id, version_no),
    UNIQUE(project_id, config_hash)
);

CREATE TABLE IF NOT EXISTS photos (
    id TEXT PRIMARY KEY,
    project_id TEXT NOT NULL REFERENCES projects(id),
    capture_key TEXT,
    created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS assets (
    id TEXT PRIMARY KEY,
    photo_id TEXT NOT NULL REFERENCES photos(id),
    kind TEXT NOT NULL,
    source_path TEXT NOT NULL,
    managed_path TEXT,
    byte_size INTEGER,
    sha256 TEXT,
    archive_state TEXT NOT NULL,
    created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_assets_photo ON assets(photo_id);
CREATE INDEX IF NOT EXISTS ix_assets_sha256 ON assets(sha256);

CREATE TABLE IF NOT EXISTS jobs (
    id TEXT PRIMARY KEY,
    photo_id TEXT NOT NULL REFERENCES photos(id),
    parent_job_id TEXT REFERENCES jobs(id),
    state TEXT NOT NULL,
    reveal_mode TEXT NOT NULL,
    preselection_config_id TEXT REFERENCES project_config_versions(id),
    processing_config_id TEXT NOT NULL REFERENCES project_config_versions(id),
    queue_seq INTEGER,
    process_next INTEGER NOT NULL DEFAULT 0,
    quality_reprocess_count INTEGER NOT NULL DEFAULT 0,
    technical_retry_count INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    started_at TEXT,
    finished_at TEXT,
    last_error_code TEXT
);

CREATE INDEX IF NOT EXISTS ix_jobs_queue ON jobs(state, process_next DESC, queue_seq);

CREATE TABLE IF NOT EXISTS job_stage_attempts (
    id TEXT PRIMARY KEY,
    job_id TEXT NOT NULL REFERENCES jobs(id),
    stage TEXT NOT NULL,
    attempt_no INTEGER NOT NULL,
    state TEXT NOT NULL,
    started_at TEXT NOT NULL,
    finished_at TEXT,
    result_json TEXT,
    error_json TEXT,
    UNIQUE(job_id, stage, attempt_no)
);

CREATE TABLE IF NOT EXISTS checkpoints (
    id TEXT PRIMARY KEY,
    job_id TEXT NOT NULL REFERENCES jobs(id),
    stage TEXT NOT NULL,
    attempt_id TEXT NOT NULL REFERENCES job_stage_attempts(id),
    artifact_manifest_json TEXT NOT NULL,
    created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_checkpoints_job ON checkpoints(job_id, created_at);

CREATE TABLE IF NOT EXISTS analyses (
    id TEXT PRIMARY KEY,
    job_id TEXT NOT NULL REFERENCES jobs(id),
    kind TEXT NOT NULL,
    schema_version INTEGER NOT NULL,
    result_json TEXT NOT NULL,
    created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS processing_passes (
    id TEXT PRIMARY KEY,
    job_id TEXT NOT NULL REFERENCES jobs(id),
    pass_type TEXT NOT NULL,
    recipe_json TEXT,
    xmp_path TEXT,
    result_json TEXT,
    created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS model_executions (
    id TEXT PRIMARY KEY,
    job_id TEXT NOT NULL REFERENCES jobs(id),
    stage TEXT NOT NULL,
    provider TEXT NOT NULL,
    model_id TEXT NOT NULL,
    model_version TEXT,
    model_sha256 TEXT,
    parameters_json TEXT,
    duration_ms INTEGER,
    created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS outputs (
    id TEXT PRIMARY KEY,
    job_id TEXT NOT NULL REFERENCES jobs(id),
    kind TEXT NOT NULL,
    path TEXT NOT NULL,
    sha256 TEXT,
    byte_size INTEGER,
    is_temporary INTEGER NOT NULL,
    is_validated INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS review_items (
    id TEXT PRIMARY KEY,
    job_id TEXT NOT NULL REFERENCES jobs(id),
    review_type TEXT NOT NULL,
    reason_json TEXT NOT NULL,
    state TEXT NOT NULL,
    created_at TEXT NOT NULL,
    resolved_at TEXT,
    resolution_json TEXT
);

CREATE TABLE IF NOT EXISTS event_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    project_id TEXT NOT NULL,
    photo_id TEXT,
    job_id TEXT,
    attempt_id TEXT,
    level TEXT NOT NULL,
    component TEXT NOT NULL,
    event_type TEXT NOT NULL,
    payload_json TEXT,
    created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_event_job ON event_log(job_id, created_at);
