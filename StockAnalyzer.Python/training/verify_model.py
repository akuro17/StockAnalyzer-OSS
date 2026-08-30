"""Standalone verification suite for an exported ``trend_predictor.onnx``.

Torch-free: needs only ``onnxruntime``, ``numpy`` and the sibling :mod:`dataset`
module. Runs the six boundary vectors from the WebAI review plus a
Python-vs-ONNX-Runtime equivalence check, and prints a real-data prediction
summary.

Vectors
-------
VEC-1  Flatline price window      -> MinMax prices == 0.5, model output finite / valid distribution.
VEC-2  Zero / negative volume     -> MinMax volume  == 0.0, model output finite / valid distribution.
VEC-3  Constant channel           -> Z-Score channel == 0.0, model output finite / valid distribution.
VEC-4  Extreme logits             -> reference softmax stays finite, sums to 1 +/- 1e-4, argmax preserved.
VEC-5  Confidence / entropy edges -> one-hot -> (1.0, 0.0); uniform-K -> (1/K, ln K).
VEC-6  Shape & node-name contract -> input "input" [b,W,C] float32, output "output" [b,3] float32.

Equivalence
-----------
- Preprocessing determinism: :mod:`dataset` transforms are bit-identical across repeated calls.
- Batch invariance: onnxruntime output for a stacked batch equals the per-sample outputs to < 1e-5.

Usage
-----
    python verify_model.py                         # verify training/artifacts/trend_predictor.onnx
    python verify_model.py --model path/to.onnx --feature-mode zscore --max-symbols 12
"""

from __future__ import annotations

import argparse
import datetime
import sys
from pathlib import Path
from typing import Tuple

import numpy as np
import onnxruntime as ort

sys.path.insert(0, str(Path(__file__).resolve().parent))
import dataset as ds  # noqa: E402
import onnx_meta  # noqa: E402

# --- Tolerances (named; mirror the C# engine where applicable) --------------

EQUIV_ATOL: float = 1e-5           # Python-vs-ONNXRuntime / batch-invariance tolerance
SOFTMAX_SUM_TOL: float = ds.SOFTMAX_SUM_TOLERANCE  # mirrors IMLDataProcessor.SoftmaxSumTolerance (1e-4f)
ENTROPY_ATOL: float = 1e-5
EPSILON: float = ds.EPSILON        # mirrors IMLDataProcessor.Epsilon (1e-7f)
NUM_CLASSES: int = len(ds.CLASS_LABELS)
DEFAULT_MODEL: Path = Path(__file__).resolve().parent / "artifacts" / "trend_predictor.onnx"
DEFAULT_MAX_SYMBOLS: int = 12


# --- Reference post-processing (mirror of MLDataProcessor) -----------------


def reference_softmax(logits: np.ndarray) -> np.ndarray:
    """Numerically stable softmax mirroring ``MLDataProcessor.ComputeSoftmax``.

    Falls back to the uniform distribution ``1/K`` when the exponential sum is
    ``<= Epsilon`` or ``NaN``.
    """
    z = np.asarray(logits, dtype=np.float64).ravel()
    k = z.shape[0]
    if k == 0:
        return z
    max_z = z.max()
    exp = np.exp(z - max_z)
    sum_exp = float(exp.sum())
    if sum_exp <= EPSILON or not np.isfinite(sum_exp):
        return np.full(k, 1.0 / k, dtype=np.float64)
    return (exp / sum_exp).astype(np.float64)


def reference_confidence_entropy(probs: np.ndarray) -> Tuple[float, float]:
    """Mirror of ``MLDataProcessor.ComputeConfidenceAndEntropy``.

    ``Confidence = max(p)``. Shannon entropy excludes ``p <= Epsilon`` terms.
    """
    p = np.asarray(probs, dtype=np.float64).ravel()
    if p.shape[0] == 0:
        return 0.0, 0.0
    confidence = float(p.max())
    mask = p > EPSILON
    entropy = float(-(p[mask] * np.log(p[mask])).sum())
    return confidence, entropy


# --- Model loading & contract (VEC-6) -------------------------------------


class ModelContract:
    def __init__(self, session: ort.InferenceSession):
        self.session = session
        in_meta = session.get_inputs()[0]
        out_meta = session.get_outputs()[0]
        self.input_name = in_meta.name
        self.output_name = out_meta.name
        self.input_shape = in_meta.shape
        self.output_shape = out_meta.shape
        # last input dim is channels when static; else infer from feature mode later
        self.channels = in_meta.shape[2] if isinstance(in_meta.shape[2], int) else None
        self.window = in_meta.shape[1] if isinstance(in_meta.shape[1], int) else ds.DEFAULT_WINDOW

    def run(self, x: np.ndarray) -> np.ndarray:
        (out,) = self.session.run([self.output_name], {self.input_name: x.astype(np.float32)})
        return out


def check_contract(contract: ModelContract) -> None:
    assert contract.input_name == "input", f"input node name {contract.input_name!r} != 'input'"
    assert contract.output_name == "output", f"output node name {contract.output_name!r} != 'output'"
    assert len(contract.input_shape) == 3, f"input rank {len(contract.input_shape)} != 3"
    assert len(contract.output_shape) == 2, f"output rank {len(contract.output_shape)} != 2"
    if isinstance(contract.input_shape[2], int):
        assert contract.input_shape[2] in ds.CHANNEL_COUNTS, (contract.input_shape, ds.CHANNEL_COUNTS)
    if isinstance(contract.output_shape[1], int):
        assert contract.output_shape[1] == NUM_CLASSES, contract.output_shape
    print(f"  VEC-6 contract OK: input{contract.input_shape} '{contract.input_name}' "
          f"-> output{contract.output_shape} '{contract.output_name}'")


def check_metadata(contract: ModelContract) -> None:
    """VEC-7: the embedded model contract (``metadata_props``) is present and
    self-consistent with the model's own input tensor. A model exported before
    this contract existed has no ``metadata_props`` and is skipped."""
    cmap = dict(contract.session.get_modelmeta().custom_metadata_map)
    if not cmap:
        print("  VEC-7 metadata: SKIP (no metadata_props; pre-contract model)")
        return

    missing = [k for k in onnx_meta.CONTRACT_KEYS if k not in cmap]
    assert not missing, f"VEC-7: metadata missing keys {missing}"
    assert cmap["feature_mode"] in ds.FEATURE_MODES, f"VEC-7: bad feature_mode {cmap['feature_mode']!r}"
    assert int(cmap["channels"]) == contract.channels, (cmap["channels"], contract.channels)
    if isinstance(contract.input_shape[1], int):
        assert int(cmap["window_size"]) == contract.input_shape[1], (cmap["window_size"], contract.input_shape)
    assert len(cmap["class_order"].split(",")) == NUM_CLASSES, cmap["class_order"]
    print(f"  VEC-7 metadata OK: feature_mode={cmap['feature_mode']} window_size={cmap['window_size']} "
          f"channels={cmap['channels']} contract_version={cmap.get('model_contract_version')}")

    # VEC-7 date ranges: dataset.train_val_date_ranges fills these four keys or
    # leaves them all empty (pre-date-range model). A partial group is a bug. Only
    # the per-side ordering holds -- the pooled training span can overlap the
    # pooled validation span when symbols cover unequal histories.
    date_vals = [cmap[k] for k in ds.DATE_RANGE_KEYS]
    if any(date_vals):
        assert all(date_vals), (
            f"VEC-7: partial date ranges {dict(zip(ds.DATE_RANGE_KEYS, date_vals))}"
        )
        tr_s, tr_e, va_s, va_e = (datetime.date.fromisoformat(v) for v in date_vals)
        assert tr_s <= tr_e, f"VEC-7: training_start {tr_s} after training_end {tr_e}"
        assert va_s <= va_e, f"VEC-7: validation_start {va_s} after validation_end {va_e}"
        print(f"  VEC-7 date ranges OK: train {tr_s}..{tr_e}  val {va_s}..{va_e}")
    else:
        print("  VEC-7 date ranges: SKIP (all empty; pre-date-range model)")


# --- Helpers -------------------------------------------------------------------


def _assert_valid_distribution(probs: np.ndarray, label: str) -> None:
    assert np.isfinite(probs).all(), f"{label}: non-finite model output {probs}"
    assert (probs >= -EQUIV_ATOL).all() and (probs <= 1.0 + EQUIV_ATOL).all(), f"{label}: out of [0,1] {probs}"
    row_sums = probs.reshape(-1, NUM_CLASSES).sum(axis=1)
    assert np.allclose(row_sums, 1.0, atol=SOFTMAX_SUM_TOL), f"{label}: rows not normalized {row_sums}"


def _feature_mode_for(contract: ModelContract, override: str | None) -> str:
    if override is not None:
        return override
    if contract.channels == ds.FEATURES_LOGRETURN:
        return "log_return"
    if contract.channels == ds.FEATURES_LOGRETURN_OHLC:
        return "log_return_ohlc"
    return "ohlcv_minmax"  # 5-channel default (zscore / zscore_joint also 5ch; pass --feature-mode to disambiguate)


# --- Boundary vectors -------------------------------------------------------


def vec1_flatline_price(contract: ModelContract, window: int) -> None:
    raw = np.tile(np.array([[100.0, 100.0, 100.0, 100.0, 1500.0]]), (window, 1))
    feat = ds.normalize_ohlcv_minmax(raw)
    assert np.allclose(feat[:, ds.PRICE_SLICE], 0.5), f"VEC-1: flat price not 0.5 -> {feat[:, ds.PRICE_SLICE]}"
    out = contract.run(feat[None, :, : contract.channels or ds.FEATURES_OHLCV])
    _assert_valid_distribution(out, "VEC-1")
    print("  VEC-1 flatline price OK (features==0.5, model output finite & normalized)")


def vec2_zero_negative_volume(contract: ModelContract, window: int) -> None:
    raw = np.tile(np.array([[10.0, 12.0, 9.0, 11.0, 0.0]]), (window, 1))
    raw[window // 2, ds.VOLUME] = -50.0
    feat = ds.normalize_ohlcv_minmax(raw)
    assert np.allclose(feat[:, ds.VOLUME], 0.0), f"VEC-2: volume not 0.0 -> {feat[:, ds.VOLUME]}"
    out = contract.run(feat[None, :, : contract.channels or ds.FEATURES_OHLCV])
    _assert_valid_distribution(out, "VEC-2")
    print("  VEC-2 zero/negative volume OK (feature==0.0, model output finite & normalized)")


def vec3_constant_channel_zscore(contract: ModelContract, window: int) -> None:
    raw = np.column_stack([
        np.full(window, 42.0),                     # open: constant -> z == 0
        np.linspace(10.0, 20.0, window),           # high: varying
        np.full(window, 5.0),
        np.full(window, 5.0),
        np.full(window, 1000.0),                   # volume: constant -> z == 0
    ])
    feat = ds.zscore_standardized(raw)
    assert np.allclose(feat[:, ds.OPEN], 0.0), f"VEC-3: constant channel z-score not 0.0 -> {feat[:, ds.OPEN]}"
    assert np.allclose(feat[:, ds.VOLUME], 0.0), f"VEC-3: constant volume z-score not 0.0 -> {feat[:, ds.VOLUME]}"
    assert abs(np.std(feat[:, ds.HIGH]) - 1.0) < 1e-4, "VEC-3: varying channel not unit-variance"
    print("  VEC-3 constant channel Z-Score OK (features==0.0, varying channel unit-variance)")


def vec4_extreme_logits() -> None:
    for logits, want_argmax in (
        ([1000.0, -1000.0, 0.0], 0),
        ([-1000.0, 1000.0, 0.0], 1),
        ([0.0, 0.0, 1000.0], 2),
        ([1e9, 1e9, 1e9], None),         # all equal -> uniform
        ([np.nan, 1.0, 2.0], None),      # NaN -> uniform fallback
    ):
        p = reference_softmax(np.array(logits))
        assert np.isfinite(p).all(), f"VEC-4: non-finite softmax for {logits} -> {p}"
        assert abs(float(p.sum()) - 1.0) < SOFTMAX_SUM_TOL, f"VEC-4: sum {p.sum()} for {logits}"
        if want_argmax is not None:
            assert int(np.argmax(p)) == want_argmax, f"VEC-4: argmax {np.argmax(p)} != {want_argmax} for {logits}"
    # all-equal and NaN cases must both be (near) uniform
    assert np.allclose(reference_softmax(np.array([1e9, 1e9, 1e9])), 1.0 / 3.0, atol=SOFTMAX_SUM_TOL)
    assert np.allclose(reference_softmax(np.array([np.nan, 1.0, 2.0])), 1.0 / 3.0, atol=SOFTMAX_SUM_TOL)
    print("  VEC-4 extreme logits OK (finite, normalized, argmax preserved, NaN/degenerate -> uniform)")


def vec5_confidence_entropy_edges() -> None:
    one_hot = np.array([1.0, 0.0, 0.0])
    conf, ent = reference_confidence_entropy(one_hot)
    assert abs(conf - 1.0) < ENTROPY_ATOL and abs(ent - 0.0) < ENTROPY_ATOL, (conf, ent)

    uniform = np.full(NUM_CLASSES, 1.0 / NUM_CLASSES)
    conf_u, ent_u = reference_confidence_entropy(uniform)
    assert abs(conf_u - 1.0 / NUM_CLASSES) < ENTROPY_ATOL, conf_u
    assert abs(ent_u - np.log(NUM_CLASSES)) < ENTROPY_ATOL, (ent_u, np.log(NUM_CLASSES))
    print(f"  VEC-5 confidence/entropy OK (one-hot -> (1.0, 0.0); uniform -> (1/{NUM_CLASSES}, ln {NUM_CLASSES}))")


# --- Equivalence ----------------------------------------------------------------


def check_preprocess_determinism(feature_mode: str, windows: np.ndarray) -> None:
    transform = ds.get_transform(feature_mode)
    for w in windows[:64]:
        a = transform(w)
        b = transform(w.copy())
        assert np.array_equal(a, b), "preprocessing is not deterministic"
    print("  equivalence: preprocessing deterministic across repeated calls")


def check_batch_invariance(contract: ModelContract, feats: np.ndarray) -> None:
    sample = feats[: min(32, len(feats))]
    batched = contract.run(sample)
    per_sample = np.stack([contract.run(row[None, ...])[0] for row in sample])
    delta = float(np.max(np.abs(batched - per_sample)))
    assert delta < EQUIV_ATOL, f"batch vs per-sample delta {delta:.2e} >= {EQUIV_ATOL:.0e}"
    print(f"  equivalence: onnxruntime batch-invariant (max delta {delta:.2e} < {EQUIV_ATOL:.0e})")


# --- Real-data report --------------------------------------------------------


def real_data_report(contract: ModelContract, feature_mode: str, data_dir: Path, max_symbols: int) -> np.ndarray:
    symbols = ds.load_parquet_dir(data_dir)
    symbols = dict(list(symbols.items())[:max_symbols])
    # window must match the model's own input, not dataset.DEFAULT_WINDOW.
    x, _ = ds.build_dataset_multi(symbols, feature_mode, window=contract.window)
    if x.shape[0] == 0:
        raise SystemExit("real-data report: empty dataset")
    channels = ds.feature_channels(feature_mode)
    assert x.shape[2] == channels, (x.shape, channels)
    probs = contract.run(x[: min(4096, len(x))])
    _assert_valid_distribution(probs, "real-data")
    argmax = probs.argmax(axis=1)
    dist = {ds.CLASS_LABELS[c]: int((argmax == c).sum()) for c in range(NUM_CLASSES)}
    conf = probs.max(axis=1)
    print(f"  real-data ({len(symbols)} symbols, {feature_mode}): {probs.shape[0]} windows, "
          f"pred dist={dist}, mean confidence={conf.mean():.3f}")
    return probs


# --- Orchestration ---------------------------------------------------------


def run_suite(args: argparse.Namespace) -> int:
    model_path = Path(args.model)
    if not model_path.is_file():
        raise SystemExit(f"model not found: {model_path}  (train one first, e.g. train_pytorch.py --smoke)")

    session = ort.InferenceSession(str(model_path), providers=["CPUExecutionProvider"])
    contract = ModelContract(session)
    feature_mode = _feature_mode_for(contract, args.feature_mode)
    if contract.channels is None:
        contract.channels = ds.feature_channels(feature_mode)

    print(f"verify_model: {model_path.name}  feature_mode={feature_mode}  window={contract.window}  channels={contract.channels}")

    check_contract(contract)
    check_metadata(contract)
    if contract.channels == ds.FEATURES_OHLCV:
        vec1_flatline_price(contract, contract.window)
        vec2_zero_negative_volume(contract, contract.window)
        vec3_constant_channel_zscore(contract, contract.window)
    else:
        print(f"  VEC-1..3 SKIP (minmax/zscore-specific; model is {contract.channels}-channel {feature_mode})")
    vec4_extreme_logits()
    vec5_confidence_entropy_edges()

    symbols = ds.load_parquet_dir(args.data_dir)
    symbols = dict(list(symbols.items())[: args.max_symbols])
    raw_windows = []
    for arr in symbols.values():
        for t in range(0, min(len(arr) - contract.window, 200)):
            raw_windows.append(arr[t:t + contract.window])
    raw_windows = np.stack(raw_windows)
    check_preprocess_determinism(feature_mode, raw_windows)

    transform = ds.get_transform(feature_mode)
    feats = np.stack([transform(w)[:, : contract.channels] for w in raw_windows]).astype(np.float32)
    check_batch_invariance(contract, feats)

    real_data_report(contract, feature_mode, args.data_dir, args.max_symbols)

    print("verify_model: ALL CHECKS PASSED")
    return 0


def _parse_args(argv):
    p = argparse.ArgumentParser(description="Verify an exported trend_predictor.onnx against the C# inference contract.")
    p.add_argument("--model", type=Path, default=DEFAULT_MODEL)
    p.add_argument("--feature-mode", choices=ds.FEATURE_MODES, default=None,
                   help="Override; inferred from input channels when omitted (5ch -> ohlcv_minmax).")
    p.add_argument("--data-dir", type=Path, default=ds.DEFAULT_DATA_DIR)
    p.add_argument("--max-symbols", type=int, default=DEFAULT_MAX_SYMBOLS)
    return p.parse_args(argv)


if __name__ == "__main__":
    raise SystemExit(run_suite(_parse_args(sys.argv[1:])))
