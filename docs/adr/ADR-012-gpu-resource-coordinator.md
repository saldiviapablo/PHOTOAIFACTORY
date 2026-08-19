# GPU Resource Coordinator

**Status:** Accepted

## Decisión
C# entrega leases exclusivos de GPU a Python, Darktable AI o ComfyUI.

## Motivo
Cada proceso administra su propia memoria; un Model Manager Python no puede
controlar VRAM de procesos externos.

## Consecuencia
Las transiciones entre super-etapas incluyen liberación/health/memory checks.
