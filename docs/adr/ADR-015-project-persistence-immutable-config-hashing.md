# Project Persistence and Immutable ConfigVersion Hashing

**Status:** Accepted

## Contexto

Phase 1 necesita crear y reabrir un proyecto sin perder su configuración, conservar
la configuración histórica que usarán Jobs futuros y aplicar cambios de schema sin
reparaciones implícitas. La solución ya fija C# como fuente de verdad, un único
escritor SQLite y una DB activa local por proyecto.

## Decisión

- Usar `Microsoft.Data.Sqlite` directo, sin EF Core ni otro ORM.
- Mantener la DB activa en
  `%LOCALAPPDATA%\PhotoAIFactory\projects\<project_id>\project.db`.
- Aplicar migrations SQL explícitas, ordenadas y transaccionales. Cada migration
  aplicada registra versión, nombre, SHA-256 y fecha UTC en `schema_migrations`.
- Rechazar de forma explícita una migration aplicada cuyo nombre o checksum ya no
  coincida con el catálogo ejecutable; no reparar ni reescribir metadata.
- Crear un backup online con `SqliteConnection.BackupDatabase` antes de una
  migration pendiente sobre una DB existente con contenido. No crear un backup
  inútil antes de `001` para una DB nueva o vacía.
- Persistir cada `ConfigVersion` como snapshot completo append-only. Una nueva
  configuración inserta una fila nueva; triggers SQLite rechazan `UPDATE` y
  `DELETE` de sus filas.
- Serializar toda escritura C# mediante una frontera por ruta de DB, compartida
  entre instancias internas. Cada operación abre su propia conexión con timeout
  finito y no comparte conexiones, comandos ni readers entre threads.
- Configurar/verificar `foreign_keys=ON`, `journal_mode=WAL`,
  `synchronous=FULL`, cache privada y pooling desactivado.
- Canonicalizar el documento V1 con `System.Text.Json`, orden explícito de
  propiedades y normalización de colecciones con semántica de conjunto. Calcular
  SHA-256 sobre los bytes UTF-8 canónicos y guardar hexadecimal lowercase.
- Excluir del hash el ID de ConfigVersion, el número de versión, la fecha de
  creación y cualquier formato incidental.
- Usar una `operation_key` única por proyecto como frontera mínima de
  idempotencia: un retry idéntico devuelve la versión ya creada; reutilizar la
  clave con contenido diferente falla explícitamente.

## Motivo

Este diseño conserva reproducibilidad, permite validar corrupción al reabrir,
evita que un retry duplique versiones y hace auditables tanto el schema como sus
cambios. SQLite directo mantiene pequeño el primer slice y evita introducir un
modelo paralelo al SQL aprobado.

## Consecuencias

- Los repositorios son responsables de transacciones y de reconstruir entidades
  sólo después de validar el hash.
- Crear `Project` y `ConfigVersion #1` es una única transacción.
- Cambiar configuración nunca modifica snapshots anteriores.
- WAL puede crear archivos auxiliares locales; la DB activa no se ubica en una
  carpeta remota elegida por el usuario.
- El backup implementado aquí protege migrations, pero no sustituye el scheduler
  periódico de backups futuro.
- Este ADR no define Jobs, queue, checkpoints, watcher, UI ni su lifecycle.

## Alternativas descartadas

- **EF Core / ORM:** agrega otra abstracción y migrations implícitas innecesarias
  para este schema mínimo.
- **Una sola fila mutable de configuración:** rompe historial, trazabilidad y
  reproducibilidad.
- **Hash de JSON serializado por reflection sin contrato canónico:** depende de
  detalles incidentales y no garantiza igualdad semántica.
- **Confiar sólo en locking de SQLite:** no evita writers internos accidentales ni
  expresa la política single-writer de C#.
- **`Cache=Shared` con WAL:** se evita para no introducir una combinación de
  caching/concurrencia innecesaria.
- **Recrear o reparar schema automáticamente:** ocultaría drift y corrupción.
