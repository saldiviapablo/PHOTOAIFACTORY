[CmdletBinding()]
param(
    [ValidateSet('Core', 'Recipe', 'Final')][string]$Stage = 'Core',
    [string]$TestRoot = 'C:\Users\Pc\Documents\PHOTO AI FACTORY TESTS\DT-01',
    [string]$DarktableCli = 'C:\Program Files\darktable\bin\darktable-cli.exe',
    [string]$DarktableIdentify = 'C:\Program Files\darktable\bin\darktable-rs-identify.exe',
    [string]$Python = 'C:\Users\Pc\AppData\Local\PhotoAIFactory\runtimes\ai-worker\Scripts\python.exe',
    [string]$RecipeXmp = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$inputDir = Join-Path $TestRoot 'INPUT'
$runDir = Join-Path $TestRoot 'RUN2'
$outputDir = Join-Path $runDir 'OUTPUT'
$workDir = Join-Path $runDir 'WORK'
$logDir = Join-Path $workDir 'LOGS'
$configDir = Join-Path $workDir 'DARKTABLE_CONFIG'
$cacheDir = Join-Path $workDir 'DARKTABLE_CACHE'
$reportDir = Join-Path $runDir 'REPORT'
$jpegDir = Join-Path $outputDir '01_BASELINE_JPEG'
$tiffDir = Join-Path $outputDir '02_PASS1_TIFF'
$xmpOutputDir = Join-Path $outputDir '03_XMP_EDIT'
$repeatDir = Join-Path $outputDir '04_REPEATABILITY'
$pass2Dir = Join-Path $outputDir '05_PASS2'
$neuralDir = Join-Path $outputDir '06_NEURAL_RESTORE'
$xmpDir = Join-Path $workDir 'XMP'
$inspector = Join-Path $PSScriptRoot 'inspect_media.py'

foreach ($directory in @($runDir, $outputDir, $workDir, $logDir, $configDir, $cacheDir, $reportDir, $jpegDir, $tiffDir, $xmpOutputDir, $repeatDir, $pass2Dir, $neuralDir, $xmpDir)) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

foreach ($required in @($DarktableCli, $DarktableIdentify, $Python, $inspector)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required file not found: $required" }
}

$raws = @(Get-ChildItem -LiteralPath $inputDir -File | Where-Object { $_.Extension -ieq '.ARW' } | Sort-Object Name)
$jpgs = @(Get-ChildItem -LiteralPath $inputDir -File | Where-Object { $_.Extension -ieq '.JPG' } | Sort-Object Name)
if ($raws.Count -ne 3 -or $jpgs.Count -ne 3) { throw "RUN2 requires exactly 3 ARW and 3 JPG files; found $($raws.Count) ARW and $($jpgs.Count) JPG" }

function Get-HashRecord([System.IO.FileInfo]$File) {
    $hash = Get-FileHash -LiteralPath $File.FullName -Algorithm SHA256
    [pscustomobject]@{
        name = $File.Name
        extension = $File.Extension
        size_bytes = $File.Length
        last_write_utc = $File.LastWriteTimeUtc.ToString('o')
        sha256 = $hash.Hash.ToLowerInvariant()
    }
}

function Convert-ToDarktablePath([string]$Path) { $Path.Replace('\', '/') }

function Invoke-NativeLogged {
    param([string]$Executable, [object[]]$Arguments, [string]$LogPath)
    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $Executable @Arguments 2>&1 | Set-Content -LiteralPath $LogPath -Encoding utf8
        return $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $savedPreference }
}

function Invoke-Export {
    param(
        [System.IO.FileInfo]$Raw,
        [string]$Destination,
        [ValidateSet('jpeg', 'tiff')][string]$Format,
        [string]$LogName,
        [string]$Xmp = ''
    )

    if (Test-Path -LiteralPath $Destination) { throw "Refusing to overwrite existing output: $Destination" }
    $arguments = @((Convert-ToDarktablePath $Raw.FullName))
    if ($Xmp) { $arguments += (Convert-ToDarktablePath $Xmp) }
    $arguments += @(
        (Convert-ToDarktablePath $Destination), '--width', '0', '--height', '0', '--hq', 'true',
        '--apply-custom-presets', 'false', '--verbose', '--core',
        '--configdir', (Convert-ToDarktablePath $configDir), '--cachedir', (Convert-ToDarktablePath $cacheDir),
        '--library', ':memory:'
    )
    if ($Format -eq 'jpeg') {
        $arguments += @('--conf', 'plugins/imageio/format/jpeg/quality=95')
    }
    else {
        $arguments += @(
            '--conf', 'plugins/imageio/format/tiff/bpp=16',
            '--conf', 'plugins/imageio/format/tiff/compress=1',
            '--conf', 'plugins/imageio/format/tiff/compresslevel=6',
            '--conf', 'plugins/imageio/format/tiff/shortfile=0'
        )
    }

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $exitCode = Invoke-NativeLogged -Executable $DarktableCli -Arguments $arguments -LogPath (Join-Path $logDir $LogName)
    $stopwatch.Stop()
    $created = Test-Path -LiteralPath $Destination -PathType Leaf
    $size = if ($created) { (Get-Item -LiteralPath $Destination).Length } else { 0 }
    [pscustomobject]@{
        input = $Raw.Name; format = $Format; xmp = $Xmp; destination = $Destination
        exit_code = $exitCode; duration_ms = $stopwatch.ElapsedMilliseconds
        created = $created; size_bytes = $size; pass = ($exitCode -eq 0 -and $created -and $size -gt 0)
    }
}

if ($Stage -eq 'Core') {
    $sourceFiles = @($raws + $jpgs | Sort-Object Name)
    @($sourceFiles | ForEach-Object { Get-HashRecord $_ }) | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $workDir 'initial_hashes.json') -Encoding utf8
    & $Python $inspector inventory @($sourceFiles.FullName) --output (Join-Path $workDir 'inventory.json') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Inventory failed: $LASTEXITCODE" }

    $formatChecks = foreach ($raw in $raws) {
        $logPath = Join-Path $logDir "$($raw.BaseName)_identify.log"
        $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $exitCode = Invoke-NativeLogged -Executable $DarktableIdentify -Arguments @($raw.FullName) -LogPath $logPath
        $stopwatch.Stop()
        $content = Get-Content -Raw -LiteralPath $logPath
        [pscustomobject]@{
            input = $raw.Name; exit_code = $exitCode; duration_ms = $stopwatch.ElapsedMilliseconds
            reduced_raw_error = $content -match 'Unsupported photometric interpretation: 6'
            decoded = ($exitCode -eq 0 -and $content -match 'dimCropped: 7032x4688')
            log = $logPath
        }
    }
    $formatChecks | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $workDir 'raw_format_checks.json') -Encoding utf8
    if (@($formatChecks | Where-Object { -not $_.decoded -or $_.reduced_raw_error }).Count -gt 0) {
        throw 'At least one RUN2 RAW is not a decodable full-size sample; stopping before export.'
    }

    $exports = [System.Collections.Generic.List[object]]::new()
    foreach ($raw in $raws) {
        $exports.Add((Invoke-Export -Raw $raw -Destination (Join-Path $jpegDir "$($raw.BaseName)_baseline.jpg") -Format jpeg -LogName "$($raw.BaseName)_baseline.log"))
        $exports.Add((Invoke-Export -Raw $raw -Destination (Join-Path $tiffDir "$($raw.BaseName)_pass1.tif") -Format tiff -LogName "$($raw.BaseName)_tiff16.log"))
    }
    $exports | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $workDir 'core_export_results.json') -Encoding utf8
    $outputs = @($exports | ForEach-Object { $_.destination })
    & $Python $inspector validate @outputs --output (Join-Path $workDir 'core_output_validation.json') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Output validation failed: $LASTEXITCODE" }
    $exports | ConvertTo-Json -Depth 6
    exit ([int](@($exports | Where-Object { -not $_.pass }).Count -gt 0))
}

if ($Stage -eq 'Recipe') {
    if (-not $RecipeXmp) { $RecipeXmp = Join-Path $xmpDir '_DSC1627.ARW.xmp' }
    if (-not (Test-Path -LiteralPath $RecipeXmp -PathType Leaf)) { throw "Recipe XMP not found: $RecipeXmp" }
    $raw = $raws[0]
    $recipeRuns = @(
        (Invoke-Export -Raw $raw -Destination (Join-Path $xmpOutputDir "$($raw.BaseName)_xmp_edit.jpg") -Format jpeg -LogName "$($raw.BaseName)_xmp_edit.log" -Xmp $RecipeXmp),
        (Invoke-Export -Raw $raw -Destination (Join-Path $repeatDir "$($raw.BaseName)_repeat_a.jpg") -Format jpeg -LogName "$($raw.BaseName)_repeat_a.log" -Xmp $RecipeXmp),
        (Invoke-Export -Raw $raw -Destination (Join-Path $repeatDir "$($raw.BaseName)_repeat_b.jpg") -Format jpeg -LogName "$($raw.BaseName)_repeat_b.log" -Xmp $RecipeXmp),
        (Invoke-Export -Raw $raw -Destination (Join-Path $pass2Dir "$($raw.BaseName)_pass2_from_original.jpg") -Format jpeg -LogName "$($raw.BaseName)_pass2.log" -Xmp $RecipeXmp)
    )
    $recipeRuns | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $workDir 'recipe_export_results.json') -Encoding utf8
    & $Python $inspector validate @($recipeRuns.destination) --output (Join-Path $workDir 'recipe_output_validation.json') | Out-Null
    & $Python $inspector compare (Join-Path $jpegDir "$($raw.BaseName)_baseline.jpg") (Join-Path $xmpOutputDir "$($raw.BaseName)_xmp_edit.jpg") --output (Join-Path $workDir 'baseline_vs_recipe.json') | Out-Null
    & $Python $inspector compare (Join-Path $repeatDir "$($raw.BaseName)_repeat_a.jpg") (Join-Path $repeatDir "$($raw.BaseName)_repeat_b.jpg") --output (Join-Path $workDir 'repeatability_comparison.json') | Out-Null
    $recipeRuns | ConvertTo-Json -Depth 6
    exit ([int](@($recipeRuns | Where-Object { -not $_.pass }).Count -gt 0))
}

$initialPath = Join-Path $workDir 'initial_hashes.json'
if (-not (Test-Path -LiteralPath $initialPath -PathType Leaf)) { throw "Initial hashes missing: $initialPath" }
$initialDocument = Get-Content -Raw -LiteralPath $initialPath | ConvertFrom-Json
$initialByName = @{}
foreach ($entry in $initialDocument) {
    $initialByName[[string]$entry.name] = $entry
}
$sourceFiles = @($raws + $jpgs | Sort-Object Name)
$final = @($sourceFiles | ForEach-Object { Get-HashRecord $_ })
$final | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $workDir 'final_hashes.json') -Encoding utf8
$comparison = foreach ($file in $sourceFiles) {
    $beforeRecord = $initialByName[$file.Name]
    $afterMatches = @($final | Where-Object { $_.name -eq $file.Name })
    $afterRecord = if ($afterMatches.Count -eq 1) { $afterMatches.Item(0) } else { $null }
    $beforeHash = if ($null -ne $beforeRecord) { [string]$beforeRecord.sha256 } else { $null }
    $afterHash = if ($null -ne $afterRecord) { [string]$afterRecord.sha256 } else { $null }
    $beforeSize = if ($null -ne $beforeRecord) { [string]$beforeRecord.size_bytes } else { $null }
    $afterSize = if ($null -ne $afterRecord) { [string]$afterRecord.size_bytes } else { $null }
    [pscustomobject]@{
        name = $file.Name; extension = $file.Extension
        initial_sha256 = $beforeHash; final_sha256 = $afterHash
        intact = ($null -ne $beforeRecord -and $null -ne $afterRecord -and $beforeHash -eq $afterHash -and $beforeSize -eq $afterSize)
    }
}
$comparison | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $workDir 'hash_comparison.json') -Encoding utf8
$comparison | ConvertTo-Json -Depth 5
exit ([int](@($comparison | Where-Object { -not $_.intact }).Count -gt 0))
