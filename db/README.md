# Base de datos

`schema_v1.sql` es el modelo lógico inicial para el live Project DB.

Reglas:

- solo C# escribe;
- DB live en `%LOCALAPPDATA%`;
- migraciones siempre versionadas;
- backup antes de migrar;
- no editar manualmente una DB de producción;
- los JSON inmutables del proyecto siguen siendo la auditoría portable.
