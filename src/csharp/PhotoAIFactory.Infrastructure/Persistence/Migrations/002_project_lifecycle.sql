ALTER TABLE projects ADD COLUMN project_state TEXT NOT NULL DEFAULT 'STOPPED'
    CHECK (project_state IN (
        'RUNNING', 'PAUSE_REQUESTED', 'PAUSED', 'STOP_REQUESTED', 'STOPPED',
        'BLOCKED_STORAGE', 'COMPONENT_UNHEALTHY'));

ALTER TABLE projects ADD COLUMN state_revision INTEGER NOT NULL DEFAULT 0
    CHECK (state_revision >= 0);

ALTER TABLE projects ADD COLUMN state_changed_at_utc TEXT NOT NULL
    DEFAULT '1970-01-01T00:00:00.0000000+00:00';

UPDATE projects
SET project_state = 'STOPPED',
    state_revision = 0,
    state_changed_at_utc = updated_at_utc;

CREATE TABLE project_state_transitions (
    transition_id TEXT PRIMARY KEY NOT NULL CHECK (length(trim(transition_id)) > 0),
    project_id TEXT NOT NULL,
    from_state TEXT NOT NULL CHECK (from_state IN (
        'RUNNING', 'PAUSE_REQUESTED', 'PAUSED', 'STOP_REQUESTED', 'STOPPED',
        'BLOCKED_STORAGE', 'COMPONENT_UNHEALTHY')),
    to_state TEXT NOT NULL CHECK (to_state IN (
        'RUNNING', 'PAUSE_REQUESTED', 'PAUSED', 'STOP_REQUESTED', 'STOPPED',
        'BLOCKED_STORAGE', 'COMPONENT_UNHEALTHY')),
    reason TEXT NOT NULL CHECK (length(trim(reason)) > 0),
    occurred_at_utc TEXT NOT NULL,
    state_revision INTEGER NOT NULL CHECK (state_revision >= 0),
    operation_id TEXT NOT NULL CHECK (length(trim(operation_id)) > 0),
    CONSTRAINT fk_transition_project FOREIGN KEY (project_id) REFERENCES projects(project_id) ON DELETE RESTRICT,
    CONSTRAINT uq_transition_project_operation UNIQUE (project_id, operation_id),
    CONSTRAINT uq_transition_project_revision UNIQUE (project_id, state_revision)
);

INSERT INTO project_state_transitions(
    transition_id, project_id, from_state, to_state, reason,
    occurred_at_utc, state_revision, operation_id)
SELECT
    'migration002-' || project_id,
    project_id,
    'STOPPED',
    'STOPPED',
    'MIGRATION_002_BACKFILL',
    state_changed_at_utc,
    0,
    'migration-002-backfill:' || project_id
FROM projects;

CREATE TRIGGER project_state_transitions_no_update
BEFORE UPDATE ON project_state_transitions
BEGIN
    SELECT RAISE(ABORT, 'Project state audit is immutable');
END;

CREATE TRIGGER project_state_transitions_no_delete
BEFORE DELETE ON project_state_transitions
BEGIN
    SELECT RAISE(ABORT, 'Project state audit is append-only');
END;
