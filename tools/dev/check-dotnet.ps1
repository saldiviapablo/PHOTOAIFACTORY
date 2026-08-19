$ErrorActionPreference = "Stop"

$cmd = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $cmd) {
    throw @"
No se encontró el .NET SDK.

El código de Phase 0 está preparado para .NET 10.
Pedir a Codex que instale/verifique el SDK oficial .NET 10 x64 antes de compilar.
No hace falta instalar WinUI/Visual Studio completo para este smoke test.
"@
}

Write-Host "dotnet: $($cmd.Source)"
& dotnet --version
if ($LASTEXITCODE -ne 0) { throw "dotnet --version falló" }

$sdks = & dotnet --list-sdks
if ($LASTEXITCODE -ne 0) { throw "dotnet --list-sdks falló" }
$sdks | ForEach-Object { Write-Host $_ }

if (-not ($sdks | Where-Object { $_ -match '^10\.' })) {
    throw "No hay un SDK .NET 10 instalado. Phase 0 targetea net10.0."
}
