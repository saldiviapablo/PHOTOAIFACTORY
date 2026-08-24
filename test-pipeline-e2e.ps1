<#
.SYNOPSIS
    PHOTO AI FACTORY — True Product Host Pipeline End-to-End Execution Evidence Runner
#>

[CmdletBinding()]
param (
    [string]$OutputDir = $null,
    [string]$RealRawFixture = "C:\Users\Pc\Documents\Editar para entregar\11082026 Mundialito de ciudades\DSC03593.ARW",
    [string]$RealJpegFixture = "C:\Users\Pc\Documents\Editar para entregar\Soles que dejan huellas - Pachamama - 09-08-2026\Exportadas\_DSC1200.JPG"
)

$ErrorActionPreference = "Stop"
$RepoRoot = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
if (-not $OutputDir) {
    $OutputDir = "$RepoRoot\docs\phase10"
}

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " PHOTO AI FACTORY -- TRUE PRODUCT HOST REAL E2E RUNNER" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
}

# Run the True Product Host Test Runner
Write-Host "[1/1] Launching PhotoAIFactory.TestHost Application Orchestration..." -ForegroundColor Yellow
$testHostProj = "$RepoRoot\tests\csharp\PhotoAIFactory.TestHost\PhotoAIFactory.TestHost.csproj"

& dotnet run --project $testHostProj -c Release -- "$RepoRoot"
if ($LASTEXITCODE -ne 0) {
    throw "PhotoAIFactory.TestHost execution failed with exit code $LASTEXITCODE"
}

Write-Host "============================================================" -ForegroundColor Green
Write-Host " TRUE PRODUCT HOST E2E TEST COMPLETED SUCCESSFULLY" -ForegroundColor Green
Write-Host " Evidence generated at: $OutputDir\PRODUCT_HOST_E2E_EVIDENCE.json" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
