# Estado del proyecto

**Baseline:** 2026-08-12  
**Última actualización:** 2026-08-19  
**Estado:** PHASE 0 CLOSED / GO — PHASE 1 FOUNDATION CLOSED / GO — PHASE 2 INGESTION CLOSED / GO — PHASE 3 CLOSED / GO WITH DOCUMENTED LIMITATIONS

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

Phase 3 Analysis / Preselection está cerrada con las limitaciones documentadas. La
siguiente etapa del Implementation Plan es Phase 4 — Revelado básico: DT_AUTO, PRE_AI,
normalized recipe, XMP/history y export. No se inició trabajo de Phase 4.

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
PHASE 4 = NOT STARTED
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
