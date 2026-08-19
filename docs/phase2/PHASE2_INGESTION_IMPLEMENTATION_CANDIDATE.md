# PHOTO AI FACTORY — Phase 2 Ingestion Implementation Candidate

**Baseline commit:** `16624475007b099d4d43065b961ade9c42fb551f`

**Status:** VALIDATED / CLOSED — Windows reference-PC audit passed on 2026-08-19.

## Implemented

- production `Photo` / `Asset` ingestion domain model;
- migration `003_ingestion`;
- C#-only SQLite ingestion store sharing the existing single-writer boundary;
- source generations that prevent pairing across input-folder changes;
- explicit pending-association resolution;
- `.ARW`, `.JPG`, `.JPEG` case-insensitive admission;
- stable-file probe;
- SHA-256 exact duplicate protection;
- RAW/JPEG pairing;
- RAW-only and JPEG-only finalization;
- late RAW before production Jobs exist;
- conservative reduced/unknown ARW review route;
- validated `COPY_TO_PROJECT`;
- bounded watcher notification channel;
- startup + periodic reconciliation;
- watcher-error/channel-pressure reconciliation recovery;
- simulation/integration tests with real SQLite and temporary filesystem.

## Requirement status intended after audit

Functional for Phase 2 scope:
- FR-FS-003/004/009
- FR-ING-001/002/004/006/007/008
- FR-ORG-001/003/004/005/006/007/008

Partial:
- FR-FS-002: watcher/session runtime is implemented and requires Project `RUNNING`,
  but the final App/lifecycle composition that starts/stops it automatically is not
  yet present. Do not overstate this requirement as fully closed.
- FR-ING-003: metadata-compatible association hook is not yet a full EXIF metadata
  parser; basename + origin generation + relative origin + time window are implemented.
- FR-ING-005: late RAW before a Job exists is implemented. Active-Job immutability
  awaits production Jobs/queue.
- FR-FS-008: activation is blocked while old associations are pending and explicit
  resolution exists; UI presentation arrives later.

Not part of Phase 2:
- visual similarity (FR-ING-009) belongs to analysis/embeddings.

## Reference Windows PC validation

Completed with PASS:
- all existing C# tests;
- all new Phase 2 tests;
- Python regression tests;
- migration upgrade from a real migration-002 fixture;
- actual NTFS watcher + reconciliation scenarios;
- slow copy / locked file;
- missed-event recovery;
- Unicode/spaces/subfolders;
- managed-original hash checks;
- source-file hash before/after;
- no `.partial-*` leftovers;
- restart reconciliation;
- stress burst;
- SQLite integrity/WAL/FULL/FK/single-writer;
- zero external AI/Darktable/ComfyUI process launches.

The audit found and minimally corrected two integration defects: use of the actual
`ConfigVersion.ReadConfig()` API, and starvation during bounded-channel recovery.
The latter was reproduced with channel capacity 16 and an 80-file burst, then
verified at 82/82 assets across reconciliation and restart. Full details are in
`PHASE2_INGESTION_REPORT.md`.

Decision: `PHASE 2 — INGESTION = CLOSED / GO`.
