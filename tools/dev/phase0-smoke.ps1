$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
Write-Host "== PHOTO AI FACTORY PHASE 0 SMOKE =="

Write-Host "[1/4] C# build"
& (Join-Path $PSScriptRoot "build-phase0.ps1")

Write-Host "[2/4] C# self-tests"
& (Join-Path $PSScriptRoot "run-selftests.ps1")

Write-Host "[3/4] Python tests"
& (Join-Path $PSScriptRoot "run-python-tests.ps1")

Write-Host "[4/4] Environment manifest"
$lock = Join-Path $root "config\components.lock.local.json"
if (Test-Path $lock) {
  Write-Host "Found $lock"
  Get-Content $lock | ConvertFrom-Json | Out-Null
  Write-Host "components.lock.local.json parses correctly"
} else {
  Write-Warning "Codex bootstrap has not created config/components.lock.local.json yet."
}
Write-Host "SMOKE COMPLETE"
