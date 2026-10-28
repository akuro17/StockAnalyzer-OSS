"""Ticker symbol validator matching StockAnalyzer.Core.Models.TickerSymbol SSoT."""

from __future__ import annotations
import os
from pathlib import Path

MAX_TICKER_LENGTH = 32
_PARENT_SEGMENT = ".."
_INVALID_CHARS = frozenset({'/', '\\', ':'})


def is_well_formed_ticker(symbol: object) -> bool:
    """True when symbol is safe to map to a file name without path traversal.

    Matches C# TickerSymbol.IsWellFormed:
    - Non-empty after trimming
    - Length <= 32
    - No parent segments ('..')
    - No path separators ('/', '\\') or drive separators (':')
    - No ASCII control characters
    """
    if not isinstance(symbol, str):
        return False
    trimmed = symbol.strip()
    if not trimmed or len(trimmed) > MAX_TICKER_LENGTH:
        return False
    if _PARENT_SEGMENT in trimmed:
        return False
    for ch in trimmed:
        if ch in _INVALID_CHARS or ord(ch) < 32 or ord(ch) == 127:
            return False
    return True


def validate_ticker(symbol: object) -> str:
    """Validate symbol and return stripped string, or raise ValueError."""
    if not is_well_formed_ticker(symbol):
        raise ValueError(f"Invalid or unsafe ticker symbol: {symbol!r}")
    return str(symbol).strip()
