# PHOTO AI FACTORY — PHASE 6 COMFYUI FINAL REPORT

**Result:** CLOSED / GO WITH DOCUMENTED LIMITATIONS
**Date:** 2026-08-22
**Baseline:** `108e8dd4fdab5267d5236b5b8e7943b0460c0e80` (branch `main`)
**ADR-022 Status:** Accepted

---

## 1. Environment & Hardware Baseline

- **Operating System:** Microsoft Windows 11 / Windows 10 x64
- **.NET SDK:** `10.0.400` (.NET 10.0 `net10.0` Release build)
- **Host GPU:** NVIDIA GeForce RTX 4060 Ti (8 GB VRAM, Driver 572.16, CUDA 12.8)
- **AI Worker Python:** Isolated CPython 3.12.12 (venv `%LOCALAPPDATA%\PhotoAIFactory\runtimes\ai-worker`)
- **Pinned ComfyUI Version:** `v0.33.1`
- **Pinned ComfyUI Commit:** `72865f4f27eaf5396f8f36370e0a2be3a9a090ee`
- **Pinned ComfyUI Embedded Python:** `3.13.14` (`%LOCALAPPDATA%\PhotoAIFactory\components\comfyui\python_embeded\python.exe`)

---

## 2. Final Validated Test Suites

All test suites executed in strict isolation on the real Windows machine:

| Test Suite | Environment / Process | Total | Passed | Failed | Result |
| :--- | :--- | :---: | :---: | :---: | :---: |
| **Solution Release Compilation** | .NET 10 Release (`net10.0`) | — | — | — | **0 Warnings, 0 Errors** |
| **Foundation Tests** (`PhotoAIFactory.Foundation.Tests`) | .NET 10 Release | 112 | 112 | 0 | **PASS (100%)** |
| **Simulation & Real-PC Tests** (`PhotoAIFactory.Simulation.Tests`) | .NET 10 Release | 97 | 97 | 0 | **PASS (100%)** |
| **Python Repository Suite** (`tests/python`) | Python 3.12 (venv `ai-worker`) | 25 | 25 | 0 | **PASS (100%)** |
| **AI Worker Internal Unit Suite** (`src/python/ai-worker/tests`) | Python 3.12 (isolated process) | 4 | 4 | 0 | **PASS (100%)** |
| **Total Reported Executions** | — | **238** | **238** | **0** | **PASS (100%)** |
| **NuGet Transitive Vulnerability Audit** | Solution wide | — | — | — | **0 Vulnerabilities** |
| **Git Diff Check** (`git diff --check`) | Working tree | — | — | — | **0 Issues / Clean** |

---

## 3. SQLite Migration 007 Verification

Verified via `007_comfyui.sql` and full simulation test suite:
- **Fresh Database Execution:** PASS (clean DDL application).
- **Upgrade 006 -> 007:** PASS (verified with pre-migration backup and identical SHA-256 stability).
- **Checksum Idempotency & Drift Rollback:** PASS (unauthorized drift rejected, automatic rollback).
- **PRAGMAs & Integrity:** `PRAGMA integrity_check` OK, `WAL`, `synchronous=FULL`, `foreign_keys=ON`, `busy_timeout=5000`.
- **Append-Only Immutability:** Triggers `comfy_plans_no_update`, `comfy_plans_no_delete`, `comfy_executions_no_update`, `comfy_executions_no_delete` abort all UPDATE/DELETE attempts.
- **Constraints:** `jobs.comfy_retry_count` constrained `BETWEEN 0 AND 2`; `comfy_executions.status` constrained `IN ('SKIPPED', 'COMPLETED')`.
- **Checkpoint Durability:** `job_checkpoints.stage_name` includes unique `COMFYUI_COMPLETE`.

---

## 4. Policy & State Transitions

- **OFF Mode:** Produces normalized plan (`action="SKIP"`, `reason="COMFYUI_OFF"`), records `comfy_executions` row with `status="SKIPPED"` pointing to reveal output, creates `COMFYUI_COMPLETE` checkpoint, and leaves Job in `QA`. PASS.
- **AUTO Mode:** Produces conservative plan (`action="SKIP"`, `reason="AUTO_POLICY_NOT_CALIBRATED"`), records skipped execution, creates `COMFYUI_COMPLETE`, and leaves Job in `QA`. PASS.
- **ON Mode with 0 Tasks:** Durable no-op completion (`status="SKIPPED"`), leaves Job in `QA`. PASS.
- **ON Mode with Blocked Tasks:** Fails closed with `COMFY_TASK_NOT_APPROVED` (`retryable=false`), transitions Job directly to `ERROR` without consuming technical retries. No silent fallback or model substitution permitted. PASS.

---

## 5. Durability, Crash Recovery & Replay

- **Replay Idempotency:** Replay on a Job with `COMFYUI_COMPLETE` returns `ComfyWorkStatus.NoWork` without re-running or creating duplicate rows.
- **Durable History:** Atomic JSON write to `{OutputFolder}/.photo-ai-factory/history/{photo_id}/{job_id}/comfyui.json`. Overwrite protection verified.
- **SHA-256 Validation:** Missing or corrupted output files fail closed with `InvalidDataException`.
- **Database Failure Recovery:** If database commits fail after ComfyUI completes, `ComfyHistoryWriter.TryReadRecoveryAsync` recovers artifact and checkpoint without re-running inference.
- **Processing Boundary:** Active execution strictly enforces `QA -> PROCESSING -> QA` transition.

---

## 6. Real Pinned ComfyUI Runtime Evidence

- **Workflow ID:** `paf-validation-core-roundtrip-v1`
- **Node Graph:** `EmptyImage` (width `64`, height `48`, batch_size `1`, color `3368601`) -> `SaveImage` (filename_prefix `"PAF_PHASE6_VALIDATION/core"`)
- **Prompt ID:** `3a2372ba-d167-447e-9d9a-c5307daecf97`
- **Output Filename:** `core_00001_.png`
- **Dimensions:** `64 x 48`
- **Output File Size:** `500` bytes
- **Output SHA-256:** `ae477a31fffdefebde1bea4bbb6c03ec5f755f752ff87d0a20a43cd2deaa26a0`
- **Measured Real Execution Duration:** `279 ms`
- **Observed PID:** `7492`
- **Loopback Port:** Dynamic ephemeral port via `TcpListener(IPAddress.Loopback, 0)`
- **API Matrix:**
  - `/system_stats`: **PASS REAL**
  - `/prompt`: **PASS REAL**
  - `/ws`: **PASS REAL**
  - `/history`: **PASS REAL**
  - `/free`: **PASS REAL**
  - `/interrupt`: **PASS REAL**
  - `/queue`: **NOT RETESTED IN PHASE 6** (previously proven by accepted Phase 0 CUI-01)

---

## 7. Failure Evidence Classification

- **Process Kill / Restart:** **REAL OS PASS** (`ComfyRuntimeSupervisor_ForceKillAndRestart_Succeeds` terminates running PID via OS signal and restarts supervisor with new PID).
- **Missing / SHA-Corrupted Output:** **PASS** (rejection verified).
- **DB Persistence Failure Recovery:** **PASS** (durable boundary recovery verified).
- **Technical Retry Bound:** **PASS** (strictly bounded at 2 retries).
- **Capability Fail-Closed:** **PASS** (unapproved task fails closed with 0 retries).
- **Timeout / Cancellation:** Phase 6 coverage primarily unit/injection; queue cancellation inherited from Phase 0 CUI-01.

---

## 8. Original Immutability Statement

**MODEL-FREE CORE RUNTIME TEST DID NOT EXERCISE A PHOTOGRAPHIC ORIGINAL.**

Phase 6 persistence tests demonstrate that `SqliteComfyStore` reads exclusively from reveal/feedback output paths and performs zero UPDATE or DELETE queries against `photos` or `assets`. Previous accepted phases continue covering original hash immutability.

---

## 9. Process Cleanup

Audit-owned running process counts on host PC:
- **Python Workers:** `0`
- **ComfyUI Daemons:** `0`
- **Darktable Processes:** `0`
- **Audit Test / Background Processes:** `0`

---

## 10. Reconciled Audit Fixes

The five minimal audit-originated fixes preserved in the candidate baseline:
1. `ComfyPlanPolicy.cs`: Added missing `using PhotoAIFactory.Domain;` to resolve `ComfyUiMode` enum reference.
2. `Phase5FeedbackTests.cs`: Updated `MigrationCatalog.All.Take(6)` to account for Migration 007 registration.
3. `ComfyRuntimeSupervisor.cs`: Added thread-safe disposal guard (`Interlocked.Exchange(ref _disposed, 1)`) to avoid `ObjectDisposedException` on container teardown.
4. `SqliteComfyStore.cs`: Handled `DBNull.Value` in `PersistPlanAsync` to avoid false plan conflict exceptions.
5. `Phase6ComfyTests.cs`: Aligned test helper `SeedJobAsync` with exact SQLite schema constraints.

---

## 11. Known Documented Limitations

1. Photographic ComfyUI enhancement workflows (`COLOR`, `DENOISE_RGB`, `FACE_MASKS`, `FACE_RETOUCH`, `LOW_LIGHT`, `SHARPNESS`, `UPSCALE`) remain `BENCHMARK_AND_LICENSE_REQUIRED`.
2. AUTO enhancement policy remains `AUTO_POLICY_NOT_CALIBRATED` (conservative skip).
3. The model-free core workflow (`EmptyImage -> SaveImage`) proves runtime, API, and durability wiring, not visual image quality.
4. `/queue` was not re-exercised during Phase 6 but was previously proven by Phase 0 CUI-01.
5. Phase 6 real core workflow used no photographic original.
6. Phase 7 QA and publish remain unimplemented.
