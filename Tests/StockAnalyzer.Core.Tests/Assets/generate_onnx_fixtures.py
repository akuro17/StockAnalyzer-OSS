"""Regenerate the minimal real ONNX fixtures used by PredictionService integration tests.

These are NOT trained models - they are hand-built graphs that exercise the exact
inference contract of StockAnalyzer.Core.Services.PredictionService:

    trend_predictor_ok.onnx        input  "input"  float32 [batch, 10, 5]
                                   output "output" float32 [batch, 3]   (Softmax)
        -> conformant: PredictAsync must return a valid PredictionResult.

    trend_predictor_badclass.onnx  same input, output float32 [batch, 4]
        -> non-conformant: EnsureModelLoaded's ValidateModelContract must reject it,
           so PredictAsync falls back to PredictionResult.Empty.

    trend_predictor_goodmeta.onnx  conformant graph + a full metadata_props contract
           (feature_mode=ohlcv_minmax, window_size=10, class_order=Up,Down,Neutral)
        -> PredictionModelMetadata.Validate must accept it.

    trend_predictor_badmeta.onnx   conformant graph + a metadata_props contract whose
           feature_mode is "zscore"
        -> PredictionModelMetadata.Validate must reject it (config default is OhlcvMinMax),
           so PredictAsync falls back to PredictionResult.Empty.

    trend_predictor_jointmeta.onnx conformant [10,5] graph + feature_mode=zscore_joint.
    trend_predictor_lrohlc.onnx    [10,4] graph + feature_mode=log_return_ohlc.

    (trend_predictor_ok.onnx is intentionally left metadata-free to cover the
     "pre-contract model still loads with a warning" path.)

Run with an interpreter that has `onnx` installed, e.g. the training venv:
    StockAnalyzer.Python/.venv/Scripts/python.exe Tests/StockAnalyzer.Core.Tests/Assets/generate_onnx_fixtures.py

The .onnx files are committed; only re-run this if the contract changes.
"""

import sys
from pathlib import Path

import numpy as np
import onnx
from onnx import TensorProto, helper

# onnx_meta.py is the single source of truth for the metadata_props contract.
sys.path.insert(0, str(Path(__file__).resolve().parents[3] / "StockAnalyzer.Python" / "training"))
import onnx_meta  # noqa: E402

# Deliberately small hand-built fixture window. Unrelated to the 75-bar product
# default (StockAnalyzer.Avalonia PredictionSettings.WindowSize /
# PredictionSettingsManager.DefaultWindowSize / dataset.py DEFAULT_WINDOW).
# PredictionServiceTests pass predictionWindowSize: 10 to match these fixtures.
FIXTURE_WINDOW = 10
CHANNELS = 5
OPSET = 17
HERE = Path(__file__).resolve().parent


def _build(num_classes: int, channels: int = CHANNELS) -> onnx.ModelProto:
    flat = FIXTURE_WINDOW * channels
    rng = np.random.default_rng(20260827)
    weight = rng.normal(0.0, 0.35, size=(flat, num_classes)).astype(np.float32)
    bias = rng.normal(0.0, 0.05, size=(num_classes,)).astype(np.float32)

    inp = helper.make_tensor_value_info("input", TensorProto.FLOAT, ["batch", FIXTURE_WINDOW, channels])
    out = helper.make_tensor_value_info("output", TensorProto.FLOAT, ["batch", num_classes])

    nodes = [
        helper.make_node("Flatten", ["input"], ["flat"], axis=1),
        helper.make_node("MatMul", ["flat", "W"], ["logits_raw"]),
        helper.make_node("Add", ["logits_raw", "B"], ["logits"]),
        helper.make_node("Softmax", ["logits"], ["output"], axis=1),
    ]
    initializers = [
        helper.make_tensor("W", TensorProto.FLOAT, [flat, num_classes], weight.flatten()),
        helper.make_tensor("B", TensorProto.FLOAT, [num_classes], bias),
    ]
    graph = helper.make_graph(nodes, f"trend_predictor_{num_classes}c", [inp], [out], initializer=initializers)
    model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", OPSET)])
    model.ir_version = 9  # compatible with Microsoft.ML.OnnxRuntime 1.24.x
    onnx.checker.check_model(model)
    return model


def _with_contract(num_classes: int, feature_mode: str, channels: int = CHANNELS) -> onnx.ModelProto:
    model = _build(num_classes, channels)
    mapping = onnx_meta.build_contract(
        feature_mode=feature_mode, window_size=FIXTURE_WINDOW, channels=channels,
        horizon=5, threshold=0.005, wf_splits=5, seed=42,
        producer="generate_onnx_fixtures.py",
    )
    onnx_meta.apply(model, mapping)
    return model


def main() -> None:
    fixtures = (
        ("trend_predictor_ok.onnx", _build(3)),
        ("trend_predictor_badclass.onnx", _build(4)),
        ("trend_predictor_goodmeta.onnx", _with_contract(3, "ohlcv_minmax")),
        ("trend_predictor_badmeta.onnx", _with_contract(3, "zscore")),
        ("trend_predictor_jointmeta.onnx", _with_contract(3, "zscore_joint")),
        ("trend_predictor_lrohlc.onnx", _with_contract(3, "log_return_ohlc", channels=4)),
    )
    for name, model in fixtures:
        path = HERE / name
        onnx.save(model, str(path))
        print(f"wrote {path} ({path.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
