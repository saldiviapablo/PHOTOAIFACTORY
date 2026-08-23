# ADR-023 — QA, Review, and Publication Durable Schema

**Status:** Accepted
**Date:** 2026-08-23
**Parents:** PRD v1.1, SRS v1.1, Architecture v1.0, ADR-007, ADR-009, ADR-012, ADR-014, ADR-022
**Baseline:** `e929ae13f6f2b97ba0166d0126f0f0efee8552ec`

## Context

Phase 6 established `COMFYUI_COMPLETE` as the mandatory durable pre-QA boundary leaving completed Jobs in `JobState.Qa`. Phase 7 implements QA evaluation, human Review management, and final physical publication (`FINAL`, `REVISAR`, `DESCARTADAS`).

## Decisions

### 1. C# Single-Writer Authority
C# remains the sole writer to the SQLite database. Python AI worker endpoints (`/v1/qa`) and external tools never connect directly to the database.

### 2. Checkpoint Stages Expansion & Completed Invariant
The `job_checkpoints.stage_name` constraint is expanded to include:
- `QA_COMPLETE`: Written when automated QA evaluation is persisted.
- `OUTPUT_PUBLISHED`: Written when the final JPEG and metadata manifest are safely placed in their destination folder.

**Invariant**: `COMPLETED` job state is strictly forbidden unless `OUTPUT_PUBLISHED` checkpoint is durably written. When a quality reprocess child is spawned, the parent job remains in `REVIEW_FINAL` and is never marked `COMPLETED`.

### 3. QA Results Model (Append-Only)
`qa_results` records the automated decision (`PASS`, `REVIEW`, `REPROCESS`, `TECH_RETRY`, `FATAL`), structured JSON results, attempt identifier, and input SHA-256.
- Enforced `UNIQUE(job_id)`: Exactly one durable QA result per job.
- SQLite triggers `qa_results_no_update` and `qa_results_no_delete` abort all UPDATE and DELETE statements.

### 4. Review Items Lifecycle Model
`review_items` models human review items for preselection (`PRE`) and final QA (`FINAL`).
- Strict 2-state lifecycle: `PENDING` -> `RESOLVED`.
- CHECK constraints and triggers ensure identifying fields are immutable and DELETE is forbidden.
- Partial unique index `ux_review_items_pending` prevents concurrent duplicate pending items per job/kind.

### 5. Publications Model & Deterministic Collision Policy (V1 = JPEG Only)
`publications` records final publication metadata: destination kind (`FINAL`, `REVIEW`, `REJECTED`), destination file path, SHA-256, byte size, width, height, and history path.
- Enforced `UNIQUE(job_id, destination_kind)`: A job cannot publish conflicting records.
- SQLite triggers `publications_no_update` and `publications_no_delete` enforce immutability.
- Final publication in V1 accepts exclusively JPEG files (`.jpg`/`.jpeg`), validated for existence, positive size, matching SHA-256, and valid JPEG header dimensions.
- Collision disambiguation is strictly deterministic based on durable Job ID (`{stem}_{jobId}{ext}`), never using random suffixes, and failing closed if conflicting content exists on the alternate path.

### 6. Strict History Conflict & Test Isolation
- `final_history.json` content conflicts fail closed (`InvalidOperationException`) instead of silently accepting mismatched histories.
- `force_decision` is strictly isolated to test environments (`PAF_ALLOW_TEST_FORCE_DECISION`) and is ignored in production.

## Consequences

Positive:
- Provides atomic, auditable, and immutable persistence for all Phase 7 stages.
- Retains existing data and checksum integrity from Migrations 001 through 007.
- Eliminates risk of duplicate checkpoints, conflicting publications, or untracked review resolutions.
- Enforces strict invariants ensuring jobs are never marked completed without publication.
