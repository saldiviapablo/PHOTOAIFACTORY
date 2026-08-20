# ADR-020 — Basic Reveal durable boundary and deferred final publication

**Status:** Accepted
**Date:** 2026-08-20
**Parents:** PRD v1.1 / SRS v1.1 / Architecture v1.0 / ADR-009 / ADR-013

## Context

Phase 4 introduces `DT_AUTO` and `PRE_AI` before FEEDBACK, ComfyUI and QA are
implemented.

The approved pipeline is:

```text
REVEAL
→ OPTIONAL ENHANCEMENT
→ QA
→ PUBLISH
```

A successful Darktable export in Phase 4 therefore cannot be treated as a
published final image.

At the same time, the Darktable result must be durable and replay-safe so a
crash does not force an already validated reveal to run again unnecessarily.

The exact creative PRE_AI recipe/model remains benchmark-pending in the PRD.
The Darktable bridge is also forbidden from inventing arbitrary XMP internals.

## Decision

Introduce the Phase 4 checkpoint:

```text
BASIC_REVEAL_COMPLETE
```

It may exist only after:

1. the managed input path and SHA-256 are validated;
2. Darktable exits successfully;
3. the staging JPEG is structurally validated;
4. the managed input SHA-256 is verified again;
5. the normalized recipe/control plan is validated;
6. the Phase 4 portable history record is materialized without overwrite;
7. C# persists recipe/validated Output/pass/checkpoint atomically in SQLite.

After this checkpoint the Job transitions:

```text
PROCESSING → QA
```

and its reveal queue entry is removed.

`QA` here means the Job is waiting at the next fixed pipeline station. Phase 4
does not execute QA.

Phase 4 does **not**:

- write `OUTPUT_PUBLISHED`;
- publish into `FINAL`;
- mark the Job `COMPLETED`;
- execute ComfyUI;
- execute QA;
- execute FEEDBACK.

The staging JPEG remains an attempt-owned downstream input until a later phase
publishes or safely cleans it.

## PRE_AI policy

Phase 4 establishes the normalized recipe contract but does not invent
benchmark-dependent creative decisions.

The baseline recipe is:

- schema v1;
- deterministic from persisted Analysis;
- strategy `CONSERVATIVE_BASELINE`;
- benchmark status `NOT_CALIBRATED`;
- zero creative Darktable operations;
- explicit `DEFAULT_PIPELINE` control.

`DarktableRecipeCompiler` rejects non-empty/unvalidated operations.

Python returns the normalized recipe. It never returns XMP and never writes
SQLite.

## Darktable control

The control bridge may use only documented and already validated mechanisms.

For the Phase 4 baseline:

- `darktable-cli` is the only reveal engine;
- custom presets are explicitly disabled;
- styles are not enabled because they require a separately controlled/validated
  Darktable config/catalog;
- JPEG quality is passed through the documented export configuration mechanism;
- no Neural Restore operation is invoked;
- no arbitrary XMP is generated.

The bridge retains support for applying a previously validated XMP input, with
optional SHA-256 verification, but Phase 4 does not synthesize one.

## XMP decision and validated evidence

```text
XMP_HISTORY = PROVEN
```

The Windows validation gate demonstrated preservation of the authentic XMP
metadata package embedded by Darktable in the validated JPEG artifact. The
package was preserved byte-for-byte as an immutable, attempt-scoped sidecar and
referenced by both the portable JSON history and the durable ProcessingPass.

Validated evidence:

- authentic Darktable XMP metadata package preserved;
- 7,375 bytes for the validated fixture;
- SHA-256 prefix `737c8c3e...`;
- package reapplied to the original RAW through `darktable-cli` 5.6.0;
- Darktable exit code 0;
- reproduced result was pixel-identical;
- no XMP module blobs or history internals were synthesized manually.

This decision approves only the demonstrated extraction, exact preservation and
reapplication mechanism. It does **not** authorize a generic arbitrary
recipe-to-XMP compiler. C# remains the source of truth and the only SQLite
writer.

## Retry and recovery

- reveal technical retries are stage-local and bounded to two;
- a valid `BASIC_REVEAL_COMPLETE` checkpoint is never repeated;
- stale `PROCESSING` is first recorded as `INTERRUPTED` before recovery;
- `RETRYING` and `INTERRUPTED` may resume only through valid domain transitions;
- output filenames are attempt-owned;
- partial files are removed after failure/cancel;
- no silent overwrite is permitted.

## Consequences

Positive:

- no premature final publication;
- durable recovery boundary after Darktable;
- normalized PRE_AI contract exists before quality tuning;
- no generic XMP compiler is smuggled into the bridge;
- downstream Phase 5–7 work can extend the fixed pipeline without rewriting the
  Phase 4 pass.

Trade-offs:

- PRE_AI baseline is intentionally conservative and is not a claim of superior
  image quality;
- an additional benchmark/PoC is needed before creative recipe operations are
  enabled;
- authentic XMP can be retained as a portable reproducibility artifact without
  inventing Darktable internals;
- this proof is deliberately narrower than arbitrary recipe-to-XMP generation.
