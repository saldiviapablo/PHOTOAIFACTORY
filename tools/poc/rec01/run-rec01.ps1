[CmdletBinding()]
param(
    [string]$EvidenceRoot = 'C:\Users\Pc\Documents\PHOTO AI FACTORY TESTS\REC-01',
    [string]$Fixture = 'C:\Users\Pc\Documents\PHOTO AI FACTORY TESTS\DT-01\INPUT\_DSC1627.JPG'
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'PhotoAIFactory.Rec01\PhotoAIFactory.Rec01.csproj'

if (-not (Test-Path -LiteralPath $Fixture -PathType Leaf)) {
    throw "REC-01 fixture not found: $Fixture"
}

dotnet build $project --configuration Release --nologo
if ($LASTEXITCODE -ne 0) {
    throw "REC-01 Release build failed with exit code $LASTEXITCODE."
}

$executable = Join-Path $PSScriptRoot 'PhotoAIFactory.Rec01\bin\Release\net10.0\PhotoAIFactory.Rec01.exe'
& $executable --mode self-test
if ($LASTEXITCODE -ne 0) {
    throw "REC-01 self-tests failed with exit code $LASTEXITCODE."
}

$vulnerabilityOutput = & dotnet list $project package --vulnerable --include-transitive 2>&1
$vulnerabilityOutput | ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0) {
    throw "Vulnerable package audit failed with exit code $LASTEXITCODE."
}
if (($vulnerabilityOutput -join "`n") -match 'has the following vulnerable packages') {
    throw 'Vulnerable packages were reported for REC-01.'
}

& $executable `
    --mode controller `
    --evidence $EvidenceRoot `
    --fixture $Fixture `
    --build-verified true `
    --self-tests-verified true `
    --vulnerable-packages 'PASS - 0 paquetes vulnerables directos/transitivos; sin actualizar paquetes.'
if ($LASTEXITCODE -ne 0) {
    throw "REC-01 suite failed with exit code $LASTEXITCODE."
}
