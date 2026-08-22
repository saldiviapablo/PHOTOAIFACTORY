# PHOTO AI FACTORY — Phase 5 FEEDBACK implementation candidate

**Status:** VALIDATED — Phase 5 CLOSED / GO WITH DOCUMENTED LIMITATIONS
**Baseline:** `746d89ab4dadb13c61caaedac0f8d1ba1d6e5cf6`

## Scope

Phase 5 implements only the approved FEEDBACK reveal station:

```text
managed original
→ Darktable Pass 1
→ TIFF RGB 16-bit + authentic XMP
→ inspection
→ normalized FeedbackRecipe
→ managed original again
→ Darktable Pass 2
→ JPEG staging
→ QA waiting boundary
```

Implemented:

- Migration 006;
- durable FEEDBACK Pass 1/inspection/Pass 2 rows;
- `DARKTABLE_PASS1_COMPLETE`;
- `FEEDBACK_INSPECTION_COMPLETE`;
- `DARKTABLE_PASS2_COMPLETE`;
- schema reservation for `RAW_DENOISE_COMPLETE`;
- TIFF 16-bit structural validation;
- authentic Darktable XMP extraction/preservation;
- Python `/v1/feedback/inspect`;
- in-memory reduced inspection preview;
- conservative normalized FeedbackRecipe;
- original-first Pass 2;
- RAW full-size input policy;
- JPEG-only first-class adaptation;
- bounded technical retries;
- restart/idempotency/recovery;
- immutable portable JSON/XMP history;
- Pass 1 TIFF cleanup only after durable Pass 2.

Not implemented:

- Darktable Neural Restore execution;
- DNG creation;
- arbitrary recipe-to-XMP compilation;
- creative FEEDBACK correction thresholds;
- ComfyUI;
- QA execution;
- FINAL publication;
- `OUTPUT_PUBLISHED`;
- Phase 6+ features.

## Darktable policy

The package extends the already-proven Darktable CLI adapter only for documented
export controls required by FEEDBACK:

- optional XMP input;
- isolated config/cache/library;
- TIFF `bpp=16`;
- RGB TIFF (`shortfile=0`);
- high-quality export;
- no custom presets;
- JPEG quality for Pass 2.

The official darktable-cli documentation defines export format configuration as:

`--core --conf plugins/imageio/format/<FORMAT>/<OPTION>=<VALUE>`

and documents TIFF `bpp` values 8/16/32 plus `shortfile=0` for RGB.

The exact installed Darktable 5.6.0 behavior must be verified by Codex before
Phase 5 can close.

## Authentic XMP

No XMP is synthesized.

Pass 1 extracts the authentic Darktable packet embedded in TIFF tag 700.
Pass 2 supplies that exact packet back to `darktable-cli` as the XMP input and
extracts the authentic packet from the resulting JPEG.

All permanent XMP files are immutable and content-verified.

## Inspector

Python receives:

- Pass 1 TIFF path;
- persisted Phase 3 Analysis;
- input kind;
- raw support status;
- Pass 1 Darktable/XMP identity.

It validates a 16-bit TIFF, creates a reduced in-memory sRGB-order preview and
returns:

- technical observations;
- a versioned normalized FeedbackRecipe.

The baseline is deliberately conservative:

- no creative operations;
- no arbitrary XMP;
- Raw Denoise OFF;
- RGB Denoise OFF;
- Upscale OFF.

This is a technical contract baseline, not a benchmarked creative-quality claim.

## JPEG-only

The approved global V1 policy makes JPEG-only first-class. Since the older
FEEDBACK prose is RAW-centric, ADR-021 explicitly proposes:

`managed JPEG original → Pass 1 → inspect → managed JPEG original → Pass 2`

Raw-specific stages remain skipped.

## Recovery

Safe recovery points:

- Pass 1 checkpoint → validate/reuse TIFF + XMP;
- inspection checkpoint → validate/reuse normalized recipe;
- Pass 2 portable history written but DB checkpoint failed → validate/reuse
  Pass 2 artifact instead of re-exporting;
- successful Pass 2 checkpoint → queue removal and `PROCESSING → QA`.

No stage overwrites another attempt silently.

## Required audit

The Windows validation gate must test at minimum:

- Migration 006 fresh and 005→006;
- full-size A7 IV RAW FEEDBACK;
- JPEG-only FEEDBACK;
- reduced RAW negative;
- corrupt RAW/JPEG;
- real 16-bit TIFF;
- in-memory preview;
- authentic XMP Pass 1 and Pass 2;
- pixel reproducibility when Pass 1 XMP is reapplied to original;
- proof Pass 2 source is managed original, never Pass 1 TIFF;
- Python crash/timeout/malformed response;
- Darktable failure/cancellation;
- DB failure at every checkpoint boundary;
- retry bound;
- pause/stop/restart;
- cleanup;
- zero process leaks;
- no Neural Restore execution;
- no ComfyUI/QA/FINAL work.
## Validation outcome

The real Windows/Darktable gate completed with `PASS_WITH_LIMITATIONS`.

Final evidence is recorded in:

`docs/phase5/PHASE5_FEEDBACK_REPORT.md`

This document remains as implementation-history documentation. The final
Phase 5 report is the authoritative validation record.
