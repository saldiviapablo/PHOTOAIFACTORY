$ErrorActionPreference = "Stop"

function Resolve-PafAiPython {
    param([string]$ProjectRoot)

    if ($env:PAF_AI_PYTHON -and (Test-Path -LiteralPath $env:PAF_AI_PYTHON)) {
        return (Resolve-Path -LiteralPath $env:PAF_AI_PYTHON).Path
    }

    $candidates = New-Object System.Collections.Generic.List[string]

    if ($ProjectRoot) {
        $lock = Join-Path $ProjectRoot "config\components.lock.local.json"
        if (Test-Path -LiteralPath $lock) {
            try {
                $doc = Get-Content -LiteralPath $lock -Raw | ConvertFrom-Json
                foreach ($c in @($doc.components)) {
                    $id = [string]$c.id
                    $lp = [string]$c.local_path
                    if (-not $lp) { continue }
                    if ($id -match '(?i)python|ai-worker|runtime') {
                        if (Test-Path -LiteralPath $lp -PathType Leaf) {
                            $candidates.Add($lp)
                        } elseif (Test-Path -LiteralPath $lp -PathType Container) {
                            $candidates.Add((Join-Path $lp "python.exe"))
                            $candidates.Add((Join-Path $lp "Scripts\python.exe"))
                        }
                    }
                }
            } catch {
                Write-Warning "No pude leer components.lock.local.json: $($_.Exception.Message)"
            }
        }
    }

    $local = Join-Path $env:LOCALAPPDATA "PhotoAIFactory\runtimes\ai-worker"
    $candidates.Add((Join-Path $local "python.exe"))
    $candidates.Add((Join-Path $local "Scripts\python.exe"))

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw @"
No encontré el Python aislado de PHOTO AI FACTORY.

Codex reportó que fue instalado bajo:
%LOCALAPPDATA%\PhotoAIFactory\runtimes\ai-worker\

Si la ruta real es distinta, define:
PAF_AI_PYTHON=C:\ruta\real\python.exe

No usaré silenciosamente el Python global.
"@
}
