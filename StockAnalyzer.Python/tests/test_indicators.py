import math
import sys
import unittest
from pathlib import Path

PYTHON_ROOT = Path(__file__).resolve().parent.parent
if str(PYTHON_ROOT) not in sys.path:
    sys.path.insert(0, str(PYTHON_ROOT))

from indicators.RelativeStrengthIndex import calculate as calc_rsi
from indicators.SimpleMovingAverage import calculate as calc_sma


def _candles(closes):
    return [{"close": c} for c in closes]


class TestSimpleMovingAverage(unittest.TestCase):
    def test_empty_and_none_candles(self):
        self.assertEqual(calc_sma([], 3), [])
        self.assertEqual(calc_sma(None, 3), [])

    def test_invalid_period_raises(self):
        with self.assertRaises(ValueError):
            calc_sma(_candles([1.0, 2.0]), 0)

    def test_leading_interior_trailing_none_and_non_finite(self):
        closes = [None, 10.0, 20.0, float("nan"), 30.0, 40.0, 50.0, float("inf"), True, None]
        result = calc_sma(_candles(closes), 3)

        expected = [
            None,   # 0: warmup
            None,   # 1: warmup
            None,   # 2: [None, 10, 20] -> None
            None,   # 3: [10, 20, nan] -> None
            None,   # 4: [20, nan, 30] -> None
            None,   # 5: [nan, 30, 40] -> None
            40.0,   # 6: [30, 40, 50] -> 40.0
            None,   # 7: [40, 50, inf] -> None
            None,   # 8: [50, inf, True] -> None
            None,   # 9: [inf, True, None] -> None
        ]
        self.assertEqual(result, expected)

    def test_long_series_drift_resync(self):
        # 10,000 bars with non-dyadic decimals to verify periodic fsum resync prevents drift
        period = 20
        closes = [100.1 + (i % 7) * 0.13 for i in range(10000)]
        result = calc_sma(_candles(closes), period)

        self.assertEqual(len(result), len(closes))
        self.assertTrue(all(v is None for v in result[: period - 1]))

        # Check at resync boundary (idx = 1018 -> bars_since_resync=1000) and final bar
        for idx in (19, 1018, 5000, 9999):
            exact = math.fsum(closes[idx - period + 1 : idx + 1]) / period
            self.assertAlmostEqual(result[idx], exact, places=12)


class TestRelativeStrengthIndex(unittest.TestCase):
    def test_empty_and_single_candle(self):
        self.assertEqual(calc_rsi([], 3), [])
        self.assertEqual(calc_rsi(None, 3), [])
        self.assertEqual(calc_rsi(_candles([10.0]), 3), [None])

    def test_leading_none_resets_seed_window(self):
        # Leading None at index 0 -> index 1 has prev=None -> seed resets until 2 valid changes
        closes = [None, 10.0, 12.0, 14.0, 13.0]
        result = calc_rsi(_candles(closes), 2)

        self.assertIsNone(result[0])
        self.assertIsNone(result[1])
        self.assertIsNone(result[2])
        self.assertAlmostEqual(result[3], 100.0, places=9)
        self.assertIsNotNone(result[4])
        # After seed avg_gain = 2.0, avg_loss = 0.0 -> change=-1 -> avg_gain=1.0, avg_loss=0.5 -> RS=2 -> RSI=66.666...
        self.assertAlmostEqual(result[4], 200.0 / 3.0, places=9)

    def test_interior_none_and_non_finite_preserves_state_and_recovers(self):
        closes = [10.0, 12.0, 14.0, None, 16.0, 15.0, float("nan"), 18.0, float("-inf")]
        result = calc_rsi(_candles(closes), 2)

        self.assertIsNone(result[0])
        self.assertIsNone(result[1])
        self.assertAlmostEqual(result[2], 100.0, places=9)
        self.assertIsNone(result[3])  # curr is None
        self.assertIsNone(result[4])  # prev is None
        # At i=5: prev=16, curr=15 -> change=-1, smooth from seeded avg_gain=2.0, avg_loss=0.0 -> avg_gain=1.0, avg_loss=0.5 -> 66.666...
        self.assertAlmostEqual(result[5], 200.0 / 3.0, places=9)
        self.assertIsNone(result[6])  # nan
        self.assertIsNone(result[7])  # prev is nan
        self.assertIsNone(result[8])  # -inf

    def test_trailing_none(self):
        closes = [10.0, 11.0, 12.0, None]
        result = calc_rsi(_candles(closes), 2)
        self.assertEqual(result[:3], [None, None, 100.0])
        self.assertIsNone(result[3])


if __name__ == "__main__":
    unittest.main()
