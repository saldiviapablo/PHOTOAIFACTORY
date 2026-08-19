# Phase 1 Foundation Inventory

**Fecha:** 2026-08-18  
**Alcance:** inventario técnico con actualizaciones de implementación de los
Slices 1–3; no promueve código de PoCs.

## Slice 1 implementation update

`Project + ConfigVersion durable` quedó implementado y validado el 2026-08-18:

- Domain modela `Project`, documento de configuración V1 y `ConfigVersion`
  inmutable con JSON canónico/SHA-256.
- Application expone repositorios y `ProjectService` para crear, abrir y anexar
  configuración sin exponer SQLite al futuro App.
- Infrastructure usa `Microsoft.Data.Sqlite` 10.0.11 directo, migration
  `001_initial_project_config`, checksums, backup pre-migration, FK/WAL/FULL y
  single-writer C# por ruta de DB.
- La solución tiene ahora 7 proyectos, incluido el proyecto MSTest estándar
  `PhotoAIFactory.Foundation.Tests`.
- Tests del slice: 27/27 PASS; self-tests existentes: 6/6 PASS; Python: 4/4 PASS.

El inventario detallado que sigue conserva la fotografía previa al slice para
trazabilidad. Sus secciones de piezas ausentes y recomendación inicial deben
leerse junto con esta actualización; Generic Host/DI/logging, UI, watcher y Jobs
continúan sin implementar.

### Corrected Slice 1 traceability

- **FULL / FUNCTIONAL:** persistencia Project/ConfigVersion, FR-CFG-001 y
  FR-CFG-002.
- **PARTIAL / FOUNDATION:** FR-PRJ-003, FR-REP-001 y FR-REP-002.
- **PENDING:** FR-FS-007 y FR-CFG-003/004/005, porque requieren lifecycle
  `PAUSED`, Jobs, queue, reprocessing, modelos, recetas, XMP o workflows.

## Slice 2 implementation update

`Host composition + DI + runtime configuration + structured logging` quedó
implementado y validado el 2026-08-18:

- composición reutilizable sobre `Host.CreateApplicationBuilder` mediante
  `AddPhotoAIFactoryFoundation`;
- Options tipadas con validación de startup y grafo DI con `ValidateOnBuild` y
  `ValidateScopes`;
- `IAppPaths`/`WindowsAppPaths` y preparación idempotente de directorios técnicos;
- logger local JSONL sincronizado con scopes, session ID, exceptions y flush;
- factory DI por proyecto para los repositories SQLite de Slice 1, conservando la
  frontera single-writer por ruta;
- 58/58 tests C# estándar PASS, incluidos los 27 de Slice 1.

No se creó UI/App ni se registraron clientes/supervisors que puedan iniciar
Python, ComfyUI o Darktable.

## Slice 3 implementation update

`Project lifecycle + ConfigService + PAUSED guard` quedó implementado y validado
el 2026-08-18:

- Project nuevo inicia `STOPPED`; estado, revisión y timestamp UTC son durables;
- state machine limita las ocho transiciones aprobadas de start/resume,
  pause-safe-completion y stop-safe-completion;
- migration `002_project_lifecycle` backfills DBs Slice 1 a `STOPPED` y agrega
  auditoría append-only atómica con el cambio de estado;
- optimistic state revision y operation ID evitan transiciones duplicadas o lost
  updates; `TimeProvider` controla timestamps;
- `ConfigService` modifica ProjectConfig únicamente en `PAUSED`, no duplica una
  configuración semánticamente igual y exige expected ConfigVersion;
- dispatch guard permite trabajo nuevo sólo en `RUNNING`;
- Host registra lifecycle/config/time y una fuente neutral explícita sin Jobs; no
  inicia servicios externos ni agrega background workers;
- 112/112 tests C# estándar PASS, incluidos los 48 obligatorios de Slice 3 y seis
  invariantes adicionales.

Trazabilidad actual: FR-PAU-001/003/005 y FR-FS-007 funcionales;
FR-PAU-002/004 y FR-STOP-001/002 permanecen foundation/partial hasta Jobs y
QueueDispatcher; FR-CFG-001/002 funcionales y FR-CFG-003/004/005 pendientes.

## Current repository state

El repositorio contiene una solución C# Phase 0 sobre .NET 10, un AI Worker Python
productivo de alcance técnico y seis PoCs aislados. No es un repositorio Git en
esta ubicación: no existe `.git`, por lo que el alcance se verificó mediante
inventario de archivos, timestamps y hashes protegidos.

Elementos de build:

- `src/csharp/PhotoAIFactory.Phase0.sln` con 6 proyectos.
- `src/csharp/Directory.Build.props`: `net10.0`, nullable e implicit usings;
  `TreatWarningsAsErrors=false`.
- No existen `.slnx`, `global.json`, `Directory.Packages.props`, `NuGet.config` ni
  directorio `scripts/`.
- `db/schema_v1.sql` es un schema lógico inicial, no una migración ejecutable ni
  un migration runner.
- `config/components.lock.local.json` contiene el inventario real del host y
  componentes/modelos fijados.

## Existing production projects

| Proyecto | Existe | Estado real |
|---|---:|---|
| `PhotoAIFactory.App` | No | No existe proyecto WinUI ni composition root. |
| `PhotoAIFactory.Application` | Sí | Contiene interfaces para Python, Darktable, ComfyUI y GPU; no contiene services/casos de uso de Foundation. |
| `PhotoAIFactory.Domain` | Sí | IDs, `JobSnapshot`, enums y state machine básica. |
| `PhotoAIFactory.Infrastructure` | Sí | `ProcessRunner`, clientes Python/ComfyUI/Darktable, GPU semaphore, file utilities y lector del component lock. |
| `PhotoAIFactory.Contracts` | Sí | DTOs JSON para AI, ComfyPlan y QA. |

Proyectos auxiliares existentes:

- `PhotoAIFactory.PocHost`: CLI diagnóstica Phase 0; no es host productivo.
- `PhotoAIFactory.SelfTests`: executable de 6 assertions; no es un proyecto de
  test estándar.
- `src/python/ai-worker`: FastAPI autenticada con health/capabilities/model status,
  análisis OpenCV, preselección y QA técnicos. Recipe/feedback están bloqueados
  explícitamente, sin resultados semánticos inventados.

## Missing production projects

- `PhotoAIFactory.App` y su composition root WinUI/Generic Host.
- Proyecto de tests C# estándar para Foundation.
- No falta ninguno de los cuatro proyectos de capas no-UI aprobados, pero sus
  implementaciones de Foundation son todavía parciales.

## Existing infrastructure

| Capacidad | Estado encontrado |
|---|---|
| Generic Host | Ausente. No hay `Microsoft.Extensions.Hosting` ni `Host.CreateApplicationBuilder`. |
| DI | Ausente. No hay registrations ni service collection productiva. |
| Configuration | Sólo JSON/lock y reader manual; no hay pipeline `IConfiguration` ni options. |
| Logging | Logs ad hoc en PoCs; no hay `ILogger`/providers productivos. |
| SQLite | Schema lógico en `db/schema_v1.sql`; no hay dependencia o conexión SQLite en Infrastructure productiva. |
| Migrations | Ausentes. No hay tabla de versión, runner, backup pre-migration ni migrations versionadas. |
| Domain entities | Parciales: IDs y `JobSnapshot`; faltan aggregates `Project`, `Photo`, `Asset`, `ConfigVersion`, checkpoint/output e invariantes. |
| State machine | Existe `JobStateMachine` con transiciones y terminal states; no modela todavía requisitos de artifacts/checkpoints para completar. |
| ProjectService | Ausente. |
| ConfigService | Ausente. |
| ConfigVersion | Existe sólo en documentación/schema; no hay tipo o servicio C#. |
| Repositories | Ausentes en producción. |
| Process execution | `ProcessRunner` usa `UseShellExecute=false` y `ArgumentList`; timeout/cancel mata sólo el árbol que inició. |
| Python client | Existe cliente HTTP autenticado; falta supervisor/lifecycle productivo. |
| ComfyUI client | Existe adapter REST/WebSocket con history autoritativo; falta supervisor, materialización segura y policy de instancia. |
| Darktable adapter | Existe invocación segura básica; faltan capability/version policy y validación completa del output. |
| GPU coordinator | Existe semaphore simple; faltan timeout, preflight NVML, owner-process reclaim y observabilidad probados en GPU-01. |

## Existing tests

- C# self-tests: 6/6 PASS; state machine, contrato AI, serialización de lease GPU y
  SHA-256. Son un executable propio, no xUnit/NUnit/MSTest.
- Python: 4/4 PASS con el runtime aislado 3.12.12; auth, health, analyze y bloqueo
  de recipe. Se observó 1 `StarletteDeprecationWarning` por TestClient/httpx.
- `tests/README.md` existe, pero no contiene una suite raíz adicional.
- Los PoCs aportan evidencia de integración, pero no deben contarse como tests
  unitarios productivos ni agregarse automáticamente al build normal.

## Phase 0 PoC promotion map

Las decisiones promueven patrones probados, no copias textuales de harnesses.

| PoC | Component/pattern | Decision | Target production layer | Notes |
|---|---|---|---|---|
| DT-01 | Argumentos separados, ejecución headless, version/config isolation | ADAPT | Infrastructure / Darktable Control Bridge | Integrar con `ProcessRunner`, component lock, capability whitelist y output validation. |
| DT-01 | XMP real generado por Darktable y subset de exposición/color probado | REFERENCE_ONLY | Infrastructure / recipe compiler tests | No copiar XMP como contrato general ni inferir módulos no probados. |
| DT-01 | Runners PowerShell e `inspect_media.py` | DO_NOT_PROMOTE | Gate tooling only | Hardcodes, paths de evidencia y lógica de informe. |
| IPC-01 | `WorkerSupervisor` lifecycle/readiness/token/crash/restart | ADAPT | Infrastructure / Python process supervisor | Separar lifecycle, port allocation, health y policy de retry; usar DI/options/logging. |
| IPC-01 | `PythonAiClient` y contratos correlacionados | PROMOTE | Application abstractions + Infrastructure | Ya existen en `src`; endurecer errores y timeouts sin duplicarlos. |
| IPC-01 | `/ipc/echo`, `/ipc/delay`, `/ipc/shutdown` | DO_NOT_PROMOTE | Gate instrumentation only | Endpoints sintéticos exclusivos del Gate. |
| CUI-01 | `ComfyUiClient` prompt/ws/history/queue/interrupt | PROMOTE | Infrastructure | Ya existe en `src`; preservar history como confirmación autoritativa. |
| CUI-01 | `ComfySupervisor` loopback/headless/readiness/owned PID | ADAPT | Infrastructure / ComfyUI supervisor | Unificar con process supervision y configuración; evitar segundo supervisor duplicado. |
| CUI-01 | `PafCui01Delay` y delay workflow | DO_NOT_PROMOTE | Gate instrumentation only | Custom node sintético; no agregar a ComfyUI productivo. |
| GPU-01 | Lease con timeout, cancel, preflight y reclaim por exit | ADAPT | Infrastructure / GPU Resource Coordinator | El coordinator productivo actual es más simple; portar comportamiento detrás de interfaces, no la clase interna. |
| GPU-01 | `NvmlMonitor` y snapshots | ADAPT | Infrastructure / hardware telemetry | Abstraer NVML, errores y selección de device para tests. |
| GPU-01 | JSON-line GPU worker, CUDA probe y custom node | DO_NOT_PROMOTE | Gate instrumentation only | Helpers de presión/memoria, hardcodes y comandos sintéticos. |
| ING-01 | Watcher + reconciliation + single-reader queue/coalescing | ADAPT | Infrastructure / ingestion | Preservar reconciliación como autoridad durable y paths seguros; agregar interfaces/options. |
| ING-01 | Stable copy, hash, `.partial` y `COPY_TO_PROJECT` | ADAPT | Infrastructure / project storage | Alinear con repositories y transacciones de producción. |
| ING-01 | `RawVariantDetector` | REFERENCE_ONLY | Infrastructure / camera capability policy | Umbral TIFF específico Sony; reemplazar por capability table validada. |
| ING-01 | `Ing01Store` y schema embebido | DO_NOT_PROMOTE | Gate database only | Difiere del schema lógico y mezcla repositorio, locking y migración ad hoc. |
| REC-01 | Semántica checkpoint, revalidación, attempts y publish idempotente | ADAPT | Domain + Application + Infrastructure | Convertir reglas en entidades/services/repositories con migrations de producción. |
| REC-01 | C# single writer, WAL/FULL/FK y writer ownership | PROMOTE | Infrastructure / persistence policy | Promover la política probada; no copiar DB/harness. |
| REC-01 | Controller crash matrix y barriers | REFERENCE_ONLY | Integration/failure-injection tests | Mantener como especificación ejecutable de recovery. |
| REC-01 | `RecoveryStore`, schema REC, stage-helper y synthetic artifacts | DO_NOT_PROMOTE | Gate instrumentation only | Schema temporal, responsabilidades mezcladas y helpers sintéticos. |
| Todos | Structured JSONL correlation fields | ADAPT | Infrastructure / logging | Implementar con `ILogger` scopes/event IDs y sinks estructurados; no copiar seis loggers. |

## Technical debt before Phase 1

- El nombre `PhotoAIFactory.Phase0.sln` y `PocHost` reflejan el bootstrap, no la
  solución/host final.
- `README.md` todavía afirma que el código productivo no se inició, aunque ya
  existen componentes Phase 0 bajo `src`; no se modificó porque este closeout sólo
  autorizó Current Status e inventarios.
- No existe gestión central de versiones NuGet.
- `TreatWarningsAsErrors=false` en producción.
- `db/schema_v1.sql` no tiene migration metadata/runner. Su tabla `checkpoints` no
  impone `UNIQUE(job_id, stage)` ni modela invalidación/historial comprobados por
  REC-01; debe revisarse antes de materializar la primera DB productiva.
- No hay single-writer repository boundary productiva ni transaction orchestration.
- Domain no expresa todavía `OUTPUT_PUBLISHED → COMPLETED`, config immutability,
  attempts/checkpoint artifacts o no-overwrite.
- Los errores HTTP/proceso no están unificados bajo el contrato estructurado.
- `ProcessRunner` es seguro en argumentos, pero carece de logging, owner metadata,
  clasificación de timeout/cancel y una policy explícita compartida.
- Los supervisors de IPC/CUI/GPU duplican port allocation, readiness, process pump,
  stop/crash y ownership.
- Los paquetes Python de `pyproject.toml` usan rangos, mientras el runtime real se
  fija por `requirements-ai-worker.lock.txt`; falta una política única de build.
- El auditor CVE Python (`pip-audit`) no está instalado en el runtime aislado. No se
  añadió durante este inventario.

## Risks

- Copiar schemas de ING-01 o REC-01 crearía un tercer modelo de DB incompatible con
  `db/schema_v1.sql`.
- Copiar supervisors produciría lifecycle y cleanup divergentes entre Python y
  ComfyUI.
- Copiar hardcodes enlazaría producción al usuario, GPU, modelo y carpetas de test
  de este host.
- Promover custom nodes/stage helpers introduciría dependencias sintéticas en
  runtime productivo.
- Promover `RawVariantDetector` como política universal clasificaría cámaras por un
  umbral Sony probado sólo en el Gate.
- Tratar los runners DT-01 como recipe compiler ampliaría falsamente el subset XMP
  probado y violaría la limitación Neural Restore.
- Agregar Generic Host, SQLite, repositories, UI y todos los services en un único
  cambio haría imposible aislar regresiones de Foundation.

## Recommended first implementation slice

**Slice recomendado: Project + ConfigVersion durable, sin UI.**

Objetivo verificable:

1. Modelar `Project` y `ConfigVersion` inmutables en Domain.
2. Definir puertos de repositorio/caso de uso en Application.
3. Añadir una migration `001` versionada y un runner SQLite local que aplique
   `foreign_keys=ON`, `journal_mode=WAL` y `synchronous=FULL`.
4. Implementar repositories sólo para crear/abrir Project y anexar/leer
   ConfigVersions; una versión usada nunca se actualiza.
5. Añadir tests C# de migration idempotente, persistencia/reopen, hash/version
   unique, FK, rollback y single-writer.

El slice termina cuando esos tests pasan sobre una DB temporal local y el build
normal sigue limpio. No incluye WinUI, watcher, pipeline, supervisors, GPU,
Darktable, ComfyUI ni modelos. Generic Host/DI/logging debe ser el siguiente slice,
una vez exista esta frontera de persistencia verificable.

## Files that should be created

Propuesta para el primer slice, sujeta a autorización explícita:

- `src/csharp/PhotoAIFactory.Domain/Projects/Project.cs`
- `src/csharp/PhotoAIFactory.Domain/Projects/ConfigVersion.cs`
- `src/csharp/PhotoAIFactory.Application/Projects/IProjectRepository.cs`
- `src/csharp/PhotoAIFactory.Application/Projects/IConfigVersionRepository.cs`
- `src/csharp/PhotoAIFactory.Application/Projects/ProjectService.cs`
- `src/csharp/PhotoAIFactory.Infrastructure/Persistence/SqliteProjectDatabase.cs`
- `src/csharp/PhotoAIFactory.Infrastructure/Persistence/MigrationRunner.cs`
- `src/csharp/PhotoAIFactory.Infrastructure/Persistence/Migrations/001_Initial.sql`
- `src/csharp/PhotoAIFactory.Infrastructure/Persistence/Repositories/ProjectRepository.cs`
- `src/csharp/PhotoAIFactory.Infrastructure/Persistence/Repositories/ConfigVersionRepository.cs`
- `src/csharp/Directory.Packages.props` para fijar centralmente las dependencias
  que Phase 1 realmente incorpore.
- `tests/csharp/PhotoAIFactory.Foundation.Tests/PhotoAIFactory.Foundation.Tests.csproj`
- tests del slice bajo el proyecto anterior.

En un slice posterior de Foundation, no en el primero recomendado:

- `src/csharp/PhotoAIFactory.App/PhotoAIFactory.App.csproj`
- composition root de `PhotoAIFactory.App` con Generic Host, DI, configuration y
  logging; la UI WinUI se agregará sólo cuando sea autorizada.

## Files that should be modified

Sólo cuando se autorice la implementación:

- solución C# para agregar el proyecto de tests;
- `PhotoAIFactory.Infrastructure.csproj` para SQLite y recursos de migrations;
- `db/schema_v1.sql` únicamente después de conciliarlo con migrations y las
  invariantes probadas por REC-01;
- documentación de persistence si la implementación descubre una decisión no
  cubierta. PRD/SRS/ADRs no deben tocarse salvo conflicto real.

## Files that must NOT be copied from PoCs

- Cualquier `Program.cs` de Gate o runner `run-*.ps1`.
- `ipc_worker_entrypoint.py` y rutas `/ipc/*`.
- `PafCui01Delay`, `PafGpu01CudaProbe` y sus workflows.
- `gpu_worker.py` y sus comandos sintéticos.
- `Ing01Store.cs` y su SQL embebido.
- `RecoveryStore.cs`, `RecoveryController.cs`, `stage-helper` y artifacts
  sintéticos de REC-01.
- Los seis loggers ad hoc de Gates.
- Paths absolutos, IDs, timeouts, nombres de fixtures y rutas de evidencia.
- XMP de test o cualquier estructura interna no documentada para Neural Restore.

## Build/test baseline

Ejecutado el 2026-08-18 sin modificar código productivo:

- .NET SDK: `10.0.400`.
- `tools/dev/phase0-smoke.ps1`: PASS.
- C# Debug solution build: PASS, 0 errores, 0 warnings.
- C# self-tests: 6/6 PASS.
- Python aislado: `C:\Users\Pc\AppData\Local\PhotoAIFactory\runtimes\ai-worker\Scripts\python.exe`, Python 3.12.12.
- Python tests: 4/4 PASS; 1 warning de deprecación Starlette TestClient/httpx.
- `components.lock.local.json`: JSON válido.
- Auditoría NuGet `--vulnerable --include-transitive`: 11/11 proyectos sin
  paquetes vulnerables reportados; no se actualizaron paquetes.
- `uv pip check`: 104 paquetes compatibles.
- Auditoría CVE Python: no ejecutada porque `pip-audit` no está instalado y el
  runtime no incluye `pip`; no se añadió tooling durante el inventario.

## Phase 1 readiness conclusion

**READY para implementar un primer slice pequeño de Phase 1 Foundation, previa
autorización explícita.**

Phase 0 queda `CLOSED / GO`. La estructura existente permite reutilizar contratos,
state machine y adapters base, pero no debe declararse Foundation completa: faltan
host/DI/config/logging, persistencia/migrations/repositories, ProjectService,
ConfigVersion productivo y tests estándar. Este documento no implementó ninguna
de esas piezas.
