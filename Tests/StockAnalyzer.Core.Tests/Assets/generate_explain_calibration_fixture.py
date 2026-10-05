"""Regenerate the C# test fixture for the T11 explanation and T12 calibration sidecars.

The fixture is a tiny hand-built classification ONNX (the same graph as the other prediction fixtures) plus the
four files a real training run publishes beside it, all produced by the *production Python code*
(``calibration.document`` / ``explain.explain_arrays``) so the C# loaders are tested against real Python output:

    explain_calibration_fixture.onnx                    declares com.stockanalyzer.calibration.ref
    explain_calibration_fixture.onnx.metrics.json       independent-outer evidence the registry requires
    explain_calibration_fixture.onnx.calibration.json   fitted temperature + before/after metrics
    explain_calibration_fixture.onnx.explain.json       Shapley contributions + partial dependence

Run with the training interpreter:
    StockAnalyzer.Python/.venv/Scripts/python.exe Tests/StockAnalyzer.Core.Tests/Assets/generate_explain_calibration_fixture.py

The generated files are committed; re-run only when a sidecar contract changes.
"""

from __future__ import annotations

import hashlib
import json
import sys
from pathlib import Path

import numpy as np
import onnx
import onnxruntime as ort

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parents[2] / "StockAnalyzer.Python" / "training"))
sys.path.insert(0, str(HERE))
import calibration  # noqa: E402
import explain  # noqa: E402
import generate_onnx_fixtures as base  # noqa: E402
import model_analysis  # noqa: E402
import onnx_meta  # noqa: E402

NAME = "explain_calibration_fixture.onnx"
DATA_REVISION = "a" * 64
EVALUATION_REVISION = "b" * 64
TRUE_TEMPERATURE = 1.8  # the fixture model is overconfident by this factor on the synthetic data
CALIBRATION_SAMPLES = 420
OUTER_TEST_SAMPLES = 300
SEED = 20261003


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _softmax(z: np.ndarray) -> np.ndarray:
    e = np.exp(z - z.max(axis=1, keepdims=True))
    return e / e.sum(axis=1, keepdims=True)


def main() -> None:
    rng = np.random.default_rng(SEED)
    model = base._build(3)
    mapping = onnx_meta.build_contract(
        feature_mode="ohlcv_minmax", window_size=base.FIXTURE_WINDOW, channels=base.CHANNELS,
        horizon=5, threshold=0.005, wf_splits=5, seed=42,
        producer="generate_explain_calibration_fixture.py",
        calibration_ref=NAME + ".calibration.json",
    )
    onnx_meta.apply(model, mapping)
    model_path = HERE / NAME
    onnx.save(model, str(model_path))
    model_hash = _sha256(model_path)
    session = ort.InferenceSession(str(model_path), providers=["CPUExecutionProvider"])
    metadata = session.get_modelmeta().custom_metadata_map

    def probabilities(x: np.ndarray) -> np.ndarray:
        (p,) = session.run(["output"], {"input": x.astype(np.float32)})
        return np.asarray(p, dtype=np.float64)

    def sample(n: int):
        x = rng.normal(0.0, 1.0, (n, base.FIXTURE_WINDOW, base.CHANNELS)).astype(np.float32)
        p = probabilities(x)
        # labels follow the *tempered* distribution, so the raw model is overconfident
        truth = _softmax(np.log(np.maximum(p, calibration.EPSILON)) / TRUE_TEMPERATURE)
        y = np.array([rng.choice(3, p=row / row.sum()) for row in truth], dtype=np.int64)
        return x, p, y

    _, cal_p, cal_y = sample(CALIBRATION_SAMPLES)
    _, test_p, test_y = sample(OUTER_TEST_SAMPLES)
    days = np.datetime64("2019-01-01") + np.arange(CALIBRATION_SAMPLES).astype("timedelta64[D]")
    half = CALIBRATION_SAMPLES // 2
    periods = [
        model_analysis.period("AAA", "calibration", days, np.arange(half)),
        model_analysis.period("BBB", "calibration", days, np.arange(half, CALIBRATION_SAMPLES)),
    ]
    calibration_document = calibration.document(
        model_sha256=model_hash, data_revision=DATA_REVISION, metadata=metadata,
        cal_p=cal_p, cal_y=cal_y, test_p=test_p, test_y=test_y, periods=periods)
    (HERE / (NAME + ".calibration.json")).write_bytes(
        (json.dumps(calibration_document, indent=2, allow_nan=False) + chr(10)).encode("utf-8"))

    base_channels, expanded = model_analysis.channel_names(metadata)
    fit = rng.normal(0.0, 1.0, (300, base.FIXTURE_WINDOW, base.CHANNELS)).astype(np.float32)
    validation = rng.normal(0.0, 1.0, (30, base.FIXTURE_WINDOW, base.CHANNELS)).astype(np.float32)
    val_dates = np.datetime64("2020-06-01") + np.arange(len(validation)).astype("timedelta64[D]")
    body = explain.explain_arrays(
        probabilities, fit_tensor=fit, validation_tensor=validation,
        validation_symbols=["AAA"] * 15 + ["BBB"] * 15, validation_dates=val_dates,
        validation_rows=np.arange(len(validation)), classes=list(explain.ds.CLASS_LABELS),
        base_channels=base_channels)
    header = explain.make_header(
        model_sha256=model_hash, data_revision=DATA_REVISION, metadata=metadata, timeframe="daily",
        window_size=base.FIXTURE_WINDOW, base_channels=base_channels, ordered_channels=expanded)
    (HERE / (NAME + ".explain.json")).write_bytes(
        (json.dumps(dict(header, **body), indent=2, allow_nan=False) + chr(10)).encode("utf-8"))

    metrics = {
        "calibration": {"minimum_samples": calibration.MIN_SAMPLES, "sample_count": CALIBRATION_SAMPLES,
                        "status": "available", "temperature": calibration_document["temperature"]},
        "class_order": "Up,Down,Neutral", "data_revision": DATA_REVISION,
        "evaluation_revision": EVALUATION_REVISION, "evaluation_status": "independent_outer",
        "fold": 1.0, "fold_is_holdout": 1.0, "fold_macro_f1": 0.5, "fold_n": 10.0, "horizon": 5, "n_splits": 2.0,
        "outer_folds": [
            {"evaluation_revision": EVALUATION_REVISION, "fold": 0.0, "fold_macro_f1": 0.5,
             "scored_model_sha256": model_hash},
            {"evaluation_revision": EVALUATION_REVISION, "fold": 1.0, "fold_macro_f1": 0.5,
             "scored_model_sha256": model_hash},
        ],
        "scored_model_sha256": model_hash, "target_type": "classification", "timeframe": "daily",
    }
    (HERE / (NAME + ".metrics.json")).write_text(
        json.dumps(metrics, separators=(",", ":"), sort_keys=True, allow_nan=False), encoding="utf-8")
    print("temperature", calibration_document["temperature"], "explain records", len(body["records"]))
    for suffix in ("", ".metrics.json", ".calibration.json", ".explain.json"):
        path = HERE / (NAME + suffix)
        print("wrote", path.name, path.stat().st_size, "bytes")


if __name__ == "__main__":
    main()
