# Estado del proyecto

**Baseline:** 2026-08-12  
**Última actualización:** 2026-08-22
**Estado:** PHASE 0 CLOSED / GO — PHASE 1 FOUNDATION CLOSED / GO — PHASE 2 INGESTION CLOSED / GO — PHASE 3 CLOSED / GO WITH DOCUMENTED LIMITATIONS — PHASE 4 CLOSED / GO WITH DOCUMENTED LIMITATIONS — PHASE 5 CLOSED / GO WITH DOCUMENTED LIMITATIONS

## Documentos congelados como baseline

- PRD v1.1
- SRS v1.1
- Arquitectura Técnica v1.0
- Modelo de Datos v1.0
- Contratos Internos v1.0
- Pipeline IA v1.0
- Plan de Pruebas v1.0
- Plan de Benchmark v1.0
- Estrategia de Deployment v1.0

## Cierre de Phase 0

- DT-01: `PASS_WITH_LIMITATIONS`.
- IPC-01: `PASS`.
- CUI-01: `PASS`.
- GPU-01: `PASS_WITH_LIMITATIONS`.
- ING-01: `PASS`.
- REC-01: `PASS` — 39/39 checks y 16/16 criterios obligatorios.

REC-01 demostró recuperación por checkpoints, revalidación de artifacts,
idempotencia, publicación crash-safe, recuperación FIFO, retries acotados,
consistencia SQLite, originales intactos y cero procesos huérfanos.

Decisión del Lead Engineer:

```text
PHASE 0 = GO / CLOSED
PHASE 1 = NEXT
```

## Limitaciones conservadas

- Darktable Neural Restore continúa `NOT_HEADLESS_PROVEN`; no inventar control
  XMP/headless ni automatización GUI.
- Sony RAW reducido M/S no se procesa como RAW en V1; se conserva y deriva a
  revisión segura.
- Algunos artifacts de modelos continúan `REVIEW_REQUIRED` por licencia.
- REC-01 valida crash de procesos/aplicación, no corte eléctrico físico.
- Las instrumentaciones sintéticas de los Gates no son código productivo.

## Lo siguiente

Phase 5 FEEDBACK está cerrada con las limitaciones documentadas. La siguiente
etapa del Implementation Plan es Phase 6 — ComfyUI. No se inició trabajo de
Phase 6.
## Phase 1 Foundation

- Slice 1 — Project + ConfigVersion durable: `PASS / ACCEPTED`.
- Slice 2 — Host composition + DI + runtime configuration + structured logging:
  `PASS`.
- Slice 3 — Project lifecycle + ConfigService + PAUSED guard: `PASS`, listo para
  cierre.
- Virtual Factory base — reloj determinístico, fault plan concurrente, scenario
  recorder y escenarios Foundation con SQLite productivo real: `PASS`.
- Tests C# estándar acumulados: 119/119 PASS (112 Foundation + 7 Simulation).
- FR-FS-007 y FR-CFG-001/002 son funcionales. Pause/stop/dispatch quedan
  correctamente clasificados entre functional y foundation; FR-CFG-003/004/005,
  Jobs, queue y reconciliation continúan pendientes.

Decisión de closeout:

```text
PHASE 1 — FOUNDATION = CLOSED / GO
PHASE 2 — INGESTION = CLOSED / GO
PHASE 3 — ANALYSIS / PRESELECTION = CLOSED / GO WITH DOCUMENTED LIMITATIONS
PHASE 4 — BASIC REVEAL = CLOSED / GO WITH DOCUMENTED LIMITATIONS
PHASE 5 — FEEDBACK = CLOSED / GO WITH DOCUMENTED LIMITATIONS
```

## Phase 2 Ingestion

- Watcher NTFS, estabilidad de archivo, reconciliación de startup/periódica y
  recuperación ante presión del channel: `PASS`.
- RAW/JPEG, RAW-only, JPEG-only, RAW tardío sin Jobs, duplicados exactos y source
  generations: `PASS`.
- Managed originals content-addressed, SHA-256, cancelación/partial safety y
  recuperación archive-before-SQLite: `PASS`.
- Migration 003 desde DB nueva y fixture Phase 1 (002), backup, drift,
  idempotencia, WAL/FULL/FK e integrity check: `PASS`.
- Tests C# estándar acumulados: 136/136 PASS (112 Foundation + 24 Simulation;
  17 de los Simulation tests son Phase 2).
- FR-FS-002/008, FR-ING-003/005 permanecen parciales en los límites documentados:
  composición operacional/UI, EXIF completo y Jobs activos son trabajo futuro.
- FR-ING-009 (similitud visual) queda fuera de Phase 2.

Informe: `docs/phase2/PHASE2_INGESTION_REPORT.md`.

## Phase 3 Analysis / Preselection

- C# conserva la autoridad durable y es el único writer SQLite; Python Worker entrega
  resultados AI estructurados: `PASS`.
- Migration 004, `ANALYSIS_COMPLETE`, `PRESELECTION_COMPLETE`, FIFO, checkpoints,
  replay/restart e idempotencia: `PASS`.
- OFF, STANDARD y FULL: `PASS`; STANDARD midió 11.590 s.
- FULL midió 41.424 s y queda como
  `PERFORMANCE_LIMITATION / BENCHMARK_REQUIRED`, no como cumplimiento del target normal.
- Florence usa `florence-community/Florence-2-large`, revisión
  `4271c66b88cdbc05735372ec13b2360108de5317`, con SHA-256 de `model.safetensors`
  `7715423d6549bf1e71188bdd84f4ac960cc0597886af24a5ef7b66f128660685`.
- Florence permanece `BASELINE` pendiente del benchmark de calidad; no fue promovido a
  `APPROVED`.
- ADR-018: `Accepted`. ADR-019: `Accepted`.
- Tests finales: C# 143/143; Python 11/11 con runtime aislado.
- No se inició Phase 4.

Informe: `docs/phase3/PHASE3_ANALYSIS_PRESELECTION_REPORT.md`.

## Phase 4 Basic Reveal

- Migration 005, DB nueva y upgrade 004 → 005, backup, checksum, drift,
  idempotencia, rollback, WAL/FULL/FK e integrity check: `PASS`.
- DT_AUTO sobre RAW y JPEG-only con Darktable 5.6.0: `PASS`.
- PRE_AI v1 (`phase4-pre-ai-v1`) conserva una receta normalizada
  `CONSERVATIVE_BASELINE`, `NOT_CALIBRATED`, `operations=[]` y
  `DEFAULT_PIPELINE`: `PASS / BENCHMARK_REQUIRED`.
- Historial JSON portable e inmutable: `PASS`.
- Preservación de XMP auténtico de Darktable, reapplication a RAW y equivalencia
  pixel a pixel: `PASS`.
- `BASIC_REVEAL_COMPLETE` es el límite durable aceptado por ADR-020; el Job pasa
  de `PROCESSING` a `QA` para esperar en la estación siguiente.
- ADR-020: `Accepted`.
- Tests finales: C# 163/163; Python 15/15 con runtime aislado.
- Phase 4 no ejecuta QA, no escribe `OUTPUT_PUBLISHED`, no publica `FINAL`, no
  usa ComfyUI y no implementa FEEDBACK.
- No se inició Phase 5.

Informe: `docs/phase4/PHASE4_BASIC_REVEAL_REPORT.md`.

## Phase 5 FEEDBACK
- Migration 006, DB nueva y upgrade 005 → 006, backup, checksum, drift,
  idempotencia, rollback, WAL/FULL/FK e integrity check: PASS.
- FEEDBACK RAW full-size y JPEG-only: PASS.
- Pass 1 produce TIFF RGB 16-bit + XMP auténtico; la inspección reutiliza
  Analysis de Phase 3 y genera phase5-feedback-v1: PASS.
- Pass 2 reinicia siempre desde el original administrado correspondiente y
  nunca desde el TIFF/JPEG derivado de Pass 1: PASS.
- DARKTABLE_PASS1_COMPLETE, FEEDBACK_INSPECTION_COMPLETE y
  DARKTABLE_PASS2_COMPLETE: PASS.
- Neural Restore continúa NOT_HEADLESS_PROVEN; Raw Denoise, RGB Denoise y
  Upscale permanecen desactivados. RAW_DENOISE_COMPLETE no se escribió.
- ADR-021: Accepted.
- Tests finales del audit: C# 183/183; Python 21/21 con runtime aislado.
- Performance medida: RAW 36.467 s; JPEG-only 35.548 s:
  PERFORMANCE_LIMITATION / BENCHMARK_REQUIRED.
- Receta creativa FEEDBACK: NOT_CALIBRATED / BENCHMARK_REQUIRED.
- Phase 5 no ejecuta ComfyUI, QA, OUTPUT_PUBLISHED ni publicación FINAL.
- Informe: docs/phase5/PHASE5_FEEDBACK_REPORT.md.

## Phase 6 ComfyUI

- Migration 007, DB nueva y upgrade 006 → 007, backup, checksum, drift, idempotencia, rollback, WAL/FULL/FK e integrity check: PASS.
- Append-only `comfy_plans` y `comfy_executions` con triggers SQLite: PASS.
- Pinned ComfyUI runtime: `v0.33.1` (commit `72865f4f27eaf5396f8f36370e0a2be3a9a090ee`, embedded Python `3.13.14`): PASS.
- Runtime core roundtrip model-free (`EmptyImage 64x48 -> SaveImage`, workflow `paf-validation-core-roundtrip-v1`): PASS (279 ms).
- Process supervisor lifecycle, crash recovery (`Process.Kill()`), health check (`/system_stats`), restart y cero procesos huérfanos: PASS.
- Política de modos: OFF (durable skip), AUTO (skip conservador `AUTO_POLICY_NOT_CALIBRATED`), ON con 0 tasks (durable no-op), ON con task no aprobada (`COMFY_TASK_NOT_APPROVED`, fail-closed, sin consumo de retries): PASS.
- Retries técnicos acotados (`comfy_retry_count` entre 0 y 2): PASS.
- Liberación de modelos Python (`/v1/models/release`) antes de adquirir el lease exclusivo de GPU: PASS.
- Pre-QA boundary: el Job transiciona `QA -> PROCESSING -> QA` registrando el checkpoint durable `COMFYUI_COMPLETE`: PASS.
- ADR-022: Accepted.
- Tests finales del audit: C# 209/209 (112 Foundation + 97 Simulation); Python 29/29 (25 repository + 4 worker); total ejecuciones reportadas: 238/238 PASS (100%).
- Informe: `docs/phase6/PHASE6_COMFYUI_REPORT.md`.

### Limitaciones documentadas de Phase 6
1. Los 7 workflows de enhancement fotográfico (`COLOR`, `DENOISE_RGB`, `FACE_MASKS`, `FACE_RETOUCH`, `LOW_LIGHT`, `SHARPNESS`, `UPSCALE`) permanecen `BENCHMARK_AND_LICENSE_REQUIRED`.
2. Política de AUTO enhancement permanece `AUTO_POLICY_NOT_CALIBRATED`.
3. El workflow core model-free valida el transporte y runtime, no calidad visual.
4. `/queue` no fue re-ejecutado en Phase 6; queda respaldado por Phase 0 CUI-01.
5. El test runtime core no utilizó originales fotográficos.
6. Phase 7 QA y publicación final permanecen sin implementar.

## Lo siguiente

```text
PHASE 6 — COMFYUI = CLOSED / GO WITH DOCUMENTED LIMITATIONS
PHASE 7 — QA = NOT STARTED
```
