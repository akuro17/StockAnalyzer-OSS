"""Ticker symbol validator matching StockAnalyzer.Core.Models.TickerSymbol SSoT.

The rules are read from ``symbol_contract.json`` (next to this file), the shared machine-readable definition. The C# side holds ``TickerSymbol`` to the
same file with parity tests, so a rule changed on one side only fails there. Run ``python symbol_validator.py --selfcheck`` to evaluate the probes of the file.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

CONTRACT_FILE: Path = Path(__file__).resolve().with_name("symbol_contract.json")


def _load_contract() -> dict:
    # A missing or unreadable contract must fail loudly at import: a validator with silently different rules would be worse than none.
    with CONTRACT_FILE.open(encoding="utf-8") as handle:
        return json.load(handle)


_CONTRACT = _load_contract()

MAX_TICKER_LENGTH: int = int(_CONTRACT["max_file_safety_length"])
_PARENT_SEGMENT: str = str(_CONTRACT["parent_segment"])
_INVALID_CHARS = frozenset(_CONTRACT["invalid_characters"])
_CONTROL_RANGES = tuple((int(r["first"]), int(r["last"])) for r in _CONTRACT["control_code_points"])
# White space that Python would trim but C# Trim() keeps (U+001C to U+001F): it is NOT trimmed here either, so the control-character rule refuses it at the edges.
_NOT_TRIMMED_RANGES = tuple((int(r["first"]), int(r["last"])) for r in _CONTRACT["not_trimmed_code_points"])


def _is_control(code_point: int) -> bool:
    return any(first <= code_point <= last for first, last in _CONTROL_RANGES)


def _is_trimmed(ch: str) -> bool:
    code_point = ord(ch)
    return ch.isspace() and not any(first <= code_point <= last for first, last in _NOT_TRIMMED_RANGES)


def _trim(text: str) -> str:
    """The trim step of C# Trim(): white space at both ends, except the not_trimmed_code_points of the contract."""
    start, end = 0, len(text)
    while start < end and _is_trimmed(text[start]):
        start += 1
    while end > start and _is_trimmed(text[end - 1]):
        end -= 1
    return text[start:end]


def is_well_formed_ticker(symbol: object) -> bool:
    """True when symbol is safe to map to a file name without path traversal.

    Matches C# TickerSymbol.IsWellFormed (the rules come from symbol_contract.json):
    - Non-empty after trimming (the trim step equals C# Trim(), see not_trimmed_code_points)
    - Length <= max_file_safety_length
    - No parent segments ('..')
    - No path separators ('/', '\\') or drive separators (':')
    - No control characters (the code point ranges of control_code_points: C0, DEL and C1)
    """
    if not isinstance(symbol, str):
        return False
    trimmed = _trim(symbol)
    if not trimmed or len(trimmed) > MAX_TICKER_LENGTH:
        return False
    if _PARENT_SEGMENT in trimmed:
        return False
    for ch in trimmed:
        if ch in _INVALID_CHARS or _is_control(ord(ch)):
            return False
    return True


def check_contract(contract: dict) -> list[str]:
    """Problems found when the validator rules are evaluated on the probes of ``contract`` (empty = consistent)."""
    problems: list[str] = []
    if not contract.get("probes"):
        problems.append("no probes")
    for probe in contract.get("probes", []):
        got = is_well_formed_ticker(probe["symbol"])
        if got != probe["well_formed"]:
            problems.append(f"probe {probe['symbol']!r} expected {probe['well_formed']} got {got}")
    return problems


def _run_selfcheck() -> None:
    problems = check_contract(_CONTRACT)
    assert not problems, problems
    assert not is_well_formed_ticker(None), "a non-string is never well formed"
    broken = json.loads(json.dumps(_CONTRACT))
    broken["probes"].append({"symbol": "AAPL", "well_formed": False})
    assert any("probe 'AAPL'" in p for p in check_contract(broken)), "a wrong probe must be reported"
    print(f"symbol_validator.py selfcheck: OK ({len(_CONTRACT['probes'])} probes)")


if __name__ == "__main__":
    if "--selfcheck" in sys.argv[1:]:
        _run_selfcheck()
    else:
        raise SystemExit("symbol_validator.py has no command line; use --selfcheck")
