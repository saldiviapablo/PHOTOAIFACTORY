# Component Map

```text
WinUI 3
  │
  ▼
Application Core
  ├── ProjectService
  ├── JobOrchestrator
  ├── QueueDispatcher
  ├── ReviewService
  ├── ConfigService
  ├── CheckpointManager
  └── PublishService
       │
       ▼
Infrastructure
  ├── SQLite repositories
  ├── Ingestion + reconciliation
  ├── ProcessSupervisor
  ├── PythonAiClient ───────→ Python AI Worker
  ├── DarktableControlBridge → darktable-cli
  ├── ComfyUiAdapter ───────→ ComfyUI REST/WebSocket
  ├── GpuResourceCoordinator
  ├── HealthMonitor
  └── ProjectStorage
```

## Ownership

| Recurso | Propietario |
|---|---|
| Job state | C# |
| Queue | C# |
| SQLite | C# |
| Checkpoints | C# |
| Python-loaded models | Python Model Manager |
| GPU scheduling | C# GPU Resource Coordinator |
| ComfyUI workflow execution | C# ComfyUiAdapter |
| Comfy task selection | Python `ComfyPlan` + C# policy validation |
| Darktable process | C# DarktableControlBridge |
| Final publish | C# |
