# ADR-021 — FEEDBACK input policy and conservative Pass 2 baseline

**Status:** Accepted
**Date:** 2026-08-20

## Context

The approved FEEDBACK design is RAW-centric: Pass 1 produces TIFF RGB 16-bit +
XMP, inspection produces a FeedbackRecipe, and Pass 2 restarts from the RAW
original or from a temporary DNG created by an approved Raw Denoise stage.

The product baseline also requires JPEG-only to be a first-class V1 workflow and
requires reprocessing to restart from the JPEG original rather than chaining
recompressions.

Darktable Neural Restore remains `NOT_HEADLESS_PROVEN`. The project must not
fabricate an XMP compiler or enable Raw/RGB Denoise/Upscale without both proven
headless control and benchmark approval.

## Decision

### RAW FEEDBACK

For a `SUPPORTED_FULL_SIZE` managed RAW:

```text
managed RAW original
→ Darktable Pass 1
→ TIFF RGB 16-bit + authentic XMP
→ FEEDBACK inspection
→ normalized FeedbackRecipe
→ managed RAW original again
→ apply authentic Pass 1 XMP
→ Darktable Pass 2 JPEG staging
```

Pass 2 never consumes Pass 1 TIFF/JPEG as its source.

Sony reduced RAW remains unsupported/review-safe.

### JPEG-only FEEDBACK

For a managed JPEG master:

```text
managed JPEG original
→ Darktable Pass 1
→ TIFF RGB 16-bit + authentic XMP
→ FEEDBACK inspection
→ normalized FeedbackRecipe
→ managed JPEG original again
→ apply authentic Pass 1 XMP
→ Darktable Pass 2 JPEG staging
```

RAW-only stages are skipped. In particular `raw_denoise=false`.

This is the JPEG-first-class adaptation of the fixed FEEDBACK line. It does not
turn the Pass 1 derivative into a new source and therefore does not create a
recompression chain.

### Conservative Phase 5 recipe

Until benchmark evidence exists, the Phase 5 FeedbackRecipe is intentionally:

- schema `1`;
- recipe version `phase5-feedback-v1`;
- strategy `CONSERVATIVE_REUSE_PASS1`;
- benchmark status `NOT_CALIBRATED`;
- no creative module operations;
- Pass 2 restarts from the immutable managed original;
- Pass 1 authentic XMP is reapplied;
- no arbitrary XMP synthesis.

The inspector may record technical evidence from the 16-bit TIFF and an in-memory
sRGB preview, but it does not silently turn unbenchmarked observations into
creative edits.

### Neural Restore decisions

The recipe explicitly carries individual decisions for:

- `raw_denoise`;
- `rgb_denoise`;
- `upscale`.

In this baseline all are `enabled=false` with reason
`NOT_HEADLESS_PROVEN_AND_BENCHMARK_PENDING`.

`RAW_DENOISE_COMPLETE` is reserved by the schema/state architecture but MUST NOT
be written while the task is disabled.

## Durable boundaries

Phase 5 validates:

- `DARKTABLE_PASS1_COMPLETE`;
- `FEEDBACK_INSPECTION_COMPLETE`;
- `DARKTABLE_PASS2_COMPLETE`.

Each checkpoint is written only by C# after output validation and persistence.

Pass 1 TIFF remains temporary and is retained until Pass 2 is durably complete.
Portable authentic XMP/history remain permanent.

After Pass 2 the Job moves to `QA` as the next waiting boundary. Phase 5 does not
execute QA, ComfyUI, final publication or `OUTPUT_PUBLISHED`.

## Consequences

- RAW and JPEG-only FEEDBACK obey original-first reprocessing.
- No derivative chaining.
- No silent Neural Restore enablement.
- No arbitrary XMP compiler.
- The exact creative FEEDBACK recipe remains benchmark-pending.
- Phase 6 can add ComfyUI after reveal without changing Phase 5's durable history.
## Validation evidence

Accepted after the Phase 5 Windows/Darktable audit.

Validated:

- RAW full-size Pass 2 restarts from the immutable managed RAW original;
- JPEG-only Pass 2 restarts from the immutable managed JPEG original;
- the Pass 1 TIFF is never the Pass 2 image source;
- authentic Darktable XMP is preserved and reapplied;
- JPEG-only skips RAW-specific operations;
- `DARKTABLE_PASS1_COMPLETE`, `FEEDBACK_INSPECTION_COMPLETE` and
  `DARKTABLE_PASS2_COMPLETE` are durable and replay-safe;
- Neural Restore remains disabled and `RAW_DENOISE_COMPLETE` is not written;
- source and managed originals remain hash-identical.

This ADR clarifies the RAW-centric FEEDBACK wording for the already-approved
first-class JPEG-only V1 path. It does not authorize derivative chaining,
arbitrary XMP compilation or Neural Restore automation.

Creative FEEDBACK quality remains `NOT_CALIBRATED / BENCHMARK_REQUIRED`.

See `docs/phase5/PHASE5_FEEDBACK_REPORT.md`.
