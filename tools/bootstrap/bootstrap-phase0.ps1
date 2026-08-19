[CmdletBinding()]
param(
    [switch]$SkipModels,
    [switch]$SkipComfyUI,
    [switch]$SkipDarktable
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_bootstrap-common.ps1')

$paths = Get-PafPaths
Initialize-PafDirectories -Paths $paths
$logPath = Join-Path $paths.Logs ("bootstrap-phase0-{0}.log" -f ([DateTimeOffset]::Now.ToString('yyyyMMdd-HHmmss')))
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))

Write-PafLog -Message "PHOTO AI FACTORY Phase 0 bootstrap started. External root: $($paths.Root)" -LogPath $logPath

if (-not $SkipDarktable) {
    $darktableInstaller = Join-Path (Join-Path $paths.Cache 'downloads') 'darktable-5.6.0-win64.exe'
    Invoke-PafDownload `
        -Uri 'https://github.com/darktable-org/darktable/releases/download/release-5.6.0/darktable-5.6.0-win64.exe' `
        -Destination $darktableInstaller `
        -ExpectedSha256 'b42989195dfff44540c0b767b407987329ca99853612304cbbf14c48d1d3f803' `
        -AllowedRoot $paths.Cache -LogPath $logPath | Out-Null

    $darktableRoot = Join-Path $paths.Components 'darktable-5.6.0'
    $darktableCli = Join-Path $darktableRoot 'bin\darktable-cli.exe'
    $programFilesCli = Join-Path $env:ProgramFiles 'darktable\bin\darktable-cli.exe'
    if (-not (Test-Path -LiteralPath $darktableCli) -and -not (Test-Path -LiteralPath $programFilesCli)) {
        Write-PafLog -Message "Installing Darktable 5.6.0 silently at $darktableRoot" -LogPath $logPath
        $installer = Start-Process -FilePath $darktableInstaller `
            -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/DIR=$darktableRoot") `
            -WindowStyle Hidden -Wait -PassThru
        if ($installer.ExitCode -ne 0) { throw "Darktable installer exited with code $($installer.ExitCode)" }
    }
    & (Join-Path $PSScriptRoot 'check-darktable.ps1') | Format-List | Out-String | ForEach-Object {
        Write-PafLog -Message $_.Trim() -LogPath $logPath
    }
}

if (-not $SkipComfyUI) {
    $comfyArchive = Join-Path (Join-Path $paths.Cache 'downloads') 'ComfyUI_windows_portable_nvidia-v0.33.1.7z'
    Invoke-PafDownload `
        -Uri 'https://github.com/Comfy-Org/ComfyUI/releases/download/v0.33.1/ComfyUI_windows_portable_nvidia.7z' `
        -Destination $comfyArchive `
        -ExpectedSha256 '4a221588979b96b8244e0e50b2edca03af732acae1deba69d60aa3b4d60b9dba' `
        -AllowedRoot $paths.Cache -LogPath $logPath | Out-Null

    $comfyRoot = Join-Path $paths.Components 'comfyui'
    $comfyPython = Join-Path $comfyRoot 'python_embeded\python.exe'
    if (-not (Test-Path -LiteralPath $comfyPython)) {
        $sevenZip = @('C:\Program Files\7-Zip\7z.exe', 'C:\Program Files (x86)\7-Zip\7z.exe') |
            Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        if (-not $sevenZip) { throw '7-Zip is required to extract the official ComfyUI portable archive.' }

        $staging = Join-Path $paths.Components 'comfyui-v0.33.1-staging'
        if (-not (Test-PathUnderRoot -Path $staging -Root $paths.Components) -or
            -not (Test-PathUnderRoot -Path $comfyRoot -Root $paths.Components)) {
            throw 'Resolved ComfyUI paths are outside the components root.'
        }
        New-Item -ItemType Directory -Force -Path $staging | Out-Null
        Write-PafLog -Message "Extracting ComfyUI v0.33.1 to staging." -LogPath $logPath
        & $sevenZip 'x' '-y' "-o$staging" $comfyArchive
        if ($LASTEXITCODE -ne 0) { throw "7-Zip extraction failed with code $LASTEXITCODE" }
        $extracted = Join-Path $staging 'ComfyUI_windows_portable'
        if (-not (Test-Path -LiteralPath (Join-Path $extracted 'python_embeded\python.exe'))) {
            throw "Unexpected ComfyUI archive layout under $staging"
        }
        if (Test-Path -LiteralPath $comfyRoot) {
            throw "Refusing to replace existing incomplete ComfyUI directory: $comfyRoot"
        }
        Move-Item -LiteralPath $extracted -Destination $comfyRoot
    }
}

$uv = Get-Command 'uv.exe' -ErrorAction Stop
$pythonVersion = '3.12.12'
$aiRuntime = Join-Path $paths.Runtimes 'ai-worker'
$aiPython = Join-Path $aiRuntime 'Scripts\python.exe'
$env:UV_PYTHON_INSTALL_DIR = Join-Path $paths.Runtimes 'python-builds'
$env:UV_CACHE_DIR = Join-Path $paths.Cache 'uv'

if (-not (Test-Path -LiteralPath $aiPython)) {
    Write-PafLog -Message "Installing isolated Python $pythonVersion with uv under $($paths.Runtimes)." -LogPath $logPath
    & $uv.Source 'python' 'install' $pythonVersion
    if ($LASTEXITCODE -ne 0) { throw "uv python install failed with code $LASTEXITCODE" }
    & $uv.Source 'venv' '--python' $pythonVersion '--managed-python' $aiRuntime
    if ($LASTEXITCODE -ne 0) { throw "uv venv failed with code $LASTEXITCODE" }
}

$requirementsIn = Join-Path $repoRoot 'config\requirements-ai-worker.in.txt'
$requirementsLock = Join-Path $repoRoot 'config\requirements-ai-worker.lock.txt'
if (-not (Test-Path -LiteralPath $requirementsLock)) {
    Write-PafLog -Message 'Resolving the initial transitive AI worker lock.' -LogPath $logPath
    & $uv.Source 'pip' 'compile' '--python-version' '3.12' '--index-url' 'https://pypi.org/simple' `
        '--extra-index-url' 'https://download.pytorch.org/whl/cu130' '--index-strategy' 'unsafe-best-match' `
        '--output-file' $requirementsLock $requirementsIn
    if ($LASTEXITCODE -ne 0) { throw "uv pip compile failed with code $LASTEXITCODE" }
}

Write-PafLog -Message "Synchronizing isolated AI worker packages from $requirementsLock" -LogPath $logPath
& $uv.Source 'pip' 'sync' '--python' $aiPython '--index-url' 'https://pypi.org/simple' `
    '--extra-index-url' 'https://download.pytorch.org/whl/cu130' '--index-strategy' 'unsafe-best-match' $requirementsLock
if ($LASTEXITCODE -ne 0) { throw "uv pip sync failed with code $LASTEXITCODE" }
& $uv.Source 'pip' 'freeze' '--python' $aiPython | Set-Content -LiteralPath (Join-Path $aiRuntime 'installed-packages.txt') -Encoding utf8

if (-not $SkipModels) {
    & (Join-Path $PSScriptRoot 'download-models.ps1')
}

Write-PafLog -Message 'Running non-destructive environment verification.' -LogPath $logPath
& (Join-Path $PSScriptRoot 'verify-environment.ps1')
Write-PafLog -Message 'PHOTO AI FACTORY Phase 0 bootstrap finished.' -LogPath $logPath
