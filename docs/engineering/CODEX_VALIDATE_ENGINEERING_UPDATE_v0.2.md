# Prompt para Codex — validar Engineering Update v0.2

El bootstrap de entorno ya terminó y está READY.

Ahora NO programes PHOTO AI FACTORY desde cero.
El código Phase 0 fue preparado previamente y acaba de integrarse.

Trabaja sobre la raíz actual de PHOTO AI FACTORY.

PRIMERO LEE:

- docs/engineering/POST_BOOTSTRAP_INTEGRATION_v0.2.md
- docs/engineering/PHASE0_ENGINEERING_DROP_v0.1.md
- docs/architecture/PHOTO_AI_FACTORY_ARCHITECTURE_v1.0.md
- docs/testing/PHOTO_AI_FACTORY_TEST_PLAN_v1.0.md

OBJETIVO

Validar el código ya entregado con el entorno Windows real.
Tu función ahora es compilar, ejecutar, detectar incompatibilidades reales y
hacer únicamente correcciones mínimas.

PASOS

1. Verifica `config/components.lock.local.json`.
2. Ejecuta:
   `powershell -ExecutionPolicy Bypass -File tools\dev\check-dotnet.ps1`
3. Si NO existe SDK .NET 10:
   - instala el SDK oficial estable .NET 10 x64 desde Microsoft;
   - no instales previews;
   - registra versión exacta;
   - no hace falta instalar WinUI/Visual Studio completo todavía.
4. Ejecuta:
   `powershell -ExecutionPolicy Bypass -File tools\dev\phase0-smoke.ps1`
5. Los tests Python DEBEN usar el runtime aislado de PHOTO AI FACTORY.
   No cambies los scripts para usar silenciosamente Python global.
6. Si C# no compila:
   - corrige solo errores reales de compilación;
   - no cambies arquitectura;
   - no agregues features;
   - no cambies PRD/SRS.
7. Verifica `DarktableCliAdapter` contra Darktable 5.6.0, sin ejecutar todavía
   una receta compleja.
8. Verifica `ComfyUiClient` contra ComfyUI v0.33.1:
   - `/system_stats`
   - `/prompt`
   - `/ws`
   - `/history/{prompt_id}`
   - `/queue`
   - `/interrupt`
   No agregues custom nodes.
9. Verifica que `ProcessRunner` no use shell concatenado.
10. Entrega un informe corto:

PHOTO AI FACTORY — ENGINEERING SMOKE REPORT

- .NET SDK exacto
- C# build PASS/FAIL
- self-tests PASS/FAIL
- Python tests PASS/FAIL
- Python aislado usado (ruta + versión)
- Darktable adapter PASS/FAIL
- ComfyUI API contract PASS/FAIL
- archivos que tuviste que modificar
- errores pendientes
- READY / NOT READY para DT-01

NO ejecutes todavía DT-01 con fotografías reales.
NO empieces WinUI.
NO implementes modelos semánticos.
NO implementes PRE-AI/FEEDBACK final.
NO hagas commits/push salvo orden explícita.
