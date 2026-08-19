$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path

& (Join-Path $PSScriptRoot "check-dotnet.ps1")

Push-Location (Join-Path $root "src\csharp")
try {
    dotnet restore .\PhotoAIFactory.Phase0.sln
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore falló" }

    dotnet build .\PhotoAIFactory.Phase0.sln -c Debug --no-restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet build falló" }
} finally {
    Pop-Location
}
