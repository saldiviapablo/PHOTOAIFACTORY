# PHOTO AI FACTORY — Phase 0 Gate Summary

**Fecha de cierre:** 2026-08-18  
**Decisión del Lead Engineer:** `PHASE 0 = GO / CLOSED`  
**Próxima fase:** Phase 1 Foundation

## Gate decisions and frozen evidence

| Gate | Estado aprobado | Reporte fuente de verdad | Observación retenida |
|---|---|---|---|
| DT-01 | `PASS_WITH_LIMITATIONS` | `C:\Users\Pc\Documents\PHOTO AI FACTORY TESTS\DT-01\RUN2\REPORT\DT01_RUN2_REPORT.md` | Pipeline RAW L, JPEG/TIFF 16-bit, XMP real y Pass 2 probados. Neural Restore sigue `NOT_HEADLESS_PROVEN`. |
| IPC-01 | `PASS` | `C:\Users\Pc\Documents\PHOTO AI FACTORY TESTS\IPC-01\REPORT\IPC01_REPORT.md` | Lifecycle C# → Python, auth, timeout, cancel, crash/restart y shutdown probados. Endpoints diagnósticos no son producción. |
| CUI-01 | `PASS` | `C:\Users\Pc\Documents\PHOTO AI FACTORY TESTS\CUI-01\REPORT\CUI01_REPORT.md` | Startup, REST/WebSocket/history, output, queue, interrupt y restart probados contra ComfyUI 0.33.1. Nodo delay es instrumentación. |
| GPU-01 | `PASS_WITH_LIMITATIONS` | `C:\Users\Pc\Documents\PHOTO AI FACTORY TESTS\GPU-01\REPORT\GPU01_REPORT.md` | Lease exclusivo, memoria, alternancia y crash recovery probados. No prueba Neural Restore headless ni calidad de modelos. |
| ING-01 | `PASS` | `C:\Users\Pc\Documents\PHOTO AI FACTORY TESTS\ING-01\REPORT\ING01_REPORT.md` | Watcher + reconciliation, estabilidad, pairing, deduplicación, `COPY_TO_PROJECT` y single writer probados. |
| REC-01 | `PASS` | `C:\Users\Pc\Documents\PHOTO AI FACTORY TESTS\REC-01\REPORT\REC01_REPORT.md` | 39/39 checks y 16/16 criterios: checkpoints, recovery, idempotencia, publicación, cola, retries y SQLite. |

El informe DT-01 inicial fallido se conserva separadamente como regresión
negativa para Sony RAW reducido M/S:
`C:\Users\Pc\Documents\PHOTO AI FACTORY TESTS\DT-01\REPORT\DT01_REPORT.md`.

No se copiaron al repositorio DBs, imágenes, logs ni otros artifacts pesados de
evidencia. Las rutas anteriores siguen siendo la evidencia congelada.

## Known limitations carried forward

- Darktable Neural Restore permanece `NOT_HEADLESS_PROVEN`. No se inventará XMP,
  una API headless o automatización GUI para suplirlo.
- Sony RAW M/S reducido no se procesa como RAW en V1. Debe conservarse, archivarse
  y marcarse para revisión segura; no se enviará a Darktable como RAW soportado.
- Algunos model artifacts permanecen `REVIEW_REQUIRED` por licencia y no pueden
  incorporarse o redistribuirse sin cerrar esa revisión.
- REC-01 valida crashes determinísticos de procesos/aplicación, no corte físico de
  alimentación.
- Los endpoints, custom nodes, stage helpers, workflows, schemas y rutas de test
  de los PoCs son instrumentación; no constituyen implementación productiva.
- El control Darktable probado se limita al subset documentado por DT-01. No se
  extiende por inferencia a módulos o parámetros no probados.

Estas limitaciones no bloquean Phase 1 Foundation porque esa fase puede construir
host, dominio, configuración, persistencia, migraciones y observabilidad sin
afirmar capacidades externas adicionales. Sí permanecen como restricciones para
los slices posteriores que dependan de RAW reducido, Neural Restore o artifacts
con licencia pendiente.

## Baseline protection

- PRD v1.1: no modificado durante el closeout.
- SRS v1.1: no modificado durante el closeout.
- Architecture v1.0: no modificada durante el closeout.
- ADR-001..ADR-014: no modificados durante el closeout.
- Ningún PoC ni evidencia de Gate fue eliminado o modificado.

## Final decision

`PHASE 0 = GO / CLOSED`

Phase 1 Foundation puede comenzar cuando exista autorización explícita para
implementar su primer slice.
