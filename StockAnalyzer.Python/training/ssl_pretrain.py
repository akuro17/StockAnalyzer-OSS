"""T13-B4 self-supervised masked-reconstruction pretraining (PyTorch only).

Approved definition (13-10=A, 13-11, 13-12)::

    for every training window x [W, C] (the model's own input space, after its preprocessing):
        M      = ceil(rate * W) distinct bars (0.30 by default; clamped to [1, W-1]) chosen uniformly at random
        x~     = x with the bars in M set to 0.0 (the mask token)
        x^     = decoder(encoder(x~))            # per-bar linear head, discarded afterwards
        loss   = mean over masked bars and channels of (x^ - x)^2

The unlabeled windows are the *fit population of the fold* (the train side the supervised stage fits
on) -- never the inner validation, calibration or outer test rows. The pretrained ``state_dict`` of the
encoder (every parameter outside ``head.*``) is written as an immutable hashed blob plus a verified
pointer ``ssl_pretrain.json`` in the fold checkpoint directory and loaded back strictly before the
supervised stage; the classification / regression head is newly initialised, so the ONNX output
contract is unchanged. Keras and LightGBM do not support this method.

Numeric classification: ``MASK_TOKEN`` is the approved constant of the algorithm; ``DEFAULT_MASK_RATE`` and
``DEFAULT_EPOCHS`` are tunable defaults (``--ssl-mask-rate`` / hyperparameter ``ssl_mask_rate``, ``--ssl-epochs`` /
``ssl_epochs``). Dropout is switched off for the duration of pretraining (the reconstruction target must not be noised
by the model itself) and restored afterwards, so the supervised stage trains exactly as before.

What the pretraining teaches depends on the encoder's receptive field, which the report records as ``encoder_context``:
a unidirectional recurrent encoder is ``causal`` (a masked bar is inferred from earlier bars only), a convolutional one
is ``local_bidirectional`` (a masked bar may be inferred from a few bars after it too, ``encoder_radius_bars``).

Run ``python ssl_pretrain.py --selfcheck`` to verify the mask and the loss.
"""

from __future__ import annotations

import argparse
import contextlib
import fractions
import math
import os
import sys
import tempfile
from pathlib import Path
from typing import Dict

import numpy as np
import torch
from torch import nn

sys.path.insert(0, str(Path(__file__).resolve().parent))
import checkpoints  # noqa: E402
import onnx_meta  # noqa: E402

SCHEMA_VERSION = 1
KIND = "ssl_pretrain"
METHOD = onnx_meta.PRETRAINING_MASKED_RECONSTRUCTION
POINTER_NAME = "ssl_pretrain.json"
DEFAULT_MASK_RATE = 0.30
MASK_TOKEN = 0.0
DEFAULT_EPOCHS = 5
HEAD_PREFIX = "head."
ENCODER_CAUSAL = "causal"
ENCODER_LOCAL_BIDIRECTIONAL = "local_bidirectional"


def validate_mask_rate(rate: object) -> float:
    """The mask share of a window: a finite real strictly between 0 and 1 (not a bool)."""
    if type(rate) not in (int, float) or not math.isfinite(rate) or not 0 < rate < 1:
        raise ValueError("the SSL mask rate must be a number strictly between 0 and 1")
    return float(rate)


def mask_count(window: int, rate: float = DEFAULT_MASK_RATE) -> int:
    """``ceil(rate * window)`` exactly (rational arithmetic), at least one bar, at most ``window - 1``."""
    if type(window) is not int or window < 2:
        raise ValueError("masked reconstruction needs a window of at least two bars")
    return min(window - 1, max(1, math.ceil(fractions.Fraction(repr(validate_mask_rate(rate))) * window)))


def sample_mask(rng: np.random.Generator, rows: int, window: int, rate: float = DEFAULT_MASK_RATE) -> np.ndarray:
    """``[rows, window]`` boolean mask with exactly ``mask_count(window, rate)`` uniformly chosen bars per row."""
    k = mask_count(window, rate)
    ranks = rng.random((rows, window)).argsort(axis=1).argsort(axis=1)
    return ranks < k


def apply_mask(x: torch.Tensor, mask: torch.Tensor) -> torch.Tensor:
    return torch.where(mask.unsqueeze(-1), torch.full_like(x, MASK_TOKEN), x)


def masked_mse(reconstruction: torch.Tensor, target: torch.Tensor, mask: torch.Tensor) -> torch.Tensor:
    """Mean squared error over the masked bars only (all channels of each masked bar)."""
    selector = mask.unsqueeze(-1).to(reconstruction.dtype)
    squared = (reconstruction - target) ** 2 * selector
    return squared.sum() / (selector.sum() * reconstruction.shape[-1])


def encoder_keys(model: nn.Module) -> list[str]:
    return [key for key in model.state_dict() if not key.startswith(HEAD_PREFIX)]


@contextlib.contextmanager
def dropout_disabled(model: nn.Module):
    """Switch every dropout of ``model`` off (``nn.Dropout`` modules and the inter-layer dropout of recurrent modules)
    and restore the configured probabilities on exit, whatever happens inside."""
    saved = []
    for module in model.modules():
        if isinstance(module, nn.modules.dropout._DropoutNd):
            saved.append((module, "p", module.p))
            module.p = 0.0
        elif isinstance(module, nn.RNNBase):
            saved.append((module, "dropout", module.dropout))
            module.dropout = 0.0
    try:
        yield
    finally:
        for module, attribute, value in saved:
            setattr(module, attribute, value)


def encoder_context(model: nn.Module) -> Dict[str, object]:
    """How far from a bar the encoder may look: ``causal`` for unidirectional recurrent encoders, otherwise
    ``local_bidirectional`` with the summed same-padding radius (in bars) of the convolutions."""
    recurrent = [m for m in model.modules() if isinstance(m, nn.RNNBase)]
    if recurrent:
        if any(m.bidirectional for m in recurrent):
            return {"encoder_context": "bidirectional", "encoder_radius_bars": None}
        return {"encoder_context": ENCODER_CAUSAL, "encoder_radius_bars": 0}
    radius = sum(m.dilation[0] * (m.kernel_size[0] - 1) // 2 for m in model.modules() if isinstance(m, nn.Conv1d))
    if radius == 0:
        raise ValueError("the encoder has neither a recurrent nor a convolutional stack to describe")
    return {"encoder_context": ENCODER_LOCAL_BIDIRECTIONAL, "encoder_radius_bars": int(radius)}


def pretrain(model: nn.Module, x_train: torch.Tensor, *, epochs: int, batch: int, lr: float,
             weight_decay: float, rng: np.random.Generator,
             mask_rate: float = DEFAULT_MASK_RATE) -> Dict[str, object]:
    """Fit ``model``'s encoder by masked reconstruction on ``x_train`` and return the report."""
    if epochs < 1 or batch < 1:
        raise ValueError("SSL epochs and batch must be positive")
    if x_train.ndim != 3 or x_train.shape[0] == 0:
        raise ValueError("SSL pretraining needs a non-empty [N, W, C] window tensor")
    window, channels = int(x_train.shape[1]), int(x_train.shape[2])
    rate = validate_mask_rate(mask_rate)
    count = mask_count(window, rate)
    context = encoder_context(model)
    decoder = nn.Linear(int(model.head.in_features), channels)
    parameters = [p for name, p in model.named_parameters() if not name.startswith(HEAD_PREFIX)]
    parameters += list(decoder.parameters())
    optimizer = torch.optim.AdamW(parameters, lr=lr, weight_decay=weight_decay)
    losses = []
    model.train()
    with dropout_disabled(model):
        for _epoch in range(epochs):
            order = np.arange(x_train.shape[0])
            rng.shuffle(order)
            running, seen = 0.0, 0
            for start in range(0, len(order), batch):
                index = order[start:start + batch]
                xb = x_train[index]
                mask = torch.from_numpy(sample_mask(rng, len(index), window, rate))
                optimizer.zero_grad()
                loss = masked_mse(decoder(model.encode_sequence(apply_mask(xb, mask))), xb, mask)
                if not torch.isfinite(loss):
                    raise ValueError("nonfinite self-supervised loss")
                loss.backward()
                optimizer.step()
                running += float(loss.item()) * len(index)
                seen += len(index)
            losses.append(running / max(seen, 1))
    return {"method": METHOD, "mask_rate": rate, "mask_token": MASK_TOKEN, "mask_count": count,
            "mask_distribution": "random_bars", "epochs": epochs, "train_windows": int(x_train.shape[0]),
            "dropout_during_pretraining": False, **context,
            "loss_target": "model_input_space_mse_masked_bars", "loss_first": losses[0], "loss_last": losses[-1],
            "loss": losses}


def save_encoder(root: Path, model: nn.Module, context: dict, report: dict) -> Path:
    """Write the encoder ``state_dict`` as an immutable hashed blob, then its atomic pointer."""
    root = Path(root)
    root.mkdir(parents=True, exist_ok=True)
    state = {key: value.detach().cpu().clone() for key, value in model.state_dict().items()
             if not key.startswith(HEAD_PREFIX)}
    fd, name = tempfile.mkstemp(prefix=".ssl_", suffix=".pt", dir=root)
    os.close(fd)
    temporary = Path(name)
    try:
        torch.save(state, temporary)
        digest = checkpoints.hash_file(temporary)
        blob = root / f"ssl_{digest}.pt"
        if blob.exists():
            if checkpoints.hash_file(blob) != digest:
                raise ValueError("immutable SSL blob was corrupted")
        else:
            checkpoints.replace_with_retry(temporary, blob)
    finally:
        temporary.unlink(missing_ok=True)
    pointer = root / POINTER_NAME
    checkpoints.atomic_json(pointer, dict(schema_version=SCHEMA_VERSION, kind=KIND, blob=blob.name, sha256=digest,
                                          context=context, report=report))
    return pointer


def load_encoder(pointer: Path, model: nn.Module, context: dict) -> dict:
    """Verify the pointer, blob hash and context, then load the encoder weights strictly into ``model``."""
    pointer = Path(pointer)
    value = checkpoints.read_json(pointer)
    name, digest = value.get("blob"), value.get("sha256")
    if (value.get("kind") != KIND or not isinstance(name, str) or Path(name).name != name
            or not isinstance(digest, str) or name != f"ssl_{digest}.pt"):
        raise ValueError("invalid SSL checkpoint reference")
    blob = (pointer.parent / name).resolve()
    if blob.parent != pointer.parent.resolve() or not blob.is_file() or checkpoints.hash_file(blob) != digest:
        raise ValueError("SSL checkpoint missing or hash mismatch")
    if value.get("context") != context:
        raise ValueError("SSL checkpoint context disagrees with this fold")
    state = torch.load(blob, map_location="cpu", weights_only=True)
    expected = set(encoder_keys(model))
    if (not isinstance(state, dict) or set(state) != expected
            or any(not isinstance(v, torch.Tensor) or not torch.isfinite(v.float()).all() for v in state.values())):
        raise ValueError("SSL encoder state does not match the model encoder")
    result = model.load_state_dict(state, strict=False)
    if result.unexpected_keys or any(not key.startswith(HEAD_PREFIX) for key in result.missing_keys):
        raise ValueError("SSL encoder state does not match the model encoder")
    return value["report"]


def run(model: nn.Module, x_train: torch.Tensor, *, epochs: int, batch: int, lr: float, weight_decay: float,
        rng: np.random.Generator, root: Path, context: dict,
        mask_rate: float = DEFAULT_MASK_RATE) -> Dict[str, object]:
    """Pretrain, persist, verify and reload the encoder; return the report recorded in the run metrics."""
    report = pretrain(model, x_train, epochs=epochs, batch=batch, lr=lr, weight_decay=weight_decay, rng=rng,
                      mask_rate=mask_rate)
    pointer = save_encoder(root, model, context, report)
    # the supervised stage starts from the verified blob, not from live memory
    loaded = load_encoder(pointer, model, context)
    if loaded != report:
        raise ValueError("SSL report changed between save and load")
    return dict(report, checkpoint_sha256=checkpoints.read_json(pointer)["sha256"])


# --- self-verification ----------------------------------------------------------------------------


def _toy_model(channels: int, hidden: int = 8, layers: int = 1, dropout: float = 0.0) -> nn.Module:
    class Toy(nn.Module):
        def __init__(self) -> None:
            super().__init__()
            self.lstm = nn.LSTM(channels, hidden, num_layers=layers, batch_first=True,
                                dropout=dropout if layers > 1 else 0.0)
            self.head = nn.Linear(hidden, 3)

        def encode_sequence(self, x: torch.Tensor) -> torch.Tensor:
            out, _ = self.lstm(x)
            return out

        def forward(self, x: torch.Tensor) -> torch.Tensor:
            return self.head(self.encode_sequence(x)[:, -1, :])

    return Toy()


def _run_selfcheck() -> None:
    # (1) mask size: ceil(0.30 * W), exact, clamped to [1, W-1]
    assert mask_count(10) == 3 and mask_count(75) == 23 and mask_count(20) == 6 and mask_count(2) == 1
    assert mask_count(3) == 1 and mask_count(4) == 2
    for bad in (1, 0, 2.5):
        try:
            mask_count(bad)
        except ValueError:
            pass
        else:  # pragma: no cover
            raise AssertionError("window must be an integer of at least two")

    # (2) every row masks exactly k distinct bars; deterministic per generator; roughly uniform
    mask = sample_mask(np.random.default_rng(1), 4000, 20)
    assert mask.shape == (4000, 20) and (mask.sum(axis=1) == 6).all()
    assert np.array_equal(mask, sample_mask(np.random.default_rng(1), 4000, 20))
    assert not np.array_equal(mask, sample_mask(np.random.default_rng(2), 4000, 20))
    assert np.abs(mask.mean(axis=0) - 0.3).max() < 0.04

    # (3) the mask token replaces the masked bars (all channels) and nothing else
    x = torch.arange(2 * 4 * 3, dtype=torch.float32).reshape(2, 4, 3) + 1.0
    m = torch.tensor([[True, False, False, True], [False, False, True, False]])
    masked = apply_mask(x, m)
    assert (masked[0, 0] == MASK_TOKEN).all() and (masked[0, 3] == MASK_TOKEN).all() and (masked[1, 2] == MASK_TOKEN).all()
    assert torch.equal(masked[0, 1:3], x[0, 1:3]) and torch.equal(masked[1, 0], x[1, 0])

    # (4) masked MSE: hand value, and unmasked bars never contribute
    target = torch.zeros(1, 3, 2)
    recon = torch.tensor([[[1.0, 3.0], [100.0, 100.0], [2.0, 0.0]]])
    bars = torch.tensor([[True, False, True]])
    expected = (1.0 + 9.0 + 4.0 + 0.0) / (2 * 2)
    assert abs(float(masked_mse(recon, target, bars)) - expected) < 1e-6

    # (5) pretraining lowers the masked reconstruction loss on a learnable signal and writes a verified blob
    torch.manual_seed(0)
    t = torch.linspace(0, 6.0, 12)
    base = torch.stack([torch.sin(t + phase) for phase in np.linspace(0, 3, 64)])  # [64, 12]
    data = torch.stack([base, base * 0.5 + 0.1], dim=-1).float()  # [64, 12, 2] smooth, predictable bars
    model = _toy_model(2)
    head_before = model.head.weight.detach().clone()
    with tempfile.TemporaryDirectory() as tmp:
        context = {"arch": "toy", "channels": 2, "window": 12, "fold": 0}
        report = run(model, data, epochs=40, batch=16, lr=5e-3, weight_decay=0.0,
                     rng=np.random.default_rng(0), root=Path(tmp), context=context)
        assert report["loss_last"] < report["loss_first"] * 0.9 and len(report["loss"]) == 40
        assert report["mask_count"] == 4 and report["method"] == METHOD
        assert torch.equal(model.head.weight, head_before), "the head is never trained by pretraining"
        pointer = Path(tmp) / POINTER_NAME
        fresh = _toy_model(2)
        loaded = load_encoder(pointer, fresh, context)
        assert all(torch.equal(fresh.state_dict()[k], model.state_dict()[k]) for k in encoder_keys(model))
        assert loaded["epochs"] == 40
        # a different context, a corrupted blob and a foreign state are all rejected
        for bad_context in (dict(context, fold=1), dict(context, window=13)):
            try:
                load_encoder(pointer, _toy_model(2), bad_context)
            except ValueError:
                pass
            else:  # pragma: no cover
                raise AssertionError("context mismatch must be rejected")
        blob = next(Path(tmp).glob("ssl_*.pt"))
        blob.write_bytes(blob.read_bytes() + b"x")
        try:
            load_encoder(pointer, _toy_model(2), context)
        except ValueError:
            pass
        else:  # pragma: no cover
            raise AssertionError("corrupted blob must be rejected")
    try:
        pretrain(_toy_model(2), torch.zeros(0, 12, 2), epochs=1, batch=4, lr=1e-3, weight_decay=0.0,
                 rng=np.random.default_rng(0))
    except ValueError:
        pass
    else:  # pragma: no cover
        raise AssertionError("empty window tensor must be rejected")
    # (6) the mask rate is tunable: exact rational ceil clamped to [1, W-1]; invalid rates are rejected
    assert [mask_count(w, 0.5) for w in (2, 3, 10)] == [1, 2, 5]
    assert [mask_count(w, 0.01) for w in (2, 3, 10)] == [1, 1, 1]            # at least one bar
    assert [mask_count(w, 0.99) for w in (2, 3, 10)] == [1, 2, 9]            # never the whole window
    assert mask_count(10, 0.3) == 3 and mask_count(10, 0.30000001) == 4      # exact: 3.0 stays 3, a hair above is 4
    assert mask_count(10) == mask_count(10, DEFAULT_MASK_RATE) == 3
    wide = sample_mask(np.random.default_rng(3), 500, 10, 0.5)
    assert (wide.sum(axis=1) == 5).all() and wide.shape == (500, 10)
    for bad_rate in (0, 1, 0.0, 1.0, -0.1, 1.5, float("nan"), float("inf"), "0.3", True, None):
        try:
            validate_mask_rate(bad_rate)
        except ValueError:
            pass
        else:  # pragma: no cover
            raise AssertionError(f"mask rate {bad_rate!r} must be rejected")
    data6 = torch.stack([torch.sin(torch.linspace(0, 6.0, 12) + p) for p in np.linspace(0, 3, 32)]).unsqueeze(-1).float()
    torch.manual_seed(1)
    rate_report = pretrain(_toy_model(1), data6, epochs=1, batch=16, lr=1e-3, weight_decay=0.0,
                           rng=np.random.default_rng(0), mask_rate=0.5)
    assert rate_report["mask_rate"] == 0.5 and rate_report["mask_count"] == 6

    # (7) dropout is off during pretraining and restored after; the encoder's receptive field is recorded
    def pretrain_with(dropout: float):
        torch.manual_seed(5)
        model = _toy_model(1, layers=2, dropout=dropout)
        torch.manual_seed(6)
        report = pretrain(model, data6, epochs=3, batch=16, lr=5e-3, weight_decay=0.0, rng=np.random.default_rng(0))
        return report, model
    noisy_report, noisy_model = pretrain_with(0.5)
    quiet_report, _ = pretrain_with(0.0)
    assert noisy_report["loss"] == quiet_report["loss"], "a configured dropout must not change the pretraining"
    assert noisy_model.lstm.dropout == 0.5 and noisy_model.training, "the supervised stage keeps its dropout"
    assert noisy_report["dropout_during_pretraining"] is False
    assert (noisy_report["encoder_context"], noisy_report["encoder_radius_bars"]) == (ENCODER_CAUSAL, 0)
    with dropout_disabled(noisy_model):
        assert noisy_model.lstm.dropout == 0.0
        try:
            raise RuntimeError("inside")
        except RuntimeError:
            pass
    assert noisy_model.lstm.dropout == 0.5
    try:
        with dropout_disabled(noisy_model):
            raise RuntimeError("fail inside")
    except RuntimeError:
        pass
    assert noisy_model.lstm.dropout == 0.5, "restored even when the body fails"
    drop_model = nn.Sequential(nn.Dropout(0.3), nn.Linear(2, 2))
    with dropout_disabled(drop_model):
        assert drop_model[0].p == 0.0
    assert drop_model[0].p == 0.3

    class ToyCnn(nn.Module):
        def __init__(self) -> None:
            super().__init__()
            self.features = nn.Sequential(nn.Conv1d(1, 4, kernel_size=3, padding=1), nn.ReLU(),
                                          nn.Conv1d(4, 8, kernel_size=3, padding=1))
            self.head = nn.Linear(8, 3)

        def encode_sequence(self, x: torch.Tensor) -> torch.Tensor:
            return self.features(x.transpose(1, 2)).transpose(1, 2)

    assert encoder_context(ToyCnn()) == {"encoder_context": ENCODER_LOCAL_BIDIRECTIONAL, "encoder_radius_bars": 2}
    cnn_report = pretrain(ToyCnn(), data6, epochs=1, batch=16, lr=1e-3, weight_decay=0.0, rng=np.random.default_rng(0))
    assert cnn_report["encoder_context"] == "local_bidirectional" and cnn_report["encoder_radius_bars"] == 2
    assert encoder_context(nn.LSTM(1, 4, bidirectional=True))["encoder_context"] == "bidirectional"
    try:
        encoder_context(nn.Linear(2, 2))
    except ValueError:
        pass
    else:  # pragma: no cover
        raise AssertionError("an encoder without a recurrent or convolutional stack cannot be described")
    print("ssl_pretrain.py selfcheck: OK (7 groups passed)")


def _parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="T13 masked-reconstruction pretraining helpers.")
    parser.add_argument("--selfcheck", action="store_true", help="Run the assertions and exit.")
    return parser.parse_args(argv)


if __name__ == "__main__":
    if _parse_args(sys.argv[1:]).selfcheck:
        _run_selfcheck()
