# PHOTO AI FACTORY — PRD v1.1

**Documento:** Product Requirements Document  
**Versión:** 1.1  
**Estado:** Aprobado  
**Plataforma inicial:** Windows 11 / Windows 64-bit  
**Aplicación principal:** C# + .NET + WinUI 3  
**Motor de IA y procesamiento:** Python  
**Revelado RAW:** Darktable CLI  
**Motor de workflows IA:** ComfyUI Server/API  
**Persistencia:** SQLite + JSON + XMP  
**Fecha:** Agosto de 2026  
**Cambio principal desde v1.0:** retención física administrada de originales (`COPY_TO_PROJECT`).

---

## 1. Visión del producto

PHOTO AI FACTORY será una aplicación de escritorio para Windows destinada a automatizar el análisis, preselección, revelado, edición, control de calidad y exportación de fotografías mediante una línea de producción local y modular.

El sistema deberá recibir fotografías de forma continua, analizarlas, colocarlas en una cola FIFO, procesarlas de a una, aplicar reglas y modelos especializados de IA, revelar mediante Darktable, utilizar ComfyUI cuando corresponda y generar una salida final con historial reproducible.

Para el usuario, PHOTO AI FACTORY deberá funcionar como una única aplicación, aunque internamente controle múltiples motores, procesos y modelos.

---

## 2. Objetivos de V1

La V1 deberá:

- recibir RAW, JPG y JPEG;
- detectar automáticamente parejas RAW + JPEG;
- generar preview solo cuando sea necesaria;
- permitir preselección configurable;
- administrar una cola FIFO persistente;
- procesar una fotografía a la vez;
- permitir pausa segura;
- soportar tres modos de revelado;
- controlar Darktable sin mostrar su interfaz;
- controlar ComfyUI como servicio local;
- mantener un historial completo;
- eliminar TIFF/DNG temporales tras su uso;
- producir JPEG final;
- permitir reprocesamiento;
- sobrevivir reinicios y errores;
- funcionar localmente por defecto.

**Objetivo normal:** 12–25 segundos por fotografía.  
**Objetivo de diseño:** ≤30 segundos por fotografía en la mayoría de los casos.  
**No es un timeout duro.**  
Prioridad: **calidad → consistencia → confiabilidad → velocidad**.

---

## 3. Plataforma y arquitectura

- **Frontend / aplicación principal:** C# + .NET + WinUI 3
- **Backend IA:** Python
- **Revelado:** Darktable CLI
- **Workflows IA:** ComfyUI Server/API
- **Persistencia:** SQLite + JSON + XMP
- **Ejecución:** local-first

Cada fotografía será una entidad lógica `Photo`. Cada ejecución será un `Job`.

Una `Photo` podrá contener:

- RAW;
- RAW + JPEG de cámara;
- JPEG solamente.

V1 utilizará **1 worker**. Solo una fotografía estará en procesamiento activo; las demás permanecerán en cola.

---

## 4. Creación de proyecto

Cada proyecto tendrá configuración propia y versionada.

Al crear un proyecto se deberá configurar:

- carpeta de entrada;
- carpeta de salida;
- inclusión o no de subcarpetas;
- preselección y filtros;
- modo de revelado;
- análisis semántico;
- estado de ComfyUI;
- tareas permitidas de ComfyUI;
- presets/perfiles;
- formato y calidad de salida;
- parámetros de QA;
- política de almacenamiento temporal.

---

## 5. Carpeta de entrada

El usuario seleccionará una **carpeta vigilada**.

Ejemplo:

```text
D:\CamRanger\Evento_Danza\
```

El sistema deberá:

- vigilar archivos nuevos;
- aceptar `.ARW`, `.JPG`, `.JPEG`;
- opcionalmente incluir subcarpetas;
- detectar parejas RAW + JPEG;
- esperar a que el archivo termine de copiarse;
- ignorar archivos ya procesados;
- detectar duplicados;
- no procesar archivos parciales;
- registrar la ruta de origen.

Cambiar la carpeta de entrada durante un proyecto requerirá **pausa segura**.

---

## 6. Carpeta de salida

El usuario seleccionará una **carpeta de exportación**.

Ejemplo:

```text
D:\Fotos_Finales\Evento_Danza\
```

Estructura sugerida:

```text
Evento_Danza/
├── FINAL/
├── REVISAR/
└── DESCARTADAS/
```

Requisitos:

- `FINAL` será obligatorio;
- `REVISAR` y `DESCARTADAS` serán configurables;
- la aplicación advertirá o impedirá una configuración donde entrada y salida puedan provocar reingesta;
- existirán accesos rápidos a “Abrir carpeta de entrada” y “Abrir carpeta de resultados”.

Las rutas formarán parte de la configuración versionada del proyecto.

---

## 7. Entrada RAW + JPEG

Cuando lleguen RAW + JPEG:

- ambos pertenecerán a una sola entidad `Photo`;
- el RAW será el original maestro;
- el JPEG de cámara se utilizará para análisis rápido y preselección;
- no se generará una preview adicional salvo necesidad técnica.

---

## 8. Entrada RAW sin JPEG

Cuando llegue solo RAW:

1. se conserva el RAW original;
2. se genera una preview;
3. la preview se usa para análisis;
4. el RAW se conserva intacto;
5. la preview podrá eliminarse si es regenerable.

---

## 9. Entrada JPEG solamente

Cuando llegue solo JPEG:

- no se generará una preview adicional como archivo;
- podrá generarse una copia reducida en memoria;
- el JPEG original se conservará intacto;
- todo reprocesamiento partirá nuevamente del JPEG original;
- nunca se encadenarán recompressiones sucesivas.

Se omitirán procesos exclusivos de RAW:

- demosaicing;
- módulos Bayer;
- denoise RAW;
- análisis lineal RAW;
- procesos dependientes de datos de sensor no disponibles.

---

## 10. Ingesta

V1 deberá soportar como mínimo **carpeta vigilada**.

Flujo:

1. detectar archivo;
2. verificar copia completa;
3. identificar tipo;
4. detectar pareja RAW/JPEG;
5. crear `Photo`;
6. crear `Job`;
7. registrar en SQLite;
8. conservar original;
9. iniciar análisis.

---

## 11. Preselección inteligente

Será configurable por proyecto.

Filtros posibles:

- nitidez/foco;
- blur;
- movimiento;
- rostro;
- ojos cerrados;
- ojos parcialmente cerrados;
- cantidad de personas;
- exposición;
- clipping;
- recuperabilidad;
- sujeto principal;
- duplicados;
- similitud dentro de ráfaga;
- composición.

Cada filtro podrá tener:

- activado/desactivado;
- umbral;
- sensibilidad;
- política ante duda.

Resultados:

- `APROBADA`
- `REVISAR_PRE`
- `DESCARTADA_PRE`

`DESCARTADA_PRE` nunca implica borrar el original.

---

## 12. Análisis semántico

Será configurable por proyecto:

### OFF
No ejecutar Florence/Qwen.

### ESTÁNDAR
Ejecutar análisis estructurado ligero, principalmente Florence-2.

### COMPLETO
Ejecutar Florence-2 + Qwen3-VL y generar JSON semántico detallado.

Se podrá guardar:

- tipo de escena;
- interior/exterior;
- cantidad de personas;
- sujeto principal;
- acción;
- iluminación;
- fondo;
- colores dominantes;
- composición;
- movimiento esperado;
- tags;
- descripción natural.

Los resultados de VLM serán probabilísticos y no serán la única base de decisiones críticas.

---

## 13. Baseline de modelos de análisis V1

### OpenCV
Foco, blur, histograma, clipping, geometría e inclinación.

### RF-DETR Medium
Personas, objetos, bounding boxes, segmentación y sujeto principal.

### MediaPipe Face Landmarker
Rostro, ojos, landmarks, párpados y orientación facial.

### MediaPipe Pose / RTMPose
Pose corporal y articulaciones.

### Florence-2 Large
Caption, regiones, relaciones visuales y análisis estructurado.

### Qwen3-VL-2B-Instruct
Comprensión semántica, contexto, descripción y JSON.

### DINOv2-S
Embeddings, similitud, ráfagas y futura selección de presets.

Todos son **baseline sujetos a benchmark** y deberán integrarse mediante interfaces reemplazables.

---

## 14. Cola FIFO y worker

Los Jobs aprobados pasarán a una cola persistente.

La cola deberá:

- persistir en SQLite;
- sobrevivir reinicios;
- mantener orden;
- mostrar posición;
- mostrar cantidad pendiente.

V1 utilizará **1 worker**.

---

## 15. Pausa segura

Botón: **PAUSAR**

Comportamiento:

1. usuario pulsa Pausar;
2. estado → `PAUSA_SOLICITADA`;
3. el Job actual termina;
4. el worker no toma otro Job;
5. estado → `PAUSADO`.

Durante pausa podrán modificarse:

- modo de revelado;
- preselección;
- análisis semántico;
- ComfyUI;
- tareas ComfyUI;
- presets;
- umbrales;
- carpetas;
- salida.

---

## 16. Versionado de configuración

Las configuraciones serán inmutables.

Cada cambio genera una nueva versión.

Cada fotografía guardará:

- `config_preselection`;
- `config_processing`;
- ID de versión;
- hash;
- fecha;
- parámetros;
- modelos y versiones.

Al crear una nueva configuración se podrá aplicar a:

1. solo fotografías futuras;
2. futuras + pendientes;
3. además crear nuevos Jobs de reprocesamiento para terminadas.

Si cambia la preselección y se aplica a pendientes, se podrá reejecutar la preselección.

---

## 17. Modos de revelado

Se selecciona al iniciar el proyecto y solo puede modificarse durante pausa segura.

Modos V1:

- `PRE_AI`
- `DT_AUTO`
- `FEEDBACK`

---

## 18. Modo PRE_AI

```text
ORIGINAL
↓
IA externa analiza
↓
genera receta
↓
Darktable
↓
resultado
```

La receta podrá incluir exposición, balance de blancos, temperatura, tinte, luces, sombras, contraste, geometría, denoise, preset y otros parámetros.

---

## 19. Modo DT_AUTO

```text
ORIGINAL
↓
Darktable
↓
automatismos
↓
IA interna disponible
↓
resultado
```

Darktable podrá usar correcciones determinísticas, presets, automatismos e IA interna cuando corresponda.

---

## 20. Modo FEEDBACK — diseño aprobado

FEEDBACK tendrá dos pasadas y una inspección intermedia.

### Pass 1

El RAW original entra a Darktable.

Puede aplicar:

- perfil de cámara;
- demosaicing;
- corrección de lente;
- aberraciones;
- balance/color calibration;
- exposición automática;
- preset base;
- automatismos de tono;
- IA interna de Darktable cuando corresponda.

Pass 1 es una **propuesta de revelado**.

### Salidas Pass 1

- XMP Pass 1;
- parámetros usados;
- TIFF RGB 16-bit temporal.

Del TIFF se genera una preview sRGB reducida **en memoria**.

### Inspección externa

La IA inspectora recibe:

**Contexto/original**
- EXIF;
- ISO;
- apertura;
- velocidad;
- focal;
- análisis técnico;
- análisis semántico previo.

**Resultado Pass 1**
- TIFF 16-bit;
- preview sRGB;
- XMP Pass 1;
- parámetros usados.

### Receta correctiva

La IA externa genera la receta final y puede decidir:

- exposición;
- WB;
- color;
- luces;
- sombras;
- contraste;
- geometría;
- preset;
- parámetros de módulos;
- uso o no de IA interna de Darktable.

### IA interna en Pass 2

La receta podrá decidir:

- Raw Denoise: ON/OFF;
- intensidad;
- RGB Denoise: ON/OFF;
- upscale: ON/OFF;
- otras funciones compatibles.

Por defecto se evitará Raw Denoise + RGB Denoise simultáneos salvo benchmark favorable.

### Raw Denoise

Si se recomienda:

```text
RAW original
↓
Neural Raw Denoise
↓
DNG temporal
↓
Darktable Pass 2
```

### Pass 2

Siempre parte de:

- RAW original; o
- DNG temporal derivado del RAW original si Raw Denoise fue autorizado.

Nunca parte del TIFF/JPEG de Pass 1.

Aplica correcciones determinísticas necesarias, receta externa final e IA interna autorizada.

### Salida

- si ComfyUI no interviene → JPEG final;
- si ComfyUI requiere alta profundidad → TIFF temporal → ComfyUI → JPEG final;
- QA;
- guardar historial;
- eliminar TIFF/DNG temporales.

---

## 21. ComfyUI

ComfyUI funcionará como servicio local en segundo plano.

PHOTO AI FACTORY deberá:

- iniciarlo;
- comprobar salud;
- enviar workflow;
- enviar archivo;
- recibir resultado;
- detectar errores;
- reiniciar cuando sea seguro;
- cerrarlo ordenadamente.

El usuario no necesitará ver la interfaz estándar.

---

## 22. Modos ComfyUI

### DESACTIVADO
No se utiliza.

### ACTIVADO
Todas las fotos ejecutan las tareas habilitadas.

### AUTOMÁTICO
El sistema decide qué tareas habilitadas necesita cada foto.

Nunca podrá activar una tarea no autorizada por el usuario.

El historial registrará por qué una tarea se ejecutó o se omitió.

---

## 23. Tareas ComfyUI configurables

Baseline:

- denoise RGB;
- color;
- retoque facial;
- máscaras faciales;
- low-light;
- upscale;
- nitidez.

---

## 24. Baseline de modelos de edición

- **Raw Denoise:** RawNIND UtNet2
- **Denoise RGB/JPEG:** NAFNet
- **Color:** Image-Adaptive 3D LUT
- **Alternativa color:** LLF-LUT
- **Face parsing:** BiSeNet
- **Low-light:** Retinexformer
- **Upscale:** RealPLKSR

Todos quedan sujetos a benchmark.

---

## 25. TIFF y DNG temporales

Los TIFF/DNG serán intermedios temporales.

Se generarán solo cuando una etapa posterior los necesite.

Un temporal solo se elimina cuando:

1. la siguiente etapa terminó correctamente;
2. el resultado fue validado;
3. el historial fue persistido.

En errores podrá conservarse temporalmente para diagnóstico/reintento.

---

## 26. Archivos permanentes

Por defecto:

- RAW/JPEG original;
- JPEG de cámara si existía;
- JPEG final;
- XMP;
- JSON de historial;
- SQLite.

No permanentes:

- TIFF;
- DNG temporal;
- previews regenerables;
- archivos de trabajo.

---

## 27. Historial

Cada Job conservará:

- original;
- proyecto;
- configuración;
- EXIF;
- análisis OpenCV;
- detecciones;
- rostros;
- ojos;
- pose;
- descripción semántica;
- embedding;
- preselección;
- modo de revelado;
- parámetros Darktable;
- XMP;
- todas las pasadas;
- decisiones IA;
- modelos/versiones;
- parámetros;
- prompts cuando corresponda;
- tareas ComfyUI;
- razones de ejecución/omisión;
- QA;
- errores;
- tiempos;
- rutas.

Persistencia:

- **SQLite:** estado operativo/consultas;
- **JSON inmutable por Job:** auditoría reproducible;
- **XMP:** receta/historial Darktable.

---

## 28. Reprocesamiento

Reprocesar siempre crea un **Job nuevo**.

Opciones previstas:

- repetir última ejecución;
- desde cero;
- usar configuración actual;
- repetir desde Darktable;
- repetir desde ComfyUI;
- usar configuración histórica.

Nunca se sobrescribe el Job anterior.

---

## 29. QA final

Baseline:

- OpenCV;
- RF-DETR;
- MediaPipe;
- Qwen3-VL solo si hace falta.

Evaluará:

- nitidez;
- exposición;
- clipping;
- rostros;
- ojos;
- artefactos;
- color extraño;
- cambios anómalos;
- calidad general.

Resultados:

- `COMPLETED`
- `REVIEW_FINAL`
- `ERROR`

---

## 30. Estados del Job

```text
RECEIVED
↓
ANALYZING
↓
REVIEW_PRE / REJECTED_PRE / QUEUED
↓
PROCESSING
↓
QA
↓
COMPLETED / REVIEW_FINAL / ERROR
```

Estados auxiliares:

- `PAUSE_REQUESTED`
- `PAUSED`
- `INTERRUPTED`
- `RETRYING`

---

## 31. Recuperación y errores

Ante cierre inesperado:

- recuperar proyectos;
- cola;
- Jobs pendientes;
- configuraciones;
- historial.

Un Job activo se marcará `INTERRUPTED`.

Cada etapa soportará política configurable de reintentos.

Si falla repetidamente:

- pasa a `ERROR`;
- el worker continúa;
- una sola foto nunca debe detener el proyecto.

---

## 32. Watchdog

Deberá detectar:

- Darktable colgado;
- ComfyUI caído;
- backend Python no disponible;
- timeout;
- crash;
- proceso zombie.

Podrá reiniciar componentes cuando sea seguro.

---

## 33. Model Manager

Gestión dinámica:

```text
NVMe
↓
RAM
↓
VRAM
```

El sistema decidirá qué precargar, mantener, cargar o descargar para evitar OOM.

---

## 34. Hardware de referencia

Inicial:

- NVIDIA RTX 4060 Ti 8 GB;
- 32 GB RAM;
- CPU moderna;
- NVMe.

Los mínimos/recomendados definitivos se decidirán tras benchmark.

---

## 35. Interfaz

Pantallas mínimas:

1. Proyectos
2. Crear proyecto
3. Dashboard
4. Cola
5. Job/Foto
6. Revisar
7. Configuración
8. Historial
9. Modelos
10. Logs/errores
11. Preferencias

La interfaz mostrará:

- proyecto;
- carpetas;
- recibidas;
- en cola;
- procesando;
- finalizadas;
- revisar;
- descartadas;
- errores;
- tiempo promedio;
- recursos;
- Pausar/Reanudar.

---

## 36. Principios UX

El usuario no necesitará:

- terminal;
- abrir Darktable;
- abrir ComfyUI;
- iniciar Python;
- cargar modelos manualmente;
- mover TIFF;
- editar JSON.

PHOTO AI FACTORY será la única interfaz de uso normal.

---

## 37. Instalación

V1 deberá aspirar a instalación unificada.

El usuario no debería instalar manualmente uno por uno:

- Python;
- dependencias;
- modelos;
- ComfyUI;
- componentes auxiliares.

La aplicación deberá detectar, validar y administrar componentes y versiones.

---

## 38. Privacidad

**Local-first.**

Ninguna fotografía, embedding, descripción o EXIF saldrá automáticamente de la PC.

Cualquier nube futura será explícita, opt-in y documentará qué datos envía.

---

## 39. Licencias

Por componente/modelo se registrará:

- nombre;
- versión;
- fuente;
- licencia;
- checksum;
- restricciones;
- redistribución.

No se incorporará ningún modelo al instalador sin revisión previa.

---

## 40. Dataset y Golden Set

### Dataset técnico
200–500 fotografías variadas.

### Golden Set
50–100 fotografías editadas manualmente como referencia.

Se medirá:

- color;
- exposición;
- denoise;
- piel;
- consistencia;
- necesidad de edición manual posterior.

---

## 41. Criterios de éxito V1

V1 será funcional cuando:

- reciba RAW/JPEG;
- detecte RAW+JPEG;
- genere preview solo si corresponde;
- preseleccione;
- administre FIFO;
- procese un Job a la vez;
- pause de forma segura;
- versiona configuración;
- ejecute PRE_AI, DT_AUTO y FEEDBACK;
- controle Darktable;
- controle ComfyUI;
- elimine temporales;
- conserve historial;
- permita reprocesar;
- produzca JPEG final;
- sobreviva reinicios;
- maneje errores sin bloquear el proyecto.

Objetivo de producto:

**la mayoría de las fotografías aprobadas no deberán necesitar edición manual posterior**.

---

## 42. Fuera de alcance V1

No será requisito inicial:

- editor manual tipo Photoshop;
- app móvil;
- macOS;
- Linux;
- nube obligatoria;
- multiusuario;
- colaboración;
- procesamiento distribuido;
- múltiples GPUs;
- marketplace;
- entrenamiento completo dentro de la app;
- venta directa.

---

## 43. Decisiones aprobadas

- Windows V1;
- C# + .NET + WinUI 3;
- Python;
- SQLite + JSON + XMP;
- Darktable CLI;
- ComfyUI Server/API;
- local-first;
- RAW/JPG/JPEG;
- pareja RAW+JPEG;
- carpetas entrada/salida configurables;
- preselección configurable;
- análisis semántico OFF/ESTÁNDAR/COMPLETO;
- FIFO;
- 1 worker;
- pausa segura;
- configuración versionada;
- PRE_AI;
- DT_AUTO;
- FEEDBACK;
- ComfyUI OFF/AUTO/ON;
- TIFF/DNG temporales;
- historial reproducible;
- reprocesamiento como Job nuevo;
- modelos reemplazables;
- objetivo de ~30 s sin timeout duro;
- retención física de originales mediante `COPY_TO_PROJECT` por defecto.

---

## 44. Decisiones diferidas

### SRS / funcional
- comportamiento exacto de filtros;
- reglas de pausa/reanudación;
- manejo detallado de errores;
- reglas de reprocesamiento;
- cambio de configuración.

### Arquitectura técnica
- contrato C# ↔ Python;
- schema SQLite;
- schema JSON;
- estrategia XMP;
- IPC;
- watchdog;
- adaptadores Darktable/ComfyUI;
- caché RAM/VRAM.

### IA / benchmark
- modelo exacto de receta PRE_AI;
- preset base FEEDBACK Pass 1;
- umbrales de foco/blur/ojos/exposición;
- RF-DETR definitivo;
- MediaPipe Pose vs RTMPose;
- cuantización de Qwen;
- RawNIND/NAFNet definitivos;
- 3D LUT vs LLF-LUT;
- Retinexformer;
- RealPLKSR;
- resoluciones;
- tiles;
- parámetros de inferencia.

### UX/UI
- wireframes;
- navegación;
- cola;
- pantallas de revisión.

### Deployment
- instalador;
- actualización de modelos;
- versiones Darktable/ComfyUI;
- rollback;
- checksums.

### QA / performance
- requisitos mínimos;
- requisitos recomendados;
- tiempos reales;
- Golden Set final;
- métricas de aceptación.

### Operación
- reintentos y timeouts;
- actualizaciones de modelos sin romper proyectos históricos;
- vigilancia exacta de carpetas;
- duplicados/archivos parciales;
- limpieza de temporales tras errores.

---

## 45. Próximos documentos

1. SRS / Functional Specification
2. Arquitectura técnica
3. Modelo de datos
4. ADRs
5. Matriz de licencias
6. Especificación del pipeline IA
7. Plan de benchmark
8. Backlog de desarrollo
9. Plan de implementación

---

## 46. Estado final

**PHOTO AI FACTORY — PRD v1.1**  
**Estado: APROBADO**

Este documento constituye el baseline de producto para la siguiente fase de especificación y arquitectura.
