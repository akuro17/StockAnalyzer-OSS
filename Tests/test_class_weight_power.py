"""The class-weight exponent (D1): the default is the former inverse-frequency weighting, 0 is unweighted, every trainer takes the flag.

Run by hand with the training venv: StockAnalyzer.Python/.venv/Scripts/python.exe Tests/test_class_weight_power.py (not part of dotnet test or CI).
"""

import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "StockAnalyzer.Python" / "training"))
import checkpoints  # noqa: E402
import dataset  # noqa: E402
import train_lightgbm  # noqa: E402
import train_pytorch  # noqa: E402
import train_tensorflow  # noqa: E402

LABELS = np.array([0] * 50 + [1] * 30 + [2] * 20 + [0] * 10)


def _former_weights(y):
    counts = np.array([(y == c).sum() for c in range(3)], dtype=np.float64)
    counts[counts == 0] = 1.0
    return counts.sum() / (3 * counts)


def test_the_default_is_bit_identical_to_the_former_formula_in_every_trainer():
    former = _former_weights(LABELS)
    assert np.array_equal(dataset.class_weight_vector(LABELS), former)
    assert np.array_equal(train_pytorch._class_weights(LABELS).numpy(), np.asarray(former, dtype=np.float32))
    assert train_tensorflow._class_weight(LABELS) == {c: float(former[c]) for c in range(3)}
    assert train_lightgbm._class_weight_argument(LABELS, None) == "balanced"
    assert train_lightgbm._class_weight_argument(LABELS, 1.0) == "balanced"


def test_zero_is_unweighted_and_a_middle_value_is_the_power():
    former = _former_weights(LABELS)
    assert train_tensorflow._class_weight(LABELS, 0.0) == {0: 1.0, 1: 1.0, 2: 1.0}
    assert np.array_equal(train_pytorch._class_weights(LABELS, 0.0).numpy(), np.ones(3, dtype=np.float32))
    assert train_lightgbm._class_weight_argument(LABELS, 0.0) is None
    assert np.allclose(train_pytorch._class_weights(LABELS, 0.5).numpy(), np.sqrt(former).astype(np.float32))
    middle = train_lightgbm._class_weight_argument(LABELS, 0.5)
    assert np.allclose([middle[c] for c in range(3)], np.sqrt(former))


def test_every_trainer_takes_the_flag_and_refuses_values_outside_the_unit_interval():
    for module in (train_pytorch, train_tensorflow, train_lightgbm):
        assert module._parse_args([]).class_weight_power is None
        assert module._parse_args(["--class-weight-power", "0.5"]).class_weight_power == 0.5
        for bad in ("1.5", "-0.1", "nan"):
            try:
                module._parse_args(["--class-weight-power", bad])
            except SystemExit as exc:
                assert "--class-weight-power must be in [0, 1]" in str(exc)
            else:
                raise AssertionError(f"{module.__name__} accepted {bad}")


def test_the_unset_key_stays_out_of_the_checkpoint_training_config():
    assert "class_weight_power" in checkpoints.OPTIONAL_RECIPE_KEYS
    assert "heads" in checkpoints.OPTIONAL_ARCH_KEYS and "heads" not in checkpoints.OPTIONAL_RECIPE_KEYS


if __name__ == "__main__":
    test_the_default_is_bit_identical_to_the_former_formula_in_every_trainer()
    test_zero_is_unweighted_and_a_middle_value_is_the_power()
    test_every_trainer_takes_the_flag_and_refuses_values_outside_the_unit_interval()
    test_the_unset_key_stays_out_of_the_checkpoint_training_config()
    print("class weight power checks passed")
