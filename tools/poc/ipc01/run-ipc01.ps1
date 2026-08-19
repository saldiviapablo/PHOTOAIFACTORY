param(
    [string]$OutputRoot = 'C:\Users\Pc\Documents\PHOTO AI FACTORY TESTS\IPC-01'
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workerRoot = Join-Path $projectRoot 'src\python\ai-worker'
$project = Join-Path $PSScriptRoot 'PhotoAIFactory.Ipc01\PhotoAIFactory.Ipc01.csproj'
$resolver = Join-Path $projectRoot 'tools\dev\resolve-ai-python.ps1'

. $resolver
$python = Resolve-PafAiPython -ProjectRoot $projectRoot

foreach ($directory in @(
    $OutputRoot,
    (Join-Path $OutputRoot 'WORK'),
    (Join-Path $OutputRoot 'LOGS'),
    (Join-Path $OutputRoot 'REPORT')
)) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

Write-Host "IPC-01 isolated Python: $python"
Write-Host "IPC-01 output: $OutputRoot"

& dotnet run --project $project -c Release -- $python $workerRoot $OutputRoot
if ($LASTEXITCODE -ne 0) { throw "IPC-01 PoC failed with exit code $LASTEXITCODE" }

