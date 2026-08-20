# PHOTO AI FACTORY — PHASE 3 ANALYSIS / PRESELECTION REPORT

**Result:** CLOSED / GO WITH DOCUMENTED LIMITATIONS
**Baseline:** `f0f0df59bbdb9def5edc59eb48d3bb03b43e2d04`
**Validation date:** 2026-08-19

## Scope and decision

Phase 3 implements the durable Analysis / Preselection boundary from
`READY_FOR_ANALYSIS` through `ANALYSIS_COMPLETE`, `PRESELECTION_COMPLETE` and the
resulting review or FIFO state. C# remains the operational source of truth and sole
SQLite writer; the isolated Python Worker returns structured AI results.

ADR-018 and ADR-019 are accepted. ADR-019 accepts the Florence runtime artifact; it
does not approve model quality. Florence remains `BASELINE` pending the planned
PHOTO AI FACTORY dataset benchmark. Phase 4 has not started.

## Validation summary

- Release build: PASS, 0 errors and 0 warnings.
- C# automated tests: 143/143 PASS — 112 Foundation and 31 Simulation.
- Python automated tests: 11/11 PASS using the isolated production runtime.
- Phase 0/1/2 regression: PASS; Phase 2 focused ingestion tests: 17/17 PASS.
- Migration 004: PASS, idempotent, real SQLite, WAL/FULL/FK and integrity check valid.
- RAW+JPEG, RAW-only Sony A7 IV full-size and JPEG-only: PASS.
- Sony reduced RAW S negative path and corrupt-input handling: PASS.
- RF-DETR Medium, MediaPipe Face/Pose and DINOv2: PASS.
- Florence-2 Large native artifact and Qwen3-VL-2B-Instruct-FP8: PASS.
- semantic modes OFF, STANDARD and FULL: PASS.
- durable FIFO, checkpoints and replay/idempotency: PASS.
- Worker crash, malformed response, restart/recovery, timeout and cancellation: PASS.
- missing, wrong-hash and partial/corrupt model artifact handling: PASS.
- managed original/hash immutability: PASS.
- audit-process cleanup: PASS.
- NuGet vulnerable-package scan: PASS, no vulnerable packages reported.

## Florence-2 Large artifact

- repository: `florence-community/Florence-2-large`;
- immutable revision: `4271c66b88cdbc05735372ec13b2360108de5317`;
- `model.safetensors` SHA-256:
  `7715423d6549bf1e71188bdd84f4ac960cc0597886af24a5ef7b66f128660685`;
- license metadata: MIT, with the model card linking the upstream Microsoft license;
- loader: native `Florence2ForConditionalGeneration` with `AutoProcessor`, local/offline,
  no `trust_remote_code`;
- load time: approximately 1.31–1.33 s;
- peak allocation: approximately 1.91 GB decimal on the RTX 4060 Ti 8 GB reference PC;
- five load/inference/release cycles: PASS without OOM or monotonic VRAM growth.

The legacy Microsoft artifact remains retained as historical evidence. The converted
snapshot has no standalone LICENSE file, and its model card describes continued
pretraining with 0.1B samples. These facts are retained as provenance and benchmark
limitations rather than treated as model-quality approval.

## Semantic-mode evidence

- `STANDARD`: 11.590 s. Florence executed and Qwen did not.
- `FULL`: 41.424 s. Florence and Qwen executed sequentially; combined peak allocation
  was `3,790,530,048` bytes.
- Qwen visual bound: 1,003,520 pixels / 1,280 visual tokens. This prevented reference-GPU
  OOM without changing the Qwen model, FP8 precision or kernel.

`FULL` at 41.424 s is explicitly classified as
`PERFORMANCE_LIMITATION / BENCHMARK_REQUIRED`. The PRD target is not a hard timeout, so
this does not block functional Phase 3 closure. Optimization requires benchmark evidence.

## Remaining limitations

- Florence remains `BASELINE` pending quality benchmarking.
- The converted Florence checkpoint has the documented continued-pretraining caveat.
- The Florence snapshot has MIT metadata and an upstream license link but no standalone
  LICENSE file.
- `FULL` measured 41.424 s on the audit fixture.
- The known `StarletteDeprecationWarning` remains; no dependency was upgraded.
- A small expected CUDA/Python allocator baseline remains after release.
- Phase 4 processing does not exist yet.

## Closure

Phase 3 is functionally closed and ready for the next explicitly authorized phase. The
next Implementation Plan stage is Phase 4 — Basic Reveal: DT_AUTO, PRE_AI, normalized
recipe, XMP/history and export.

## Final shutdown regression — 2026-08-20

The formal closure run exposed a test-only start/stop synchronization race in
`HostShutdown_LeavesNoBackgroundWorker`. With .NET 10, host startup can complete before
the test `BackgroundService.ExecuteAsync` has entered and begun observing its lifetime
token. Unlike the neighboring cancellation test, this test stopped the host without first
waiting for its existing `Started` signal. Under adverse scheduling, the test then waited
for a cancellation observation from work whose start had not been synchronized.

The permanent regression fix waits for `Started` before `StopAsync`, records a separate
`Exited` signal in `finally`, and verifies that the execution task completed successfully.
The existing five-second bounds remain deadlock guards; no timeout was increased and no
production delay or lifecycle behavior was changed.

Evidence:

- before the fix: the formal full-suite run failed once; a subsequent isolated baseline
  run passed 50/50, confirming the scheduling-sensitive nature of the race;
- after the fix: isolated Release repetitions passed 100/100, with 1.208 s median and
  1.264 s maximum total test-process duration;
- two consecutive complete C# suite runs passed 143/143 each;
- Release build remained at 0 errors and 0 warnings;
- no PHOTO AI FACTORY test worker or external engine remained after validation.
