[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_bootstrap-common.ps1')

$paths = Get-PafPaths
Initialize-PafDirectories -Paths $paths
$logPath = Join-Path $paths.Logs ("download-models-{0}.log" -f ([DateTimeOffset]::Now.ToString('yyyyMMdd-HHmmss')))
$downloadRoot = Join-Path $paths.Cache 'downloads'

$directModels = @(
    [ordered]@{
        Id = 'mediapipe-face-landmarker'
        File = 'face_landmarker.task'
        Uri = 'https://storage.googleapis.com/mediapipe-models/face_landmarker/face_landmarker/float16/latest/face_landmarker.task'
        Sha256 = '64184e229b263107bc2b804c6625db1341ff2bb731874b0bcc2fe6544e0bc9ff'
    },
    [ordered]@{
        Id = 'mediapipe-pose-landmarker-full'
        File = 'pose_landmarker_full.task'
        Uri = 'https://storage.googleapis.com/mediapipe-models/pose_landmarker/pose_landmarker_full/float16/latest/pose_landmarker_full.task'
        Sha256 = '4eaa5eb7a98365221087693fcc286334cf0858e2eb6e15b506aa4a7ecdcec4ad'
    },
    [ordered]@{
        Id = 'dinov2-vits14-standard'
        File = 'dinov2_vits14_pretrain.pth'
        Uri = 'https://dl.fbaipublicfiles.com/dinov2/dinov2_vits14/dinov2_vits14_pretrain.pth'
        Sha256 = 'b938bf1bc15cd2ec0feacfe3a1bb553fe8ea9ca46a7e1d8d00217f29aef60cd9'
    }
)

foreach ($model in $directModels) {
    $targetDir = Join-Path $paths.Models $model.Id
    $destination = Join-Path $targetDir $model.File
    $auditCopy = Join-Path (Join-Path $paths.Cache 'bootstrap-audit') $model.File
    if (-not (Test-Path -LiteralPath $destination) -and (Test-Path -LiteralPath $auditCopy)) {
        if ((Get-PafSha256 -Path $auditCopy) -eq $model.Sha256) {
            New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
            Move-Item -LiteralPath $auditCopy -Destination $destination
        }
    }
    Invoke-PafDownload -Uri $model.Uri -Destination $destination -ExpectedSha256 $model.Sha256 `
        -AllowedRoot $paths.Models -LogPath $logPath | Out-Null
}

$darktableModels = @(
    [ordered]@{ Id = 'rawdenoise-nind'; File = 'rawdenoise-nind.dtmodel'; Sha256 = 'd71b5f1e727c85a359e6f74dca9e2016c9d8fc3e2f7ac3e9b347d80ceca969af' },
    [ordered]@{ Id = 'denoise-nind'; File = 'denoise-nind.dtmodel'; Sha256 = '825b3657cbb5193a67432a2f0b44ab86531cc7337f89e8e6c17d93db9665708a' },
    [ordered]@{ Id = 'denoise-nafnet'; File = 'denoise-nafnet.dtmodel'; Sha256 = '4b5c0b9c2650956eaab9a3fc6ffe3e11d4dc4f55b246b7f3a0d2d72161b6980c' },
    [ordered]@{ Id = 'upscale-realplksr'; File = 'upscale-realplksr.dtmodel'; Sha256 = '7f7b4942b2363691628f10c52bce66ff3ac67d07d4d55c3804d3f89fc9cd6ba7' }
)
$darktableRoot = Join-Path $paths.Models 'darktable-ai'
$releaseBase = 'https://github.com/darktable-org/darktable-ai/releases/download/release-5.6.0'
foreach ($model in $darktableModels) {
    $targetDir = Join-Path $darktableRoot $model.Id
    $destination = Join-Path $targetDir $model.File
    Invoke-PafDownload -Uri "$releaseBase/$($model.File)" -Destination $destination `
        -ExpectedSha256 $model.Sha256 -AllowedRoot $paths.Models -LogPath $logPath | Out-Null
}
Invoke-PafDownload -Uri "$releaseBase/versions.json" -Destination (Join-Path $darktableRoot 'versions.json') `
    -ExpectedSha256 'd8b54c81fb769e8770807462f09bd86b40f242c480db7878538b5bd7526e8d9c' `
    -AllowedRoot $paths.Models -LogPath $logPath | Out-Null

# Darktable 5.6 discovers .dtmodel packages in %APPDATA%\darktable\models. Hard
# links keep the bytes in the PhotoAIFactory model store while exposing only the
# approved packages at Darktable's documented discovery path.
$darktableDiscovery = Join-Path $env:APPDATA 'darktable\models'
New-Item -ItemType Directory -Force -Path $darktableDiscovery | Out-Null
foreach ($model in $darktableModels) {
    $source = Join-Path (Join-Path $darktableRoot $model.Id) $model.File
    $link = Join-Path $darktableDiscovery $model.File
    if (Test-Path -LiteralPath $link) {
        if ((Get-PafSha256 -Path $link) -ne $model.Sha256) {
            throw "Darktable discovery file exists with an unexpected checksum: $link"
        }
    } else {
        New-Item -ItemType HardLink -Path $link -Target $source | Out-Null
    }
}

$aiPython = Join-Path $paths.Runtimes 'ai-worker\Scripts\python.exe'
$hfHelper = Join-Path $PSScriptRoot '_download-hf-snapshots.py'
if (-not (Test-Path -LiteralPath $aiPython)) {
    throw "AI runtime is missing: $aiPython. Run bootstrap-phase0.ps1 first or create the runtime before downloading Hugging Face snapshots."
}

$env:HF_HOME = Join-Path $paths.Cache 'huggingface'
$env:HF_HUB_DISABLE_TELEMETRY = '1'
$env:HF_HUB_DISABLE_XET = '0'
& $aiPython $hfHelper '--models-root' $paths.Models
if ($LASTEXITCODE -ne 0) { throw "Hugging Face snapshot download failed with exit code $LASTEXITCODE" }

Write-PafLog -Message "All approved Phase 0 baseline models are present and checksummed." -LogPath $logPath

