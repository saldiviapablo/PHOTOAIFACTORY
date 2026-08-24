# PHOTO AI FACTORY V1 — RELEASE ENGINEERING CHECKLIST

## 1. Pre-Build Invariants
- [x] Branch is `main`, clean working tree without untracked personal files or build artifacts.
- [x] Solution builds in Release configuration with 0 warnings and 0 errors.
- [x] Automated test suite passes 100% (Foundation, Simulation, Python Worker, UI/Integration).
- [x] Package vulnerability audit (`dotnet list package --vulnerable --include-transitive`) reports 0 vulnerabilities.
- [x] Python dependencies audit (`uv pip check`) reports full compatibility.
- [x] No `REVIEW_REQUIRED` model weights bundled into offline distribution packages.
- [x] No forbidden development/debug test flags (e.g. `PAF_ALLOW_TEST_FORCE_DECISION`) enabled in production release configuration.

## 2. Component & License Manifest Verification
- [x] `release/components.lock.json` contains explicit locked versions, hashes, and licenses for all components.
- [x] `release/release-manifest.json` matches SHA-256 of `components.lock.json`.
- [x] `docs/release/THIRD_PARTY_NOTICES.txt` contains full notices and GPL source offers for Darktable and ComfyUI.
- [x] Windows App SDK 2.4.0 distribution terms verified.

## 3. Code Signing & Release Trust
- [x] Release build script (`build-release.ps1`) signs assemblies and installer if certificate parameters are supplied.
- [x] If signing certificate is absent, release manifest truthfully reports `PRODUCTION_SIGNING_PENDING`.
- [x] Zero signing secrets, PFX certificates, or private keys committed to Git.

## 4. Post-Build Verification
- [x] `release/SBOM/sbom.cyclonedx.json` generated and complete.
- [x] `release/checksums.txt` generated for all distributable binaries.
- [x] Automated installer & lifecycle test suite (`test-release-install.ps1`) verifies clean install, first-run provisioning, project execution, and safe uninstallation.
