# Live DB local + backups del proyecto

**Status:** Accepted

## Decisión
`project.db` activo reside en `%LOCALAPPDATA%`. El output conserva manifests y
snapshots/backups.

## Motivo
No confiar una SQLite activa a una carpeta remota/removible seleccionada por el usuario.

## Consecuencia
El proyecto sigue siendo portable mediante metadata y backups.
