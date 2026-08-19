# ComfyUI controlado directamente por C#

**Status:** Accepted

## Decisión
Python produce `ComfyPlan`; C# ejecuta ComfyUI REST/WebSocket.

## Motivo
Mantener cancelación, retries, GPU leases, checkpoints y observabilidad en el
orquestador authoritative.

## Consecuencia
C# usa prompt/history/ws/interrupt/queue según contrato versionado.
