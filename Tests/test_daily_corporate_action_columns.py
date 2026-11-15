"""Daily file corporate-action columns (WebAiExpansion plan 08, T1).

No network: a fake ``yf`` module object is injected into ``StockDataProvider``.
Contract under test: the daily file keeps the six canonical columns first and adds adj_close / dividends /
stock_splits as Float64 (null = not fetched, 0.0 = no event); price columns stay Float32; volume stays Int64.
"""

import datetime
import sys
from pathlib import Path

import pandas as pd
import polars as pl
import pytest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "StockAnalyzer.Python"))
import data_provider  # noqa: E402
import update_daily  # noqa: E402

BASE = list(data_provider.BASE_COLUMNS)
ACTIONS = list(data_provider.CORPORATE_ACTION_COLUMNS)
ALL = list(data_provider.DAILY_COLUMNS)
SYMBOL = "AAA"


def _provider_frame(rows, with_actions=True):
    """A yfinance-shaped frame: DatetimeIndex 'Date', MultiIndex columns (field, ticker)."""
    index = pd.DatetimeIndex([r["date"] for r in rows], name="Date")
    fields = {"Open": "open", "High": "high", "Low": "low", "Close": "close", "Volume": "volume"}
    if with_actions:
        fields.update({"Adj Close": "adj", "Dividends": "div", "Stock Splits": "split"})
    data = {(name, SYMBOL): [r.get(key) for r in rows] for name, key in fields.items()}
    frame = pd.DataFrame(data, index=index)
    frame.columns = pd.MultiIndex.from_tuples(frame.columns)
    return frame


def _row(date, close, adj=None, div=None, split=None, volume=1000):
    base = float("nan") if close is None else close  # a None close makes a row the provider cleaning must drop
    return {"date": date, "open": base - 1.0, "high": base + 1.0, "low": base - 2.0, "close": close,
            "volume": volume, "adj": adj, "div": div, "split": split}


class FakeYf:
    """Records every download() keyword set; answers with the queued frames (an empty frame when none is left)."""

    def __init__(self, *frames):
        self.frames = list(frames)
        self.calls = []

    def download(self, symbol, **kwargs):
        self.calls.append((symbol, kwargs))
        return self.frames.pop(0) if self.frames else pd.DataFrame()


@pytest.fixture(autouse=True)
def _no_sleep(monkeypatch):
    monkeypatch.setattr(data_provider.time, "sleep", lambda *_: None)


def _provider(*frames):
    provider = data_provider.StockDataProvider()
    provider.yf = FakeYf(*frames)
    return provider


def test_every_download_requests_actions_in_all_call_shapes():
    ts = int(datetime.datetime(2024, 1, 1, tzinfo=datetime.timezone.utc).timestamp())
    end = int(datetime.datetime(2024, 2, 1, tzinfo=datetime.timezone.utc).timestamp())
    good = _provider_frame([_row("2024-01-02", 10.0, 9.9, 0.0, 0.0)])
    for kwargs in ({"start_ts": 0}, {"start_ts": ts}, {"start_ts": ts, "end_ts": end}):
        provider = _provider(good)
        assert provider.get_daily_data(SYMBOL, **kwargs) is not None
        assert provider.yf.calls[0][1]["actions"] is True
        assert provider.yf.calls[0][1]["auto_adjust"] is False
    # The "^" index retry (first download empty) must also ask for actions, in each of its three shapes.
    for kwargs in ({"start_ts": 0}, {"start_ts": ts}, {"start_ts": ts, "end_ts": end}):
        provider = _provider(pd.DataFrame(), pd.DataFrame(), pd.DataFrame(), good)
        assert provider.get_daily_data(SYMBOL, **kwargs) is not None
        assert provider.yf.calls[-1][0].startswith("^")
        assert all(call[1]["actions"] is True for call in provider.yf.calls)


def test_provider_returns_nine_columns_in_contract_order():
    frame = _provider_frame([
        _row("2024-01-02", 10.0, 9.5, 0.0, 0.0),
        _row("2024-01-03", 11.0, 10.4, 0.25, 0.0),
        _row("2024-01-04", 12.0, 11.4, 0.0, 10.0),
    ])
    df = _provider(frame).get_daily_data(SYMBOL, start_ts=0)
    assert list(df.columns) == ALL
    assert list(df["adj_close"]) == [9.5, 10.4, 11.4]
    assert list(df["dividends"]) == [0.0, 0.25, 0.0]
    assert list(df["stock_splits"]) == [0.0, 0.0, 10.0]


def test_provider_fills_missing_and_nan_corporate_action_values():
    nan = float("nan")
    frame = _provider_frame([_row("2024-01-02", 10.0, nan, nan, nan), _row("2024-01-03", 11.0, None, None, None)])
    df = _provider(frame).get_daily_data(SYMBOL, start_ts=0)
    assert list(df["adj_close"]) == [10.0, 11.0]  # adj_close = that row's close
    assert list(df["dividends"]) == [0.0, 0.0]
    assert list(df["stock_splits"]) == [0.0, 0.0]


def test_provider_fills_defaults_when_provider_returns_no_action_columns():
    frame = _provider_frame([_row("2024-01-02", 10.0), _row("2024-01-03", 11.0)], with_actions=False)
    df = _provider(frame).get_daily_data(SYMBOL, start_ts=0)
    assert list(df.columns) == ALL
    assert list(df["adj_close"]) == [10.0, 11.0]
    assert list(df["dividends"]) == [0.0, 0.0]
    assert list(df["stock_splits"]) == [0.0, 0.0]


def test_provider_aligns_actions_with_rows_that_were_dropped_for_a_null_close():
    rows = [_row("2024-01-02", 10.0, 9.0, 0.5, 0.0), _row("2024-01-03", None, 8.0, 0.7, 0.0), _row("2024-01-04", 12.0, 11.0, 0.0, 2.0)]
    df = _provider(_provider_frame(rows)).get_daily_data(SYMBOL, start_ts=0)
    assert len(df) == 2
    assert list(df["dividends"]) == [0.5, 0.0]
    assert list(df["stock_splits"]) == [0.0, 2.0]


def conform(frame):
    return update_daily.conform_corporate_action_columns(frame)


def test_conform_adds_null_columns_to_a_six_column_frame_and_keeps_order():
    six = pl.DataFrame({"date": [datetime.date(2024, 1, 2)], "open": [1.0], "high": [2.0], "low": [0.5], "close": [1.5],
                        "volume": [10]}).with_columns([pl.col(c).cast(pl.Float32) for c in ("open", "high", "low", "close")])
    out = conform(six)
    assert out.columns == ALL
    assert out.schema["close"] == pl.Float32 and out.schema["volume"] == pl.Int64
    assert all(out.schema[c] == pl.Float64 for c in ACTIONS)
    assert all(out[c][0] is None for c in ACTIONS)


def test_conform_keeps_existing_values():
    nine = pl.DataFrame({"date": [datetime.date(2024, 1, 2)], "open": [1.0], "high": [2.0], "low": [0.5], "close": [1.5],
                         "volume": [10], "adj_close": [1.4], "dividends": [0.0], "stock_splits": [2.0]})
    out = conform(nine)
    assert out.columns == ALL
    assert out["adj_close"][0] == 1.4 and out["dividends"][0] == 0.0 and out["stock_splits"][0] == 2.0


def _write_six_column_file(root, days):
    frame = pl.DataFrame({
        "date": days, "open": [10.0] * len(days), "high": [11.0] * len(days), "low": [9.0] * len(days),
        "close": [10.5] * len(days), "volume": [100] * len(days),
    }).with_columns([pl.col(c).cast(pl.Float32) for c in ("open", "high", "low", "close")]).with_columns(pl.col("volume").cast(pl.Int64))
    root.mkdir(parents=True, exist_ok=True)
    frame.write_parquet(root / f"{SYMBOL}.parquet")


class _FixedProvider:
    """update_ticker only calls get_daily_data; return a ready 9-column pandas frame."""

    def __init__(self, frame):
        self.frame = frame
        self.calls = []

    def get_daily_data(self, symbol, start_ts=0, end_ts=None, delay=None, auto_adjust=False):
        self.calls.append(start_ts)
        return self.frame


def _new_rows(dates, split_on=None):
    return pd.DataFrame({
        "date": pd.to_datetime(dates), "open": [20.0] * len(dates), "high": [21.0] * len(dates), "low": [19.0] * len(dates),
        "close": [20.5] * len(dates), "volume": [200.0] * len(dates), "adj_close": [20.25] * len(dates),
        "dividends": [0.0] * len(dates), "stock_splits": [10.0 if pd.Timestamp(d) == split_on else 0.0 for d in dates],
    })[ALL]


def _assert_storage_types(frame):
    assert frame.columns == ALL
    assert all(frame.schema[c] == pl.Float32 for c in ("open", "high", "low", "close"))  # D-01=C': prices stay Float32
    assert frame.schema["volume"] == pl.Int64
    assert all(frame.schema[c] == pl.Float64 for c in ACTIONS)


def test_update_appends_nine_column_rows_to_a_six_column_file(tmp_path, monkeypatch):
    root = tmp_path / "Daily"
    _write_six_column_file(root, [datetime.date(2024, 1, 2), datetime.date(2024, 1, 3)])
    monkeypatch.setattr(update_daily, "DATA_ROOT", str(root))
    # No split in the new rows: an unrecorded split would trigger the full re-sync (covered by test_split_resync.py).
    update_daily.update_ticker(_FixedProvider(_new_rows(["2024-01-04", "2024-01-05"])), SYMBOL)
    out = pl.read_parquet(root / f"{SYMBOL}.parquet")
    _assert_storage_types(out)
    assert out.height == 4
    assert out["date"].to_list() == [datetime.date(2024, 1, d) for d in (2, 3, 4, 5)]
    for column in ACTIONS:  # old rows: not fetched = null (never 0.0)
        assert out[column].to_list()[:2] == [None, None]
    assert out["stock_splits"].to_list()[2:] == [0.0, 0.0]
    assert out["adj_close"].to_list()[2:] == [20.25, 20.25]
    assert out["close"].to_list()[:2] == [10.5, 10.5]  # existing price rows unchanged


def test_force_period_path_also_handles_a_six_column_file(tmp_path, monkeypatch):
    root = tmp_path / "Daily"
    _write_six_column_file(root, [datetime.date(2001, 1, 2), datetime.date(2001, 1, 3)])
    monkeypatch.setattr(update_daily, "DATA_ROOT", str(root))
    update_daily.update_ticker(_FixedProvider(_new_rows(["2024-01-04"])), SYMBOL, force_period=True)
    out = pl.read_parquet(root / f"{SYMBOL}.parquet")
    _assert_storage_types(out)
    assert out["date"].to_list() == [datetime.date(2001, 1, 2), datetime.date(2001, 1, 3), datetime.date(2024, 1, 4)]
    assert out["dividends"].to_list() == [None, None, 0.0]


def test_new_file_is_created_with_nine_columns(tmp_path, monkeypatch):
    root = tmp_path / "Daily"
    root.mkdir()
    monkeypatch.setattr(update_daily, "DATA_ROOT", str(root))
    update_daily.update_ticker(_FixedProvider(_new_rows(["2024-01-04", "2024-01-05"])), SYMBOL)
    out = pl.read_parquet(root / f"{SYMBOL}.parquet")
    _assert_storage_types(out)
    assert out["adj_close"].to_list() == [20.25, 20.25]


def test_second_append_keeps_the_values_of_a_nine_column_file(tmp_path, monkeypatch):
    root = tmp_path / "Daily"
    root.mkdir()
    monkeypatch.setattr(update_daily, "DATA_ROOT", str(root))
    update_daily.update_ticker(_FixedProvider(_new_rows(["2024-01-04"], split_on=pd.Timestamp("2024-01-04"))), SYMBOL)
    update_daily.update_ticker(_FixedProvider(_new_rows(["2024-01-05"])), SYMBOL)
    out = pl.read_parquet(root / f"{SYMBOL}.parquet")
    _assert_storage_types(out)
    assert out["stock_splits"].to_list() == [10.0, 0.0]
