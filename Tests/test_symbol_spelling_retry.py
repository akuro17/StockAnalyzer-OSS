"""Yahoo spelling of share-class tickers (owner decision D-08-8d = C).

`7203-T` must be asked as `7203.T`, but `BRK-B` only exists at Yahoo with the hyphen. The provider rewrites a trailing "-X" to ".X",
so when that spelling returns nothing it retries ONCE with the original spelling (only when the two spellings differ).
No network: a scripted fake yf records every symbol asked.
"""

import datetime
import sys
from pathlib import Path

import pandas as pd
import pytest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "StockAnalyzer.Python"))
import data_provider  # noqa: E402


def _frame(symbol):
    index = pd.DatetimeIndex(["2024-01-02", "2024-01-03"], name="Date")
    data = {(field, symbol): [1.0, 2.0] for field in ("Open", "High", "Low", "Close", "Volume")}
    frame = pd.DataFrame(data, index=index)
    frame.columns = pd.MultiIndex.from_tuples(frame.columns)
    return frame


class FakeYf:
    """Answers only for the symbols in `known`; every other symbol is an empty frame (Yahoo: No data found)."""

    def __init__(self, *known, failing=()):
        self.known = set(known)
        self.failing = set(failing)
        self.asked = []

    def download(self, symbol, **kwargs):
        self.asked.append(symbol)
        if symbol in self.failing:
            raise RuntimeError("network down")
        return _frame(symbol) if symbol in self.known else pd.DataFrame()


@pytest.fixture(autouse=True)
def _no_sleep(monkeypatch):
    monkeypatch.setattr(data_provider.time, "sleep", lambda *_: None)


def _fetch(yf, symbol, start_ts=0, end_ts=None):
    provider = data_provider.StockDataProvider()
    provider.yf = yf
    return provider.get_daily_data(symbol, start_ts=start_ts, end_ts=end_ts)


def test_share_class_ticker_is_found_with_the_original_hyphen_spelling_after_the_dot_spelling_is_empty():
    yf = FakeYf("BRK-B")
    df = _fetch(yf, "BRK-B")
    assert df is not None and len(df) == 2
    assert yf.asked == ["BRK.B", "BRK.B", "BRK.B", "BRK-B"]  # 3 attempts with the rewritten spelling, then ONE retry


def test_market_suffix_ticker_is_found_at_once_and_never_retried():
    yf = FakeYf("7203.T")
    assert _fetch(yf, "7203-T") is not None
    assert yf.asked == ["7203.T"]


def test_four_character_japanese_code_is_normalized_and_found_with_the_dot_spelling():
    yf = FakeYf("7203.T")
    assert _fetch(yf, "7203") is not None
    assert yf.asked == ["7203.T"]


def test_ticker_without_a_suffix_gets_no_original_spelling_retry():
    yf = FakeYf()  # nothing is known: AAPL -> 3 attempts, then the index retry only
    assert _fetch(yf, "AAPL") is None
    assert yf.asked == ["AAPL", "AAPL", "AAPL", "^AAPL"]


def test_nothing_found_in_either_spelling_ends_with_the_index_retry_and_none():
    yf = FakeYf()
    assert _fetch(yf, "ZZZ-B") is None
    assert yf.asked == ["ZZZ.B", "ZZZ.B", "ZZZ.B", "ZZZ-B", "^ZZZ.B"]  # exactly one original-spelling request


def test_a_failing_original_spelling_retry_does_not_stop_the_index_retry():
    yf = FakeYf("^ZZZ.B", failing=("ZZZ-B",))
    assert _fetch(yf, "ZZZ-B") is not None
    assert yf.asked[-2:] == ["ZZZ-B", "^ZZZ.B"]


@pytest.mark.parametrize("start_ts,end_ts", [
    (0, None),
    (int(datetime.datetime(2024, 1, 1, tzinfo=datetime.timezone.utc).timestamp()), None),
    (int(datetime.datetime(2024, 1, 1, tzinfo=datetime.timezone.utc).timestamp()),
     int(datetime.datetime(2024, 2, 1, tzinfo=datetime.timezone.utc).timestamp())),
])
def test_the_original_spelling_retry_works_in_every_request_shape(start_ts, end_ts):
    yf = FakeYf("BRK-A")
    assert _fetch(yf, "BRK-A", start_ts=start_ts, end_ts=end_ts) is not None
    assert yf.asked[-1] == "BRK-A"
