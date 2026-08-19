# Version locking de componentes/modelos

**Status:** Accepted

## Decisión
Cada build/instalación compatible usa un manifest lock con versiones, fuentes,
hashes y licencias.

## Motivo
Reproducibilidad y rollback.

## Consecuencia
Nunca usar `latest` implícito para un Job histórico.
