-- Migration 008: QA Results, Review Items, Publications, and Checkpoints expansion

CREATE TABLE job_checkpoints_new (
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
            'COMFYUI_COMPLETE',
            'QA_COMPLETE',
            'OUTPUT_PUBLISHED'
        )
    ),
    attempt_id TEXT NOT NULL CHECK (length(trim(attempt_id)) > 0),
    input_fingerprint TEXT NOT NULL CHECK (length(trim(input_fingerprint)) > 0),
    created_at_utc TEXT NOT NULL,
    UNIQUE(job_id, stage_name)
);

INSERT INTO job_checkpoints_new(
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
FROM job_checkpoints;

DROP TRIGGER job_checkpoints_no_update;
DROP TRIGGER job_checkpoints_no_delete;

DROP TABLE job_checkpoints;

ALTER TABLE job_checkpoints_new RENAME TO job_checkpoints;

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

CREATE TABLE qa_results (
    qa_result_id TEXT PRIMARY KEY NOT NULL CHECK (length(trim(qa_result_id)) > 0),
    job_id TEXT NOT NULL UNIQUE REFERENCES jobs(job_id) ON DELETE RESTRICT,
    attempt_id TEXT NOT NULL CHECK (length(trim(attempt_id)) > 0),
    decision TEXT NOT NULL CHECK (decision IN ('PASS','REVIEW','REPROCESS','TECH_RETRY','FATAL','QA_PASS','QA_REVIEW','QA_REPROCESS','QA_TECH_RETRY','QA_FATAL')),
    result_json TEXT NOT NULL CHECK (length(trim(result_json)) > 2),
    input_path TEXT NOT NULL CHECK (length(trim(input_path)) > 0),
    input_sha256 TEXT NOT NULL CHECK (length(input_sha256) = 64),
    created_at_utc TEXT NOT NULL
);

CREATE TRIGGER qa_results_no_update
BEFORE UPDATE ON qa_results
BEGIN
    SELECT RAISE(ABORT, 'QaResult rows are immutable');
END;

CREATE TRIGGER qa_results_no_delete
BEFORE DELETE ON qa_results
BEGIN
    SELECT RAISE(ABORT, 'QaResult rows are append-only');
END;

CREATE TABLE review_items (
    review_item_id TEXT PRIMARY KEY NOT NULL CHECK (length(trim(review_item_id)) > 0),
    job_id TEXT NOT NULL REFERENCES jobs(job_id) ON DELETE RESTRICT,
    review_kind TEXT NOT NULL CHECK (review_kind IN ('PRE','FINAL')),
    status TEXT NOT NULL CHECK (status IN ('PENDING','RESOLVED')),
    created_at_utc TEXT NOT NULL,
    resolved_at_utc TEXT NULL,
    resolution TEXT NULL CHECK (resolution IS NULL OR resolution IN ('APPROVED','REJECTED','REPROCESS')),
    resolution_operation_id TEXT NULL CHECK (resolution_operation_id IS NULL OR length(trim(resolution_operation_id)) > 0),
    CHECK (
        (status = 'PENDING' AND resolved_at_utc IS NULL AND resolution IS NULL AND resolution_operation_id IS NULL)
        OR
        (status = 'RESOLVED' AND resolved_at_utc IS NOT NULL AND resolution IS NOT NULL AND resolution_operation_id IS NOT NULL)
    )
);

CREATE UNIQUE INDEX ux_review_items_pending
ON review_items(job_id, review_kind)
WHERE status = 'PENDING';

CREATE TRIGGER review_items_no_delete
BEFORE DELETE ON review_items
BEGIN
    SELECT RAISE(ABORT, 'Review items are append-only');
END;

CREATE TRIGGER review_items_lifecycle_update
BEFORE UPDATE ON review_items
BEGIN
    SELECT RAISE(ABORT, 'Resolved review items cannot be modified')
    WHERE OLD.status = 'RESOLVED';

    SELECT RAISE(ABORT, 'Review items can only transition from PENDING to RESOLVED')
    WHERE OLD.status = 'PENDING' AND NEW.status != 'RESOLVED';

    SELECT RAISE(ABORT, 'Review item identity and creation metadata are immutable')
    WHERE NEW.review_item_id != OLD.review_item_id
       OR NEW.job_id != OLD.job_id
       OR NEW.review_kind != OLD.review_kind
       OR NEW.created_at_utc != OLD.created_at_utc;
END;

CREATE TABLE publications (
    publication_id TEXT PRIMARY KEY NOT NULL CHECK (length(trim(publication_id)) > 0),
    job_id TEXT NOT NULL UNIQUE REFERENCES jobs(job_id) ON DELETE RESTRICT,
    attempt_id TEXT NOT NULL CHECK (length(trim(attempt_id)) > 0),
    destination_kind TEXT NOT NULL CHECK (destination_kind IN ('FINAL','REVIEW','REJECTED')),
    destination_path TEXT NOT NULL CHECK (length(trim(destination_path)) > 0),
    sha256 TEXT NOT NULL CHECK (length(sha256) = 64),
    size_bytes INTEGER NOT NULL CHECK (size_bytes > 0),
    width INTEGER NOT NULL CHECK (width > 0),
    height INTEGER NOT NULL CHECK (height > 0),
    history_path TEXT NOT NULL CHECK (length(trim(history_path)) > 0),
    published_at_utc TEXT NOT NULL
);

CREATE INDEX ix_publications_job
ON publications(job_id);

CREATE TRIGGER publications_no_update
BEFORE UPDATE ON publications
BEGIN
    SELECT RAISE(ABORT, 'Publication rows are immutable');
END;

CREATE TRIGGER publications_no_delete
BEFORE DELETE ON publications
BEGIN
    SELECT RAISE(ABORT, 'Publication rows are append-only');
END;
