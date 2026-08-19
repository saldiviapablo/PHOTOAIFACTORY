# AI Pipeline v1.0

## Regla

Modelos que **entienden** y modelos que **modifican** están separados.

## Línea V1

```text
INGEST
→ ANALYSIS
→ PRESELECTION
→ QUEUE
→ REVEAL
→ OPTIONAL ENHANCEMENT
→ QA
→ PUBLISH
```

## ANALYSIS — GPU lease Python

Orden fijo:

1. OpenCV
2. RF-DETR
3. MediaPipe Face
4. MediaPipe Pose cuando corresponda
5. Florence-2 según semantic mode
6. Qwen3-VL según semantic mode/necesidad
7. DINOv2 embeddings

Outputs estructurados, no decisiones creativas libres.

## REVEAL

### PRE_AI
Python → receta normalizada → Darktable bridge.

### DT_AUTO
Darktable presets/automatismos/funciones validadas.

### FEEDBACK
Pass 1 → TIFF 16-bit → inspección → receta → original → Pass 2.

## Darktable AI

Puede decidirse Raw Denoise/RGB Denoise/Upscale si:

1. tarea autorizada;
2. control headless validado;
3. benchmark la aprueba.

Normalmente no usar Raw Denoise + RGB Denoise simultáneamente.

## ComfyUI

Python genera un `ComfyPlan`.

C# ejecuta solamente tareas:

- autorizadas;
- disponibles;
- con licencia aprobada;
- con workflow versionado.

## QA — GPU lease Python

Especialistas primero.

VLM solo en casos semánticos/dudosos.

## GPU leases

```text
Python Analysis
→ release/park
→ Darktable
→ release
→ ComfyUI
→ free/release
→ Python QA
```

No existe carga simultánea “porque sí”.
