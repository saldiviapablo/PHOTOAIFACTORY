# Test Plan v1.0

## Fase 0 — Hard Gates

### GATE DT-01 — Darktable Control Bridge

**Bloquea el pipeline completo.**

Testear con Sony A7 IV ARW reales:

- CLI headless;
- XMP conocido;
- TIFF 16-bit;
- JPEG;
- presets/styles;
- subset de módulos necesario para receta;
- Pass 2 desde original;
- automatización de neural restore si se pretende usar.

**PASS:** parámetros observados coinciden con receta y son repetibles.

**FAIL:** no se sigue construyendo el pipeline como si el problema no existiera.

### GATE IPC-01

C#:

- inicia Python;
- health;
- request/response;
- token;
- timeout;
- cancel;
- worker crash;
- restart.

### GATE CUI-01

C#:

- inicia ComfyUI headless;
- envía prompt;
- espera por WebSocket;
- recupera history;
- valida output;
- interrupt;
- queue cancel;
- restart.

### GATE GPU-01

Con RTX 4060 Ti 8 GB:

```text
Python → release → Darktable/ComfyUI → release → Python
```

Probar 100 ciclos sin OOM acumulativo.

### GATE REC-01

Matar procesos/aplicación durante cada checkpoint y verificar recuperación.

### GATE ING-01

- copia lenta;
- archivo locked;
- JPEG primero;
- RAW primero;
- RAW tardío;
- duplicado;
- watcher overflow/reconciliation.

---

## Unit tests

- state machine;
- config immutability;
- naming;
- retry classification;
- recipe validation;
- ComfyPlan authorization;
- path safety;
- duplicate identity;
- queue ordering;
- checkpoint rules.

## Integration tests

- SQLite migrations/backups;
- C# ↔ Python;
- Darktable;
- ComfyUI;
- filesystem;
- GPU coordinator.

## Failure injection

- disk full;
- output read-only;
- ComfyUI kill;
- Python kill;
- Darktable exit non-zero;
- corrupted TIFF;
- corrupted RAW/JPEG;
- OOM;
- missing model;
- output collision.

## Acceptance

No feature se considera hecha solo porque funciona en el happy path.
