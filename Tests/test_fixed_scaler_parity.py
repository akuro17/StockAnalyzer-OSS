"""Verify training-window overlap counts and float32 parity fixture."""

import json
import sys
import tempfile
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "StockAnalyzer.Python" / "training"))
import dataset  # noqa: E402
import fixed_scaler  # noqa: E402


def test_fixture() -> None:
    fixture = json.loads((ROOT / "Tests" / "FixedScalerParity.json").read_text(encoding="utf-8"))
    lags = fixed_scaler.validate_lags(fixture["lags"])
    x, _ = dataset.build_dataset(
        np.asarray(fixture["ohlcv"], dtype=np.float64), "ohlcv_minmax",
        window=fixture["window_size"], horizon=fixture["horizon"],
        fixed_zscore=True, lags=lags,
    )
    np.testing.assert_array_equal(x, np.asarray(fixture["raw_windows"], dtype=np.float64))
    scaler = fixed_scaler.fit(x, "ohlcv_minmax", None, lags, fixture["clip_sigma"])
    assert scaler == fixture["scaler"]
    np.testing.assert_array_equal(
        fixed_scaler.transform(x, scaler), np.asarray(fixture["scaled_windows"], dtype=np.float32)
    )
    degenerate_raw = np.asarray([[[1, 0, 0, 0, 0], [1, 2, 2, 2, 2]]], dtype=np.float64)
    degenerate = fixed_scaler.fit(degenerate_raw, "ohlcv_minmax", None, (), 1.0)
    degenerate_output = fixed_scaler.transform(degenerate_raw, degenerate)
    assert np.array_equal(degenerate_output[:, :, 0], np.zeros((1, 2), dtype=np.float32))
    clipped = {**scaler, "clip_sigma": 1.0}
    assert np.max(fixed_scaler.transform(x, clipped)) == np.float32(1.0)
    with tempfile.TemporaryDirectory() as directory:
        model = Path(directory) / "fixture.onnx"
        metadata = {
            "feature_mode": "ohlcv_minmax", "window_size": "2", "channels": "10",
            "normalization": "fixed_zscore", "lags": "[1]", "clip_sigma": "3.0",
            "scaler_ref": "fixture.onnx.scaler.json",
        }
        fixed_scaler.write_sidecar(model, scaler, metadata)
        assert fixed_scaler.load_sidecar(model, metadata)["statistics"] == scaler["statistics"]
        try:
            fixed_scaler.load_sidecar(model, {**metadata, "lags": "[2]"})
        except ValueError:
            pass
        else:
            raise AssertionError("tampered lag metadata was accepted")


if __name__ == "__main__":
    test_fixture()
    print("fixed scaler parity fixture: PASS")
