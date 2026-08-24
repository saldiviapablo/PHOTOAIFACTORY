# ADR-025: PHOTO AI FACTORY V1 Packaging, Deployment, and Component Provisioning Strategy

## Status
Accepted

## Date
2026-08-23

## Context
PHOTO AI FACTORY is a Windows-first, local-first computational photography application that combines:
1. A C# .NET 10 desktop presentation shell built with WinUI 3 and Windows App SDK 2.4.0 (Stable).
2. A deterministic core pipeline orchestrating SQLite database state transitions and storage operations.
3. External local engines: Python AI Worker (FastAPI/PyTorch/OpenCV), Darktable 5.6.0 CLI, and ComfyUI v0.33.1 runtime.
4. Curated model artifacts for semantic detection, facial analysis, and QA inspection.

The distribution strategy for V1 must satisfy critical operational and legal invariants:
- **Zero Manual Setup**: End users must not be required to manually install .NET, Windows App SDK, Python, Darktable, or ComfyUI one by one.
- **Local-First & Data Sovereignty**: All processing executes on the local machine; original photo files and project SQLite databases must remain strictly immutable and isolated from installation directories.
- **Side-by-Side Versioning & Reproducibility**: Historical jobs preserve the exact component versions and model hashes that produced them.
- **Clean Install & Uninstall**: Uninstalling the application must clean binaries without touching user projects, managed originals, final published outputs, or historical backups.
- **License Integrity**: Separation of proprietary presentation/orchestration logic from GPL-3.0 external engines (Darktable, ComfyUI) and model weights.

## Options Evaluated

### Option 1: Monolithic Packaged MSIX Containing All Engines and Models
- **Description**: Package the WinUI 3 desktop shell, Python runtime, Darktable binaries, ComfyUI, and all model weights into a single massive MSIX package (~10+ GB).
- **Pros**: Single package file, Windows App identity, native installation/updates via Store or App Installer.
- **Cons**:
  - MSIX packages are installed into immutable virtualized package roots (`C:\Program Files\WindowsApps\...`).
  - Darktable, ComfyUI, and Python require writable cache, temp, and runtime scratch directories, causing severe virtualized filesystem redirection issues.
  - Updating a single AI model or external engine requires downloading and reinstalling the entire multi-gigabyte MSIX package.
  - Side-by-side historical model versioning within the immutable package is impractical and bloats update payloads.
  - Commercial/distribution licensing terms for different components cannot be decoupled.

### Option 2: Pure Unpackaged Portable Directory with Shell Scripts
- **Description**: Distribute a portable `.zip` or uncompressed directory with batch/PowerShell startup scripts.
- **Pros**: Simple local extraction without installation.
- **Cons**: Unprofessional user experience, missing Windows start menu integration, absence of verified integrity checks on first launch, vulnerability to accidental user file deletion or DLL search path hijacking.

### Option 3 (Selected): Hybrid Architecture — Self-Contained WinUI 3 Desktop Application + External Managed Component & Model Provisioner
- **Description**:
  1. **Application Shell**: Distributed as a self-contained WinUI 3 Windows desktop application (`WindowsAppSDKSelfContained=true`, `.NET 10 self-contained`, x64). Packaged via a clean, professional Windows installer or MSIX shell with package identity.
  2. **Managed Component Roots**: External engines (Python AI Worker runtime, Darktable 5.6.0, ComfyUI v0.33.1) and model checkpoints are provisioned and managed outside the application binary root, located under standard per-user local application storage:
     - `%LOCALAPPDATA%\PhotoAIFactory\components\<component_id>\<version>\`
     - `%LOCALAPPDATA%\PhotoAIFactory\models\<model_id>\<version>\`
  3. **Automated Component Provisioner**: Built-in C# provisioning engine (`IComponentProvisioner`) that detects component status, enforces cryptographic SHA-256 preflight verification before promotion, extracts archives with zip-slip defense, maintains side-by-side versions, and repairs corrupt/missing components.
  4. **Packaging Modality**:
     - *Primary Release Target*: Self-contained Windows installer / per-user setup bundling the verified application and offline component manifest.
     - *Secondary / Enterprise Target*: MSIX application package with external location (`Windows.DesktopBridge`), retaining Store compatibility while managing AI components in LocalAppData.

## Decision Drivers & Trade-Off Comparison

| Evaluation Metric | Option 1 (Monolithic MSIX) | Option 2 (Portable Zip) | Option 3 (Hybrid Self-Contained + Provisioner) |
|---|---|---|---|
| **User Setup Experience** | Single install but huge download | Manual scripts required | Single installer; automated background verification |
| **Component Upgrades** | Full multi-GB package re-download | Manual file replacement | Granular, delta-capable component provisioning |
| **GPL & License Boundary** | Blurred within single package | Separated | Strictly separated process boundaries & paths |
| **Reproducibility & Side-by-Side** | Poor (single immutable bundle) | Manual | Deterministic versioned subfolders per component |
| **Clean Uninstall Safety** | May delete redirected appdata | User must clean manually | Removes binaries; preserves user photos and DB |
| **Security & Path Hijack Defense** | Good | High risk | Strict allowlists, SHA-256 pre-activation checks |

## Architectural Invariants Enforced
1. **Separation of Concerns**: Application binaries reside in `%LOCALAPPDATA%\Programs\PhotoAIFactory` (or MSIX root). Mutable operational data resides in `%LOCALAPPDATA%\PhotoAIFactory\projects\`. Engines and models reside in `%LOCALAPPDATA%\PhotoAIFactory\components\` and `%LOCALAPPDATA%\PhotoAIFactory\models\`.
2. **Cryptographic Verification**: Every downloaded or staged payload is verified against `release/components.lock.json` via SHA-256 *before* activation or execution. Staged `.partial` files are never executed.
3. **Zip-Slip & Path Traversal Prevention**: Archive extraction canonicalizes destination paths and strictly rejects any entry attempting path traversal (`../`) outside the target directory.
4. **Data Protection on Uninstall**: Uninstallers remove only installed application binaries and runtime caches. User project databases, managed originals, final published outputs, and historical backups are strictly preserved.
5. **Signing & Trust Transparency**: The release engineering pipeline supports transparent classification: `DEV_SIGNED`, `TEST_SIGNED`, `PRODUCTION_SIGNED`, or `PRODUCTION_SIGNING_PENDING`. Private keys and signing secrets are never committed to version control.

## Consequences
- **Positive**:
  - Streamlined, professional installation for users on clean Windows 11 machines.
  - Zero manual command-line setup for Python, Darktable, or ComfyUI.
  - Full adherence to GPL-3.0 license boundaries and individual model licensing constraints.
  - Complete historical reproducibility with multi-version component co-existence.
- **Negative / Mitigations**:
  - First-run experience requires disk space preflight (~4–8 GB depending on installed model set); mitigated by preflight checks (`IStorageSpaceInspector`) and honest progress UI.
