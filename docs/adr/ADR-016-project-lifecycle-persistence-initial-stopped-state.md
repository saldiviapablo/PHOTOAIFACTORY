# Project Lifecycle Persistence and Initial STOPPED State

**Status:** Accepted for this implementation

## Contexto

Los documentos aprobados enumeran los estados de Project, pero no fijan de forma
explícita el estado inicial de un Project nuevo. Phase 1 Slice 3 necesita además
persistir la intención de pausa/stop antes de que existan QueueDispatcher y
JobOrchestrator productivos, y debe impedir cambios de configuración mientras el
Project pueda estar procesando trabajo.

## Decisión

- Un Project nuevo comienza en `STOPPED`. Crear y configurar no inicia
  procesamiento implícitamente; un `Start` explícito será la única entrada a
  `RUNNING`.
- Los únicos estados reconocidos son `RUNNING`, `PAUSE_REQUESTED`, `PAUSED`,
  `STOP_REQUESTED`, `STOPPED`, `BLOCKED_STORAGE` y `COMPONENT_UNHEALTHY`.
- Las transiciones habilitadas en V1 son:
  `STOPPED -> RUNNING`, `RUNNING -> PAUSE_REQUESTED`,
  `PAUSE_REQUESTED -> PAUSED`, `PAUSED -> RUNNING`,
  `RUNNING|PAUSE_REQUESTED|PAUSED -> STOP_REQUESTED` y
  `STOP_REQUESTED -> STOPPED`.
- `BLOCKED_STORAGE` y `COMPONENT_UNHEALTHY` se reconocen sin inventar todavía
  políticas de entrada, salida o recovery.
- El estado actual, su revisión monotónica y el timestamp UTC de cambio se
  persisten en `projects`. Toda transición usa compare-and-swap sobre estado y
  revisión esperados.
- Cada transición inserta una fila append-only en
  `project_state_transitions`. El cambio de estado y su auditoría ocurren dentro
  de la misma transacción SQLite; si uno falla, ambos hacen rollback.
- La creación de Project, ConfigVersion #1 y auditoría inicial `STOPPED` es una
  sola transacción. Migration 002 convierte determinísticamente DBs anteriores a
  `STOPPED`, revisión 0, sin reinterpretar Jobs inexistentes.
- C# conserva autoridad exclusiva sobre reglas y transiciones. SQL aplica
  integridad y atomicidad, pero no decide transiciones de negocio.
- En V1, `ConfigService` puede crear una ConfigVersion para un Project existente
  únicamente cuando el estado durable es `PAUSED`. Configuración semánticamente
  idéntica devuelve `UNCHANGED`; un expected ConfigVersion obsoleto produce
  conflicto explícito.
- `CanDispatchNextJob` devuelve true sólo para `RUNNING`. El futuro
  QueueDispatcher deberá consumir esta regla antes de tomar un Job.
- Hasta integrar Jobs, producción registra una fuente explícita
  `NoActiveProjectWorkStatus`; debe reemplazarse por el adapter real del
  JobOrchestrator/Queue cuando ese subsistema exista.

## Motivo

`STOPPED` evita side effects al crear o abrir un Project y permite configurar el
sistema antes de autorizar procesamiento. Persistir las intenciones intermedias
evita perder una solicitud de pausa/stop durante restart. Revisión optimista,
single-writer y auditoría atómica hacen visibles los conflictos sin retries de
negocio silenciosos.

## Consecuencias

- `Start`/`Resume` sólo cambia estado durable en este slice; no inicia watcher,
  queue ni motores.
- Con trabajo activo, pause/stop queda solicitado hasta recibir una notificación
  explícita de finalización segura. Sin trabajo activo, ambas transiciones
  conceptuales se registran inmediatamente y son auditables.
- Ningún Job futuro podrá despacharse en estados de request, pause, stop o salud
  degradada.
- La creación inicial continúa siendo la excepción natural al guard `PAUSED`.
- FR-STOP y la coordinación real de trabajo siguen parciales hasta existir Jobs,
  QueueDispatcher, reconciliation y JobOrchestrator.

## Alternativas descartadas

- **Estado inicial RUNNING:** activaría side effects futuros por el mero hecho de
  crear un Project.
- **Estado nuevo CREATED/IDLE/READY:** no pertenece al conjunto aprobado.
- **Estado sólo en memoria:** pierde intención de pause/stop en un restart.
- **Config editable en STOPPED:** contradice la política V1 que exige `PAUSED`
  para cambios de carpetas/configuración de un Project existente.
- **Retry ciego tras conflicto:** puede aplicar una intención sobre un estado
  distinto al observado por el caller.
- **Tabla Job sintética:** inventaría un subsistema productivo fuera de alcance.
