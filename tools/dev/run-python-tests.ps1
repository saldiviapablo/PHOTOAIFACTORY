$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
. (Join-Path $PSScriptRoot "resolve-ai-python.ps1")
$python = Resolve-PafAiPython -ProjectRoot $root
$worker = Join-Path $root "src\python\ai-worker"

Write-Host "AI Python: $python"
Push-Location $worker
try {
    & $python -m pytest -q
    if ($LASTEXITCODE -ne 0) { throw "Python tests fallaron con código $LASTEXITCODE" }
} finally {
    Pop-Location
}
