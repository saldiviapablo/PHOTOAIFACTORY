# PHOTO AI FACTORY — PHASE 1 SLICE 3 REPORT

**Fecha:** 2026-08-18

## Result

PASS

## Initial project state

`STOPPED`, revisión 0. Project, ConfigVersion #1 y auditoría inicial se insertan
atómicamente. DBs con sólo migration 001 reciben backfill determinístico a
`STOPPED` sin inferir Jobs históricos.

## Lifecycle states

`RUNNING`, `PAUSE_REQUESTED`, `PAUSED`, `STOP_REQUESTED`, `STOPPED`,
`BLOCKED_STORAGE` y `COMPONENT_UNHEALTHY`. No se añadieron estados. Los dos
estados de salud quedan reconocidos sin recovery transitions inventadas.

## Transitions

- `STOPPED -> RUNNING`
- `RUNNING -> PAUSE_REQUESTED -> PAUSED`
- `PAUSED -> RUNNING`
- `RUNNING|PAUSE_REQUESTED|PAUSED -> STOP_REQUESTED -> STOPPED`

Las demás transiciones se rechazan explícitamente. Repeticiones coherentes
devuelven `AlreadyInDesiredState`; se distinguen invalid transition, optimistic
conflict, operation conflict y not found.

## Pause behavior

Con Job activo, `RequestPause` persiste inmediatamente `PAUSE_REQUESTED` y no
cancela el trabajo. `NotifySafeCompletion` completa a `PAUSED`. Sin Job activo se
registran las dos transiciones observables en la misma operación de aplicación y
el Project no queda varado. La dependencia productiva temporal
`NoActiveProjectWorkStatus` es explícita y está marcada para reemplazo.

## Stop behavior

Con Job activo queda `STOP_REQUESTED` hasta la señal safe-completion. Sin trabajo
activo completa `STOP_REQUESTED -> STOPPED`; lo mismo aplica desde `PAUSED`.
Reconciliation, queue y cancelación de Jobs no se implementaron, por lo que
FR-STOP continúa foundation/partial.

## Dispatch guard

`ProjectDispatchGuard.CanDispatchNextJob` devuelve true únicamente para
`RUNNING`; todos los demás estados devuelven false. QueueDispatcher sigue
pendiente y deberá consumir esta regla.

## ConfigService

Es el único caso de uso productivo para modificar configuración existente. Lee
estado durable, exige `PAUSED`, vuelve a verificar el estado dentro de la
transacción, canonicaliza, calcula SHA-256 e inserta una ConfigVersion append-only.
El método legado sin guard fue retirado de `ProjectService`; el puerto `AppendAsync`
queda sólo como primitive de persistencia de bajo nivel y no se registra como
servicio de modificación.

## PAUSED guard

Functional. `RUNNING`, `PAUSE_REQUESTED`, `STOP_REQUESTED`, `STOPPED`,
`BLOCKED_STORAGE` y `COMPONENT_UNHEALTHY` rechazan cambios sin crear filas. El
cambio válido de input/output en `PAUSED` crea versión 2 y conserva el Project en
`PAUSED`.

## ConfigVersion idempotency

La igualdad usa hash del JSON canónico, no el número de versión. Configuración
semánticamente idéntica devuelve `Unchanged` y reutiliza la versión actual. Un
operation ID repetido con el mismo contenido se reproduce; con contenido distinto
es conflicto. Las versiones anteriores permanecen byte-for-byte intactas.

## Concurrency

El single-writer por ruta serializa writes. Lifecycle usa estado +
`state_revision` esperados en el `UPDATE`; una revisión stale no se reintenta a
ciegas. ConfigService exige `expected_config_version_id`; dos cambios simultáneos
sobre la misma base producen una creación y un conflicto, sin lost update.

## State audit

`project_state_transitions` registra ID, Project, from/to, razón, UTC, revisión y
operation ID. Triggers rechazan UPDATE/DELETE. El estado y la auditoría comparten
transacción; se probaron por separado fallas inyectadas en UPDATE de estado e
INSERT de auditoría, ambas sin cambios parciales.

## Migration 002

`002_project_lifecycle` agrega columnas explícitas de estado/revisión/timestamp,
tabla de auditoría, constraints y triggers append-only. Conserva checksum, backup
pre-migration, transacción, rollback, idempotencia y drift detection del runner.
Se validaron DB nueva, DB schema 001, ejecución repetida, drift e SQL inválido.

## TimeProvider

`ProjectLifecycleService` y `ConfigService` reciben `System.TimeProvider` por DI;
producción usa `TimeProvider.System`. Tests usan un provider controlable sin nueva
dependencia. No se dispersó `UtcNow` en los servicios de lifecycle.

## Persistence/reopen

Reopen recupera estado, revisión y auditoría. Un Project reabierto en
`PAUSE_REQUESTED` permanece así hasta una señal safe-completion; no se promueve a
`PAUSED` por el mero restart.

## Requirements

| Requisito | Estado de Slice 3 |
|---|---|
| FR-PAU-001 | Functional |
| FR-PAU-002 | Partial/foundation hasta JobOrchestrator real |
| FR-PAU-003 | Functional en la frontera lifecycle |
| FR-PAU-004 | Foundation rule; QueueDispatcher runtime pendiente |
| FR-PAU-005 | Functional mediante ConfigService |
| FR-FS-007 | Functional |
| FR-CFG-001 / FR-CFG-002 | Functional |
| FR-CFG-003 / 004 / 005 | Pending |
| FR-STOP-001 / FR-STOP-002 | Partial/foundation |
| FR-PRJ-002 | Partial/foundation reforzado con state/revision/audit durable |
| FR-PRJ-003 | Partial; Jobs/queue siguen pendientes |

## Tests

112/112 PASS: 48 tests obligatorios Slice 3, 6 invariantes adicionales y 58 tests
acumulados Slice 1/2.

## Regression

C# self-tests 6/6 PASS; Python aislado 4/4 PASS; Phase 0 light smoke PASS. No se
iniciaron Python, ComfyUI, Darktable ni GPU por resolución DI o lifecycle.

## SQLite

`integrity_check=ok`, FK/WAL/FULL conservados, writer único y transacciones sin
estado/auditoría parcial. Migration desde 001 genera backup probado.

## Build

.NET SDK 10.0.400. Release PASS, 0 errores, 0 warnings.
Se agregó la referencia directa `Microsoft.Extensions.Logging.Abstractions`
10.0.10 a Application para los eventos estructurados; no se actualizó ninguna
dependencia existente.

## Warnings

0 warnings C#. Python conserva 1 `StarletteDeprecationWarning` conocida de
TestClient/httpx.

## Vulnerable packages

NuGet no reportó paquetes vulnerables directos o transitivos. No se instaló
`pip-audit` ni se actualizó ninguna dependencia.

## Bugs found

- El entry point legado de ProjectService permitía anexar configuración sin el
  guard PAUSED ni expected ConfigVersion.
- El test histórico de migration failure reutilizaba el número 2, que ahora
  pertenece a migration 002.
- El primer build local se lanzó con un nombre `.slnx` inexistente; el entry point
  real continúa siendo `src/csharp/PhotoAIFactory.Phase0.sln`.

## Bugs fixed

- ConfigService quedó como único caso de uso productivo de modificación y se
  retiró el bypass legado; los tests de persistence usan el primitive de storage.
- El failure test calcula el próximo número libre de migration.
- El gate final usa la solución real sin renombrarla fuera de alcance.

## Files created

- `src/csharp/PhotoAIFactory.Domain/Projects/ProjectStateMachine.cs`
- `src/csharp/PhotoAIFactory.Application/Projects/ProjectLifecycleService.cs`
- `src/csharp/PhotoAIFactory.Application/Projects/ConfigService.cs`
- `src/csharp/PhotoAIFactory.Infrastructure/Persistence/Migrations/002_project_lifecycle.sql`
- `src/csharp/PhotoAIFactory.Infrastructure/Hosting/NoActiveProjectWorkStatus.cs`
- `tests/csharp/PhotoAIFactory.Foundation.Tests/ProjectLifecycleAndConfigServiceTests.cs`
- `docs/adr/ADR-016-project-lifecycle-persistence-initial-stopped-state.md`
- `docs/phase1/PHASE1_SLICE3_PROJECT_LIFECYCLE_REPORT.md`

## Files modified

- `src/csharp/PhotoAIFactory.Domain/Projects/Project.cs`
- `src/csharp/PhotoAIFactory.Application/PhotoAIFactory.Application.csproj`
- `src/csharp/PhotoAIFactory.Application/Projects/ProjectPersistence.cs`
- `src/csharp/PhotoAIFactory.Infrastructure/Persistence/MigrationRunner.cs`
- `src/csharp/PhotoAIFactory.Infrastructure/Persistence/Repositories/SqliteProjectStore.cs`
- `src/csharp/PhotoAIFactory.Infrastructure/Hosting/PhotoAIFactoryHostingExtensions.cs`
- `tests/csharp/PhotoAIFactory.Foundation.Tests/ProjectConfigPersistenceTests.cs`
- `tests/csharp/PhotoAIFactory.Foundation.Tests/HostCompositionTests.cs`
- `PROJECT_STATUS.md`
- `docs/phase1/PHASE1_FOUNDATION_INVENTORY.md`

## ADR

ADR-016 — Project Lifecycle Persistence and Initial STOPPED State. Status:
Accepted for this implementation. ADR-001 a ADR-015 no se modificaron.

## Known debt

- Sustituir `NoActiveProjectWorkStatus` cuando exista JobOrchestrator/Queue real.
- Implementar QueueDispatcher consumidor del dispatch guard, Jobs, reconciliation
  y políticas completas de `BLOCKED_STORAGE`/`COMPONENT_UNHEALTHY`.
- FR-CFG-003/004/005 y FR-STOP-002 requieren Jobs y reprocessing.
- No existe todavía WinUI/App ni lifecycle de motores externos.

## Confirm

- PRD untouched
- SRS untouched
- Architecture untouched
- previous ADRs untouched
- PoCs untouched
- Phase 0 evidence untouched
- no UI
- no watcher/queue/jobs
- no external engines started
- no dependency upgrades
- no commit/push

## Final

READY FOR SLICE 3 REVIEW
