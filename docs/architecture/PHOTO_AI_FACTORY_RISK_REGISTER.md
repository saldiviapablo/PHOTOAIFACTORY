# Risk Register v1.0

| ID | Riesgo | Impacto | Mitigación / Gate |
|---|---|---:|---|
| R-001 | Darktable no permite controlar robustamente todos los parámetros necesarios headless | Crítico | Gate DT-01 antes del pipeline completo |
| R-002 | VRAM 8 GB insuficiente con procesos coexistiendo | Alto | GPU Resource Coordinator, 1 heavy Job, unload/lease |
| R-003 | ComfyUI cambia API/workflow schema | Alto | version lock + adapter + contract tests |
| R-004 | Modelos/pesos con licencia no redistribuible | Crítico | license gate por artifact antes de bundling |
| R-005 | `FileSystemWatcher` pierde eventos | Medio | reconciliation scan |
| R-006 | SQLite en carpeta remota/removible | Alto | live DB local + snapshots en proyecto |
| R-007 | Temporales llenan disco | Alto | preflight + BLOCKED_STORAGE + cleanup |
| R-008 | VLM toma decisión técnica incorrecta | Alto | especialistas/determinísticos para decisiones críticas |
| R-009 | Retry produce efectos duplicados | Alto | idempotencia + attempt/stage IDs |
| R-010 | Upgrade rompe reproducibilidad | Alto | components.lock + manifests + new Job on reprocess |
| R-011 | RAW/JPEG se asocian mal | Medio | identidad + metadatos + source generation + ventana |
| R-012 | Auto-reprocess entra en loop | Alto | máximo 1 quality reprocess |
