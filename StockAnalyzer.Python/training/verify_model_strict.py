"""Strict ONNX contract and real-data diagnostic for classification and regression.

The legacy verify_model.py remains available for older, metadata-free models.
This command requires the current contract and an independent expected FeatureSpec
for composed models. It proves structural and runtime consistency, not accuracy.
"""

from __future__ import annotations

import argparse
import datetime
import json
import math
import sys
from pathlib import Path

import numpy as np
import onnxruntime as ort

sys.path.insert(0, str(Path(__file__).resolve().parent))
import dataset as ds  # noqa: E402
import feature_spec_strict  # noqa: E402
import onnx_meta  # noqa: E402
from verify_model import EQUIV_ATOL, SOFTMAX_SUM_TOL  # noqa: E402

INFERENCE_BATCH = 32


class ValidationFailure(Exception):
    """An input model, contract, or dataset fails a diagnostic requirement."""


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValidationFailure(message)


def _positive_int(value: object, label: str) -> int:
    require(type(value) is int and value > 0, f"{label} must be a positive static integer")
    return value


def _metadata_int(metadata: dict[str, str], key: str) -> int:
    raw = metadata[key]
    require(raw.isdecimal(), f"metadata {key} must be a positive integer")
    return _positive_int(int(raw), f"metadata {key}")


def _batch_dimension(value: object, label: str) -> bool:
    dynamic = value is None or isinstance(value, str) or value == -1
    require(dynamic or (type(value) is int and value == 1),
            f"{label} batch dimension must be dynamic or 1")
    return dynamic


def _graph(session: ort.InferenceSession) -> tuple[int, int, bool]:
    inputs, outputs = session.get_inputs(), session.get_outputs()
    require(len(inputs) == 1 and len(outputs) == 1, "graph must have exactly one input and one output")
    inp, out = inputs[0], outputs[0]
    require(inp.name == "input" and out.name == "output", "graph tensor names must be input/output")
    require(inp.type == "tensor(float)" and out.type == "tensor(float)",
            "graph input/output must be float32 tensors")
    require(len(inp.shape) == 3 and len(out.shape) == 2,
            "graph input rank must be 3 and output rank must be 2")
    dynamic = _batch_dimension(inp.shape[0], "input")
    _batch_dimension(out.shape[0], "output")
    window = _positive_int(inp.shape[1], "graph window")
    channels = _positive_int(inp.shape[2], "graph channels")
    _positive_int(out.shape[1], "graph output width")
    return window, channels, dynamic


def _metadata(session: ort.InferenceSession, window: int, channels: int,
              output_width: int) -> tuple[dict[str, str], str, str, int]:
    metadata = dict(session.get_modelmeta().custom_metadata_map)
    missing = set(onnx_meta.CONTRACT_KEYS) - set(metadata)
    require(not missing, f"metadata missing contract keys: {sorted(missing)}")
    require(metadata["model_contract_version"] == onnx_meta.MODEL_CONTRACT_VERSION,
            "metadata contract version is not current")
    mode = metadata["feature_mode"]
    require(mode in ds.FEATURE_MODES + (ds.COMPOSED_FEATURES_MODE,),
            f"metadata has unknown feature_mode {mode!r}")
    target = metadata["target_type"]
    require(target in onnx_meta.TARGET_TYPES, f"metadata has unknown target_type {target!r}")
    require(_metadata_int(metadata, "window_size") == window, "metadata window disagrees with graph")
    require(_metadata_int(metadata, "channels") == channels, "metadata channels disagree with graph")
    horizon = _metadata_int(metadata, "prediction_horizon")
    require(output_width == (len(ds.CLASS_LABELS) if target == "classification" else 1),
            f"graph output width disagrees with {target} target")
    if target == "classification":
        require(metadata["class_order"] == ",".join(ds.CLASS_LABELS),
                "metadata class_order disagrees with dataset class order")
    try:
        threshold = float(metadata["neutral_threshold"])
    except ValueError as exc:
        raise ValidationFailure("metadata neutral_threshold must be numeric") from exc
    require(math.isfinite(threshold), "metadata neutral_threshold must be finite")
    ranges = [metadata[key] for key in ds.DATE_RANGE_KEYS]
    require(not any(ranges) or all(ranges), "metadata date ranges must be all populated or all empty")
    if all(ranges):
        try:
            train_start, train_end, val_start, val_end = map(datetime.date.fromisoformat, ranges)
        except ValueError as exc:
            raise ValidationFailure("metadata date range is not an ISO date") from exc
        require(train_start <= train_end and val_start <= val_end,
                "metadata train or validation date range is reversed")
    return metadata, mode, target, horizon


def _spec_and_exports(args: argparse.Namespace, metadata: dict[str, str], mode: str,
                      channels: int) -> tuple[dict | None, dict[str, str] | None]:
    if mode != ds.COMPOSED_FEATURES_MODE:
        require(args.feature_spec is None and args.indicator_channel_export_paths is None,
                "feature-spec and indicator exports are only valid for composed_features")
        require("feature_spec" not in metadata, "fixed-mode metadata must not contain feature_spec")
        require(ds.feature_channels(mode) == channels, "graph channels disagree with fixed feature_mode")
        return None, None

    require(args.feature_spec is not None, "composed_features requires --feature-spec")
    require("feature_spec" in metadata, "composed metadata is missing feature_spec")
    expected, expected_json = feature_spec_strict.parse_feature_spec(args.feature_spec)
    embedded, embedded_json = feature_spec_strict.parse_feature_spec(metadata["feature_spec"])
    require(expected_json == embedded_json,
            "embedded feature_spec differs from independent expected feature_spec")
    require(ds.feature_channels(mode, expected) == channels,
            "graph channels disagree with feature_spec channel count")

    has_indicator = any(ch["kind"] == "indicator" for ch in embedded["channels"])
    if args.indicator_channel_export_paths is None:
        require(not has_indicator, "Indicator channel requires --indicator-channel-export-paths")
        return expected, None
    require(has_indicator, "indicator exports are invalid for a Price-only feature_spec")
    try:
        exports = json.loads(args.indicator_channel_export_paths)
    except json.JSONDecodeError as exc:
        raise ValidationFailure(f"indicator exports JSON is invalid: {exc}") from exc
    require(isinstance(exports, dict) and exports and
            all(isinstance(k, str) and k and isinstance(v, str) and v.strip()
                for k, v in exports.items()),
            "indicator exports must be a non-empty symbol-to-path object")
    return expected, exports


def _run_outputs(session: ort.InferenceSession, x: np.ndarray, target: str,
                 dynamic_batch: bool) -> float | None:
    checked = 0
    for start in range(0, len(x), INFERENCE_BATCH if dynamic_batch else 1):
        chunk = x[start:start + (INFERENCE_BATCH if dynamic_batch else 1)]
        (output,) = session.run(["output"], {"input": chunk})
        require(isinstance(output, np.ndarray) and output.shape ==
                (len(chunk), len(ds.CLASS_LABELS) if target == "classification" else 1),
                "runtime output shape disagrees with graph contract")
        require(np.isfinite(output).all(), "runtime output contains NaN or Infinity")
        if target == "classification":
            require(np.all(output >= -EQUIV_ATOL) and np.all(output <= 1 + EQUIV_ATOL),
                    "classification output is outside probability bounds")
            require(np.all(np.abs(output.sum(axis=1) - 1) <= SOFTMAX_SUM_TOL),
                    "classification probability rows do not sum to one")
        checked += len(chunk)
    require(checked == len(x), "runtime did not check every sample")

    if not dynamic_batch:
        return None
    sample = x[:min(INFERENCE_BATCH, len(x))]
    (batched,) = session.run(["output"], {"input": sample})
    singles = np.stack([session.run(["output"], {"input": row[None, ...]})[0][0]
                        for row in sample])
    require(np.isfinite(batched).all() and np.isfinite(singles).all(),
            "batch comparison contains NaN or Infinity")
    delta = float(np.max(np.abs(batched - singles)))
    require(delta < EQUIV_ATOL, f"batch/single maximum absolute difference {delta} >= {EQUIV_ATOL}")
    return delta


def run(args: argparse.Namespace) -> int:
    require(args.model.is_file(), f"model not found: {args.model}")
    require(args.data_dir.is_dir(), f"data directory not found: {args.data_dir}")
    session = ort.InferenceSession(str(args.model), providers=["CPUExecutionProvider"])
    window, channels, dynamic = _graph(session)
    print("graph: PASS")
    output_width = session.get_outputs()[0].shape[1]
    metadata, mode, target, horizon = _metadata(session, window, channels, output_width)
    print("metadata: PASS")
    spec, exports = _spec_and_exports(args, metadata, mode, channels)
    print("shape/spec: PASS")
    symbols, dates = ds.load_parquet_dir(args.data_dir, return_dates=True)
    require(bool(symbols), "dataset has no usable symbols")
    if exports is not None:
        require(all(symbol in exports for symbol in symbols),
                "indicator export path missing for a loaded symbol")
    x, _ = ds.build_dataset_multi(
        symbols, mode, window=window, horizon=horizon,
        threshold=float(metadata["neutral_threshold"]), feature_spec=spec,
        target_type=target, dates=dates, indicator_export_paths=exports,
    )
    require(len(x) > 0, "dataset produced zero valid samples")
    require(x.dtype == np.float32 and x.shape[1:] == (window, channels),
            "dataset tensor shape/type disagrees with graph")
    print(f"dataset: PASS ({len(symbols)} symbols, {len(x)} samples)")
    require(np.isfinite(x).all(), "float32 dataset tensor contains NaN or Infinity")
    print("finite input: PASS")
    delta = _run_outputs(session, x, target, dynamic)
    print(f"inference: PASS ({len(x)} samples, {target})")
    print("batch equivalence: N/A (fixed batch=1)" if delta is None else
          f"batch equivalence: PASS (max absolute difference {delta:.3g})")
    print(f"result: PASS model={args.model} mode={mode} target={target} W={window} C={channels} N={len(x)}")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", type=Path, required=True)
    parser.add_argument("--data-dir", type=Path, required=True)
    parser.add_argument("--feature-spec", type=str)
    parser.add_argument("--indicator-channel-export-paths", type=str)
    args = parser.parse_args(argv)
    try:
        return run(args)
    except Exception as exc:
        print(f"result: FAIL {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
