"""Train a Keras (LSTM / GRU / 1D-CNN) trend predictor and export it to ONNX via tf2onnx.

The exported graph matches the C# inference contract
(``StockAnalyzer.Core.Services.PredictionService``):

    input  : float32  [batch, window, channels]   node name "input"
    output : float32  [batch, 3]                   node name "output"  (softmax probabilities; class order Up, Down, Neutral)

Feature preprocessing / labeling come from :mod:`dataset` (the C# ``MLDataProcessor``
mirror). Keras models emit softmax directly, so no export wrapper is needed.

Examples
--------
    python train_tensorflow.py --smoke
    python train_tensorflow.py --arch gru --feature-mode zscore --epochs 30
"""

from __future__ import annotations

import argparse
import os
import sys
import tempfile
from pathlib import Path
from typing import Tuple

import numpy as np

# Keep TensorFlow quiet and deterministic-ish before it is imported.
os.environ.setdefault("TF_CPP_MIN_LOG_LEVEL", "2")

import tensorflow as tf  # noqa: E402
import tf2onnx  # noqa: E402
import onnx  # noqa: E402

sys.path.insert(0, str(Path(__file__).resolve().parent))
import dataset as ds  # noqa: E402
import fixed_scaler  # noqa: E402
import composed_training  # noqa: E402
import metrics  # noqa: E402
import onnx_meta  # noqa: E402

# --- Defaults (named; no magic numbers) -------------------------------------

DEFAULT_ARCH: str = "lstm"
ARCHES: Tuple[str, str, str] = ("lstm", "gru", "cnn")
DEFAULT_HIDDEN: int = 64
DEFAULT_DENSE: int = 32
DEFAULT_DROPOUT: float = 0.2
DEFAULT_EPOCHS: int = 15
DEFAULT_BATCH: int = 256
DEFAULT_LR: float = 1e-3
DEFAULT_PATIENCE: int = 4
DEFAULT_SEED: int = 42
DEFAULT_OPSET: int = 17
DEFAULT_WF_SPLITS: int = ds.DEFAULT_WF_SPLITS  # SSoT: dataset.DEFAULT_WF_SPLITS
CNN_FILTERS_1: int = 32
CNN_FILTERS_2: int = 64
CNN_KERNEL: int = 3
ONNX_ATOL: float = 1e-4  # keras-vs-onnxruntime float32 tolerance

NUM_CLASSES: int = len(ds.CLASS_LABELS)  # mirrors PredictionSettings.ClassLabels length
INPUT_NAME: str = "input"
OUTPUT_NAME: str = "output"
DEFAULT_OUT: Path = Path(__file__).resolve().parent / "artifacts" / "trend_predictor_tf.onnx"

SMOKE_MAX_SYMBOLS: int = 8
SMOKE_EPOCHS: int = 3


# --- Model -----------------------------------------------------------------------


def build_model(arch: str, channels: int, window: int, hidden: int, dense: int, dropout: float, lr: float) -> tf.keras.Model:
    inp = tf.keras.layers.Input(shape=(window, channels), name=INPUT_NAME, dtype=tf.float32)
    if arch == "lstm":
        x = tf.keras.layers.LSTM(hidden, return_sequences=False)(inp)
    elif arch == "gru":
        x = tf.keras.layers.GRU(hidden, return_sequences=False)(inp)
    elif arch == "cnn":
        x = tf.keras.layers.Conv1D(CNN_FILTERS_1, CNN_KERNEL, padding="same", activation="relu")(inp)
        x = tf.keras.layers.Conv1D(CNN_FILTERS_2, CNN_KERNEL, padding="same", activation="relu")(x)
        x = tf.keras.layers.GlobalAveragePooling1D()(x)
    else:
        raise ValueError(f"Unknown arch {arch!r}; expected one of {ARCHES}")

    x = tf.keras.layers.Dropout(dropout)(x)
    x = tf.keras.layers.Dense(dense, activation="relu")(x)
    out = tf.keras.layers.Dense(NUM_CLASSES, activation="softmax", name=OUTPUT_NAME)(x)

    model = tf.keras.Model(inp, out, name=f"trend_predictor_{arch}")
    model.compile(
        optimizer=tf.keras.optimizers.Adam(learning_rate=lr),
        loss="sparse_categorical_crossentropy",
        metrics=["accuracy"],
    )
    return model


# --- Training -----------------------------------------------------------------


def _class_weight(y: np.ndarray) -> dict:
    counts = np.array([(y == c).sum() for c in range(NUM_CLASSES)], dtype=np.float64)
    counts[counts == 0] = 1.0
    w = counts.sum() / (NUM_CLASSES * counts)
    return {c: float(w[c]) for c in range(NUM_CLASSES)}


def train_model(
    args: argparse.Namespace, prepared: composed_training.PreparedComposedData | None = None,
) -> Tuple[tf.keras.Model, int, dict, dict]:
    tf.keras.utils.set_random_seed(args.seed)

    if prepared is None:
        symbols, dates = ds.load_parquet_dir(args.data_dir, return_dates=True)
        if args.max_symbols is not None:
            symbols = dict(list(symbols.items())[: args.max_symbols])
        dates = {k: dates[k] for k in symbols}
        if not symbols:
            raise SystemExit(f"No parquet data under {args.data_dir}")

        x_tr, y_tr, x_va, y_va = ds.split_symbols_chronological(
            symbols, args.feature_mode, window=args.window,
            horizon=args.horizon, threshold=args.threshold, n_splits=args.wf_splits,
            gap=args.gap, dates=dates, outer_fold_index=args.outer_fold_index,
            fixed_zscore=args.fixed_zscore, lags=fixed_scaler.trainer_lags(args, None),
        )
        if x_tr.shape[0] == 0 or x_va.shape[0] == 0:
            raise SystemExit("Empty train or val split; check --window/--horizon/--wf-splits vs data length.")
        date_ranges = ds.train_val_date_ranges(
            symbols, dates, feature_mode=args.feature_mode, window=args.window,
            horizon=args.horizon, threshold=args.threshold, n_splits=args.wf_splits,
            gap=args.gap, outer_fold_index=args.outer_fold_index,
            fixed_zscore=args.fixed_zscore, lags=fixed_scaler.trainer_lags(args, None),
        )
        channels = ds.feature_channels(args.feature_mode) * (1 + len(fixed_scaler.trainer_lags(args, None)))
        symbol_count = len(symbols)
    else:
        x_tr, y_tr = prepared.x_train, prepared.y_train
        x_va, y_va = prepared.x_validation, prepared.y_validation
        date_ranges = prepared.date_ranges
        channels = prepared.channels
        symbol_count = len(prepared.symbols)

    spec = prepared.feature_spec if prepared is not None else None
    lags = fixed_scaler.trainer_lags(args, spec)
    x_tr, x_va, scaler = fixed_scaler.prepare(
        x_tr, x_va, enabled=args.fixed_zscore, mode=args.feature_mode,
        feature_spec=spec, lags=lags, clip_sigma=args.clip_sigma,
    )
    model = build_model(args.arch, channels, args.window, args.hidden, args.dense, args.dropout, args.lr)
    print(
        f"arch={args.arch} feature_mode={args.feature_mode} channels={channels} "
        f"train={len(x_tr)} val={len(x_va)} symbols={symbol_count}"
    )
    model.fit(
        x_tr, y_tr,
        validation_data=(x_va, y_va),
        epochs=args.epochs,
        batch_size=args.batch,
        class_weight=_class_weight(y_tr),
        callbacks=[tf.keras.callbacks.EarlyStopping(
            monitor="val_loss", patience=args.patience, restore_best_weights=True,
        )],
        verbose=2,
    )

    val_probs = np.asarray(model.predict(x_va, verbose=0), dtype=np.float64)
    report = metrics.classification_report_dict(
        y_va, val_probs.argmax(axis=1), probs=val_probs
    )
    print(metrics.format_report(report))
    return model, channels, report, date_ranges, scaler


# --- Export & verification -------------------------------------------------


def _rename_io(model_proto: onnx.ModelProto, input_name: str, output_name: str) -> onnx.ModelProto:
    """Rename the single graph input/output (and all their references) to the contract names."""
    graph = model_proto.graph
    old_in = graph.input[0].name
    old_out = graph.output[0].name
    remap = {old_in: input_name, old_out: output_name}

    for value_info in list(graph.input) + list(graph.output):
        if value_info.name in remap:
            value_info.name = remap[value_info.name]
    for node in graph.node:
        node.input[:] = [remap.get(n, n) for n in node.input]
        node.output[:] = [remap.get(n, n) for n in node.output]
    return model_proto


def export_onnx(
    model: tf.keras.Model, channels: int, window: int, out_path: Path, opset: int,
    metadata: dict | None = None,
) -> None:
    out_path.parent.mkdir(parents=True, exist_ok=True)
    spec = (tf.TensorSpec((None, window, channels), tf.float32, name=INPUT_NAME),)
    try:
        model_proto, _ = tf2onnx.convert.from_keras(model, input_signature=spec, opset=opset)
        path_used = "from_keras"
    except Exception as exc:  # noqa: BLE001 - fall back to the SavedModel route
        print(f"tf2onnx.from_keras failed ({type(exc).__name__}: {exc}); retrying via SavedModel export")
        with tempfile.TemporaryDirectory() as tmp:
            saved = Path(tmp) / "saved_model"
            model.export(str(saved))  # Keras 3 -> TF SavedModel with a serving signature
            model_proto, _ = tf2onnx.convert.from_saved_model(
                str(saved), input_names=None, output_names=None, opset=opset,
            )
        path_used = "from_saved_model"

    model_proto = _rename_io(model_proto, INPUT_NAME, OUTPUT_NAME)
    if metadata is not None:
        onnx_meta.apply(model_proto, metadata)
    onnx.checker.check_model(model_proto)
    onnx.save(model_proto, str(out_path))
    contract_note = f", contract {len(metadata)} keys" if metadata is not None else ""
    print(f"exported {out_path}  (tf2onnx {path_used}, opset {opset}{contract_note})")


def verify_onnx(out_path: Path, model: tf.keras.Model, channels: int, window: int) -> None:
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

    x = np.random.randn(3, window, channels).astype(np.float32)
    (probs,) = session.run([OUTPUT_NAME], {INPUT_NAME: x})
    assert probs.shape == (3, NUM_CLASSES), probs.shape
    assert np.isfinite(probs).all()
    assert (probs >= 0.0).all() and (probs <= 1.0).all()
    assert np.allclose(probs.sum(axis=1), 1.0, atol=ONNX_ATOL)

    ref = model.predict(x, verbose=0)
    max_delta = float(np.max(np.abs(ref - probs)))
    assert max_delta < ONNX_ATOL, f"keras vs onnxruntime delta {max_delta:.2e} >= {ONNX_ATOL:.0e}"
    print(f"onnx verify: OK  (keras-vs-ort max delta {max_delta:.2e}, dynamic batch OK)")


# --- CLI --------------------------------------------------------------------


def _parse_args(argv) -> argparse.Namespace:
    p = argparse.ArgumentParser(description="Train a Keras trend predictor and export to ONNX via tf2onnx.")
    p.add_argument("--data-dir", type=Path, default=ds.DEFAULT_DATA_DIR)
    p.add_argument("--feature-mode", choices=ds.FEATURE_MODES + (ds.COMPOSED_FEATURES_MODE,), default="ohlcv_minmax")
    p.add_argument("--feature-spec", type=str, default=None)
    p.add_argument("--fixed-zscore", action="store_true")
    p.add_argument("--clip-sigma", type=float, default=None)
    p.add_argument("--lags", type=str, default="[]")
    p.add_argument("--indicator-channel-export-paths", type=str, default=None)
    p.add_argument("--arch", choices=ARCHES, default=DEFAULT_ARCH)
    p.add_argument("--window", type=int, default=ds.DEFAULT_WINDOW)
    p.add_argument("--horizon", type=int, default=ds.DEFAULT_HORIZON)
    p.add_argument("--timeframe", choices=tuple(ds.TIMEFRAME_DIRS), default=onnx_meta.DEFAULT_TIMEFRAME)
    p.add_argument("--threshold", type=float, default=ds.DEFAULT_THRESHOLD)
    p.add_argument("--hidden", type=int, default=DEFAULT_HIDDEN)
    p.add_argument("--dense", type=int, default=DEFAULT_DENSE)
    p.add_argument("--dropout", type=float, default=DEFAULT_DROPOUT)
    p.add_argument("--epochs", type=int, default=DEFAULT_EPOCHS)
    p.add_argument("--batch", type=int, default=DEFAULT_BATCH)
    p.add_argument("--lr", type=float, default=DEFAULT_LR)
    p.add_argument("--patience", type=int, default=DEFAULT_PATIENCE)
    p.add_argument("--wf-splits", type=int, default=DEFAULT_WF_SPLITS)
    p.add_argument("--outer-fold-index", type=int, default=None,
                   help="Original outer fold to fit independently.")
    p.add_argument("--gap", type=int, default=None,
                   help="Purge gap in bars between each walk-forward fold's train/val blocks "
                        "(default: None -> dataset.resolve_purge_gap picks window + horizon - 1).")
    p.add_argument("--seed", type=int, default=DEFAULT_SEED)
    p.add_argument("--opset", type=int, default=DEFAULT_OPSET)
    p.add_argument("--max-symbols", type=int, default=None)
    p.add_argument("--out", type=Path, default=DEFAULT_OUT)
    p.add_argument("--price-adjustment", default=onnx_meta.DEFAULT_PRICE_ADJUSTMENT,
                   help="Value embedded as metadata_props.price_adjustment (default: adjusted).")
    p.add_argument("--no-verify", action="store_true")
    p.add_argument("--smoke", action="store_true",
                   help=f"Fast run: --max-symbols {SMOKE_MAX_SYMBOLS} --epochs {SMOKE_EPOCHS}.")
    args = p.parse_args(argv)
    if args.gap is not None and args.gap < 0:
        raise SystemExit(f"--gap must be non-negative, got {args.gap}")
    if args.outer_fold_index is not None and not 0 <= args.outer_fold_index < args.wf_splits:
        raise SystemExit("--outer-fold-index must be within --wf-splits")
    if args.smoke:
        if args.max_symbols is None:
            args.max_symbols = SMOKE_MAX_SYMBOLS
        args.epochs = min(args.epochs, SMOKE_EPOCHS)
    try:
        composed_training.validate_flags(args)
    except ValueError as exc:
        p.error(str(exc))
    return args


def main(argv) -> int:
    args = _parse_args(argv)
    prepared = composed_training.prepare(args) if args.feature_mode == ds.COMPOSED_FEATURES_MODE else None
    model, channels, report, date_ranges, scaler = train_model(args, prepared)
    lags = fixed_scaler.trainer_lags(args, prepared.feature_spec if prepared is not None else None)
    contract = onnx_meta.build_contract(
        feature_mode=args.feature_mode, window_size=args.window, channels=channels,
        horizon=args.horizon, threshold=args.threshold, wf_splits=args.wf_splits,
        seed=args.seed, producer=f"train_tensorflow.py arch={args.arch}", gap=args.gap,
        timeframe=args.timeframe,
        price_adjustment=args.price_adjustment, date_ranges=date_ranges,
        feature_spec_json=args.feature_spec if prepared is not None else None,
        fixed_zscore=args.fixed_zscore, lags=lags, clip_sigma=args.clip_sigma,
        scaler_ref=args.out.name + ".scaler.json" if scaler is not None else None,
        analysis_ref=args.out.name + ".analysis.json" if args.outer_fold_index is not None else None,
    )
    def export_with_scaler(path: Path) -> None:
        export_onnx(model, channels, args.window, path, args.opset, contract)
        if scaler is not None:
            fixed_scaler.write_sidecar(path, scaler, contract)
    if prepared is not None:
        composed_training.publish_verified(
            args.out, report,
            export_with_scaler,
            lambda staged: composed_training.verify_export(
                staged, prepared, args.feature_spec,
                lambda x: model.predict(x, verbose=0), ONNX_ATOL, scaler=scaler,
            ),
        )
        print(f"published {args.out} and metrics")
        return 0
    export_with_scaler(args.out)
    print(f"wrote {metrics.write_report(report, args.out)}")
    if not args.no_verify:
        verify_onnx(args.out, model, channels, args.window)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
