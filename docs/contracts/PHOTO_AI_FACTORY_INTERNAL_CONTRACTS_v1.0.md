# Internal Contracts v1.0

## C# ↔ Python AI Worker

Base conceptual:

```text
http://127.0.0.1:<port>/v1/
Authorization: Bearer <session-token>
```

Operaciones:

```text
GET  health
GET  capabilities
GET  models/status
POST models/prepare
POST models/release
POST analyze
POST preselect
POST recipe/pre-ai
POST feedback/inspect
POST qa
```

Todo request incluye:

```text
api_version
request_id
job_id
operation
input_paths
config
```

Todo response incluye:

```text
api_version
request_id
success
result
error
timings
```

## C# ↔ ComfyUI

El adapter C# utiliza las rutas disponibles en la versión bloqueada de ComfyUI.

Baseline conceptual:

- `/prompt`
- `/ws`
- `/history/{prompt_id}`
- `/queue`
- `/interrupt`
- `/free`
- `/system_stats`

No se acopla Domain a estos nombres: quedan detrás de `IComfyUiAdapter`.

## Recipes

Python produce estructuras normalizadas, no XMP.

```text
PhotoRecipe
FeedbackRecipe
ComfyPlan
QaResult
```

`DarktableControlBridge` traduce únicamente parámetros whitelisted y validados.

## Errors

Cada error deberá incluir:

```text
code
category
retryable
component
message
details
```

No usar texto libre como única señal para determinar retry.
