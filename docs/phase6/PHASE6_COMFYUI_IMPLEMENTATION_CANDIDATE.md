# PHOTO AI FACTORY — Phase 6 ComfyUI implementation candidate

**Status:** VALIDATED — Phase 6 CLOSED / GO WITH DOCUMENTED LIMITATIONS
**Baseline:** `108e8dd4fdab5267d5236b5b8e7943b0460c0e80`
**Phase:** 6 — ComfyUI

## Scope & Validated Implementation

This component implements the durable ComfyUI decision boundary that sits after
Phase 4/5 reveal and before Phase 7 QA.

Validated:

- versioned `ComfyPlan` endpoint in the Python Worker (`/v1/comfy/plan`);
- strict C# plan policy validation (`ComfyPlanPolicy`);
- OFF / ON / AUTO mode behavior;
- seven V1 task identifiers (`COLOR`, `DENOISE_RGB`, `FACE_MASKS`, `FACE_RETOUCH`, `LOW_LIGHT`, `SHARPNESS`, `UPSCALE`);
- fail-closed production workflow catalog;
- migration 007 with `COMFYUI_COMPLETE` pre-QA checkpoint;
- append-only `comfy_plans` and `comfy_executions` persistence with SQLite triggers;
- pre-QA dispatcher integration in `ProjectProcessingManager`;
- lazy headless ComfyUI supervisor (`ComfyRuntimeSupervisor`) based on proven CUI-01 contract;
- loopback REST/WebSocket execution through `ComfyUiClient`;
- exclusive single-GPU lease and Python model release before ComfyUI execution;
- bounded technical retries (`comfy_retry_count` between 0 and 2);
- cancellation/interrupt, crash restart, and owned-process cleanup (0 orphan processes);
- immutable portable Comfy history (`ComfyHistoryWriter`);
- internal model-free runtime validation workflow (`paf-validation-core-roundtrip-v1`);
- 100% passing C# and Python test suites (238 total tests).

Not included:

- Phase 7 QA;
- final publish;
- model downloads;
- dependency upgrades;
- ComfyUI upgrades;
- custom-node installation;
- promotion of any editing weight/workflow to APPROVED;
- silent fallback to another model/workflow.

## Explicit Mode & Task Policies

The production task registry is present, but every photographic enhancement
workflow remains `BENCHMARK_AND_LICENSE_REQUIRED`.

Therefore:

- **OFF**: durable skip, no ComfyUI execution; leaves Job in QA.
- **AUTO**: currently conservative (`AUTO_POLICY_NOT_CALIBRATED`); durable skip, no ComfyUI execution; leaves Job in QA.
- **ON with zero tasks**: durable no-op completion (`status="SKIPPED"`, points to reveal output); leaves Job in QA.
- **ON with unapproved task**: fails closed with `COMFY_TASK_NOT_APPROVED` (`retryable=false`). Transitions Job to ERROR with zero retries consumed.
- All seven photographic enhancement tasks remain `BENCHMARK_AND_LICENSE_REQUIRED`.
- No model was promoted to `APPROVED`.
- No model or dependency was downloaded or upgraded.
- Phase 7 was not implemented.

## Internal Runtime Validation

`paf-validation-core-roundtrip-v1` uses only core nodes:

`EmptyImage (64x48, color 3368601) -> SaveImage (PAF_PHASE6_VALIDATION/core)`

It exists solely to validate the pinned local ComfyUI server, `/prompt`, `/ws`,
`/history`, output validation, cancellation/restart, and cleanup. It is not
selectable through `ComfyPlan` and does not represent visual quality.

## Audit & Verification Outcome

All 13 audit validation areas completed with PASS on the Windows PC:
- Solution release build: 0 warnings, 0 errors.
- 112 Foundation tests passed.
- 97 Simulation tests passed (including real ComfyUI model-free roundtrip and process force-kill restart).
- 25 Python repository tests passed in isolated process.
- 4 Python AI worker internal unit tests passed in isolated process.
- SQLite Migration 007 passed fresh apply, upgrade, checksum, drift rejection, WAL/FULL/FK, and trigger immutability.
- 0 transitive NuGet vulnerabilities found.
- 0 git diff check errors.
- 0 orphan processes remaining.

## Formal Decision

Phase 6 is formally **CLOSED / GO WITH DOCUMENTED LIMITATIONS**.
ADR-022 is **Accepted**.
Phase 7 remains **NOT STARTED**.
