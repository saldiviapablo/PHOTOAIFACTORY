# C# como fuente de verdad operativa

**Status:** Accepted

## Decisión
Solo C# decide transiciones durables del proyecto y los Jobs.

## Motivo
Evitar dos orquestadores y facilitar recuperación, cancelación y auditoría.

## Consecuencia
Python/ComfyUI/Darktable devuelven resultados; no mutan estado durable.
