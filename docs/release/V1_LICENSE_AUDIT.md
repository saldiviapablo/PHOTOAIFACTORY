# PHOTO AI FACTORY V1 — COMPREHENSIVE LICENSE & REDISTRIBUTION AUDIT

**Release Version**: `1.0.0-rc.1`  
**Date**: 2026-08-23  
**Status**: COMPLETE / TECHNICAL AUDIT PASSED  

---

## 1. Executive Summary & Legal Guardrails

PHOTO AI FACTORY enforces strict technical boundaries to respect third-party software and AI model licenses:
1. **Proprietary Application Code**: The desktop shell (`PhotoAIFactory.App`), Core Application, Domain, and Infrastructure assemblies are proprietary.
2. **GPL-3.0 Separation**: Darktable (5.6.0) and ComfyUI (v0.33.1) are licensed under GPL-3.0. They are executed strictly as independent external CLI/API processes via standard operating system process spawning and loopback network communication. No GPL code is compiled into or statically linked with the proprietary C# application binaries.
3. **Microsoft Windows App SDK 2.4.0**: Audited directly from NuGet package artifacts (`Microsoft.WindowsAppSDK 2.4.0`, `Microsoft.WindowsAppSDK.Runtime 2.4.0`, `Microsoft.WindowsAppSDK.WinUI 2.3.6`). Section 3 explicitly grants commercial redistribution rights for binplaced files in both self-contained and framework-dependent deployments. No "Engineering Preview" restrictions exist in the 2.4.0 stable release.
4. **AI Models Review**: Every model checkpoint is tracked as a distinct artifact with explicit licensing and redistribution terms. Any artifact classified as `REVIEW_REQUIRED` (e.g. non-commercial research checkpoints or unverified licenses) is strictly excluded from offline redistribution packs.

---

## 2. Component-by-Component License Matrix

| Component ID | Component Name | Kind | License | Redistribution Status | Commercial Use Permitted | Notes / Source Offer |
|---|---|---|---|---|---|---|
| `photoaifactory-app` | PHOTO AI FACTORY Desktop Shell | Application | Proprietary | Distributable | Yes | Main user presentation shell. |
| `windowsappsdk-runtime` | Microsoft Windows App SDK 2.4.0 | Runtime | Microsoft Distributable Code | Distributable | Yes | Audited in NuGet cache `license.txt`. |
| `system-drawing-common` | System.Drawing.Common 10.0.11 | Library | MIT | Distributable | Yes | Used exclusively for Windows UI thumbnail downscaling. |
| `python-runtime-isolated` | Standalone CPython 3.12.12 | Runtime | Python Software Foundation (PSF-2.0) | Distributable | Yes | Python Build Standalone distribution. |
| `opencv-python-headless` | OpenCV 4.10.x | Library | Apache-2.0 | Distributable | Yes | Computer vision & technical analysis engine. |
| `fastapi` / `uvicorn` | FastAPI / Uvicorn | Library | MIT / BSD-3-Clause | Distributable | Yes | Loopback IPC communication server. |
| `pydantic` | Pydantic 2.8.x | Library | MIT | Distributable | Yes | Structured JSON schema validation. |
| `darktable-engine` | Darktable 5.6.0 | External Engine | GPL-3.0-or-later | Automated Download / Source Offer | Yes | Executed via CLI. Source code available at github.com/darktable-org/darktable. |
| `comfyui-engine` | ComfyUI v0.33.1 | External Engine | GPL-3.0 | Automated Download / Source Offer | Yes | Executed via loopback API. Source code at github.com/comfyanonymous/ComfyUI. |
| `model-florence2-large` | Florence-2 Large | Model Weights | MIT | Distributable | Yes | Model weights by Microsoft (florence-community). |
| `model-mediapipe-face` | MediaPipe Face Landmarker | Model Asset | Apache-2.0 | Distributable | Yes | Google MediaPipe task asset. |
| `model-dinov2-vits14` | DINOv2 ViT-S/14 | Model Weights | Apache-2.0 | Distributable | Yes | Meta AI visual embedding weights. |
| `model-rfdetr-medium` | RF-DETR Medium | Model Weights | Apache-2.0 | Automated Download | Yes | Object detection checkpoint. |
| `model-qwen3-vl-2b` | Qwen3-VL-2B-Instruct | Model Weights | Tongyi Qianwen Research / Apache-2.0 | Automated Download | Conditional | Automated provisioner obtains exact hash. |

---

## 3. Windows App SDK 2.4.0 Specific Redistribution Audit

- **Package Inspected**: `Microsoft.WindowsAppSDK 2.4.0` (SHA512 metadata: verified)
- **License Document**: `license.txt` (93 lines, Microsoft Software License Terms)
- **Key Clauses Verified**:
  - **Section 1.a**: Rights to install and use to develop and test applications solely for Windows.
  - **Section 3.a.i**: *"Any files that are binplaced with your application by the WindowsAppSDK NuGet package are, by definition, permitted to be redistributed. This applies to both framework package dependent and self-contained deployments."*
  - **Section 3.b**: Requires adding primary functionality, protecting terms, and standard indemnification for custom application modifications.
- **Audit Finding**: **PASS** — Binary redistribution is explicitly authorized for production Windows V1 desktop application deployment.

---

## 4. GPL Compliance & Process Isolation Invariant

- **Process Isolation**:
  - Darktable is invoked via `ProcessRunner` calling `darktable-cli.exe`.
  - ComfyUI is supervised via `ComfyRuntimeSupervisor` communicating over `http://127.0.0.1:8188/`.
  - Python AI Worker is supervised via `PythonWorkerSupervisor` communicating over `http://127.0.0.1:8000/`.
- **Source Offer**: `THIRD_PARTY_NOTICES.txt` contains prominent instructions and direct repository links where full corresponding source code for Darktable and ComfyUI can be obtained under GPL-3.0.
