# PHOTO AI FACTORY — Phase 3 Analysis / Preselection Implementation Candidate

**Baseline commit:** `f0f0df59bbdb9def5edc59eb48d3bb03b43e2d04`
**Status:** CANDIDATE — requires real-PC validation before Phase 3 can close.

## Scope

Implements the Phase 3 line:

`READY_FOR_ANALYSIS → ANALYZING → ANALYSIS_COMPLETE → PRESELECTION_COMPLETE → APPROVED/REVIEW_PRE/REJECTED_PRE`

Approved Photos are inserted into the durable FIFO queue. This package does not implement
Reveal, FEEDBACK, ComfyUI processing, QA or final publishing.

## Analysis inputs

- RAW+JPEG: verified managed `JPEG_CAMERA`.
- JPEG-only: verified managed `JPEG_MASTER`, analyzed directly; no extra JPEG file.
- RAW-only: verified full-size RAW → temporary sRGB JPEG preview under the Job attempt
  workspace using the validated Darktable CLI adapter.
- Sony reduced RAW: remains unsupported/review.
- Unknown/corrupt RAW: does not enter supported RAW analysis.

The Job freezes source Asset ID, SHA-256, input kind and representation path before
inference. Later retries do not silently switch source.

## Python analysis order

1. OpenCV technical metrics.
2. RF-DETR Medium.
3. MediaPipe Face Landmarker.
4. MediaPipe Pose Landmarker Full only when a person is detected.
5. Florence-2 Large in STANDARD/FULL.
6. Qwen3-VL-2B-Instruct-FP8 in FULL.
7. DINOv2 ViT-S/14 embedding.

The model IDs match Phase 0 bootstrap artifacts exactly. Runtime download is disabled.
Adapters release after each station to bound VRAM on the 8 GB reference GPU.

## Preselection policy

Python returns structured findings. C# validates the suggested decision.
Automatic `REJECTED_PRE` is deliberately disabled while thresholds are not benchmarked;
uncertainty routes to `REVIEW_PRE`. Disabled preselection produces `APPROVED`.

## Persistence

Migration `004_analysis_preselection_queue` adds:

- `jobs`
- `job_state_transitions`
- `analysis_results`
- `model_executions`
- `preselection_results`
- `job_checkpoints`
- `queue_entries`

`ANALYSIS_COMPLETE` and `PRESELECTION_COMPLETE` are persisted only after their durable
outputs are written. Result/history rows are immutable. Queue sequence is durable and
`PROCESS_NEXT` metadata is supported.

## Recovery / retry

- technical retry: initial + at most 2 retries;
- OOM recovery: at most 1 retry;
- permanent validation/model-missing errors: no blind retry;
- cancellation marks the in-flight Job interrupted when safe;
- restart uses existing checkpoints rather than overwriting history;
- `INTERRUPTED → ANALYZING` is added as the Phase 3 recovery transition.

## Validation required on Windows

Codex must compile and run all existing regressions plus the Phase 3 tests using the
isolated Python runtime. Real model inference must verify exact local artifacts/loaders,
VRAM release, RAW preview, cancellation/crash/restart, migration 003→004, single writer,
hash immutability and process cleanup.

No PRD/SRS/Architecture baseline is changed by this candidate.

## Windows audit finding — 2026-08-19

Release build, C#/Python regressions, Migration 004, durable replay/FIFO, the Darktable
5.6.0 RAW preview, OFF-mode real inference and the Qwen FP8 loader passed after minimal
Windows integration fixes. Qwen visual input is bounded to 1,280 28×28 tokens
(1,003,520 pixels) for the validated 8 GB GPU.

The original Microsoft Florence-2 Large repository representation was confirmed
incompatible with native Transformers 5.15. ADR-019 proposes the exact native-converted
artifact `florence-community/Florence-2-large` at revision
`4271c66b88cdbc05735372ec13b2360108de5317`; the artifact gate passed without remote code,
dependency changes or mismatched-weight fallback. Post-integration `STANDARD` and `FULL`
ran successfully with sequential GPU ownership and the existing Qwen visual bound.
Five Florence cycles were stable, and crash/restart, malformed response, timeout,
cancellation, artifact-integrity and durable replay paths passed. This candidate remains
subject to Lead Engineer review; ADR-018 and ADR-019 remain `PROPOSED` and no model is
promoted from `BASELINE` before project quality benchmarking. ADR-019 records the model
card's continued-pretraining caveat; compatibility PASS is not treated as evidence of
quality equivalence to the legacy Microsoft weights.
