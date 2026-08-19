# Phase 0 environment bootstrap

These scripts install only the external Phase 0 runtimes and approved baseline
models. Large files are stored under `%LOCALAPPDATA%\PhotoAIFactory`; the
repository contains only scripts, locks, and small reports.

Run from PowerShell:

```powershell
.\tools\bootstrap\bootstrap-phase0.ps1
```

The bootstrap is idempotent: downloads have pinned SHA-256 values, existing
valid files are reused, and mismatched files are preserved with an `.invalid-*`
suffix. ComfyUI listens only on `127.0.0.1` and has explicit start/stop scripts.

`verify-environment.ps1` performs a non-destructive GPU, Darktable, Python,
ONNX Runtime, ComfyUI, model, checksum, and disk-space audit. It writes the
machine-local component lock to `config/components.lock.local.json`.

`PHOTO_AI_FACTORY_HOME` can override the external root. Do not point it at the
repository.

