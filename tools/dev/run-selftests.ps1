$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
dotnet run --project (Join-Path $root "src\csharp\PhotoAIFactory.SelfTests\PhotoAIFactory.SelfTests.csproj")
