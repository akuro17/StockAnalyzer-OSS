"""Regenerate the attention fixtures the C# consumer is verified against.

* ``Assets/attention_fixture.onnx`` + ``Assets/attention_fixture.onnx.attention.onnx``: a small production model with the single-symbol contract and its
  attention sidecar, both written by the production Python code (``train_pytorch.export_onnx``, ``attention_export.export_sidecar``).
* ``Tests/AttentionParity.json``: the sidecar contract constants and, for candle histories, the attention the sidecar gives for the newest bar (every head, the
  head mean and ``w / max(w)``), computed with onnxruntime on the written file. ``AttentionContract`` / ``AttentionService`` must reproduce them.

The model has seeded random weights with enlarged query / key projections so the attention rows are visibly non-uniform (a default-initialised model attends
almost uniformly and would make the parity check blind).

Run with the training interpreter:
    StockAnalyzer.Python/.venv/Scripts/python.exe Tests/StockAnalyzer.Core.Tests/Assets/generate_attention_fixture.py
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

import numpy as np
import torch

HERE = Path(__file__).resolve().parent
TRAINING = HERE.parents[2] / "StockAnalyzer.Python" / "training"
sys.path.insert(0, str(TRAINING))
import attention_export as ae  # noqa: E402
import dataset as ds  # noqa: E402
import fixed_scaler  # noqa: E402
import onnx_meta  # noqa: E402
import train_pytorch as tp  # noqa: E402

WINDOW, HEADS, HIDDEN, LAYERS = 8, 2, 8, 1
FEATURE_MODE = "ohlcv_minmax"
HORIZON, THRESHOLD, WF_SPLITS, SEED = 3, 0.005, 5, 1
MODEL_SEED = 3
QK_SCALE = 6.0
MINIMUM_SPREAD = 0.02          # max - min of the head-mean row; a flat window may be uniform
PRICE_DECIMALS = 4
MODEL_PATH = HERE / "attention_fixture.onnx"
PARITY_PATH = HERE.parents[1] / "AttentionParity.json"


def candles(rows: int, seed: int, flat_tail: int = 0) -> np.ndarray:
    rng = np.random.default_rng(seed)
    close = 100.0 * np.exp(np.cumsum(rng.normal(0.0, 0.01, rows)))
    open_ = close * np.exp(rng.normal(0.0, 0.003, rows))
    high = np.maximum(open_, close) * (1.0 + np.abs(rng.normal(0.0, 0.003, rows)))
    low = np.minimum(open_, close) * (1.0 - np.abs(rng.normal(0.0, 0.003, rows)))
    volume = rng.integers(1_000, 50_000, rows).astype(np.float64)
    data = np.stack([open_, high, low, close, volume], axis=1)
    data[:, :4] = np.round(data[:, :4], PRICE_DECIMALS)
    if flat_tail:
        data[rows - flat_tail:] = data[rows - flat_tail]
    return data


def dates(rows: int) -> list:
    out, day = [], np.datetime64("2022-01-03")
    while len(out) < rows:
        if day.astype(object).weekday() < 5:
            out.append(str(day))
        day = day + np.timedelta64(1, "D")
    return out


def build_models() -> None:
    torch.manual_seed(MODEL_SEED)
    model = ae.AttentionLstmClassifier(5, HIDDEN, LAYERS, 0.0, tp.NUM_CLASSES, HEADS).eval()
    with torch.no_grad():
        for layer in (model.attn.query, model.attn.key):
            layer.weight.mul_(QK_SCALE)
    contract = onnx_meta.build_contract(feature_mode=FEATURE_MODE, window_size=WINDOW, channels=5, horizon=HORIZON, threshold=THRESHOLD, wf_splits=WF_SPLITS,
                                        seed=SEED, producer="generate_attention_fixture.py")
    tp.export_onnx(model, 5, WINDOW, MODEL_PATH, tp.DEFAULT_OPSET, contract)
    sidecar = ae.export_sidecar(model, MODEL_PATH, window=WINDOW, channels=5, opset=tp.DEFAULT_OPSET, feature_contract_hash=fixed_scaler.contract_hash(contract))
    ae.verify_sidecar(sidecar, model, MODEL_PATH, window=WINDOW, channels=5)


def reference(rows: np.ndarray, minimum_spread: float) -> dict:
    import onnxruntime as ort

    window = rows[-WINDOW:]
    x = ds.normalize_ohlcv_minmax(window).astype(np.float32)[None]
    session = ort.InferenceSession(str(ae.sidecar_path(MODEL_PATH)), providers=["CPUExecutionProvider"])
    (weights,) = session.run([ae.OUTPUT_NAME], {ae.INPUT_NAME: x})
    by_head = weights[0, :, WINDOW - 1, :].astype(np.float64)          # the last query row of every head
    mean = by_head.mean(axis=0)
    assert float(mean.max()) > 0.0 and float(np.ptp(mean)) > minimum_spread, f"the fixture attention is too uniform ({float(np.ptp(mean)):.4f})"
    return dict(heads=by_head.tolist(), weights=mean.astype(np.float32).tolist(), relative=(mean / mean.max()).astype(np.float32).tolist())


def main() -> None:
    build_models()
    cases = []
    for name, rows, seed, flat in (("newest_bar_of_a_short_history", 20, 11, 0), ("only_the_last_window_matters", 45, 12, 0), ("flat_window", 25, 13, WINDOW)):
        data = candles(rows, seed, flat)
        cases.append(dict(name=name, candles=[[d, *map(float, r[:4]), int(r[4])] for d, r in zip(dates(rows), data)], expected=reference(data, 0.0 if flat else MINIMUM_SPREAD)))
    parity = dict(
        contract=dict(sidecar_suffix=ae.SIDECAR_SUFFIX, metadata_prefix=ae.KEY_PREFIX,
                      keys=dict(schema_version=ae.KEY_SCHEMA, parent_sha256=ae.KEY_PARENT_SHA256, window_size=ae.KEY_WINDOW, heads=ae.KEY_HEADS,
                                feature_contract_hash=ae.KEY_FEATURE_HASH, layout=ae.KEY_LAYOUT, mask=ae.KEY_MASK, unit=ae.KEY_UNIT),
                      schema_version=ae.SCHEMA_VERSION, layout=ae.LAYOUT, mask=ae.MASK, unit=ae.UNIT, input_name=ae.INPUT_NAME, output_name=ae.OUTPUT_NAME,
                      max_heads=ae.MAX_HEADS, row_sum_tolerance=ae.ROW_SUM_ATOL),
        model=dict(file=MODEL_PATH.name, window=WINDOW, heads=HEADS, channels=5, feature_mode=FEATURE_MODE), cases=cases)
    PARITY_PATH.write_bytes((json.dumps(parity, indent=1, allow_nan=False) + chr(10)).encode("utf-8"))
    print("wrote", PARITY_PATH.name, len(cases), "cases; model", MODEL_PATH.name)


if __name__ == "__main__":
    main()
