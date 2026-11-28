import logging
import sys
import tempfile
import time
import unittest
from pathlib import Path
from unittest import mock

import pandas as pd

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "StockAnalyzer.Python"))
import data_provider  # noqa: E402
import update_pipeline  # noqa: E402


class _FakeTicker:
    def __init__(self, info, last_price=100.0, prev_close=99.0):
        self.info = info
        self.fast_info = {"last_price": last_price, "regularMarketPreviousClose": prev_close}


class _FakeYfForMetadata:
    def __init__(self, mapping):
        self._mapping = mapping

    def Ticker(self, symbol):
        return self._mapping.get(symbol, _FakeTicker({}))


class _FakeYfForDownload:
    def __init__(self, known):
        self._known = set(known)
        self.asked = []

    def download(self, symbol, **_kwargs):
        self.asked.append(symbol)
        if symbol not in self._known:
            return pd.DataFrame()
        index = pd.DatetimeIndex(["2024-01-02", "2024-01-03"], name="Date")
        data = {(field, symbol): [10.0, 11.0] for field in ("Open", "High", "Low", "Close", "Volume")}
        frame = pd.DataFrame(data, index=index)
        frame.columns = pd.MultiIndex.from_tuples(frame.columns)
        return frame


class SecurityMp1Mp2Tests(unittest.TestCase):
    def test_update_pipeline_run_script_enforces_timeout(self):
        with tempfile.TemporaryDirectory() as tmp:
            tmp_dir = Path(tmp)
            hanging = tmp_dir / "hang_step.py"
            hanging.write_text("import time\nprint('started', flush=True)\ntime.sleep(30)\n", encoding="utf-8")

            with mock.patch.object(update_pipeline.os.path, "dirname", return_value=str(tmp_dir)):
                start = time.monotonic()
                ok = update_pipeline.run_script("hang_step.py", 0, 100, timeout_seconds=0.5)
                elapsed = time.monotonic() - start

        self.assertFalse(ok)
        self.assertLess(elapsed, 5.0)

    def test_update_pipeline_rejects_non_positive_timeout(self):
        with mock.patch.object(sys, "argv", ["update_pipeline.py", "--timeout", "0"]):
            with self.assertRaises(SystemExit):
                update_pipeline.main()

    def test_get_ticker_metadata_sets_resolved_as_index_flag_and_warns(self):
        provider = data_provider.StockDataProvider()
        provider.yf = _FakeYfForMetadata({
            "GSPC": _FakeTicker({}),
            "^GSPC": _FakeTicker(
                {"shortName": "S&P 500", "longName": "S&P 500 Index", "currency": "USD"},
                last_price=5000.0,
                prev_close=4980.0,
            ),
        })

        with self.assertLogs(level=logging.WARNING) as captured:
            res = provider.get_ticker_metadata("GSPC")

        self.assertEqual(res["status"], "ok")
        self.assertEqual(res["shortName"], "S&P 500")
        self.assertTrue(res["resolvedAsIndex"])
        self.assertEqual(res["providerSymbol"], "^GSPC")
        self.assertTrue(any("index fallback" in msg for msg in captured.output))

    def test_get_from_yfinance_warns_on_caret_index_fallback(self):
        provider = data_provider.StockDataProvider()
        provider.yf = _FakeYfForDownload({"^GSPC"})

        with mock.patch.object(data_provider.time, "sleep", return_value=None):
            with self.assertLogs(level=logging.WARNING) as captured:
                df = provider._get_from_yfinance("GSPC", 1704067200, 1704326400)

        self.assertFalse(df.empty)
        self.assertEqual(provider.yf.asked[0], "GSPC")
        self.assertEqual(provider.yf.asked[-1], "^GSPC")
        self.assertTrue(any("index fallback" in msg for msg in captured.output))


if __name__ == "__main__":
    unittest.main()
