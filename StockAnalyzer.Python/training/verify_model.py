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

Dynamic quantization (T17, ``--quantize``)
------------------------------------------
Approved decisions (2026-10-10, ``Y:\\Temp\\sa_decision_options_onnx_research_t17_t22.md``): Q01=A Q02=A Q03=A
Q04=A Q05=A Q06=A Q07=A Q08=A Q09=A+B Q10=B Q11=A Q12=1234 Q13=C Q14=A Q15=A Q16=B Q17=A Q18=A Q19=A Q20=A
Q21=A Q22=A, C01/C03..C08=A C09=A C10=A C11=A C12=A.

* ``--quantize`` runs ONLY the quantization workflow (the boundary-vector suite above is unchanged and runs without it).
* The quantized model is a derived evaluation artifact: it is written under ``<DataRoot>/TrainingArtifacts/Quantized/<runId>/``
  (never ``Data/Models``), is never activated and is never registered as a model generation.
* ``onnxruntime.quantization.quantize_dynamic`` with QInt8 weights, per-tensor, ops ``MatMul, Gemm, LSTM``, CPU provider.
  The result is a failure when no compute node was quantized, the contract differs, an output is non-finite or a tolerance is exceeded.
* Both models run on the SAME seeded sample of the model's VALIDATION population, one window per call (batch 1). The out-of-sample
  tail is never read: the data is clipped per symbol to the last validation anchor plus ``horizon`` rows from the model metadata.
* Dynamic quantization derives the activation scale per call, so a batch result can differ from the per-sample result. The judged
  value is the batch-1 one; the batch-vs-single difference is reported only.
* Starting tolerances (``QUANT_DEFAULT_*``) have no empirical basis; the real measurement is written to the report.

Usage
-----
    python verify_model.py                         # verify <DataRoot>/TrainingArtifacts/TW/trend_predictor.onnx
    python verify_model.py --model path/to.onnx --feature-mode zscore --max-symbols 12
    python verify_model.py --quantize --model path/to.onnx --max-symbols 12
    python verify_model.py --selfcheck
"""

from __future__ import annotations

import argparse
import contextlib
import datetime
import gc
import hashlib
import json
import math
import os
import shutil
import sys
import tempfile
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Tuple

import numpy as np
import onnxruntime as ort

sys.path.insert(0, str(Path(__file__).resolve().parent))
import calibration  # noqa: E402  (REFERENCE_KEY only)
import checkpoints  # noqa: E402
import dataset as ds  # noqa: E402
import fixed_scaler  # noqa: E402
import model_analysis  # noqa: E402  (REFERENCE_KEY only)
import onnx_meta  # noqa: E402

# --- Tolerances (named; mirror the C# engine where applicable) --------------

EQUIV_ATOL: float = 1e-5           # Python-vs-ONNXRuntime / batch-invariance tolerance
SOFTMAX_SUM_TOL: float = ds.SOFTMAX_SUM_TOLERANCE  # mirrors IMLDataProcessor.SoftmaxSumTolerance (1e-4f)
ENTROPY_ATOL: float = 1e-5
EPSILON: float = ds.EPSILON        # mirrors IMLDataProcessor.Epsilon (1e-7f)
NUM_CLASSES: int = len(ds.CLASS_LABELS)
# <DataRoot>/TrainingArtifacts/TW, matching run_training.py's ARTIFACTS_DIR (not a path relative
# to this script -- see that module's comment for why).
DEFAULT_MODEL: Path = ds.TRAINING_WIZARD_ARTIFACTS_DIR / "trend_predictor.onnx"
DEFAULT_MAX_SYMBOLS: int = 12

# --- Dynamic quantization (T17) -- named values; tolerances are CLI-overridable --------------------------------
QUANT_SCHEMA_VERSION: int = 1
QUANT_TOOL: str = "verify_model.py --quantize"
QUANT_TOOL_VERSION: str = "1"
QUANT_OUT_DIR: Path = ds.DATA_ROOT / "TrainingArtifacts" / "Quantized"
QUANT_MODEL_SUFFIX: str = ".quant.onnx"
QUANT_REPORT_SUFFIX: str = ".quant.onnx.quantization.json"
QUANT_INCOMING_SUFFIX: str = ".incoming"
QUANT_WEIGHT_TYPES: Tuple[str, ...] = ("qint8", "quint8")
QUANT_DEFAULT_WEIGHT_TYPE: str = "qint8"
QUANT_ALLOWED_OPS: Tuple[str, ...] = ("MatMul", "Gemm", "LSTM")
QUANT_DEFAULT_OPS: Tuple[str, ...] = QUANT_ALLOWED_OPS
# Compute nodes that exist only after quantization; the DynamicQuantizeLinear helper node alone does not count.
QUANT_COMPUTE_OPS: Tuple[str, ...] = ("MatMulInteger", "DynamicQuantizeLSTM", "ConvInteger", "QLinearMatMul", "QLinearConv")
# Approved Q11=A: 2048 windows, capped by the existing tensor / batch bounds (C10=A).
QUANT_DEFAULT_SAMPLES: int = 2048
QUANT_SAMPLE_STREAM: int = 17          # SeedSequence namespace of the sample draw
QUANT_DEFAULT_SEED: int = 42           # = train_pytorch.DEFAULT_SEED / model_analysis.SEED (C08=A)
# Starting tolerances (Q13=C: all gates must pass). NO empirical basis: they are measured on a real model and
# reported, then frozen by the owner. Classification gates use the batch-1 results.
QUANT_DEFAULT_MIN_AGREEMENT: float = 0.98      # argmax agreement rate, float vs quantized
QUANT_DEFAULT_MAX_PROB_DELTA: float = 0.05     # max |p_float - p_quant| over all windows and classes
QUANT_DEFAULT_MAX_ACCURACY_DROP: float = 0.01  # accuracy_float - accuracy_quant, absolute points
QUANT_DEFAULT_MAX_REGRESSION_DELTA: float = 0.01  # max |y_float - y_quant| in log-return units
# K2 (WR12 A / DF29 A): what the report says about its own gates and about the validation population it judged
THRESHOLDS_BASIS_DEFAULT: str = "planning_default_uncalibrated"   # every gate is still the planning default (no empirical basis)
THRESHOLDS_BASIS_USER: str = "user_supplied"                     # at least one gate was overridden on the command line
POPULATION_VERIFICATION: str = "period_only"
POPULATION_VERIFICATION_NOTE: str = ("the rebuilt validation span lies inside the model's validation_start..validation_end, --data-end does not pass it "
                                     "and the data revision is recorded; the NUMBER of validation windows is not checked (the model metadata has no count key)")
QUANT_PROGRESS_STRIDE: int = 256               # deadline check cadence of the per-window loop
# Run-duration bound (hard / standard band as in checkpoints.resolve_band; env override SA_QUANTIZE_MAX_SECONDS).
QUANT_MAX_SECONDS_ENV: str = "SA_QUANTIZE_MAX_SECONDS"
QUANT_MAX_SECONDS: int = checkpoints.resolve_band(QUANT_MAX_SECONDS_ENV, 3600, (1, 86_400), (60, 7_200))
# Metadata keys that name a sidecar file of the SOURCE model; the derived file has another name, so they are dropped (Q16=B).
QUANT_SIDECAR_REF_KEYS: Tuple[str, ...] = (
    "scaler_ref", model_analysis.REFERENCE_KEY, calibration.REFERENCE_KEY,
)
# Exit codes (C17=A).
EXIT_OK, EXIT_FAILED, EXIT_PREREQUISITE = 0, 1, 3


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


def legacy_contract_version(metadata) -> str | None:
    """The ``model_contract_version`` text when it is a number below the current contract version, else ``None``."""
    value = metadata.get("model_contract_version")
    try:
        return value if int(value) < int(onnx_meta.MODEL_CONTRACT_VERSION) else None
    except (TypeError, ValueError):
        return None


def check_metadata(contract: ModelContract) -> None:
    """VEC-7: the embedded model contract (``metadata_props``) is present and
    self-consistent with the model's own input tensor. A model exported before
    this contract existed has no ``metadata_props`` and is skipped."""
    cmap = dict(contract.session.get_modelmeta().custom_metadata_map)
    if not cmap:
        print("  VEC-7 metadata: SKIP (no metadata_props; pre-contract model)")
        return

    legacy = legacy_contract_version(cmap)
    if legacy is None:
        missing = [k for k in onnx_meta.CONTRACT_KEYS if k not in cmap]
        assert not missing, f"VEC-7: metadata missing keys {missing}"
    else:  # the key set of an older contract is not defined here: skip only that check, say so, and claim nothing about the contract
        print(f"  VEC-7 key set: SKIP (legacy contract v{legacy}; the v{onnx_meta.MODEL_CONTRACT_VERSION} key set is not required, "
              f"the legacy contract is not verified)")
        needed = [k for k in ("feature_mode", "channels", "window_size", "class_order") if k not in cmap]
        assert not needed, f"VEC-7: legacy metadata missing keys {needed}"
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
    date_vals = [cmap.get(k, "") for k in ds.DATE_RANGE_KEYS]
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


# --- Dynamic quantization (T17) ---------------------------------------------------


class PrerequisiteError(Exception):
    """A required input is missing or unusable (exit code 3). Nothing is published."""


@dataclass(frozen=True)
class QuantizeThresholds:
    """Pass/fail gates (Q13=C: every applicable gate must hold)."""

    min_agreement: float
    max_prob_delta: float
    max_accuracy_drop: float
    max_regression_delta: float

    def validate(self) -> "QuantizeThresholds":
        for name in ("min_agreement", "max_prob_delta", "max_accuracy_drop", "max_regression_delta"):
            value = getattr(self, name)
            if type(value) is bool or not isinstance(value, (int, float)) or not math.isfinite(value):
                raise ValueError(f"{name} must be a finite number")
        if not 0.0 < self.min_agreement <= 1.0:
            raise ValueError("min_agreement must be in (0, 1]")
        for name in ("max_prob_delta", "max_accuracy_drop", "max_regression_delta"):
            if getattr(self, name) < 0.0:
                raise ValueError(f"{name} must be >= 0")
        return self

    def as_dict(self) -> dict:
        return dict(min_agreement=self.min_agreement, max_prob_delta=self.max_prob_delta,
                    max_accuracy_drop=self.max_accuracy_drop, max_regression_delta=self.max_regression_delta)

    def basis(self) -> str:
        """``THRESHOLDS_BASIS_DEFAULT`` while every gate equals its ``QUANT_DEFAULT_*`` planning value, otherwise ``THRESHOLDS_BASIS_USER``."""
        defaults = QuantizeThresholds(QUANT_DEFAULT_MIN_AGREEMENT, QUANT_DEFAULT_MAX_PROB_DELTA, QUANT_DEFAULT_MAX_ACCURACY_DROP,
                                      QUANT_DEFAULT_MAX_REGRESSION_DELTA)
        return THRESHOLDS_BASIS_DEFAULT if self.as_dict() == defaults.as_dict() else THRESHOLDS_BASIS_USER


@contextlib.contextmanager
def _temp_dir(path: Path):
    """``quantize_dynamic`` stages the model through the temp directory as external data. The default temp directory
    may be a RAM disk whose path canonicalization fails ("Incorrect function"), so it is pointed at a normal disk."""
    previous = tempfile.tempdir
    tempfile.tempdir = str(path)
    try:
        yield
    finally:
        tempfile.tempdir = previous


def parse_ops(text: str | tuple) -> Tuple[str, ...]:
    ops = tuple(t.strip() for t in (text.split(",") if isinstance(text, str) else text) if t.strip())
    if not ops or len(set(ops)) != len(ops) or any(op not in QUANT_ALLOWED_OPS for op in ops):
        raise ValueError(f"--quantize-ops must be a distinct subset of {QUANT_ALLOWED_OPS}, got {text!r}")
    return ops


def validate_quantize_args(args: argparse.Namespace) -> None:
    """Argument rules of ``--quantize`` (exit code 2 on violation)."""
    parse_ops(args.quantize_ops)
    if args.quantize_weight_type not in QUANT_WEIGHT_TYPES:
        raise ValueError(f"--quantize-weight-type must be one of {QUANT_WEIGHT_TYPES}")
    validate_population_arguments(args)
    for name in ("quantize_samples", "quantize_seed"):
        value = getattr(args, name)
        if type(value) is not int or value < (0 if name == "quantize_seed" else 1):
            raise ValueError(f"--{name.replace('_', '-')} must be a positive integer (seed: >= 0)")
    if args.quantize_samples > ds.MAX_COMPOSED_BATCH_SAMPLES:
        raise ValueError(f"--quantize-samples {args.quantize_samples} exceeds the bound {ds.MAX_COMPOSED_BATCH_SAMPLES}")
    if type(args.quantize_max_seconds) is not int or not 1 <= args.quantize_max_seconds <= QUANT_MAX_SECONDS:
        raise ValueError(f"--quantize-max-seconds must be in 1..{QUANT_MAX_SECONDS}")
    QuantizeThresholds(args.quantize_min_agreement, args.quantize_max_prob_delta,
                       args.quantize_max_accuracy_drop, args.quantize_max_regression_delta).validate()


def quantize_model(src: Path, dst: Path, *, ops: Tuple[str, ...], weight_type: str, per_channel: bool,
                   scratch: Path) -> None:
    """Dynamic quantization of *src* into *dst*; the source file is only read."""
    from onnxruntime.quantization import QuantType, quantize_dynamic

    types = {"qint8": QuantType.QInt8, "quint8": QuantType.QUInt8}
    scratch.mkdir(parents=True, exist_ok=True)
    with _temp_dir(scratch):
        quantize_dynamic(str(src), str(dst), op_types_to_quantize=list(ops),
                         weight_type=types[weight_type], per_channel=per_channel)


def count_quantized_nodes(path: Path) -> dict:
    """``{op_type: count}`` of the compute nodes that exist only in a quantized graph; empty when none."""
    import onnx

    counts: dict = {}
    for node in onnx.load(str(path)).graph.node:
        if node.op_type in QUANT_COMPUTE_OPS:
            counts[node.op_type] = counts.get(node.op_type, 0) + 1
    return counts


def contract_snapshot(path: Path) -> dict:
    """Names, element types, shapes (symbolic axes by name), default-domain opset and metadata_props of a model."""
    import onnx

    model = onnx.load(str(path), load_external_data=False)

    def describe(value) -> dict:
        tensor = value.type.tensor_type
        dims = [d.dim_value if d.HasField("dim_value") else (d.dim_param or None) for d in tensor.shape.dim]
        return dict(name=value.name, elem_type=int(tensor.elem_type), shape=dims)

    default_opset = next((o.version for o in model.opset_import if o.domain in ("", "ai.onnx")), None)
    return dict(
        inputs=[describe(v) for v in model.graph.input],
        outputs=[describe(v) for v in model.graph.output],
        default_opset=default_opset,
        extra_domains=sorted(f"{o.domain}:{o.version}" for o in model.opset_import if o.domain not in ("", "ai.onnx")),
        metadata={p.key: p.value for p in model.metadata_props},
    )


def derived_metadata(source_metadata: dict) -> Tuple[dict, list]:
    """The metadata a derived file carries (Q16=B): every key except the sidecar references of the SOURCE file name."""
    removed = sorted(k for k in QUANT_SIDECAR_REF_KEYS if k in source_metadata)
    return {k: v for k, v in source_metadata.items() if k not in QUANT_SIDECAR_REF_KEYS}, removed


def compare_contracts(source: dict, derived: dict, expected_metadata: dict) -> list:
    """Mismatches between the source and the derived model contract (Q17=A); empty list = identical."""
    problems = []
    for key in ("inputs", "outputs", "default_opset"):
        if source[key] != derived[key]:
            problems.append(f"{key}: {source[key]} != {derived[key]}")
    if derived["metadata"] != expected_metadata:
        keys = sorted(set(derived["metadata"]) ^ set(expected_metadata)
                      | {k for k in expected_metadata if derived["metadata"].get(k) != expected_metadata[k]})
        problems.append(f"metadata_props differ at {keys}")
    return problems


def clip_to_validation_end(symbols: dict, dates: dict, validation_end: str, horizon: int) -> Tuple[dict, dict]:
    """Per symbol keep the rows up to the last validation anchor plus ``horizon`` label rows.

    The model metadata ``validation_end`` is the newest validation ANCHOR date; the rows after it that the labels need
    are the only ones kept, so rows that were added after training (or that form the out-of-sample tail) never enter a
    window and the validation partition is rebuilt from the same sample count the trainer saw."""
    end = np.datetime64(validation_end, "D")
    out_symbols, out_dates = {}, {}
    for symbol, array in symbols.items():
        d = np.asarray(dates[symbol]).astype("datetime64[D]").reshape(-1)
        last = int(np.searchsorted(d, end, side="right")) - 1
        if last < 0:
            continue
        keep = min(d.shape[0], last + horizon + 1)
        out_symbols[symbol], out_dates[symbol] = array[:keep], d[:keep]
    return out_symbols, out_dates


def reconstruct_validation_population(metadata: dict, model_path: Path, args: argparse.Namespace) -> dict:
    """The VALIDATION windows of the model (Q10=B, C11=A, C13=A), as the ONNX input tensor and the labels.

    Everything but the symbols and the split arguments ``--symbols`` / ``--wf-splits`` / ``--outer-fold-index`` /
    ``--calibration-segment`` comes from the model metadata; the rebuilt validation span must lie inside the metadata span, otherwise the split arguments are wrong."""
    needed = ("feature_mode", "window_size", "prediction_horizon", "neutral_threshold")
    missing = [k for k in needed if k not in metadata]
    if missing:
        raise PrerequisiteError(f"model metadata lacks {missing}; the validation population cannot be reconstructed")
    target_type = metadata.get("target_type", onnx_meta.TARGET_TYPE)
    if target_type not in onnx_meta.TARGET_TYPES:
        raise PrerequisiteError(f"unknown target_type {target_type!r} in the model metadata")
    feature_mode = metadata["feature_mode"]
    window, horizon = int(metadata["window_size"]), int(metadata["prediction_horizon"])
    threshold = float(metadata["neutral_threshold"])
    gap = int(metadata[onnx_meta.PURGE_GAP_KEY]) if onnx_meta.PURGE_GAP_KEY in metadata else None
    fixed = fixed_scaler.requires_fixed_scaler(metadata)
    lags = tuple(json.loads(metadata["lags"])) if fixed else ()
    feature_spec = json.loads(metadata["feature_spec"]) if "feature_spec" in metadata else None
    indicator_paths = json.loads(args.indicator_channel_export_paths) if args.indicator_channel_export_paths else None

    meta_start, meta_end = metadata.get("validation_start", ""), metadata.get("validation_end", "")
    data_end = args.data_end or meta_end
    if not data_end:
        raise PrerequisiteError("the model has no validation_end metadata; pass --data-end (the newest validation anchor date)")
    if meta_end and args.data_end and args.data_end > meta_end:
        raise PrerequisiteError(f"--data-end {args.data_end} is after the model validation_end {meta_end}: that would read the out-of-sample tail")

    wanted = [t.strip() for t in args.symbols.split(",") if t.strip()] if args.symbols else None
    if args.symbols is not None and not wanted:
        raise PrerequisiteError("--symbols names no symbol")
    try:
        symbols, dates = ds.load_parquet_dir(args.data_dir, return_dates=True, symbols=wanted, start=args.data_start)
    except (FileNotFoundError, ValueError) as exc:  # missing folder, synthetic folder / file: a prerequisite, not a crash (D2)
        raise PrerequisiteError(str(exc)) from exc
    if wanted is not None:
        absent = sorted({w.lower() for w in wanted} - {s.lower() for s in symbols})
        if absent:
            raise PrerequisiteError(f"--symbols not found under {args.data_dir}: {absent}")
    symbols = dict(list(symbols.items())[: args.max_symbols])
    dates = {k: dates[k] for k in symbols}
    if meta_end and not args.data_end:
        symbols, dates = clip_to_validation_end(symbols, dates, meta_end, horizon)
    else:  # explicit bound: rows up to the date only, the newest `horizon` anchors lose their labels (reported as unverified)
        symbols, dates = clip_to_validation_end(symbols, dates, data_end, 0)
    if not symbols:
        raise PrerequisiteError(f"no symbol of {args.data_dir} has rows up to {data_end}")

    split = dict(
        feature_mode=feature_mode, window=window, horizon=horizon, threshold=threshold, n_splits=args.wf_splits, gap=gap,
        feature_spec=feature_spec, target_type=target_type, indicator_export_paths=indicator_paths,
        outer_fold_index=args.outer_fold_index, fixed_zscore=fixed, lags=lags, calibration=args.calibration_segment,
    )
    try:
        _x_tr, _y_tr, x_va, y_va = ds.split_symbols_chronological(symbols, dates=dates, **split)
    except ValueError as exc:  # e.g. the cumulative sample cap of fixed z-score / composed models
        raise PrerequisiteError(f"validation population cannot be built: {exc}") from exc
    if x_va.shape[0] == 0:
        raise PrerequisiteError("the reconstructed validation population is empty; check --wf-splits / --outer-fold-index / --data-dir")
    ranges = ds.train_val_date_ranges(symbols, dates, **split)
    vs, ve = ranges["validation_start"], ranges["validation_end"]
    if meta_start and meta_end and not (vs >= meta_start and ve <= meta_end):
        raise PrerequisiteError(
            f"rebuilt validation span {vs}..{ve} lies outside the model span {meta_start}..{meta_end}; "
            "the split arguments (--wf-splits, --outer-fold-index, --calibration-segment) or the symbols do not match the training run")
    scaler = fixed_scaler.load_sidecar(model_path, metadata) if fixed else None
    if fixed:
        x_va = fixed_scaler.transform(x_va, scaler)
    return dict(
        x=np.ascontiguousarray(x_va, dtype=np.float32), y=y_va, symbols=len(symbols), data_end=data_end,
        data_revision=checkpoints.source_revision(symbols, dates, indicator_paths), validation_span=[vs, ve],
        oos_consumed=False if meta_end else "unverified",
        target_type=target_type, wf_splits=args.wf_splits, outer_fold_index=args.outer_fold_index,
        calibration_segment=bool(args.calibration_segment),
        # the rebuilt inputs, for a tool that must also identify single windows (counterfactual.py); not part of any report
        context=dict(symbols=symbols, dates=dates, split=split, scaler=scaler),
    )


def population_transform(population: dict):
    """The function that turns raw window rows into the ONNX input of *population*: the fixed scaler when the model has one, else a float32 cast."""
    scaler = population["context"]["scaler"]
    return (lambda x: fixed_scaler.transform(x, scaler)) if scaler is not None else (lambda x: np.asarray(x, dtype=np.float32))


def validation_folds(population: dict) -> dict:
    """``{symbol: (x, y, anchors, train, val)}`` of the inputs ``reconstruct_validation_population`` rebuilt.

    The validation rows of the folds, concatenated in this order, are the pooled validation population ``population["x"]`` (before the
    fixed scaler); ``anchors[val]`` are the anchor row indices of the validation windows inside each symbol's own bars. The pooled rows must equal
    the population exactly (after :func:`population_transform`), otherwise the split arguments do not describe the training run
    (``PrerequisiteError``)."""
    context = population["context"]
    symbols, dates, split = context["symbols"], context["dates"], context["split"]
    paths = split["indicator_export_paths"]
    if split["outer_fold_index"] is not None:
        folds = ds.independent_fold_data(
            symbols, dates, feature_mode=split["feature_mode"], window=split["window"], horizon=split["horizon"], threshold=split["threshold"],
            n_splits=split["n_splits"], gap=split["gap"], fold_index=split["outer_fold_index"], feature_spec=split["feature_spec"],
            target_type=split["target_type"], indicator_export_paths=paths, inner=True, fixed_zscore=split["fixed_zscore"],
            lags=split["lags"], calibration=split["calibration"])
    else:
        gap = ds.resolve_purge_gap(split["window"], split["horizon"], split["gap"])
        folds = {}
        for sym, arr in symbols.items():
            fold = ds.last_fold(arr, split["feature_mode"], split["window"], split["horizon"], split["threshold"], split["n_splits"], gap,
                                split["feature_spec"], split["target_type"], dates=dates.get(sym),
                                indicator_export_path=paths.get(sym) if paths else None, fixed_zscore=split["fixed_zscore"], lags=split["lags"])
            if fold is not None:
                folds[sym] = fold
    transform = population_transform(population)
    pooled = np.ascontiguousarray(np.concatenate([transform(f[0][f[4]]) for f in folds.values()]), dtype=np.float32)
    if pooled.shape != population["x"].shape or not np.array_equal(pooled, population["x"]):
        raise PrerequisiteError("the per-window reconstruction disagrees with the validation population; the split arguments do not "
                                "describe the training run")
    return folds


def add_population_arguments(parser: argparse.ArgumentParser) -> None:
    """The arguments that rebuild a model's validation population (``reconstruct_validation_population``); one owner for ``verify_model --quantize``,
    ``counterfactual.py`` and ``event_eval.py``. The training run's symbols and split arguments are not stored in the model metadata."""
    parser.add_argument("--data-dir", type=Path, default=ds.DEFAULT_DATA_DIR)
    parser.add_argument("--max-symbols", type=int, default=DEFAULT_MAX_SYMBOLS)
    parser.add_argument("--symbols", default=None,
                        help="Comma list of the training run's symbols (recorded in Data/Experiments/<runId>/config.json); default: first --max-symbols files.")
    parser.add_argument("--wf-splits", type=int, default=ds.DEFAULT_WF_SPLITS, help="The training run's --wf-splits (not stored in the metadata).")
    parser.add_argument("--outer-fold-index", type=int, default=None, help="The training run's --outer-fold-index, if any.")
    parser.add_argument("--calibration-segment", action="store_true", help="The training run carved a calibration segment.")
    parser.add_argument("--indicator-channel-export-paths", default=None, help="JSON {symbol: parquet} for composed Indicator channels.")
    parser.add_argument("--data-start", type=_iso_date, default=None,
                        help="First calendar date the training run loaded (its --start); the model metadata does not record it.")
    parser.add_argument("--data-end", type=_iso_date, default=None,
                        help="Newest validation anchor date; only for a model without validation_end metadata (never after it).")


def validate_population_arguments(args: argparse.Namespace) -> None:
    """Argument rules of :func:`add_population_arguments` for the standalone tools (exit code 2 on violation)."""
    for name in ("max_symbols", "wf_splits"):
        value = getattr(args, name)
        if type(value) is not int or value < 1:
            raise ValueError(f"--{name.replace('_', '-')} must be an integer >= 1")
    if args.outer_fold_index is not None and (type(args.outer_fold_index) is not int or args.outer_fold_index < 0):
        raise ValueError("--outer-fold-index must be an integer >= 0")


def select_sample(total: int, size: int, seed: int) -> np.ndarray:
    """Ascending indices of a seeded sample without replacement (all of them when ``size >= total``)."""
    if total <= 0 or size <= 0:
        raise ValueError("total and size must be positive")
    if size >= total:
        return np.arange(total, dtype=np.int64)
    rng = np.random.default_rng(np.random.SeedSequence([seed, QUANT_SAMPLE_STREAM]))
    return np.sort(rng.choice(total, size=size, replace=False)).astype(np.int64)


def _run_each(contract: ModelContract, x: np.ndarray, deadline: float) -> np.ndarray:
    rows = []
    for i in range(x.shape[0]):
        if i % QUANT_PROGRESS_STRIDE == 0 and time.monotonic() > deadline:
            raise TimeoutError("quantization comparison exceeded --quantize-max-seconds")
        rows.append(contract.run(x[i:i + 1])[0])
    return np.stack(rows).astype(np.float64)


def compare_outputs(float_out: np.ndarray, quant_out: np.ndarray, y: np.ndarray, target_type: str) -> dict:
    """Degradation metrics of the quantized model against the float model on the same windows (Q12)."""
    if not (np.isfinite(float_out).all() and np.isfinite(quant_out).all()):
        raise ArithmeticError("non-finite model output")
    if target_type == "regression":
        f, q, truth = float_out.reshape(-1), quant_out.reshape(-1), np.asarray(y, dtype=np.float64).reshape(-1)
        delta = np.abs(f - q)
        return dict(
            n=int(f.shape[0]), max_abs_delta=float(delta.max()), mean_abs_delta=float(delta.mean()),
            sign_agreement=float((np.sign(f) == np.sign(q)).mean()),
            rmse_float=float(np.sqrt(np.mean((f - truth) ** 2))), rmse_quant=float(np.sqrt(np.mean((q - truth) ** 2))),
            rmse_delta=float(np.sqrt(np.mean((q - truth) ** 2)) - np.sqrt(np.mean((f - truth) ** 2))),
        )
    delta = np.abs(float_out - quant_out)
    af, aq = float_out.argmax(axis=1), quant_out.argmax(axis=1)  # np.argmax: first index wins an exact tie
    truth = np.asarray(y).reshape(-1)
    acc_f, acc_q = float((af == truth).mean()), float((aq == truth).mean())
    return dict(
        n=int(float_out.shape[0]), max_abs_prob_delta=float(delta.max()), mean_abs_prob_delta=float(delta.mean()),
        argmax_agreement=float((af == aq).mean()), accuracy_float=acc_f, accuracy_quant=acc_q, accuracy_drop=acc_f - acc_q,
    )


def judge(metrics: dict, target_type: str, thresholds: QuantizeThresholds) -> dict:
    """``{gate: passed}``. Regression is gated on the label-free maximum only (no label gate is defined for it)."""
    if target_type == "regression":
        return dict(max_abs_delta=metrics["max_abs_delta"] <= thresholds.max_regression_delta)
    return dict(
        argmax_agreement=metrics["argmax_agreement"] >= thresholds.min_agreement,
        max_abs_prob_delta=metrics["max_abs_prob_delta"] <= thresholds.max_prob_delta,
        accuracy_drop=metrics["accuracy_drop"] <= thresholds.max_accuracy_drop,
    )


def drop_nonfinite(value, path: str, omitted: list):
    """Copy of a JSON-bound structure without non-finite floats (omitted keys are listed; nothing is replaced by 0)."""
    if isinstance(value, dict):
        out = {}
        for key, item in value.items():
            cleaned = drop_nonfinite(item, f"{path}.{key}", omitted)
            if cleaned is not _OMIT:
                out[key] = cleaned
        return out
    if isinstance(value, (list, tuple)):
        return [drop_nonfinite(item, f"{path}[]", omitted) for item in value]
    if isinstance(value, (np.floating, float)):
        if not math.isfinite(float(value)):
            omitted.append(path)
            return _OMIT
        return float(value)
    if isinstance(value, (np.integer,)):
        return int(value)
    if isinstance(value, np.bool_):
        return bool(value)
    return value


_OMIT = object()


def _input_sha256(x: np.ndarray) -> str:
    digest = hashlib.sha256()
    digest.update(checkpoints.canonical([str(x.dtype), list(x.shape)]).encode())
    digest.update(np.ascontiguousarray(x).tobytes())
    return digest.hexdigest()


def run_quantize(args: argparse.Namespace) -> int:
    """The ``--quantize`` workflow; returns the process exit code (0 passed, 1 failed, 3 prerequisite)."""
    started = time.monotonic()
    try:
        return _run_quantize(args, started)
    except PrerequisiteError as exc:
        print(f"verify_model --quantize: PREREQUISITE NOT MET: {exc}", file=sys.stderr)
        return EXIT_PREREQUISITE


def _run_quantize(args: argparse.Namespace, started: float) -> int:
    validate_quantize_args(args)
    deadline = started + args.quantize_max_seconds
    thresholds = QuantizeThresholds(args.quantize_min_agreement, args.quantize_max_prob_delta,
                                    args.quantize_max_accuracy_drop, args.quantize_max_regression_delta).validate()
    ops = parse_ops(args.quantize_ops)
    model_path = Path(args.model).resolve()
    if not model_path.is_file():
        raise PrerequisiteError(f"model not found: {model_path}")
    sha_before = checkpoints.hash_file(model_path)
    session = ort.InferenceSession(str(model_path), providers=["CPUExecutionProvider"])
    metadata = dict(session.get_modelmeta().custom_metadata_map)
    if not metadata:
        raise PrerequisiteError("the model has no metadata_props contract (pre-contract model)")
    population = reconstruct_validation_population(metadata, model_path, args)
    target_type = population["target_type"]
    chosen = select_sample(population["x"].shape[0], args.quantize_samples, args.quantize_seed)
    tensor_bytes = int(chosen.shape[0]) * int(np.prod(population["x"].shape[1:])) * 4
    if tensor_bytes > ds.MAX_COMPOSED_TENSOR_SIZE_MB * 1024 * 1024:
        raise PrerequisiteError(f"the sample tensor of {tensor_bytes} bytes exceeds {ds.MAX_COMPOSED_TENSOR_SIZE_MB} MB")
    x, y = population["x"][chosen], population["y"][chosen]
    input_sha = _input_sha256(x)

    config = dict(
        model_sha256=sha_before, data_revision=population["data_revision"], ops=list(ops),
        weight_type=args.quantize_weight_type, per_channel=bool(args.quantize_per_channel), samples=int(chosen.shape[0]),
        seed=args.quantize_seed, thresholds=thresholds.as_dict(), wf_splits=args.wf_splits,
        outer_fold_index=args.outer_fold_index, calibration_segment=bool(args.calibration_segment),
        max_symbols=args.max_symbols, data_end=population["data_end"],
    )
    config_sha = hashlib.sha256(checkpoints.canonical(config).encode()).hexdigest()
    created = datetime.datetime.now(datetime.timezone.utc)
    run_id = f"{created.strftime('%Y%m%dT%H%M%SZ')}_{config_sha[:8]}"
    root = Path(args.quantize_out_root)
    final_dir, work = root / run_id, root / (run_id + QUANT_INCOMING_SUFFIX)
    if final_dir.exists() or work.exists():
        raise PrerequisiteError(f"run folder already exists: {final_dir}")
    work.mkdir(parents=True)
    quant_path = work / f"{model_path.stem}{QUANT_MODEL_SUFFIX}"
    report_path = work / f"{model_path.stem}{QUANT_REPORT_SUFFIX}"

    report: dict = dict(
        schema_version=QUANT_SCHEMA_VERSION, tool=QUANT_TOOL, tool_version=QUANT_TOOL_VERSION,
        created_utc=created.isoformat(), run_id=run_id, model_sha256=sha_before,
        data_revision=population["data_revision"], config_sha256=config_sha, seed=args.quantize_seed, config=config,
        thresholds=thresholds.as_dict(), thresholds_basis=thresholds.basis(),
        population_verification=POPULATION_VERIFICATION, population_verification_note=POPULATION_VERIFICATION_NOTE,
        population=dict(
            source="validation", oos_consumed=population["oos_consumed"], target_type=target_type,
            symbols=population["symbols"], validation_total=int(population["x"].shape[0]), sampled=int(chosen.shape[0]),
            input_sha256=input_sha, data_end=population["data_end"], validation_span=population["validation_span"],
            reconstruction="data clipped per symbol to the last validation anchor plus horizon rows; split arguments from the CLI",
        ),
    )
    status, reason, detail = "failed", None, None
    quant_contract = None
    try:
        try:
            quantize_model(model_path, quant_path, ops=ops, weight_type=args.quantize_weight_type,
                           per_channel=bool(args.quantize_per_channel), scratch=work / "scratch")
        except Exception as exc:  # the converter reports many unsupported cases as different exception types
            reason, detail = "quantization_error", f"{type(exc).__name__}: {str(exc)[:300]}"
        finally:
            shutil.rmtree(work / "scratch", ignore_errors=True)
        if reason is None:
            expected, removed = derived_metadata(metadata)
            onnx_meta.load_apply_save(quant_path, expected)
            counts = count_quantized_nodes(quant_path)
            report["quantization"] = dict(
                method="quantize_dynamic", weight_type=args.quantize_weight_type, per_channel=bool(args.quantize_per_channel),
                ops=list(ops), quantized_compute_nodes=counts, removed_ref_keys=removed, ort_version=ort.__version__,
                provider="CPUExecutionProvider",
            )
            mismatches = compare_contracts(contract_snapshot(model_path), contract_snapshot(quant_path), expected)
            report["contract"] = dict(mismatches=mismatches, derived_extra_domains=contract_snapshot(quant_path)["extra_domains"])
            if not counts:
                reason, detail = "no_quantized_nodes", "no compute node was quantized"
            elif mismatches:
                reason, detail = "contract_mismatch", "; ".join(mismatches)[:300]
        if reason is None:
            quant_contract = ModelContract(ort.InferenceSession(str(quant_path), providers=["CPUExecutionProvider"]))
            float_contract = ModelContract(session)
            try:
                float_out, quant_out = _run_each(float_contract, x, deadline), _run_each(quant_contract, x, deadline)
                metrics = compare_outputs(float_out, quant_out, y, target_type)
                batch_q = quant_contract.run(x).astype(np.float64)
                batch_f = float_contract.run(x).astype(np.float64)
                report["batch_reference"] = dict(
                    quantized_batch_vs_single_max_abs_delta=float(np.abs(batch_q - quant_out).max()),
                    float_batch_vs_single_max_abs_delta=float(np.abs(batch_f - float_out).max()),
                    note="reported only; the judged values are the batch-1 results",
                )
                report["metrics"] = metrics
                checks = judge(metrics, target_type, thresholds)
                report["checks"] = checks
                if not all(checks.values()):
                    reason = "tolerance_exceeded"
                    detail = ", ".join(k for k, ok in checks.items() if not ok)
            except ArithmeticError as exc:
                reason, detail = "non_finite_output", str(exc)
            except TimeoutError as exc:  # D1: the comparison only; the quantizer call above is not under the deadline
                reason, detail = "timeout", f"{exc} (the quantization conversion itself is not time-bounded)"
        sha_after = checkpoints.hash_file(model_path)
        report["model_sha256_after"], report["source_unchanged"] = sha_after, sha_after == sha_before
        if sha_after != sha_before:
            reason, detail = "source_modified", "the source model changed during the run"
        status = "failed" if reason else "passed"
        report["status"] = status
        if reason:
            report["failure_reason"], report["failure_detail"] = reason, detail
            quant_path.unlink(missing_ok=True)  # Q18=A: a failed conversion leaves no usable model file
        report["elapsed_seconds"] = round(time.monotonic() - started, 3)
        omitted: list = []
        checkpoints.atomic_json(report_path, drop_nonfinite(report, "report", omitted)
                                | ({"omitted_non_finite": omitted} if omitted else {}))
        quant_contract = None  # release the open handle on the quantized file (Windows cannot rename an in-use folder)
        gc.collect()
        os.rename(work, final_dir)
    except BaseException:
        shutil.rmtree(work, ignore_errors=True)
        raise
    _print_quantize_summary(report, final_dir)
    return EXIT_OK if status == "passed" else EXIT_FAILED


def _print_quantize_summary(report: dict, folder: Path) -> None:
    print(f"verify_model --quantize: {report['status'].upper()}  run={report['run_id']}  folder={folder}")
    population = report["population"]
    print(f"  population: validation {population['sampled']}/{population['validation_total']} windows from {population['symbols']} symbols"
          f" (oos_consumed={population['oos_consumed']})")
    print(f"  thresholds: {report['thresholds_basis']}; validation population verified by {report['population_verification']} (window count not checked)")
    if "quantization" in report:
        print(f"  quantized compute nodes: {report['quantization']['quantized_compute_nodes'] or 'none'}")
    for key, value in report.get("metrics", {}).items():
        print(f"  {key} = {value:.6g}" if isinstance(value, float) else f"  {key} = {value}")
    if report["status"] != "passed":
        print(f"  FAILED: {report['failure_reason']}: {report['failure_detail']}")


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


# --- Self-check ----------------------------------------------------------------


def build_fixture_model(path: Path, *, window: int, channels: int, classes: int, seed: int, metadata: dict | None,
                        softmax: bool = True, weight_scale: float = 0.35, with_matmul: bool = True) -> None:
    """Hand-built ``Flatten -> MatMul -> Add [-> Softmax]`` model (the graph family of ``Assets/generate_onnx_fixtures.py``)."""
    import onnx
    from onnx import TensorProto, helper

    flat = window * channels
    rng = np.random.default_rng(seed)
    weight = rng.normal(0.0, weight_scale, size=(flat, classes)).astype(np.float32)
    bias = rng.normal(0.0, 0.05, size=(classes,)).astype(np.float32)
    inp = helper.make_tensor_value_info("input", TensorProto.FLOAT, ["batch", window, channels])
    out = helper.make_tensor_value_info("output", TensorProto.FLOAT, ["batch", classes])
    if with_matmul:
        tail = "logits" if softmax else "output"
        nodes = [helper.make_node("Flatten", ["input"], ["flat"], axis=1),
                 helper.make_node("MatMul", ["flat", "W"], ["raw"]),
                 helper.make_node("Add", ["raw", "B"], [tail])]
        if softmax:
            nodes.append(helper.make_node("Softmax", ["logits"], ["output"], axis=1))
        inits = [helper.make_tensor("W", TensorProto.FLOAT, [flat, classes], weight.flatten()),
                 helper.make_tensor("B", TensorProto.FLOAT, [classes], bias)]
    else:  # no weight-bearing op: nothing for the dynamic quantizer to convert
        nodes = [helper.make_node("Flatten", ["input"], ["flat"], axis=1),
                 helper.make_node("Softmax", ["flat"], ["output"], axis=1)]
        inits = []
    model = helper.make_model(helper.make_graph(nodes, "verify_model_fixture", [inp], [out], initializer=inits),
                              opset_imports=[helper.make_opsetid("", 17)])
    model.ir_version = 9  # compatible with Microsoft.ML.OnnxRuntime 1.24.x
    if metadata is not None:
        onnx_meta.apply(model, metadata)
    onnx.checker.check_model(model)
    onnx.save(model, str(path))


def _fixture_series(rows: int, names: Tuple[str, ...], seed: int) -> dict:
    """Seeded random-walk OHLCV per symbol: ``{name: (dates, ohlcv)}`` with H >= max(O,C) and L <= min(O,C)."""
    out = {}
    start = datetime.date(2020, 1, 1)
    for k, name in enumerate(names):
        rng = np.random.default_rng(seed + k)
        close = 100.0 * np.exp(np.cumsum(rng.normal(0.0, 0.01, rows)))
        open_ = np.concatenate([[100.0], close[:-1]]) * np.exp(rng.normal(0.0, 0.002, rows))
        high = np.maximum(open_, close) * np.exp(np.abs(rng.normal(0.0, 0.003, rows)))
        low = np.minimum(open_, close) * np.exp(-np.abs(rng.normal(0.0, 0.003, rows)))
        volume = rng.integers(1000, 5000, rows).astype(np.float64)
        out[name] = ([start + datetime.timedelta(days=i) for i in range(rows)], np.column_stack([open_, high, low, close, volume]))
    return out


def _write_fixture_parquet(directory: Path, series: dict, rows: int) -> None:
    import polars as pl

    directory.mkdir(parents=True, exist_ok=True)
    for name, (dates, arr) in series.items():
        frame = {"date": dates[:rows]}
        frame.update({col: arr[:rows, i] for i, col in enumerate(ds.OHLCV_COLUMNS)})
        pl.DataFrame(frame).write_parquet(directory / f"{name}.parquet")


def _selfcheck_args(model: Path, data_dir: Path, root: Path, *extra: str) -> argparse.Namespace:
    return _parse_args(["--quantize", "--model", str(model), "--data-dir", str(data_dir),
                        "--quantize-out-root", str(root), *extra])


def _run_selfcheck() -> None:
    import onnx

    window, channels, horizon, threshold, splits = 10, ds.FEATURES_OHLCV, 5, ds.DEFAULT_THRESHOLD, ds.DEFAULT_WF_SPLITS
    names = ("AAA", "BBB", "CCC")
    skipped = []
    ds.DATA_ROOT.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="sa_verify_model_selfcheck_", dir=str(ds.DATA_ROOT)) as tmp_name:
        tmp = Path(tmp_name)

        # group 1: argument and threshold rules
        base = _parse_args(["--quantize"])
        validate_quantize_args(base)
        for bad in (["--quantize-ops", "Conv"], ["--quantize-ops", "MatMul,MatMul"], ["--quantize-samples", "0"],
                    ["--quantize-samples", str(ds.MAX_COMPOSED_BATCH_SAMPLES + 1)], ["--quantize-min-agreement", "nan"],
                    ["--quantize-min-agreement", "0"], ["--quantize-max-prob-delta", "-0.1"], ["--quantize-max-seconds", "0"],
                    ["--quantize-max-seconds", str(QUANT_MAX_SECONDS + 1)], ["--outer-fold-index", "-1"], ["--wf-splits", "0"]):
            try:
                validate_quantize_args(_parse_args(["--quantize", *bad]))
            except ValueError:
                continue
            raise AssertionError(f"argument {bad} should be rejected")
        assert parse_ops("MatMul,Gemm") == ("MatMul", "Gemm")
        print("  group 1 argument rules OK")

        # group 2: sample selection is seeded, ordered, without replacement, and total when small
        a, b = select_sample(10_000, 2048, 42), select_sample(10_000, 2048, 42)
        assert np.array_equal(a, b) and a.shape == (2048,) and np.all(np.diff(a) > 0) and a.max() < 10_000
        assert not np.array_equal(a, select_sample(10_000, 2048, 43))
        assert np.array_equal(select_sample(5, 2048, 42), np.arange(5))
        print("  group 2 sample selection OK")

        # group 3: non-finite values are omitted and listed, never replaced by 0
        omitted: list = []
        cleaned = drop_nonfinite(dict(a=1.0, b=float("nan"), c=dict(d=float("inf"), e=np.float32(2.0)), f=[1, np.int64(3)]), "r", omitted)
        assert cleaned == dict(a=1.0, c=dict(e=2.0), f=[1, 3]) and sorted(omitted) == ["r.b", "r.c.d"], (cleaned, omitted)
        print("  group 3 non-finite handling OK")

        # group 4: the converter on a model with weight-bearing ops, and a model with none
        meta = onnx_meta.build_contract(
            feature_mode="ohlcv_minmax", window_size=window, channels=channels, horizon=horizon, threshold=threshold,
            wf_splits=splits, seed=42, producer="verify_model.py selfcheck",
            analysis_ref="m.onnx.analysis.json", calibration_ref="m.onnx.calibration.json")
        source = tmp / "m.onnx"
        build_fixture_model(source, window=window, channels=channels, classes=3, seed=1, metadata=meta)
        digest = checkpoints.hash_file(source)
        quant = tmp / "m.quant.onnx"
        quantize_model(source, quant, ops=QUANT_DEFAULT_OPS, weight_type="qint8", per_channel=False, scratch=tmp / "scratch")
        counts = count_quantized_nodes(quant)
        assert counts.get("MatMulInteger", 0) >= 1, counts
        assert checkpoints.hash_file(source) == digest, "the source model must not change"
        bare = tmp / "bare.onnx"
        build_fixture_model(bare, window=1, channels=3, classes=3, seed=1, metadata=None, with_matmul=False)
        bare_quant = tmp / "bare.quant.onnx"
        quantize_model(bare, bare_quant, ops=QUANT_DEFAULT_OPS, weight_type="qint8", per_channel=False, scratch=tmp / "scratch")
        assert count_quantized_nodes(bare_quant) == {}, "a model without MatMul/Gemm/LSTM has nothing to quantize"
        print(f"  group 4 converter OK (quantized compute nodes {counts}; weightless model -> none)")

        # group 5: contract comparison (Q16/Q17)
        expected, removed = derived_metadata(meta)
        assert removed == sorted([model_analysis.REFERENCE_KEY, calibration.REFERENCE_KEY]), removed
        assert "scaler_ref" not in expected and set(expected) == set(meta) - set(removed)
        onnx_meta.load_apply_save(quant, expected)
        s_snap, q_snap = contract_snapshot(source), contract_snapshot(quant)
        assert compare_contracts(s_snap, q_snap, expected) == [], compare_contracts(s_snap, q_snap, expected)
        assert q_snap["extra_domains"] == [], q_snap["extra_domains"]  # MatMulInteger is a standard-domain op
        assert compare_contracts(s_snap, q_snap, dict(expected, window_size="11")), "a metadata difference must be reported"
        renamed = onnx.load(str(quant))
        renamed.graph.output[0].name = "probs"
        renamed_path = tmp / "renamed.onnx"
        onnx.save(renamed, str(renamed_path))
        assert compare_contracts(s_snap, contract_snapshot(renamed_path), expected), "an output rename must be reported"
        print("  group 5 contract comparison OK (refs dropped, differences detected)")

        # group 6: judge gates and the metric definitions on hand-made outputs
        th = QuantizeThresholds(0.98, 0.05, 0.01, 0.01).validate()
        f = np.array([[0.7, 0.2, 0.1], [0.1, 0.8, 0.1], [0.2, 0.2, 0.6], [0.5, 0.3, 0.2]])
        q = f + np.array([[0.01, -0.01, 0.0]] * 4)
        m = compare_outputs(f, q, np.array([0, 1, 2, 0]), "classification")
        assert abs(m["max_abs_prob_delta"] - 0.01) < 1e-12 and m["argmax_agreement"] == 1.0 and m["accuracy_drop"] == 0.0, m
        assert all(judge(m, "classification", th).values())
        flipped = f.copy()
        flipped[0] = [0.1, 0.7, 0.2]
        m2 = compare_outputs(f, flipped, np.array([0, 1, 2, 0]), "classification")
        assert m2["argmax_agreement"] == 0.75 and m2["accuracy_drop"] == 0.25 and not judge(m2, "classification", th)["argmax_agreement"]
        try:
            compare_outputs(f, np.where(f > 0.5, np.nan, f), np.array([0, 1, 2, 0]), "classification")
        except ArithmeticError:
            pass
        else:
            raise AssertionError("non-finite output must fail")
        mr = compare_outputs(np.array([[0.01], [-0.02]]), np.array([[0.012], [-0.019]]), np.array([[0.0], [0.0]]), "regression")
        assert abs(mr["max_abs_delta"] - 0.002) < 1e-12 and mr["sign_agreement"] == 1.0
        assert judge(mr, "regression", th) == dict(max_abs_delta=True)
        assert judge(dict(mr, max_abs_delta=0.0101), "regression", th) == dict(max_abs_delta=False)
        print("  group 6 metrics and gates OK")

        # group 7: validation-population reconstruction survives data growth and never reads past the validation span
        series = _fixture_series(330, names, 7)
        old_dir, new_dir = tmp / "old", tmp / "new"
        _write_fixture_parquet(old_dir, series, 300)
        _write_fixture_parquet(new_dir, series, 330)
        old_symbols, old_dates = ds.load_parquet_dir(old_dir, return_dates=True)
        ranges = ds.train_val_date_ranges(old_symbols, old_dates, feature_mode="ohlcv_minmax", window=window, horizon=horizon,
                                          threshold=threshold, n_splits=splits)
        direct = ds.split_symbols_chronological(old_symbols, "ohlcv_minmax", window=window, horizon=horizon, threshold=threshold,
                                                n_splits=splits, dates=old_dates)
        meta_d = onnx_meta.build_contract(
            feature_mode="ohlcv_minmax", window_size=window, channels=channels, horizon=horizon, threshold=threshold,
            wf_splits=splits, seed=42, producer="verify_model.py selfcheck", date_ranges=ranges)
        model_d = tmp / "d.onnx"
        build_fixture_model(model_d, window=window, channels=channels, classes=3, seed=2, metadata=meta_d)
        args_d = _selfcheck_args(model_d, new_dir, tmp / "runs", "--max-symbols", "3")
        pop = reconstruct_validation_population(meta_d, model_d, args_d)
        assert np.array_equal(pop["x"], direct[2].astype(np.float32)) and np.array_equal(pop["y"], direct[3]), \
            "the rebuilt validation population must equal the training-time one after rows were appended"
        assert pop["oos_consumed"] is False and pop["validation_span"] == [ranges["validation_start"], ranges["validation_end"]]
        try:  # a bound after the model validation_end would read the out-of-sample tail
            reconstruct_validation_population(meta_d, model_d, _selfcheck_args(
                model_d, new_dir, tmp / "runs", "--data-end", (datetime.date.fromisoformat(ranges["validation_end"])
                                                               + datetime.timedelta(days=1)).isoformat()))
        except PrerequisiteError:
            pass
        else:
            raise AssertionError("--data-end after validation_end must be refused")
        try:  # wrong split argument -> the rebuilt span leaves the metadata span or the population differs
            wrong = reconstruct_validation_population(meta_d, model_d, _selfcheck_args(model_d, new_dir, tmp / "runs", "--wf-splits", "2"))
            assert not np.array_equal(wrong["x"], pop["x"])
        except PrerequisiteError:
            pass
        print(f"  group 7 population reconstruction OK ({pop['x'].shape[0]} validation windows, oos_consumed=False)")

        # group 8: end to end, passing run
        root = tmp / "runs"
        code = run_quantize(_selfcheck_args(model_d, new_dir, root, "--max-symbols", "3"))
        assert code == EXIT_OK, code
        runs = [p for p in root.iterdir() if p.is_dir()]
        assert len(runs) == 1 and not runs[0].name.endswith(QUANT_INCOMING_SUFFIX), runs
        report = json.loads((runs[0] / f"d{QUANT_REPORT_SUFFIX}").read_text(encoding="utf-8"))
        assert report["status"] == "passed" and report["source_unchanged"] is True and "failure_reason" not in report, report
        assert report["quantization"]["quantized_compute_nodes"] and report["population"]["oos_consumed"] is False
        assert report["metrics"]["n"] == report["population"]["sampled"] and (runs[0] / f"d{QUANT_MODEL_SUFFIX}").is_file()
        assert not (runs[0] / "scratch").exists()
        assert report["model_sha256"] == checkpoints.hash_file(model_d) == report["model_sha256_after"]
        assert report["thresholds_basis"] == THRESHOLDS_BASIS_DEFAULT == "planning_default_uncalibrated"                       # K2: nothing was overridden
        assert report["population_verification"] == POPULATION_VERIFICATION == "period_only" and "NUMBER of validation windows" in report["population_verification_note"]
        print(f"  group 8 passing run OK (agreement {report['metrics']['argmax_agreement']:.3f}, "
              f"max |dp| {report['metrics']['max_abs_prob_delta']:.2e}, batch-vs-single "
              f"{report['batch_reference']['quantized_batch_vs_single_max_abs_delta']:.2e})")

        # group 9: failure paths -> exit 1 with the quantized file deleted, or exit 3 with nothing published
        root2 = tmp / "runs_strict"
        code = run_quantize(_selfcheck_args(model_d, new_dir, root2, "--max-symbols", "3", "--quantize-max-prob-delta", "0"))
        assert code == EXIT_FAILED, code
        failed_dir = next(p for p in root2.iterdir() if p.is_dir())
        failed = json.loads((failed_dir / f"d{QUANT_REPORT_SUFFIX}").read_text(encoding="utf-8"))
        assert failed["status"] == "failed" and failed["failure_reason"] == "tolerance_exceeded", failed
        assert failed["thresholds_basis"] == THRESHOLDS_BASIS_USER == "user_supplied" and failed["population_verification"] == "period_only"
        assert QuantizeThresholds(0.98, 0.05, 0.01, 0.01).basis() == THRESHOLDS_BASIS_DEFAULT and QuantizeThresholds(0.98, 0.05, 0.01, 0.02).basis() == THRESHOLDS_BASIS_USER
        assert not (failed_dir / f"d{QUANT_MODEL_SUFFIX}").exists(), "a failed conversion must leave no model file"
        assert checkpoints.hash_file(model_d) == failed["model_sha256"]
        root3 = tmp / "runs_none"
        no_meta = tmp / "nometa.onnx"
        build_fixture_model(no_meta, window=window, channels=channels, classes=3, seed=3, metadata=None)
        for model, extra in ((no_meta, ()), (tmp / "missing.onnx", ()), (model_d, ("--data-end", "2099-01-01"))):
            assert run_quantize(_selfcheck_args(model, new_dir, root3, *extra)) == EXIT_PREREQUISITE, (model, extra)
        assert not root3.exists() or not any(root3.iterdir()), "a prerequisite failure must publish nothing"
        # D2: a missing or synthetic --data-dir is a prerequisite (exit 3), not a traceback; nothing is published
        import polars as pl
        import types
        from unittest import mock

        synth_dir = tmp / "synthetic_data"
        synth_dir.mkdir()
        for parquet in new_dir.glob("*.parquet"):
            pl.read_parquet(parquet).write_parquet(synth_dir / parquet.name, metadata={ds.SYNTHETIC_ORIGIN_KEY: ds.SYNTHETIC_ORIGIN})
        root4 = tmp / "runs_data_dir"
        for bad_dir in (tmp / "no_such_data_dir", synth_dir):
            assert run_quantize(_selfcheck_args(model_d, bad_dir, root4, "--max-symbols", "3")) == EXIT_PREREQUISITE, bad_dir
        assert not root4.exists() or not any(root4.iterdir()), "a data-dir failure must publish nothing"
        # D1: the comparison exceeds --quantize-max-seconds -> a failed report (failure_reason timeout), no model file, exit 1.
        # The clock is replaced (first reading = start, every later one far past the deadline) so the test is deterministic.
        calls = {"n": 0}

        def expired_clock() -> float:
            calls["n"] += 1
            return 0.0 if calls["n"] == 1 else 1e9

        root5 = tmp / "runs_timeout"
        with mock.patch.object(sys.modules[__name__], "time", types.SimpleNamespace(monotonic=expired_clock)):
            assert run_quantize(_selfcheck_args(model_d, new_dir, root5, "--max-symbols", "3", "--quantize-max-seconds", "1")) == EXIT_FAILED
        (timeout_dir,) = [p for p in root5.iterdir()]
        timed_out = json.loads((timeout_dir / f"d{QUANT_REPORT_SUFFIX}").read_text(encoding="utf-8"))
        assert not timeout_dir.name.endswith(QUANT_INCOMING_SUFFIX) and timed_out["status"] == "failed", timed_out
        assert timed_out["failure_reason"] == "timeout" and "not time-bounded" in timed_out["failure_detail"], timed_out
        assert not (timeout_dir / f"d{QUANT_MODEL_SUFFIX}").exists() and "metrics" not in timed_out
        assert timed_out["source_unchanged"] is True and checkpoints.hash_file(model_d) == timed_out["model_sha256"]
        print("  group 9 failure paths OK (tolerance / timeout -> exit 1 without model file; prerequisites incl. missing or synthetic "
              "data dir -> exit 3, nothing published)")

        # group 10: a regression model (label-free gate on the maximum difference)
        meta_r = onnx_meta.build_contract(
            feature_mode="ohlcv_minmax", window_size=window, channels=channels, horizon=horizon, threshold=threshold,
            wf_splits=splits, seed=42, producer="verify_model.py selfcheck", date_ranges=ranges, target_type="regression")
        model_r = tmp / "r.onnx"
        build_fixture_model(model_r, window=window, channels=channels, classes=1, seed=4, metadata=meta_r, softmax=False,
                            weight_scale=0.01)
        assert run_quantize(_selfcheck_args(model_r, new_dir, tmp / "runs_reg", "--max-symbols", "3")) == EXIT_OK
        rep_r = json.loads((next(p for p in (tmp / "runs_reg").iterdir()) / f"r{QUANT_REPORT_SUFFIX}").read_text(encoding="utf-8"))
        assert rep_r["population"]["target_type"] == "regression" and "max_abs_delta" in rep_r["metrics"] and list(rep_r["checks"]) == ["max_abs_delta"]
        print(f"  group 10 regression run OK (max |dy| {rep_r['metrics']['max_abs_delta']:.2e})")

        # group 11: an LSTM graph (needs torch; reported as skipped, never as passed, when it is missing)
        try:
            import torch
            from torch import nn
        except ImportError:
            skipped.append("group 11 LSTM (torch not importable)")
        else:
            class _Lstm(nn.Module):
                def __init__(self):
                    super().__init__()
                    self.lstm, self.head = nn.LSTM(channels, 8, batch_first=True), nn.Linear(8, 3)

                def forward(self, x):
                    return torch.softmax(self.head(self.lstm(x)[0][:, -1]), dim=-1)

            torch.manual_seed(5)
            net = _Lstm().eval()
            lstm_path = tmp / "lstm.onnx"
            torch.onnx.export(net, torch.randn(1, window, channels), str(lstm_path), input_names=["input"], output_names=["output"],
                              dynamic_axes={"input": {0: "batch"}, "output": {0: "batch"}}, opset_version=17, dynamo=False)
            lstm_quant = tmp / "lstm.quant.onnx"
            quantize_model(lstm_path, lstm_quant, ops=QUANT_DEFAULT_OPS, weight_type="qint8", per_channel=False, scratch=tmp / "scratch")
            lstm_counts = count_quantized_nodes(lstm_quant)
            assert lstm_counts.get("DynamicQuantizeLSTM", 0) >= 1, lstm_counts
            probe = np.random.default_rng(6).normal(size=(32, window, channels)).astype(np.float32)
            delta = float(np.abs(ModelContract(ort.InferenceSession(str(lstm_path), providers=["CPUExecutionProvider"])).run(probe)
                                 - ModelContract(ort.InferenceSession(str(lstm_quant), providers=["CPUExecutionProvider"])).run(probe)).max())
            assert delta < QUANT_DEFAULT_MAX_PROB_DELTA, delta
            print(f"  group 11 LSTM OK (quantized compute nodes {lstm_counts}, max |dp| {delta:.2e})")

    # group 12: VEC-7 on a legacy contract skips only the key-set check, visibly (E01 A / E02 A)
    import contextlib
    import io

    v2_only = (onnx_meta.PURGE_GAP_KEY, onnx_meta.OUTPUT_SEMANTIC_KEY, onnx_meta.OUTPUT_UNIT_KEY, onnx_meta.OUTPUT_HORIZON_KEY,
               onnx_meta.OUTPUT_TIMEFRAME_KEY, onnx_meta.OUTPUT_CONFIDENCE_TYPE_KEY, onnx_meta.TARGET_FORMULA_KEY)  # absent in a real v1 file
    with tempfile.TemporaryDirectory(prefix="sa_verify_model_vec7_", dir=str(ds.DATA_ROOT)) as vec7_dir:
        tmp = Path(vec7_dir)

        v2_meta = onnx_meta.build_contract(feature_mode="ohlcv_minmax", window_size=window, channels=channels, horizon=horizon,
                                           threshold=threshold, wf_splits=splits, seed=42, producer="verify_model.py selfcheck")

        def vec7(metadata: dict) -> str:
            path = tmp / "vec7.onnx"
            build_fixture_model(path, window=window, channels=channels, classes=3, seed=1, metadata=metadata)
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                check_metadata(ModelContract(ort.InferenceSession(str(path), providers=["CPUExecutionProvider"])))
            return out.getvalue()

        current = vec7(v2_meta)
        assert "VEC-7 metadata OK" in current and "SKIP" not in current.split("VEC-7 metadata OK")[0]
        legacy_meta = {k: v for k, v in v2_meta.items() if k not in v2_only}
        legacy_meta["model_contract_version"] = "1"
        skipped_text = vec7(legacy_meta)
        assert "key set: SKIP (legacy contract v1" in skipped_text and "VEC-7 metadata OK" in skipped_text, skipped_text
        for broken, expect in (({k: v for k, v in v2_meta.items() if k != "com.stockanalyzer.output.unit"}, "missing keys"),
                               ({k: v for k, v in legacy_meta.items() if k != "feature_mode"}, "legacy metadata missing keys"),
                               ({**legacy_meta, "model_contract_version": "x"}, "missing keys")):
            try:
                vec7(broken)
            except AssertionError as exc:
                assert expect in str(exc), (expect, str(exc))
            else:
                raise AssertionError(f"VEC-7 must still fail for {expect}")
        assert legacy_contract_version({"model_contract_version": onnx_meta.MODEL_CONTRACT_VERSION}) is None
        assert legacy_contract_version({"model_contract_version": "99"}) is None and legacy_contract_version({}) is None
        print("  group 12 VEC-7 legacy contract OK (key set skipped visibly, other checks and the current contract unchanged)")

    suffix = f", {len(skipped)} skipped: {'; '.join(skipped)}" if skipped else ""
    print(f"verify_model.py selfcheck: OK (12 groups{suffix})" if not skipped else f"verify_model.py selfcheck: OK ({12 - len(skipped)} groups passed{suffix})")


def _iso_date(text: str) -> str:
    try:
        return datetime.date.fromisoformat(text).isoformat()
    except ValueError as exc:
        raise argparse.ArgumentTypeError(f"expected YYYY-MM-DD, got {text!r}") from exc


def _parse_args(argv):
    p = argparse.ArgumentParser(description="Verify an exported trend_predictor.onnx against the C# inference contract.")
    p.add_argument("--model", type=Path, default=DEFAULT_MODEL)
    p.add_argument("--feature-mode", choices=ds.FEATURE_MODES, default=None,
                   help="Override; inferred from input channels when omitted (5ch -> ohlcv_minmax).")
    add_population_arguments(p)          # --data-dir, --max-symbols and the population arguments of --quantize (one owner, shared with the standalone tools)
    p.add_argument("--selfcheck", action="store_true", help="Run the assertions (including the quantization workflow) and exit.")
    q = p.add_argument_group("dynamic quantization (T17); runs INSTEAD of the boundary-vector suite")
    q.add_argument("--quantize", action="store_true", help="Quantize the model and compare it with the float model.")
    q.add_argument("--quantize-ops", default=",".join(QUANT_DEFAULT_OPS), help=f"Comma list, subset of {QUANT_ALLOWED_OPS}.")
    q.add_argument("--quantize-weight-type", choices=QUANT_WEIGHT_TYPES, default=QUANT_DEFAULT_WEIGHT_TYPE)
    q.add_argument("--quantize-per-channel", action="store_true")
    q.add_argument("--quantize-samples", type=int, default=QUANT_DEFAULT_SAMPLES, help="Validation windows compared.")
    q.add_argument("--quantize-seed", type=int, default=QUANT_DEFAULT_SEED)
    q.add_argument("--quantize-min-agreement", type=float, default=QUANT_DEFAULT_MIN_AGREEMENT)
    q.add_argument("--quantize-max-prob-delta", type=float, default=QUANT_DEFAULT_MAX_PROB_DELTA)
    q.add_argument("--quantize-max-accuracy-drop", type=float, default=QUANT_DEFAULT_MAX_ACCURACY_DROP)
    q.add_argument("--quantize-max-regression-delta", type=float, default=QUANT_DEFAULT_MAX_REGRESSION_DELTA)
    q.add_argument("--quantize-max-seconds", type=int, default=QUANT_MAX_SECONDS, help=f"Run bound; env {QUANT_MAX_SECONDS_ENV}.")
    q.add_argument("--quantize-out-root", type=Path, default=QUANT_OUT_DIR)
    return p.parse_args(argv)


if __name__ == "__main__":
    _args = _parse_args(sys.argv[1:])
    if _args.selfcheck:
        _run_selfcheck()
    elif _args.quantize:
        try:
            validate_quantize_args(_args)
        except ValueError as _exc:
            print(f"verify_model --quantize: invalid argument: {_exc}", file=sys.stderr)
            raise SystemExit(2)
        raise SystemExit(run_quantize(_args))
    else:
        raise SystemExit(run_suite(_args))
