[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_bootstrap-common.ps1')

$paths = Get-PafPaths
$installRoot = Join-Path $paths.Components 'darktable-5.6.0'
$candidates = @(
    (Join-Path $installRoot 'bin\darktable-cli.exe'),
    (Join-Path $installRoot 'darktable-cli.exe'),
    (Join-Path $env:ProgramFiles 'darktable\bin\darktable-cli.exe')
)
$cli = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $cli) {
    throw "darktable-cli.exe was not found under the managed root or Program Files."
}

$output = & $cli '--version' 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "darktable-cli --version failed with exit code $LASTEXITCODE`n$output"
}
if (($output -join "`n") -notmatch '5\.6\.0') {
    throw "Unexpected Darktable version:`n$output"
}

[pscustomobject]@{
    status = 'PASS'
    version = '5.6.0'
    executable = $cli
    output = ($output -join "`n").Trim()
}
