# ADR-024: Recovery, Storage Preflight, Health Watchdog & Hardening Architecture

## Status
Accepted (Phase 8 Recovery & Hardening Validation)

## Context
Photo AI Factory operates as a desktop/local background orchestration pipeline processing high-resolution RAW and JPEG imagery through multi-stage pipelines (Darktable, Python AI Worker, ComfyUI). System interruptions (process crashes, host restarts, disk-full events, GPU OOM exceptions, worker timeouts) must be handled gracefully with zero data corruption, zero lost work, and deterministic recovery.

## Decisions

### 1. In-Flight Startup Crash Recovery & Reconciliation
- On application restart, `ProductionRecoveryCoordinator` scans all jobs for the project in strict FIFO order.
- Any active job (`Analyzing`, `Processing`, `Qa`, `Retrying`) that was in-flight when the process died is normalized through `INTERRUPTED` with transactional audit logging.
- All real supported checkpoints are inspected (`job_checkpoints`):
  - `ANALYSIS_COMPLETE`, `PRESELECTION_COMPLETE`, `BASIC_REVEAL_COMPLETE`, `DARKTABLE_PASS1_COMPLETE`, `FEEDBACK_INSPECTION_COMPLETE`, `RAW_DENOISE_COMPLETE`, `DARKTABLE_PASS2_COMPLETE`, `COMFYUI_COMPLETE`, `QA_COMPLETE`, `OUTPUT_PUBLISHED`.
  - Checkpoint artifacts, byte counts, and SHA-256 hashes are verified on disk.
  - Final publication verification requires both destination JPEG + SHA-256 and `final_history.json` belonging to the exact job and publication.
  - Missing or corrupted artifacts roll back the unverified stage to `INTERRUPTED` with audit reason `CORRUPT_CHECKPOINT_ARTIFACT`.
  - Checkpoints, publications, and child reprocess jobs are never duplicated on recovery replays.

### 2. Storage Preflight & Health (`BLOCKED_STORAGE`)
- Volume free space is inspected before executing storage-heavy stages using `DriveInfo.AvailableFreeSpace` with conservative 50 MB safety headroom.
- If storage budget is insufficient, the project enters `ProjectState.BlockedStorage` and `ProjectDispatchGuard.CanDispatchNextJob` prevents new jobs from dispatching.
- When disk space is restored, the project resumes safely to `Running`.
- On unexpected disk-full during write, only temporary staging files of the failed attempt are cleaned up; immutable originals, checkpoints, and histories are preserved.

### 3. Component Health Watchdog & Circuit Breaker
- Background workers (Python AI Worker, ComfyUI, Darktable CLI, Storage, GPU Coordinator) are monitored via real probes with states `Starting`, `Healthy`, `Degraded`, `Unhealthy`, `Stopped`.
- Repeated failures (threshold = 3) open the circuit breaker, transitioning component state to `Unhealthy` and the project to `ComponentUnhealthy`.
- Failure in one component blocks only dependent pipeline stages (e.g., Python worker failure blocks Analysis/QA, but leaves independent stages unaffected).
- Automatic restarts are strictly bounded (max 2 attempts) to prevent infinite restart loops.
- `OperationCanceledException` is strictly excluded from counting as a health failure.

### 4. GPU / OOM Hardening
- GPU lease acquisition via `IGpuResourceCoordinator` is strictly protected by `IAsyncDisposable` / `try-finally`, guaranteeing release on all exit paths (success, cancellation, exception, OOM, timeout).
- On GPU OOM, models are released and memory is reclaimed with at most ONE memory-recovery retry via `IGpuExecutionPolicy`. Second consecutive OOM fails cleanly to durable `ERROR` without silent degradation or quality compromise.

### 5. SQLite Single-Writer Online Backup & Safe Restore
- Online database backups utilize `SqliteConnection.BackupDatabase`, coordinated through single-writer locks.
- Backup integrity is validated via `PRAGMA integrity_check` and `PRAGMA foreign_key_check` with SHA-256 manifest generation.
- Dynamic manifest captures real `schema_migrations` version and assembly version.
- Retention policy keeps the last $N$ backups while strictly preserving the last known-good backup.
- Database restore is explicit and fail-closed: preserves current database as `damaged_pre_restore_*.db`, validates candidate integrity, foreign keys, SHA-256 manifest, and schema version compatibility (rejecting future/unsupported schema versions), and atomically replaces the live database.

### 6. Safe Temporary Staging Cleanup
- `SafeCleanupService` enforces strict path canonicalization and allowlists, requiring candidate paths to be strictly within managed work roots (`%LOCALAPPDATA%\PhotoAIFactory\work\<project_id>`).
- Directory junctions / symlinks / reparse points are detected and refused from traversal.
- Directory and extension guards ensure originals, managed archives, published JPEGs, XMP sidecars, histories, and database backups are never deleted.

## Limitations Documented
- Physical power-loss without write-flush on unbuffered storage controllers is outside software testing scope.
- Tests utilize controlled process terminations, crash simulations, and fault injections.
- Filesystem replacement atomicity relies on host NTFS/ReFS semantics on Windows.

## Consequences
- **Positive**: Resilient startup crash recovery; zero corrupted jobs; clear storage and health isolation; durable online backups and fail-safe atomic restore capabilities.
- **Verification**: Validated by 291 automated tests (112 Foundation, 142 Simulation, 33 Python repository, 4 Python worker) with 0 failures and 0 vulnerable packages.
