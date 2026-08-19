# Phase 0 Engineering Drop v0.1

## Objetivo

Avanzar código que no depende de tener todavía todos los modelos instalados.

## Implementado

- Domain state machine.
- Contratos AI.
- IPC client.
- ComfyUI adapter REST/WebSocket.
- Darktable CLI adapter conservador.
- GPU lease coordinator.
- File stability + SHA-256.
- Python Worker autenticado.
- OpenCV técnico real.
- Preselection/QA técnico base.
- Self-tests y pytest.

## Hard blocks deliberados

`PRE_AI` y `FEEDBACK` no devuelven recetas inventadas.

Hasta que DT-01 demuestre el control real de Darktable, esos endpoints responden
`MODEL_PIPELINE_NOT_READY`.

## Próximo merge

Cuando Codex termine bootstrap:

1. build C#;
2. pytest;
3. corregir compatibilidad concreta;
4. ejecutar DT-01;
5. ejecutar IPC-01/CUI-01/GPU-01.
