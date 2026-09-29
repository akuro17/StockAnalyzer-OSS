"""Shared composed-feature classification path for LightGBM and TensorFlow.

Fixed feature modes retain their existing trainer paths. This module owns only
the new composed input, dataset preparation, verification and publication flow.
"""

from __future__ import annotations

import argparse
import json
import math
import os
import shutil
import uuid
from dataclasses import dataclass
from pathlib import Path
from typing import Callable

import numpy as np

import dataset as ds
import feature_spec_strict
import fixed_scaler
import metrics


@dataclass(frozen=True)
class PreparedComposedData:
    symbols: dict[str, np.ndarray]
    dates: dict[str, np.ndarray]
    feature_spec: dict
    indicator_export_paths: dict[str, str] | None
    channels: int
    x_train: np.ndarray
    y_train: np.ndarray
    x_validation: np.ndarray
    y_validation: np.ndarray
    date_ranges: dict[str, str]


def validate_flags(args: argparse.Namespace) -> None:
    """Validate only the new CLI flags; leave the five fixed modes unchanged."""
    if args.feature_mode != ds.COMPOSED_FEATURES_MODE:
        if args.feature_spec is not None or args.indicator_channel_export_paths is not None:
            raise ValueError("--feature-spec and --indicator-channel-export-paths require composed_features")
        return

    if args.feature_spec is None:
        raise ValueError("composed_features requires --feature-spec")
    spec, _ = feature_spec_strict.parse_feature_spec(args.feature_spec)
    lags = fixed_scaler.trainer_lags(args, spec)
    if not args.fixed_zscore and (lags or args.clip_sigma is not None):
        raise ValueError("clipping and lags require --fixed-zscore")
    if args.fixed_zscore and any(ch.get("normalization", "none") != "none" for ch in spec["channels"]):
        raise ValueError("fixed z-score cannot follow per-window channel normalization")
    for name in ("window", "horizon"):
        value = getattr(args, name)
        if type(value) is not int or value <= 0:
            raise ValueError(f"--{name} must be a positive integer")
    if type(args.wf_splits) is not int or args.wf_splits < 2:
        raise ValueError("--wf-splits must be an integer of at least 2")
    if args.gap is not None and (type(args.gap) is not int or args.gap < 0):
        raise ValueError("--gap must be a non-negative integer")
    if not math.isfinite(args.threshold):
        raise ValueError("--threshold must be finite")
    if args.max_symbols is not None and (type(args.max_symbols) is not int or args.max_symbols <= 0):
        raise ValueError("--max-symbols must be a positive integer")
    if type(args.opset) is not int or args.opset <= 0:
        raise ValueError("--opset must be a positive integer")
    if args.no_verify:
        raise ValueError("--no-verify is unavailable for composed_features; staged verification is required")

    exports = None
    if args.indicator_channel_export_paths is not None:
        try:
            exports = json.loads(args.indicator_channel_export_paths)
        except json.JSONDecodeError as exc:
            raise ValueError(f"invalid --indicator-channel-export-paths JSON: {exc}") from exc
        if not isinstance(exports, dict) or any(
            not isinstance(symbol, str) or not symbol.strip() or
            not isinstance(path, str) or not path.strip()
            for symbol, path in exports.items()
        ):
            raise ValueError("indicator exports must be a symbol-to-nonempty-path JSON object")
    if any(channel["kind"] == "indicator" for channel in spec["channels"]) and not exports:
        raise ValueError("Indicator channels require --indicator-channel-export-paths")

    args.composed_spec = spec
    args.composed_export_paths = exports


def prepare(args: argparse.Namespace) -> PreparedComposedData:
    """Build the same chronological split and date spans as the PyTorch path."""
    spec = args.composed_spec
    exports = args.composed_export_paths
    symbols, dates = ds.load_parquet_dir(args.data_dir, return_dates=True)
    if args.max_symbols is not None:
        symbols = dict(list(symbols.items())[:args.max_symbols])
    dates = {symbol: dates[symbol] for symbol in symbols}
    if not symbols:
        raise ValueError(f"No parquet data under {args.data_dir}")
    if exports is not None and any(channel["kind"] == "indicator" for channel in spec["channels"]):
        missing = [symbol for symbol in symbols if symbol not in exports]
        if missing:
            raise ValueError(f"indicator export path missing for symbol(s) {missing}")

    lags = fixed_scaler.trainer_lags(args, spec)
    channels = ds.feature_channels(args.feature_mode, feature_spec=spec) * (1 + len(lags))
    split_args = dict(
        feature_mode=args.feature_mode, window=args.window, horizon=args.horizon,
        threshold=args.threshold, n_splits=args.wf_splits, gap=args.gap,
        feature_spec=spec, target_type="classification", dates=dates,
        indicator_export_paths=exports,
        outer_fold_index=args.outer_fold_index,
        fixed_zscore=args.fixed_zscore, lags=lags,
    )
    x_tr, y_tr, x_va, y_va = ds.split_symbols_chronological(symbols, **split_args)
    if not len(x_tr) or not len(x_va):
        raise ValueError("Empty train or val split; check --window/--horizon/--wf-splits vs data length")
    if x_tr.shape[2] != channels or x_va.shape[2] != channels:
        raise ValueError("split tensor channels disagree with FeatureSpec")
    if not np.isfinite(x_tr).all() or not np.isfinite(x_va).all():
        raise ValueError("composed training tensor contains NaN or Infinity after float32 conversion")
    date_ranges = ds.train_val_date_ranges(symbols, dates, **{
        key: value for key, value in split_args.items() if key != "dates"
    })
    return PreparedComposedData(
        symbols, dates, spec, exports, channels, x_tr, y_tr, x_va, y_va, date_ranges,
    )


def verify_export(path: Path, prepared: PreparedComposedData, spec_json: str,
                  native_predict: Callable[[np.ndarray], np.ndarray], atol: float,
                  *, scaler: dict | None = None) -> float:
    """Check strict contract, real validation probabilities and native/ORT parity."""
    import onnxruntime as ort
    import verify_model_strict as strict

    session = ort.InferenceSession(str(path), providers=["CPUExecutionProvider"])
    window, channels, dynamic = strict._graph(session)
    metadata, mode, target, _ = strict._metadata(
        session, window, channels, session.get_outputs()[0].shape[1])
    flags = argparse.Namespace(
        feature_spec=spec_json,
        indicator_channel_export_paths=(
            json.dumps(prepared.indicator_export_paths)
            if prepared.indicator_export_paths is not None and any(
                channel["kind"] == "indicator" for channel in prepared.feature_spec["channels"]
            ) else None
        ),
    )
    strict._spec_and_exports(flags, metadata, mode, channels)
    if mode != ds.COMPOSED_FEATURES_MODE or target != "classification" or not dynamic:
        raise ValueError("composed export must have a dynamic-batch classification contract")
    sample = prepared.x_validation[:min(strict.INFERENCE_BATCH, len(prepared.x_validation))]
    if scaler is not None:
        sample = fixed_scaler.transform(sample, scaler)
    if sample.shape[1:] != (window, channels) or not np.isfinite(sample).all():
        raise ValueError("validation tensor disagrees with exported input contract")
    strict._run_outputs(session, sample, target, dynamic)
    (actual,) = session.run(["output"], {"input": sample})
    expected = np.asarray(native_predict(sample), dtype=np.float64)
    if expected.shape != actual.shape or not np.isfinite(expected).all():
        raise ValueError("native classifier returned invalid validation probabilities")
    delta = float(np.max(np.abs(expected - actual)))
    if not delta < atol:
        raise ValueError(f"native/ORT max absolute difference {delta:.3g} >= {atol:.3g}")
    print(f"composed ONNX verify: PASS (native/ORT max absolute difference {delta:.3g})")
    return delta


def publish_verified(out_path: Path, report: dict, export: Callable[[Path], None],
                     verify: Callable[[Path], None]) -> None:
    """Stage model and metrics, then publish after all validation succeeds."""
    final_model = out_path.resolve()
    final_model.parent.mkdir(parents=True, exist_ok=True)
    stage = final_model.parent / f".{final_model.stem}_stage_{uuid.uuid4().hex}"
    stage.mkdir()
    staged_model = stage / final_model.name
    final_metrics = final_model.with_name(final_model.name + ".metrics.json")
    final_scaler = final_model.with_name(final_model.name + ".scaler.json")
    metrics_published = False
    old_metrics = stage / "previous.metrics.json"
    old_model = stage / "previous.onnx"
    old_scaler = stage / "previous.scaler.json"
    had_metrics = final_metrics.exists()
    try:
        export(staged_model)
        staged_scaler = staged_model.with_name(staged_model.name + ".scaler.json")
        staged_metrics = Path(metrics.write_report(report, staged_model))
        verify(staged_model)
        if had_metrics:
            shutil.copy2(final_metrics, old_metrics)
        if final_model.exists():
            shutil.copy2(final_model, old_model)
        if final_scaler.exists():
            shutil.copy2(final_scaler, old_scaler)
        try:
            os.replace(staged_metrics, final_metrics)
            metrics_published = True
            os.replace(staged_model, final_model)
            if staged_scaler.exists():
                os.replace(staged_scaler, final_scaler)
        except OSError:
            if old_model.exists():
                os.replace(old_model, final_model)
            else:
                final_model.unlink(missing_ok=True)
            if metrics_published:
                if had_metrics:
                    os.replace(old_metrics, final_metrics)
                else:
                    final_metrics.unlink()
            if old_scaler.exists():
                os.replace(old_scaler, final_scaler)
            else:
                final_scaler.unlink(missing_ok=True)
            raise
    finally:
        if stage.resolve().parent != final_model.parent.resolve() or not stage.name.startswith(
            f".{final_model.stem}_stage_"
        ):
            raise RuntimeError("refusing to remove a staging directory outside the output parent")
        shutil.rmtree(stage)
