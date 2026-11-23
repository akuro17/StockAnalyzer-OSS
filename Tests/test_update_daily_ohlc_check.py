"""Tests for warning-only OHLCV invariant checking in update_daily.py and parity with candle_invariant_contract.json."""

import datetime
import json
import logging
import sys
import tempfile
import unittest
from pathlib import Path

import pandas as pd
import polars as pl

ROOT = Path(__file__).resolve().parents[1]
PYTHON_DIR = ROOT / "StockAnalyzer.Python"
sys.path.insert(0, str(PYTHON_DIR))
import data_provider  # noqa: E402
import update_daily  # noqa: E402

ALL = list(data_provider.DAILY_COLUMNS)


def _contract():
    return json.loads((PYTHON_DIR / "candle_invariant_contract.json").read_text(encoding="utf-8"))


def _storage_frame(rows):
    return (
        pl.DataFrame({
            "date": [r[0] for r in rows],
            "open": [r[1] for r in rows],
            "high": [r[2] for r in rows],
            "low": [r[3] for r in rows],
            "close": [r[4] for r in rows],
            "volume": [r[5] for r in rows],
            "adj_close": [r[4] for r in rows],
            "dividends": [0.0] * len(rows),
            "stock_splits": [0.0] * len(rows),
        })
        .with_columns([pl.col(c).cast(pl.Float32) for c in ("open", "high", "low", "close")])
        .with_columns([pl.col("volume").cast(pl.Int64)] + [pl.col(c).cast(pl.Float64) for c in data_provider.CORPORATE_ACTION_COLUMNS])
    )


def _provider_frame(rows):
    return pd.DataFrame({
        "date": pd.to_datetime([r[0].isoformat() for r in rows]),
        "open": [float(r[1]) for r in rows],
        "high": [float(r[2]) for r in rows],
        "low": [float(r[3]) for r in rows],
        "close": [float(r[4]) for r in rows],
        "volume": [float(r[5]) for r in rows],
        "adj_close": [float(r[4]) for r in rows],
        "dividends": [0.0] * len(rows),
        "stock_splits": [0.0] * len(rows),
    })[ALL]


class _ScriptedProvider:
    def __init__(self, frame):
        self.frame = frame

    def get_daily_data(self, symbol, start_ts=0, end_ts=None, delay=None, auto_adjust=False):
        return self.frame


class UpdateDailyOhlcCheckTests(unittest.TestCase):
    def test_parity_with_shared_candle_invariant_contract(self):
        contract = _contract()
        for case in contract["cases"]:
            with self.subTest(case=case["name"]):
                frame = _storage_frame([(
                    datetime.date.fromisoformat(case["date"]),
                    case["open"],
                    case["high"],
                    case["low"],
                    case["close"],
                    case["volume"],
                )])
                row = frame.to_dicts()[0]
                actual_codes = [code for code, _ in update_daily.check_ohlc_invariants(row)]
                self.assertEqual(case["expected_codes"], actual_codes)

    def test_stored_legacy_bad_row_never_warns_on_clean_incremental_update(self):
        with tempfile.TemporaryDirectory() as tmp:
            old_root = update_daily.DATA_ROOT
            try:
                update_daily.DATA_ROOT = tmp
                # Stored file has legacy bad bar on 2022-05-17 (Close > High).
                stored = _storage_frame([
                    (datetime.date(2022, 5, 17), 3821.333, 3866.667, 3770.667, 3869.333, 1000),
                    (datetime.date(2026, 11, 19), 9000.0, 9100.0, 8950.0, 9050.0, 1200),
                ])
                stored.write_parquet(Path(tmp) / "8035-T.parquet")

                provider = _ScriptedProvider(_provider_frame([
                    (datetime.date(2026, 11, 20), 9050.0, 9120.0, 9020.0, 9100.0, 1500),
                ]))

                with self.assertLogs(level="INFO") as captured:
                    update_daily.update_ticker(provider, "8035-T")

                warnings = [r for r in captured.records if r.levelno >= logging.WARNING]
                self.assertEqual([], warnings)

                merged = pl.read_parquet(Path(tmp) / "8035-T.parquet")
                self.assertEqual(3, merged.height)
            finally:
                update_daily.DATA_ROOT = old_root

    def test_invalid_fetched_bar_logs_warning_and_writes_file_without_dropping_row(self):
        with tempfile.TemporaryDirectory() as tmp:
            old_root = update_daily.DATA_ROOT
            try:
                update_daily.DATA_ROOT = tmp
                stored = _storage_frame([
                    (datetime.date(2026, 11, 19), 1500.0, 1520.0, 1490.0, 1510.0, 1000),
                ])
                stored.write_parquet(Path(tmp) / "8411-T.parquet")

                provider = _ScriptedProvider(_provider_frame([
                    (datetime.date(2026, 11, 20), 1500.0, 1513.5, 1487.5, 1486.5, 2000),
                    (datetime.date(2026, 11, 23), 1490.0, 1505.0, 1480.0, 1500.0, 1800),
                ]))

                with self.assertLogs(level="WARNING") as captured:
                    update_daily.update_ticker(provider, "8411-T")

                warning_msgs = [r.getMessage() for r in captured.records if r.levelno == logging.WARNING]
                self.assertEqual(1, len(warning_msgs))
                self.assertIn("8411-T: invalid bar 2026-11-20", warning_msgs[0])
                self.assertIn("Close 1486.5 < Low 1487.5", warning_msgs[0])
                self.assertIn("stored as fetched", warning_msgs[0])

                merged = pl.read_parquet(Path(tmp) / "8411-T.parquet").sort("date")
                self.assertEqual(3, merged.height)
                self.assertEqual(datetime.date(2026, 11, 20), merged["date"][1])
            finally:
                update_daily.DATA_ROOT = old_root

    def test_warning_lines_are_capped_by_named_constant_and_all_rows_still_stored(self):
        with tempfile.TemporaryDirectory() as tmp:
            old_root = update_daily.DATA_ROOT
            try:
                update_daily.DATA_ROOT = tmp
                stored = _storage_frame([
                    (datetime.date(2026, 11, 1), 100.0, 105.0, 95.0, 102.0, 1000),
                ])
                stored.write_parquet(Path(tmp) / "7203-T.parquet")

                bad_count = update_daily.MAX_LOGGED_INVALID_BARS + 3
                rows = [
                    (datetime.date(2026, 11, 2 + i), 100.0, 105.0, 95.0, 110.0 + i, 500)
                    for i in range(bad_count)
                ]
                provider = _ScriptedProvider(_provider_frame(rows))

                with self.assertLogs(level="WARNING") as captured:
                    update_daily.update_ticker(provider, "7203-T")

                warning_msgs = [r.getMessage() for r in captured.records if r.levelno == logging.WARNING]
                self.assertEqual(update_daily.MAX_LOGGED_INVALID_BARS + 1, len(warning_msgs))
                self.assertIn("3 more invalid bar(s) omitted", warning_msgs[-1])

                merged = pl.read_parquet(Path(tmp) / "7203-T.parquet")
                self.assertEqual(1 + bad_count, merged.height)
            finally:
                update_daily.DATA_ROOT = old_root

    def test_null_and_nan_direct_warn_invalid_bars_does_not_crash(self):
        frame = pl.DataFrame({
            "date": [datetime.date(2026, 11, 20), datetime.date(2026, 11, 21)],
            "open": [100.0, float("nan")],
            "high": [None, 105.0],
            "low": [95.0, 95.0],
            "close": [100.0, 102.0],
            "volume": [1000, 1000],
        }).cast({
            "date": pl.Date,
            "open": pl.Float32,
            "high": pl.Float32,
            "low": pl.Float32,
            "close": pl.Float32,
            "volume": pl.Int64,
        })
        with self.assertLogs(level="WARNING") as captured:
            count = update_daily.warn_invalid_bars("TEST", frame)
        self.assertEqual(2, count)
        self.assertEqual(2, len([r for r in captured.records if r.levelno == logging.WARNING]))


if __name__ == "__main__":
    unittest.main()
