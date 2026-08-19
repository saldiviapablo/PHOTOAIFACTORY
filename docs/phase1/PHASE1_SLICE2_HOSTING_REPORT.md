# Phase 1 Slice 2 — Hosting Report

**Fecha:** 2026-08-18  
**Resultado:** PASS  
**Alcance:** Generic Host foundation, DI, runtime configuration/paths, logging
JSONL y composición de persistencia; no completa Phase 1.

## Host and startup

`PhotoAIFactoryHost.CreateBuilder` usa directamente el modelo aprobado
`Host.CreateApplicationBuilder` y aplica la composición reusable
`AddPhotoAIFactoryFoundation(IHostApplicationBuilder)`. No se creó un ejecutable
ni `PhotoAIFactory.App`.

La secuencia validada es:

1. Generic Host construye configuración;
2. Options se enlazan y validan con `ValidateOnStart`;
3. el container se construye con `ValidateOnBuild` y `ValidateScopes`;
4. `RuntimeInitializationHostedService` prepara los directorios autorizados;
5. activa el provider JSONL;
6. el Host queda ready.

Options inválidas, directorio no creable, destino de logger inválido o servicio
DI faltante impiden startup con excepción explícita.

## Runtime configuration and paths

`PhotoAIFactoryRuntimeOptions` contiene sólo `RootPath` técnico opcional y el
nombre leaf `.jsonl`. Por defecto, `WindowsAppPaths` deriva la raíz desde
`Environment.SpecialFolder.LocalApplicationData`; tests inyectan roots temporales.

Rutas disponibles: `projects`, `work`, `logs`, `models` y `components`. El cálculo
no depende del current working directory y soporta espacios, Unicode y trailing
separators. Crear directorios es una responsabilidad separada, idempotente, que no
borra contenido, no toca fotografías y no hace fallback silencioso.

Runtime Options nunca se enlazan con `ProjectConfigV1`; input/output, reveal,
semantic, ComfyUI y el hash histórico siguen exclusivamente en ConfigVersion.

## DI and service lifetimes

| Servicio | Lifetime | Motivo |
|---|---|---|
| `IRuntimeSession` / `RuntimeSession` | Singleton | Un session ID estable por Host. |
| `IAppPaths` / `WindowsAppPaths` | Singleton | Rutas inmutables durante la ejecución. |
| `IRuntimeDirectoryInitializer` | Singleton | Stateless e idempotente. |
| `JsonLinesLoggerProvider` | Singleton | Ownership exclusivo de un archivo físico. |
| `IProjectStoreFactory` | Singleton | Factory thread-safe; stores/conexiones se crean por proyecto/operación. |
| `ProjectService` | Transient | Caso de uso liviano; selecciona store por ProjectId. |
| `ProcessRunner`, `ComponentLockReader` | Singleton | Stateless y side-effect free al construir/resolver. |
| `IGpuResourceCoordinator` | Singleton | Arbitraje compartido; resolverlo no reserva GPU. |

No se registraron `IPythonAiClient`, `IComfyUiClient` ni `IDarktableCli`: faltan
runtime endpoints/tokens/paths autorizados y resolver DI no debe iniciar motores.

## Structured JSONL logging

Destino: `<runtime-root>\logs\photo-ai-factory.jsonl`.

Cada línea se genera con `Utf8JsonWriter` e incluye `timestamp_utc`, `level`,
`category`, `event_id` y `message`. Siempre incluye `session_id`; scopes agregan
solamente IDs realmente presentes entre `project_id`, `photo_id`, `job_id`,
`attempt_id`, `stage`, `component` y `request_id`. No se inventan correlations.

Exceptions incluyen `type`, `message` y `stack_trace` cuando existe. El provider
usa un único `StreamWriter` UTF-8 sin BOM bajo lock, `AutoFlush`, dispose
idempotente y un registry por ruta que rechaza un segundo writer físico.

No usa Channel, queue ni thread/worker de background; por ello no existe cola sin
límite, drop policy ni test de saturación pendiente. Concurrencia de 600 mensajes
produjo 600 líneas JSON válidas y event IDs únicos.

## Shutdown

El hosted service productivo registra stopping y fuerza flush. Un hosted service
exclusivo de test recibió cancellation, completó su Task y no dejó background
workers. El provider no posee threads ni procesos y su dispose doble es seguro.

## Persistence integration

`IProjectStoreFactory` calcula
`<projects>\<project_id>\project.db` y crea stores SQLite sólo cuando un proyecto
se abre/crea. El Host no mantiene conexiones ni migra todas las DBs al iniciar.
Dos stores para la misma ruta comparten el mismo estado de
`SqliteWriteCoordinator`; los 27 tests de Slice 1 permanecen PASS.

## Architecture and traceability

El test arquitectónico inspecciona referencias compiladas y confirma que Domain
no depende de Infrastructure, Hosting ni Logging. Application sólo contiene
puertos runtime/persistence y no conoce el Host.

Clasificación corregida de Slice 1:

- **FULL / FUNCTIONAL:** persistencia Project/ConfigVersion, FR-CFG-001/002.
- **PARTIAL / FOUNDATION:** FR-PRJ-003, FR-REP-001/002.
- **PENDING:** FR-FS-007, FR-CFG-003/004/005.

## Tests and failure injection

- C# estándar: **58/58 PASS** — 31 tests Slice 2 + 27 tests Slice 1.
- C# self-tests: **6/6 PASS**.
- Python aislado 3.12.12: **4/4 PASS**, con la única
  `StarletteDeprecationWarning` preexistente.
- Phase 0 smoke liviano: PASS.
- Fallos inyectados: Options inválidas, servicio DI faltante, directorio técnico
  bloqueado, destino logger inválido, ownership duplicado, logging concurrente,
  excepción estructurada y shutdown con mensajes/cancellation.

## Build and dependencies

- SDK .NET: `10.0.400`.
- Release build: PASS, 0 errores, 0 warnings.
- NuGet vulnerable audit: 7/7 proyectos sin paquetes vulnerables reportados,
  incluyendo transitivos.
- Se agregó `Microsoft.Extensions.Hosting` 10.0.10 ya disponible localmente; no
  se actualizó ninguna dependencia existente ni se agregó un logging package.
- `pip-audit` sigue ausente y no se instaló.

## Bugs found and fixed

- Un primary constructor capturaba dos veces `SqliteProjectDatabase` y producía
  CS9124; se convirtió a constructor explícito sin cambiar comportamiento.
- `ClearProviders` se ejecutaba después de registrar el provider JSONL y lo
  retiraba de `LoggerFactory`; la prueba real con `ILogger<T>` lo detectó y se
  corrigió el orden.
- El reader de tests necesitaba `FileShare.ReadWrite` para inspeccionar JSONL
  mientras el writer del Host seguía abierto.
- Se desambiguó una sobrecarga de `JsonDocument.Parse` en el test reader y se
  endureció la validación filesystem de `ProjectId`.

## Files

Created:

- `src/csharp/PhotoAIFactory.Application/Runtime/RuntimeServices.cs`
- `src/csharp/PhotoAIFactory.Infrastructure/Hosting/PhotoAIFactoryRuntimeOptions.cs`
- `src/csharp/PhotoAIFactory.Infrastructure/Hosting/WindowsAppPaths.cs`
- `src/csharp/PhotoAIFactory.Infrastructure/Hosting/PhotoAIFactoryHostingExtensions.cs`
- `src/csharp/PhotoAIFactory.Infrastructure/Logging/JsonLinesLoggerProvider.cs`
- `src/csharp/PhotoAIFactory.Infrastructure/Persistence/Repositories/SqliteProjectStoreFactory.cs`
- `tests/csharp/PhotoAIFactory.Foundation.Tests/HostCompositionTests.cs`
- `docs/phase1/PHASE1_SLICE2_HOSTING_REPORT.md`

Modified:

- `src/csharp/PhotoAIFactory.Application/Projects/ProjectPersistence.cs`
- `src/csharp/PhotoAIFactory.Infrastructure/PhotoAIFactory.Infrastructure.csproj`
- `src/csharp/PhotoAIFactory.Infrastructure/Persistence/SqliteWriteCoordinator.cs`
- `src/csharp/PhotoAIFactory.Infrastructure/Persistence/Repositories/SqliteProjectStore.cs`
- `docs/phase1/PHASE1_SLICE1_PROJECT_CONFIG_REPORT.md`
- `docs/phase1/PHASE1_FOUNDATION_INVENTORY.md`
- `PROJECT_STATUS.md`

## Known debt

- No existe todavía el futuro composition root `PhotoAIFactory.App` ni WinUI.
- No hay project lifecycle `RUNNING/PAUSED`, watcher, ingestion, queue, Jobs o
  checkpoint manager productivo.
- Python/ComfyUI/Darktable/GPU lifecycle y su runtime configuration siguen fuera.
- No hay rolling, compresión o retención de logs; tampoco backup scheduler.
- Los adapters existentes todavía no usan `ILogger<T>` internamente; no se
  reescribieron en este slice para preservar el alcance Phase 0.

## Scope confirmation

- PRD, SRS y Architecture intactos.
- ADR-001 a ADR-015 intactos; no se creó ADR nuevo.
- PoCs y evidencia Phase 0 intactos.
- Sin UI/App, external engine auto-start, dependency upgrades, commit ni push.
