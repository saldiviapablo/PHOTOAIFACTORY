# Model Policy v1.0

## Estados de un modelo

- `BASELINE` — candidato inicial.
- `APPROVED` — pasó benchmark y licencia.
- `OPTIONAL` — solo casos específicos.
- `REVIEW_REQUIRED` — no bundle hasta revisión.
- `REJECTED` — no utilizar.
- `SUPERSEDED` — reemplazado, se conserva para reproducibilidad histórica.

## Reglas

1. Nunca sustituir silenciosamente un modelo.
2. Cada ejecución registra model ID, versión y hash.
3. Cada modelo debe pasar:
   - quality benchmark;
   - VRAM/RAM benchmark;
   - performance benchmark;
   - license gate;
   - deterministic/reproducibility review cuando aplique.
4. Las versiones históricas no se actualizan retroactivamente.
5. Los modelos generativos de rostro no son default V1.
6. El retoque facial V1 prioriza máscaras + ajustes conservadores.
7. Cualquier checkpoint redistribuido debe revisarse independientemente del código.

## Baseline

### Understanding
- OpenCV
- RF-DETR Medium (no Plus)
- MediaPipe Face Landmarker
- MediaPipe Pose Landmarker; RTMPose como benchmark
- Florence-2 Large
- Qwen3-VL-2B-Instruct
- DINOv2-S estándar

### Editing
- Darktable 5.6 neural restore candidates
- RawNIND UtNet2
- NAFNet
- Image-Adaptive 3D LUT
- BiSeNet face parsing
- Retinexformer optional
- RealPLKSR optional
- LLF-LUT benchmark alternative
