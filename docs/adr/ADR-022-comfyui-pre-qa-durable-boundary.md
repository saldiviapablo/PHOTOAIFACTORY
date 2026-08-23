# ADR-022 — ComfyUI pre-QA durable boundary

**Status:** Accepted
**Date:** 2026-08-22
**Parents:** PRD v1.1, SRS v1.1, Architecture v1.0, ADR-007, ADR-009, ADR-012, ADR-014
**Baseline:** `108e8dd4fdab5267d5236b5b8e7943b0460c0e80`

## Context

Phases 4 and 5 finish reveal by placing a Job in `QA`. Phase 7 is the phase that
will implement actual QA. Phase 6 must insert optional ComfyUI enhancement after
reveal and before actual QA without making Python or ComfyUI owners of durable
Job state.

The approved architecture defines:

1. Python produces a normalized `ComfyPlan`.
2. C# validates authorization and workflow availability.
3. C# owns the exclusive GPU lease.
4. C# controls ComfyUI through its local API/WebSocket.
5. `COMFYUI_COMPLETE` is durable only after output validation and history
   persistence.
6. Recovery repeats only the incomplete ComfyUI stage when its validated input
   is still available.

## Validated Decisions

`QA` remains a coarse waiting boundary until Phase 7.

For Phase 6:

- C# remains the only durable owner of state and the only process that writes SQLite.
- Python produces `ComfyPlan` only via the `/v1/comfy/plan` endpoint.
- C# directly controls ComfyUI via HTTP/WebSocket loopback.
- Pre-QA durable checkpoint is `COMFYUI_COMPLETE`.
- Reveal-complete Job in `QA` without `COMFYUI_COMPLETE` is eligible for the
  ComfyUI decision stage.
- OFF mode and valid no-op AUTO/ON decisions persist `COMFYUI_COMPLETE` while
  leaving the Job in `QA`.
- When actual ComfyUI work is executed, C# transitions `QA -> PROCESSING`, runs
  the controlled ComfyUI stage, and transitions `PROCESSING -> QA` only after
  durable completion.
- Technical retries are strictly bounded (`comfy_retry_count` constrained between 0 and 2).
- ComfyUI is strictly loopback (127.0.0.1) and headless (`--disable-auto-launch`, `--disable-all-custom-nodes`).
- All seven production photographic enhancement tasks remain blocked (`BENCHMARK_AND_LICENSE_REQUIRED`).
- Internal workflow (`paf-validation-core-roundtrip-v1` via `EmptyImage -> SaveImage`) is runtime/transport validation only.
- Phase 7 must require `COMFYUI_COMPLETE` before executing final QA.

## Validated Pinned Runtime

- **ComfyUI Version:** `v0.33.1`
- **ComfyUI Commit:** `72865f4f27eaf5396f8f36370e0a2be3a9a090ee`
- **Embedded Python:** `3.13.14` (`python_embeded\python.exe`)
- **Host GPU:** NVIDIA GeForce RTX 4060 Ti (8 GB VRAM)

## Fail-Closed Workflow Policy

The V1 configurable task vocabulary is:

- `DENOISE_RGB`
- `COLOR`
- `FACE_RETOUCH`
- `FACE_MASKS`
- `LOW_LIGHT`
- `UPSCALE`
- `SHARPNESS`

Phase 6 does **not** silently promote editing weights or workflows.

At this baseline the production catalog records these tasks but marks
their enhancement workflows `BENCHMARK_AND_LICENSE_REQUIRED`. A task requested
in ON mode therefore fails closed with a structured capability error (`COMFY_TASK_NOT_APPROVED`, non-retryable) until its
model/workflow passes license and quality/VRAM/performance gates.

AUTO is intentionally conservative until benchmark calibration: authorized
tasks are recorded as skipped with reason `AUTO_POLICY_NOT_CALIBRATED`.

A model-free core workflow (`EmptyImage -> SaveImage`) is retained only as an
internal transport/runtime validation workflow (`paf-validation-core-roundtrip-v1`). It is not user-selectable and
does not count as a photographic enhancement.

## Security and Durability

- loopback only (127.0.0.1);
- no browser/UI;
- no shell command concatenation;
- exact owned-process termination and lifecycle supervision;
- no model download or dependency upgrade at runtime;
- SQLite remains C#-only;
- plan/execution rows are append-only (enforced by SQLite triggers);
- output paths are constrained to the owned ComfyUI output directory;
- retry count is bounded at two technical retries;
- output and history SHA-256 are validated before `COMFYUI_COMPLETE`;
- replay and crash recovery are idempotent.

## Consequences

Positive:

- preserves C# ownership and Phase 0 CUI-01/GPU-01 patterns;
- makes OFF/AUTO behavior durable and auditable;
- prevents unbenchmarked editing models from becoming production defaults;
- establishes the exact pre-QA checkpoint needed by Phase 7.

Limitations until future benchmark qualification:

- no photographic enhancement workflow is `APPROVED` yet;
- the technical core round-trip proves runtime/API wiring, not visual quality;
- task-specific workflows remain blocked until separate license + quality +
  VRAM/performance evidence exists.

## Acceptance

Accepted following complete real-PC audit and evidence reconciliation verifying Migration 007, OFF/ON/AUTO policy, fail-closed authorization, real pinned ComfyUI API roundtrip, single-GPU lease ownership, process force-kill restart, crash recovery, and 0 orphan processes.
