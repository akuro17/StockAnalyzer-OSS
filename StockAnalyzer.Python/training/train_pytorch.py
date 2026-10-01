"""Train an LSTM / 1D-CNN trend predictor in PyTorch and export it to ONNX.

The exported graph is contract-compatible with the C# inference engine
(``StockAnalyzer.Core.Services.PredictionService``):

    input  : float32  [batch, window, channels]   node name "input"
    output : float32  [batch, 3]                   node name "output"  (softmax probabilities, class order Up, Down, Neutral)
                                                     -- or, with --target-type regression --
    output : float32  [batch, 1]                   node name "output"  (raw forward log-return value, no activation)

Feature preprocessing and labeling come from :mod:`dataset`, which mirrors the
C# ``MLDataProcessor``. The classification (default) model is trained on raw
logits with ``CrossEntropyLoss``; a thin :class:`SoftmaxExport` wrapper adds
the final ``Softmax`` so the ONNX file emits a probability distribution
directly. The regression model (``--target-type regression``) is trained with
``MSELoss`` against :func:`dataset.make_regression_target` and exports its raw
output unwrapped.

Examples
--------
Fast smoke run (few symbols, few epochs) with post-export ONNX verification::

    python train_pytorch.py --smoke

Full run::

    python train_pytorch.py --arch lstm --feature-mode ohlcv_minmax --epochs 30

Composed features (a user-picked list of price channels; see ``dataset.COMPOSED_FEATURES_MODE``)::

    python train_pytorch.py --feature-mode composed_features \
        --feature-spec '{"channels":[{"kind":"price","price":"high","normalization":"none"},{"kind":"price","price":"close","normalization":"none"}]}'

Regression (single forward-log-return value instead of the 3-class head)::

    python train_pytorch.py --target-type regression --arch lstm --epochs 30
"""

from __future__ import annotations

import argparse
import copy
import json
import random
import sys
import time
from pathlib import Path
from typing import Tuple

import numpy as np
import torch
from torch import nn

sys.path.insert(0, str(Path(__file__).resolve().parent))
import dataset as ds  # noqa: E402  (sibling module, path inserted above)
import fixed_scaler  # noqa: E402
import metrics  # noqa: E402  (sibling module)
import onnx_meta  # noqa: E402  (sibling module)
import checkpoints  # noqa: E402

# --- Defaults (named; no magic numbers) -------------------------------------

DEFAULT_ARCH: str = "lstm"
ARCHES: Tuple[str, str] = ("lstm", "cnn")
DEFAULT_HIDDEN: int = 64
DEFAULT_LAYERS: int = 2
DEFAULT_DROPOUT: float = 0.2
DEFAULT_EPOCHS: int = 15
DEFAULT_BATCH: int = 256
DEFAULT_LR: float = 1e-3
DEFAULT_WEIGHT_DECAY: float = 1e-4
DEFAULT_PATIENCE: int = 4
DEFAULT_SEED: int = 42
DEFAULT_OPSET: int = 17
DEFAULT_WF_SPLITS: int = ds.DEFAULT_WF_SPLITS  # SSoT: dataset.DEFAULT_WF_SPLITS
EARLY_STOP_MIN_DELTA: float = 1e-5
ONNX_ATOL: float = 1e-4  # torch-vs-onnxruntime float32 export tolerance

NUM_CLASSES: int = len(ds.CLASS_LABELS)  # mirrors PredictionSettings.ClassLabels length
DEFAULT_OUT: Path = Path(__file__).resolve().parent / "artifacts" / "trend_predictor.onnx"

SMOKE_MAX_SYMBOLS: int = 8
SMOKE_EPOCHS: int = 3


# --- Models ---------------------------------------------------------------------


class LstmClassifier(nn.Module):
    """2-layer LSTM over the window; last hidden state -> Linear(out_dim) logits/value."""

    def __init__(self, channels: int, hidden: int, layers: int, dropout: float, out_dim: int):
        super().__init__()
        self.lstm = nn.LSTM(
            input_size=channels,
            hidden_size=hidden,
            num_layers=layers,
            batch_first=True,
            dropout=dropout if layers > 1 else 0.0,
        )
        self.head = nn.Linear(hidden, out_dim)

    def forward(self, x: torch.Tensor) -> torch.Tensor:  # x: [N, W, C] -> [N, out_dim]
        out, _ = self.lstm(x)
        return self.head(out[:, -1, :])


class CnnClassifier(nn.Module):
    """Two Conv1d blocks + global average pool -> Linear(out_dim) logits/value."""

    def __init__(self, channels: int, hidden: int, dropout: float, out_dim: int):
        super().__init__()
        self.features = nn.Sequential(
            nn.Conv1d(channels, max(hidden // 2, 1), kernel_size=3, padding=1),
            nn.ReLU(),
            nn.Conv1d(max(hidden // 2, 1), hidden, kernel_size=3, padding=1),
            nn.ReLU(),
            nn.AdaptiveAvgPool1d(1),
        )
        self.drop = nn.Dropout(dropout)
        self.head = nn.Linear(hidden, out_dim)

    def forward(self, x: torch.Tensor) -> torch.Tensor:  # x: [N, W, C] -> [N, out_dim]
        z = self.features(x.transpose(1, 2)).squeeze(-1)
        return self.head(self.drop(z))


class SoftmaxExport(nn.Module):
    """Wraps a logits model so the exported ONNX emits a probability distribution."""

    def __init__(self, base: nn.Module):
        super().__init__()
        self.base = base

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        return torch.softmax(self.base(x), dim=-1)


def build_model(arch: str, channels: int, hidden: int, layers: int, dropout: float, out_dim: int) -> nn.Module:
    if arch == "lstm":
        return LstmClassifier(channels, hidden, layers, dropout, out_dim)
    if arch == "cnn":
        return CnnClassifier(channels, hidden, dropout, out_dim)
    raise ValueError(f"Unknown arch {arch!r}; expected one of {ARCHES}")


# --- Training -----------------------------------------------------------------


def _class_weights(y: np.ndarray) -> torch.Tensor:
    """Inverse-frequency weights so the minority (Neutral) class is not ignored."""
    counts = np.array([(y == c).sum() for c in range(NUM_CLASSES)], dtype=np.float64)
    counts[counts == 0] = 1.0
    w = counts.sum() / (NUM_CLASSES * counts)
    return torch.tensor(w, dtype=torch.float32)


def _iterate_minibatches(x: torch.Tensor, y: torch.Tensor, batch: int, shuffle: bool, rng: np.random.Generator):
    order = np.arange(x.shape[0])
    if shuffle:
        rng.shuffle(order)
    for start in range(0, len(order), batch):
        idx = order[start:start + batch]
        yield x[idx], y[idx]


def validate_checkpoint(path):
    """Preflight every persisted fold before any fold is fitted in a resumed run."""
    pointer, blob = checkpoints.checked_blob(path)
    restored = torch.load(blob, map_location="cpu", weights_only=True)
    required = {"context", "model", "optimizer", "completed_epoch", "best_state", "best_val",
                "patience_counter", "torch_rng", "numpy_rng", "numpy_global_rng", "python_rng"}
    if (not isinstance(restored, dict) or not required.issubset(restored)
            or restored["context"] != pointer["context"]):
        raise ValueError("incomplete native PyTorch checkpoint state")
    if (type(restored["completed_epoch"]) is not int or restored["completed_epoch"] < 1
            or type(restored["patience_counter"]) is not int or restored["patience_counter"] < 0
            or not np.isfinite(restored["best_val"])
            or any(pointer.get(k) != restored[k] for k in ("completed_epoch", "best_val", "patience_counter"))):
        raise ValueError("invalid native epoch/early stopping state")
    context = restored["context"]
    if context.get("runtime") != {"torch": str(torch.__version__), "numpy": np.__version__, "device": "cpu"}:
        raise ValueError("checkpoint runtime mismatch")
    contract = context["contract"]
    model = build_model(contract["arch"], len(contract["ordered_channels"]), contract["hidden"],
                        contract["layers"], contract["dropout"],
                        1 if contract["target_type"] == "regression" else NUM_CLASSES)
    for key in ("best_state", "model"):
        if any(not isinstance(v, torch.Tensor) or not torch.isfinite(v).all() for v in restored[key].values()):
            raise ValueError("nonfinite or invalid checkpoint weights")
        model.load_state_dict(restored[key], strict=True)
    checkpoints.freeze_modules(model, context["freeze_paths"])
    optimizer = torch.optim.AdamW((p for p in model.parameters() if p.requires_grad))
    optimizer.load_state_dict(restored["optimizer"])
    parameters = {p for group in optimizer.param_groups for p in group["params"]}
    if set(optimizer.state) != parameters:
        raise ValueError("incomplete optimizer parameter state")
    for parameter, saved in optimizer.state.items():
        if not {"step", "exp_avg", "exp_avg_sq"}.issubset(saved):
            raise ValueError("incomplete optimizer moment state")
        for key in ("exp_avg", "exp_avg_sq"):
            if saved[key].shape != parameter.shape or not torch.isfinite(saved[key]).all():
                raise ValueError("invalid optimizer moment state")
        if saved["step"].numel() != 1 or not torch.isfinite(saved["step"]).all():
            raise ValueError("invalid optimizer step state")
    # Validate RNG on isolated generators; preflight must not alter the training streams.
    torch.Generator().set_state(restored["torch_rng"])
    np.random.default_rng().bit_generator.state = restored["numpy_rng"]
    ng = restored["numpy_global_rng"]
    np.random.RandomState().set_state((ng[0], np.asarray(ng[1], dtype=np.uint32), ng[2], ng[3], ng[4]))
    random.Random().setstate(restored["python_rng"])
    if pointer.get("slot") == "last":
        best_pointer = pointer.get("best_checkpoint")
        if not isinstance(best_pointer, dict) or best_pointer.get("slot") != "best":
            raise ValueError("Resume best checkpoint reference is missing")
        _, best_blob = checkpoints.checked_blob(path, best_pointer)
        best = torch.load(best_blob, map_location="cpu", weights_only=True)
        if (best_pointer.get("context") != context or best_pointer.get("best_val") != restored["best_val"]
                or best.get("context") != context or best.get("best_val") != restored["best_val"]
                or best["model"].keys() != restored["best_state"].keys()
                or any(not torch.equal(v, best["model"][k]) for k, v in restored["best_state"].items())):
            raise ValueError("Resume best weights and committed checkpoint disagree")
    return restored


def train_model(args: argparse.Namespace) -> Tuple[nn.Module, int, dict, dict]:
    torch.manual_seed(args.seed)
    np.random.seed(args.seed)
    random.seed(args.seed)
    rng = np.random.default_rng(args.seed)

    symbols, dates = ds.load_parquet_dir(args.data_dir, return_dates=True)
    if args.max_symbols is not None:
        symbols = dict(list(symbols.items())[: args.max_symbols])
    dates = {k: dates[k] for k in symbols}
    if not symbols:
        raise SystemExit(f"No parquet data under {args.data_dir}")

    # feature_spec is a parsed dict only for composed_features (validated + JSON-parsed
    # up front in _parse_args); every other mode carries no spec.
    feature_spec = json.loads(args.feature_spec) if args.feature_mode == ds.COMPOSED_FEATURES_MODE else None
    lags = fixed_scaler.trainer_lags(args, feature_spec)
    if not args.fixed_zscore and (lags or args.clip_sigma is not None):
        raise ValueError("clipping and lags require --fixed-zscore")
    # indicator_export_paths (symbol -> exported Indicator-channel parquet path) is likewise
    # parsed only for composed_features; only meaningful when feature_spec has an Indicator-kind
    # channel, in which case dataset.build_dataset requires it (see dataset._build_indicator_columns).
    indicator_export_paths = (
        json.loads(args.indicator_channel_export_paths)
        if args.indicator_channel_export_paths else None
    )

    # Per-symbol chronological split then pool: validation is always time-after-train
    # within each symbol, with a purge gap of window + horizon - 1 bars (or --gap,
    # when given -- ds.resolve_purge_gap resolves None the same way downstream).
    x_tr_np, y_tr_np, x_va_np, y_va_np = ds.split_symbols_chronological(
        symbols, args.feature_mode, window=args.window,
        horizon=args.horizon, threshold=args.threshold, n_splits=args.wf_splits,
        gap=args.gap, feature_spec=feature_spec, target_type=args.target_type,
        dates=dates, indicator_export_paths=indicator_export_paths,
        outer_fold_index=args.outer_fold_index,
        fixed_zscore=args.fixed_zscore, lags=lags,
    )
    if x_tr_np.shape[0] == 0 or x_va_np.shape[0] == 0:
        raise SystemExit("Empty train or val split; check --window/--horizon/--wf-splits vs data length.")

    # Calendar spans of the same split, for the model-contract provenance metadata.
    date_ranges = ds.train_val_date_ranges(
        symbols, dates, feature_mode=args.feature_mode, window=args.window,
        horizon=args.horizon, threshold=args.threshold, n_splits=args.wf_splits,
        gap=args.gap, feature_spec=feature_spec, target_type=args.target_type,
        indicator_export_paths=indicator_export_paths,
        outer_fold_index=args.outer_fold_index,
        fixed_zscore=args.fixed_zscore, lags=lags,
    )

    channels = ds.feature_channels(args.feature_mode, feature_spec=feature_spec) * (1 + len(lags))
    source_arrays = tuple(symbols[s] for s in sorted(symbols))
    session = checkpoints.Session(args, "pytorch", (x_tr_np, y_tr_np, x_va_np, y_va_np, *source_arrays),
                                  dates, feature_spec, lags,
                                  {"torch": str(torch.__version__), "numpy": np.__version__, "device": "cpu"})
    args._checkpoint_session = session
    x_tr_np, x_va_np, scaler = session.preprocess(
        x_tr_np, x_va_np, feature_spec=feature_spec, lags=lags)
    is_regression = args.target_type == "regression"
    out_dim = 1 if is_regression else NUM_CLASSES
    x_tr = torch.from_numpy(x_tr_np)
    y_tr = torch.from_numpy(y_tr_np)
    x_va = torch.from_numpy(x_va_np)
    y_va = torch.from_numpy(y_va_np)

    model = build_model(args.arch, channels, args.hidden, args.layers, args.dropout, out_dim)
    restored = None
    if session.source is not None:
        restored = validate_checkpoint(args.init_from)
        # Validate both weight sets before fitting, even when only weights are reused.
        model.load_state_dict(restored["best_state"], strict=True)
        model.load_state_dict(restored["model"], strict=True)
    checkpoints.freeze_modules(model, session.context["freeze_paths"])
    optimizer = torch.optim.AdamW((p for p in model.parameters() if p.requires_grad),
                                  lr=args.lr, weight_decay=args.weight_decay)
    loss_fn = nn.MSELoss() if is_regression else nn.CrossEntropyLoss(weight=_class_weights(y_tr_np))

    best_val = float("inf")
    best_state = copy.deepcopy(model.state_dict())
    patience_ctr = 0
    completed_epoch = 0
    if session.mode == "resume":
        optimizer.load_state_dict(restored["optimizer"])
        completed_epoch = restored["completed_epoch"]
        best_state = restored["best_state"]
        best_val = restored["best_val"]
        patience_ctr = restored["patience_counter"]
        torch.set_rng_state(restored["torch_rng"])
        rng.bit_generator.state = restored["numpy_rng"]
        ng = restored["numpy_global_rng"]
        np.random.set_state((ng[0], np.asarray(ng[1], dtype=np.uint32), ng[2], ng[3], ng[4]))
        random.setstate(restored["python_rng"])
        best_pointer = session.source.get("best_checkpoint")
        if not isinstance(best_pointer, dict):
            raise ValueError("Resume best checkpoint reference is missing")
        # The last pointer commits the epoch and its best pointer together. A crash
        # publishing the next best must not introduce future state into this Resume.
        best_path = session.root / "best.json"
        if not args.preflight:
            checkpoints.atomic_json(best_path, best_pointer)
            checkpoints.checked_blob(best_path)

    if args.preflight:
        return model, channels, {}, date_ranges, scaler

    def save_epoch(epoch, improved):
        ng = np.random.get_state()
        state = dict(context=session.context, model=model.state_dict(), optimizer=optimizer.state_dict(),
                     completed_epoch=epoch, best_state=best_state, best_val=best_val,
                     patience_counter=patience_ctr, torch_rng=torch.get_rng_state(),
                     numpy_rng=rng.bit_generator.state,
                     numpy_global_rng=(ng[0], ng[1].tolist(), ng[2], ng[3], ng[4]),
                     python_rng=random.getstate())
        summary = dict(completed_epoch=epoch, best_val=best_val, patience_counter=patience_ctr)
        if improved:
            session.save(lambda path: torch.save(state, path), ".pt", "best", summary)
        summary["best_checkpoint"] = checkpoints.read_json(session.root / "best.json")
        session.save(lambda path: torch.save(state, path), ".pt", "last", summary)

    print(
        f"arch={args.arch} feature_mode={args.feature_mode} channels={channels} "
        f"target_type={args.target_type} train={len(x_tr_np)} val={len(x_va_np)} symbols={len(symbols)}"
    )
    for epoch in range(completed_epoch + 1, completed_epoch + args.epochs + 1):
        if patience_ctr >= args.patience:
            print("restored early stopping is exhausted; no additional optimizer steps")
            break
        model.train()
        t0 = time.time()
        run_loss = 0.0
        n_seen = 0
        for xb, yb in _iterate_minibatches(x_tr, y_tr, args.batch, shuffle=True, rng=rng):
            optimizer.zero_grad()
            loss = loss_fn(model(xb), yb)
            loss.backward()
            optimizer.step()
            run_loss += loss.item() * xb.shape[0]
            n_seen += xb.shape[0]
        train_loss = run_loss / max(n_seen, 1)

        model.eval()
        with torch.no_grad():
            val_out = model(x_va)
            val_loss = loss_fn(val_out, y_va).item()
            epoch_line = (
                f"epoch {epoch:3d}/{args.epochs}  train_loss={train_loss:.4f}  "
                f"val_loss={val_loss:.4f}  ({time.time() - t0:.1f}s)"
            )
            if not is_regression:
                val_acc = (val_out.argmax(dim=-1) == y_va).float().mean().item()
                epoch_line = (
                    f"epoch {epoch:3d}/{args.epochs}  train_loss={train_loss:.4f}  "
                    f"val_loss={val_loss:.4f}  val_acc={val_acc:.3f}  ({time.time() - t0:.1f}s)"
                )

        print(epoch_line)

        if not np.isfinite(train_loss) or not np.isfinite(val_loss):
            raise ValueError("nonfinite loss; completed checkpoint was not advanced")
        improved = val_loss < best_val - EARLY_STOP_MIN_DELTA
        if improved:
            best_val = val_loss
            best_state = copy.deepcopy(model.state_dict())
            patience_ctr = 0
        else:
            patience_ctr += 1
        save_epoch(epoch, improved)
        if patience_ctr >= args.patience:
            print(f"early stop at epoch {epoch} (no val improvement for {args.patience} epochs)")
            break

    model.load_state_dict(best_state)
    model.eval()

    if is_regression:
        with torch.no_grad():
            val_preds = model(x_va).cpu().numpy()
        report = metrics.regression_metrics(y_va_np.ravel(), val_preds.ravel())
        print(
            f"samples={report['n_samples']}  rmse={report['rmse']:.4f}  mae={report['mae']:.4f}  "
            f"directional_accuracy={report['directional_accuracy']:.4f}  "
            f"(delta_vs_zero_baseline={report['rmse_over_baseline']:+.4f})"
        )
    else:
        with torch.no_grad():
            val_probs = torch.softmax(model(x_va), dim=-1).cpu().numpy()
        report = metrics.classification_report_dict(
            y_va_np, val_probs.argmax(axis=1), probs=val_probs
        )
        print(metrics.format_report(report))
    return model, channels, report, date_ranges, scaler


# --- Export & verification -------------------------------------------------


def export_onnx(
    model: nn.Module, channels: int, window: int, out_path: Path, opset: int,
    metadata: dict | None = None, *, target_type: str = "classification",
) -> None:
    out_path.parent.mkdir(parents=True, exist_ok=True)
    # Classification exports a probability distribution (Softmax applied at export time);
    # regression exports the model's raw continuous value -- no activation to wrap it in.
    wrapper = SoftmaxExport(model).eval() if target_type == "classification" else model.eval()
    dummy = torch.randn(1, window, channels, dtype=torch.float32)
    torch.onnx.export(
        wrapper,
        dummy,
        str(out_path),
        input_names=["input"],
        output_names=["output"],
        dynamic_axes={"input": {0: "batch"}, "output": {0: "batch"}},
        opset_version=opset,
        dynamo=False,  # pin the stable TorchScript exporter for C# ORT 1.24.3 compatibility
    )
    print(f"exported {out_path}")
    if metadata is not None:
        onnx_meta.load_apply_save(out_path, metadata)
        print(f"embedded model contract ({len(metadata)} keys)")


def verify_onnx(
    out_path: Path, model: nn.Module, channels: int, window: int,
    *, target_type: str = "classification",
) -> None:
    import onnx
    import onnxruntime as ort

    is_regression = target_type == "regression"
    out_dim = 1 if is_regression else NUM_CLASSES

    onnx.checker.check_model(onnx.load(str(out_path)))

    session = ort.InferenceSession(str(out_path), providers=["CPUExecutionProvider"])
    in_meta = session.get_inputs()[0]
    out_meta = session.get_outputs()[0]
    assert in_meta.name == "input", in_meta.name
    assert out_meta.name == "output", out_meta.name
    assert len(in_meta.shape) == 3, in_meta.shape
    assert len(out_meta.shape) == 2, out_meta.shape
    if isinstance(in_meta.shape[2], int):
        assert in_meta.shape[2] == channels, in_meta.shape
    if isinstance(out_meta.shape[1], int):
        assert out_meta.shape[1] == out_dim, out_meta.shape

    x = np.random.randn(1, window, channels).astype(np.float32)
    (probs,) = session.run(["output"], {"input": x})
    assert probs.shape == (1, out_dim), probs.shape
    assert np.isfinite(probs).all()
    if not is_regression:
        assert (probs >= 0.0).all() and (probs <= 1.0).all()
        assert abs(float(probs.sum()) - 1.0) < ONNX_ATOL

    with torch.no_grad():
        base_out = model(torch.from_numpy(x))
        ref = torch.softmax(base_out, dim=-1).numpy() if not is_regression else base_out.numpy()
    max_delta = float(np.max(np.abs(ref - probs)))
    assert max_delta < ONNX_ATOL, f"torch vs onnxruntime delta {max_delta:.2e} >= {ONNX_ATOL:.0e}"

    # dynamic batch sanity
    (batch_probs,) = session.run(["output"], {"input": np.random.randn(4, window, channels).astype(np.float32)})
    assert batch_probs.shape == (4, out_dim), batch_probs.shape

    print(f"onnx verify: OK  (torch-vs-ort max delta {max_delta:.2e}, opset checked, dynamic batch OK)")


# --- CLI --------------------------------------------------------------------


def _parse_args(argv) -> argparse.Namespace:
    p = argparse.ArgumentParser(description="Train an LSTM/1D-CNN trend predictor and export to ONNX.")
    checkpoints.add_arguments(p)
    p.add_argument("--data-dir", type=Path, default=ds.DEFAULT_DATA_DIR)
    p.add_argument(
        "--feature-mode", choices=ds.FEATURE_MODES + (ds.COMPOSED_FEATURES_MODE,),
        default="ohlcv_minmax",
    )
    p.add_argument(
        "--feature-spec", type=str, default=None,
        help="JSON channel list (mirrors C# Training.FeatureSpec); required iff "
             "--feature-mode composed_features, e.g. "
             '\'{"channels":[{"kind":"price","price":"close","normalization":"none"}]}\'.',
    )
    p.add_argument("--fixed-zscore", action="store_true")
    p.add_argument("--clip-sigma", type=float, default=None)
    p.add_argument("--lags", type=str, default="[]")
    p.add_argument(
        "--indicator-channel-export-paths", type=str, default=None,
        help="JSON object mapping symbol -> exported Indicator-channel parquet path (mirrors "
             "C# TrainingJobConfig.IndicatorChannelExportPaths, emitted by run_training.py); "
             "only meaningful when --feature-spec has an Indicator-kind channel, and must not "
             "be given otherwise.",
    )
    p.add_argument(
        "--target-type", choices=onnx_meta.TARGET_TYPES, default=onnx_meta.TARGET_TYPE,
        help="Learning objective: 'classification' (3-class Up/Down/Neutral, default) or "
             "'regression' (single forward-log-return value).",
    )
    p.add_argument("--arch", choices=ARCHES, default=DEFAULT_ARCH)
    p.add_argument("--window", type=int, default=ds.DEFAULT_WINDOW)
    p.add_argument("--horizon", type=int, default=ds.DEFAULT_HORIZON)
    p.add_argument("--timeframe", choices=tuple(ds.TIMEFRAME_DIRS), default=onnx_meta.DEFAULT_TIMEFRAME)
    p.add_argument("--threshold", type=float, default=ds.DEFAULT_THRESHOLD)
    p.add_argument("--hidden", type=int, default=DEFAULT_HIDDEN)
    p.add_argument("--layers", type=int, default=DEFAULT_LAYERS)
    p.add_argument("--dropout", type=float, default=DEFAULT_DROPOUT)
    p.add_argument("--epochs", type=int, default=DEFAULT_EPOCHS)
    p.add_argument("--batch", type=int, default=DEFAULT_BATCH)
    p.add_argument("--lr", type=float, default=DEFAULT_LR)
    p.add_argument("--weight-decay", type=float, default=DEFAULT_WEIGHT_DECAY)
    p.add_argument("--patience", type=int, default=DEFAULT_PATIENCE)
    p.add_argument("--wf-splits", type=int, default=DEFAULT_WF_SPLITS)
    p.add_argument("--outer-fold-index", type=int, default=None,
                   help="Original outer fold to fit independently; omitted keeps the standalone path.")
    p.add_argument("--gap", type=int, default=None,
                   help="Purge gap in bars between each walk-forward fold's train/val blocks "
                        "(default: None -> dataset.resolve_purge_gap picks window + horizon - 1).")
    p.add_argument("--seed", type=int, default=DEFAULT_SEED)
    p.add_argument("--opset", type=int, default=DEFAULT_OPSET)
    p.add_argument("--max-symbols", type=int, default=None, help="Limit number of symbols (speed).")
    p.add_argument("--out", type=Path, default=DEFAULT_OUT)
    p.add_argument("--price-adjustment", default=onnx_meta.DEFAULT_PRICE_ADJUSTMENT,
                   help="Value embedded as metadata_props.price_adjustment (default: adjusted).")
    p.add_argument("--no-verify", action="store_true", help="Skip post-export ONNX verification.")
    p.add_argument("--smoke", action="store_true",
                   help=f"Fast run: --max-symbols {SMOKE_MAX_SYMBOLS} --epochs {SMOKE_EPOCHS}.")
    args = p.parse_args(argv)
    if args.epochs <= 0 or args.patience <= 0 or args.batch <= 0:
        p.error("epochs, patience and batch must be positive")
    checkpoints.validate_controls(args.initialization_mode, "pytorch", args.init_from,
                                  args.parent_model_id, args.parent_model_hash,
                                  json.loads(args.freeze_paths) if args.initialization_mode != "resume" else [])
    if args.gap is not None and args.gap < 0:
        raise SystemExit(f"--gap must be non-negative, got {args.gap}")
    if args.outer_fold_index is not None and not 0 <= args.outer_fold_index < args.wf_splits:
        raise SystemExit("--outer-fold-index must be within --wf-splits")
    # --feature-spec is required (and must be valid JSON) iff composed_features -- mirrors the
    # symmetric rule in TrainingJobConfig.validate / run_training.py's JobConfig.validate.
    if args.feature_mode == ds.COMPOSED_FEATURES_MODE:
        if not args.feature_spec:
            raise SystemExit("--feature-spec is required when --feature-mode composed_features")
        try:
            json.loads(args.feature_spec)
        except json.JSONDecodeError as exc:
            raise SystemExit(f"--feature-spec is not valid JSON: {exc}") from exc
    elif args.feature_spec is not None:
        raise SystemExit("--feature-spec must not be given unless --feature-mode is composed_features")
    # --indicator-channel-export-paths (JSON object) is optional even for composed_features (a
    # Price-only composed_features run has none), but when given it must be valid JSON, and it
    # must not be given at all outside composed_features -- mirrors --feature-spec's own rule.
    if args.indicator_channel_export_paths is not None:
        if args.feature_mode != ds.COMPOSED_FEATURES_MODE:
            raise SystemExit(
                "--indicator-channel-export-paths must not be given unless --feature-mode is "
                "composed_features"
            )
        try:
            json.loads(args.indicator_channel_export_paths)
        except json.JSONDecodeError as exc:
            raise SystemExit(f"--indicator-channel-export-paths is not valid JSON: {exc}") from exc
    if args.smoke:
        if args.max_symbols is None:
            args.max_symbols = SMOKE_MAX_SYMBOLS
        args.epochs = min(args.epochs, SMOKE_EPOCHS)
    return args


def main(argv) -> int:
    args = _parse_args(argv)
    model, channels, report, date_ranges, scaler = train_model(args)
    if args.preflight:
        return 0
    spec = json.loads(args.feature_spec) if args.feature_spec else None
    lags = fixed_scaler.trainer_lags(args, spec)
    contract = onnx_meta.build_contract(
        feature_mode=args.feature_mode, window_size=args.window, channels=channels,
        horizon=args.horizon, threshold=args.threshold, wf_splits=args.wf_splits,
        seed=args.seed, producer=f"train_pytorch.py arch={args.arch}", gap=args.gap,
        timeframe=args.timeframe,
        price_adjustment=args.price_adjustment, date_ranges=date_ranges,
        feature_spec_json=args.feature_spec if args.feature_mode == ds.COMPOSED_FEATURES_MODE else None,
        target_type=args.target_type,
        fixed_zscore=args.fixed_zscore, lags=lags, clip_sigma=args.clip_sigma,
        scaler_ref=args.out.name + ".scaler.json" if scaler is not None else None,
        analysis_ref=args.out.name + ".analysis.json" if args.outer_fold_index is not None else None,
    )
    contract.update(args._checkpoint_session.metadata())
    export_onnx(model, channels, args.window, args.out, args.opset, contract, target_type=args.target_type)
    if scaler is not None:
        fixed_scaler.write_sidecar(args.out, scaler, contract)
    print(f"wrote {metrics.write_report(report, args.out)}")
    if not args.no_verify:
        verify_onnx(args.out, model, channels, args.window, target_type=args.target_type)
        args._checkpoint_session.exported(args.out)
    return 0


# --- self-verification -----------------------------------------------------
#
# Scoped to argument-parsing/validation only (no real training run): unlike dataset.py /
# run_training.py's own --selfcheck, a real train_model() pass here needs actual parquet data
# on disk plus a torch training loop, which would make --selfcheck slow and environment-
# dependent. The real end-to-end proof is a manual `run_training.py --config ...` run.


def _run_selfcheck() -> None:
    composed_spec_json = json.dumps({
        "channels": [
            {"kind": "price", "price": "high", "normalization": "none"},
            {"kind": "price", "price": "close", "normalization": "none"},
        ]
    })

    # (a) a valid composed_features + --feature-spec pair parses, and dataset.feature_channels
    # resolves the channel count from the spec through the same call path train_model() uses.
    args = _parse_args([
        "--feature-mode", ds.COMPOSED_FEATURES_MODE, "--feature-spec", composed_spec_json,
    ])
    assert args.feature_mode == ds.COMPOSED_FEATURES_MODE
    feature_spec = json.loads(args.feature_spec)
    assert ds.feature_channels(args.feature_mode, feature_spec=feature_spec) == 2

    # (b) composed_features without --feature-spec is rejected up front.
    try:
        _parse_args(["--feature-mode", ds.COMPOSED_FEATURES_MODE])
    except SystemExit:
        pass
    else:  # pragma: no cover
        raise AssertionError("expected SystemExit for composed_features with no --feature-spec")

    # (c) --feature-spec given with a non-composed feature-mode is rejected, mirroring
    # TrainingJobConfig.validate's symmetric rule (spec allowed iff mode is composed_features).
    try:
        _parse_args(["--feature-mode", "ohlcv_minmax", "--feature-spec", composed_spec_json])
    except SystemExit:
        pass
    else:  # pragma: no cover
        raise AssertionError("expected SystemExit for --feature-spec attached to a non-composed mode")

    # (d) malformed --feature-spec JSON fails fast, before any data loading or training starts.
    try:
        _parse_args(["--feature-mode", ds.COMPOSED_FEATURES_MODE, "--feature-spec", "{not json"])
    except SystemExit:
        pass
    else:  # pragma: no cover
        raise AssertionError("expected SystemExit for malformed --feature-spec JSON")

    # (e) every pre-existing fixed-width mode still parses with no --feature-spec (regression
    # guard: extending --feature-mode's choices must not disturb the other 5 modes).
    for mode in ds.FEATURE_MODES:
        plain_args = _parse_args(["--feature-mode", mode])
        assert plain_args.feature_spec is None
        assert ds.feature_channels(mode) == ds.feature_channels(mode, feature_spec=None)

    # (f) T04: --target-type defaults to classification; regression is accepted; an unknown
    # value is rejected by argparse's own choices= check.
    default_args = _parse_args([])
    assert default_args.target_type == "classification"
    reg_args = _parse_args(["--target-type", "regression"])
    assert reg_args.target_type == "regression"
    try:
        _parse_args(["--target-type", "ranking"])
    except SystemExit:
        pass
    else:  # pragma: no cover
        raise AssertionError("expected SystemExit for unknown --target-type")

    # (g) build_model's out_dim wiring: a pure forward pass (no data loading, no optimizer,
    # no training loop -- stays fast/deterministic per this module's selfcheck scope) confirms
    # the regression head really is width-1 and the classification head is unaffected.
    torch.manual_seed(0)
    reg_model = build_model("lstm", channels=3, hidden=8, layers=1, dropout=0.0, out_dim=1)
    reg_out = reg_model(torch.randn(2, 5, 3))
    assert reg_out.shape == (2, 1), reg_out.shape
    cls_model = build_model("cnn", channels=3, hidden=8, layers=1, dropout=0.0, out_dim=NUM_CLASSES)
    cls_out = cls_model(torch.randn(2, 5, 3))
    assert cls_out.shape == (2, NUM_CLASSES), cls_out.shape

    # (h) Task8: --indicator-channel-export-paths round-trips as JSON (symbol -> path) for
    # composed_features, defaults to None when unset, is rejected outside composed_features
    # (mirroring --feature-spec's own rule), and malformed JSON fails fast.
    iep_json = json.dumps({"X": "/tmp/sa_indicator_channels_X.parquet"})
    iep_args = _parse_args([
        "--feature-mode", ds.COMPOSED_FEATURES_MODE, "--feature-spec", composed_spec_json,
        "--indicator-channel-export-paths", iep_json,
    ])
    assert json.loads(iep_args.indicator_channel_export_paths) == {
        "X": "/tmp/sa_indicator_channels_X.parquet"
    }
    assert args.indicator_channel_export_paths is None  # `args` from (a) above never set it

    try:
        _parse_args([
            "--feature-mode", "ohlcv_minmax", "--indicator-channel-export-paths", iep_json,
        ])
        raise AssertionError(
            "expected SystemExit for --indicator-channel-export-paths attached to a "
            "non-composed mode"
        )
    except SystemExit:
        pass

    try:
        _parse_args([
            "--feature-mode", ds.COMPOSED_FEATURES_MODE, "--feature-spec", composed_spec_json,
            "--indicator-channel-export-paths", "{not json",
        ])
        raise AssertionError("expected SystemExit for malformed --indicator-channel-export-paths JSON")
    except SystemExit:
        pass

    print("train_pytorch.py selfcheck: OK")


if __name__ == "__main__":
    if "--selfcheck" in sys.argv[1:]:
        _run_selfcheck()
    else:
        raise SystemExit(main(sys.argv[1:]))
