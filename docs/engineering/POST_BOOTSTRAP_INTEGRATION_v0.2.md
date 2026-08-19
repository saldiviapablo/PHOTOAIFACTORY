# PHOTO AI FACTORY — Post-bootstrap Integration v0.2

## Estado recibido de Codex

Bootstrap reportado como READY:

- Darktable 5.6.0
- ComfyUI v0.33.1
- Python AI aislado 3.13.14
- PyTorch 2.13.0+cu130
- ONNX Runtime GPU 1.28.0
- RTX 4060 Ti 8 GB
- RF-DETR Medium
- MediaPipe Face/Pose
- Florence-2 Large
- Qwen3-VL-2B-Instruct-FP8
- DINOv2-S
- RawNIND / RGB NIND / NAFNet / RealPLKSR

Image-Adaptive 3D LUT y BiSeNet permanecen REVIEW_REQUIRED. Eso es correcto:
no son requisito para iniciar Phase 0.

## Separación de archivos

Archivos de bootstrap de Codex:

```text
config/components.lock.local.json
config/requirements-ai-worker.in.txt
tools/bootstrap/
```

Este Engineering Update **no modifica esos archivos**.

El código de ingeniería se agrega principalmente en:

```text
src/csharp/
src/python/ai-worker/
tools/dev/
docs/engineering/
config/phase0.dev.example.json
```

## Gap a verificar

El reporte de bootstrap no menciona .NET SDK.

El C# de Phase 0 targetea `net10.0`.

Antes del build se debe ejecutar:

```powershell
tools\dev\check-dotnet.ps1
```

Si falta .NET 10 SDK, se instala únicamente el SDK oficial estable x64.
WinUI/Visual Studio completo no es necesario todavía para este smoke test.

## Después de aplicar este update

1. No ejecutar todavía features de producto.
2. Compilar.
3. Self-tests C#.
4. Tests Python usando el runtime aislado.
5. Verificar el lock.
6. Corregir solo incompatibilidades reales.
7. Con smoke verde, empezar los Gates, comenzando por DT-01.
