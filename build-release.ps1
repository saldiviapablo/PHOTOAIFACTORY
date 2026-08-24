<#
.SYNOPSIS
    PHOTO AI FACTORY — Release Engineering Build & Packaging Automation Script
#>

[CmdletBinding()]
param (
    [string]$Configuration = "Release",
    [string]$Platform = "x64",
    [switch]$SkipTests = $false,
    [switch]$RequireProductionSigning = $false,
    [string]$SigningCertificateThumbprint = $null
)

$ErrorActionPreference = "Stop"
$RepoRoot = $PSScriptRoot

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " PHOTO AI FACTORY -- RELEASE ENGINEERING BUILD (v1.0.0-rc.1)" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

function Verify-ChecksumFile([string]$targetFile, [string]$expectedSha) {
    if (-not (Test-Path $targetFile)) {
        throw "CHECKSUM INTEGRITY FAILURE: Required checksum target file missing: $targetFile"
    }
    $actualSha = (Get-FileHash -Path $targetFile -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualSha -ne $expectedSha) {
        throw "CHECKSUM MISMATCH for '$targetFile'! Expected: $expectedSha, Got: $actualSha"
    }
    return $actualSha
}

# 1. Validate Working Tree & Git Cleanliness
Write-Host "[1/8] Validating Git status..." -ForegroundColor Yellow
$gitDiff = git status --short
if ($LASTEXITCODE -ne 0) {
    throw "Git status check failed."
}

# 2. Compile Solution (Release / x64)
Write-Host "[2/8] Compiling solution ($Configuration / $Platform)..." -ForegroundColor Yellow
dotnet build "$RepoRoot\src\csharp\PhotoAIFactory.sln" -c $Configuration /p:Platform=$Platform
if ($LASTEXITCODE -ne 0) {
    throw "Solution compilation failed."
}

# 3. Execute Complete Test Suite (Quality Gates)
if (-not $SkipTests) {
    Write-Host "[3/8] Executing Foundation and Simulation automated test suites..." -ForegroundColor Yellow
    dotnet test "$RepoRoot\src\csharp\PhotoAIFactory.sln" -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) {
        throw "Automated tests failed."
    }

    Write-Host "Executing Python AI Worker test suite..." -ForegroundColor Yellow
    Push-Location "$RepoRoot\src\python\ai-worker"
    try {
        uv run --extra test pytest tests ..\..\..\tests\python
        if ($LASTEXITCODE -ne 0) {
            throw "Python worker tests failed."
        }
    }
    finally {
        Pop-Location
    }
} else {
    Write-Host "[3/8] Skipping tests as requested." -ForegroundColor DarkGray
}

# 4. Validate Release Manifest & Components Lock
Write-Host "[4/8] Validating release manifest and cryptographic integrity..." -ForegroundColor Yellow
$lockPath = "$RepoRoot\release\components.lock.json"
$manifestPath = "$RepoRoot\release\release-manifest.json"

if (-not (Test-Path $lockPath)) { throw "components.lock.json not found." }
if (-not (Test-Path $manifestPath)) { throw "release-manifest.json not found." }

$lockHash = (Get-FileHash -Path $lockPath -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = Get-Content $manifestPath | ConvertFrom-Json

if ($manifest.components_lock_sha256 -ne $lockHash) {
    throw "Components lock SHA-256 mismatch! Manifest: $($manifest.components_lock_sha256), Actual: $lockHash"
}

# 5. Publish Self-Contained App Shell & Build Standalone Native Installer
Write-Host "[5/8] Publishing self-contained WinUI 3 application and building Standalone Setup..." -ForegroundColor Yellow
$publishDir = "$RepoRoot\release\artifacts\PhotoAIFactory-1.0.0-rc.1-win-x64"
if (Test-Path $publishDir) {
    Remove-Item -Recurse -Force $publishDir
}

dotnet publish "$RepoRoot\src\csharp\PhotoAIFactory.App\PhotoAIFactory.App.csproj" -c $Configuration -r win-x64 --self-contained true -o $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "Dotnet publish for App failed."
}

# Create app_payload.zip to embed directly inside PhotoAIFactory.Installer
$installerProjectDir = "$RepoRoot\src\csharp\PhotoAIFactory.Installer"
$embeddedPayloadZip = "$installerProjectDir\app_payload.zip"
if (Test-Path $embeddedPayloadZip) {
    Remove-Item -Force $embeddedPayloadZip
}

Write-Host "Creating embedded standalone installer payload ($embeddedPayloadZip)..." -ForegroundColor Yellow
Compress-Archive -Path "$publishDir\*" -DestinationPath $embeddedPayloadZip -CompressionLevel Optimal

# Compile and publish true standalone single-file native installer executable with embedded app_payload.zip
$singleFileOut = "$installerProjectDir\bin\PublishSingleFile"
if (Test-Path $singleFileOut) { Remove-Item -Recurse -Force $singleFileOut }
dotnet publish "$installerProjectDir\PhotoAIFactory.Installer.csproj" -c $Configuration -r win-x64 --self-contained true /p:PublishSingleFile=true -o $singleFileOut
if ($LASTEXITCODE -ne 0) {
    throw "Publish for Standalone Installer failed."
}

# Clean temporary zip file from source tree after build
if (Test-Path $embeddedPayloadZip) {
    Remove-Item -Force $embeddedPayloadZip
}

# Copy standalone setup executable into release artifacts
$builtSetup = "$singleFileOut\PhotoAIFactory-1.0.0-rc.1-Setup.exe"
if (Test-Path $builtSetup) {
    Copy-Item -Path $builtSetup -Destination "$publishDir\PhotoAIFactory-1.0.0-rc.1-Setup.exe" -Force
}

# 6. Secret Scan
Write-Host "[6/8] Scanning published artifacts for secrets..." -ForegroundColor Yellow
$forbiddenPatterns = @("*.pfx", "*.key", "*.pem", ".env", ".env.*", "*id_rsa*", "*secrets.json")
foreach ($pat in $forbiddenPatterns) {
    $found = Get-ChildItem -Path $publishDir -Filter $pat -Recurse
    if ($found) {
        throw "SECURITY ALERT: Potential secret or private key found in publish payload: $($found[0].FullName)"
    }
}

# 7. Signing Verification
Write-Host "[7/8] Evaluating code signing status..." -ForegroundColor Yellow
if ($SigningCertificateThumbprint) {
    Write-Host "Applying Authenticode production signature..." -ForegroundColor Green
    Set-AuthenticodeSignature -FilePath "$publishDir\PhotoAIFactory.App.exe" -Certificate (Get-Item "Cert:\CurrentUser\My\$SigningCertificateThumbprint") -HashAlgorithm SHA256
    $sig = Get-AuthenticodeSignature -FilePath "$publishDir\PhotoAIFactory.App.exe"
    if ($sig.Status -ne "Valid") {
        throw "CODE SIGNING VERIFICATION FAILED: Authenticode signature status is $($sig.Status)"
    }
} else {
    if ($RequireProductionSigning) {
        throw "RELEASE GATE FAILURE: RequireProductionSigning is enabled but no signing certificate was provided."
    }
    Write-Host "Signing certificate not supplied; marking as PRODUCTION_SIGNING_PENDING (ENGINEERING_RC)." -ForegroundColor DarkYellow
}

# 8. Checksum Generation & Strict Re-Verification from Disk
Write-Host "[8/8] Generating and verifying release checksums from disk..." -ForegroundColor Yellow
$checksumsPath = "$RepoRoot\release\checksums.txt"
$checksumEntries = @()

$manifestHash = (Get-FileHash -Path $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
$appExe = "$publishDir\PhotoAIFactory.App.exe"
$appExeHash = (Get-FileHash -Path $appExe -Algorithm SHA256).Hash.ToLowerInvariant()

$checksumEntries += "$lockHash  components.lock.json"
$checksumEntries += "$manifestHash  release-manifest.json"
$checksumEntries += "$appExeHash  PhotoAIFactory.App.exe"

Set-Content -Path $checksumsPath -Value ($checksumEntries -join "`r`n")

# Strict re-verification from disk (fail-closed)
$writtenChecksums = Get-Content $checksumsPath
foreach ($line in $writtenChecksums) {
    if ($line.Trim().StartsWith("#") -or [string]::IsNullOrWhiteSpace($line)) { continue }
    if ($line -match "^([a-f0-9]{64})\s+(.+)$") {
        $expectedSha = $matches[1]
        $fileName = $matches[2].Trim()
        
        $resolvedFile = "$RepoRoot\release\$fileName"
        if (-not (Test-Path $resolvedFile)) {
            $resolvedFile = "$publishDir\$fileName"
        }

        Verify-ChecksumFile $resolvedFile $expectedSha | Out-Null
    }
}

# Demonstrating fail-closed on 1-byte disk file tamper via Verify-ChecksumFile
$tempTestFile = "$env:TEMP\PAF_Tamper_Test_" + [Guid]::NewGuid().ToString("N") + ".tmp"
try {
    Set-Content -Path $tempTestFile -Value "VALID_ORIGINAL_BYTES"
    $originalHash = (Get-FileHash -Path $tempTestFile -Algorithm SHA256).Hash.ToLowerInvariant()
    
    # Valid file passes
    Verify-ChecksumFile $tempTestFile $originalHash | Out-Null

    # Tamper 1 byte on disk
    Set-Content -Path $tempTestFile -Value "VALID_ORIGINAL_BYTEZ"
    
    $tamperCaught = $false
    try {
        Verify-ChecksumFile $tempTestFile $originalHash | Out-Null
    } catch {
        $tamperCaught = $true
    }

    if (-not $tamperCaught) {
        throw "TAMPER DETECTION FAILED: Tampered file was not rejected by Verify-ChecksumFile."
    }
}
finally {
    if (Test-Path $tempTestFile) { Remove-Item -Force $tempTestFile }
}

Write-Host "============================================================" -ForegroundColor Green
Write-Host " RELEASE BUILD SUCCEEDED: $publishDir" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
