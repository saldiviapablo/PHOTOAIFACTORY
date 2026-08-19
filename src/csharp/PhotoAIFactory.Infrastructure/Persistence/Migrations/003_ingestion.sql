CREATE TABLE ingestion_sources (
    source_id TEXT PRIMARY KEY NOT NULL,
    project_id TEXT NOT NULL REFERENCES projects(project_id) ON DELETE RESTRICT,
    input_root TEXT NOT NULL CHECK (length(trim(input_root)) > 0),
    include_subfolders INTEGER NOT NULL CHECK (include_subfolders IN (0,1)),
    config_version_id TEXT NOT NULL REFERENCES project_config_versions(config_version_id) ON DELETE RESTRICT,
    created_at_utc TEXT NOT NULL,
    closed_at_utc TEXT NULL
);

CREATE TABLE photos (
    photo_id TEXT PRIMARY KEY NOT NULL,
    project_id TEXT NOT NULL REFERENCES projects(project_id) ON DELETE RESTRICT,
    source_id TEXT NOT NULL REFERENCES ingestion_sources(source_id) ON DELETE RESTRICT,
    association_key TEXT NOT NULL CHECK (length(trim(association_key)) > 0),
    state TEXT NOT NULL CHECK (
        state IN ('WAITING_FOR_ASSOCIATION','READY_FOR_ANALYSIS','REVIEW_UNSUPPORTED_FORMAT')
    ),
    master_asset_id TEXT NULL,
    master_format TEXT NULL CHECK (master_format IS NULL OR master_format IN ('RAW','JPEG')),
    association_deadline_utc TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    UNIQUE(project_id, source_id, association_key)
);

CREATE TABLE assets (
    asset_id TEXT PRIMARY KEY NOT NULL,
    project_id TEXT NOT NULL REFERENCES projects(project_id) ON DELETE RESTRICT,
    photo_id TEXT NOT NULL REFERENCES photos(photo_id) ON DELETE RESTRICT,
    source_id TEXT NOT NULL REFERENCES ingestion_sources(source_id) ON DELETE RESTRICT,
    source_path TEXT NOT NULL CHECK (length(trim(source_path)) > 0),
    source_relative_path TEXT NOT NULL CHECK (length(trim(source_relative_path)) > 0),
    managed_path TEXT NOT NULL CHECK (length(trim(managed_path)) > 0),
    format TEXT NOT NULL CHECK (format IN ('RAW','JPEG')),
    role TEXT NOT NULL CHECK (role IN ('RAW_ORIGINAL','JPEG_PENDING','JPEG_CAMERA','JPEG_MASTER')),
    archive_state TEXT NOT NULL CHECK (archive_state='ARCHIVED'),
    size_bytes INTEGER NOT NULL CHECK (size_bytes >= 0),
    sha256 TEXT NOT NULL CHECK (length(sha256)=64),
    raw_support_status TEXT NOT NULL CHECK (
        raw_support_status IN ('NOT_APPLICABLE','SUPPORTED_FULL_SIZE','UNSUPPORTED_REDUCED','UNKNOWN')
    ),
    raw_max_width INTEGER NOT NULL CHECK (raw_max_width >= 0),
    raw_max_height INTEGER NOT NULL CHECK (raw_max_height >= 0),
    raw_classification TEXT NOT NULL,
    observed_at_utc TEXT NOT NULL,
    archived_at_utc TEXT NOT NULL
);

CREATE UNIQUE INDEX ux_assets_project_sha256 ON assets(project_id, sha256);
CREATE INDEX ix_assets_photo ON assets(photo_id);
CREATE INDEX ix_photos_project_state ON photos(project_id, state);
CREATE INDEX ix_photos_source_state ON photos(source_id, state);
CREATE INDEX ix_ingestion_sources_project_created ON ingestion_sources(project_id, created_at_utc);
