# Implementation Plan v1.0

## Fase 0 — PoCs / gates

No UI completa.

1. DT-01 Darktable bridge.
2. IPC-01 C# ↔ Python.
3. CUI-01 ComfyUI adapter.
4. GPU-01 GPU leases.
5. ING-01 ingestion.
6. REC-01 checkpoints/crash recovery.

**Salida:** decisión GO / FIX / REASSESS.

## Fase 1 — Foundation

- solución C#;
- Generic Host;
- logging;
- SQLite;
- migrations;
- Domain state machine;
- ProjectService;
- ConfigVersion.

## Fase 2 — Ingesta

- watcher;
- reconciliation;
- file stability;
- pairing;
- archive originals;
- duplicates.

## Fase 3 — Analysis / preselection

- Python Worker;
- OpenCV;
- detector;
- face/pose;
- semantic modes;
- DINO;
- REVIEW_PRE.

## Fase 4 — Revelado básico

- DT_AUTO;
- PRE_AI;
- normalized recipe;
- XMP/history;
- export.

## Fase 5 — FEEDBACK

- Pass 1;
- TIFF;
- inspection;
- Pass 2;
- neural restore decision.

## Fase 6 — ComfyUI

- plans;
- workflows;
- ON/OFF/AUTO;
- interrupt/retry;
- enhancement tasks.

## Fase 7 — QA / review

- QA classes;
- one quality reprocess;
- REVIEW_FINAL;
- manual overrides.

## Fase 8 — Recovery / hardening

- crash injection;
- disk full;
- OOM;
- component health;
- backups.

## Fase 9 — UX final

- dashboard;
- queue;
- review;
- settings;
- history;
- model/component screen.

## Fase 10 — Packaging

- installer;
- component manager;
- license notices;
- updates;
- signed release.
