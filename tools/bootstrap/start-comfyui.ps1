[CmdletBinding()]
param(
    [ValidateRange(1024, 65535)][int]$Port = 8188,
    [ValidateRange(5, 300)][int]$WaitSeconds = 120
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_bootstrap-common.ps1')

$paths = Get-PafPaths
Initialize-PafDirectories -Paths $paths
$root = Join-Path $paths.Components 'comfyui'
$python = Join-Path $root 'python_embeded\python.exe'
$main = Join-Path $root 'ComfyUI\main.py'
$workDir = Join-Path $root 'ComfyUI'
$statePath = Join-Path $paths.Logs 'comfyui-process.json'
$stdout = Join-Path $paths.Logs 'comfyui-stdout.log'
$stderr = Join-Path $paths.Logs 'comfyui-stderr.log'

foreach ($required in @($python, $main)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Missing ComfyUI runtime file: $required" }
}

if (Test-Path -LiteralPath $statePath) {
    $state = Get-Content -Raw -LiteralPath $statePath | ConvertFrom-Json
    $existing = Get-Process -Id $state.pid -ErrorAction SilentlyContinue
    if ($existing) {
        try {
            $health = Invoke-RestMethod -TimeoutSec 5 -Uri "http://127.0.0.1:$($state.port)/system_stats"
            [pscustomobject]@{ status = 'ALREADY_RUNNING'; pid = $state.pid; port = $state.port; health = $health }
            exit 0
        } catch {
            throw "PID $($state.pid) is running but the recorded loopback endpoint is unhealthy. Stop it explicitly before restart."
        }
    }
}

$arguments = @(
    '-s', $main,
    '--listen', '127.0.0.1',
    '--port', $Port.ToString(),
    '--disable-auto-launch',
    '--preview-method', 'none'
)
$process = Start-Process -FilePath $python -ArgumentList $arguments -WorkingDirectory $workDir `
    -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru

$deadline = [DateTimeOffset]::Now.AddSeconds($WaitSeconds)
do {
    Start-Sleep -Milliseconds 750
    if ($process.HasExited) {
        $tail = if (Test-Path -LiteralPath $stderr) { Get-Content -LiteralPath $stderr -Tail 80 } else { @() }
        throw "ComfyUI exited during startup with code $($process.ExitCode).`n$($tail -join "`n")"
    }
    try {
        $health = Invoke-RestMethod -TimeoutSec 5 -Uri "http://127.0.0.1:$Port/system_stats"
        break
    } catch {
        $health = $null
    }
} while ([DateTimeOffset]::Now -lt $deadline)

if (-not $health) {
    throw "ComfyUI did not become healthy on 127.0.0.1:$Port within $WaitSeconds seconds. Process PID: $($process.Id)"
}

[ordered]@{
    pid = $process.Id
    port = $Port
    listen = '127.0.0.1'
    executable = $python
    main = $main
    started_at = [DateTimeOffset]::Now.ToString('o')
} | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding utf8

[pscustomobject]@{ status = 'STARTED'; pid = $process.Id; port = $Port; health = $health }

