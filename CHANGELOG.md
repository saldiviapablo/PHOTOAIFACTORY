# Changelog documental

## Baseline v1.0 — 2026-08-12

- Consolidación de PRD v1.1 y SRS v1.1.
- Arquitectura Técnica promovida a v1.0 tras revisión.
- C# confirmado como fuente de verdad y único escritor de SQLite.
- ComfyUI pasa a ser controlado directamente por un adapter C#.
- Python devuelve `ComfyPlan`; no orquesta el Job.
- Se agrega `GpuResourceCoordinator` para evitar competencia entre Python,
  Darktable AI y ComfyUI en GPU de 8 GB.
- Live Project DB se almacena localmente; el proyecto conserva snapshots/backups
  y manifests para portabilidad.
- `COPY_TO_PROJECT` confirmado para originales.
- Se define Darktable Control Bridge como gate técnico obligatorio.
- Se agrega modelo de datos, contratos, schemas, test plan, benchmark plan,
  license matrix, deployment strategy, recovery runbook y backlog.
