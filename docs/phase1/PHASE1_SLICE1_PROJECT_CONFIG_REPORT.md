# Phase 1 Slice 1 — Project + ConfigVersion Report

**Fecha:** 2026-08-18  
**Resultado:** PASS  
**Alcance:** Project + ConfigVersion durable, migration SQLite mínima,
repositories y tests; no completa Phase 1.

## Requirements coverage classification

| Requisito | Clasificación | Cobertura de este slice |
|---|---|---|
| Persistencia Project/ConfigVersion | **FULL / FUNCTIONAL** | Creación atómica, reopen, append, hash, migrations, backup y single writer. |
| FR-CFG-001 / FR-CFG-002 | **FULL / FUNCTIONAL** | ConfigVersion inmutable y append-only; toda configuración nueva crea una fila/version nueva. |
| FR-PRJ-001 | **PARTIAL / FOUNDATION** | Creación durable con snapshot completo; UI y política de creación física de carpetas quedan pendientes. |
| FR-PRJ-002 | **PARTIAL / FOUNDATION** | Reapertura de Project + ConfigVersions; queue, Jobs, historial operativo, estadísticas y componentes quedan pendientes. |
| FR-PRJ-003 | **PARTIAL / FOUNDATION** | Dispose/reopen conserva Project/configuración; cierre seguro de Jobs requiere lifecycle futuro. |
| FR-FS-001 / FR-FS-005 / FR-FS-006 | **FOUNDATION** | Rutas persistidas/normalizadas y prevención de reingesta implementada. UI/watcher quedan fuera. |
| FR-FS-007 | **PENDING** | Crear versiones nuevas está disponible, pero el guard `PAUSED` depende del lifecycle productivo. |
| FR-CFG-003 / FR-CFG-004 / FR-CFG-005 | **PENDING** | Requieren referencias de Job, opciones A/B/C, reprocessing y protección de Jobs terminados. |
| FR-REP-001 / FR-REP-002 | **PARTIAL / FOUNDATION** | Payload/hash/version durable; modelos, recetas, XMP, workflows, seeds y engines requieren Jobs. |
| NFR-REL-004 | **FOUNDATION** | Retry por `operation_key` es idempotente y conflictos fallan sin mutar datos. |
| ARC-001 / ARC-002 | **FULL para el slice** | C# persiste/valida y serializa writers incluso entre instancias internas. |
| ADR-003 / ADR-008 / ADR-009 / ADR-014 | **FULL para el slice** | Single writer, DB local, backup pre-migration, idempotencia y versiones fijadas. |

## Architecture

- Domain no referencia SQLite y reutiliza `ProjectId`, `RevealMode`,
  `SemanticMode` y `ComfyUiMode` existentes.
- Application define `IProjectRepository`, `IConfigVersionRepository` y
  `ProjectService`.
- Infrastructure implementa DB, runner de migrations, coordinator y store con
  una conexión privada por operación, `Pooling=false`, timeout de 5 segundos y
  sin `Cache=Shared`.
- DB viva: `%LOCALAPPDATA%\PhotoAIFactory\projects\<project_id>\project.db`.
  Los tests usan roots temporales aislados.

## Schema and migration

`001_initial_project_config` crea:

- `projects(project_id PK, name, creation_operation_key UNIQUE,
  created_at_utc, updated_at_utc)`;
- `project_config_versions(config_version_id PK, project_id FK,
  version_number > 0, schema_version > 0, config_json no vacío,
  config_sha256 requerido, operation_key, created_at_utc,
  UNIQUE(project_id, version_number), UNIQUE(project_id, operation_key))`;
- triggers que rechazan cualquier UPDATE o DELETE de ConfigVersion.

`schema_migrations` registra `version`, `name`, `migration_sha256` y
`applied_at_utc`. El runner aplica sólo pendientes en transacción, registra cada
migration después de ejecutar su SQL, es idempotente y rechaza drift de hash o
nombre. Una DB existente con migration pendiente recibe antes un backup online
mediante `SqliteConnection.BackupDatabase`; una DB nueva no genera copia vacía.
El archivo SQL 001 verificado tiene SHA-256
`45dcd7acb594af2c795bbbe256bdb38c1f6e33166cd7c393d104171e35d1da89`.

## ConfigVersion and canonical hash

El documento V1 representa rutas, include-subfolders, reveal mode,
preselection enabled/profile, semantic mode, ComfyUI mode/tareas autorizadas,
preset profiles, export format/quality y association window. `schema_version=1`
es independiente del `version_number` durable.

`ProjectConfigCanonicalizer` escribe propiedades en orden explícito, normaliza
sets/tokens, genera UTF-8 compacto y calcula SHA-256 lowercase. IDs, timestamps y
número de versión quedan fuera del hash. La lectura recalcula el hash del payload
exacto y rechaza mismatch con `ConfigIntegrityException`; nunca corrige datos.

## Single writer and transactions

- Crear Project + ConfigVersion #1 ocurre en una transacción; el test inyecta una
  colisión de PK después del INSERT del Project y comprueba rollback completo.
- Append obtiene el próximo número y hace INSERT/commit bajo el coordinator.
- Stress: 24 solicitudes concurrentes desde dos servicios/stores independientes,
  writer máximo observado `1`, overlap `0`, sin versiones perdidas.

## Tests and injected failures

- Foundation MSTest: **27/27 PASS** (25 obligatorios + corrupción de hash + retry/conflicto).
- C# self-tests anteriores: **6/6 PASS**.
- Python aislado 3.12.12: **4/4 PASS**, con 1 warning preexistente de
  `StarletteDeprecationWarning`.
- Fallos inyectados: colisión transaccional, FK inválida, migration SQL inválida,
  migration checksum modificado, UPDATE/DELETE append-only, config hash corrupto,
  ruta insegura y contención concurrente.
- SQLite verificado: `integrity_check=ok`, `journal_mode=wal`,
  `synchronous=2 (FULL)`, `foreign_keys=1`.

## Build and dependency quality

- SDK: .NET `10.0.400`.
- Solution Release: PASS, 0 errores, 0 warnings.
- NuGet vulnerable check: 7/7 proyectos sin paquetes vulnerables reportados,
  incluyendo transitivos.
- No se actualizaron dependencias. Se reutilizó `Microsoft.Data.Sqlite` 10.0.11
  y `SQLitePCLRaw.bundle_e_sqlite3` 2.1.13 ya fijados en los PoCs; MSTest se agregó
  como primer framework estándar, sin coexistir con otro framework de tests.
- `pip-audit` continúa ausente y no se instaló, según el alcance.

## Bugs found and fixed

- .NET 10 expone `InvalidDataException` como tipo sellado: las excepciones de
  integridad se derivaron de `IOException`.
- `BeginTransactionAsync` retorna `DbTransaction`: se hizo el cast explícito a
  `SqliteTransaction` requerido por commands tipados.
- El fixture del backup necesitaba crear el parent de la DB legacy y desactivar
  pooling en conexiones raw para liberar archivos temporales en Windows.
- La telemetría del single-writer se movió al estado compartido por ruta para que
  también mida múltiples instancias internas, no sólo llamadas al mismo store.

## Files

Creación productiva:

- `src/csharp/PhotoAIFactory.Domain/Projects/Project.cs`
- `src/csharp/PhotoAIFactory.Domain/Projects/ProjectConfigV1.cs`
- `src/csharp/PhotoAIFactory.Domain/Projects/ConfigVersion.cs`
- `src/csharp/PhotoAIFactory.Application/Projects/ProjectPersistence.cs`
- `src/csharp/PhotoAIFactory.Infrastructure/Persistence/SqliteProjectDatabase.cs`
- `src/csharp/PhotoAIFactory.Infrastructure/Persistence/SqliteWriteCoordinator.cs`
- `src/csharp/PhotoAIFactory.Infrastructure/Persistence/MigrationRunner.cs`
- `src/csharp/PhotoAIFactory.Infrastructure/Persistence/Migrations/001_initial_project_config.sql`
- `src/csharp/PhotoAIFactory.Infrastructure/Persistence/Repositories/SqliteProjectStore.cs`

Tests y documentación creados:

- `tests/csharp/PhotoAIFactory.Foundation.Tests/PhotoAIFactory.Foundation.Tests.csproj`
- `tests/csharp/PhotoAIFactory.Foundation.Tests/ProjectConfigPersistenceTests.cs`
- `docs/adr/ADR-015-project-persistence-immutable-config-hashing.md`
- `docs/phase1/PHASE1_SLICE1_PROJECT_CONFIG_REPORT.md`

Modificados:

- `src/csharp/PhotoAIFactory.Infrastructure/PhotoAIFactory.Infrastructure.csproj`
- `src/csharp/PhotoAIFactory.Phase0.sln`
- `docs/phase1/PHASE1_FOUNDATION_INVENTORY.md`

## Known debt and next recommendation

- FR-PRJ-001 aún necesita la política productiva para comprobar/crear carpetas;
  no se tocaron fotografías ni se creó watcher.
- FR-FS-007 necesita el guard `PAUSED` cuando se implemente lifecycle.
- FR-CFG-003/004/005 y el resto de FR-REP requieren Jobs, fuera de este slice.
- Generic Host, DI global, logging, UI, ingestion, queue, checkpoints, adapters y
  backup scheduler continúan pendientes.
- `db/schema_v1.sql` permanece como diseño lógico previo; no se copió ni se usó
  como mecanismo de migration.
- Próxima recomendación: revisar este slice. No iniciar el siguiente hasta recibir
  autorización explícita.

## Scope confirmation

- PRD y SRS intactos.
- ADR-001 a ADR-014 intactos; sólo se agregó ADR-015.
- PoCs y evidencia Phase 0 intactos.
- Sin UI, App, Generic Host, watcher, Jobs, Python/Darktable/ComfyUI/GPU changes.
- Sin dependency upgrades, commit ni push.
