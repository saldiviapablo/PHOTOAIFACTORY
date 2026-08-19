param([int]$Port = 8765)
$ErrorActionPreference = "Stop"

$root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
. (Join-Path $PSScriptRoot "resolve-ai-python.ps1")

$python = Resolve-PafAiPython -ProjectRoot $root
if (-not $env:PAF_AI_TOKEN) { $env:PAF_AI_TOKEN = [guid]::NewGuid().ToString("N") }
$env:PAF_AI_PORT = "$Port"
$worker = Join-Path $root "src\python\ai-worker"

Write-Host "AI Python: $python"
Write-Host "PAF_AI_TOKEN=$env:PAF_AI_TOKEN"
Write-Host "Port=$Port"

Push-Location $worker
try {
    & $python .\worker_entrypoint.py
    if ($LASTEXITCODE -ne 0) { throw "AI Worker terminó con código $LASTEXITCODE" }
} finally {
    Pop-Location
}
