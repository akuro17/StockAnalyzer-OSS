"""T24a attention-capable architecture (``attn_lstm``) and its attention sidecar ONNX.

Approved decisions (2026-10-11, ``Y:\\Temp\\sa_decision_options_onnx_meta_attention_gnn_t23_t25.md`` star rows A0, A1, A2, A3, S9):

* A0 A the production ONNX keeps ONE float32 ``output`` (Decision 13-13=A, ``API_SPECIFICATION`` multi-head decision). The attention weights are never a second
  output of the production model.
* A1 A ``attn_lstm`` = LSTM -> ONE causal multi-head self-attention layer (``--heads``, default 4, a job hyperparameter) -> the context vector of the LAST bar ->
  Linear. The attention layer is written out here (query/key/value projections, scaled dot product, causal mask, softmax, output projection) instead of
  ``nn.MultiheadAttention`` so the exported graph is plain MatMul / Softmax / Where and the weights are exactly what the model used.
* A2 A / S9 A a SEPARATE optional derived artifact ``<model>.onnx.attention.onnx`` beside the production file: input ``input`` (the SAME post-preprocessing tensor
  ``[batch, window, channels]`` as the production model), output ``attention`` ``[batch, heads, window, window]`` float32. It is not a registry sidecar and
  carries no key in the production metadata (``onnx_meta.CONTRACT_KEYS`` and ``model_contract_version`` are unchanged). Its own metadata binds it to the
  parent: parent SHA-256, window, heads, feature-contract hash, layout, mask and unit. A missing, foreign or stale sidecar is "unsupported", never an error.
* A3 A the layer is the only attention layer. Mask = causal lower triangle: query ``q`` sees keys ``k <= q``; a future position has weight exactly 0 and every row
  is a softmax (sum 1). The display row is the LAST query (the anchor) over the ``window`` keys (T24b); the heads are averaged by the consumer. The unit is
  "attention weight" (dimensionless, in [0, 1]); it is not a price and not a probability of the prediction.

Nothing here is computed during prediction or rendering; the sidecar is written once beside the model.
"""

from __future__ import annotations

import hashlib
import math
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

SCHEMA_VERSION: int = 1
ARCH: str = "attn_lstm"
DEFAULT_HEADS: int = 4                      # planning value, no empirical basis (job hyperparameter --heads)
MAX_HEADS: int = 16                         # layer-3 safety bound; mirrored by the T24b AttentionContract
INPUT_NAME: str = "input"
OUTPUT_NAME: str = "attention"
SIDECAR_SUFFIX: str = ".attention.onnx"     # appended to the production model path: <model>.onnx.attention.onnx
KEY_PREFIX: str = "com.stockanalyzer.attention."
KEY_SCHEMA: str = KEY_PREFIX + "schema_version"
KEY_PARENT_SHA256: str = KEY_PREFIX + "parent_sha256"
KEY_WINDOW: str = KEY_PREFIX + "window_size"
KEY_HEADS: str = KEY_PREFIX + "heads"
KEY_FEATURE_HASH: str = KEY_PREFIX + "feature_contract_hash"
KEY_LAYOUT: str = KEY_PREFIX + "layout"
KEY_MASK: str = KEY_PREFIX + "mask"
KEY_UNIT: str = KEY_PREFIX + "unit"
BINDING_KEYS = (KEY_SCHEMA, KEY_PARENT_SHA256, KEY_WINDOW, KEY_HEADS, KEY_FEATURE_HASH, KEY_LAYOUT, KEY_MASK, KEY_UNIT)
LAYOUT: str = "batch_head_query_key"
MASK: str = "causal_lower_triangular"
UNIT: str = "attention_weight"
ONNX_ATOL: float = 1e-4                     # torch-vs-onnxruntime float32 tolerance, as train_pytorch.ONNX_ATOL
ROW_SUM_ATOL: float = 1e-4
INCOMING_SUFFIX: str = ".incoming"


def validate_heads(hidden: int, heads: int) -> None:
    if type(heads) is not int or not 1 <= heads <= MAX_HEADS:
        raise ValueError(f"heads must be an integer in 1..{MAX_HEADS}, got {heads!r}")
    if hidden % heads:
        raise ValueError(f"hidden ({hidden}) must be a multiple of heads ({heads})")


class CausalSelfAttention(nn.Module):
    """One multi-head self-attention layer with a causal mask. ``forward`` returns ``(output [N, W, D], weights [N, H, W, W])``."""

    def __init__(self, dim: int, heads: int):
        super().__init__()
        validate_heads(dim, heads)
        self.heads = heads
        self.head_dim = dim // heads
        self.scale = 1.0 / math.sqrt(self.head_dim)
        self.query = nn.Linear(dim, dim)
        self.key = nn.Linear(dim, dim)
        self.value = nn.Linear(dim, dim)
        self.proj = nn.Linear(dim, dim)

    def _split(self, x: torch.Tensor) -> torch.Tensor:           # [N, W, D] -> [N, H, W, head_dim]
        n, w, _ = x.shape
        return x.reshape(n, w, self.heads, self.head_dim).transpose(1, 2)

    def forward(self, x: torch.Tensor):
        n, w, d = x.shape
        q, k, v = self._split(self.query(x)), self._split(self.key(x)), self._split(self.value(x))
        scores = torch.matmul(q, k.transpose(-2, -1)) * self.scale                     # [N, H, W, W]
        future = torch.triu(torch.ones(w, w, dtype=torch.bool, device=x.device), diagonal=1)
        weights = torch.softmax(scores.masked_fill(future, float("-inf")), dim=-1)
        context = torch.matmul(weights, v).transpose(1, 2).reshape(n, w, d)
        return self.proj(context), weights


class AttentionLstmClassifier(nn.Module):
    """LSTM -> causal multi-head self-attention -> the last bar's context vector -> Linear(out_dim)."""

    def __init__(self, channels: int, hidden: int, layers: int, dropout: float, out_dim: int, heads: int = DEFAULT_HEADS):
        super().__init__()
        validate_heads(hidden, heads)
        self.lstm = nn.LSTM(input_size=channels, hidden_size=hidden, num_layers=layers, batch_first=True,
                            dropout=dropout if layers > 1 else 0.0)
        self.attn = CausalSelfAttention(hidden, heads)
        self.head = nn.Linear(hidden, out_dim)
        self.heads = heads

    def encode_sequence(self, x: torch.Tensor) -> torch.Tensor:  # x: [N, W, C] -> [N, W, hidden]
        out, _ = self.lstm(x)
        return out

    def attention_weights(self, x: torch.Tensor) -> torch.Tensor:  # x: [N, W, C] -> [N, H, W, W]
        return self.attn(self.encode_sequence(x))[1]

    def forward(self, x: torch.Tensor) -> torch.Tensor:          # x: [N, W, C] -> [N, out_dim]
        context, _ = self.attn(self.encode_sequence(x))
        return self.head(context[:, -1, :])


class AttentionWeights(nn.Module):
    """Export wrapper: the same weights as the production model, but the attention matrix is the only output."""

    def __init__(self, model: AttentionLstmClassifier):
        super().__init__()
        self.model = model

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        return self.model.attention_weights(x)


# --- sidecar ----------------------------------------------------------------------------------------------------------------------


def sidecar_path(model_path: Path | str) -> Path:
    return Path(str(model_path) + SIDECAR_SUFFIX)


def _sha256(path: Path) -> str:
    return checkpoints.hash_file(path)


def binding(parent_path: Path, *, window: int, heads: int, feature_contract_hash: str) -> Dict[str, str]:
    """The metadata that ties a sidecar to exactly one production file."""
    return {KEY_SCHEMA: str(SCHEMA_VERSION), KEY_PARENT_SHA256: _sha256(parent_path), KEY_WINDOW: str(int(window)), KEY_HEADS: str(int(heads)),
            KEY_FEATURE_HASH: feature_contract_hash, KEY_LAYOUT: LAYOUT, KEY_MASK: MASK, KEY_UNIT: UNIT}


def read_binding(path: Path | str) -> Dict[str, str]:
    import onnx
    meta = {p.key: p.value for p in onnx.load(str(path)).metadata_props}
    missing = [k for k in BINDING_KEYS if k not in meta]
    if missing:
        raise ValueError(f"attention sidecar lacks metadata: {', '.join(missing)}")
    return {k: meta[k] for k in BINDING_KEYS}


def check_binding(sidecar: Path | str, parent: Path | str) -> Dict[str, str]:
    """Raise ``ValueError`` unless ``sidecar`` is the attention sidecar of exactly this ``parent`` file; return its binding."""
    sidecar, parent = Path(sidecar), Path(parent)
    if sidecar.name != parent.name + SIDECAR_SUFFIX:
        raise ValueError("an attention sidecar must be named <model>" + SIDECAR_SUFFIX)
    found = read_binding(sidecar)
    if found[KEY_SCHEMA] != str(SCHEMA_VERSION) or found[KEY_LAYOUT] != LAYOUT or found[KEY_MASK] != MASK or found[KEY_UNIT] != UNIT:
        raise ValueError("attention sidecar schema, layout, mask or unit is not supported")
    if found[KEY_PARENT_SHA256] != _sha256(parent):
        raise ValueError("attention sidecar belongs to another generation of the model (parent SHA-256 differs)")
    return found


def export_sidecar(model: AttentionLstmClassifier, parent_path: Path, *, window: int, channels: int, opset: int, feature_contract_hash: str) -> Path:
    """Write ``<parent>.attention.onnx`` (built as ``.incoming`` and renamed once). The parent must already be final: its SHA-256 is recorded."""
    parent_path = Path(parent_path)
    target = sidecar_path(parent_path)
    mapping = binding(parent_path, window=window, heads=model.heads, feature_contract_hash=feature_contract_hash)
    temporary = target.with_name(target.name + INCOMING_SUFFIX)
    temporary.unlink(missing_ok=True)
    try:
        wrapper = AttentionWeights(model).eval()
        torch.onnx.export(wrapper, torch.randn(1, window, channels, dtype=torch.float32), str(temporary), input_names=[INPUT_NAME],
                          output_names=[OUTPUT_NAME], dynamic_axes={INPUT_NAME: {0: "batch"}, OUTPUT_NAME: {0: "batch"}}, opset_version=opset,
                          dynamo=False)  # same stable exporter as train_pytorch.export_onnx (C# ORT 1.24.3)
        onnx_meta.load_apply_save(temporary, mapping)
        checkpoints.replace_with_retry(temporary, target)
    finally:
        temporary.unlink(missing_ok=True)
    return target


def verify_sidecar(sidecar: Path, model: AttentionLstmClassifier, parent_path: Path, *, window: int, channels: int) -> float:
    """Structural and numeric checks of a written sidecar; returns the largest torch-vs-ORT difference. Raises on any violation."""
    import onnx
    import onnxruntime as ort

    onnx.checker.check_model(onnx.load(str(sidecar)))
    check_binding(sidecar, parent_path)
    session = ort.InferenceSession(str(sidecar), providers=["CPUExecutionProvider"])
    (inp,), (out,) = session.get_inputs(), session.get_outputs()
    if inp.name != INPUT_NAME or out.name != OUTPUT_NAME or len(inp.shape) != 3 or len(out.shape) != 4:
        raise ValueError(f"unexpected sidecar signature: {inp.name}{inp.shape} -> {out.name}{out.shape}")
    heads = model.heads
    rng = np.random.default_rng(0)
    worst = 0.0
    for batch in (1, 3):                                         # dynamic batch
        x = rng.standard_normal((batch, window, channels)).astype(np.float32)
        (weights,) = session.run([OUTPUT_NAME], {INPUT_NAME: x})
        if weights.shape != (batch, heads, window, window) or weights.dtype != np.float32 or not np.isfinite(weights).all():
            raise ValueError(f"attention output must be finite float32 {(batch, heads, window, window)}, got {weights.dtype}{weights.shape}")
        if not np.allclose(weights.sum(axis=-1), 1.0, atol=ROW_SUM_ATOL):
            raise ValueError("every attention row must sum to 1")
        if (np.triu(weights, k=1) != 0.0).any():
            raise ValueError("a future position must have attention weight exactly 0 (causal mask)")
        model.eval()
        with torch.no_grad():
            reference = model.attention_weights(torch.from_numpy(x)).numpy()
        worst = max(worst, float(np.max(np.abs(reference - weights))))
    if worst >= ONNX_ATOL:
        raise ValueError(f"torch vs onnxruntime attention delta {worst:.2e} >= {ONNX_ATOL:.0e}")
    return worst


# --- self-verification ---------------------------------------------------------------------------------------------------------


def _expect_error(call, fragment: str) -> None:
    try:
        call()
    except ValueError as exc:
        assert fragment in str(exc), f"{fragment!r} not in {exc}"
        return
    raise AssertionError(f"expected ValueError containing {fragment!r}")


def _run_selfcheck() -> None:
    import onnx
    import onnxruntime as ort

    torch.manual_seed(1)
    # (a) heads validation: divisibility, range, bool/float
    _expect_error(lambda: AttentionLstmClassifier(5, 6, 1, 0.0, 3, heads=4), "multiple of heads")
    _expect_error(lambda: AttentionLstmClassifier(5, 8, 1, 0.0, 3, heads=0), "heads must be")
    _expect_error(lambda: AttentionLstmClassifier(5, 32, 1, 0.0, 3, heads=MAX_HEADS + 1), "heads must be")
    _expect_error(lambda: AttentionLstmClassifier(5, 8, 1, 0.0, 3, heads=True), "heads must be")
    _expect_error(lambda: AttentionLstmClassifier(5, 8, 1, 0.0, 3, heads=2.0), "heads must be")

    window, channels, hidden, heads = 9, 5, 8, 4
    model = AttentionLstmClassifier(channels, hidden, 2, 0.0, 3, heads).eval()
    x = torch.randn(4, window, channels)
    w = model.attention_weights(x)
    assert w.shape == (4, heads, window, window) and model(x).shape == (4, 3)
    # (b) every row is a softmax, the future is exactly zero, and the diagonal/past carries the mass
    assert torch.allclose(w.sum(-1), torch.ones(4, heads, window), atol=1e-6) and (torch.triu(w, 1) == 0).all() and (w >= 0).all()
    assert torch.allclose(w[:, :, 0, 0], torch.ones(4, heads)), "the first query can only see itself"
    # (c) causality of the whole model: changing the LAST bars changes neither earlier rows nor earlier-position encodings
    x2 = x.clone()
    x2[:, 6:, :] += torch.randn(4, window - 6, channels)
    w2 = model.attention_weights(x2)
    assert torch.equal(w[:, :, :6, :], w2[:, :, :6, :]), "attention of earlier queries must not depend on later bars"
    assert not torch.equal(w[:, :, 6:, :], w2[:, :, 6:, :])
    # (d) the production forward is the head over the last query's context: recompute by hand from the weights
    with torch.no_grad():
        h = model.encode_sequence(x)
        v = model.attn._split(model.attn.value(h))
        context = torch.matmul(w, v).transpose(1, 2).reshape(4, window, hidden)
        manual = model.head(model.attn.proj(context)[:, -1, :])
    assert torch.allclose(manual, model(x), atol=1e-6)
    # (e) one head equals plain single-head attention on the same projections (identity of the head split)
    single = AttentionLstmClassifier(channels, hidden, 1, 0.0, 3, heads=1).eval()
    ws = single.attention_weights(x)
    with torch.no_grad():
        hh = single.encode_sequence(x)
        q, k = single.attn.query(hh), single.attn.key(hh)
        ref = torch.softmax((q @ k.transpose(-2, -1) / math.sqrt(hidden)).masked_fill(torch.triu(torch.ones(window, window, dtype=torch.bool), 1), float("-inf")), -1)
    assert torch.allclose(ws[:, 0], ref, atol=1e-6)

    with tempfile.TemporaryDirectory() as raw:
        folder = Path(raw)
        # (f) production export: ONE output named output, standard ops only, ORT parity (the attention graph is part of the production model)
        parent = folder / "model.onnx"
        class Softmaxed(nn.Module):
            def __init__(self, base):
                super().__init__()
                self.base = base

            def forward(self, t):
                return torch.softmax(self.base(t), dim=-1)

        torch.onnx.export(Softmaxed(model).eval(), torch.randn(1, window, channels), str(parent), input_names=["input"], output_names=["output"],
                          dynamic_axes={"input": {0: "batch"}, "output": {0: "batch"}}, opset_version=17, dynamo=False)
        session = ort.InferenceSession(str(parent), providers=["CPUExecutionProvider"])
        assert [o.name for o in session.get_outputs()] == ["output"]
        probs = session.run(["output"], {"input": x.numpy()})[0]
        assert np.allclose(probs, torch.softmax(model(x), -1).detach().numpy(), atol=ONNX_ATOL)
        # (g) sidecar: written beside the parent, verified, bound to the parent hash
        feature_hash = hashlib.sha256(b"contract").hexdigest()
        side = export_sidecar(model, parent, window=window, channels=channels, opset=17, feature_contract_hash=feature_hash)
        assert side.name == "model.onnx.attention.onnx" and not list(folder.glob("*" + INCOMING_SUFFIX))
        delta = verify_sidecar(side, model, parent, window=window, channels=channels)
        assert delta < ONNX_ATOL
        found = check_binding(side, parent)
        assert found[KEY_HEADS] == str(heads) and found[KEY_WINDOW] == str(window) and found[KEY_FEATURE_HASH] == feature_hash
        assert {p.key for p in onnx.load(str(side)).metadata_props} == set(BINDING_KEYS), "no production contract key may appear on the sidecar"
        assert {p.key for p in onnx.load(str(parent)).metadata_props} == set(), "the production model carries no attention key"
        # (h) a stale / foreign / renamed sidecar is refused
        with parent.open("ab") as stream:
            stream.write(b"\x00")
        _expect_error(lambda: check_binding(side, parent), "another generation")
        other = folder / "other.onnx"
        other.write_bytes(parent.read_bytes())
        _expect_error(lambda: check_binding(side, other), "must be named")
        # (i) a failing export leaves neither a sidecar nor an .incoming file and keeps an existing sidecar intact
        before = side.read_bytes()
        broken = AttentionLstmClassifier(channels, hidden, 1, 0.0, 3, heads)
        broken.attn.proj = nn.Linear(hidden + 1, hidden)          # incompatible shapes: the trace fails
        try:
            export_sidecar(broken, parent, window=window, channels=channels, opset=17, feature_contract_hash=feature_hash)
        except Exception:
            pass
        else:
            raise AssertionError("a broken model must not export")
        assert side.read_bytes() == before and not list(folder.glob("*" + INCOMING_SUFFIX))
    print("attention_export.py selfcheck: OK")


if __name__ == "__main__":
    if "--selfcheck" in sys.argv[1:]:
        _run_selfcheck()
    else:
        raise SystemExit("attention_export.py has no command line; use --selfcheck")
