"""Train an LSTM / 1D-CNN trend predictor in PyTorch and export it to ONNX.

The exported graph is contract-compatible with the C# inference engine
(``StockAnalyzer.Core.Services.PredictionService``):

    input  : float32  [batch, window, channels]   node name "input"
    output : float32  [batch, 3]                   node name "output"  (softmax probabilities, class order Up, Down, Neutral)

Feature preprocessing and labeling come from :mod:`dataset`, which mirrors the
C# ``MLDataProcessor``. The model is trained on raw logits with
``CrossEntropyLoss``; a thin :class:`SoftmaxExport` wrapper adds the final
``Softmax`` so the ONNX file emits a probability distribution directly.

Examples
--------
Fast smoke run (few symbols, few epochs) with post-export ONNX verification::

    python train_pytorch.py --smoke

Full run::

    python train_pytorch.py --arch lstm --feature-mode ohlcv_minmax --epochs 30
"""

from __future__ import annotations

import argparse
import copy
import sys
import time
from pathlib import Path
from typing import Tuple

import numpy as np
import torch
from torch import nn

sys.path.insert(0, str(Path(__file__).resolve().parent))
import dataset as ds  # noqa: E402  (sibling module, path inserted above)
import metrics  # noqa: E402  (sibling module)
import onnx_meta  # noqa: E402  (sibling module)

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
    """2-layer LSTM over the window; last hidden state -> Linear(3) logits."""

    def __init__(self, channels: int, hidden: int, layers: int, dropout: float):
        super().__init__()
        self.lstm = nn.LSTM(
            input_size=channels,
            hidden_size=hidden,
            num_layers=layers,
            batch_first=True,
            dropout=dropout if layers > 1 else 0.0,
        )
        self.head = nn.Linear(hidden, NUM_CLASSES)

    def forward(self, x: torch.Tensor) -> torch.Tensor:  # x: [N, W, C] -> [N, 3] logits
        out, _ = self.lstm(x)
        return self.head(out[:, -1, :])


class CnnClassifier(nn.Module):
    """Two Conv1d blocks + global average pool -> Linear(3) logits."""

    def __init__(self, channels: int, hidden: int, dropout: float):
        super().__init__()
        self.features = nn.Sequential(
            nn.Conv1d(channels, max(hidden // 2, 1), kernel_size=3, padding=1),
            nn.ReLU(),
            nn.Conv1d(max(hidden // 2, 1), hidden, kernel_size=3, padding=1),
            nn.ReLU(),
            nn.AdaptiveAvgPool1d(1),
        )
        self.drop = nn.Dropout(dropout)
        self.head = nn.Linear(hidden, NUM_CLASSES)

    def forward(self, x: torch.Tensor) -> torch.Tensor:  # x: [N, W, C] -> [N, 3] logits
        z = self.features(x.transpose(1, 2)).squeeze(-1)
        return self.head(self.drop(z))


class SoftmaxExport(nn.Module):
    """Wraps a logits model so the exported ONNX emits a probability distribution."""

    def __init__(self, base: nn.Module):
        super().__init__()
        self.base = base

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        return torch.softmax(self.base(x), dim=-1)


def build_model(arch: str, channels: int, hidden: int, layers: int, dropout: float) -> nn.Module:
    if arch == "lstm":
        return LstmClassifier(channels, hidden, layers, dropout)
    if arch == "cnn":
        return CnnClassifier(channels, hidden, dropout)
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


def train_model(args: argparse.Namespace) -> Tuple[nn.Module, int, dict, dict]:
    torch.manual_seed(args.seed)
    rng = np.random.default_rng(args.seed)

    symbols, dates = ds.load_parquet_dir(args.data_dir, return_dates=True)
    if args.max_symbols is not None:
        symbols = dict(list(symbols.items())[: args.max_symbols])
    dates = {k: dates[k] for k in symbols}
    if not symbols:
        raise SystemExit(f"No parquet data under {args.data_dir}")

    # Per-symbol chronological split then pool: validation is always time-after-train
    # within each symbol, with a purge gap of window + horizon - 1 bars.
    x_tr_np, y_tr_np, x_va_np, y_va_np = ds.split_symbols_chronological(
        symbols, args.feature_mode, window=args.window,
        horizon=args.horizon, threshold=args.threshold, n_splits=args.wf_splits,
    )
    if x_tr_np.shape[0] == 0 or x_va_np.shape[0] == 0:
        raise SystemExit("Empty train or val split; check --window/--horizon/--wf-splits vs data length.")

    # Calendar spans of the same split, for the model-contract provenance metadata.
    date_ranges = ds.train_val_date_ranges(
        symbols, dates, feature_mode=args.feature_mode, window=args.window,
        horizon=args.horizon, threshold=args.threshold, n_splits=args.wf_splits,
    )

    channels = ds.feature_channels(args.feature_mode)
    x_tr = torch.from_numpy(x_tr_np)
    y_tr = torch.from_numpy(y_tr_np)
    x_va = torch.from_numpy(x_va_np)
    y_va = torch.from_numpy(y_va_np)

    model = build_model(args.arch, channels, args.hidden, args.layers, args.dropout)
    optimizer = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=args.weight_decay)
    loss_fn = nn.CrossEntropyLoss(weight=_class_weights(y_tr_np))

    best_val = float("inf")
    best_state = copy.deepcopy(model.state_dict())
    patience_ctr = 0

    print(
        f"arch={args.arch} feature_mode={args.feature_mode} channels={channels} "
        f"train={len(x_tr_np)} val={len(x_va_np)} symbols={len(symbols)}"
    )
    for epoch in range(1, args.epochs + 1):
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
            val_logits = model(x_va)
            val_loss = loss_fn(val_logits, y_va).item()
            val_acc = (val_logits.argmax(dim=-1) == y_va).float().mean().item()

        print(
            f"epoch {epoch:3d}/{args.epochs}  train_loss={train_loss:.4f}  "
            f"val_loss={val_loss:.4f}  val_acc={val_acc:.3f}  ({time.time() - t0:.1f}s)"
        )

        if val_loss < best_val - EARLY_STOP_MIN_DELTA:
            best_val = val_loss
            best_state = copy.deepcopy(model.state_dict())
            patience_ctr = 0
        else:
            patience_ctr += 1
            if patience_ctr >= args.patience:
                print(f"early stop at epoch {epoch} (no val improvement for {args.patience} epochs)")
                break

    model.load_state_dict(best_state)
    model.eval()

    with torch.no_grad():
        val_probs = torch.softmax(model(x_va), dim=-1).cpu().numpy()
    report = metrics.classification_report_dict(
        y_va_np, val_probs.argmax(axis=1), probs=val_probs
    )
    print(metrics.format_report(report))
    return model, channels, report, date_ranges


# --- Export & verification -------------------------------------------------


def export_onnx(
    model: nn.Module, channels: int, window: int, out_path: Path, opset: int,
    metadata: dict | None = None,
) -> None:
    out_path.parent.mkdir(parents=True, exist_ok=True)
    wrapper = SoftmaxExport(model).eval()
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


def verify_onnx(out_path: Path, model: nn.Module, channels: int, window: int) -> None:
    import onnx
    import onnxruntime as ort

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
        assert out_meta.shape[1] == NUM_CLASSES, out_meta.shape

    x = np.random.randn(1, window, channels).astype(np.float32)
    (probs,) = session.run(["output"], {"input": x})
    assert probs.shape == (1, NUM_CLASSES), probs.shape
    assert np.isfinite(probs).all()
    assert (probs >= 0.0).all() and (probs <= 1.0).all()
    assert abs(float(probs.sum()) - 1.0) < ONNX_ATOL

    with torch.no_grad():
        ref = torch.softmax(model(torch.from_numpy(x)), dim=-1).numpy()
    max_delta = float(np.max(np.abs(ref - probs)))
    assert max_delta < ONNX_ATOL, f"torch vs onnxruntime delta {max_delta:.2e} >= {ONNX_ATOL:.0e}"

    # dynamic batch sanity
    (batch_probs,) = session.run(["output"], {"input": np.random.randn(4, window, channels).astype(np.float32)})
    assert batch_probs.shape == (4, NUM_CLASSES), batch_probs.shape

    print(f"onnx verify: OK  (torch-vs-ort max delta {max_delta:.2e}, opset checked, dynamic batch OK)")


# --- CLI --------------------------------------------------------------------


def _parse_args(argv) -> argparse.Namespace:
    p = argparse.ArgumentParser(description="Train an LSTM/1D-CNN trend predictor and export to ONNX.")
    p.add_argument("--data-dir", type=Path, default=ds.DEFAULT_DATA_DIR)
    p.add_argument("--feature-mode", choices=ds.FEATURE_MODES, default="ohlcv_minmax")
    p.add_argument("--arch", choices=ARCHES, default=DEFAULT_ARCH)
    p.add_argument("--window", type=int, default=ds.DEFAULT_WINDOW)
    p.add_argument("--horizon", type=int, default=ds.DEFAULT_HORIZON)
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
    if args.smoke:
        if args.max_symbols is None:
            args.max_symbols = SMOKE_MAX_SYMBOLS
        args.epochs = min(args.epochs, SMOKE_EPOCHS)
    return args


def main(argv) -> int:
    args = _parse_args(argv)
    model, channels, report, date_ranges = train_model(args)
    contract = onnx_meta.build_contract(
        feature_mode=args.feature_mode, window_size=args.window, channels=channels,
        horizon=args.horizon, threshold=args.threshold, wf_splits=args.wf_splits,
        seed=args.seed, producer=f"train_pytorch.py arch={args.arch}",
        price_adjustment=args.price_adjustment, date_ranges=date_ranges,
    )
    export_onnx(model, channels, args.window, args.out, args.opset, contract)
    print(f"wrote {metrics.write_report(report, args.out)}")
    if not args.no_verify:
        verify_onnx(args.out, model, channels, args.window)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
