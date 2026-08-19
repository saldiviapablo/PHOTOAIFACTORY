[CmdletBinding()]
param(
    [ValidateRange(1024, 65535)][int]$ComfyPort = 8188
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_bootstrap-common.ps1')

$paths = Get-PafPaths
Initialize-PafDirectories -Paths $paths
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$timestamp = [DateTimeOffset]::Now
$reportPath = Join-Path $paths.Logs ("environment-verification-{0}.json" -f $timestamp.ToString('yyyyMMdd-HHmmss'))
$lockPath = Join-Path $repoRoot 'config\components.lock.local.json'
$errors = [System.Collections.Generic.List[string]]::new()
$checks = [ordered]@{}

function Test-KnownFile {
    param([string]$Path, [string]$ExpectedSha256)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    return (Get-PafSha256 -Path $Path) -eq $ExpectedSha256
}

function New-LockEntry {
    param(
        [string]$Id,
        [string]$Version,
        [string]$Source,
        [string]$LocalPath,
        [AllowNull()][string]$Sha256,
        [string]$License,
        [bool]$Installed,
        [string]$Status,
        [string]$Notes,
        [object[]]$Artifacts = @()
    )
    [ordered]@{
        id = $Id
        version = $Version
        source = $Source
        local_path = $LocalPath
        sha256 = $Sha256
        license = $License
        installed = $Installed
        status = $Status
        notes = $Notes
        artifacts = $Artifacts
    }
}

# GPU
try {
    $nvidia = Get-Command 'nvidia-smi.exe' -ErrorAction Stop
    $gpuCsv = (& $nvidia.Source '--query-gpu=name,driver_version,memory.total,memory.free,compute_cap' '--format=csv,noheader,nounits').Trim()
    if ($LASTEXITCODE -ne 0) { throw 'nvidia-smi returned a non-zero exit code.' }
    $gpuParts = $gpuCsv -split ',\s*'
    $checks.gpu = [ordered]@{
        status = if ($gpuParts[0] -match 'RTX 4060 Ti') { 'PASS' } else { 'FAIL' }
        name = $gpuParts[0]
        driver = $gpuParts[1]
        vram_total_mib = [int]$gpuParts[2]
        vram_free_mib = [int]$gpuParts[3]
        compute_capability = $gpuParts[4]
    }
    if ($checks.gpu.status -ne 'PASS') { $errors.Add("Unexpected GPU: $($gpuParts[0])") }
} catch {
    $checks.gpu = [ordered]@{ status = 'FAIL'; error = $_.Exception.Message }
    $errors.Add("GPU verification failed: $($_.Exception.Message)")
}

# Darktable
try {
    $checks.darktable = & (Join-Path $PSScriptRoot 'check-darktable.ps1')
} catch {
    $checks.darktable = [ordered]@{ status = 'FAIL'; error = $_.Exception.Message }
    $errors.Add("Darktable verification failed: $($_.Exception.Message)")
}

# Python / CUDA / ONNX / OpenCV
$aiPython = Join-Path $paths.Runtimes 'ai-worker\Scripts\python.exe'
try {
    if (-not (Test-Path -LiteralPath $aiPython)) { throw "Missing AI runtime: $aiPython" }
    $env:PAF_QWEN_MODEL = Join-Path $paths.Models 'qwen3-vl-2b-instruct-fp8\model-00001-of-00001.safetensors'
    $env:PAF_QWEN_DIR = Join-Path $paths.Models 'qwen3-vl-2b-instruct-fp8'
    $env:PAF_RF_MODEL = Join-Path $paths.Models 'rf-detr-medium\model.safetensors'
    $env:PAF_FLORENCE_MODEL = Join-Path $paths.Models 'florence-2-large\model.safetensors'
    $env:PAF_FACE_MODEL = Join-Path $paths.Models 'mediapipe-face-landmarker\face_landmarker.task'
    $env:PAF_POSE_MODEL = Join-Path $paths.Models 'mediapipe-pose-landmarker-full\pose_landmarker_full.task'
    $probe = @'
import importlib.metadata as m
import json
import os
import cv2
import mediapipe
import onnxruntime as ort
import torch
import torchvision
from mediapipe.tasks import python as mp_python
from mediapipe.tasks.python import vision as mp_vision
from safetensors import safe_open
from transformers import AutoConfig

def inspect_safetensors(path):
    with safe_open(path, framework="pt", device="cpu") as handle:
        keys = list(handle.keys())
        dtype_counts = {}
        for key in keys:
            dtype = str(handle.get_slice(key).get_dtype())
            dtype_counts[dtype] = dtype_counts.get(dtype, 0) + 1
        return {"tensor_count": len(keys), "dtype_counts": dtype_counts}

face = mp_vision.FaceLandmarker.create_from_options(
    mp_vision.FaceLandmarkerOptions(base_options=mp_python.BaseOptions(model_asset_path=os.environ["PAF_FACE_MODEL"]))
)
face.close()
pose = mp_vision.PoseLandmarker.create_from_options(
    mp_vision.PoseLandmarkerOptions(base_options=mp_python.BaseOptions(model_asset_path=os.environ["PAF_POSE_MODEL"]))
)
pose.close()

fp8_cuda_storage = False
if torch.cuda.is_available() and hasattr(torch, "float8_e4m3fn"):
    probe_tensor = torch.tensor([1.0], dtype=torch.float8_e4m3fn, device="cuda")
    fp8_cuda_storage = probe_tensor.device.type == "cuda"
    del probe_tensor

qwen_config = AutoConfig.from_pretrained(os.environ["PAF_QWEN_DIR"], local_files_only=True)
data = {
    "python": __import__("sys").version.split()[0],
    "torch": torch.__version__,
    "torchvision": torchvision.__version__,
    "cuda_available": torch.cuda.is_available(),
    "cuda_runtime": torch.version.cuda,
    "cuda_device": torch.cuda.get_device_name(0) if torch.cuda.is_available() else None,
    "cuda_capability": list(torch.cuda.get_device_capability(0)) if torch.cuda.is_available() else None,
    "onnxruntime": ort.__version__,
    "onnx_providers": ort.get_available_providers(),
    "transformers": m.version("transformers"),
    "rfdetr": m.version("rfdetr"),
    "mediapipe": m.version("mediapipe"),
    "opencv": cv2.__version__,
    "fp8_cuda_storage": fp8_cuda_storage,
    "qwen_model_type": qwen_config.model_type,
    "qwen_safetensors": inspect_safetensors(os.environ["PAF_QWEN_MODEL"]),
    "florence_safetensors": inspect_safetensors(os.environ["PAF_FLORENCE_MODEL"]),
    "rf_detr_safetensors": inspect_safetensors(os.environ["PAF_RF_MODEL"]),
    "mediapipe_bundles_loadable": True,
}
print(json.dumps(data))
'@
    $pythonResult = (& $aiPython '-c' $probe 2>&1)
    if ($LASTEXITCODE -ne 0) { throw ($pythonResult -join "`n") }
    $pythonJsonLine = $pythonResult | Where-Object { $_ -match '^\{' } | Select-Object -Last 1
    $checks.python_ai = $pythonJsonLine | ConvertFrom-Json
    $checks.python_ai | Add-Member -NotePropertyName status -NotePropertyValue $(
        if ($checks.python_ai.cuda_available -and $checks.python_ai.fp8_cuda_storage -and
            $checks.python_ai.cuda_device -match 'RTX 4060 Ti') { 'PASS' } else { 'FAIL' }
    )
    if ($checks.python_ai.status -ne 'PASS') { $errors.Add('PyTorch CUDA verification failed.') }
} catch {
    $checks.python_ai = [ordered]@{ status = 'FAIL'; error = $_.Exception.Message }
    $errors.Add("Python AI verification failed: $($_.Exception.Message)")
}

# ComfyUI portable runtime and controlled loopback start/stop.
$comfyRoot = Join-Path $paths.Components 'comfyui'
$comfyPython = Join-Path $comfyRoot 'python_embeded\python.exe'
try {
    if (-not (Test-Path -LiteralPath $comfyPython)) { throw "Missing ComfyUI Python: $comfyPython" }
    $versionProbe = 'import json,sys,torch; print(json.dumps({"python":sys.version.split()[0],"torch":torch.__version__,"cuda":torch.version.cuda,"cuda_available":torch.cuda.is_available()}))'
    $comfyRuntime = (& $comfyPython '-s' '-c' $versionProbe | Select-Object -Last 1) | ConvertFrom-Json
    $start = & (Join-Path $PSScriptRoot 'start-comfyui.ps1') -Port $ComfyPort -WaitSeconds 180
    $stop = & (Join-Path $PSScriptRoot 'stop-comfyui.ps1')
    $checks.comfyui = [ordered]@{
        status = if ($start.status -in @('STARTED', 'ALREADY_RUNNING') -and $stop.status -eq 'STOPPED') { 'PASS' } else { 'FAIL' }
        tag = 'v0.33.1'
        commit = '72865f4f27eaf5396f8f36370e0a2be3a9a090ee'
        listen = '127.0.0.1'
        port = $ComfyPort
        runtime = $comfyRuntime
        start_status = $start.status
        stop_status = $stop.status
    }
    if ($checks.comfyui.status -ne 'PASS') { $errors.Add('ComfyUI start/health/stop verification failed.') }
} catch {
    $checks.comfyui = [ordered]@{ status = 'FAIL'; error = $_.Exception.Message }
    $errors.Add("ComfyUI verification failed: $($_.Exception.Message)")
    try { & (Join-Path $PSScriptRoot 'stop-comfyui.ps1') | Out-Null } catch { }
}

$modelSpecs = @(
    [ordered]@{ Id='darktable-ai-rawdenoise-nind'; Version='1.0 / release-5.6.0'; Rel='darktable-ai\rawdenoise-nind\rawdenoise-nind.dtmodel'; Sha='d71b5f1e727c85a359e6f74dca9e2016c9d8fc3e2f7ac3e9b347d80ceca969af'; Source='https://github.com/darktable-org/darktable-ai/releases/tag/release-5.6.0'; License='GPL-3.0 (embedded model card)' },
    [ordered]@{ Id='darktable-ai-denoise-nind'; Version='1.0 / release-5.6.0'; Rel='darktable-ai\denoise-nind\denoise-nind.dtmodel'; Sha='825b3657cbb5193a67432a2f0b44ab86531cc7337f89e8e6c17d93db9665708a'; Source='https://github.com/darktable-org/darktable-ai/releases/tag/release-5.6.0'; License='GPL-3.0 (embedded model card)' },
    [ordered]@{ Id='darktable-ai-denoise-nafnet'; Version='1.0 / release-5.6.0'; Rel='darktable-ai\denoise-nafnet\denoise-nafnet.dtmodel'; Sha='4b5c0b9c2650956eaab9a3fc6ffe3e11d4dc4f55b246b7f3a0d2d72161b6980c'; Source='https://github.com/darktable-org/darktable-ai/releases/tag/release-5.6.0'; License='MIT (embedded model card)' },
    [ordered]@{ Id='darktable-ai-upscale-realplksr'; Version='1.0 / release-5.6.0'; Rel='darktable-ai\upscale-realplksr\upscale-realplksr.dtmodel'; Sha='7f7b4942b2363691628f10c52bce66ff3ac67d07d4d55c3804d3f89fc9cd6ba7'; Source='https://github.com/darktable-org/darktable-ai/releases/tag/release-5.6.0'; License='MIT (embedded model card; training-data notice retained)' },
    [ordered]@{ Id='rf-detr-medium'; Version='1b5b672408f86dd38e05dd3cf3f2e0834e545a59'; Rel='rf-detr-medium\model.safetensors'; Sha='e52098adc46969794fbdd16e0548a62b81ba0c0f4b14392676edba50be9a69f6'; Source='https://huggingface.co/Roboflow/rf-detr-medium'; License='Apache-2.0' },
    [ordered]@{ Id='mediapipe-face-landmarker'; Version='float16 bundle pinned by SHA-256'; Rel='mediapipe-face-landmarker\face_landmarker.task'; Sha='64184e229b263107bc2b804c6625db1341ff2bb731874b0bcc2fe6544e0bc9ff'; Source='https://ai.google.dev/edge/mediapipe/solutions/vision/face_landmarker'; License='Apache-2.0 (official model card)' },
    [ordered]@{ Id='mediapipe-pose-landmarker-full'; Version='float16 full bundle pinned by SHA-256'; Rel='mediapipe-pose-landmarker-full\pose_landmarker_full.task'; Sha='4eaa5eb7a98365221087693fcc286334cf0858e2eb6e15b506aa4a7ecdcec4ad'; Source='https://ai.google.dev/edge/mediapipe/solutions/vision/pose_landmarker'; License='Apache-2.0 (official model card)' },
    [ordered]@{ Id='florence-2-large'; Version='21a599d414c4d928c9032694c424fb94458e3594'; Rel='florence-2-large\model.safetensors'; Sha='4f38ce741c6b71188fe2b3419a55e11917a8a7b321ae2e63c61da0191b0ebad7'; Source='https://huggingface.co/microsoft/Florence-2-large'; License='MIT' },
    [ordered]@{ Id='qwen3-vl-2b-instruct-fp8'; Version='46485250d8854c0a9be4f1adbc67ca47e5bb6fa5'; Rel='qwen3-vl-2b-instruct-fp8\model-00001-of-00001.safetensors'; Sha='da14428e061d80e3aa575bbcf911f48fda9be492b5a3e8be6a15e9d54313e57c'; Source='https://huggingface.co/Qwen/Qwen3-VL-2B-Instruct-FP8'; License='Apache-2.0' },
    [ordered]@{ Id='dinov2-vits14-standard'; Version='LVD-142M standard ViT-S/14 pinned by SHA-256'; Rel='dinov2-vits14-standard\dinov2_vits14_pretrain.pth'; Sha='b938bf1bc15cd2ec0feacfe3a1bb553fe8ea9ca46a7e1d8d00217f29aef60cd9'; Source='https://github.com/facebookresearch/dinov2'; License='Apache-2.0' }
)

$modelResults = [System.Collections.Generic.List[object]]::new()
$lockEntries = [System.Collections.Generic.List[object]]::new()
foreach ($spec in $modelSpecs) {
    $modelPath = Join-Path $paths.Models $spec.Rel
    $valid = Test-KnownFile -Path $modelPath -ExpectedSha256 $spec.Sha
    $fileInfo = if (Test-Path -LiteralPath $modelPath -PathType Leaf) { Get-Item -LiteralPath $modelPath } else { $null }
    if ($valid -and $spec.Id -like 'darktable-ai-*') {
        $discoveryPath = Join-Path $env:APPDATA "darktable\models\$($fileInfo.Name)"
        $valid = Test-KnownFile -Path $discoveryPath -ExpectedSha256 $spec.Sha
    }
    $artifacts = if ($fileInfo) {
        @([ordered]@{
            file = $fileInfo.Name
            size_bytes = $fileInfo.Length
            sha256 = $spec.Sha
            downloaded_at = $fileInfo.LastWriteTimeUtc.ToString('o')
        })
    } else { @() }
    $result = [ordered]@{ id=$spec.Id; status=if($valid){'PASS'}else{'FAIL'}; path=$modelPath; sha256=$spec.Sha }
    $modelResults.Add($result)
    if (-not $valid) { $errors.Add("Model missing or corrupt: $($spec.Id)") }
    $modelNotes = if ($spec.Id -like 'darktable-ai-*') {
        "Official artifact; package SHA-256 verified. Darktable discovery hardlink: $env:APPDATA\darktable\models\$($fileInfo.Name)"
    } else {
        'Official artifact; primary weight/package SHA-256 verified.'
    }
    $lockEntries.Add((New-LockEntry -Id $spec.Id -Version $spec.Version -Source $spec.Source `
        -LocalPath $modelPath -Sha256 $spec.Sha -License $spec.License -Installed $valid `
        -Status $(if($valid){'BASELINE_INSTALLED'}else{'MISSING_OR_CORRUPT'}) `
        -Notes $modelNotes -Artifacts $artifacts))
}
$checks.models = $modelResults

# Component lock entries.
$darktableInstaller = Join-Path $paths.Cache 'downloads\darktable-5.6.0-win64.exe'
$darktableInstalled = $checks.darktable.status -eq 'PASS'
$darktableLocalPath = if ($darktableInstalled) { Split-Path -Parent (Split-Path -Parent $checks.darktable.executable) } else { Join-Path $paths.Components 'darktable-5.6.0' }
$darktableArtifacts = @()
if (Test-Path -LiteralPath $darktableInstaller) {
    $installerInfo = Get-Item -LiteralPath $darktableInstaller
    $darktableArtifacts += [ordered]@{ file=$installerInfo.Name; size_bytes=$installerInfo.Length; sha256='b42989195dfff44540c0b767b407987329ca99853612304cbbf14c48d1d3f803'; downloaded_at=$installerInfo.LastWriteTimeUtc.ToString('o') }
}
if ($darktableInstalled) {
    $cliInfo = Get-Item -LiteralPath $checks.darktable.executable
    $darktableArtifacts += [ordered]@{ file=$cliInfo.FullName; size_bytes=$cliInfo.Length; sha256=(Get-PafSha256 -Path $cliInfo.FullName); downloaded_at=$cliInfo.LastWriteTimeUtc.ToString('o') }
}
$lockEntries.Insert(0, (New-LockEntry -Id 'darktable' -Version '5.6.0' `
    -Source 'https://github.com/darktable-org/darktable/releases/tag/release-5.6.0' `
    -LocalPath $darktableLocalPath `
    -Sha256 'b42989195dfff44540c0b767b407987329ca99853612304cbbf14c48d1d3f803' `
    -License 'GPL-3.0-or-later' -Installed $darktableInstalled `
    -Status $(if($darktableInstalled){'INSTALLED'}else{'FAILED'}) `
    -Notes "Official Windows x64 stable installer; installer cached at $darktableInstaller; release commit 3c17b29." `
    -Artifacts $darktableArtifacts))

$comfyInstalled = $checks.comfyui.status -eq 'PASS'
$comfyArchive = Join-Path $paths.Cache 'downloads\ComfyUI_windows_portable_nvidia-v0.33.1.7z'
$comfyArtifacts = @()
if (Test-Path -LiteralPath $comfyArchive) {
    $comfyArchiveInfo = Get-Item -LiteralPath $comfyArchive
    $comfyArtifacts += [ordered]@{ file=$comfyArchiveInfo.Name; size_bytes=$comfyArchiveInfo.Length; sha256='4a221588979b96b8244e0e50b2edca03af732acae1deba69d60aa3b4d60b9dba'; downloaded_at=$comfyArchiveInfo.LastWriteTimeUtc.ToString('o') }
}
$lockEntries.Insert(1, (New-LockEntry -Id 'comfyui' -Version 'v0.33.1 / 72865f4f27eaf5396f8f36370e0a2be3a9a090ee' `
    -Source 'https://github.com/Comfy-Org/ComfyUI/releases/tag/v0.33.1' `
    -LocalPath $comfyRoot -Sha256 '4a221588979b96b8244e0e50b2edca03af732acae1deba69d60aa3b4d60b9dba' `
    -License 'GPL-3.0' -Installed $comfyInstalled -Status $(if($comfyInstalled){'INSTALLED_VERIFIED'}else{'FAILED'}) `
    -Notes 'Official NVIDIA portable archive. Start command uses --listen 127.0.0.1 --disable-auto-launch; no custom nodes added.' `
    -Artifacts $comfyArtifacts))

$pythonInstalled = $checks.python_ai.status -eq 'PASS'
$requirementsLock = Join-Path $repoRoot 'config\requirements-ai-worker.lock.txt'
$lockSha = if (Test-Path -LiteralPath $requirementsLock) { Get-PafSha256 -Path $requirementsLock } else { $null }
$pythonArtifacts = @()
if (Test-Path -LiteralPath $requirementsLock) {
    $lockInfo = Get-Item -LiteralPath $requirementsLock
    $pythonArtifacts += [ordered]@{ file=$lockInfo.FullName; size_bytes=$lockInfo.Length; sha256=$lockSha; downloaded_at=$lockInfo.LastWriteTimeUtc.ToString('o') }
}
$lockEntries.Insert(2, (New-LockEntry -Id 'python-ai-worker' -Version $(if($pythonInstalled){$checks.python_ai.python}else{'3.12.12'}) `
    -Source 'https://github.com/astral-sh/python-build-standalone (installed by uv)' `
    -LocalPath (Join-Path $paths.Runtimes 'ai-worker') -Sha256 $lockSha -License 'PSF-2.0' `
    -Installed $pythonInstalled -Status $(if($pythonInstalled){'INSTALLED_VERIFIED'}else{'FAILED'}) `
    -Notes 'Isolated managed Python; SHA-256 refers to the resolved requirements lock.' -Artifacts $pythonArtifacts))

if ($pythonInstalled) {
    foreach ($package in @(
        @('pytorch', $checks.python_ai.torch, 'BSD-3-Clause'),
        @('torchvision', $checks.python_ai.torchvision, 'BSD-3-Clause'),
        @('onnxruntime-gpu', $checks.python_ai.onnxruntime, 'MIT'),
        @('transformers', $checks.python_ai.transformers, 'Apache-2.0'),
        @('opencv', $checks.python_ai.opencv, 'Apache-2.0')
    )) {
        $lockEntries.Add((New-LockEntry -Id $package[0] -Version $package[1] -Source 'Pinned Python package in requirements-ai-worker.lock.txt' `
            -LocalPath (Join-Path $paths.Runtimes 'ai-worker') -Sha256 $lockSha -License $package[2] `
            -Installed $true -Status 'INSTALLED_VERIFIED' -Notes 'Version verified by isolated runtime import.'))
    }
}

# Record the complete stable darktable-ai 5.6.0 release inventory. These five
# alternatives are official and available, but are intentionally not installed.
foreach ($available in @(
    @('darktable-ai-mask-object-sam21-base-plus', 'mask-object-sam21-base-plus.dtmodel', 'ee9a5d3c86cbc0ef4e4c550070c25af224a44ec80c8bade4419383b7aab890cb'),
    @('darktable-ai-mask-object-sam21-small', 'mask-object-sam21-small.dtmodel', '06a629ac1adf40daec47d2191cbdbdac609d93769584a05f65f4276bf784663f'),
    @('darktable-ai-mask-object-sam21-tiny', 'mask-object-sam21-tiny.dtmodel', '1e0887434f2c5a452ae5c37c543656d5ad3c478f35d0497baeb7f3e039e42c91'),
    @('darktable-ai-mask-object-segnext-b2hq', 'mask-object-segnext-b2hq.dtmodel', '87936f12f815e05c6f601be5e46d69ce2e52c788ae2c26ec7e3db39d33780857'),
    @('darktable-ai-upscale-bsrgan', 'upscale-bsrgan.dtmodel', 'e90621e12fea12098ad6abd701bf67f50cea6c633bad98a19a4ccebba94404a6')
)) {
    $plannedPath = Join-Path (Join-Path $paths.Models 'darktable-ai') $available[1]
    $lockEntries.Add((New-LockEntry -Id $available[0] -Version '1.0 / release-5.6.0' `
        -Source "https://github.com/darktable-org/darktable-ai/releases/download/release-5.6.0/$($available[1])" `
        -LocalPath $plannedPath -Sha256 $available[2] -License 'See embedded official model card; not accepted or downloaded' `
        -Installed $false -Status 'AVAILABLE_NOT_DOWNLOADED' `
        -Notes 'Present in the stable compatible release inventory; not required for the approved Phase 0 baseline download set.'))
}

# Explicitly record candidates that policy forbids us to download automatically.
foreach ($pending in @(
    @('image-adaptive-3dlut', 'ARTIFACT_REVIEW', 'Checkpoint artifact license/source is not explicit.'),
    @('bisenet-face-parsing', 'REVIEW_REQUIRED', 'Tier 2; exact official checkpoint and license not selected.'),
    @('retinexformer', 'OPTIONAL_NOT_DOWNLOADED', 'Tier 2; no approved PoC currently requires it.'),
    @('llf-lut', 'OPTIONAL_NOT_DOWNLOADED', 'Tier 2 benchmark alternative.'),
    @('rtmpose', 'OPTIONAL_NOT_DOWNLOADED', 'Tier 2 benchmark alternative.'),
    @('rf-detr-plus', 'REJECTED_NOT_DOWNLOADED', 'Plus XL/2XL expressly forbidden by baseline policy.'),
    @('dinov2-specialty-xray-cell', 'REJECTED_NOT_DOWNLOADED', 'Specialized non-standard variants expressly forbidden.')
)) {
    $lockEntries.Add((New-LockEntry -Id $pending[0] -Version 'not selected' -Source 'not downloaded' `
        -LocalPath '' -Sha256 $null -License 'REVIEW_REQUIRED' -Installed $false -Status $pending[1] -Notes $pending[2]))
}

$componentBytes = Get-PafDirectorySize -Path $paths.Components
$modelBytes = Get-PafDirectorySize -Path $paths.Models
$runtimeBytes = Get-PafDirectorySize -Path $paths.Runtimes
$cacheBytes = Get-PafDirectorySize -Path $paths.Cache
$drive = Get-PSDrive -Name ([System.IO.Path]::GetPathRoot($paths.Root).TrimEnd(':\'))
$checks.disk = [ordered]@{
    components_bytes = $componentBytes
    models_bytes = $modelBytes
    runtimes_bytes = $runtimeBytes
    cache_bytes = $cacheBytes
    total_bytes = $componentBytes + $modelBytes + $runtimeBytes + $cacheBytes
    free_bytes = [int64]$drive.Free
}

$ready = $errors.Count -eq 0
$gates = [ordered]@{
    'DT-01' = if ($darktableInstalled) { 'READY_TO_START_NOT_EXECUTED' } else { 'NOT_READY' }
    'IPC-01' = if ($pythonInstalled) { 'READY_TO_START_NOT_EXECUTED' } else { 'NOT_READY' }
    'CUI-01' = if ($comfyInstalled) { 'READY_TO_START_NOT_EXECUTED' } else { 'NOT_READY' }
    'GPU-01' = if ($checks.gpu.status -eq 'PASS' -and $pythonInstalled) { 'READY_TO_START_NOT_EXECUTED' } else { 'NOT_READY' }
}

$report = [ordered]@{
    schema_version = 1
    generated_at = $timestamp.ToString('o')
    external_root = $paths.Root
    ready = $ready
    gates = $gates
    checks = $checks
    errors = @($errors)
}
$lock = [ordered]@{
    schema_version = 1
    generated_at = $timestamp.ToString('o')
    host = [ordered]@{ os='Windows 11 x64'; gpu=if($checks.gpu.name){$checks.gpu.name}else{$null}; external_root=$paths.Root }
    components = @($lockEntries)
}

$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $reportPath -Encoding utf8
$lock | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $lockPath -Encoding utf8
$report | ConvertTo-Json -Depth 12

if (-not $ready) { exit 1 }
