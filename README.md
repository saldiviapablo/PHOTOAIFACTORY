# PHOTO AI FACTORY

Baseline documental y arquitectónico **v1.0** preparado para iniciar desarrollo.

## Estado

- Producto: definido.
- Requisitos funcionales: aprobados.
- Arquitectura: aprobada como baseline.
- Modelo de datos: definido.
- Contratos internos: definidos a nivel de diseño.
- Pipeline IA: definido como línea fija con estaciones condicionales.
- Plan de pruebas/benchmarks: definido.
- Matriz de licencias: iniciada y con gates de redistribución.
- Código de producción: **todavía no iniciado deliberadamente**.

## Stack aprobado

- Windows 11 / Windows 64-bit.
- C# + .NET + WinUI 3 para la aplicación principal.
- Python como AI Worker.
- SQLite para estado durable.
- Darktable como motor RAW externo.
- ComfyUI como motor local de workflows IA.
- JSON + XMP para trazabilidad/reproducibilidad.
- Un Job pesado a la vez en V1.

## Regla de autoridad

**C# es la única fuente de verdad operativa.**

C# controla proyecto, cola, estado, checkpoints, SQLite, cancelación, reintentos,
Darktable, ComfyUI, recursos y publicación final.

Python analiza y devuelve resultados/planes/recetas; no controla la cola ni escribe SQLite.

## Gate técnico más importante

Antes de construir el pipeline completo se debe demostrar mediante PoC que la
automatización headless de Darktable puede aplicar de forma robusta el conjunto
de parámetros que PHOTO AI FACTORY necesita.

No se asumirá que `darktable-cli` permite modificar arbitrariamente cualquier
slider interno. La integración se limitará a mecanismos validados y versionados.

Ver:

`docs/testing/PHOTO_AI_FACTORY_TEST_PLAN_v1.0.md`

## Cómo leer este repositorio

1. `docs/requirements/` — qué debe hacer el producto.
2. `docs/architecture/` — cómo se divide el sistema.
3. `docs/adr/` — decisiones técnicas importantes y su motivo.
4. `docs/data/` — datos, almacenamiento y base.
5. `docs/contracts/` — contratos entre componentes.
6. `docs/ai/` — pipeline y política de modelos.
7. `docs/testing/` — PoCs, pruebas y benchmarks.
8. `docs/licenses/` — licencias y revisión de redistribución.
9. `docs/planning/` — orden de implementación.
10. `docs/deployment/` — instalación/versionado.
11. `docs/operations/` — recuperación ante fallos.

## Principios no negociables

- Nunca modificar el original.
- Nunca sobrescribir silenciosamente.
- Nunca retry infinito.
- Nunca sustituir un modelo silenciosamente.
- Nunca perder la cola.
- Nunca dejar que una sola fotografía bloquee toda la fábrica.
- Reprocesar siempre desde el original administrado.
- Persistir checkpoint antes de avanzar.
- Separar fallo técnico, duda visual y fallo de calidad.
- Mantener trazabilidad completa.
