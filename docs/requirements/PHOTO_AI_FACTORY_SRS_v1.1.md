# PHOTO AI FACTORY — SRS v1.1

**Documento:** Software Requirements Specification  
**Versión:** 1.1  
**Estado:** APROBADO  
**Documento padre:** PHOTO AI FACTORY — PRD v1.1  
**Plataforma:** Windows 11 / Windows 64-bit  
**Aplicación principal:** C# + .NET + WinUI 3  
**Backend de IA:** Python  
**Revelado RAW:** Darktable CLI  
**Motor de workflows IA:** ComfyUI Server/API  
**Persistencia:** SQLite + JSON + XMP  
**Fecha:** Agosto de 2026  
**Cambio principal desde v1.0:** incorporación de almacenamiento administrado de originales (`COPY_TO_PROJECT`).

---

## 1. Propósito

Este documento define el comportamiento funcional de PHOTO AI FACTORY.

Describe:

- entradas;
- estados;
- reglas;
- validaciones;
- procesamiento;
- errores;
- recuperación;
- resultados;
- interacción del usuario;
- condiciones de aceptación.

No define todavía:

- contrato técnico C# ↔ Python;
- esquema SQL definitivo;
- protocolo IPC;
- clases internas;
- estructura final del código;
- wireframes finales;
- decisiones de despliegue detalladas.

---

## 2. Convenciones y entidades

### 2.1 Project

Unidad de trabajo creada por el usuario.

### 2.2 Photo

Fotografía lógica.

Puede estar compuesta por:

- RAW;
- RAW + JPEG;
- JPEG.

### 2.3 Asset

Archivo físico perteneciente a una Photo.

### 2.4 Job

Una ejecución concreta de procesamiento.

### 2.5 ConfigVersion

Snapshot inmutable de configuración.

### 2.6 ProcessingPass

Una pasada de revelado/procesamiento.

---

## 3. Estados principales

### 3.1 Estados principales del Job

```text
RECEIVED
↓
ANALYZING
↓
├── REVIEW_PRE
├── REJECTED_PRE
└── QUEUED
      ↓
   PROCESSING
      ↓
      QA
      ↓
├── COMPLETED
├── REVIEW_FINAL
├── REJECTED_FINAL
└── ERROR
```

### 3.2 Estados auxiliares del Job

```text
WAITING_FOR_FILE
CANCEL_REQUESTED
CANCELLED
RETRYING
INTERRUPTED
```

### 3.3 Estados auxiliares del proyecto

```text
RUNNING
PAUSE_REQUESTED
PAUSED
STOP_REQUESTED
STOPPED
BLOCKED_STORAGE
COMPONENT_UNHEALTHY
```

---

# 4. Gestión de proyectos

## FR-PRJ-001 — Crear proyecto

El sistema deberá permitir crear un proyecto nuevo.

Campos mínimos:

- nombre;
- carpeta de entrada;
- carpeta de salida;
- modo de revelado;
- configuración de preselección;
- modo semántico;
- modo ComfyUI;
- tareas ComfyUI autorizadas;
- calidad/formato de exportación.

### Validaciones

- el nombre no podrá estar vacío;
- la carpeta de entrada deberá existir o poder crearse según política;
- la carpeta de salida deberá existir o poder crearse;
- la carpeta de salida no podrá provocar reingesta accidental.

---

## FR-PRJ-002 — Abrir proyecto

Al abrir un proyecto se deberán recuperar:

- configuración;
- cola;
- Jobs;
- historial;
- estado de procesamiento;
- carpetas;
- estadísticas;
- componentes requeridos.

---

## FR-PRJ-003 — Cerrar proyecto

Cerrar un proyecto no deberá perder:

- cola;
- historial;
- configuración;
- Jobs pendientes.

Si existe un Job en ejecución, deberá finalizar de forma segura antes del cierre operativo.

---

# 5. Carpetas

## FR-FS-001 — Carpeta de entrada

El usuario seleccionará una carpeta vigilada desde la interfaz.

La ruta será persistida dentro del proyecto.

---

## FR-FS-002 — Vigilancia

Mientras el proyecto esté `RUNNING`, el sistema deberá vigilar la carpeta de entrada y detectar archivos nuevos compatibles.

---

## FR-FS-003 — Subcarpetas

El usuario podrá seleccionar:

```text
Incluir subcarpetas: Sí / No
```

---

## FR-FS-004 — Archivo parcialmente copiado

El sistema no deberá ingerir un archivo hasta determinar que terminó de escribirse/copiarse.

Mientras tanto:

```text
WAITING_FOR_FILE
```

---

## FR-FS-005 — Carpeta de salida

El usuario seleccionará una carpeta raíz de exportación.

---

## FR-FS-006 — Prevención de reingesta

Por defecto, el sistema deberá impedir una configuración donde la carpeta de salida quede dentro de una ruta vigilada de entrada de forma que una exportación pueda volver a ingresar.

Ejemplo inválido:

```text
INPUT  = D:\Fotos\
OUTPUT = D:\Fotos\Salida\
```

si se vigilan subcarpetas.

---

## FR-FS-007 — Cambio de carpetas

Las carpetas de entrada/salida solo podrán cambiarse cuando el proyecto esté `PAUSED`.

El cambio creará una nueva `ConfigVersion`.

---

## FR-FS-008 — Cambio de carpeta con asociaciones pendientes

Si existen archivos esperando pareja RAW/JPEG al cambiar la carpeta de entrada:

- se mostrarán al usuario;
- se resolverán antes de activar completamente la nueva carpeta, o;
- el usuario podrá finalizar explícitamente esas asociaciones como RAW-only/JPEG-only.

Los archivos de la carpeta anterior nunca se emparejarán con los de la carpeta nueva.

---

## FR-FS-009 — Reconciliación al reiniciar

Al iniciar o reanudar un proyecto, el sistema deberá realizar un escaneo de reconciliación de la carpeta de entrada para detectar archivos que puedan haberse perdido por eventos del watcher.

---

# 6. Detección de archivos

## FR-ING-001 — Formatos V1

Formatos:

```text
.ARW
.JPG
.JPEG
```

La comparación de extensión será case-insensitive.

---

## FR-ING-002 — RAW + JPEG

Cuando RAW y JPEG correspondan a la misma toma, deberán agruparse en una única `Photo`.

Ejemplo:

```text
DSC01234.ARW
DSC01234.JPG
```

Resultado:

```text
Photo #1234
├── RAW
└── JPEG_CAMERA
```

---

## FR-ING-003 — Asociación por identidad

La asociación RAW/JPEG utilizará, en este orden conceptual:

1. nombre/base compatible;
2. origen;
3. metadatos compatibles;
4. ventana temporal.

---

## FR-ING-004 — Ventana de asociación

Valor V1 recomendado:

```text
30 segundos
```

La ventana será configurable.

Durante esa ventana:

- el JPEG podrá comenzar análisis visual;
- la Photo no deberá iniciar procesamiento definitivo hasta que llegue el RAW o expire el tiempo.

---

## FR-ING-005 — RAW tardío

Si el RAW llega después del JPEG:

- si el Job todavía no comenzó, se fusionará y el RAW será master;
- si el Job ya comenzó, el RAW se adjuntará a la `Photo` y quedará disponible para reprocesamiento posterior;
- no se modificará un Job activo en vuelo.

---

## FR-ING-006 — RAW solo

Si vence la ventana sin JPEG:

- la Photo será RAW-only;
- se generará preview cuando sea necesaria.

---

## FR-ING-007 — JPEG solo

Si no existe RAW:

- el JPEG será el master disponible;
- nunca será modificado;
- todo reprocesamiento partirá de él.

---

## FR-ING-008 — Duplicados exactos

El sistema diferenciará:

- duplicado exacto de archivo;
- fotografía visualmente similar.

Para duplicado exacto podrá usar:

- ruta;
- nombre;
- tamaño;
- SHA-256 cuando corresponda.

Si el contenido es exactamente el mismo, no se creará una `Photo` adicional.

---

## FR-ING-009 — Similitud visual

Fotografías visualmente similares o pertenecientes a una ráfaga no serán consideradas duplicados de archivo.

Se tratarán mediante preselección/embeddings.

---

# 7. Originales

## FR-ORG-001

PHOTO AI FACTORY nunca deberá modificar directamente un original.

---

## FR-ORG-002

Un original nunca se eliminará automáticamente por:

- rechazo;
- error;
- QA fallido;
- cancelación;
- reprocesamiento.

---


## FR-ORG-003 — Copia administrada del original

Después de validar que el archivo de entrada terminó de copiarse/escribirse, PHOTO AI FACTORY deberá crear una copia administrada del original dentro del almacenamiento permanente del proyecto.

Por defecto:

```text
COPY_TO_PROJECT
```

---

## FR-ORG-004 — Originales que deben archivarse

Se archivará:

- RAW original, cuando exista;
- JPEG de cámara, cuando forme parte de RAW + JPEG;
- JPEG original, cuando la entrada sea JPEG-only.

---

## FR-ORG-005 — Validación de copia

Una copia administrada no se considerará válida hasta comprobar como mínimo:

- existencia;
- lectura correcta;
- tamaño esperado;
- hash/checksum cuando corresponda.

Si la validación falla, el sistema deberá conservar el archivo fuente intacto y bloquear la continuación que dependa de haber protegido el original.

---

## FR-ORG-006 — Fuente de reprocesamiento

Una vez validada la copia administrada, los reprocesamientos deberán poder partir de ella aunque la carpeta de entrada original ya no exista.

---

## FR-ORG-007 — Ubicación administrada

La ubicación normal se derivará automáticamente del proyecto y no requerirá que el usuario seleccione una tercera carpeta en el flujo estándar.

Ubicación conceptual:

```text
<OUTPUT>\
└── .photo-ai-factory\
    └── originals\
        ├── RAW\
        └── JPEG_CAMERA\
```

La interfaz deberá ofrecer una acción para abrir esta ubicación.

---

## FR-ORG-008 — Sin modo reference-only por defecto

V1 no utilizará `REFERENCE_ONLY` como política predeterminada.

Una modalidad de solo referencia podrá evaluarse en versiones futuras.

---

# 8. Análisis inicial

## FR-ANA-001

Toda `Photo` deberá pasar por análisis antes de ingresar en cola.

Estado:

```text
ANALYZING
```

---

## FR-ANA-002 — RAW + JPEG

Si existe JPEG de cámara:

- se utilizará preferentemente para análisis visual rápido;
- el RAW quedará reservado para revelado/análisis técnico RAW.

---

## FR-ANA-003 — RAW-only

Si existe solo RAW:

- se generará preview;
- la preview podrá ser temporal.

---

## FR-ANA-004 — JPEG-only

Si existe solo JPEG:

- se analizará directamente;
- reducciones de resolución podrán hacerse en memoria.

---

# 9. Preselección

## FR-PRE-001

Cada criterio podrá activarse/desactivarse individualmente.

---

## FR-PRE-002 — Criterios mínimos V1

- foco;
- blur;
- movimiento;
- rostro;
- ojos;
- exposición;
- clipping;
- duplicados;
- similitud;
- sujeto principal.

---

## FR-PRE-003 — Resultado

```text
APPROVED
REVIEW_PRE
REJECTED_PRE
```

---

## FR-PRE-004 — Regla de duda

Ante baja confianza o ambigüedad en un criterio crítico, se deberá favorecer:

```text
REVIEW_PRE
```

antes que rechazo automático.

---

## FR-PRE-005 — Rechazo

`REJECTED_PRE` significa:

> no continuar automáticamente con la edición.

No significa borrar.

---

## FR-PRE-006 — Rescate manual

Desde `REJECTED_PRE`, el usuario podrá:

- `APROBAR Y PROCESAR`;
- `REANALIZAR`;
- `MANTENER DESCARTADA`.

Aprobar manualmente:

- no borra la decisión original de IA;
- registra `manual_override = approved`;
- crea un nuevo Job de procesamiento.

---

# 10. Análisis semántico

## FR-SEM-001

El proyecto ofrecerá:

```text
OFF
STANDARD
FULL
```

---

## FR-SEM-002 — OFF

No ejecutar modelos semánticos de alto costo salvo necesidad explícita.

---

## FR-SEM-003 — STANDARD

Producir análisis estructurado ligero.

---

## FR-SEM-004 — FULL

Producir análisis semántico completo y JSON descriptivo.

---

## FR-SEM-005

Los VLM no podrán ser la única fuente para decidir:

- foco;
- ojos;
- clipping;
- corrupción;
- fallos técnicos.

---

# 11. Cola

## FR-QUE-001

Las `Photo` aprobadas ingresarán como Jobs en cola.

---

## FR-QUE-002

V1 utilizará FIFO.

---

## FR-QUE-003

Solo un Job podrá estar en `PROCESSING`.

---

## FR-QUE-004

La cola deberá sobrevivir al cierre de la aplicación.

---

## FR-QUE-005

Una Photo pendiente no ejecutará etapas de edición hasta llegar al frente de cola.

---

## FR-QUE-006 — Procesar siguiente

V1 permitirá una única prioridad manual sencilla:

```text
PROCESAR SIGUIENTE
```

La Photo seleccionada pasa a ser la próxima.

Después, la cola continúa en FIFO normal.

---

## FR-QUE-007 — Sin reordenamiento libre en V1

V1 no requiere drag-and-drop arbitrario ni múltiples niveles de prioridad.

---

# 12. Pausa y parada

## FR-PAU-001 — Pausar

Al presionar Pausar:

```text
RUNNING
↓
PAUSE_REQUESTED
```

---

## FR-PAU-002

El Job activo deberá terminar de forma segura.

---

## FR-PAU-003

Después:

```text
PAUSE_REQUESTED
↓
PAUSED
```

---

## FR-PAU-004

No comenzará otro Job mientras el proyecto esté `PAUSED`.

---

## FR-PAU-005

Durante `PAUSED` se podrá modificar configuración.

---

## FR-STOP-001 — Detener proyecto

`DETENER PROYECTO` será distinto de Pausar.

Al detener:

- se dejará de vigilar la carpeta;
- el Job activo terminará de forma segura;
- la cola se persistirá;
- el proyecto pasará a `STOPPED`.

---

## FR-STOP-002 — Reanudar proyecto detenido

Al volver a iniciar:

- se recuperará cola;
- se recuperará configuración;
- se realizará reconciliación de carpeta;
- se detectarán archivos llegados durante `STOPPED`.

---

# 13. Cancelación de Jobs

## FR-CAN-001 — Cancelar Job pendiente

Si está en `QUEUED`:

```text
QUEUED
↓
CANCELLED
```

de forma inmediata.

---

## FR-CAN-002 — Cancelar Job activo

Si está en `PROCESSING`:

```text
PROCESSING
↓
CANCEL_REQUESTED
```

La cancelación será cooperativa y segura.

---

## FR-CAN-003 — Punto seguro

Si la etapa actual puede interrumpirse sin corrupción, se solicitará la interrupción.

Si no puede interrumpirse de forma segura:

- se terminará la etapa actual;
- no se iniciará la siguiente.

---

## FR-CAN-004

Cancelar nunca borrará el original.

---

## FR-CAN-005

Un Job cancelado quedará registrado como `CANCELLED`.

---

## FR-CAN-006 — Cancelación masiva

Los Jobs pendientes podrán cancelarse individualmente o en lote.

La cancelación no eliminará su historial.

---

# 14. Versionado de configuración

## FR-CFG-001

Una `ConfigVersion` utilizada por un Job será inmutable.

---

## FR-CFG-002

Modificar configuración relevante creará nueva `ConfigVersion`.

---

## FR-CFG-003

Cada Job almacenará:

```text
preselection_config_id
processing_config_id
```

---

## FR-CFG-004

Al aplicar nueva configuración, el usuario elegirá:

```text
A — Solo nuevas fotografías
B — Nuevas + pendientes
C — Nuevas + pendientes + reprocesar terminadas
```

---

## FR-CFG-005

La opción C creará nuevos Jobs.

Nunca reescribirá Jobs finalizados.

---

# 15. PRE_AI

## FR-MOD-PRE-001

PRE_AI analizará el original y/o representaciones antes del revelado.

---

## FR-MOD-PRE-002

La salida será una receta estructurada.

---

## FR-MOD-PRE-003

Darktable aplicará la receta al original.

---

## FR-MOD-PRE-004

Receta y resultado quedarán registrados.

---

# 16. DT_AUTO

## FR-MOD-DTA-001

DT_AUTO enviará el original a Darktable.

---

## FR-MOD-DTA-002

Darktable podrá aplicar:

- automatismos;
- presets;
- correcciones determinísticas;
- funciones IA internas autorizadas.

---

## FR-MOD-DTA-003

El historial exacto deberá guardarse.

---

# 17. FEEDBACK

## FR-MOD-FBK-001 — Pass 1

El original entrará a Darktable para producir una propuesta inicial.

---

## FR-MOD-FBK-002

Pass 1 podrá aplicar:

- pipeline RAW normal;
- corrección de lente;
- perfil/color;
- exposición automática;
- preset base;
- automatismos;
- IA interna permitida.

---

## FR-MOD-FBK-003 — Salidas Pass 1

```text
TIFF RGB 16-bit temporal
XMP Pass 1
parámetros Pass 1
```

---

## FR-MOD-FBK-004 — Preview VLM

A partir del TIFF se generará una preview sRGB en memoria.

No será obligatorio escribir JPEG de inspección en disco.

---

## FR-MOD-FBK-005 — Inspección externa

La IA inspectora podrá usar:

- TIFF;
- preview;
- EXIF;
- análisis técnico previo;
- contexto semántico;
- XMP;
- parámetros Pass 1.

---

## FR-MOD-FBK-006 — Receta final

La inspección generará una receta correctiva estructurada.

---

## FR-MOD-FBK-007 — Decisiones IA Darktable

La receta podrá decidir individualmente:

```text
raw_denoise = on/off
rgb_denoise = on/off
upscale = on/off
```

y otras funciones compatibles.

---

## FR-MOD-FBK-008 — Raw Denoise

Si se autoriza:

```text
RAW original
↓
Raw Denoise
↓
DNG temporal
↓
Pass 2
```

---

## FR-MOD-FBK-009 — Pass 2

Siempre partirá de:

- RAW original; o
- DNG temporal derivado del RAW original.

Nunca partirá del TIFF/JPEG de Pass 1.

---

## FR-MOD-FBK-010

Pass 2 aplicará:

- pipeline técnico requerido;
- receta final;
- IA interna autorizada.

---

# 18. ComfyUI

## FR-CUI-001

Modos:

```text
OFF
ON
AUTO
```

---

## FR-CUI-002 — OFF

Ningún Job ingresará a ComfyUI.

---

## FR-CUI-003 — ON

Todos los Jobs aprobados ejecutarán tareas autorizadas.

---

## FR-CUI-004 — AUTO

El sistema decidirá qué tareas autorizadas ejecutar.

---

## FR-CUI-005

AUTO nunca podrá ejecutar una tarea deshabilitada.

---

## FR-CUI-006

Cada decisión deberá registrarse.

Ejemplo:

```text
face_retouch = skipped
reason = no_relevant_face_detected
```

---

# 19. Preflight de componentes

## FR-PRECHECK-001

Antes de iniciar procesamiento de un proyecto se ejecutará un preflight.

Se comprobarán:

- espacio suficiente para archivar originales;
- permisos sobre el almacenamiento administrado;
- modelos requeridos;
- versiones;
- checksums;
- workflows;
- Darktable;
- ComfyUI;
- backend Python;
- almacenamiento;
- configuración.

---

## FR-PRECHECK-002 — Modelo faltante

Si falta un modelo obligatorio:

- el procesamiento se bloquea;
- se informa claramente;
- se ofrece reparar/instalar;
- nunca se sustituye silenciosamente por otro modelo.

---

## FR-PRECHECK-003 — Modelo opcional

Si un modelo es opcional:

- se marcará `UNAVAILABLE`;
- las funciones dependientes no se ejecutarán;
- se informará al usuario si afectan el proyecto.

---

# 20. Temporales

## FR-TMP-001

Cada Job tendrá área temporal independiente.

---

## FR-TMP-002

TIFF/DNG solo se crearán cuando sean necesarios.

---

## FR-TMP-003

Un temporal no se eliminará hasta que:

- la siguiente etapa termine;
- su resultado se valide;
- el historial se persista.

---

## FR-TMP-004

Tras éxito:

```text
cleanup job workspace
```

---

## FR-TMP-005

Ante error, temporales podrán conservarse temporalmente para diagnóstico/reintento.

---

# 21. Exportación

## FR-EXP-001

V1 exportará JPEG como formato final principal.

---

## FR-EXP-002

El usuario podrá configurar:

- calidad;
- dimensiones;
- perfil de color;
- convención de nombre.

---

## FR-EXP-003 — Naming por defecto

Por defecto se conservará el nombre de cámara:

```text
DSC04582.jpg
```

---

## FR-EXP-004 — Reprocesamiento

Un reprocesamiento generará por defecto:

```text
DSC04582_v02.jpg
DSC04582_v03.jpg
```

---

## FR-EXP-005 — Colisión de nombres

El sistema nunca sobrescribirá silenciosamente.

Si el archivo ya existe:

- si pertenece al mismo Job y coincide con la salida esperada, la operación podrá tratarse como idempotente;
- si es distinto, se generará nombre versionado/único.

---

## FR-EXP-006 — Resultado final

Resultado aprobado:

```text
FINAL
```

Resultado para revisión:

```text
REVISAR
```

según política del proyecto.

---

# 22. QA final

## FR-QA-001

Toda fotografía procesada deberá pasar por QA.

---

## FR-QA-002

QA evaluará:

- foco/nitidez;
- exposición;
- clipping;
- rostros;
- ojos;
- artefactos;
- color anómalo;
- cambios extraños;
- calidad general.

---

## FR-QA-003 — Resultados internos

QA podrá producir:

```text
QA_PASS
QA_REVIEW
QA_REPROCESS
QA_TECH_RETRY
QA_FATAL
```

---

## FR-QA-004 — QA_PASS

```text
QA_PASS
↓
COMPLETED
```

---

## FR-QA-005 — QA_REVIEW

Si el resultado es técnicamente válido pero dudoso:

```text
QA_REVIEW
↓
REVIEW_FINAL
```

No deberá reprocesarse automáticamente por simple ambigüedad estética.

---

## FR-QA-006 — QA_REPROCESS

Si QA detecta un defecto visual y puede generar una corrección concreta:

```text
QA_REPROCESS
↓
crear intento de reparación
↓
volver al ORIGINAL
↓
aplicar receta corregida
↓
QA nuevamente
```

V1 permitirá:

```text
máximo 1 auto-reprocess de calidad
```

Si vuelve a fallar:

```text
REVIEW_FINAL
```

---

## FR-QA-007 — QA_TECH_RETRY

Para fallos técnicos transitorios:

```text
QA_TECH_RETRY
↓
recuperación
↓
retry limitado
```

---

## FR-QA-008 — QA_FATAL

Para fallos permanentes:

```text
QA_FATAL
↓
ERROR
```

No se reintentará automáticamente.

---

# 23. REVIEW_FINAL

## FR-REV-001

`REVIEW_FINAL` nunca deberá bloquear el worker.

La fábrica continuará con el siguiente Job.

---

## FR-REV-002

La vista deberá mostrar:

- resultado;
- original/preview;
- motivo del QA;
- métricas relevantes.

---

## FR-REV-003 — Acciones

El usuario podrá:

- `APROBAR`;
- `REPROCESAR`;
- `RECHAZAR RESULTADO`;
- `DEJAR PENDIENTE`.

---

## FR-REV-004 — Aprobar

Aprobar:

- cambia el Job a `COMPLETED`;
- registra `approval = manual`;
- promueve/mueve el JPEG existente a `FINAL`;
- no vuelve a comprimirlo.

---

## FR-REV-005 — Reprocesar

Reprocesar crea un Job nuevo.

---

## FR-REV-006 — Rechazar resultado

Rechazar resultado:

```text
REJECTED_FINAL
```

Nunca borra el original.

---

# 24. Reprocesamiento

## FR-RPR-001

Todo reprocesamiento crea un Job nuevo.

---

## FR-RPR-002

El Job anterior permanece inmutable.

---

## FR-RPR-003

Se registrará:

```text
parent_job_id
```

---

# 25. Reintentos técnicos

## FR-ERR-001

Los retries solo se utilizarán para fallos transitorios.

---

## FR-ERR-002

Política V1 recomendada:

```text
1 ejecución inicial
+ máximo 2 retries técnicos
```

---

## FR-ERR-003

Espera orientativa:

```text
retry 1 → ~1 s
retry 2 → ~3 s
```

con pequeña variación cuando corresponda.

---

## FR-ERR-004

Errores permanentes no tendrán retry automático.

Ejemplos:

- RAW corrupto;
- modelo obligatorio ausente;
- configuración inválida.

---

## FR-ERR-005

Un Job en error nunca deberá detener los siguientes Jobs.

---

# 26. Circuit breaker / salud de componentes

## FR-CB-001

Si un mismo componente falla repetidamente de forma consecutiva, el sistema deberá considerar que el problema es sistémico y no específico del Job.

---

## FR-CB-002

Estado:

```text
COMPONENT_UNHEALTHY
```

---

## FR-CB-003

Mientras esté `COMPONENT_UNHEALTHY`:

- no se enviarán nuevos trabajos al componente afectado;
- se intentará recuperación controlada;
- los Jobs afectados quedarán pendientes/bloqueados, no convertidos masivamente en `ERROR`.

---

## FR-CB-004

El umbral exacto de aperturas/reintentos del circuit breaker se definirá en arquitectura técnica.

---

# 27. Darktable / ComfyUI no disponibles

## FR-SVC-001

Al iniciar un componente se realizarán intentos controlados.

Recomendación V1:

```text
hasta 3 intentos de arranque
```

---

## FR-SVC-002

Si Darktable o ComfyUI siguen sin responder:

```text
COMPONENT_UNHEALTHY
```

---

## FR-SVC-003

Si ComfyUI está `AUTO` y una Photo no lo necesita, esa Photo podrá continuar.

Si sí lo necesita, deberá esperar.

---

## FR-SVC-004

Nunca se omitirá silenciosamente una tarea requerida.

---

# 28. Recuperación de Jobs interrumpidos

## FR-REC-001

Después de crash/reinicio, la cola deberá recuperarse.

---

## FR-REC-002

Un Job activo al momento del fallo quedará:

```text
INTERRUPTED
```

---

## FR-REC-003 — Checkpoints

Cada etapa tendrá un checkpoint confirmado solo cuando:

- la etapa terminó;
- la salida fue validada;
- el historial fue persistido.

---

## FR-REC-004 — Reanudación

El sistema retomará desde el último checkpoint seguro.

Ejemplos:

- Pass 1 válido + inspección pendiente → reutilizar Pass 1;
- Pass 1 incompleto → repetir desde original;
- TIFF de entrada ComfyUI válido + ComfyUI falló → repetir solo ComfyUI;
- salida final válida + QA pendiente → repetir solo QA.

---

# 29. Almacenamiento insuficiente

## FR-STO-001

Antes de una etapa pesada, el sistema deberá comprobar espacio disponible.

---

## FR-STO-002

Si no hay espacio suficiente:

```text
BLOCKED_STORAGE
```

---

## FR-STO-003

Mientras esté `BLOCKED_STORAGE`:

- no se iniciará otro Job;
- la cola se conservará;
- se informará al usuario;
- se reanudará cuando haya espacio suficiente.

---

## FR-STO-004

Si el disco se llena durante una etapa:

- no se realizarán retries ciegos;
- se preservará el estado seguro posible;
- se podrán eliminar temporales seguros;
- se esperará a recuperación de espacio.

---

# 30. VRAM insuficiente

## FR-VRAM-001

El Model Manager deberá intentar evitar OOM cargando solo modelos necesarios.

---

## FR-VRAM-002

Ante OOM:

1. liberar modelos no usados;
2. liberar caché reutilizable;
3. aplicar fallback permitido de memoria, como menor tile/batch;
4. realizar máximo un retry de recuperación de memoria.

---

## FR-VRAM-003

Si vuelve a fallar:

- no se reducirá calidad arbitrariamente;
- no se cambiará de modelo en secreto;
- el Job pasará a error/revisión de recurso según contexto;
- fallos repetidos podrán activar circuit breaker.

---

# 31. Watchdog

## FR-WDG-001

El sistema vigilará:

- backend Python;
- Darktable;
- ComfyUI.

---

## FR-WDG-002

Detectará:

- crash;
- timeout;
- proceso no responsivo;
- API no disponible;
- proceso zombie.

---

## FR-WDG-003

El reinicio automático solo ocurrirá cuando sea seguro.

---

# 32. Historial

## FR-HIS-001

Todo Job generará historial auditable.

---

## FR-HIS-002

Incluirá:

- entradas;
- configuraciones;
- modelos;
- versiones;
- parámetros;
- decisiones;
- timings;
- errores;
- retries;
- salidas;
- QA;
- overrides manuales.

---

## FR-HIS-003

Los datos históricos de un Job finalizado serán inmutables.

---

# 33. Reproducibilidad

## FR-REP-001

Se almacenará suficiente información para intentar reproducir una ejecución histórica.

---

## FR-REP-002

Como mínimo:

- original;
- config hash;
- modelo;
- versión;
- checksum;
- parámetros;
- receta;
- XMP;
- workflow;
- seed cuando exista;
- versiones de motores.

---

# 34. Interfaz

## FR-UI-001 — Dashboard

Mostrará:

- proyecto;
- estado;
- carpeta entrada;
- carpeta salida;
- recibidas;
- cola;
- procesando;
- terminadas;
- revisar;
- rechazadas;
- errores;
- tiempo promedio.

---

## FR-UI-002 — Job activo

Mostrará:

```text
Archivo
Etapa actual
Progreso
Tiempo transcurrido
Modo
ConfigVersion
```

---

## FR-UI-003 — Revisión

El usuario podrá visualizar:

- `REVIEW_PRE`;
- `REVIEW_FINAL`;
- `ERROR`;
- `REJECTED_PRE`;
- `REJECTED_FINAL`.

---

# 35. Privacidad

## FR-PRV-001

Ninguna fotografía se enviará a Internet por defecto.

---

## FR-PRV-002

Los componentes V1 operarán localmente salvo función futura explícitamente habilitada.

---

# 36. Rendimiento

## NFR-PERF-001

Objetivo típico:

```text
12–25 s / foto
```

---

## NFR-PERF-002

Objetivo general:

```text
≤30 s / foto
```

para la mayoría de casos normales.

No es timeout.

---

## NFR-PERF-003

El sistema priorizará evitar OOM sobre mantener todos los modelos en VRAM.

---

# 37. Confiabilidad

## NFR-REL-001

Un Job defectuoso no bloqueará Jobs posteriores.

---

## NFR-REL-002

La cola persistirá ante cierre inesperado.

---

## NFR-REL-003

Los originales nunca serán modificados.

---

## NFR-REL-004

Las operaciones críticas deberán diseñarse con comportamiento idempotente siempre que sea posible.

---

# 38. Usabilidad

## NFR-UX-001

El usuario no necesitará:

- terminal;
- Darktable UI;
- ComfyUI UI;
- Python CLI;
- editar JSON;
- gestionar procesos manualmente.

---

# 39. Principios funcionales aprobados

PHOTO AI FACTORY deberá respetar siempre:

- nunca modificar originales;
- nunca sobrescribir silenciosamente;
- nunca reintentar indefinidamente;
- nunca cambiar modelos silenciosamente;
- nunca perder la cola;
- nunca permitir que una sola foto bloquee el proyecto;
- nunca mezclar fallo técnico con fallo visual;
- siempre registrar qué ocurrió;
- siempre reprocesar desde el original;
- siempre conservar trazabilidad histórica;
- siempre detenerse en puntos seguros;
- siempre separar revisión humana de fallo técnico.

---

# 40. Decisiones cerradas en SRS v1.0

Quedan aprobadas:

1. QA con `PASS / REVIEW / REPROCESS / TECH_RETRY / FATAL`.
2. Cancelación segura y cooperativa.
3. Pausar y Detener como acciones diferentes.
4. Cambio controlado de carpeta con asociaciones pendientes.
5. Asociación RAW/JPEG por identidad + ventana de 30 s configurable.
6. Duplicados exactos mediante metadatos + hash cuando corresponda.
7. Naming de exportación basado en nombre de cámara y versiones.
8. Nunca sobrescribir una salida existente silenciosamente.
9. Máximo 2 retries técnicos tras ejecución inicial.
10. Recuperación mediante checkpoints de etapa.
11. Bloqueo recuperable ante falta de almacenamiento.
12. Recuperación escalonada ante OOM.
13. Preflight obligatorio de modelos/componentes.
14. Componentes no disponibles → `COMPONENT_UNHEALTHY`.
15. FIFO + `PROCESAR SIGUIENTE`.
16. Cancelar no elimina historial ni originales.
17. Rescate de `REJECTED_PRE` mediante override humano trazable.
18. `REVIEW_FINAL` como bandeja humana no bloqueante.
19. Retención física de originales mediante `COPY_TO_PROJECT` por defecto.

---

# 41. Decisiones diferidas a arquitectura técnica

Estas ya no son decisiones funcionales del usuario final y no bloquean la SRS:

- IPC exacto C# ↔ Python;
- REST/gRPC/Named Pipes;
- esquema SQL exacto;
- schema JSON definitivo;
- representación exacta de checkpoints;
- persistencia de circuit breaker;
- tiempos exactos de health checks;
- umbral exacto de circuit breaker;
- estrategia de locking;
- algoritmo exacto para detectar archivo completamente copiado;
- estrategia exacta de checksum incremental;
- implementación de watcher + reconciliación;
- estrategia de recuperación de procesos hijos;
- fallback de OpenCL/CUDA;
- estructura interna del Model Manager;
- estrategia de logs rotativos;
- estructura física final de carpetas temporales.

---

# 42. Próximo documento

Después de esta SRS, el siguiente paso será:

**Arquitectura Técnica v0.1**

Ese documento deberá definir:

- componentes;
- fronteras C# / Python;
- IPC;
- procesos;
- persistencia;
- watchdog;
- Model Manager;
- filesystem;
- contratos internos;
- estrategia de instalación;
- recuperación;
- seguridad y observabilidad.

---

# 43. Estado final

**PHOTO AI FACTORY — SRS v1.1**  
**Estado: APROBADO**

Este documento constituye el baseline funcional aprobado para iniciar la arquitectura técnica.
