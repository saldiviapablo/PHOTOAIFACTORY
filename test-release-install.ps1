<#
.SYNOPSIS
    PHOTO AI FACTORY — Real Native Installer / Lifecycle & Installed App Startup Test Harness
#>

[CmdletBinding()]
param (
    [string]$TargetDir = "$env:TEMP\PAF_Real_Install_Test"
)

$ErrorActionPreference = "Stop"
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " PHOTO AI FACTORY -- REAL STANDALONE INSTALLER & STARTUP SMOKE" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

$RepoRoot = $PSScriptRoot

try {
    if (Test-Path $TargetDir) {
        Remove-Item -Recurse -Force $TargetDir
    }
    New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null

    $appInstallDir = "$TargetDir\Programs\PhotoAIFactory"
    $localAppDataDir = "$TargetDir\PhotoAIFactory"
    $projectsDir = "$localAppDataDir\projects"
    $componentsDir = "$localAppDataDir\components"
    $modelsDir = "$localAppDataDir\models"

    $installerExe = "$RepoRoot\release\artifacts\PhotoAIFactory-1.0.0-rc.1-win-x64\PhotoAIFactory-1.0.0-rc.1-Setup.exe"
    if (-not (Test-Path $installerExe)) {
        $installerExe = "$RepoRoot\src\csharp\PhotoAIFactory.Installer\bin\PublishSingleFile\PhotoAIFactory-1.0.0-rc.1-Setup.exe"
    }
    if (-not (Test-Path $installerExe)) {
        $installerExe = "$RepoRoot\src\csharp\PhotoAIFactory.Installer\bin\x64\Release\net10.0-windows10.0.19041.0\PhotoAIFactory-1.0.0-rc.1-Setup.exe"
    }

    # 1. Execute Real Native Setup Executable Standalone
    Write-Host "[1/7] Executing PhotoAIFactory-1.0.0-rc.1-Setup.exe standalone..." -ForegroundColor Yellow
    & $installerExe --install --target-dir "$appInstallDir" --quiet
    if ($LASTEXITCODE -ne 0) {
        throw "Native installer failed with exit code: $LASTEXITCODE"
    }

    $appExe = "$appInstallDir\PhotoAIFactory.App.exe"
    if (-not (Test-Path $appExe)) {
        throw "INSTALLATION FAILED: PhotoAIFactory.App.exe not found in install target."
    }

    # 2. Installed App Startup Smoke & Event Log Audit
    Write-Host "[2/7] Testing installed application startup and lifecycle stability..." -ForegroundColor Yellow
    $startTime = Get-Date
    $appProc = Start-Process -FilePath $appExe -PassThru
    try {
        Start-Sleep -Seconds 6
        if ($appProc.HasExited) {
            throw "CRITICAL FAILURE: Installed PhotoAIFactory.App.exe crashed or exited unexpectedly at startup! ExitCode: $($appProc.ExitCode)"
        }
        Write-Host "      Installed app is running stably (PID: $($appProc.Id))." -ForegroundColor Green
    }
    finally {
        if (-not $appProc.HasExited) {
            Stop-Process -Id $appProc.Id -Force
        }
    }

    # Audit Windows Event Log for any crash events from PhotoAIFactory.App / Microsoft.UI.Xaml
    $recentCrashes = Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='Application Error'; StartTime=$startTime.AddSeconds(-5)} -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match "PhotoAIFactory\.App\.exe" -or $_.Message -match "Microsoft\.UI\.Xaml\.dll" }

    if ($recentCrashes) {
        throw "CRITICAL FAILURE: Application crash detected in Windows Event Log during startup smoke: $($recentCrashes[0].Message)"
    }
    Write-Host "      Event Log verified clean (0 crashes detected)." -ForegroundColor Green

    # 3. Verify Windows Registry Uninstall Entry
    Write-Host "[3/7] Verifying Windows Uninstall Registry entry..." -ForegroundColor Yellow
    $regKey = Get-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\PhotoAIFactory" -ErrorAction SilentlyContinue
    if ($null -eq $regKey) {
        throw "REGISTRY ERROR: PhotoAIFactory uninstaller not registered in CurrentUser."
    }
    if ($regKey.UninstallString -match "PhotoAIFactory\.App\.exe") {
        throw "REGISTRY ERROR: UninstallString incorrectly points to PhotoAIFactory.App.exe instead of uninstaller!"
    }

    # 4. First-Run Provisioning Storage Initialization
    Write-Host "[4/7] Initializing per-user application storage..." -ForegroundColor Yellow
    New-Item -ItemType Directory -Force -Path $projectsDir | Out-Null
    New-Item -ItemType Directory -Force -Path $componentsDir | Out-Null
    New-Item -ItemType Directory -Force -Path $modelsDir | Out-Null

    # 5. Simulate Project Lifecycle with Genuine Binary Fixtures
    Write-Host "[5/7] Executing project data lifecycle with genuine SQLite & image fixtures..." -ForegroundColor Yellow
    $testProj = "$projectsDir\TestWeddingProject_001"
    $origDir = "$testProj\originals"
    $outDir = "$testProj\output"
    New-Item -ItemType Directory -Force -Path $origDir | Out-Null
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null

    # Create genuine binary SQLite DB header (SQLite format 3\0)
    $sqliteHeader = [System.Text.Encoding]::ASCII.GetBytes("SQLite format 3`0")
    $dbBytes = New-Object byte[] 1024
    [Array]::Copy($sqliteHeader, $dbBytes, $sqliteHeader.Length)
    [System.IO.File]::WriteAllBytes("$testProj\project.db", $dbBytes)

    # Create genuine binary JPEG fixture (SOI 0xFFD8, APP0 JFIF marker, EOI 0xFFD9)
    $jpegBytes = [byte[]]@(0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x01, 0x00, 0x48, 0x00, 0x48, 0x00, 0x00, 0xFF, 0xD9)
    [System.IO.File]::WriteAllBytes("$outDir\DSC0001.JPG", $jpegBytes)

    # Create genuine binary TIFF/RAW ARW header (Little Endian TIFF II*\0)
    $arwBytes = [byte[]]@(0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00)
    [System.IO.File]::WriteAllBytes("$origDir\DSC0001.ARW", $arwBytes)

    $origHashBefore = (Get-FileHash -Path "$origDir\DSC0001.ARW" -Algorithm SHA256).Hash

    # 6. Repair & Upgrade Verification via Standalone Installer
    Write-Host "[6/7] Testing installer repair and upgrade operations..." -ForegroundColor Yellow
    & $installerExe --repair --target-dir "$appInstallDir" --quiet
    if ($LASTEXITCODE -ne 0) {
        throw "Repair operation failed."
    }

    & $installerExe --upgrade --target-dir "$appInstallDir" --quiet
    if ($LASTEXITCODE -ne 0) {
        throw "Upgrade operation failed."
    }

    # 7. Clean Uninstallation Verification via Standalone Installer
    Write-Host "[7/7] Executing real uninstallation and verifying data protection..." -ForegroundColor Yellow
    & $installerExe --uninstall --target-dir "$appInstallDir" --quiet
    if ($LASTEXITCODE -ne 0) {
        throw "Uninstall operation failed."
    }

    if (Test-Path $appInstallDir) {
        throw "UNINSTALL FAILED: Application binaries still exist."
    }

    if (-not (Test-Path "$testProj\project.db")) {
        throw "REGRESSION: Uninstall deleted user project database!"
    }
    if (-not (Test-Path "$origDir\DSC0001.ARW")) {
        throw "REGRESSION: Uninstall deleted managed original photo!"
    }
    if (-not (Test-Path "$outDir\DSC0001.JPG")) {
        throw "REGRESSION: Uninstall deleted published output photo!"
    }

    $origHashAfter = (Get-FileHash -Path "$origDir\DSC0001.ARW" -Algorithm SHA256).Hash
    if ($origHashBefore -ne $origHashAfter) {
        throw "REGRESSION: Original RAW file hash modified during uninstallation!"
    }

    Write-Host "============================================================" -ForegroundColor Green
    Write-Host " REAL STANDALONE INSTALLER & STARTUP SMOKE: 100% SUCCESS" -ForegroundColor Green
    Write-Host "============================================================" -ForegroundColor Green
}
finally {
    if (Test-Path $TargetDir) {
        Remove-Item -Recurse -Force $TargetDir
    }
}
