# Checkpoints e idempotencia por etapa

**Status:** Accepted

## Decisión
Persistir un checkpoint validado antes de avanzar; outputs identificados por
`job_id/attempt_id/stage_id`.

## Motivo
Recuperación precisa y retries sin efectos duplicados.
