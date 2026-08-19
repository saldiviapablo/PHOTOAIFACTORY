# PHOTO AI FACTORY — Arquitectura Técnica v1.0

**Estado:** APROBADO COMO BASELINE  
**Fecha:** 2026-08-12  
**Padres:** PRD v1.1 / SRS v1.1

---

## 1. Objetivo

Definir una arquitectura Windows local, durable, reproducible y recuperable para
una línea automatizada de fotografía basada en C#, Python, Darktable y ComfyUI.

---

## 2. Decisiones centrales

### ARC-001 — C# es la fuente de verdad

`PHOTO AI FACTORY.exe` controla:

- proyectos;
- Photos y Assets;
- Jobs;
- cola;
- estados;
- ConfigVersions;
- checkpoints;
- cancelación;
- retries;
- publicación;
- revisión;
- salud de componentes;
- asignación de GPU;
- persistencia SQLite.

Ni Python ni ComfyUI ni Darktable pueden cambiar por sí mismos el estado durable
de un Job.

### ARC-002 — Un único escritor SQLite

Solo C# escribe la base operativa.

Los procesos externos devuelven resultados estructurados.

### ARC-003 — Python es AI Worker

Python administra modelos que corren dentro del worker y ejecuta:

- análisis;
- preselección;
- embeddings;
- VLM;
- receta PRE-AI;
- inspección FEEDBACK;
- QA IA;
- generación de `ComfyPlan`.

Python no controla la cola ni la publicación final.

### ARC-004 — C# controla ComfyUI directamente

Python decide **qué trabajo IA conviene** y devuelve un `ComfyPlan`.

C#:

1. valida que la tarea esté autorizada;
2. obtiene un lease de GPU;
3. materializa/parametriza un workflow versionado;
4. envía `/prompt`;
5. escucha `/ws`;
6. confirma salida mediante `/history/{prompt_id}`;
7. puede usar `/interrupt` para cancelación segura;
8. registra checkpoint.

Así ComfyUI no convierte a Python en un segundo orquestador.

### ARC-005 — Darktable detrás de un Control Bridge

No se asumirá que cualquier parámetro de Darktable es controlable directamente
por CLI.

El `DarktableControlBridge` solo podrá utilizar mecanismos **validados y
versionados**, por ejemplo:

- `darktable-cli`;
- XMP previamente validado;
- styles/presets;
- opciones oficiales de export;
- un compiler de XMP limitado a módulos/parámetros confirmados por PoC.

**Está prohibido construir XMP arbitrario basándose en supuestos no validados.**

### ARC-006 — GPU Resource Coordinator

La RTX 4060 Ti de referencia tiene 8 GB. Python, Darktable AI y ComfyUI no
deberán pelear por VRAM.

C# administrará un lease exclusivo de GPU para las etapas pesadas:

```text
ANALYSIS   → Python
DARKTABLE  → Darktable / AI, si aplica
COMFYUI    → ComfyUI
QA         → Python
```

Antes de ceder el lease:

- el propietario anterior recibe orden de liberar/parkear modelos;
- se valida memoria disponible;
- se registra uso y timeout.

### ARC-007 — Línea fija, estaciones condicionales

La secuencia global es fija. Las estaciones pueden saltarse si la configuración
lo permite.

No habrá un agente que invente el orden de herramientas por fotografía.

---

## 3. Vista general

```text
┌────────────────────────────────────────────────────┐
│ PHOTO AI FACTORY.exe — C# / .NET / WinUI 3        │
│                                                    │
│ UI                                                 │
│ Application Core                                   │
│ Domain / State Machine                             │
│ Queue / Job Orchestrator                           │
│ Checkpoints / Retry / Cancel                       │
│ SQLite Repositories                                │
│ Ingestion + Reconciliation                         │
│ Process Supervisor / Watchdog                      │
│ GPU Resource Coordinator                           │
│ Darktable Control Bridge                           │
│ ComfyUI Adapter                                    │
│ Python AI Client                                   │
└─────────┬──────────────────┬─────────────────┬──────┘
          │                  │                 │
          ▼                  ▼                 ▼
   Python AI Worker      darktable-cli      ComfyUI
   HTTP loopback         child process      local server
```

---

## 4. C# Solution

Capas:

```text
PhotoAIFactory.App
PhotoAIFactory.Application
PhotoAIFactory.Domain
PhotoAIFactory.Infrastructure
PhotoAIFactory.Contracts
```

### App

WinUI 3 solamente.

No contiene lógica de inferencia ni acceso directo a procesos externos.

### Application

Casos de uso:

- crear/abrir proyecto;
- ingestión;
- queue dispatch;
- ejecutar Job;
- pausa/stop/cancel;
- review;
- recovery;
- publish.

### Domain

Entidades, estados, invariantes y reglas sin dependencia de UI/SQLite/Python.

### Infrastructure

- Microsoft.Data.Sqlite repositories;
- filesystem;
- process supervision;
- HTTP clients;
- Darktable bridge;
- ComfyUI adapter;
- logs;
- recursos del sistema.

### Contracts

DTOs/schemas versionados.

---

## 5. Host y servicios de fondo

Usar `Microsoft.Extensions.Hosting`, DI, configuration y logging dentro de la
aplicación WinUI.

Background services:

```text
FileWatcherService
ReconciliationService
QueueDispatcherService
ProcessSupervisorService
HealthMonitorService
GpuResourceCoordinator
CleanupService
BackupService
```

---

## 6. C# ↔ Python

### Transporte V1

```text
HTTP + JSON
127.0.0.1
puerto dinámico/administrado
token de sesión
```

Las imágenes no se transportan como blobs salvo necesidad excepcional.

Se pasan:

- `job_id`;
- `request_id`;
- paths locales;
- configuración;
- hashes;
- resultados estructurados.

### Motivo

- simple de diagnosticar;
- natural entre C# y Python;
- health endpoint;
- versionado sencillo;
- bajo volumen de mensajes;
- imágenes de 33 MP permanecen en filesystem.

---

## 7. ComfyUI

ComfyUI se inicia headless:

- loopback;
- sin auto-launch;
- puerto administrado;
- sin UI durante operación normal.

### Flujo

```text
Python → ComfyPlan
C# valida plan
C# obtiene GPU lease
C# → /prompt
C# ← /ws
C# → /history/{prompt_id}
C# valida output
C# guarda checkpoint
```

Cancelación:

- pendientes → gestión `/queue`;
- activo → `/interrupt` cuando sea seguro.

`/free` puede utilizarse para liberar recursos entre super-etapas cuando resulte
compatible con la versión fijada.

---

## 8. Darktable Control Bridge

Darktable es un proceso externo, no una librería embebida.

El bridge debe:

- conocer versión exacta;
- crear comando sin shell string concatenation;
- aplicar XMP/style solo mediante estrategias validadas;
- capturar stdout/stderr/exit code;
- validar salida;
- registrar parámetros;
- aislar particularidades de Darktable del resto del dominio.

### Gate DT-01 — obligatorio

Antes del desarrollo del pipeline completo se debe demostrar:

1. ARW A7 IV → export headless reproducible.
2. Aplicación de XMP conocido.
3. TIFF 16-bit correcto.
4. JPEG final correcto.
5. Modificación automatizada de un conjunto mínimo de módulos requeridos por
   PRE-AI/FEEDBACK.
6. Pass 2 desde original reproduce la receta prevista.
7. Comportamiento de neural restore automatizable o, si no lo es, ruta
   alternativa definida.

Si 5–7 no son robustos, no se oculta el problema: se revisa el bridge o la
estrategia RAW antes de seguir.

---

## 9. Neural Restore de Darktable

Darktable 5.6 incorpora tareas AI específicas de restauración:

- raw denoise;
- RGB denoise;
- upscale.

Estas son herramientas especializadas, no un editor creativo general.

Política:

- preferir Raw Denoise para RAW difícil cuando benchmark lo justifique;
- normalmente Raw Denoise **o** RGB Denoise, no ambos;
- upscale cerca del final;
- utilizar funciones de Darktable AI solo si el control headless exacto queda
  validado por PoC;
- si no, realizar la tarea equivalente en una estación Python/ComfyUI autorizada.

Nunca ejecutar dos herramientas equivalentes por costumbre.

---

## 10. SQLite

### Live DB

La base operativa vivirá **siempre en almacenamiento local**:

```text
%LOCALAPPDATA%\PhotoAIFactory\projects\<project_id>\project.db
```

Motivo: la base activa no debe depender de que la carpeta de exportación sea
removible, lenta o de red.

### Project portability

En:

```text
<OUTPUT>\.photo-ai-factory\
```

se guardarán:

- manifests inmutables;
- XMP;
- history JSON;
- snapshots/backups del DB;
- configuración;
- component manifest.

### Escritura

Solo C#.

Configuración inicial prioriza durabilidad:

```text
foreign_keys = ON
journal_mode = WAL
synchronous = FULL
```

Siempre sobre disco local.

---

## 11. Originales administrados

Política:

```text
COPY_TO_PROJECT
```

Ubicación:

```text
<OUTPUT>\.photo-ai-factory\originals\
```

Se copiará y validará:

- RAW;
- JPEG de cámara asociado;
- JPEG master cuando sea JPEG-only.

El original de la carpeta de entrada jamás se mueve ni modifica.

El Asset solo pasa a `ARCHIVED` después de validación.

---

## 12. Workspace temporal

```text
%LOCALAPPDATA%\PhotoAIFactory\work\<project_id>\<job_id>\<attempt_id>\
```

Puede contener:

- preview;
- Pass 1 TIFF;
- DNG temporal;
- Comfy input/output;
- staging JPEG.

Success → cleanup.

Error → retención temporal configurable.

---

## 13. Ingesta

No confiar únicamente en `FileSystemWatcher`.

Usar:

```text
watcher de baja latencia
+
escaneo de reconciliación
```

Flujo:

```text
detectado
→ WAITING_FOR_FILE
→ estabilidad
→ identidad/hash
→ asociación RAW/JPEG
→ copia administrada
→ persistir
→ analizar
```

---

## 14. Cola y concurrencia

```text
MaxConcurrentHeavyJobs = 1
```

FIFO por defecto.

Único override V1:

```text
PROCESS_NEXT
```

Servicios livianos pueden ejecutarse en paralelo.

---

## 15. Checkpoints

Checkpoints posibles:

```text
INGEST_COMPLETE
ORIGINAL_ARCHIVED
ANALYSIS_COMPLETE
PRESELECTION_COMPLETE
DARKTABLE_PASS1_COMPLETE
FEEDBACK_INSPECTION_COMPLETE
RAW_DENOISE_COMPLETE
DARKTABLE_PASS2_COMPLETE
COMFYUI_COMPLETE
QA_COMPLETE
OUTPUT_PUBLISHED
```

Un checkpoint solo existe después de validación + persistencia.

---

## 16. Idempotencia

Toda etapa usa:

```text
job_id
attempt_id
stage_id
```

Los outputs de un intento no sustituyen silenciosamente a los de otro.

Publicación final:

```text
staging
→ validate
→ persist history
→ atomic-ish publish/rename
→ OUTPUT_PUBLISHED
```

Nunca overwrite silencioso.

---

## 17. Flujo PRE-AI

```text
original administrado
→ análisis Python
→ PhotoRecipe
→ DarktableRecipeCompiler validado
→ Darktable
→ ComfyUI opcional
→ QA
→ publish
```

---

## 18. Flujo DT-AUTO

```text
original administrado
→ Darktable (presets/automatismos permitidos)
→ ComfyUI opcional
→ QA
→ publish
```

---

## 19. Flujo FEEDBACK

```text
RAW original administrado
→ Darktable Pass 1
→ TIFF 16-bit + XMP + params
→ análisis técnico TIFF
→ preview sRGB RAM para VLM
→ FeedbackRecipe
→ decisión Raw Denoise/otras tareas
→ volver al RAW original
   └─ si Raw Denoise autorizado: DNG derivado temporal
→ Darktable Pass 2
→ ComfyUI opcional
→ QA
→ publish
→ cleanup
```

Pass 2 nunca edita el TIFF/JPEG de Pass 1.

---

## 20. QA

Decisiones:

```text
QA_PASS
QA_REVIEW
QA_REPROCESS
QA_TECH_RETRY
QA_FATAL
```

- quality reprocess automático: máximo 1;
- technical retries: initial + máximo 2;
- fatal: 0 retries;
- repeated component faults → circuit breaker.

---

## 21. Health / circuit breaker

Componentes:

- Python;
- Darktable capability;
- ComfyUI;
- storage;
- GPU;
- model inventory.

Estados:

```text
STARTING
HEALTHY
DEGRADED
UNHEALTHY
STOPPED
```

Un componente `UNHEALTHY` bloquea únicamente etapas que dependen de él.

---

## 22. Seguridad local

- APIs internas solo loopback.
- Token efímero de sesión.
- No cloud por defecto.
- No shell commands concatenados.
- Paths validados.
- Modelos/workflows con checksum.
- Outputs validados antes de publicar.

---

## 23. Version locking

No se fija “latest” durante un Job.

El bootstrap genera un `components.lock.json` con:

- versión;
- origen;
- checksum;
- licencia;
- compatibilidad.

Actualizar componentes nunca cambia un Job histórico.

---

## 24. Backups

- snapshot local del DB antes de migraciones;
- backups periódicos;
- copia/snapshot en `.photo-ai-factory/backups`;
- manifests JSON permiten auditoría y reconstrucción parcial.

---

## 25. Observabilidad

Logging estructurado:

```text
timestamp
level
component
project_id
photo_id
job_id
attempt_id
stage
event
duration_ms
```

`job_id` es correlation ID principal.

---

## 26. Estructura de solución

```text
src/
├── csharp/
│   ├── PhotoAIFactory.App/
│   ├── PhotoAIFactory.Application/
│   ├── PhotoAIFactory.Domain/
│   ├── PhotoAIFactory.Infrastructure/
│   └── PhotoAIFactory.Contracts/
└── python/
    └── ai-worker/
        ├── api/
        ├── model_manager/
        ├── analysis/
        ├── recipes/
        ├── feedback/
        ├── qa/
        └── common/
```

---

## 27. Gates antes de feature development completo

### Gate DT-01
Darktable headless + receta.

### Gate IPC-01
C# inicia Python, health, request, timeout, cancel/restart.

### Gate CUI-01
C# ejecuta workflow ComfyUI por API, observa WebSocket, recupera history y cancela.

### Gate GPU-01
Transferencia estable de GPU lease entre Python → Darktable/ComfyUI → Python.

### Gate REC-01
Crash injection y recuperación por checkpoints.

### Gate ING-01
Archivo lento, pares RAW/JPEG, duplicados y reconciliación.

---

## 28. Estado

Arquitectura v1.0 aprobada como baseline.

Las futuras decisiones de implementación se documentarán mediante ADRs y no
modificarán PRD/SRS salvo que realmente cambie el producto o su comportamiento.
