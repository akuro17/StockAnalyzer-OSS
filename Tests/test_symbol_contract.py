"""Python side of the symbol-contract parity: `symbol_validator.py` reads `symbol_contract.json`; the C# side is held to the same file by TickerSymbolContractParityTests.

The validator must work from a folder that holds only the two files (the shipped layout: the JSON is copied next to the scripts), so a missing copy is caught here.
"""

import json
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PYTHON_DIR = ROOT / "StockAnalyzer.Python"
sys.path.insert(0, str(PYTHON_DIR))
import symbol_validator  # noqa: E402


def _contract():
    return json.loads((PYTHON_DIR / "symbol_contract.json").read_text(encoding="utf-8"))


def test_the_validator_reads_the_bound_from_the_contract():
    assert symbol_validator.MAX_TICKER_LENGTH == _contract()["max_file_safety_length"] == 32


def test_every_probe_of_the_contract_gives_the_expected_answer():
    assert symbol_validator.check_contract(_contract()) == []


def test_every_single_character_between_two_letters_is_judged_like_the_contract():
    contract = _contract()
    invalid = set(contract["invalid_characters"])
    ranges = [(r["first"], r["last"]) for r in contract["control_code_points"]]
    assert invalid and ranges, "the sweep must not pass vacuously"
    mismatches = []
    for code in range(0x10000):
        char = chr(code)
        refused_by_the_file = char in invalid or any(first <= code <= last for first, last in ranges)
        if refused_by_the_file == symbol_validator.is_well_formed_ticker("A" + char + "B"):
            mismatches.append(code)
    assert mismatches == []


def test_every_not_trimmed_code_point_is_refused_at_the_edges():
    """C# Trim() keeps U+001C..U+001F (control characters that are not white space) while Python str.strip() would remove them and accept the symbol.

    The contract lists them as not_trimmed_code_points; the validator must not trim them, so the control-character rule refuses them at the start and the end.
    """
    ranges = _contract()["not_trimmed_code_points"]
    assert ranges, "the check must not pass vacuously"
    for rng in ranges:
        for code in range(rng["first"], rng["last"] + 1):
            char = chr(code)
            assert not symbol_validator.is_well_formed_ticker(char + "AAPL"), code
            assert not symbol_validator.is_well_formed_ticker("AAPL" + char), code


def test_a_non_string_is_never_well_formed():
    for value in (None, 7, b"AAPL", ["AAPL"]):
        assert not symbol_validator.is_well_formed_ticker(value), value


def test_the_validator_runs_from_a_folder_holding_only_the_shipped_files():
    with tempfile.TemporaryDirectory() as folder:
        for name in ("symbol_validator.py", "symbol_contract.json"):
            shutil.copy(PYTHON_DIR / name, Path(folder) / name)
        result = subprocess.run([sys.executable, "-I", str(Path(folder) / "symbol_validator.py"), "--selfcheck"],
                                capture_output=True, text=True, cwd=folder)
        assert result.returncode == 0, result.stderr
        assert "selfcheck: OK" in result.stdout


def test_without_the_contract_file_the_import_fails_loudly():
    with tempfile.TemporaryDirectory() as folder:
        shutil.copy(PYTHON_DIR / "symbol_validator.py", Path(folder) / "symbol_validator.py")
        result = subprocess.run([sys.executable, "-I", str(Path(folder) / "symbol_validator.py"), "--selfcheck"],
                                capture_output=True, text=True, cwd=folder)
        assert result.returncode != 0
        assert "symbol_contract.json" in result.stderr
