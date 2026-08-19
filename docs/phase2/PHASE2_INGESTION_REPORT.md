# PHOTO AI FACTORY — Phase 2 Ingestion Report

**Audit date:** 2026-08-19

**Baseline before package:** `16624475007b099d4d43065b961ade9c42fb551f`

**Result:** PASS

**Decision:** `PHASE 2 — INGESTION = CLOSED / GO`

## Build and automated regression

| Validation | Result |
|---|---|
| `dotnet build src/csharp/PhotoAIFactory.sln -c Release` | PASS — 0 errors, 0 warnings |
| Foundation tests | 112/112 PASS |
| Simulation tests | 24/24 PASS |
| Total C# tests | 136/136 PASS |
| New Phase 2 tests | 17/17 PASS |
| C# self-tests | 6/6 PASS |
| Python tests | 4/4 PASS with the known `StarletteDeprecationWarning` |
| Isolated Python | `C:\Users\Pc\AppData\Local\PhotoAIFactory\runtimes\ai-worker\Scripts\python.exe` — 3.12.12 |
| Phase 0 light smoke | PASS |
| NuGet vulnerable package check | PASS — none reported in 8 projects |

## Migration 003

- A new database applied 001, 002 and 003 successfully.
- A database initialized only through migration 002 produced a pre-migration
  backup and then applied 003 exactly once.
- Reopening was idempotent and retained the registered migration checksum.
- A copied database opened with intentionally altered test-only migration 003 SQL
  failed explicitly with `MigrationIntegrityException`; the real migration was not
  modified.
- `PRAGMA integrity_check=ok`, `journal_mode=wal`, `synchronous=FULL` and
  `foreign_keys=ON` were verified after upgrade.

## Windows NTFS audit

The product implementation was exercised exclusively below
`C:\Users\Pc\Documents\PHOTO AI FACTORY TESTS\PHASE2\`, with separate
`INPUT`, `OUTPUT`, `WORK`, `LOGS` and `REPORT` roots.

- `.ARW`, `.arw`, `.JPG`, `.jpg`, `.JPEG` and `.jpeg` were admitted;
  `.PNG`, `.TXT` and `.TMP` were ignored.
- A six-block slow copy emitted repeated filesystem changes but created no durable
  Asset or managed original until the writer closed and the file became stable.
- A file held with `FileShare.None` was not ingested while locked and was ingested
  once after release, within the finite stability policy.
- Watcher detection, explicit missed-event reconciliation, startup reconciliation,
  clean stop and restart all passed. Existing files were not duplicated and the
  file added while stopped was discovered on restart.
- With a bounded channel capacity of 16, an 80-file burst was recovered completely
  by reconciliation. The final restart scenario reached 82/82 durable assets.
- `IncludeSubfolders=true`, identical basenames in different subfolders,
  `IncludeSubfolders=false`, Unicode and spaces all passed.
- JPEG-first and RAW-first pairing produced one Photo and two Assets with RAW as
  master and JPEG as `JPEG_CAMERA`. RAW-only and JPEG-only expiration passed.
- A late RAW replaced a finalized JPEG-only master on the same Photo. The active-Job
  immutability branch remains intentionally pending until production Jobs exist.
- Exact byte duplicates across different paths/names did not create another Photo
  or Asset; byte-different files remained distinct.
- Source-generation activation was blocked by a pending association, succeeded
  after explicit resolution, and did not cross-pair identical basenames between
  Source A and Source B.
- Files written below OUTPUT were not considered input. Unsafe INPUT/OUTPUT
  relationships remain rejected by configuration validation.

## Managed originals and failure safety

- Managed copies were created below
  `<OUTPUT>/.photo-ai-factory/originals/RAW|JPEG_CAMERA` with content-addressed
  names, matching size/readability/SHA-256. Source hashes were identical before and
  after ingestion.
- Successful operations left zero `.partial-*` files.
- A 128 MiB copy cancelled during I/O left no durable Asset, accepted managed
  original or partial file; the source hash remained unchanged.
- An injected SQLite foreign-key failure after a validated archive left the
  content-addressed copy intact. Retry revalidated/reused that copy and produced
  one consistent Asset without duplicate storage.
- Concurrent ingestion observed maximum logical writers = 1, overlap violations =
  0, no deadlocks and an intact database.

## Real Sony A7 IV admission fixtures

Only copies were classified; Darktable was not invoked.

| Fixture | Size | SHA-256 | Result |
|---|---:|---|---|
| `_DSC1627.ARW` full-size | 42,123,264 | `a7c1974c9b84a54a668d8d704762a7352ed60dcd2f7cd84ace46be2835cc9484` | `SUPPORTED_FULL_SIZE` |
| `_DSC0141.ARW` Sony RAW S | 19,206,144 | `3ff461436fe15c11ac0cb961de68c123b102b8087014fd2153c6b88314502bad` | `UNSUPPORTED_REDUCED` / review |
| Corrupt synthetic `.ARW` | 5 | test-local | `UNKNOWN` / review |

The classifier remains a conservative admission classifier, not a general RAW
decoder.

## Bugs found and minimal fixes

1. `ProjectIngestionManager` referenced a nonexistent `ConfigVersion.Config`
   property. It now obtains the project configuration with the existing
   `ReadConfig()` API.
2. Reconciliation could starve files beyond a full bounded channel because every
   scan repeatedly queued the same leading paths. Reconciliation now waits for
   bounded-channel space while enumerating its durable filesystem snapshot;
   watcher callbacks retain non-blocking `TryWrite` and request reconciliation on
   pressure. The `pending` counter update was ordered before publication to remove
   a small consumer race.

No architecture, dependency or schema redesign was made.

## Requirements traceability

Functional within Phase 2 scope:

- FR-FS-003, FR-FS-004, FR-FS-009;
- FR-ING-001, FR-ING-002, FR-ING-004, FR-ING-006, FR-ING-007, FR-ING-008;
- FR-ORG-001, FR-ORG-003, FR-ORG-004, FR-ORG-005, FR-ORG-006, FR-ORG-007,
  FR-ORG-008.

Partial, without overstating compliance:

- FR-FS-002: watcher/session runtime exists; final automatic App/lifecycle
  composition is future work.
- FR-FS-008: source blocking and explicit resolution exist; UI presentation is
  future work.
- FR-ING-003: basename, relative origin, source generation and time-window pairing
  exist; full EXIF identity parsing does not.
- FR-ING-005: late RAW without an active Job is covered; active-Job immutability
  awaits production Jobs/queue.

Outside Phase 2:

- FR-ING-009 visual similarity belongs to Phase 3 analysis/embeddings.

## Safety and closeout

- Background watcher, consumer and periodic reconciliation tasks completed after
  stop; zero Phase 2 workers remained.
- Zero Python Worker, Darktable or ComfyUI engines were started by the C# audit.
- PRD, SRS, Architecture, ADR-001…016, Phase 0 PoCs and Phase 0 evidence were not
  modified.
- No dependency upgrades, Phase 3 implementation, AI/model work or active-Job
  workaround were introduced.

ADR-017 is accepted. Phase 3 is not started.
