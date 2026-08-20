# PHOTO AI FACTORY — PHASE 4 BASIC REVEAL REPORT

**Result:** CLOSED / GO WITH DOCUMENTED LIMITATIONS
**Baseline:** `cc84bc78a33b089c0940e44ad150b6ca9fdf3d3f`
**Validation date:** 2026-08-20
**ADR:** ADR-020 — Accepted

## Build and automated regression

- Release build: PASS using .NET SDK 10.0.400, 0 errors, 0 warnings.
- C#: 163/163 PASS — Foundation 112/112, Simulation 51/51.
- Dedicated Phase 4 C#: 20/20 PASS.
- Python: 15/15 PASS using the approved isolated runtime.
- Dedicated Phase 4 Python: 4/4 PASS, plus the updated worker contract test.
- Warning: only the known `StarletteDeprecationWarning` remains.
- NuGet vulnerable-package scan: no known vulnerable packages.

## Migration 005

Migration 005 passed fresh database creation and upgrade from 004, including
pre-migration backup, catalog checksum, idempotent reopen, drift detection,
transaction rollback and `integrity_check=ok`. The resulting database retains
WAL journal mode, synchronous FULL and foreign keys enabled. Recipe, processing
pass and checkpoint records are immutable under the validated storage rules.

## Real Darktable 5.6.0 evidence

| Mode | Input | Dimensions | Bytes | Duration | SHA-256 prefix | Result |
|---|---|---:|---:|---:|---|---|
| DT_AUTO | RAW | 7032×4688 | 18,137,004 | 16.035 s | `334318d5...` | PASS |
| DT_AUTO | JPEG-only | 7008×4672 | 5,647,339 | 15.143 s | `c9380183...` | PASS |
| PRE_AI | RAW | 7032×4688 | 18,137,004 | 15.201 s | `847ad912...` | PASS |
| PRE_AI | JPEG-only | 7008×4672 | 5,647,339 | 15.164 s | `c6b50db4...` | PASS |

The validated process/control pattern was:

```text
--hq true
--apply-custom-presets false
--core
--configdir <isolated directory>
--cachedir <isolated directory>
--library :memory:
--conf plugins/imageio/format/jpeg/quality=<value>
```

The production bridge uses `UseShellExecute=false`, passes arguments through
`ArgumentList`, captures stdout/stderr/exit code and supports timeout and
cancellation. Windows paths with spaces and Unicode were validated. It does not
use shell string concatenation, Neural Restore, or an unvalidated style/preset
dependency. This evidence does not claim arbitrary Darktable sliders are
headless-controllable.

## PRE_AI contract

PRE_AI v1 remains a conservative normalized boundary:

```text
schema_version=1
recipe_version=phase4-pre-ai-v1
strategy=CONSERVATIVE_BASELINE
benchmark_status=NOT_CALIBRATED
operations=[]
darktable_control.mode=DEFAULT_PIPELINE
```

The normalized recipe/control boundary is proven. Creative quality is not
benchmark-approved and this baseline is not a final creative AI editing model.
C# remains the source of truth and the only SQLite writer.

## JPEG quality evidence

- quality 35: 442,212 bytes;
- quality 80: 1,660,903 bytes;
- output hashes and sizes differed.

This confirms that the documented Darktable export configuration affected JPEG
encoding as intended.

## XMP and portable history

```text
XMP_HISTORY = PROVEN
```

The authentic 7,375-byte Darktable XMP metadata package from the validated
fixture was preserved exactly; its SHA-256 prefix is `737c8c3e...`. The package
was reapplied to the original RAW through `darktable-cli` 5.6.0 with exit code 0
and reproduced a pixel-identical result. No XMP module blobs or history
internals were fabricated.

This approves exact extraction, preservation and reapplication only; it does
not authorize a generic arbitrary recipe-to-XMP compiler.

SQLite is the operational durable truth. Immutable JSON and authentic XMP are
complementary portable reproducibility and audit artifacts. The JSON records
input/config/recipe/engine/output provenance and explicitly records
`final_published=false`.

## Durability, recovery and pipeline boundary

Validated PASS:

- `BASIC_REVEAL_COMPLETE` ordering, with no checkpoint before validated
  artifact/history persistence;
- `PROCESSING → QA` as a waiting boundary only;
- reveal queue removal after success;
- replay and recovery without a redundant Darktable rerun;
- no duplicate recipe, pass or checkpoint;
- database rollback before checkpoint;
- recovery after an already completed export;
- PROCESS_NEXT/FIFO semantics;
- PAUSED does not claim a new Job;
- cancellation becomes `INTERRUPTED`;
- initial attempt plus at most two technical retries, with no infinite retry;
- partial cleanup and collision rejection;
- source and managed-original path/hash immutability;
- one heavy Job at a time;
- FEEDBACK remains deferred.

Explicit boundary facts:

```text
OUTPUT_PUBLISHED = NOT WRITTEN
FINAL = NOT PUBLISHED
QA EXECUTION = NOT IMPLEMENTED
COMFYUI = NOT USED
FEEDBACK = NOT IMPLEMENTED
```

## Documented limitations

1. **PRE_AI creative policy:** `NOT_CALIBRATED / BENCHMARK_REQUIRED`. The
   contract is validated; creative quality is not benchmark-approved.
2. **JPEG byte reproducibility:** Darktable JPEG SHA-256 can differ between
   reruns because metadata contains time-varying values. Decoded pixels were
   pixel-identical. Reproducibility is preserved through processing config,
   normalized recipe, authentic XMP, Darktable version, pixel equivalence and
   portable output history; byte-identical JPEG repeatability is not claimed.
3. **Darktable process crash:** an OS-level forced kill of the Darktable
   executable was not performed for safety. Real non-zero failure, corrupt
   input, cancellation, bounded retry, cleanup and recovery were validated.
   Classification: `DOCUMENTED_TEST_LIMITATION`.

## Closure decision

Phase 4 Basic Reveal is closed and ready to hand off to the next fixed pipeline
station with the limitations above. Phase 5 — FEEDBACK is next and has not been
started.
