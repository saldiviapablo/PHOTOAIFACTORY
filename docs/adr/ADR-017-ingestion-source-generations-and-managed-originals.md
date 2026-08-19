# ADR-017 — Ingestion source generations and managed originals

**Status:** Accepted — validated on the reference Windows PC (2026-08-19).

**Baseline:** `16624475007b099d4d43065b961ade9c42fb551f`

## Context

Phase 2 must promote the behavior proven by ING-01 without copying its temporary
store/schema. The SRS requires watcher + reconciliation, stable-file protection,
RAW/JPEG association, exact duplicate protection and `COPY_TO_PROJECT`.

A changed input folder must never pair new files with pending files from the
previous source.

## Decision

1. Keep `FileSystemWatcher` as a low-latency signal only. Reconciliation is the
   durable recovery mechanism.
2. Use a bounded in-memory notification channel. Losing an event to pressure marks
   reconciliation required; the in-memory channel is never durable state.
3. Persist `ingestion_sources`. A new source generation is created when the watched
   root/include-subfolders identity changes.
4. Refuse activation of a new source generation while the previous source has
   unresolved RAW/JPEG associations. The user/system must explicitly finalize them.
5. Pair files inside one source generation using normalized relative directory +
   filename stem, plus the configured association window.
6. Persist `Photo` and `Asset` in production SQLite. Exact content duplicates are
   project-wide by SHA-256 and do not create a second Photo.
7. Managed originals use content-addressed names below:
   `<OUTPUT>/.photo-ai-factory/originals/RAW|JPEG_CAMERA`.
   Copy uses a unique `.partial-*` staging file, validation, SHA-256, and rename.
8. An Asset becomes durable only after the managed copy has been validated.
9. V1 ARW admission is conservative. The A7 IV gate-derived TIFF-dimension classifier
   allows demonstrated full-size files; reduced/unknown variants route to
   `REVIEW_UNSUPPORTED_FORMAT`. It is not a general RAW decoder.
10. Late RAW may replace a finalized JPEG master while no production Job subsystem
    exists. The "do not mutate an active Job" branch remains pending until Jobs exist.

## Consequences

- Watcher overflow or notification pressure cannot be the sole cause of missed files.
- A folder change cannot accidentally cross-pair photos.
- SQLite remains the C#-owned source of truth.
- Managed copies may exist without a DB reference after a DB failure; because they
  are content-addressed and validated, retry safely reuses them.
- Phase 3/queue must complete late-RAW semantics once Job state is durable.

## Alternatives rejected

- Copy ING-01's temporary store/schema directly.
- Treat FileSystemWatcher as reliable queue storage.
- Use an unbounded notification channel.
- Pair only by basename across all project history.
- Process unknown/reduced ARW optimistically through Darktable.
