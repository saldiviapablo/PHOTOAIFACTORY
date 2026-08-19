# License Matrix v1.0

**Importante:** esto es una revisión técnica de licencias, no asesoramiento legal.
Antes de distribuir comercialmente un instalador se debe revisar el artifact
exacto, su versión y sus notices.

| Componente | Licencia observada | Estado bundle | Nota |
|---|---|---|---|
| OpenCV 4.5+ | Apache-2.0 | OK_CON_NOTICES | Fuente oficial OpenCV |
| ONNX Runtime | MIT | OK_CON_NOTICES | Fuente Microsoft |
| MediaPipe código | Apache-2.0 | OK_CON_NOTICES | Revisar también el model asset exacto |
| RF-DETR Medium / Apache-designated | Apache-2.0 | OK_CON_NOTICES | **No** asumir lo mismo para XL/2XL Plus |
| Qwen3-VL-2B-Instruct | Apache-2.0 | OK_CON_NOTICES | Verificar quantization artifact exacto |
| Florence-2 Large | MIT | OK_CON_NOTICES | Verificar peso exacto por hash |
| DINOv2 estándar | Apache-2.0 | OK_CON_NOTICES | Evitar X-Ray/Cell specialty licenses |
| Darktable | GPL-3.0 | LEGAL_REVIEW_DISTRIBUTION | Mantener como proceso separado + obligations |
| darktable-ai tooling | GPL-3.0 | LEGAL_REVIEW_DISTRIBUTION | Modelos curados GPL-compatible |
| ComfyUI | GPL-3.0 | LEGAL_REVIEW_DISTRIBUTION | Proceso separado; preservar obligaciones |
| NAFNet código | MIT + componentes Apache-2.0 | ARTIFACT_REVIEW | Revisar checkpoint exacto |
| Image-Adaptive 3D LUT | Apache-2.0 | ARTIFACT_REVIEW | Revisar pesos/datasets elegidos |
| Retinexformer repo | MIT | ARTIFACT_REVIEW | Revisar checkpoint exacto |
| LLF-LUT repo | MIT | ARTIFACT_REVIEW | Revisar checkpoint exacto |
| BiSeNet face parsing seleccionado | pendiente artifact exacto | REVIEW_REQUIRED | No bundle hasta seleccionar repo+weights |
| RealPLKSR elegido | dependerá artifact | REVIEW_REQUIRED | Preferir paquete darktable-ai aprobado |

## Reglas

- Código y pesos son artifacts distintos.
- Una licencia permisiva del repositorio no prueba por sí sola la licencia de
  cualquier checkpoint externo.
- Cada modelo bundled requiere: URL, versión, hash, licencia, NOTICE y origen.
- No utilizar RF-DETR Plus bajo la suposición de Apache-2.0.
- No utilizar variantes DINOv2 especializadas bajo la licencia del backbone estándar.
- GPL components permanecen como programas separados; cualquier distribución
  comercial deberá pasar revisión legal de empaquetado y obligaciones.
