<#
.SYNOPSIS
    Creates the dedicated virtual environment for the ONNX training tooling and
    installs every dependency in the correct order.

.DESCRIPTION
    torch must be installed first from the CPU-only wheel index
    (https://download.pytorch.org/whl/cpu); a plain `pip install torch` on
    Windows would otherwise try to pull a CUDA build. Once torch is present,
    `pip install -r requirements.txt` sees the `torch>=2.2` line as already
    satisfied and installs only the remaining packages (onnx / onnxruntime /
    tensorflow / tf2onnx / lightgbm / skl2onnx / onnxmltools / scikit-learn).

    Idempotent: re-running against an existing venv only upgrades/re-checks
    packages. Use -Recreate for a clean rebuild.

.PARAMETER Python
    Launcher used to create the venv. Default: "py" on Windows (with -PythonArgs
    "-3.13"), "python3.13" elsewhere. Override e.g. -Python "py" -PythonArgs "-3.14".

.PARAMETER PipMinVersion
    Minimum pip version enforced when bootstrapping the venv (security requirement).
    Default: 26.2.2. The bootstrap fails if a pip at or above this version cannot be installed.

.PARAMETER VenvPath
    Target venv directory. Default: StockAnalyzer.Python/.venv (one level up
    from this script).

.PARAMETER TorchIndexUrl
    Wheel index for the CPU-only torch build.

.PARAMETER Recreate
    Delete an existing venv before creating a fresh one.

.PARAMETER SkipVerify
    Skip the post-install verification (pip check + import smoke + dataset.py --selfcheck).

.EXAMPLE
    pwsh ./setup_env.ps1

.EXAMPLE
    pwsh ./setup_env.ps1 -Recreate -Python "py" -PythonArgs "-3.13"
#>
[CmdletBinding()]
param(
    [string]   $Python         = $(if ($PSVersionTable.Platform -eq "Unix") { "python3.13" } else { "py" }),
    [string[]] $PythonArgs     = $(if ($PSVersionTable.Platform -eq "Unix") { @() } else { @("-3.13") }),
    [string]   $PipMinVersion  = "26.2.2",
    [string]   $VenvPath       = (Join-Path (Split-Path $PSScriptRoot -Parent) ".venv"),
    [string]   $TorchIndexUrl  = "https://download.pytorch.org/whl/cpu",
    [switch]   $Recreate,
    [switch]   $SkipVerify
)

$ErrorActionPreference = "Stop"
$scriptDir = $PSScriptRoot
$requirements = Join-Path $scriptDir "requirements.txt"

function Write-Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function Invoke-Checked($file, $arguments) {
    Write-Host "> $file $($arguments -join ' ')" -ForegroundColor DarkGray
    & $file @arguments
    if ($LASTEXITCODE -ne 0) { throw "Command failed (exit $LASTEXITCODE): $file $($arguments -join ' ')" }
}

if (-not (Test-Path $requirements)) { throw "requirements.txt not found next to this script: $requirements" }

# --- 1. Locate the base interpreter --------------------------------------------
Write-Step "Base interpreter"
$baseArgs = @($PythonArgs + @("--version"))
try { Invoke-Checked $Python $baseArgs }
catch { throw "Cannot run '$Python'. Install Python 3.13 (3.13.17+) or pass -Python / -PythonArgs. ($_)" }

# --- 2. Create (or reuse) the venv -------------------------------------------
Write-Step "Virtual environment: $VenvPath"
if ($Recreate -and (Test-Path $VenvPath)) {
    Write-Host "Removing existing venv (-Recreate)..." -ForegroundColor Yellow
    Remove-Item -Recurse -Force $VenvPath
}
if (-not (Test-Path $VenvPath)) {
    Invoke-Checked $Python @($PythonArgs + @("-m", "venv", $VenvPath))
} else {
    Write-Host "Reusing existing venv." -ForegroundColor Green
}

$venvPy = Join-Path $VenvPath "Scripts/python.exe"          # Windows
if (-not (Test-Path $venvPy)) { $venvPy = Join-Path $VenvPath "bin/python" }  # POSIX (pwsh on Linux/macOS)
if (-not (Test-Path $venvPy)) { throw "venv python not found under $VenvPath" }

# --- 3. Bootstrap pip -------------------------------------------------------
Write-Step "Upgrade pip (>=$PipMinVersion) / setuptools / wheel / certifi"
Invoke-Checked $venvPy @("-m", "pip", "install", "--upgrade", "pip>=$PipMinVersion", "setuptools", "wheel", "certifi")

# --- 4. torch first, from the CPU wheel index -------------------------------
Write-Step "torch (CPU wheel index: $TorchIndexUrl)"
Invoke-Checked $venvPy @("-m", "pip", "install", "--index-url", $TorchIndexUrl, "torch>=2.2")

# --- 5. Everything else from requirements.txt (torch already satisfied) --------
Write-Step "Remaining dependencies (requirements.txt)"
Invoke-Checked $venvPy @("-m", "pip", "install", "-r", $requirements)

# --- 6. Verify -------------------------------------------------------------
if ($SkipVerify) {
    Write-Step "Verification skipped (-SkipVerify)"
} else {
    Write-Step "Verification"
    Invoke-Checked $venvPy @("-m", "pip", "check")

    $importSmoke = "import torch, onnx, onnxruntime, tensorflow, tf2onnx, lightgbm, sklearn, skl2onnx, onnxmltools, gymnasium, stable_baselines3; " +
                   "print('torch', torch.__version__); print('onnx', onnx.__version__); " +
                   "print('onnxruntime', onnxruntime.__version__); print('tensorflow', tensorflow.__version__); " +
                   "print('lightgbm', lightgbm.__version__); print('gymnasium', gymnasium.__version__); " +
                   "print('stable_baselines3', stable_baselines3.__version__)"
    Invoke-Checked $venvPy @("-c", $importSmoke)

    Write-Host "> dataset.py --selfcheck" -ForegroundColor DarkGray
    Push-Location $scriptDir
    try { Invoke-Checked $venvPy @("dataset.py", "--selfcheck") }
    finally { Pop-Location }
}

Write-Step "Done"
Write-Host "Activate with:" -ForegroundColor Green
Write-Host "  $VenvPath\Scripts\Activate.ps1   (PowerShell)"
Write-Host "  $VenvPath\Scripts\activate.bat   (cmd)"
Write-Host "Or call the interpreter directly: $venvPy"
