#!/usr/bin/env bash
# Creates the dedicated virtual environment for the ONNX training tooling and
# installs every dependency in the correct order.
#
# torch is installed first from the CPU-only wheel index
# (https://download.pytorch.org/whl/cpu); afterwards `pip install -r
# requirements.txt` sees `torch>=2.2` as already satisfied and installs only the
# remaining packages (onnx / onnxruntime / tensorflow / tf2onnx / lightgbm /
# skl2onnx / onnxmltools / scikit-learn).
#
# Idempotent: re-running against an existing venv only upgrades/re-checks
# packages. Pass --recreate for a clean rebuild.
#
# Usage:
#   ./setup_env.sh [--python python3.12] [--venv PATH] [--recreate] [--skip-verify]

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REQUIREMENTS="${SCRIPT_DIR}/requirements.txt"

PYTHON="python3"
VENV_PATH="$(cd "${SCRIPT_DIR}/.." && pwd)/.venv"
TORCH_INDEX_URL="https://download.pytorch.org/whl/cpu"
RECREATE=0
SKIP_VERIFY=0

while [ $# -gt 0 ]; do
    case "$1" in
        --python)       PYTHON="$2"; shift 2 ;;
        --venv)         VENV_PATH="$2"; shift 2 ;;
        --torch-index)  TORCH_INDEX_URL="$2"; shift 2 ;;
        --recreate)     RECREATE=1; shift ;;
        --skip-verify)  SKIP_VERIFY=1; shift ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
done

step() { printf '\n=== %s ===\n' "$1"; }
run()  { printf '> %s\n' "$*"; "$@"; }

[ -f "$REQUIREMENTS" ] || { echo "requirements.txt not found: $REQUIREMENTS" >&2; exit 1; }

step "Base interpreter"
command -v "$PYTHON" >/dev/null 2>&1 || { echo "'$PYTHON' not found; install Python 3.10+ or pass --python." >&2; exit 1; }
run "$PYTHON" --version

step "Virtual environment: $VENV_PATH"
if [ "$RECREATE" -eq 1 ] && [ -d "$VENV_PATH" ]; then
    echo "Removing existing venv (--recreate)..."
    rm -rf "$VENV_PATH"
fi
if [ ! -d "$VENV_PATH" ]; then
    run "$PYTHON" -m venv "$VENV_PATH"
else
    echo "Reusing existing venv."
fi

VENV_PY="${VENV_PATH}/bin/python"
[ -x "$VENV_PY" ] || VENV_PY="${VENV_PATH}/Scripts/python.exe"   # Git Bash on Windows
[ -x "$VENV_PY" ] || { echo "venv python not found under $VENV_PATH" >&2; exit 1; }

step "Upgrade pip / setuptools / wheel"
run "$VENV_PY" -m pip install --upgrade pip setuptools wheel

step "torch (CPU wheel index: $TORCH_INDEX_URL)"
run "$VENV_PY" -m pip install --index-url "$TORCH_INDEX_URL" "torch>=2.2"

step "Remaining dependencies (requirements.txt)"
run "$VENV_PY" -m pip install -r "$REQUIREMENTS"

if [ "$SKIP_VERIFY" -eq 1 ]; then
    step "Verification skipped (--skip-verify)"
else
    step "Verification"
    run "$VENV_PY" -m pip check
    run "$VENV_PY" -c "import torch, onnx, onnxruntime, tensorflow, tf2onnx, lightgbm, sklearn, skl2onnx, onnxmltools; \
print('torch', torch.__version__); print('onnx', onnx.__version__); \
print('onnxruntime', onnxruntime.__version__); print('tensorflow', tensorflow.__version__); \
print('lightgbm', lightgbm.__version__)"
    ( cd "$SCRIPT_DIR" && run "$VENV_PY" dataset.py --selfcheck )
fi

step "Done"
echo "Activate with: source ${VENV_PATH}/bin/activate"
echo "Or call the interpreter directly: $VENV_PY"
