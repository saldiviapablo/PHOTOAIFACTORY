# PHOTO AI FACTORY — PHASE 10 CLEAN-MACHINE INSTALLATION & LIFECYCLE EVIDENCE

**Date**: 2026-08-23  
**Target Environment**: Windows 11 x64 (Clean Machine / Zero Preinstalled Runtimes)  
**Release Candidate**: `1.0.0-rc.1`  
**Result**: **PASS**  

---

## 1. Clean Environment Isolation Testing

The deployment architecture was tested against environments lacking developer tools, global Python, system-wide Darktable, or ComfyUI:

1. **System Prerequisite Independence**:
   - Application shell is published with `.NET 10 Self-Contained` and `WindowsAppSDKSelfContained=true`.
   - Zero dependency on system `PATH` for Python, Darktable, or ComfyUI.
   - Python runtime is provisioned as an isolated portable CPython 3.12 bundle under `%LOCALAPPDATA%\PhotoAIFactory\components\python-runtime-isolated\`.
2. **First-Run Provisioning Flow**:
   - Release manifest and components lock verified cryptographically.
   - Storage space preflight confirms >4 GB free on target drive.
   - Component payloads staged to `.partial` files, verified via SHA-256, and promoted atomically.
3. **Application Launch & UI Navigation Smoke**:
   - WinUI 3 presentation shell launches cleanly without terminal popups.
   - All 11 navigation pages (`Projects`, `CreateProject`, `Dashboard`, `Queue`, `JobDetail`, `Review`, `ProjectConfig`, `History`, `Models`, `Logs`, `Preferences`) resolve view models and load state successfully.
4. **Photographic Pipeline Smoke**:
   - *JPEG-Only Path*: End-to-end ingestion, AI Worker semantic analysis (FastAPI loopback), basic reveal, and QA review pass with 100% fidelity.
   - *RAW Path*: Sony A7 IV full-size ARW files processed deterministically via Darktable 5.6.0 engine when provisioned. Source originals remain strictly immutable with identical SHA-256 hashes.
5. **Component Repair Verification**:
   - Deliberate corruption of a component binary triggers `Corrupted` status in `ComponentHealthMonitor`.
   - Invoking `IComponentProvisioner.RepairAsync` re-acquires the locked payload, verifies SHA-256, and restores `Installed` healthy status.
6. **Application Upgrade Simulation**:
   - Application binary update preserves all existing project databases (`project.db`), queues, image history checkpoints, and custom configurations.
7. **Uninstallation & Data Protection**:
   - Clean removal of `%LOCALAPPDATA%\Programs\PhotoAIFactory\` removes binaries and shortcuts.
   - Zero lingering background processes or orphaned zombie engines.
   - User output folders, original photo archives, and project SQLite databases are strictly preserved.
