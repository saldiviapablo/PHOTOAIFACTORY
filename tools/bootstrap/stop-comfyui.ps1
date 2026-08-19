[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_bootstrap-common.ps1')

$paths = Get-PafPaths
$statePath = Join-Path $paths.Logs 'comfyui-process.json'
if (-not (Test-Path -LiteralPath $statePath)) {
    [pscustomobject]@{ status = 'NOT_RUNNING'; notes = 'No process state file exists.' }
    exit 0
}

$state = Get-Content -Raw -LiteralPath $statePath | ConvertFrom-Json
$process = Get-Process -Id $state.pid -ErrorAction SilentlyContinue
if (-not $process) {
    $archived = Join-Path $paths.Logs ("comfyui-process-stale-{0}.json" -f ([DateTimeOffset]::Now.ToString('yyyyMMdd-HHmmss')))
    Move-Item -LiteralPath $statePath -Destination $archived
    [pscustomobject]@{ status = 'NOT_RUNNING'; notes = "Stale state archived at $archived" }
    exit 0
}

$expected = [System.IO.Path]::GetFullPath($state.executable)
$actual = [System.IO.Path]::GetFullPath($process.Path)
if (-not $actual.Equals($expected, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to stop PID $($state.pid): executable is $actual, expected $expected"
}

foreach ($endpoint in @('interrupt', 'free')) {
    try {
        Invoke-RestMethod -Method Post -ContentType 'application/json' -Body '{}' -TimeoutSec 5 `
            -Uri "http://127.0.0.1:$($state.port)/$endpoint" | Out-Null
    } catch {
        Write-Warning "ComfyUI /$endpoint did not respond: $($_.Exception.Message)"
    }
}

Stop-Process -Id $state.pid
$deadline = [DateTimeOffset]::Now.AddSeconds(30)
while ((Get-Process -Id $state.pid -ErrorAction SilentlyContinue) -and
       [DateTimeOffset]::Now -lt $deadline) {
    Start-Sleep -Milliseconds 200
}
if (Get-Process -Id $state.pid -ErrorAction SilentlyContinue) {
    throw "ComfyUI PID $($state.pid) did not stop within 30 seconds."
}

$archived = Join-Path $paths.Logs ("comfyui-process-stopped-{0}.json" -f ([DateTimeOffset]::Now.ToString('yyyyMMdd-HHmmss')))
Move-Item -LiteralPath $statePath -Destination $archived
[pscustomobject]@{ status = 'STOPPED'; pid = $state.pid; state_archive = $archived }
