[CmdletBinding()]
param(
    [string]$TestRoot = 'C:\Users\Pc\Documents\PHOTO AI FACTORY TESTS\DT-01',
    [string]$DarktableCli = 'C:\Program Files\darktable\bin\darktable-cli.exe',
    [string]$Python = 'C:\Users\Pc\AppData\Local\PhotoAIFactory\runtimes\ai-worker\Scripts\python.exe'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$inputDir = Join-Path $TestRoot 'INPUT'
$outputDir = Join-Path $TestRoot 'OUTPUT'
$workDir = Join-Path $TestRoot 'WORK'
$reportDir = Join-Path $TestRoot 'REPORT'
$jpegDir = Join-Path $outputDir '01_BASELINE_JPEG'
$tiffDir = Join-Path $outputDir '02_PASS1_TIFF'
$pass2Dir = Join-Path $outputDir '03_PASS2'
$xmpDir = Join-Path $workDir 'XMP'
$logDir = Join-Path $workDir 'LOGS'
$configDir = Join-Path $workDir 'DARKTABLE_CONFIG'
$cacheDir = Join-Path $workDir 'DARKTABLE_CACHE'

foreach ($directory in @($outputDir, $workDir, $reportDir, $jpegDir, $tiffDir, $pass2Dir, $xmpDir, $logDir, $configDir, $cacheDir)) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

if (-not (Test-Path -LiteralPath $DarktableCli -PathType Leaf)) { throw "darktable-cli not found: $DarktableCli" }
if (-not (Test-Path -LiteralPath $Python -PathType Leaf)) { throw "Isolated Python not found: $Python" }

$raws = @(Get-ChildItem -LiteralPath $inputDir -File | Where-Object { $_.Extension -ieq '.ARW' } | Sort-Object Name)
if ($raws.Count -eq 0) { throw "No ARW files found in $inputDir" }

function Get-HashRecord([System.IO.FileInfo]$File) {
    $hash = Get-FileHash -LiteralPath $File.FullName -Algorithm SHA256
    [pscustomobject]@{
        name = $File.Name
        size_bytes = $File.Length
        last_write_utc = $File.LastWriteTimeUtc.ToString('o')
        sha256 = $hash.Hash.ToLowerInvariant()
    }
}

$initialHashes = @($raws | ForEach-Object { Get-HashRecord $_ })
$initialHashes | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $workDir 'initial_hashes.json') -Encoding utf8

$inspector = Join-Path $PSScriptRoot 'inspect_media.py'
$inventoryPath = Join-Path $workDir 'inventory.json'
& $Python $inspector inventory @($raws.FullName) --output $inventoryPath | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Inventory inspection failed with exit code $LASTEXITCODE" }

function Convert-ToDarktablePath([string]$Path) {
    return $Path.Replace('\', '/')
}

function Invoke-DarktableExport {
    param(
        [System.IO.FileInfo]$Raw,
        [string]$Destination,
        [ValidateSet('jpeg', 'tiff')][string]$Format,
        [string]$LogName
    )

    if (Test-Path -LiteralPath $Destination) {
        throw "Refusing to overwrite an existing DT-01 output: $Destination"
    }

    $arguments = @(
        (Convert-ToDarktablePath $Raw.FullName),
        (Convert-ToDarktablePath $Destination),
        '--width', '0',
        '--height', '0',
        '--hq', 'true',
        '--apply-custom-presets', 'false',
        '--verbose',
        '--core',
        '--configdir', (Convert-ToDarktablePath $configDir),
        '--cachedir', (Convert-ToDarktablePath $cacheDir),
        '--library', ':memory:'
    )

    if ($Format -eq 'jpeg') {
        $arguments += @('--conf', 'plugins/imageio/format/jpeg/quality=95')
    }
    else {
        # Official darktable-cli export configuration keys for RGB 16-bit TIFF.
        $arguments += @(
            '--conf', 'plugins/imageio/format/tiff/bpp=16',
            '--conf', 'plugins/imageio/format/tiff/compress=1',
            '--conf', 'plugins/imageio/format/tiff/compresslevel=6',
            '--conf', 'plugins/imageio/format/tiff/shortfile=0'
        )
    }

    $logPath = Join-Path $logDir $LogName
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    # Windows PowerShell 5.1 wraps native stderr as NativeCommandError when
    # ErrorActionPreference=Stop. Darktable writes notices/errors to stderr, so
    # relax only around the child process and still trust its numeric exit code.
    $savedErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $DarktableCli @arguments 2>&1 | Set-Content -LiteralPath $logPath -Encoding utf8
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $savedErrorActionPreference
    }
    $stopwatch.Stop()

    $created = Test-Path -LiteralPath $Destination -PathType Leaf
    $size = if ($created) { (Get-Item -LiteralPath $Destination).Length } else { 0 }
    [pscustomobject]@{
        input = $Raw.Name
        format = $Format
        destination = $Destination
        exit_code = $exitCode
        duration_ms = $stopwatch.ElapsedMilliseconds
        created = $created
        size_bytes = $size
        log = $logPath
        pass = ($exitCode -eq 0 -and $created -and $size -gt 0)
    }
}

$exports = [System.Collections.Generic.List[object]]::new()
foreach ($raw in $raws) {
    $stem = [System.IO.Path]::GetFileNameWithoutExtension($raw.Name)
    $exports.Add((Invoke-DarktableExport -Raw $raw -Destination (Join-Path $jpegDir "${stem}_baseline.jpg") -Format jpeg -LogName "${stem}_baseline.log"))
    $exports.Add((Invoke-DarktableExport -Raw $raw -Destination (Join-Path $tiffDir "${stem}_pass1.tif") -Format tiff -LogName "${stem}_tiff16.log"))
}
$exports | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $workDir 'export_results.json') -Encoding utf8

$finalFiles = @(Get-ChildItem -LiteralPath $inputDir -File | Where-Object { $_.Extension -ieq '.ARW' } | Sort-Object Name)
$finalHashes = @($finalFiles | ForEach-Object { Get-HashRecord $_ })
$finalHashes | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $workDir 'final_hashes.json') -Encoding utf8

$hashComparison = foreach ($initial in $initialHashes) {
    $final = $finalHashes | Where-Object { $_.name -eq $initial.name }
    [pscustomobject]@{
        name = $initial.name
        initial_sha256 = $initial.sha256
        final_sha256 = $final.sha256
        intact = ($null -ne $final -and $initial.sha256 -eq $final.sha256 -and $initial.size_bytes -eq $final.size_bytes)
    }
}
$hashComparison | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $workDir 'hash_comparison.json') -Encoding utf8

$summary = [pscustomobject]@{
    raw_count = $raws.Count
    jpeg_pass = @($exports | Where-Object { $_.format -eq 'jpeg' -and $_.pass }).Count
    tiff_pass = @($exports | Where-Object { $_.format -eq 'tiff' -and $_.pass }).Count
    originals_intact = (@($hashComparison | Where-Object { -not $_.intact }).Count -eq 0)
}
$summary | ConvertTo-Json -Depth 5

if ($summary.jpeg_pass -ne $raws.Count -or $summary.tiff_pass -ne $raws.Count -or -not $summary.originals_intact) {
    exit 1
}
