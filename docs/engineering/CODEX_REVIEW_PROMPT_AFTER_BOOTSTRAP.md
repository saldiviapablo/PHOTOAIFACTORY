# Prompt para Codex — revisión del Engineering Drop

Cuando termine el bootstrap, pedirle:

```text
No programes features nuevas todavía.

Sobre la raíz PHOTO AI FACTORY:
1. Lee docs/engineering/PHASE0_ENGINEERING_DROP_v0.1.md.
2. Ejecuta tools/dev/phase0-smoke.ps1.
3. Corrige únicamente errores de compilación/runtime causados por diferencias
   reales del entorno Windows/.NET/Python instalado.
4. No cambies arquitectura, PRD/SRS ni contratos sin reportarlo.
5. Verifica que DarktableCliAdapter use ProcessStartInfo.ArgumentList y no shell.
6. Verifica Python health con token.
7. Verifica ComfyUiClient contra la versión exacta instalada.
8. Si la API ComfyUI difiere, adapta SOLO Infrastructure y documenta el cambio.
9. Entrega un informe de build/tests y lista de cambios.
10. No comiences DT-01 hasta que yo lo autorice.
```
