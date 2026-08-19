[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Get-PafPaths {
    $root = if ($env:PHOTO_AI_FACTORY_HOME) {
        [System.IO.Path]::GetFullPath($env:PHOTO_AI_FACTORY_HOME)
    } else {
        Join-Path $env:LOCALAPPDATA 'PhotoAIFactory'
    }

    [ordered]@{
        Root       = $root
        Components = Join-Path $root 'components'
        Models     = Join-Path $root 'models'
        Runtimes   = Join-Path $root 'runtimes'
        Cache      = Join-Path $root 'cache'
        Logs       = Join-Path $root 'logs'
    }
}

function Initialize-PafDirectories {
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Paths)

    foreach ($path in $Paths.Values) {
        New-Item -ItemType Directory -Force -Path $path | Out-Null
    }
    New-Item -ItemType Directory -Force -Path (Join-Path $Paths.Cache 'downloads') | Out-Null
}

function Write-PafLog {
    param(
        [Parameter(Mandatory)][string]$Message,
        [ValidateSet('INFO', 'WARN', 'ERROR')][string]$Level = 'INFO',
        [string]$LogPath
    )

    $line = '{0} [{1}] {2}' -f ([DateTimeOffset]::Now.ToString('o')), $Level, $Message
    Write-Host $line
    if ($LogPath) {
        Add-Content -LiteralPath $LogPath -Value $line -Encoding utf8
    }
}

function Test-PathUnderRoot {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Root
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    $fullRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
    return $fullPath.Equals($fullRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $fullPath.StartsWith($fullRoot + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Get-PafSha256 {
    param([Parameter(Mandatory)][string]$Path)
    (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

function Invoke-PafDownload {
    param(
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$ExpectedSha256,
        [Parameter(Mandatory)][string]$AllowedRoot,
        [string]$LogPath
    )

    if (-not (Test-PathUnderRoot -Path $Destination -Root $AllowedRoot)) {
        throw "Refusing download outside allowed root: $Destination"
    }

    $parent = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Force -Path $parent | Out-Null

    if (Test-Path -LiteralPath $Destination) {
        $actual = Get-PafSha256 -Path $Destination
        if ($actual -eq $ExpectedSha256.ToLowerInvariant()) {
            Write-PafLog -Message "Checksum valid; reusing $Destination" -LogPath $LogPath
            return Get-Item -LiteralPath $Destination
        }

        $quarantine = '{0}.invalid-{1}' -f $Destination, ([DateTimeOffset]::Now.ToString('yyyyMMdd-HHmmss'))
        Move-Item -LiteralPath $Destination -Destination $quarantine
        Write-PafLog -Level WARN -Message "Existing checksum mismatch; preserved as $quarantine" -LogPath $LogPath
    }

    $partial = "$Destination.partial"
    $curl = Get-Command 'curl.exe' -ErrorAction Stop
    Write-PafLog -Message "Downloading $Uri" -LogPath $LogPath
    & $curl.Source '--fail' '--location' '--retry' '5' '--retry-all-errors' '--continue-at' '-' '--output' $partial $Uri
    if ($LASTEXITCODE -ne 0) {
        throw "Download failed with exit code ${LASTEXITCODE}: $Uri"
    }

    $actual = Get-PafSha256 -Path $partial
    if ($actual -ne $ExpectedSha256.ToLowerInvariant()) {
        throw "SHA-256 mismatch for $Uri. Expected $ExpectedSha256, got $actual. Partial file retained at $partial"
    }

    Move-Item -LiteralPath $partial -Destination $Destination
    Write-PafLog -Message "Verified SHA-256 $actual for $Destination" -LogPath $LogPath
    Get-Item -LiteralPath $Destination
}

function Get-PafDirectorySize {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return [int64]0 }
    $measure = Get-ChildItem -LiteralPath $Path -File -Recurse -Force -ErrorAction SilentlyContinue |
        Measure-Object -Property Length -Sum
    if ($null -eq $measure.Sum) { return [int64]0 }
    return [int64]$measure.Sum
}

function Get-PafDirectoryInventory {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) { return @() }
    $root = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    @(
        Get-ChildItem -LiteralPath $root -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
            [ordered]@{
                file = $_.FullName.Substring($root.Length).TrimStart('\').Replace('\', '/')
                size = $_.Length
                sha256 = Get-PafSha256 -Path $_.FullName
            }
        }
    )
}
