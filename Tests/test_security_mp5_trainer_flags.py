import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "StockAnalyzer.Python" / "training"))
import trainer_flags  # noqa: E402


class TrainerFlagsSecurityTests(unittest.TestCase):
    def test_opset_and_price_adjustment_are_reserved(self):
        self.assertIn("opset", trainer_flags.RESERVED_TRAINER_FLAGS)
        self.assertIn("price-adjustment", trainer_flags.RESERVED_TRAINER_FLAGS)
        self.assertIn("opset", trainer_flags.SWEEP_RESERVED_TRAINER_FLAGS)
        self.assertIn("price-adjustment", trainer_flags.SWEEP_RESERVED_TRAINER_FLAGS)

        with self.assertRaises(ValueError):
            trainer_flags.validate_extra_flags("--opset 17")
        with self.assertRaises(ValueError):
            trainer_flags.validate_extra_flags("--price-adjustment raw")


if __name__ == "__main__":
    unittest.main()
