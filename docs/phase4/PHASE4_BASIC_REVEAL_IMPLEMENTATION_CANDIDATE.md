# PHOTO AI FACTORY — Phase 4 Basic Reveal implementation candidate

**Status:** VALIDATED CANDIDATE — superseded by the final Phase 4 report
**Baseline:** `cc84bc78a33b089c0940e44ad150b6ca9fdf3d3f`
**Date:** 2026-08-20

The Windows/Darktable validation described by this historical candidate is
complete. The accepted result, measured evidence and documented limitations are
recorded in [PHASE4_BASIC_REVEAL_REPORT.md](PHASE4_BASIC_REVEAL_REPORT.md).

## 1. Scope

Implemented only:

- queue claim for the next durable Job;
- one heavy reveal Job at a time;
- `DT_AUTO`;
- `PRE_AI`;
- normalized PRE_AI recipe schema;
- conservative PRE_AI baseline;
- validated Darktable JPEG staging export;
- project JPEG quality through documented CLI export configuration;
- managed-original SHA-256 verification before and after Darktable;
- immutable recipe/pass persistence;
- `BASIC_REVEAL_COMPLETE`;
- portable immutable Phase 4 history JSON;
- bounded reveal retries;
- cancellation/restart recovery boundaries;
- transition to `QA` as the next not-yet-executed station.

Explicitly not implemented:

- FEEDBACK;
- Neural Restore;
- ComfyUI;
- QA execution;
- REVIEW_FINAL;
- FINAL publication;
- `OUTPUT_PUBLISHED`;
- Phase 5+ functionality.

## 2. Requirements traceability

Primary Phase 4 requirements:

- FR-MOD-PRE-001 — PRE_AI uses the original and persisted analysis context.
- FR-MOD-PRE-002 — Python returns a structured normalized recipe.
- FR-MOD-PRE-003 — the validated C# Darktable bridge applies the authorized
  control plan to the managed original.
- FR-MOD-PRE-004 — recipe and reveal result are recorded.
- FR-MOD-DTA-001 — DT_AUTO sends the managed original to Darktable.
- FR-MOD-DTA-002 — only validated/deterministic Darktable mechanisms may be used.
- FR-MOD-DTA-003 — exact reveal history is retained.

Cross-cutting:

- processing config is the Job's immutable `processing_config_id`;
- one heavy Job;
- FIFO / PROCESS_NEXT semantics;
- safe pause does not claim a new Job;
- source and managed originals remain immutable;
- technical retry is bounded;
- checkpoints are durable and replay-safe.

## 3. Darktable policy

Only documented/proven control mechanisms are permitted.

Phase 4 extends the existing `DarktableCliAdapter` instead of bypassing it.
`DarktableExportRequest` gains optional:

- `ApplyCustomPresets`;
- `JpegQuality`.

Existing Phase 3 preview callers remain compatible because both fields are
optional.

For baseline reveal:

```text
--hq true
--apply-custom-presets false
--verbose
--core --conf plugins/imageio/format/jpeg/quality=<5..100>
```

The exact argument behavior must be confirmed against installed Darktable 5.6.0
during the gate.

Styles are intentionally disabled because a style requires a controlled
Darktable config/catalog path.

No Neural Restore is requested.

## 4. PRE_AI normalized recipe

The PRD explicitly defers the exact PRE_AI recipe/model to benchmark.

Therefore Phase 4 does not pretend that unbenchmarked exposure/color thresholds
are product-quality decisions.

`POST /v1/recipe/pre-ai` now returns schema v1:

```json
{
  "schema_version": 1,
  "recipe_version": "phase4-pre-ai-v1",
  "strategy": "CONSERVATIVE_BASELINE",
  "benchmark_status": "NOT_CALIBRATED",
  "operations": [],
  "darktable_control": {
    "mode": "DEFAULT_PIPELINE",
    "arbitrary_xmp_compilation": false,
    "style": null,
    "apply_custom_presets": false
  }
}
```

The recipe is deterministic and receives persisted Phase 3 Analysis for
provenance.

`DarktableRecipeCompiler` rejects any non-empty/unvalidated operation.

This is a technical contract baseline, not a quality benchmark result.

## 5. Managed input

Reveal input is always the frozen Job master Asset:

- RAW+JPEG → managed RAW master;
- RAW-only → managed RAW master;
- JPEG-only → managed JPEG master.

No source-folder original is used once the managed copy exists.

Before Darktable:

- file must exist;
- SHA-256 must equal the Asset row.

After Darktable:

- SHA-256 is checked again.

A mismatch is permanent/integrity failure, not an infinite retry.

## 6. Workspace and artifact

Each execution uses an attempt-owned path:

```text
%LOCALAPPDATA%\PhotoAIFactory\
└── work\<project_id>\<job_id>\<attempt_id>\
    └── reveal\
        └── basic-reveal.jpg
```

Darktable first writes:

```text
basic-reveal.partial-<uuid>.jpg
```

C# validates:

- JPEG SOI;
- decodable JPEG frame dimensions;
- non-empty file;
- SHA-256;
- width/height.

Only then is it renamed to the attempt-owned staging filename.

The result is **not** published to `FINAL`.

## 7. Migration 005

`005_basic_reveal.sql` adds:

- `jobs.reveal_retry_count` with maximum 2;
- one-`PROCESSING`-Job-per-project partial unique index;
- `BASIC_REVEAL_COMPLETE` as an allowed durable checkpoint;
- immutable `processing_recipes`;
- immutable `outputs` for validated staging artifacts;
- immutable `processing_passes` referencing the Output entity.

Migrations 001–004 are not rewritten.

Migration 005 must pass:

- fresh DB;
- 004 → 005 upgrade;
- backup;
- checksum;
- idempotent reopen;
- drift detection;
- integrity/WAL/FULL/FK checks.

## 8. Durable completion

`BASIC_REVEAL_COMPLETE` is written only after artifact/history validation.

In the same SQLite transaction C#:

1. inserts the normalized recipe when PRE_AI;
2. inserts the validated staging Output entity;
3. inserts the ProcessingPass referencing that Output;
4. inserts `BASIC_REVEAL_COMPLETE`;
5. removes the reveal queue entry;
6. transitions `PROCESSING → QA`;
7. records the state transition.

Phase 4 does not execute QA. `QA` is the next fixed pipeline station.

## 9. History

Phase 4 writes an immutable portable stage record:

```text
<OUTPUT>\
└── .photo-ai-factory\
    └── history\
        └── <photo_id>\
            └── <job_id>\
                └── basic-reveal.json
```

It records:

- project/photo/job;
- reveal mode;
- processing ConfigVersion ID + SHA-256;
- input Asset ID + SHA-256 + format;
- normalized recipe;
- Darktable control policy;
- Darktable version;
- staging output SHA-256/size/dimensions;
- explicit `final_published=false`.

A different existing history file is a collision and is never overwritten.

## 10. XMP status after validation

No arbitrary XMP is generated. The validated implementation extracts the exact
authentic Darktable XMP metadata package from the staging JPEG, preserves it as
an immutable attempt-scoped sidecar and records its path and SHA-256 in portable
history and SQLite.

The validated fixture produced a 7,375-byte package with SHA-256 prefix
`737c8c3e...`. Reapplying that exact package to the original RAW through
`darktable-cli` 5.6.0 exited 0 and reproduced pixel-identical output. No module
blobs or Darktable history internals were synthesized manually.

This proves exact preservation and reapplication only. It does not authorize a
generic arbitrary recipe-to-XMP compiler.

## 11. Recovery

A stale reveal Job is recognized only if it remains attached to a durable queue
entry.

On re-entry:

- stale `PROCESSING` → durable `INTERRUPTED` → `PROCESSING`;
- `RETRYING` → `PROCESSING`;
- `INTERRUPTED` → `PROCESSING`.

A valid `BASIC_REVEAL_COMPLETE` is never repeated.

Technical reveal retries:

```text
initial attempt + maximum 2 retries
```

No infinite retry.

A `FEEDBACK` FIFO head is not skipped; Phase 4 stops and defers it to Phase 5.

## 12. Required Phase 4 gate

Codex must validate on the real Windows PC:

- Release build;
- all C# + Python tests;
- Migration 005 fresh and 004→005;
- DT_AUTO full-size A7 IV RAW;
- DT_AUTO JPEG-only;
- PRE_AI full-size A7 IV RAW;
- PRE_AI JPEG-only;
- RAW+JPEG selects managed RAW master;
- JPEG quality behavior on Darktable 5.6.0;
- repeatability;
- source and managed-original hashes;
- portable history;
- PRE_AI contract/correlation/malformed response;
- Darktable timeout/cancel/crash;
- DB failure before checkpoint;
- restart before/after checkpoint;
- retry bounds;
- partial cleanup;
- FIFO / PROCESS_NEXT;
- safe pause;
- no second `PROCESSING` Job;
- FEEDBACK head deferral;
- no ComfyUI/QA/FINAL work;
- XMP limitation/proven mechanism assessment.

The gate was reviewed on 2026-08-20. The accepted result is `CLOSED / GO WITH
DOCUMENTED LIMITATIONS`; see the final Phase 4 report.
