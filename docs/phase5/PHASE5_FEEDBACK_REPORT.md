# PHOTO AI FACTORY — PHASE 5 FEEDBACK REPORT

**Result:** CLOSED / GO WITH DOCUMENTED LIMITATIONS
**Baseline:** `746d89ab4dadb13c61caaedac0f8d1ba1d6e5cf6`
**Validated:** 2026-08-22

## Scope validated

Phase 5 validates the FEEDBACK reveal path only:

```text
managed original
→ Darktable Pass 1
→ TIFF RGB 16-bit + authentic XMP
→ external inspection
→ normalized FeedbackRecipe
→ managed original again
→ Darktable Pass 2
→ JPEG staging
→ QA waiting boundary
```

Phase 5 does not execute ComfyUI, QA, FINAL publication or
`OUTPUT_PUBLISHED`.

## Build and automated tests

- Release build: PASS, .NET SDK 10.0.400, 0 errors, 0 warnings.
- C#: 183/183 PASS.
  - Foundation: 112.
  - Simulation: 71.
  - Phase 5: 20/20.
- Python: 21/21 PASS using isolated Python 3.12.12.
  - Phase 5: 6/6.
- Known warning: `StarletteDeprecationWarning`.
- NuGet vulnerable packages: none reported.

## Migration 006

PASS:

- fresh database → schema 006;
- schema 005 → 006 upgrade;
- pre-migration backup;
- stable migration checksums;
- idempotent reopen;
- checksum drift rejection;
- transactional rollback;
- `PRAGMA integrity_check = ok`;
- WAL;
- `synchronous = FULL`;
- foreign keys ON;
- append-only / immutable triggers.

New FEEDBACK persistence covers Pass 1, inspection and Pass 2.

Durable checkpoints validated:

- `DARKTABLE_PASS1_COMPLETE`;
- `FEEDBACK_INSPECTION_COMPLETE`;
- `DARKTABLE_PASS2_COMPLETE`.

`RAW_DENOISE_COMPLETE` is reserved by the schema but was not written.

## RAW full-size FEEDBACK

Sony A7 IV full-size RAW:

### Pass 1

- PASS.
- 7032×4688.
- TIFF RGB 16-bit, 3 channels.
- embedded sRGB ICC profile.
- 173,587,026 bytes.
- SHA-256:
  `d2a14eb430c338bb9b20f7fbecd49a24ef7c8cbdeb71f1a0cf16c2a3bbf6ed71`.
- duration: 17.375 s.

Authentic Darktable Pass 1 XMP:

- 8,159 bytes.
- SHA-256:
  `875e233f07b781ac1b1c1d3eb572646008e0169b7ce366410e62d2a0ca9d4c9d`.

### Inspection

- PASS.
- Pass 1 TIFF remained unchanged.
- sRGB preview generated in memory at 1280×853.
- persisted Phase 3 Analysis was reused.
- duration: 1.402 s.

### Pass 2

- PASS.
- source proven to be the immutable managed RAW original plus authentic
  Pass 1 XMP.
- Pass 1 TIFF was never used as the Pass 2 image source.
- JPEG 7032×4688.
- 18,137,004 bytes.
- SHA-256:
  `7f4aaf749e69a4ccad91838436ad6a7519cc61f7375d73d8437c426c6236bd47`.
- authentic output XMP: 7,375 bytes.
- duration: 14.506 s.

Total measured RAW FEEDBACK time: 36.467 s.

## JPEG-only FEEDBACK

JPEG-only remains a first-class V1 path.

### Pass 1

- PASS.
- source: immutable managed JPEG original.
- TIFF RGB 16-bit, 3 channels.
- 7008×4672.
- embedded sRGB ICC profile.
- 83,193,692 bytes.
- SHA-256:
  `b90515db3a428f12c46112689e1d59d8617125b01ec7622725a1a40f8360b779`.
- duration: 17.342 s.

Authentic Darktable Pass 1 XMP:

- 4,677 bytes.
- SHA-256:
  `66f4294923d70e84060376e0cde0f86c767ca3439c49c4b28d2af841db6fc3a8`.

### Inspection

- PASS.
- TIFF unchanged.
- bounded sRGB preview generated in memory.
- RAW-specific stages explicitly skipped.
- duration: 1.257 s.

### Pass 2

- PASS.
- source proven to be the same immutable managed JPEG original plus authentic
  Pass 1 XMP.
- Pass 1 TIFF and derived JPEGs were not used as source.
- JPEG 7008×4672.
- 5,647,339 bytes.
- SHA-256:
  `92c2fc083d06c20ad3623c567a0e9eab363715ebf83f4b51d913d8f21f02a45c`.
- authentic output XMP: 4,348 bytes.
- duration: 14.426 s.

Total measured JPEG FEEDBACK time: 35.548 s.

## FeedbackRecipe baseline

Validated normalized contract:

- schema version `1`;
- recipe version `phase5-feedback-v1`;
- strategy `CONSERVATIVE_REUSE_PASS1`;
- Pass 2 mode `REUSE_PASS1_XMP`;
- `operations=[]`;
- no arbitrary XMP compilation;
- restart from managed original;
- Pass 1 derivative is never the source.

The creative FEEDBACK recipe remains:

`NOT_CALIBRATED / BENCHMARK_REQUIRED`.

## Darktable Neural Restore

Darktable Neural Restore remains `NOT_HEADLESS_PROVEN`.

Phase 5 therefore keeps:

- Raw Denoise: disabled;
- RGB Denoise: disabled;
- Upscale: disabled.

Reason:

`NOT_HEADLESS_PROVEN_AND_BENCHMARK_PENDING`.

No Neural Restore checkpoint was falsely written and no replacement model or
ComfyUI workaround was silently introduced.

## Reproducibility

Decoded Pass 1 / Pass 2 comparison:

RAW:

- MAE 2.083271;
- RMSE 2.934301;
- PSNR 38.780709 dB.

JPEG-only:

- MAE 0.486613;
- RMSE 0.871716;
- PSNR 49.3233 dB.

Different TIFF/JPEG byte streams are expected. The validation demonstrates
consistent decoded image behavior rather than requiring byte identity across
different output formats.

## Durability and recovery

PASS:

- durable Pass 1 boundary;
- durable inspection boundary;
- durable Pass 2 boundary;
- replay/idempotency;
- database reopen recovery;
- exact Pass 2 recovery from portable history;
- duplicate row/checkpoint prevention;
- bounded retry: initial attempt + maximum two retries;
- cancellation;
- transport/runtime fault injection;
- DB failure injection;
- no premature queue removal;
- `PROCESSING → QA` only after successful Pass 2 persistence.

Pass 1 TIFF is temporary and removed only after durable Pass 2.
Permanent JSON/XMP history remains preserved.

## Input safety

PASS negative handling:

- Sony reduced RAW;
- missing managed original;
- hash mismatch;
- corrupt JPEG;
- corrupt RAW.

Source files and managed originals retained identical SHA-256 values before and
after processing.

## Pipeline boundaries

Phase 5 validated:

- one heavy Job at a time;
- existing reveal execution serialization;
- C# GPU coordination boundary;
- C# remains Job/queue/checkpoint/SQLite authority;
- Python returns structured inspection/recipe only;
- no ComfyUI;
- no QA execution;
- no FINAL publication;
- no `OUTPUT_PUBLISHED`.

Process cleanup at audit end:

- Python workers: 0;
- Darktable: 0;
- ComfyUI: 0;
- test/background workers: 0.

## Bugs corrected during validation

The audit found and minimally corrected:

- C# variable shadowing;
- Phase 4 schema fixture pinned incorrectly to schema 5;
- corrupt JPEG validation;
- overly permissive cleanup/recovery paths;
- missing cleanup after completed replay;
- incomplete Python contract behavior;
- database persistence failure classification;
- unmarked export failure;
- inconsistent Neural Restore reason text.

A permanent Phase 5 regression suite was added.

## Limitations

### PERFORMANCE_LIMITATION / BENCHMARK_REQUIRED

Measured FEEDBACK paths exceeded the normal ~30 s product target:

- RAW total: 36.467 s;
- JPEG total: 35.548 s.

The PRD treats ~30 s as a target rather than a hard timeout. Performance must be
measured/optimized using the project benchmark dataset rather than by weakening
quality or durability.

### CREATIVE_RECIPE_LIMITATION

The FEEDBACK creative recipe remains `NOT_CALIBRATED`. Phase 5 validates the
contract, original-first two-pass mechanism and durable boundaries, not final
creative-quality approval.

### NEURAL_RESTORE_LIMITATION

Darktable Neural Restore remains `NOT_HEADLESS_PROVEN`. No unsupported headless
mechanism was invented.

### TEST_LIMITATION

An OS-level hard kill of Darktable was not forced. Real cancellation, non-zero
process behavior, deterministic crash/timeout injection, recovery and process
cleanup were validated.

## Decision

ADR-021 is accepted as the formal V1 clarification for FEEDBACK input policy:

- full-size RAW Pass 2 restarts from the managed RAW original;
- JPEG-only Pass 2 restarts from the managed JPEG original;
- neither path uses the Pass 1 derivative as source;
- RAW-only operations are skipped for JPEG-only.

This resolves the RAW-centric FEEDBACK wording while preserving the approved
global requirement that JPEG-only is first-class and that reprocessing starts
from the immutable managed original.

**PHASE 5 — FEEDBACK = CLOSED / GO WITH DOCUMENTED LIMITATIONS**

Next implementation phase:

**Phase 6 — ComfyUI**.

Phase 6 was not started by the Phase 5 audit.
