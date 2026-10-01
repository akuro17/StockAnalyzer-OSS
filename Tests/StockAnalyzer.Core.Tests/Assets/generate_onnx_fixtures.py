"""Regenerate the minimal real ONNX fixtures used by PredictionService integration tests.

These are NOT trained models - they are hand-built graphs that exercise the exact
inference contract of StockAnalyzer.Core.Services.PredictionService:

    trend_predictor_ok.onnx        input  "input"  float32 [batch, 10, 5]
                                   output "output" float32 [batch, 3]   (Softmax)
        -> legacy: PredictAsync must reject it without semantic metadata.

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

    trend_predictor_regression.onnx  input "input" float32 [batch, 10, 5]
                                      output "output" float32 [batch, 1]
           + metadata_props target_type=regression. Weight matrix is all-zero and bias is a
           fixed constant (REGRESSION_FIXTURE_Y), so the output is that exact known value
           regardless of input. This pre-v2 fixture is retained for legacy-contract checks.

    trend_predictor_regression_v2.onnx  the same known output with version-2 semantic,
           unit, horizon, timeframe, confidence-type and purge-gap metadata.

    (trend_predictor_ok.onnx is intentionally left metadata-free to cover
     fail-fast rejection of a pre-contract classification model.)

Run with an interpreter that has `onnx` installed, e.g. the training venv:
    StockAnalyzer.Python/.venv/Scripts/python.exe Tests/StockAnalyzer.Core.Tests/Assets/generate_onnx_fixtures.py

The .onnx files are committed; only re-run this if the contract changes.
"""

import json
import sys
from pathlib import Path

import numpy as np
import onnx
from onnx import TensorProto, helper

# onnx_meta.py is the single source of truth for the metadata_props contract.
sys.path.insert(0, str(Path(__file__).resolve().parents[3] / "StockAnalyzer.Python" / "training"))
import onnx_meta  # noqa: E402
import fixed_scaler  # noqa: E402

# Deliberately small hand-built fixture window. Unrelated to the 75-bar product
# default (StockAnalyzer.Avalonia PredictionSettings.WindowSize /
# PredictionSettingsManager.DefaultWindowSize / dataset.py DEFAULT_WINDOW).
# PredictionServiceTests pass predictionWindowSize: 10 to match these fixtures.
FIXTURE_WINDOW = 10
CHANNELS = 5
VARIANT_BIAS_SHIFT = 3.0  # makes an otherwise contract-compatible model observably different
OPSET = 17
HERE = Path(__file__).resolve().parent

# Fixed known output for trend_predictor_regression.onnx (see _build_regression below).
REGRESSION_FIXTURE_Y = 0.04


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


def _build_regression(channels: int = CHANNELS) -> onnx.ModelProto:
    """A [batch, FIXTURE_WINDOW, channels] -> [batch, 1] graph whose output is always exactly
    REGRESSION_FIXTURE_Y, regardless of input (zero weight matrix, constant bias) - deterministic
    on purpose so tests can assert the exact expected SimpleReturnPercent conversion."""
    flat = FIXTURE_WINDOW * channels
    weight = np.zeros((flat, 1), dtype=np.float32)
    bias = np.array([REGRESSION_FIXTURE_Y], dtype=np.float32)

    inp = helper.make_tensor_value_info("input", TensorProto.FLOAT, ["batch", FIXTURE_WINDOW, channels])
    out = helper.make_tensor_value_info("output", TensorProto.FLOAT, ["batch", 1])

    nodes = [
        helper.make_node("Flatten", ["input"], ["flat"], axis=1),
        helper.make_node("MatMul", ["flat", "W"], ["logits_raw"]),
        helper.make_node("Add", ["logits_raw", "B"], ["output"]),
    ]
    initializers = [
        helper.make_tensor("W", TensorProto.FLOAT, [flat, 1], weight.flatten()),
        helper.make_tensor("B", TensorProto.FLOAT, [1], bias),
    ]
    graph = helper.make_graph(nodes, "trend_predictor_regression", [inp], [out], initializer=initializers)
    model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", OPSET)])
    model.ir_version = 9  # compatible with Microsoft.ML.OnnxRuntime 1.24.x
    onnx.checker.check_model(model)
    return model


def _with_regression_contract(channels: int = CHANNELS) -> onnx.ModelProto:
    model = _build_regression(channels)
    mapping = onnx_meta.build_contract(
        feature_mode="ohlcv_minmax", window_size=FIXTURE_WINDOW, channels=channels,
        horizon=5, threshold=0.005, wf_splits=5, seed=42,
        producer="generate_onnx_fixtures.py", target_type="regression",
    )
    onnx_meta.apply(model, mapping)
    return model


def _with_legacy_regression_contract() -> onnx.ModelProto:
    model = _with_regression_contract()
    metadata = {entry.key: entry.value for entry in model.metadata_props
                if not entry.key.startswith("com.stockanalyzer.")}
    metadata["model_contract_version"] = "1"
    onnx_meta.apply(model, metadata)
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


def _with_shifted_contract() -> onnx.ModelProto:
    """Same contract as goodmeta, different output for an in-flight generation test."""
    model = _with_contract(3, "ohlcv_minmax")
    bias = next(value for value in model.graph.initializer if value.name == "B")
    bias.float_data[0] += VARIANT_BIAS_SHIFT
    onnx.checker.check_model(model)
    return model


def _with_missing_scaler_reference() -> onnx.ModelProto:
    """Fixed metadata with an intentionally absent mandatory sidecar reference."""
    model = _build(3)
    mapping = onnx_meta.build_contract(
        feature_mode="ohlcv_minmax", window_size=FIXTURE_WINDOW, channels=CHANNELS,
        horizon=5, threshold=0.005, wf_splits=5, seed=42,
        producer="generate_onnx_fixtures.py", fixed_zscore=True,
        scaler_ref="trend_predictor_missing_scaler.onnx.scaler.json",
    )
    del mapping["scaler_ref"]
    onnx_meta.apply(model, mapping)
    return model


def _with_fixed_scaler(name: str = "trend_predictor_fixed_scaler.onnx",
                       lags: tuple[int, ...] = (), clip_sigma: float = 3.0) -> onnx.ModelProto:
    model = _build(3, channels=CHANNELS * (1 + len(lags)))
    mapping = onnx_meta.build_contract(
        feature_mode="ohlcv_minmax", window_size=FIXTURE_WINDOW,
        channels=CHANNELS * (1 + len(lags)),
        horizon=5, threshold=0.005, wf_splits=5, seed=42,
        producer="generate_onnx_fixtures.py", fixed_zscore=True,
        scaler_ref=name + ".scaler.json", lags=lags, clip_sigma=clip_sigma,
    )
    onnx_meta.apply(model, mapping)
    return model


def _with_legacy_composed() -> onnx.ModelProto:
    """Pre-lag composed contract: no schema_version or lags in feature_spec."""
    model = _build(3)
    spec = {"channels": [{"kind": "price", "price": name}
                         for name in ("open", "high", "low", "close", "typical")]}
    mapping = onnx_meta.build_contract(
        feature_mode="composed_features", window_size=FIXTURE_WINDOW, channels=CHANNELS,
        horizon=5, threshold=0.005, wf_splits=5, seed=42,
        producer="generate_onnx_fixtures.py", feature_spec_json=json.dumps(spec, separators=(",", ":")),
    )
    onnx_meta.apply(model, mapping)
    return model


def _with_runtime_extra_class() -> onnx.ModelProto:
    """Advertise dynamic width but emit four values; the first three still sum to one."""
    model = _with_contract(3, "ohlcv_minmax")
    model.graph.node[-1].output[0] = "probabilities"
    model.graph.initializer.extend([
        helper.make_tensor("slice_start", TensorProto.INT64, [1], [0]),
        helper.make_tensor("slice_end", TensorProto.INT64, [1], [1]),
        helper.make_tensor("slice_axis", TensorProto.INT64, [1], [1]),
        helper.make_tensor("reduce_axes", TensorProto.INT64, [2], [0, 1]),
        helper.make_tensor("four", TensorProto.INT64, [], [4]),
        helper.make_tensor("unsqueeze_axes", TensorProto.INT64, [1], [0]),
    ])
    model.graph.node.extend([
        helper.make_node("Slice", ["probabilities", "slice_start", "slice_end", "slice_axis"], ["first"]),
        helper.make_node("Sub", ["first", "first"], ["zero"]),
        helper.make_node("Concat", ["probabilities", "zero"], ["wide"], axis=1),
        helper.make_node("ReduceSum", ["zero", "reduce_axes"], ["zero_sum"], keepdims=0),
        helper.make_node("Cast", ["zero_sum"], ["zero_int"], to=TensorProto.INT64),
        helper.make_node("Add", ["zero_int", "four"], ["dynamic_end"]),
        helper.make_node("Unsqueeze", ["dynamic_end", "unsqueeze_axes"], ["end_vector"]),
        helper.make_node("Slice", ["wide", "slice_start", "end_vector", "slice_axis"], ["output"]),
    ])
    width = model.graph.output[0].type.tensor_type.shape.dim[1]
    width.ClearField("dim_value")
    width.dim_param = "classes"
    onnx.checker.check_model(model)
    return model


def _with_runtime_extra_batch() -> onnx.ModelProto:
    """Advertise dynamic batch but return two rows for one input row."""
    model = _with_contract(3, "ohlcv_minmax")
    model.graph.node[-1].output[0] = "probabilities"
    model.graph.node.append(helper.make_node(
        "Concat", ["probabilities", "probabilities"], ["output"], axis=0))
    onnx.checker.check_model(model)
    return model


def _with_reordered_classes() -> onnx.ModelProto:
    """Return the same probabilities in Neutral,Up,Down order with matching metadata."""
    model = _with_contract(3, "ohlcv_minmax")
    model.graph.node[-1].output[0] = "probabilities"
    model.graph.initializer.append(helper.make_tensor("class_indices", TensorProto.INT64, [3], [2, 0, 1]))
    model.graph.node.append(helper.make_node("Gather", ["probabilities", "class_indices"], ["output"], axis=1))
    next(entry for entry in model.metadata_props if entry.key == "class_order").value = "Neutral,Up,Down"
    onnx.checker.check_model(model)
    return model


def _admission_fixtures() -> tuple[tuple[str, onnx.ModelProto], ...]:
    unknown_mode = _with_contract(3, "ohlcv_minmax")
    for entry in unknown_mode.metadata_props:
        if entry.key in ("feature_mode", "normalization"):
            entry.value = "unsupported_feature_mode"

    malformed_composed = _with_legacy_composed()
    next(entry for entry in malformed_composed.metadata_props
         if entry.key == "feature_spec").value = json.dumps({
             "channels": [{"kind": "price", "price": "open"}],
         }, separators=(",", ":"))

    duplicate = _with_contract(3, "ohlcv_minmax")
    next(entry for entry in duplicate.metadata_props if entry.key == "class_order").value = "Up,Up,Neutral"

    whitespace = _with_contract(3, "ohlcv_minmax")
    next(entry for entry in whitespace.metadata_props if entry.key == "class_order").value = "Up, Down,Neutral"

    fixed_batch_two = _with_contract(3, "ohlcv_minmax")
    for value in (fixed_batch_two.graph.input[0], fixed_batch_two.graph.output[0]):
        batch = value.type.tensor_type.shape.dim[0]
        batch.ClearField("dim_param")
        batch.dim_value = 2

    fixed_batch_one = _with_contract(3, "ohlcv_minmax")
    for value in (fixed_batch_one.graph.input[0], fixed_batch_one.graph.output[0]):
        batch = value.type.tensor_type.shape.dim[0]
        batch.ClearField("dim_param")
        batch.dim_value = 1

    double_input = _with_contract(3, "ohlcv_minmax")
    double_input.graph.input[0].type.tensor_type.elem_type = TensorProto.DOUBLE
    double_input.graph.node.insert(0, helper.make_node(
        "Cast", ["input"], ["float_input"], to=TensorProto.FLOAT))
    double_input.graph.node[1].input[0] = "float_input"

    double_output = _with_contract(3, "ohlcv_minmax")
    double_output.graph.node[-1].output[0] = "float_output"
    double_output.graph.node.append(helper.make_node(
        "Cast", ["float_output"], ["output"], to=TensorProto.DOUBLE))
    double_output.graph.output[0].type.tensor_type.elem_type = TensorProto.DOUBLE

    fixtures = (
        ("trend_predictor_unknown_feature_mode.onnx", unknown_mode),
        ("trend_predictor_invalid_composed_spec.onnx", malformed_composed),
        ("trend_predictor_duplicate_classes.onnx", duplicate),
        ("trend_predictor_whitespace_classes.onnx", whitespace),
        ("trend_predictor_fixed_batch_two.onnx", fixed_batch_two),
        ("trend_predictor_fixed_batch_one.onnx", fixed_batch_one),
        ("trend_predictor_double_input.onnx", double_input),
        ("trend_predictor_double_output.onnx", double_output),
    )
    for _, model in fixtures:
        onnx.checker.check_model(model)
    return fixtures


def _write_fixed_scaler_sidecar(path: Path, model: onnx.ModelProto) -> None:
    metadata = {entry.key: entry.value for entry in model.metadata_props}
    lags = tuple(json.loads(metadata["lags"]))
    clip_sigma = float(metadata["clip_sigma"])
    scaler = {
        "schema_version": fixed_scaler.SCHEMA_VERSION,
        "feature_mode": "ohlcv_minmax", "window_size": FIXTURE_WINDOW,
        "lags": list(lags), "clip_sigma": clip_sigma,
        "channel_identities": fixed_scaler.identities("ohlcv_minmax", None, lags),
        "statistics": [{"mean": 0.0, "std": 1.0, "min": 0.0, "max": 1000.0}
                         for _ in range(CHANNELS * (1 + len(lags)))],
    }
    fixed_scaler.write_sidecar(path, scaler, metadata)


def main() -> None:
    fixtures = (
        ("trend_predictor_ok.onnx", _build(3)),
        ("trend_predictor_badclass.onnx", _build(4)),
        ("trend_predictor_goodmeta.onnx", _with_contract(3, "ohlcv_minmax")),
        ("trend_predictor_goodmeta_variant.onnx", _with_shifted_contract()),
        ("trend_predictor_badmeta.onnx", _with_contract(3, "zscore")),
        ("trend_predictor_jointmeta.onnx", _with_contract(3, "zscore_joint")),
        ("trend_predictor_lrohlc.onnx", _with_contract(3, "log_return_ohlc", channels=4)),
        ("trend_predictor_regression.onnx", _with_legacy_regression_contract()),
        ("trend_predictor_regression_v2.onnx", _with_regression_contract()),
        ("trend_predictor_missing_scaler.onnx", _with_missing_scaler_reference()),
        ("trend_predictor_fixed_scaler.onnx", _with_fixed_scaler()),
        ("trend_predictor_fixed_lag1_clip3.onnx", _with_fixed_scaler(
            "trend_predictor_fixed_lag1_clip3.onnx", (1,), 3.0)),
        ("trend_predictor_fixed_lag2_clip3.onnx", _with_fixed_scaler(
            "trend_predictor_fixed_lag2_clip3.onnx", (2,), 3.0)),
        ("trend_predictor_fixed_lag1_clip4.onnx", _with_fixed_scaler(
            "trend_predictor_fixed_lag1_clip4.onnx", (1,), 4.0)),
        ("trend_predictor_legacy_composed.onnx", _with_legacy_composed()),
        ("trend_predictor_runtime_extra_class.onnx", _with_runtime_extra_class()),
        ("trend_predictor_runtime_extra_batch.onnx", _with_runtime_extra_batch()),
        ("trend_predictor_reordered_classes.onnx", _with_reordered_classes()),
    )
    for name, model in fixtures:
        path = HERE / name
        onnx.save(model, str(path))
        if name.startswith("trend_predictor_fixed_"):
            _write_fixed_scaler_sidecar(path, model)
        print(f"wrote {path} ({path.stat().st_size} bytes)")


if __name__ == "__main__":
    if sys.argv[1:] == ["--admission-fixtures"]:
        for name, model in _admission_fixtures():
            path = HERE / name
            onnx.save(model, str(path))
            print(f"wrote {path} ({path.stat().st_size} bytes)")
    else:
        main()
