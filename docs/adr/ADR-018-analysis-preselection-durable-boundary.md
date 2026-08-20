# ADR-018 — Analysis / Preselection durable boundary

**Status:** Accepted
**Date:** 2026-08-19

## Context

Phase 3 turns archived Photos into analyzed/preselected Jobs. The approved architecture
requires C# to remain the operational source of truth and sole SQLite writer while Python
performs AI inference.

## Decision

1. C# creates/freezes the initial Job before inference, including source Asset identity,
   SHA-256 and analysis representation.
2. Analysis input priority is `JPEG_CAMERA` → `JPEG_MASTER` → regenerable `RAW_PREVIEW`.
3. RAW preview is allowed only for `SUPPORTED_FULL_SIZE` RAW and is generated through the
   already validated Darktable CLI boundary, without fabricated XMP or Neural Restore.
4. C# owns `ANALYZING`, `ANALYSIS_COMPLETE`, `PRESELECTION_COMPLETE`, terminal preselection
   state and durable FIFO insertion.
5. Python runs the fixed V1 analysis sequence and returns structured observations only.
6. Heavy Python adapters release immediately after their station; C# retains the exclusive
   GPU lease until a final bounded model-release attempt completes.
7. V1 automatic rejection remains disabled until project-dataset benchmark thresholds are
   approved. Ambiguous/uncalibrated evidence routes to `REVIEW_PRE`.
8. `INTERRUPTED` may resume at `ANALYZING` so an incomplete Phase 3 stage can restart from
   its last durable checkpoint.
9. Model IDs must match the Phase 0 installed artifacts. No runtime downloads or silent
   model substitution are allowed.

## Consequences

- The queue becomes durable at the end of preselection.
- JPEG-only does not create a recompressed analysis file.
- Reduced Sony RAW remains outside V1 RAW processing.
- Migration 004 adds Job, Analysis, ModelExecution, Preselection, checkpoint and queue data.
- Exact model/API behavior and performance remain subject to the Phase 3 real-PC audit and
  project-dataset benchmark.

## Validation evidence

The Phase 3 Windows audit confirmed that C# owns durable Analysis and Preselection state
and remains the only SQLite writer, while Python returns structured AI results. The
`ANALYSIS_COMPLETE` and `PRESELECTION_COMPLETE` boundaries, durable FIFO insertion,
checkpoint replay, restart and idempotent reconciliation passed against real SQLite.

Malformed Worker responses, process crash/recovery, timeout and cancellation were handled
without unbounded retries. A failed Photo is durably classified without preventing later
Photos from being reconciled. GPU-heavy model execution remained serialized, including
the Florence-to-Qwen handoff in `FULL` mode.
