"""Train a LightGBM (GBDT) trend predictor and export it to ONNX with a 3D input.

LightGBM is a 2D tabular model: it trains on flattened windows ``[N, window*channels]``.
To keep the ONNX file contract-compatible with the C# inference engine (which always
feeds ``[batch, window, channels]``), a ``Reshape`` node is prepended to the converted
graph so the exported model accepts the 3D tensor directly:

    input  : float32  [batch, window, channels]   node name "input"
      -> Reshape -> [batch, window*channels]
      -> LightGBM TreeEnsembleClassifier (zipmap disabled)
    output : float32  [batch, 3]                   node name "output"  (class probabilities; order Up, Down, Neutral)

Feature preprocessing / labeling come from :mod:`dataset` (the C# ``MLDataProcessor`` mirror).

Examples
--------
    python train_lightgbm.py --smoke
    python train_lightgbm.py --feature-mode zscore --n-estimators 400 --learning-rate 0.03
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path
from typing import Tuple

import numpy as np
import onnx
from onnx import TensorProto, helper

sys.path.insert(0, str(Path(__file__).resolve().parent))
import dataset as ds  # noqa: E402
import metrics  # noqa: E402
import onnx_meta  # noqa: E402

# --- Defaults (named; no magic numbers) -------------------------------------

DEFAULT_N_ESTIMATORS: int = 200
DEFAULT_LEARNING_RATE: float = 0.05
DEFAULT_NUM_LEAVES: int = 31
DEFAULT_MAX_DEPTH: int = -1
DEFAULT_MIN_CHILD_SAMPLES: int = 20
DEFAULT_SUBSAMPLE: float = 0.9
DEFAULT_COLSAMPLE: float = 0.9
DEFAULT_SEED: int = 42
DEFAULT_OPSET: int = 17
# onnxmltools' LightGBM converter supports at most ai.onnx opset 15; the exported
# graph is clamped to this (opset 15 loads on C# ORT 1.24.3 and onnxruntime 1.29 alike).
LGBM_CONVERT_OPSET_CEILING: int = 15
DEFAULT_WF_SPLITS: int = ds.DEFAULT_WF_SPLITS  # SSoT: dataset.DEFAULT_WF_SPLITS
DEFAULT_EARLY_STOPPING: int = 30
ONNX_ATOL: float = 1e-4  # lightgbm-vs-onnxruntime probability tolerance

NUM_CLASSES: int = len(ds.CLASS_LABELS)  # mirrors PredictionSettings.ClassLabels length
INPUT_NAME: str = "input"
OUTPUT_NAME: str = "output"
_FLAT_INPUT_NAME: str = "flat_features"  # internal 2D tensor between Reshape and the tree ensemble
DEFAULT_OUT: Path = Path(__file__).resolve().parent / "artifacts" / "trend_predictor_lgbm.onnx"

SMOKE_MAX_SYMBOLS: int = 8
SMOKE_N_ESTIMATORS: int = 40


# --- Training -----------------------------------------------------------------


def train_booster(args: argparse.Namespace):
    import lightgbm as lgb

    symbols, dates = ds.load_parquet_dir(args.data_dir, return_dates=True)
    if args.max_symbols is not None:
        symbols = dict(list(symbols.items())[: args.max_symbols])
    dates = {k: dates[k] for k in symbols}
    if not symbols:
        raise SystemExit(f"No parquet data under {args.data_dir}")

    # Per-symbol chronological split then pool: validation is always time-after-train
    # within each symbol, with a purge gap of window + horizon - 1 bars.
    x_tr_3d, y_tr, x_va_3d, y_va = ds.split_symbols_chronological(
        symbols, args.feature_mode, window=args.window,
        horizon=args.horizon, threshold=args.threshold, n_splits=args.wf_splits,
    )
    if x_tr_3d.shape[0] == 0 or x_va_3d.shape[0] == 0:
        raise SystemExit("Empty train or val split; check --window/--horizon/--wf-splits vs data length.")

    # Calendar spans of the same split, for the model-contract provenance metadata.
    date_ranges = ds.train_val_date_ranges(
        symbols, dates, feature_mode=args.feature_mode, window=args.window,
        horizon=args.horizon, threshold=args.threshold, n_splits=args.wf_splits,
    )

    channels = ds.feature_channels(args.feature_mode)
    flat = args.window * channels
    x_tr = x_tr_3d.reshape(x_tr_3d.shape[0], flat).astype(np.float32)
    x_va = x_va_3d.reshape(x_va_3d.shape[0], flat).astype(np.float32)

    model = lgb.LGBMClassifier(
        objective="multiclass",
        num_class=NUM_CLASSES,
        n_estimators=args.n_estimators,
        learning_rate=args.learning_rate,
        num_leaves=args.num_leaves,
        max_depth=args.max_depth,
        min_child_samples=args.min_child_samples,
        subsample=args.subsample,
        subsample_freq=1,
        colsample_bytree=args.colsample_bytree,
        class_weight="balanced",
        random_state=args.seed,
        n_jobs=-1,
        verbose=-1,
    )
    print(
        f"feature_mode={args.feature_mode} channels={channels} flat={flat} "
        f"train={len(x_tr)} val={len(x_va)} symbols={len(symbols)}"
    )
    model.fit(
        x_tr, y_tr,
        eval_set=[(x_va, y_va)],
        eval_metric="multi_logloss",
        callbacks=[lgb.early_stopping(args.early_stopping, verbose=False), lgb.log_evaluation(0)],
    )
    val_acc = float((model.predict(x_va) == y_va).mean())
    print(f"validation accuracy={val_acc:.3f}  best_iteration={model.best_iteration_}")

    val_probs = np.asarray(model.predict_proba(x_va), dtype=np.float64)
    report = metrics.classification_report_dict(
        y_va, val_probs.argmax(axis=1), probs=val_probs
    )
    print(metrics.format_report(report))
    return model, channels, flat, report, date_ranges


# --- Export (LightGBM 2D graph + prepended Reshape) -----------------------


def _convert_lightgbm_2d(model, flat: int, opset: int) -> onnx.ModelProto:
    from onnxmltools.convert import convert_lightgbm
    from onnxmltools.convert.common.data_types import FloatTensorType

    convert_opset = min(opset, LGBM_CONVERT_OPSET_CEILING)
    onx = convert_lightgbm(
        model,
        initial_types=[(_FLAT_INPUT_NAME, FloatTensorType([None, flat]))],
        target_opset=convert_opset,
        zipmap=False,  # emit a plain [N, num_class] float tensor, not a sequence of maps
    )
    if convert_opset != opset:
        print(f"note: LightGBM ONNX converted at opset {convert_opset} (onnxmltools ceiling); "
              f"requested {opset}")
    return onx


def _probabilities_output_name(model_proto: onnx.ModelProto) -> str:
    names = [o.name for o in model_proto.graph.output]
    for cand in ("probabilities", "output_probability", "probability_tensor"):
        if cand in names:
            return cand
    # fall back to the first float output that is not the argmax label
    for o in model_proto.graph.output:
        if o.type.tensor_type.elem_type == TensorProto.FLOAT:
            return o.name
    raise RuntimeError(f"Could not locate the probabilities output among {names}")


def build_3d_model(model, flat: int, window: int, channels: int, opset: int) -> onnx.ModelProto:
    """Wrap the 2D LightGBM ONNX with a Reshape so it accepts [batch, window, channels]."""
    inner = _convert_lightgbm_2d(model, flat, opset)
    prob_name = _probabilities_output_name(inner)

    graph = inner.graph
    # New 3D entry tensor + a constant [-1, flat] shape for the Reshape.
    input_3d = helper.make_tensor_value_info(INPUT_NAME, TensorProto.FLOAT, ["batch", window, channels])
    shape_init = helper.make_tensor("reshape_to_2d", TensorProto.INT64, [2], np.array([-1, flat], dtype=np.int64))
    reshape_node = helper.make_node("Reshape", [INPUT_NAME, "reshape_to_2d"], [_FLAT_INPUT_NAME], name="flatten_window")

    new_nodes = [reshape_node] + list(graph.node)
    new_initializers = list(graph.initializer) + [shape_init]

    # Keep only the probabilities output, renamed to the contract name. Its original
    # value-info is discarded and replaced by out_vi; we only assert the output exists.
    if prob_name not in {o.name for o in graph.output}:
        raise ValueError(f"probabilities output {prob_name!r} not found in graph outputs")
    out_vi = helper.make_tensor_value_info(OUTPUT_NAME, TensorProto.FLOAT, ["batch", NUM_CLASSES])
    for node in new_nodes:
        node.output[:] = [OUTPUT_NAME if n == prob_name else n for n in node.output]
        node.input[:] = [OUTPUT_NAME if n == prob_name else n for n in node.input]

    new_graph = helper.make_graph(
        new_nodes,
        graph.name,
        [input_3d],
        [out_vi],
        initializer=new_initializers,
        value_info=list(graph.value_info),
    )
    wrapped = helper.make_model(
        new_graph,
        opset_imports=list(inner.opset_import),
        ir_version=inner.ir_version,
    )
    wrapped.producer_name = "train_lightgbm.py"
    onnx.checker.check_model(wrapped)
    return wrapped


def export_onnx(
    model, flat: int, window: int, channels: int, out_path: Path, opset: int,
    metadata: dict | None = None,
) -> None:
    out_path.parent.mkdir(parents=True, exist_ok=True)
    wrapped = build_3d_model(model, flat, window, channels, opset)
    if metadata is not None:
        onnx_meta.apply(wrapped, metadata)
    onnx.save(wrapped, str(out_path))
    actual_opset = next((op.version for op in wrapped.opset_import if op.domain in ("", "ai.onnx")), opset)
    contract_note = f", contract {len(metadata)} keys" if metadata is not None else ""
    print(f"exported {out_path}  (LightGBM 2D + prepended Reshape, ai.onnx opset {actual_opset}{contract_note})")


def verify_onnx(out_path: Path, model, flat: int, window: int, channels: int) -> None:
    import onnxruntime as ort

    session = ort.InferenceSession(str(out_path), providers=["CPUExecutionProvider"])
    in_meta = session.get_inputs()[0]
    out_meta = session.get_outputs()[0]
    assert in_meta.name == INPUT_NAME, in_meta.name
    assert out_meta.name == OUTPUT_NAME, out_meta.name
    assert len(in_meta.shape) == 3, in_meta.shape
    assert len(out_meta.shape) == 2, out_meta.shape
    if isinstance(in_meta.shape[2], int):
        assert in_meta.shape[2] == channels, in_meta.shape
    if isinstance(out_meta.shape[1], int):
        assert out_meta.shape[1] == NUM_CLASSES, out_meta.shape

    x = np.random.randn(5, window, channels).astype(np.float32)
    (probs,) = session.run([OUTPUT_NAME], {INPUT_NAME: x})
    assert probs.shape == (5, NUM_CLASSES), probs.shape
    assert np.isfinite(probs).all()
    assert (probs >= -ONNX_ATOL).all() and (probs <= 1.0 + ONNX_ATOL).all()
    assert np.allclose(probs.sum(axis=1), 1.0, atol=ONNX_ATOL), probs.sum(axis=1)

    ref = model.predict_proba(x.reshape(x.shape[0], flat).astype(np.float32))
    max_delta = float(np.max(np.abs(ref - probs)))
    assert max_delta < ONNX_ATOL, f"lightgbm vs onnxruntime delta {max_delta:.2e} >= {ONNX_ATOL:.0e}"
    print(f"onnx verify: OK  (lightgbm-vs-ort max delta {max_delta:.2e}, Reshape wrapper OK, dynamic batch OK)")


# --- CLI --------------------------------------------------------------------


def _parse_args(argv) -> argparse.Namespace:
    p = argparse.ArgumentParser(description="Train a LightGBM trend predictor and export a 3D-input ONNX.")
    p.add_argument("--data-dir", type=Path, default=ds.DEFAULT_DATA_DIR)
    p.add_argument("--feature-mode", choices=ds.FEATURE_MODES, default="ohlcv_minmax")
    p.add_argument("--window", type=int, default=ds.DEFAULT_WINDOW)
    p.add_argument("--horizon", type=int, default=ds.DEFAULT_HORIZON)
    p.add_argument("--threshold", type=float, default=ds.DEFAULT_THRESHOLD)
    p.add_argument("--n-estimators", type=int, default=DEFAULT_N_ESTIMATORS)
    p.add_argument("--learning-rate", type=float, default=DEFAULT_LEARNING_RATE)
    p.add_argument("--num-leaves", type=int, default=DEFAULT_NUM_LEAVES)
    p.add_argument("--max-depth", type=int, default=DEFAULT_MAX_DEPTH)
    p.add_argument("--min-child-samples", type=int, default=DEFAULT_MIN_CHILD_SAMPLES)
    p.add_argument("--subsample", type=float, default=DEFAULT_SUBSAMPLE)
    p.add_argument("--colsample-bytree", type=float, default=DEFAULT_COLSAMPLE)
    p.add_argument("--early-stopping", type=int, default=DEFAULT_EARLY_STOPPING)
    p.add_argument("--wf-splits", type=int, default=DEFAULT_WF_SPLITS)
    p.add_argument("--seed", type=int, default=DEFAULT_SEED)
    p.add_argument("--opset", type=int, default=DEFAULT_OPSET)
    p.add_argument("--max-symbols", type=int, default=None)
    p.add_argument("--out", type=Path, default=DEFAULT_OUT)
    p.add_argument("--price-adjustment", default=onnx_meta.DEFAULT_PRICE_ADJUSTMENT,
                   help="Value embedded as metadata_props.price_adjustment (default: adjusted).")
    p.add_argument("--no-verify", action="store_true")
    p.add_argument("--smoke", action="store_true",
                   help=f"Fast run: --max-symbols {SMOKE_MAX_SYMBOLS} --n-estimators {SMOKE_N_ESTIMATORS}.")
    args = p.parse_args(argv)
    if args.smoke:
        if args.max_symbols is None:
            args.max_symbols = SMOKE_MAX_SYMBOLS
        args.n_estimators = min(args.n_estimators, SMOKE_N_ESTIMATORS)
    return args


def main(argv) -> int:
    args = _parse_args(argv)
    model, channels, flat, report, date_ranges = train_booster(args)
    contract = onnx_meta.build_contract(
        feature_mode=args.feature_mode, window_size=args.window, channels=channels,
        horizon=args.horizon, threshold=args.threshold, wf_splits=args.wf_splits,
        seed=args.seed, producer="train_lightgbm.py arch=gbdt",
        price_adjustment=args.price_adjustment, date_ranges=date_ranges,
    )
    export_onnx(model, flat, args.window, channels, args.out, args.opset, contract)
    print(f"wrote {metrics.write_report(report, args.out)}")
    if not args.no_verify:
        verify_onnx(args.out, model, flat, args.window, channels)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
