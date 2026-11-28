import contextlib
import datetime
import io
import logging
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import pandas as pd
import polars as pl

PYTHON_ROOT = Path(__file__).resolve().parent.parent
if str(PYTHON_ROOT) not in sys.path:
    sys.path.insert(0, str(PYTHON_ROOT))

import data_provider  # noqa: E402
import update_daily  # noqa: E402
import verify_data  # noqa: E402


def _make_yfinance_frame(rows):
    index = pd.DatetimeIndex([r["date"] for r in rows], name="Date")
    data = {
        ("Open", "AAA"): [r["open"] for r in rows],
        ("High", "AAA"): [r["high"] for r in rows],
        ("Low", "AAA"): [r["low"] for r in rows],
        ("Close", "AAA"): [r["close"] for r in rows],
        ("Volume", "AAA"): [r["volume"] for r in rows],
        ("Adj Close", "AAA"): [r["close"] for r in rows],
        ("Dividends", "AAA"): [0.0 for _ in rows],
        ("Stock Splits", "AAA"): [0.0 for _ in rows],
    }
    frame = pd.DataFrame(data, index=index)
    frame.columns = pd.MultiIndex.from_tuples(frame.columns)
    return frame


class _FakeYf:
    def __init__(self, df):
        self._df = df

    def download(self, symbol, **kwargs):
        return self._df


class _ScriptedUpdateProvider:
    def __init__(self, df_pd):
        self._df_pd = df_pd

    def get_daily_data(self, symbol, start_ts=0, end_ts=None, delay=None, auto_adjust=False):
        return self._df_pd


class TestOhlcvIngestionDiagnostics(unittest.TestCase):
    def test_missing_ohl_and_volume_imputation_emits_warning_with_counts_and_samples(self):
        nan = float("nan")
        yf_df = _make_yfinance_frame([
            {"date": "2025-01-02", "open": 100.0, "high": nan, "low": 98.0, "close": 101.0, "volume": 1000.0},
            {"date": "2025-01-03", "open": 101.0, "high": 103.0, "low": nan, "close": 102.0, "volume": nan},
            {"date": "2025-01-06", "open": 102.0, "high": 104.0, "low": 101.0, "close": 103.0, "volume": 1200.0},
        ])
        provider = data_provider.StockDataProvider()
        provider.yf = _FakeYf(yf_df)

        with mock.patch.object(data_provider.time, "sleep", lambda *_: None):
            with self.assertLogs(level="WARNING") as captured:
                result = provider.get_daily_data("AAA", start_ts=0)

        self.assertIsNotNone(result)
        self.assertEqual(len(result), 3)
        self.assertEqual(float(result.iloc[0]["high"]), 101.0)
        self.assertEqual(float(result.iloc[1]["low"]), 102.0)
        self.assertEqual(float(result.iloc[1]["volume"]), 0.0)

        msg = "\n".join(r.getMessage() for r in captured.records if r.levelno == logging.WARNING)
        self.assertIn("AAA", msg)
        self.assertIn("missing O/H/L filled from close in 2 row(s)", msg)
        self.assertIn("missing volume filled with 0 in 1 row(s)", msg)
        self.assertIn("2025-01-02", msg)
        self.assertIn("2025-01-03", msg)

    def test_warn_invalid_bars_returns_exact_count_and_validate_resync_warns_non_destructively(self):
        stored_dates = pl.Series("date", [datetime.date(2025, 1, 2), datetime.date(2025, 1, 3)], dtype=pl.Date)
        frame = (
            pl.DataFrame({
                "date": [datetime.date(2025, 1, 2), datetime.date(2025, 1, 3)],
                "open": [100.0, 100.0],
                "high": [105.0, 99.0],   # bar 2 violates High < Low and High < Open/Close
                "low": [95.0, 101.0],
                "close": [102.0, 100.0],
                "volume": [1000, -5],
                "adj_close": [102.0, 100.0],
                "dividends": [0.0, 0.0],
                "stock_splits": [0.0, 0.0],
            })
            .with_columns([pl.col(c).cast(pl.Float32) for c in ("open", "high", "low", "close")])
            .with_columns([pl.col("volume").cast(pl.Int64)] + [pl.col(c).cast(pl.Float64) for c in data_provider.CORPORATE_ACTION_COLUMNS])
        )

        with self.assertLogs(level="WARNING") as captured_warn:
            invalid_count = update_daily.warn_invalid_bars("AAA", frame)
        self.assertEqual(invalid_count, 1)
        self.assertEqual(len(captured_warn.records), 1)

        with tempfile.TemporaryDirectory() as tmpdir:
            parquet_path = Path(tmpdir) / "AAA.parquet"
            frame.write_parquet(parquet_path)

            with self.assertLogs(level="WARNING") as captured_resync:
                problems = update_daily.validate_resync_file(str(parquet_path), stored_dates)

            self.assertEqual(problems, [])
            resync_msgs = "\n".join(r.getMessage() for r in captured_resync.records if r.levelno == logging.WARNING)
            self.assertIn("1 invalid OHLCV bar(s)", resync_msgs)

    def test_verify_data_prints_invalid_ohlcv_bar_count(self):
        frame = (
            pl.DataFrame({
                "date": [datetime.date(2025, 1, 2), datetime.date(2025, 1, 3), datetime.date(2025, 1, 6)],
                "open": [100.0, 100.0, 100.0],
                "high": [105.0, 95.0, 102.0],  # bar 2: High < Open/Low/Close
                "low": [95.0, 98.0, 99.0],
                "close": [102.0, 100.0, -1.0],  # bar 3: non-positive close & Low > Close
                "volume": [1000, 500, 100],
            })
            .with_columns([pl.col(c).cast(pl.Float32) for c in ("open", "high", "low", "close")])
            .with_columns(pl.col("volume").cast(pl.Int64))
        )

        with tempfile.TemporaryDirectory() as tmpdir:
            parquet_path = Path(tmpdir) / "BAD.parquet"
            frame.write_parquet(parquet_path)

            stdout = io.StringIO()
            with contextlib.redirect_stdout(stdout):
                ok = verify_data.verify_parquet(str(parquet_path))

        self.assertTrue(ok)
        self.assertIn("Invalid OHLCV bars: 2", stdout.getvalue())

    def test_update_daily_main_logs_run_level_summary(self):
        with tempfile.TemporaryDirectory() as tmpdir:
            df_pd = pd.DataFrame({
                "date": pd.to_datetime(["2025-01-02", "2025-01-03"]),
                "open": [100.0, 100.0],
                "high": [105.0, 98.0],   # bar 2 invalid: High < Low/Open/Close
                "low": [95.0, 99.0],
                "close": [101.0, 100.0],
                "volume": [1000.0, 1000.0],
                "adj_close": [101.0, 100.0],
                "dividends": [0.0, 0.0],
                "stock_splits": [0.0, 0.0],
            })
            provider = _ScriptedUpdateProvider(df_pd)

            with mock.patch.object(update_daily, "DATA_ROOT", tmpdir), \
                 mock.patch.object(update_daily, "StockDataProvider", return_value=provider), \
                 mock.patch.object(sys, "argv", ["update_daily.py", "--ticker", "AAA"]):
                with self.assertLogs(level="INFO") as captured:
                    update_daily.main()

            msgs = "\n".join(r.getMessage() for r in captured.records)
            self.assertIn("tickers_updated=1", msgs)
            self.assertIn("total_invalid_bars=1", msgs)
            self.assertIn("tickers_with_invalid_bars=1", msgs)


if __name__ == "__main__":
    unittest.main()
