# PHOTO AI FACTORY — Virtual Factory Test Strategy v1.0

**Status:** Proposed test baseline for Phase 1 closeout / Phase 2 onward  
**Production impact:** None. Simulation code lives under `tests/` only.

## Purpose

Provide a deterministic, safe way to exercise PHOTO AI FACTORY orchestration without
pretending that simulated Darktable, Python, ComfyUI or GPU behavior proves the real
external engines.

The strategy complements — never replaces — Windows/hardware validation.

## Four test layers

### A. Domain / deterministic rules

Pure state machines and invariants. No filesystem, SQLite or external process.

Use this layer for:
- legal/illegal transitions;
- retry ceilings;
- queue ordering rules;
- checkpoint eligibility rules;
- publish invariants;
- configuration immutability.

### B. Real infrastructure in an isolated sandbox

Use the real production infrastructure with temporary paths:
- Microsoft.Data.Sqlite;
- migrations;
- WAL/FULL/FK;
- local filesystem;
- hashes;
- atomic-ish staging/publish behavior;
- restart/reopen.

Do not mock SQLite when the behavior under test is transactional/durable.

### C. Scripted external boundaries

Test-only adapters may return controlled outcomes:
- success;
- timeout;
- process crash;
- invalid output;
- access denied;
- disk full signal;
- OOM signal;
- hash mismatch.

The simulator must not implement queue/retry/checkpoint/business decisions.
Those decisions remain in production C#.

### D. Real-PC validation

Codex/Windows validation remains mandatory where simulation cannot prove reality:
- FileSystemWatcher behavior on Windows;
- process ownership/termination;
- Darktable CLI + Sony ARW;
- ComfyUI REST/WebSocket;
- CUDA/VRAM/model loading;
- WinUI rendering/Windows App SDK;
- installer/update/signing.

## Fault injection rules

Faults are described as data:

```text
stage + point + fault kind + occurrence
```

Example:

```text
DARKTABLE_PASS2
AFTER_OUTPUT_BEFORE_CHECKPOINT
PROCESS_CRASH
occurrence=1
```

Requirements:
- deterministic;
- thread-safe;
- reproducible;
- no hidden global state;
- no production behavior inside the injector;
- no destructive resource exhaustion (do not fill the real disk or intentionally
  consume all system RAM/VRAM for routine tests).

## Time

Time-dependent application logic should use injected `TimeProvider`.
Simulation uses a deterministic test provider and never sleeps to advance logical time.

## Scenario events

Simulation records ordered events with:
- monotonic sequence;
- UTC timestamp;
- category;
- name;
- structured string metadata.

These events are test evidence, not the production durable event store.

## Safety

Automated simulation must:
- write only to temporary isolated directories;
- never read/write user photographs;
- never launch Darktable/ComfyUI/Python unless the test category explicitly says so;
- never mutate Phase 0 evidence;
- never write the user's live `%LOCALAPPDATA%\PhotoAIFactory` tree;
- clean its temporary workspace after success/failure.

## Initial implementation

`PhotoAIFactory.Simulation.Tests` starts with:
- deterministic `TimeProvider`;
- thread-safe scripted fault plan;
- ordered scenario event recorder;
- scripted `IProjectWorkStatus`;
- real SQLite lifecycle scenarios using production repositories/services.

This proves the harness can drive the production core without cloning its business logic.

## Expansion by phase

### Phase 2 — Ingestion
Add virtual file-event source and scripted file-stability observations, while keeping
real filesystem integration tests for copy/hash/archive behavior.

### Phase 3 — Analysis
Add scripted Python boundary responses plus contract fixtures from the real AI Worker.

### Phase 4/5 — Darktable / Feedback
Add scripted Darktable outcomes and artifact validation fixtures. Real Sony RAW validation
remains a Windows test.

### Phase 6 — ComfyUI
Add scripted prompt/ws/history sequences; real server validation remains separate.

### Phase 7/8 — QA / hardening
Add reproducible scenario library, recovery seeds and fault matrices.

## Rule of evidence

A simulation PASS means:

> PHOTO AI FACTORY reacted correctly to the specified external outcome.

It does **not** mean:

> the external engine itself was proven to produce that outcome correctly.

Only the appropriate real-PC Gate/integration validation may make that claim.
