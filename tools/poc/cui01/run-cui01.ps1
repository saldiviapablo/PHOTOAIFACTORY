param(
    [string]$OutputRoot = 'C:\Users\Pc\Documents\PHOTO AI FACTORY TESTS\CUI-01'
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$project = Join-Path $PSScriptRoot 'PhotoAIFactory.Cui01\PhotoAIFactory.Cui01.csproj'

foreach ($directory in @(
    $OutputRoot,
    (Join-Path $OutputRoot 'OUTPUT'),
    (Join-Path $OutputRoot 'WORK'),
    (Join-Path $OutputRoot 'LOGS'),
    (Join-Path $OutputRoot 'REPORT')
)) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

Write-Host "CUI-01 project: $projectRoot"
Write-Host "CUI-01 output: $OutputRoot"

& dotnet run --project $project -c Release -- $projectRoot $OutputRoot
if ($LASTEXITCODE -ne 0) { throw "CUI-01 PoC failed with exit code $LASTEXITCODE" }

