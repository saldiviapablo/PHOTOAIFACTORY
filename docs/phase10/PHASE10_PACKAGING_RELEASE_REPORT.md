# PHOTO AI FACTORY — PHASE 10 PACKAGING & RELEASE ENGINEERING REPORT

**Release Version**: `1.0.0-rc.1`  
**Target Platform**: `Windows 10/11 x64`  
**Git Baseline**: `f441c2aeda4d1e5997daf6b90222500b2047742f`  
**Status**: **COMPLETE / READY FOR LEAD ENGINEER FINAL REVIEW**  

---

## 1. Executive Summary

Phase 10 delivers the complete Packaging, Deployment, Component Provisioning, and Release Engineering architecture for PHOTO AI FACTORY V1. The release guarantees a professional, zero-manual-setup user experience while enforcing strict legal, data sovereignty, and security boundaries.

### Key Architectural Invariants Implemented
1. **Zero Manual User Prerequisites**: End users do not manually install .NET, Windows App SDK, Python, Darktable, ComfyUI, or individual AI model checkpoints.
2. **Hybrid Deployment (ADR-025)**: Self-contained WinUI 3 desktop shell combined with an automated, cryptographically verified component provisioner operating under `%LOCALAPPDATA%\PhotoAIFactory\components\` and `models\`.
3. **Cryptographic Integrity Preflight**: All components are locked in `release/components.lock.json` with explicit SHA-256 hashes and size bytes. Staged payloads are verified *before* activation or promotion. Staged `.partial` files are never executed.
4. **Zip-Slip & Path Traversal Defense**: Safe archive extraction (`ArchiveExtractionHelper`) canonicalizes destination paths and strictly defends against malicious relative paths.
5. **GPL-3.0 Process Isolation & Source Offer**: External engines (Darktable 5.6.0, ComfyUI v0.33.1) are executed strictly across process/loopback boundaries. `THIRD_PARTY_NOTICES.txt` provides prominent source code offer links.
6. **Data Retention Safety on Uninstall**: Uninstallation cleanly removes application binaries and transient caches while strictly preserving user project databases, managed raw originals, exported final photos, and database backups.

---

## 2. Windows App SDK 2.4.0 Technical License Audit

- **Resolved Packages**: `Microsoft.WindowsAppSDK 2.4.0`, `Microsoft.WindowsAppSDK.Runtime 2.4.0`, `Microsoft.WindowsAppSDK.WinUI 2.3.6`, `Microsoft.Windows.SDK.BuildTools 10.0.26100.4654`.
- **License Terms Inspected**: NuGet package root `license.txt` (Microsoft Software License Terms).
- **Distributable Code Clause (Section 3.a.i)**:
  > *"Any files that are binplaced with your application by the WindowsAppSDK NuGet package are, by definition, permitted to be redistributed. This applies to both framework package dependent and self-contained deployments."*
- **Audit Result**: **PASS** — Commercial binary redistribution is officially granted. No Engineering Preview restrictions exist in stable 2.4.0.

---

## 3. Automated Component Provisioner & Lifecycle Verification

- **Provisioning Engine**: `ComponentProvisioningService` + `ReleaseManifestVerifier` + `ArchiveExtractionHelper`.
- **Preflight Inspections**: Disk space requirement verification (`IStorageSpaceInspector`), HTTPS download allowlist check, SHA-256 pre-activation check.
- **Side-by-Side Versioning**: Preserves historical component versions for deterministic job replay.
- **Repair Mechanism**: Atomic replacement of missing or corrupted components via hash verification.

---

## 4. Test Evidence & Validation Summary

| Test Suite | Total | Passed | Failed | Status |
|---|---|---|---|---|
| **Foundation Tests** | 112 | 112 | 0 | **PASS** |
| **Simulation / Integration Tests** (including 8 new Phase 10 tests) | 166 | 166 | 0 | **PASS** |
| **Python Worker Tests** | 37 | 37 | 0 | **PASS** |
| **Total Automated Tests** | **315** | **315** | **0** | **100% PASS** |
| **Package Vulnerability Audit** | 9 projects | 0 vulnerable | 0 | **PASS** |
| **Python Package Audit** (`uv pip check`) | 25 packages | 25 compatible | 0 | **PASS** |
| **Installer Lifecycle Test** (`test-release-install.ps1`) | 4 steps | 4 pass | 0 | **PASS** |
| **Release Build Script** (`build-release.ps1`) | 6 steps | 6 pass | 0 | **PASS** |

---

## 5. Release Trust & Code Signing Status

- **Signing Classification**: `PRODUCTION_SIGNING_PENDING` (Truthfully reported in `release-manifest.json` and build scripts; zero dev/test keys or PFX certificates committed to repository).

---

## 6. Hotfix: RC Startup Crash (WinUI 3 Self-Contained)

- **Bug Observed**: Installed application (`PhotoAIFactory.App.exe`) terminated immediately on startup with exit code `0xC000027B` and Windows Application Event Log error in faulting module `Microsoft.UI.Xaml.dll` (v3.2.3.0).
- **Root Cause**: `PhotoAIFactory.App.csproj` had `<EnableMsixTooling>false</EnableMsixTooling>` and `<AppxGeneratePriEnabled>false</AppxGeneratePriEnabled>`, which prevented the Windows App SDK MSBuild targets from compiling and generating `PhotoAIFactory.App.pri` (containing WinUI XAML binary resources) into the self-contained publish payload. Additionally, `<WindowsAppSdkUndockedRegFreeWinRTInitialize>` was not explicitly configured, causing `Microsoft.UI.Xaml.dll` to throw an unhandled WinRT resource lookup failure upon window initialization.
- **Fix Applied**:
  * Configured `<EnableMsixTooling>true</EnableMsixTooling>` and `<WindowsAppSdkUndockedRegFreeWinRTInitialize>true</WindowsAppSdkUndockedRegFreeWinRTInitialize>`.
  * Removed `<AppxGeneratePriEnabled>false</AppxGeneratePriEnabled>`.
  * Set `<PublishTrimmed>false</PublishTrimmed>`.
  * Verified generation of `PhotoAIFactory.App.pri` (2,221,888 bytes) in the self-contained publish output.
- **Regression Test Gate Added**: Updated `test-release-install.ps1` to execute the installed executable, wait 6 seconds to ensure the process remains active and running stably, and audit the Windows Application Event Log to ensure 0 crash events are emitted.

- **Public Distribution Status**: **NOT PUBLISHED** (Awaiting formal Lead Engineer review).
