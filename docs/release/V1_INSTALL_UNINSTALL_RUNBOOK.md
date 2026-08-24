# PHOTO AI FACTORY V1 — INSTALLATION & UNINSTALLATION RUNBOOK

## 1. Installation Procedures

### 1.1 Clean Machine Installation (Standard User, Non-Admin)
1. Download the release package (`PhotoAIFactory-1.0.0-rc.1-x64-setup.zip` or MSIX).
2. Extract or run the installer. The installer deploys the self-contained WinUI 3 desktop shell to `%LOCALAPPDATA%\Programs\PhotoAIFactory\`.
3. Launch `PhotoAIFactory.App.exe` from Start Menu or desktop shortcut.

### 1.2 First-Run Provisioning Flow
1. On initial startup, the application verifies the internal release manifest against `components.lock.json`.
2. The `ComponentProvisioningService` performs a disk preflight check (~4–8 GB available disk space required on `%LOCALAPPDATA%`).
3. Required runtime components (isolated Python runtime, Python AI Worker environment) are unpacked/provisioned into:
   - `%LOCALAPPDATA%\PhotoAIFactory\components\<component_id>\<version>\`
4. Base AI model checkpoints are verified or downloaded from authoritative HTTPS repositories into:
   - `%LOCALAPPDATA%\PhotoAIFactory\models\<model_id>\<version>\`
5. The application enters `Healthy` operational state with all 11 UI pages fully accessible.

---

## 2. Component Repair & Maintenance
If a component executable or model checkpoint is modified, corrupted, or deleted:
1. The application's `ComponentHealthMonitor` flags the component as `Corrupted` or `Missing`.
2. Navigate to **Models & Engines** (`ModelsPage`).
3. Click **Repair Component**. The provisioner re-acquires the exact locked payload, verifies its SHA-256 hash, and performs atomic replacement.

---

## 3. Application Upgrade Procedures
1. Run the newer version installer (e.g. `1.0.1`).
2. The installer updates application binaries in `%LOCALAPPDATA%\Programs\PhotoAIFactory\`.
3. Operational project databases (`%LOCALAPPDATA%\PhotoAIFactory\projects\<project_id>\project.db`), managed original photos, final publication folders, and configuration versions are completely preserved.
4. Older component versions are retained side-by-side where required for historical job replay.

---

## 4. Uninstallation & Data Protection Policy
When running the uninstaller:
- **Removed**:
  - Application binary folder (`%LOCALAPPDATA%\Programs\PhotoAIFactory\`)
  - Start Menu shortcuts and registration
  - Temporary staging caches (`%LOCALAPPDATA%\PhotoAIFactory\temp\`)
- **Strictly Preserved**:
  - User project databases (`%LOCALAPPDATA%\PhotoAIFactory\projects\`)
  - Managed original files archive
  - Exported / published photo folders (`output_folder`)
  - Historical execution logs and database backups (`%LOCALAPPDATA%\PhotoAIFactory\backups\`)
