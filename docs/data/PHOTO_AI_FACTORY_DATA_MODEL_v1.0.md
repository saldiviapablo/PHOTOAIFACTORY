# Modelo de Datos v1.0

## Principios

- C# único escritor.
- IDs UUID/ULID textuales.
- Timestamps UTC ISO-8601.
- Historial terminal inmutable.
- ConfigVersions inmutables.
- Outputs ligados a Job + Attempt.
- Rutas nunca sustituyen identidad de Asset; se conserva hash/metadata.

## Entidades

### Project
Proyecto lógico.

### ProjectConfigVersion
Snapshot inmutable del comportamiento.

### Photo
Una toma lógica.

### Asset
Archivo físico: RAW, JPEG_CAMERA, JPEG_MASTER, managed original.

### Job
Una ejecución concreta sobre una Photo.

### JobStageAttempt
Intento de una etapa.

### Checkpoint
Punto durable confirmado.

### Analysis
Resultado técnico/semántico.

### ProcessingPass
PRE_AI / DT_AUTO / FEEDBACK Pass1/Pass2.

### ModelExecution
Modelo, versión, parámetros, tiempos.

### Output
Artefacto temporal o permanente.

### ReviewItem
Elemento de REVIEW_PRE/REVIEW_FINAL.

### EventLog
Auditoría de transiciones.

### ComponentHealth
Snapshots de salud.

## Relaciones clave

```text
Project 1 ── N ConfigVersion
Project 1 ── N Photo
Photo   1 ── N Asset
Photo   1 ── N Job
Job     1 ── N StageAttempt
Job     1 ── N Checkpoint
Job     1 ── N Analysis
Job     1 ── N ProcessingPass
Job     1 ── N ModelExecution
Job     1 ── N Output
```

## Invariantes

- Un Job terminal no se reabre; reprocesar crea otro Job.
- `COMPLETED` requiere output final validado.
- ConfigVersion usada no se modifica.
- Un Asset original archivado tiene hash y ruta administrada.
- Un checkpoint apunta a outputs ya validados.
