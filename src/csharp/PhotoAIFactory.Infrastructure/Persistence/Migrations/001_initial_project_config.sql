CREATE TABLE projects (
    project_id TEXT PRIMARY KEY NOT NULL CHECK (length(trim(project_id)) > 0),
    name TEXT NOT NULL CHECK (length(trim(name)) > 0),
    creation_operation_key TEXT NOT NULL UNIQUE CHECK (length(trim(creation_operation_key)) > 0),
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);

CREATE TABLE project_config_versions (
    config_version_id TEXT PRIMARY KEY NOT NULL CHECK (length(trim(config_version_id)) > 0),
    project_id TEXT NOT NULL,
    version_number INTEGER NOT NULL CHECK (version_number > 0),
    schema_version INTEGER NOT NULL CHECK (schema_version > 0),
    config_json TEXT NOT NULL CHECK (length(trim(config_json)) > 2),
    config_sha256 TEXT NOT NULL CHECK (length(config_sha256) = 64),
    operation_key TEXT NOT NULL CHECK (length(trim(operation_key)) > 0),
    created_at_utc TEXT NOT NULL,
    CONSTRAINT fk_config_project FOREIGN KEY (project_id) REFERENCES projects(project_id) ON DELETE RESTRICT,
    CONSTRAINT uq_config_project_version UNIQUE (project_id, version_number),
    CONSTRAINT uq_config_project_operation UNIQUE (project_id, operation_key)
);

CREATE TRIGGER project_config_versions_no_update
BEFORE UPDATE ON project_config_versions
BEGIN
    SELECT RAISE(ABORT, 'ConfigVersion rows are immutable');
END;

CREATE TRIGGER project_config_versions_no_delete
BEFORE DELETE ON project_config_versions
BEGIN
    SELECT RAISE(ABORT, 'ConfigVersion rows are append-only');
END;
