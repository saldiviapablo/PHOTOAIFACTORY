# Recovery Runbook v1.0

## La app se cerró durante un Job

1. Abrir Project DB local.
2. Marcar ejecución activa anterior `INTERRUPTED`.
3. Leer último checkpoint.
4. Validar artifacts de checkpoint.
5. Reanudar desde la etapa siguiente o repetir etapa incompleta.

## Python caído

- circuit breaker;
- no iniciar nuevas etapas Python;
- restart limitado;
- health;
- conservar Job;
- continuar desde checkpoint.

## ComfyUI caído

- conservar TIFF input validado;
- restart limitado;
- recuperar estado/history si existe;
- si no, repetir ComfyUI desde su input;
- no repetir Darktable innecesariamente.

## Darktable falla

- capturar exit/stderr;
- clasificar permanente/transitorio;
- no tratar RAW corrupto como retryable;
- si el problema es sistémico, `COMPONENT_UNHEALTHY`.

## Disco lleno

- `BLOCKED_STORAGE`;
- no iniciar más Jobs;
- limpiar solo temporales seguros;
- reanudar cuando haya espacio.

## OOM

- liberar modelos inactivos;
- transferir/recuperar GPU lease;
- fallback de tile/batch autorizado;
- máximo 1 memory-recovery retry;
- nunca cambiar modelo silenciosamente.

## DB dañada

- detener procesamiento;
- copiar archivo dañado para diagnóstico;
- restaurar snapshot;
- reconciliar manifests/histories;
- nunca “recrear y continuar” silenciosamente.

## Regla

Ante duda, preservar originales, historial y cola antes que intentar recuperar
agresivamente.
