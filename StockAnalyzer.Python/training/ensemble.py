"""T13 ensemble methods: Bagging members and Stacking weights, without any new inference contract.

Every member is an independent, independently evaluated classification ONNX produced by the ordinary
single-model pipeline (``run_training._train_one``). The ensemble is the existing T06 linear mixture
(``EnsemblePredictionService``): ``p = sum_m w_m p_m`` renormalised by its total. This module owns only

* **Bagging** (13-1=B ... 13-4=A): member ``i`` fits a time-series block bootstrap of its own train
  side (:func:`dataset.bagging_block_bootstrap`); the weights are equal (1/M).
* **Stacking** (13-5=A ... 13-7=A): the weights are fitted on out-of-fold probabilities. The
  OOF rows of fold ``k`` are the test rows of the independent outer-fold models of fold ``k``
  (purged, expanding window, past-only); fold ``n-1`` -- the release/outer test -- is never used to fit.
* the leakage proof (fit rows vs. release rows), the mixture evaluation and the manifest
  ``<stem>.ensemble.json`` the C# deployment reads (schema ``ensemble_manifest`` v1).

Boosting (13-9=A) adds no code here: the existing multiclass GBDT trainer (``train_lightgbm.py``) is the
Boosting method of the extended-learning requirement, and a LightGBM model may be a Bagging member
(``framework=lightgbm``) or a Stacking base (``lightgbm``).

Stacking weights solve ``min_w NLL(sum_m w_m p_m(y))`` over the simplex (``w >= 0``, ``sum w = 1``) by
the monotone EM fixed point ``w_m <- mean_i[ w_m p_m(y_i) / sum_j w_j p_j(y_i) ]``.

With ``stacking_weight_source = release_meta_segment`` the release weights are instead fitted on a meta segment
(the calibration-style time-ordered tail of the release fold's train side) that the release members were NOT trained on,
scored by those very members; the conventional past-OOF weights are still computed and recorded for comparison.

Numeric classification: the EM constants are constants of the algorithm; ``MIN_META_SAMPLES`` and
``MAX_EVALUATION_PEAK_BYTES`` are safety bounds (the run fails explicitly beyond them -- no silent fallback to equal weights,
no sampling); the member upper bound is the existing single owner ``max_ensemble_members`` (``SA_MAX_ENSEMBLE_MEMBERS``).

Run ``python ensemble.py --selfcheck`` to verify the formulas.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import sys
import tracemalloc
from pathlib import Path
from typing import Dict, List, Sequence, Tuple

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import dataset as ds  # noqa: E402
import metrics  # noqa: E402

SCHEMA_VERSION = 1
KIND = "ensemble_manifest"
METHOD_NONE = "none"
METHOD_BAGGING = "bagging"
METHOD_STACKING = "stacking"
METHODS = (METHOD_NONE, METHOD_BAGGING, METHOD_STACKING)
WEIGHTS_EQUAL = "equal"
WEIGHTS_SIMPLEX_NLL = "simplex_nll_em"
SUM_TOLERANCE = 1e-4  # mirror of C# EnsembleSpec.SumTolerance
# Stacking provenance labels recorded in the manifest (the C# loader ignores keys it does not read).
WEIGHT_FIT_POPULATION = "previous_outer_oof"
DEPLOYMENT_POPULATION = "release_fold_models"
# Where the stacking weights are fitted (job config ``stacking_weight_source``; mirrored by C# ``StackingWeightSource``).
WEIGHT_SOURCE_PAST_OOF = "past_out_of_fold"
WEIGHT_SOURCE_META = "release_meta_segment"
WEIGHT_SOURCES = (WEIGHT_SOURCE_PAST_OOF, WEIGHT_SOURCE_META)
META_FIT_POPULATION = "release_meta_segment"

# defaults (config tunables; mirrored by C# TrainingJobConfig.DefaultBaggingMembers)
DEFAULT_BAGGING_MEMBERS = 5
MIN_MEMBERS = 2
# the trainers' own default seed (train_*.py DEFAULT_SEED); the run seed bagging streams derive from
DEFAULT_RUN_SEED = 42

# artifact layout (mirror of C# TrainingEnsembleArtifactLayout / TrainingEnsembleManifest.FileSuffix; the host rolls a failed run
# back by these names, see Tests/EnsembleMixtureParity.json)
MEMBER_STEM_MARKER = "_m"
MEMBER_RUN_ID_MARKER = "-m"
ENSEMBLE_FILE_SUFFIX = ".ensemble.json"

# EM constants (algorithm)
EM_MAX_ITERATIONS = 10_000
EM_TOLERANCE = 1e-12
PROBABILITY_FLOOR = metrics.LOGLOSS_EPS

# safety bound (code Limit): minimum out-of-fold rows needed to fit stacking weights
MIN_META_SAMPLES = 100

# Captured member probabilities are kept as float32: the ONNX output is float32, so this is lossless (the mixture and the
# EM work in float64 after ``pool_fold``). The item size is part of the capture-size estimate below.
CAPTURE_DTYPE = np.float32
CAPTURE_ITEM_BYTES = int(np.dtype(CAPTURE_DTYPE).itemsize)
# safety bound (code Limit, approved 2026-10-03): the working-set PEAK of the evaluation phase, i.e. what
# ``evaluate`` holds at once -- the captured probabilities of every member and outer fold plus its own working copies. Above it
# the run fails before training instead of sampling rows (sampling would change the fitted weights). The largest capture that is
# allowed is derived from this peak (:func:`max_capture_bytes`), not fixed: the working copies make the peak a multiple of the
# captured bytes that depends on the member count and the class count.
MAX_EVALUATION_PEAK_BYTES = 4 << 30  # 4 GiB
# How ``evaluate`` turns captured rows into a peak (measured with tracemalloc on synthetic captures, see the selfcheck): it widens
# the probabilities to float64, pools every fold, concatenates the past folds for the EM fit and builds likelihood arrays, so the
# peak is ``EVALUATION_PEAK_FACTOR`` float64 copies of all probabilities plus a per-row cost (row keys, labels, the sets of the
# leakage proof). Measured: 1.8 to 3.5 copies and 130 to 160 B per test window (stacking and a single dominating fold are the
# worst cases); the constants (approved 2026-10-03) are the rounded-up bound, which stayed 1.3 to 2.2 times above every measured
# peak. The budget is a fixed code Limit on purpose: it is not derived from the machine's free memory, so a job is accepted or
# refused the same way everywhere.
EVALUATION_ITEM_BYTES = 8
EVALUATION_PEAK_FACTOR = 4
EVALUATION_ROW_OVERHEAD_BYTES = 256

# framework -> architectures a stacking base may name (None: the framework has no architecture choice)
BASE_FRAMEWORKS = ("pytorch", "tensorflow", "lightgbm")
LIGHTGBM_ARCHITECTURE = "gbdt"

FoldRows = Dict[str, Tuple[np.ndarray, np.ndarray, np.ndarray]]  # symbol -> (anchors, y, probs)


# --- member planning --------------------------------------------------------------------------


def parse_base(text: str) -> Tuple[str, str]:
    """``framework[:architecture]`` -> ``(framework, architecture)``; lightgbm takes no architecture."""
    if not isinstance(text, str) or not text.strip() or text != text.strip():
        raise ValueError("a stacking base must be a non-empty 'framework[:architecture]' string")
    framework, separator, architecture = text.partition(":")
    framework = framework.lower()
    if framework not in BASE_FRAMEWORKS:
        raise ValueError(f"stacking base framework {framework!r} not in {list(BASE_FRAMEWORKS)}")
    if framework == "lightgbm":
        if separator:
            raise ValueError("a lightgbm stacking base takes no architecture")
        return framework, LIGHTGBM_ARCHITECTURE
    architecture = architecture.strip().lower()
    if not separator or not architecture:
        raise ValueError(f"a {framework} stacking base requires an architecture ('{framework}:<architecture>')")
    return framework, architecture


def validate_plan(method: str, members: int, bases: Sequence[str], target_type: str, max_members: int) -> None:
    """Shape of the requested ensemble; ``max_members`` is the single owner ``max_ensemble_members``."""
    if method not in METHODS:
        raise ValueError(f"config.ensemble_method {method!r} not in {list(METHODS)}")
    if method == METHOD_NONE:
        if bases:
            raise ValueError("config.stacking_bases requires ensemble_method 'stacking'")
        return
    if target_type != "classification":
        raise ValueError("ensembles combine classification generations only")
    if method == METHOD_BAGGING:
        if bases:
            raise ValueError("config.stacking_bases requires ensemble_method 'stacking'")
        if type(members) is not int or not MIN_MEMBERS <= members <= max_members:
            raise ValueError(f"config.ensemble_members must be an integer from {MIN_MEMBERS} to {max_members}")
        return
    parsed = [parse_base(text) for text in bases]
    if not MIN_MEMBERS <= len(parsed) <= max_members:
        raise ValueError(f"config.stacking_bases needs {MIN_MEMBERS} to {max_members} entries")
    if len(set(parsed)) != len(parsed):
        raise ValueError("config.stacking_bases entries must be distinct")


BAG_FRACTION_KEY = "bag_fraction"
BAG_BLOCK_LENGTH_KEY = "bag_block_length"


def split_bagging_options(hyperparameters: Dict[str, str]) -> Tuple[float, int | None, Dict[str, str]]:
    """Take the job-level bagging tunables out of the hyperparameters.

    Returns ``(fraction, block_length, remaining)``: ``bag_fraction`` in ``(0, 1]`` (default
    :data:`dataset.BAGGING_FRACTION`) and ``bag_block_length`` >= 1 in samples (default ``None``: the trainer resolves
    window + horizon - 1). Keys are matched like the trainer flags they would collide with (``-``/``_``). A bad value fails
    explicitly; the remaining hyperparameters are what a member's own trainer receives.
    """
    found: Dict[str, str] = {}
    remaining: Dict[str, str] = {}
    for key, value in hyperparameters.items():
        name = str(key).strip().lstrip("-").replace("-", "_")
        if name in (BAG_FRACTION_KEY, BAG_BLOCK_LENGTH_KEY):
            if name in found:
                raise ValueError(f"hyperparameter {name} is given twice")
            found[name] = value
        else:
            remaining[key] = value
    fraction = ds.BAGGING_FRACTION
    if BAG_FRACTION_KEY in found:
        try:
            fraction = float(found[BAG_FRACTION_KEY])
        except (TypeError, ValueError):
            raise ValueError(f"hyperparameter {BAG_FRACTION_KEY} must be a number in (0, 1]") from None
        if not math.isfinite(fraction) or not 0 < fraction <= 1:
            raise ValueError(f"hyperparameter {BAG_FRACTION_KEY} must lie in (0, 1]")
    block_length = None
    if BAG_BLOCK_LENGTH_KEY in found:
        text = str(found[BAG_BLOCK_LENGTH_KEY]).strip()
        if not text.isascii() or not text.isdigit() or int(text) < 1:
            raise ValueError(f"hyperparameter {BAG_BLOCK_LENGTH_KEY} must be a positive integer")
        block_length = int(text)
    return fraction, block_length, remaining


def capture_bytes(members: int, fold_rows: Sequence[int], classes: int) -> int:
    """Estimated size of the captured probabilities: ``members x (rows of every outer fold) x classes x 4 B``."""
    if members < 1 or classes < 1 or any(type(rows) is not int or rows < 0 for rows in fold_rows):
        raise ValueError("capture size needs at least one member and class and nonnegative integer row counts")
    return members * sum(fold_rows) * classes * CAPTURE_ITEM_BYTES


def _peak_bytes_per_row(members: int, classes: int) -> int:
    if members < 1 or classes < 1:
        raise ValueError("the evaluation peak needs at least one member and class")
    return EVALUATION_PEAK_FACTOR * members * classes * EVALUATION_ITEM_BYTES + EVALUATION_ROW_OVERHEAD_BYTES


def evaluation_peak_bytes(members: int, fold_rows: Sequence[int], classes: int) -> int:
    """Estimated working-set peak of :func:`evaluate` (captured probabilities included) over all outer-fold and meta rows."""
    capture_bytes(members, fold_rows, classes)  # the argument validation lives there
    return sum(fold_rows) * _peak_bytes_per_row(members, classes)


def max_capture_rows(members: int, classes: int) -> int:
    """The most test windows (over all folds and the meta segment) whose evaluation peak fits :data:`MAX_EVALUATION_PEAK_BYTES`."""
    return MAX_EVALUATION_PEAK_BYTES // _peak_bytes_per_row(members, classes)


def max_capture_bytes(members: int, classes: int) -> int:
    """The capture size the peak budget allows for this shape: :func:`max_capture_rows` windows at :func:`capture_bytes` each."""
    return capture_bytes(members, [max_capture_rows(members, classes)], classes)


def check_capture_budget(members: int, fold_rows: Sequence[int], classes: int) -> int:
    """Return the estimated evaluation peak; raise (no sampling, no fallback) when it exceeds :data:`MAX_EVALUATION_PEAK_BYTES`."""
    peak = evaluation_peak_bytes(members, fold_rows, classes)
    if peak > MAX_EVALUATION_PEAK_BYTES:
        raise ValueError(
            f"ensemble evaluation would peak at about {peak} bytes ({members} members x {sum(fold_rows)} test windows "
            f"over {len(fold_rows)} folds x {classes} classes; the captured probabilities alone are "
            f"{capture_bytes(members, fold_rows, classes)} bytes), above the limit {MAX_EVALUATION_PEAK_BYTES}: at most "
            f"{max_capture_rows(members, classes)} test windows ({max_capture_bytes(members, classes)} captured bytes) fit "
            f"this member and class count; use fewer members, symbols or folds")
    return peak


def member_stem(group_stem: str, index: int) -> str:
    return f"{group_stem}{MEMBER_STEM_MARKER}{index + 1:02d}"


def member_run_id(run_id: str, index: int) -> str:
    return f"{run_id}{MEMBER_RUN_ID_MARKER}{index + 1:02d}"


# --- weights ----------------------------------------------------------------------------------


def equal_weights(count: int) -> np.ndarray:
    return np.full(count, 1.0 / count, dtype=np.float64)


def _mixture_nll(weights: np.ndarray, likelihood: np.ndarray) -> float:
    return float(-np.log(weights @ likelihood).mean())


def _kkt_violation(weights: np.ndarray, likelihood: np.ndarray) -> float:
    """Largest residual of the simplex optimality (KKT) conditions of ``min_w NLL(w)``.

    With the score ``s_m = mean_i[ p_m(y_i) / mixed_i ]`` and the common level ``lambda = sum_m w_m s_m``
    (``s_m == lambda`` for every weight that is positive at the optimum, ``s_m <= lambda`` for the others)
    the residual is ``max_m max( w_m |s_m - lambda|, s_m - lambda )``: the first term is the stationarity of the
    positive weights (weighted, so a weight that is merely decaying toward zero is not counted as active), the
    second is a weight that would still improve the likelihood by growing. It is 0 at the exact optimum.
    """
    mixed = weights @ likelihood
    scores = (likelihood / mixed[None, :]).mean(axis=1)
    level = float(weights @ scores)
    gap = scores - level
    return float(max(np.max(weights * np.abs(gap)), np.max(gap), 0.0))


def fit_simplex_weights(member_probs: np.ndarray, y: np.ndarray, *,
                        max_iterations: int = EM_MAX_ITERATIONS) -> Tuple[np.ndarray, Dict[str, object]]:
    """EM fit of the nonnegative sum-1 mixture weights; returns ``(weights, diagnostics)``.

    ``member_probs`` is ``[M, N, K]``; ``y`` the true class per row. The likelihood of a row under member
    ``m`` is the probability it gave the true class, floored at :data:`PROBABILITY_FLOOR`.

    ``diagnostics`` makes the fit auditable: ``converged`` (the L1 weight change fell below :data:`EM_TOLERANCE`),
    ``iterations``, ``final_delta`` (that last L1 change), ``initial_nll`` (equal weights), ``final_nll`` and
    ``kkt_violation`` (:func:`_kkt_violation`). Reaching ``max_iterations`` is reported as ``converged=False``, not as
    an error: the objective is convex and EM lowers it monotonically, so the weights are still an improving
    approximation, and the caller decides how to surface it (no pass/fail threshold is invented here).
    """
    probs = np.asarray(member_probs, dtype=np.float64)
    labels = np.asarray(y, dtype=np.int64)
    if probs.ndim != 3 or labels.ndim != 1 or probs.shape[1] != labels.shape[0] or probs.shape[0] < 1:
        raise ValueError("member probabilities must be [M, N, K] with one label per row")
    if not np.isfinite(probs).all() or labels.min(initial=0) < 0 or labels.max(initial=0) >= probs.shape[2]:
        raise ValueError("member probabilities must be finite and labels valid class indices")
    if probs.shape[1] < MIN_META_SAMPLES:
        raise ValueError(f"stacking needs at least {MIN_META_SAMPLES} out-of-fold rows, got {probs.shape[1]}")
    likelihood = np.maximum(probs[:, np.arange(labels.shape[0]), labels], PROBABILITY_FLOOR)  # [M, N]
    if type(max_iterations) is not int or max_iterations < 1:
        raise ValueError("max_iterations must be a positive integer")
    weights = equal_weights(probs.shape[0])
    initial_nll = _mixture_nll(weights, likelihood)
    iterations, delta, converged = 0, math.inf, False
    for iterations in range(1, max_iterations + 1):
        mixed = weights @ likelihood  # [N]
        updated = (weights[:, None] * likelihood / mixed[None, :]).mean(axis=1)
        updated = updated / updated.sum()
        delta = float(np.abs(updated - weights).sum())
        weights = updated
        if delta < EM_TOLERANCE:
            converged = True
            break
    diagnostics = {"converged": converged, "iterations": iterations, "final_delta": delta,
                   "initial_nll": initial_nll, "final_nll": _mixture_nll(weights, likelihood),
                   "kkt_violation": _kkt_violation(weights, likelihood)}
    return weights, diagnostics


def mix(weights: np.ndarray, member_probs: np.ndarray) -> np.ndarray:
    """The T06 linear mixture: ``sum_m w_m p_m`` divided by its total (mirror of ``Aggregate``)."""
    w = np.asarray(weights, dtype=np.float64)
    p = np.asarray(member_probs, dtype=np.float64)
    if p.ndim != 3 or w.shape != (p.shape[0],):
        raise ValueError("one weight per member and probabilities [M, N, K] are required")
    if (w < 0).any() or not np.isfinite(w).all() or abs(float(w.sum()) - 1.0) > SUM_TOLERANCE:
        raise ValueError("weights must be finite, nonnegative and sum to one")
    weighted = np.tensordot(w, p, axes=1)
    total = weighted.sum(axis=1, keepdims=True)
    if not np.isfinite(total).all() or (total <= 0).any():
        raise ValueError("combined probability sum is invalid")
    return weighted / total


# --- pooled rows, leakage proof, evaluation ---------------------------------------------------


def pool_fold(member_rows: Sequence[FoldRows]) -> Tuple[List[Tuple[str, int]], np.ndarray, np.ndarray]:
    """Pool one fold across symbols (sorted); verify every member scored exactly the same rows.

    Returns ``(row_keys, y, probs[M, N, K])`` where a key is ``(symbol, anchor_row)``.
    """
    if not member_rows:
        raise ValueError("no member predictions to pool")
    symbols = sorted(member_rows[0])
    keys: List[Tuple[str, int]] = []
    labels: List[np.ndarray] = []
    parts: List[List[np.ndarray]] = [[] for _ in member_rows]
    for symbol in symbols:
        anchors, y, _ = member_rows[0][symbol]
        for rows in member_rows:
            if set(rows) != set(symbols):
                raise ValueError("members scored different symbol sets")
            other_anchors, other_y, _ = rows[symbol]
            if not np.array_equal(anchors, other_anchors) or not np.array_equal(y, other_y):
                raise ValueError(f"members scored different rows for {symbol!r}")
        keys.extend((symbol, int(a)) for a in anchors)
        labels.append(np.asarray(y, dtype=np.int64))
        for m, rows in enumerate(member_rows):
            parts[m].append(np.asarray(rows[symbol][2], dtype=np.float64))
    return keys, np.concatenate(labels), np.stack([np.concatenate(p, axis=0) for p in parts], axis=0)


def rows_sha256(keys: Sequence[Tuple[str, int]]) -> str:
    digest = hashlib.sha256()
    for symbol, anchor in keys:
        digest.update(f"{symbol}\x1f{anchor}\n".encode("utf-8"))
    return digest.hexdigest()


def fold_metrics(fold: int, n_splits: int, y: np.ndarray, probs: np.ndarray, *, holdout: bool) -> Dict[str, float]:
    """The same flat row ``run_training.evaluate_folds`` emits, for a mixture."""
    report = metrics.classification_report_dict(y, probs.argmax(axis=1), probs)
    row = {
        "fold": float(fold), "n_splits": float(n_splits), "fold_n": float(report["n_samples"]),
        "fold_accuracy": float(report["accuracy"]), "fold_macro_f1": float(report["macro_f1"]),
        "fold_baseline_accuracy": float(report["majority_baseline_accuracy"]),
        "fold_is_holdout": 1.0 if holdout else 0.0,
    }
    if report["multi_logloss"] is not None and math.isfinite(report["multi_logloss"]):
        row["fold_multi_logloss"] = float(report["multi_logloss"])
    return row


def _meta_segment_fit(meta_captures: Sequence[FoldRows], release_keys: Sequence[Tuple[str, int]], release_y: np.ndarray,
                      release_probs: np.ndarray, n_splits: int, past: Sequence[int], past_fit_keys: Sequence[Tuple[str, int]],
                      past_fit: Tuple[np.ndarray, Dict[str, object]] | None
                      ) -> Tuple[np.ndarray, Dict[str, object]]:
    """Release weights fitted on the meta segment, plus the conventional past-OOF weights for comparison."""
    meta_keys, meta_y, meta_probs = pool_fold(meta_captures)
    if len(meta_keys) < MIN_META_SAMPLES:
        raise ValueError(f"the stacking meta segment needs at least {MIN_META_SAMPLES} rows, got {len(meta_keys)}")
    overlap = set(meta_keys) & set(release_keys)
    if overlap:
        raise ValueError(f"stacking meta segment rows overlap the release test rows ({len(overlap)})")
    weights, diagnostics = fit_simplex_weights(meta_probs, meta_y)
    if past_fit is None:
        past_oof: Dict[str, object] = {"status": "unavailable", "reason": "insufficient_oof",
                                       "fit_samples": len(past_fit_keys)}
        shift = None
    else:
        past_weights, past_diagnostics = past_fit
        past_oof = {"status": "fitted", "fit_folds": list(past), "fit_samples": len(past_fit_keys),
                    "fit_rows_sha256": rows_sha256(past_fit_keys), "weights": [float(w) for w in past_weights],
                    **past_diagnostics,
                    "release_metrics": fold_metrics(n_splits - 1, n_splits, release_y, mix(past_weights, release_probs),
                                                    holdout=True)}
        shift = float(np.abs(weights - past_weights).sum())
    info = {"method": WEIGHTS_SIMPLEX_NLL, "population": META_FIT_POPULATION, "fit_folds": [],
            "fit_samples": len(meta_keys), "fit_rows_sha256": rows_sha256(meta_keys), **diagnostics,
            "past_oof": past_oof, "weight_shift_l1": shift}
    return weights, info


def evaluate(method: str, n_splits: int, captures: Sequence[Dict[int, FoldRows]],
             meta_captures: Sequence[FoldRows] | None = None) -> Dict[str, object]:
    """Mixture weights, leakage proof and per-fold evaluation from every member's captured fold rows.

    ``captures[m][k]`` are member ``m``'s independent outer-fold-``k`` test rows. Bagging uses equal weights
    on every fold. Stacking evaluates fold ``k >= 1`` with weights fitted on the pooled OOF rows of folds
    ``0..k-1`` only (strictly past); the release weights come from folds ``0..n-2`` and the release fold is
    scored but never fitted. A release fit with fewer than ``MIN_META_SAMPLES`` rows raises.

    ``meta_captures[m]`` (stacking only) are member ``m``'s release-fold probabilities on the meta segment. When given, the
    release weights are fitted on those rows instead (see :func:`_meta_segment_fit`); earlier folds are unchanged, and a
    release fold with too little past history no longer raises because the past weights are then only a comparison.
    """
    if method not in (METHOD_BAGGING, METHOD_STACKING):
        raise ValueError(f"ensemble evaluation needs bagging or stacking, got {method!r}")
    if meta_captures is not None and (method != METHOD_STACKING or len(meta_captures) != len(captures)):
        raise ValueError("meta-segment rows belong to stacking and need one capture per member")
    count = len(captures)
    pooled = {k: pool_fold([capture[k] for capture in captures]) for k in range(n_splits)}
    release = n_splits - 1
    release_keys, release_y, release_probs = pooled[release]
    folds: List[Dict[str, object]] = []
    fit_info: Dict[str, object] = {"method": WEIGHTS_EQUAL, "fit_folds": [], "fit_samples": 0,
                                   "fit_rows_sha256": rows_sha256([]), "iterations": 0}
    weights_release = equal_weights(count)
    for k in range(n_splits):
        keys, y, probs = pooled[k]
        if method == METHOD_BAGGING:
            weights = equal_weights(count)
            status = "evaluated"
        else:
            past = list(range(k))
            if not past:
                folds.append({"fold": k, "status": "no_past_fold", "weights": None, "metrics": None})
                continue
            fit_keys = [key for j in past for key in pooled[j][0]]
            past_fit = None
            if len(fit_keys) >= MIN_META_SAMPLES:
                fit_y = np.concatenate([pooled[j][1] for j in past])
                fit_probs = np.concatenate([pooled[j][2] for j in past], axis=1)
                past_fit = fit_simplex_weights(fit_probs, fit_y)
            elif k != release:
                folds.append({"fold": k, "status": "insufficient_oof", "weights": None, "metrics": None})
                continue
            elif meta_captures is None:
                raise ValueError(f"stacking needs at least {MIN_META_SAMPLES} out-of-fold rows, "
                                 f"got {len(fit_keys)}")
            status = "evaluated"
            if k == release and meta_captures is not None:
                weights, fit_info = _meta_segment_fit(meta_captures, release_keys, release_y, release_probs, n_splits,
                                                      past, fit_keys, past_fit)
                weights_release = weights
            else:
                weights, diagnostics = past_fit
                if k == release:
                    overlap = set(fit_keys) & set(release_keys)
                    if overlap:
                        raise ValueError(f"stacking fit rows overlap the release test rows ({len(overlap)})")
                    weights_release = weights
                    fit_info = {"method": WEIGHTS_SIMPLEX_NLL, "fit_folds": past, "fit_samples": len(fit_keys),
                                "fit_rows_sha256": rows_sha256(fit_keys), **diagnostics}
        folds.append({"fold": k, "status": status, "weights": [float(w) for w in weights],
                      "metrics": fold_metrics(k, n_splits, y, mix(weights, probs), holdout=k == release)})
    return {
        "weights": [float(w) for w in weights_release],
        "weights_fit": dict(fit_info, release_rows_sha256=rows_sha256(release_keys),
                            release_rows=len(release_keys), row_overlap=0),
        "folds": folds,
    }


# --- manifest -----------------------------------------------------------------------------------


def build_manifest(*, method: str, run_id: str, timeframe: str, horizon: int, class_order: str,
                   members: Sequence[Dict[str, object]], evaluation: Dict[str, object],
                   bagging: Dict[str, object] | None, resource_limits: Dict[str, int],
                   weight_source: str = WEIGHT_SOURCE_PAST_OOF) -> Dict[str, object]:
    """The ``<stem>.ensemble.json`` document. ``members[i]`` already carries file / sha256 / metrics_file / weight."""
    if weight_source not in WEIGHT_SOURCES or (method != METHOD_STACKING and weight_source != WEIGHT_SOURCE_PAST_OOF):
        raise ValueError("the stacking weight source must be a known value and applies to stacking only")
    weights = [float(member["weight"]) for member in members]
    manifest = {
        "schema_version": SCHEMA_VERSION, "kind": KIND, "method": method, "run_id": run_id,
        "target_type": "classification", "timeframe": timeframe, "horizon": horizon, "class_order": class_order,
        "mixture": "linear_normalized",
        "members": list(members), "weights": weights,
        "weights_fit": evaluation["weights_fit"], "folds": evaluation["folds"],
        "bagging": bagging, "max_ensemble_members": resource_limits.get("max_ensemble_members"),
    }
    if method == METHOD_STACKING and weight_source == WEIGHT_SOURCE_META:
        # Fitted on the deployed release models' own predictions on a segment they were not trained on: nothing is transferred.
        manifest.update(weight_source=weight_source, weight_fit_population=META_FIT_POPULATION,
                        deployment_population=DEPLOYMENT_POPULATION)
    elif method == METHOD_STACKING:
        # The weights are fitted on past outer folds' out-of-fold rows (shorter train windows) but applied to the
        # release-fold models (the longest window): a possible distribution shift whose size is not measured here.
        manifest.update(weight_source=weight_source, weight_fit_population=WEIGHT_FIT_POPULATION,
                        deployment_population=DEPLOYMENT_POPULATION, weight_transfer_warning=True)
    validate_manifest(manifest)
    return manifest


def validate_manifest(manifest: Dict[str, object]) -> None:
    """Structural validation shared by the writer and its tests (the C# loader enforces the same rules)."""
    if manifest.get("schema_version") != SCHEMA_VERSION or manifest.get("kind") != KIND:
        raise ValueError("unsupported ensemble manifest schema")
    if manifest.get("method") not in (METHOD_BAGGING, METHOD_STACKING):
        raise ValueError("ensemble manifest method must be bagging or stacking")
    members = manifest.get("members")
    if not isinstance(members, list) or not MIN_MEMBERS <= len(members) <= max(
            MIN_MEMBERS, int(manifest.get("max_ensemble_members") or 0)):
        raise ValueError("ensemble manifest member count is out of range")
    files = [member.get("file") for member in members]
    if len(set(files)) != len(files) or any(
            not isinstance(f, str) or Path(f).name != f or not f.endswith(".onnx") for f in files):
        raise ValueError("ensemble member files must be distinct plain file names")
    weights = [member.get("weight") for member in members]
    if any(type(w) not in (int, float) or not math.isfinite(w) or w < 0 for w in weights):
        raise ValueError("ensemble weights must be finite and nonnegative")
    if abs(sum(weights) - 1.0) > SUM_TOLERANCE or manifest.get("weights") != [float(w) for w in weights]:
        raise ValueError("ensemble weights must sum to one and match the member rows")
    for member in members:
        digest = member.get("sha256")
        if not isinstance(digest, str) or len(digest) != 64 or any(c not in "0123456789abcdef" for c in digest):
            raise ValueError("ensemble member sha256 must be 64 lowercase hex digits")
    _validate_optional_provenance(manifest)


def _validate_optional_provenance(manifest: Dict[str, object]) -> None:
    """Optional audit keys (absent in older manifests): EM diagnostics and the stacking weight-transfer record."""
    fit = manifest.get("weights_fit")
    if isinstance(fit, dict) and "converged" in fit:
        if type(fit["converged"]) is not bool or type(fit.get("iterations")) is not int or fit["iterations"] < 1:
            raise ValueError("weights_fit.converged must be a boolean and iterations a positive integer")
        for key in ("final_delta", "initial_nll", "final_nll", "kkt_violation"):
            value = fit.get(key)
            # the NLLs are finite reals (a perfect fit rounds to a hair below zero); the others are nonnegative
            lowest = -math.inf if key.endswith("_nll") else 0.0
            if type(value) not in (int, float) or not math.isfinite(value) or value < lowest:
                raise ValueError(f"weights_fit.{key} must be a finite number{'' if lowest < 0 else ' >= 0'}")
    source = manifest.get("weight_source", WEIGHT_SOURCE_PAST_OOF)
    if source not in WEIGHT_SOURCES or ("weight_source" in manifest and manifest.get("method") != METHOD_STACKING):
        raise ValueError("the stacking weight source must be a known value and applies to stacking only")
    if source == WEIGHT_SOURCE_META:
        if ("weight_transfer_warning" in manifest or manifest.get("weight_fit_population") != META_FIT_POPULATION
                or manifest.get("deployment_population") != DEPLOYMENT_POPULATION
                or not isinstance(fit, dict) or fit.get("population") != META_FIT_POPULATION):
            raise ValueError("meta-segment weights carry no transfer warning and name the meta fit population")
        shift = fit.get("weight_shift_l1")
        if shift is not None and (type(shift) not in (int, float) or not math.isfinite(shift) or shift < 0):
            raise ValueError("weights_fit.weight_shift_l1 must be null or a finite number >= 0")
    elif "weight_transfer_warning" in manifest:
        if (manifest["weight_transfer_warning"] is not True or manifest.get("method") != METHOD_STACKING
                or manifest.get("weight_fit_population") != WEIGHT_FIT_POPULATION
                or manifest.get("deployment_population") != DEPLOYMENT_POPULATION):
            raise ValueError("a weight-transfer warning belongs to stacking and names the fit and deployment populations")


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


# --- self-verification ----------------------------------------------------------------------------


def _fake_rows(rng: np.random.Generator, anchors: np.ndarray, y: np.ndarray, sharpness: float) -> np.ndarray:
    logits = rng.normal(0.0, 1.0, (len(y), 3))
    logits[np.arange(len(y)), y] += sharpness
    e = np.exp(logits - logits.max(axis=1, keepdims=True))
    return e / e.sum(axis=1, keepdims=True)


def _run_selfcheck() -> None:
    # (1) base parsing
    assert parse_base("pytorch:LSTM") == ("pytorch", "lstm")
    assert parse_base("lightgbm") == ("lightgbm", "gbdt")
    for bad in ("pytorch", "lightgbm:x", " pytorch:lstm", "xgboost:a", "tensorflow:", ""):
        try:
            parse_base(bad)
        except ValueError:
            pass
        else:  # pragma: no cover
            raise AssertionError(f"base {bad!r} must be rejected")

    # (2) plan validation
    validate_plan(METHOD_NONE, 5, [], "regression", 8)
    validate_plan(METHOD_BAGGING, 5, [], "classification", 8)
    validate_plan(METHOD_STACKING, 5, ["pytorch:lstm", "lightgbm"], "classification", 8)
    for args in ((METHOD_BAGGING, 1, [], "classification"), (METHOD_BAGGING, 9, [], "classification"),
                 (METHOD_BAGGING, 5, ["lightgbm"], "classification"), (METHOD_BAGGING, 5, [], "regression"),
                 (METHOD_STACKING, 5, ["lightgbm"], "classification"),
                 (METHOD_STACKING, 5, ["pytorch:lstm", "pytorch:lstm"], "classification"),
                 (METHOD_NONE, 5, ["lightgbm", "pytorch:cnn"], "classification"), ("boosting", 5, [], "classification")):
        try:
            validate_plan(*args, 8)
        except ValueError:
            pass
        else:  # pragma: no cover
            raise AssertionError(f"plan {args} must be rejected")

    # (3) EM: a clearly better member earns the larger weight; weights stay on the simplex
    rng = np.random.default_rng(3)
    anchors = np.arange(400)
    y = rng.integers(0, 3, 400)
    good, poor = _fake_rows(rng, anchors, y, 2.5), _fake_rows(rng, anchors, y, 0.2)
    w, fit = fit_simplex_weights(np.stack([good, poor]), y)
    assert abs(w.sum() - 1.0) < 1e-12 and (w >= 0).all() and w[0] > w[1]
    assert 1 <= fit["iterations"] <= EM_MAX_ITERATIONS
    nll = lambda weights: -np.log(np.maximum(  # noqa: E731
        weights @ np.stack([good, poor])[:, np.arange(400), y], PROBABILITY_FLOOR)).mean()
    assert nll(w) <= nll(equal_weights(2)) + 1e-12 and nll(w) <= nll(np.array([1.0, 0.0])) + 1e-9
    # diagnostics: the NLLs are the objective at equal / fitted weights, EM lowered it, and a converged fit is a KKT point
    assert set(fit) == {"converged", "iterations", "final_delta", "initial_nll", "final_nll", "kkt_violation"}
    assert abs(fit["initial_nll"] - nll(equal_weights(2))) < 1e-12 and abs(fit["final_nll"] - nll(w)) < 1e-12
    assert fit["final_nll"] <= fit["initial_nll"] and fit["converged"] is True
    assert 0.0 <= fit["final_delta"] < EM_TOLERANCE and fit["kkt_violation"] < 1e-6, fit
    # (a) an iteration budget that is too small is reported as not converged -- neither an error nor a fallback
    cut_w, cut = fit_simplex_weights(np.stack([good, poor]), y, max_iterations=2)
    assert cut["converged"] is False and cut["iterations"] == 2 and cut["final_delta"] >= EM_TOLERANCE
    assert abs(cut_w.sum() - 1.0) < 1e-12 and cut["final_nll"] <= cut["initial_nll"] + 1e-15
    assert cut["kkt_violation"] > fit["kkt_violation"], "an unconverged fit is visibly farther from optimal"
    for bad_budget in (0, -1, 1.5, True):
        try:
            fit_simplex_weights(np.stack([good, poor]), y, max_iterations=bad_budget)
        except ValueError:
            pass
        else:  # pragma: no cover
            raise AssertionError("an invalid iteration budget must be rejected")
    # (b) identical members -> equal weights, optimum reached at once (KKT residual 0); a perfect member takes everything
    same, same_fit = fit_simplex_weights(np.stack([good, good]), y)
    assert np.allclose(same, [0.5, 0.5]) and same_fit["converged"] and same_fit["kkt_violation"] < 1e-12, same_fit
    perfect = np.eye(3)[y]
    taken, taken_fit = fit_simplex_weights(np.stack([perfect, poor]), y)
    assert taken[0] > 0.999 and taken_fit["kkt_violation"] < 1e-6, taken_fit
    try:
        fit_simplex_weights(np.stack([good[:50], poor[:50]]), y[:50])
    except ValueError:
        pass
    else:  # pragma: no cover
        raise AssertionError("too few OOF rows must be rejected")

    # (4) mixture: hand value + identity for one member; invalid weights rejected
    a = np.array([[0.6, 0.3, 0.1]])
    b = np.array([[0.2, 0.2, 0.6]])
    m = mix(np.array([0.25, 0.75]), np.stack([a, b]))
    assert np.allclose(m, [[0.30, 0.225, 0.475]], atol=1e-12)
    assert np.allclose(mix(np.array([1.0]), a[None]), a)
    assert abs(m.sum() - 1.0) < 1e-12
    for bad in (np.array([0.5, 0.6]), np.array([-0.1, 1.1]), np.array([0.5])):
        try:
            mix(bad, np.stack([a, b]))
        except ValueError:
            pass
        else:  # pragma: no cover
            raise AssertionError("invalid weights must be rejected")

    # (5) pooled rows must align between members
    rows_a = {"S1": (np.array([5, 6]), np.array([0, 1]), np.array([[0.5, 0.3, 0.2], [0.1, 0.8, 0.1]]))}
    rows_b = {"S1": (np.array([5, 6]), np.array([0, 1]), np.array([[0.4, 0.4, 0.2], [0.2, 0.6, 0.2]]))}
    keys, labels, probs = pool_fold([rows_a, rows_b])
    assert keys == [("S1", 5), ("S1", 6)] and probs.shape == (2, 2, 3) and labels.tolist() == [0, 1]
    rows_c = {"S1": (np.array([5, 7]), np.array([0, 1]), rows_b["S1"][2])}
    try:
        pool_fold([rows_a, rows_c])
    except ValueError:
        pass
    else:  # pragma: no cover
        raise AssertionError("misaligned members must be rejected")

    # (6) evaluation: 4 outer folds x 2 members x 2 symbols
    n_splits = 4
    captures: List[Dict[int, FoldRows]] = [{}, {}]
    for fold in range(n_splits):
        for m_index, sharp in enumerate((2.0, 0.3)):
            rows: FoldRows = {}
            for sym_index, symbol in enumerate(("AAA", "BBB")):
                size = 70
                anchors = np.arange(size) + 1000 * fold + 100 * sym_index
                yy = np.random.default_rng(100 * fold + sym_index).integers(0, 3, size)
                rows[symbol] = (anchors, yy, _fake_rows(np.random.default_rng(7 * fold + m_index + sym_index),
                                                         anchors, yy, sharp))
            captures[m_index][fold] = rows
    bag = evaluate(METHOD_BAGGING, n_splits, captures)
    assert bag["weights"] == [0.5, 0.5] and all(f["status"] == "evaluated" for f in bag["folds"])
    assert bag["weights_fit"]["method"] == WEIGHTS_EQUAL and bag["weights_fit"]["row_overlap"] == 0
    stack = evaluate(METHOD_STACKING, n_splits, captures)
    assert stack["folds"][0]["status"] == "no_past_fold" and stack["folds"][1]["status"] == "evaluated"
    assert stack["weights_fit"]["fit_folds"] == [0, 1, 2] and stack["weights_fit"]["fit_samples"] == 3 * 140
    # the release fit records its EM diagnostics (bagging has no fit); they are plain JSON numbers
    for key in ("converged", "iterations", "final_delta", "initial_nll", "final_nll", "kkt_violation"):
        assert key in stack["weights_fit"], key
    assert "converged" not in bag["weights_fit"] and bag["weights_fit"]["iterations"] == 0
    assert stack["weights"][0] > stack["weights"][1] and abs(sum(stack["weights"]) - 1.0) < 1e-12
    assert stack["folds"][-1]["metrics"]["fold_is_holdout"] == 1.0 and stack["folds"][0]["metrics"] is None
    # fold-k weights use strictly earlier folds: fold 1 is fitted on fold 0 only (140 rows)
    assert stack["folds"][1]["weights"] is not None
    # the release fold keys never appear in the fit set
    release_keys = pool_fold([c[n_splits - 1] for c in captures])[0]
    fit_keys = [key for j in range(n_splits - 1) for key in pool_fold([c[j] for c in captures])[0]]
    assert not set(release_keys) & set(fit_keys)
    assert stack["weights_fit"]["fit_rows_sha256"] == rows_sha256(fit_keys)
    assert stack["weights_fit"]["release_rows_sha256"] == rows_sha256(release_keys)
    # too little OOF history for the release fit fails explicitly
    tiny: List[Dict[int, FoldRows]] = [
        {k: {s: (a[:10], y[:10], p[:10]) for s, (a, y, p) in rows.items()} for k, rows in c.items()}
        for c in captures]
    try:
        evaluate(METHOD_STACKING, n_splits, tiny)
    except ValueError:
        pass
    else:  # pragma: no cover
        raise AssertionError("insufficient OOF rows must fail the stacking release")

    # (7) manifest
    members = [{"index": i, "file": f"g_m0{i + 1}.onnx", "sha256": "ab" * 32, "metrics_file": f"g_m0{i + 1}.onnx.metrics.json",
                "weight": 0.5, "framework": "pytorch", "architecture": "lstm"} for i in range(2)]
    manifest = build_manifest(method=METHOD_BAGGING, run_id="r", timeframe="daily", horizon=5,
                              class_order="Up,Down,Neutral", members=members, evaluation=bag,
                              bagging={"members": 2}, resource_limits={"max_ensemble_members": 8})
    json.dumps(manifest, allow_nan=False)
    broken = dict(manifest, members=[dict(members[0], weight=0.7), members[1]])
    for case in (broken, dict(manifest, kind="x"), dict(manifest, method="none"),
                 dict(manifest, members=[members[0], dict(members[1], file="../x.onnx")]),
                 dict(manifest, members=[members[0], dict(members[1], sha256="XY")])):
        try:
            validate_manifest(case)
        except ValueError:
            pass
        else:  # pragma: no cover
            raise AssertionError("invalid manifest must be rejected")
    # stacking manifests carry the weight-transfer record and the EM diagnostics; bagging has no fitted weights
    assert not {"weight_fit_population", "deployment_population", "weight_transfer_warning"} & set(manifest)
    stack_manifest = build_manifest(method=METHOD_STACKING, run_id="r", timeframe="daily", horizon=5,
                                    class_order="Up,Down,Neutral", members=members, evaluation=stack,
                                    bagging=None, resource_limits={"max_ensemble_members": 8})
    json.dumps(stack_manifest, allow_nan=False)
    assert stack_manifest["weight_fit_population"] == "previous_outer_oof"
    assert stack_manifest["deployment_population"] == "release_fold_models"
    assert stack_manifest["weight_transfer_warning"] is True
    assert stack_manifest["weights_fit"]["converged"] is True
    # an older manifest without any of the audit keys is still valid
    legacy = {k: v for k, v in stack_manifest.items()
              if k not in ("weight_fit_population", "deployment_population", "weight_transfer_warning")}
    legacy["weights_fit"] = {k: v for k, v in stack_manifest["weights_fit"].items()
                             if k not in ("converged", "final_delta", "initial_nll", "final_nll", "kkt_violation")}
    validate_manifest(legacy)
    for corrupt in (dict(stack_manifest, weight_transfer_warning=False),
                    dict(stack_manifest, weight_fit_population="other"),
                    dict(stack_manifest, method=METHOD_BAGGING),
                    dict(stack_manifest, weights_fit=dict(stack_manifest["weights_fit"], converged="yes")),
                    dict(stack_manifest, weights_fit=dict(stack_manifest["weights_fit"], kkt_violation=float("nan"))),
                    dict(stack_manifest, weights_fit=dict(stack_manifest["weights_fit"], final_delta=-1.0))):
        try:
            validate_manifest(corrupt)
        except ValueError:
            pass
        else:  # pragma: no cover
            raise AssertionError("an inconsistent audit record must be rejected")
    # (8) capture size estimate, its code limit, and the lossless float32 capture
    assert (member_stem("grp", 0), member_stem("grp", 9), member_run_id("run", 1)) == ("grp_m01", "grp_m10", "run-m02")
    assert LIGHTGBM_ARCHITECTURE == "gbdt" and ENSEMBLE_FILE_SUFFIX == ".ensemble.json"
    assert CAPTURE_ITEM_BYTES == 4 and MAX_EVALUATION_PEAK_BYTES == 4 * 1024 ** 3
    assert capture_bytes(5, [100, 200], 3) == 5 * 300 * 3 * 4 and capture_bytes(2, [], 3) == 0
    # the limit is on the PEAK: the allowed capture is derived from it (a float32 capture grows into at least four float64 copies)
    assert evaluation_peak_bytes(2, [100, 200], 3) == 300 * (EVALUATION_PEAK_FACTOR * 2 * 3 * EVALUATION_ITEM_BYTES
                                                             + EVALUATION_ROW_OVERHEAD_BYTES)
    assert max_capture_bytes(2, 3) < max_capture_bytes(8, 3) < MAX_EVALUATION_PEAK_BYTES // (EVALUATION_PEAK_FACTOR * 2)
    for shape_members in (2, 5, 8):
        at_limit = max_capture_rows(shape_members, 3)  # most rows this shape may hold
        assert check_capture_budget(shape_members, [at_limit], 3) == evaluation_peak_bytes(shape_members, [at_limit], 3) \
            <= MAX_EVALUATION_PEAK_BYTES < evaluation_peak_bytes(shape_members, [at_limit + 1], 3)
        assert check_capture_budget(shape_members, [at_limit - 5, 5], 3) == check_capture_budget(shape_members, [at_limit], 3)
        assert max_capture_bytes(shape_members, 3) == capture_bytes(shape_members, [at_limit], 3)
        try:
            check_capture_budget(shape_members, [at_limit + 1], 3)
        except ValueError as error:
            assert "peak" in str(error) and str(MAX_EVALUATION_PEAK_BYTES) in str(error) and "above the limit" in str(error)
            assert str(max_capture_bytes(shape_members, 3)) in str(error)
        else:  # pragma: no cover
            raise AssertionError("an estimate above the limit must fail explicitly")
    for bad_args in ((0, [10], 3), (2, [10], 0), (2, [-1], 3), (2, [1.5], 3)):
        for function in (capture_bytes, evaluation_peak_bytes):
            try:
                function(*bad_args)
            except ValueError:
                pass
            else:  # pragma: no cover
                raise AssertionError(f"capture size arguments {bad_args} must be rejected")
    for bad_shape in ((0, 3), (2, 0)):
        for function in (max_capture_rows, max_capture_bytes):
            try:
                function(*bad_shape)
            except ValueError:
                pass
            else:  # pragma: no cover
                raise AssertionError(f"shape {bad_shape} must be rejected")
    # the estimate is an UPPER bound of what evaluate really peaks at (tracemalloc: captures + working copies), and not a
    # wildly pessimistic one; checked for bagging and both stacking sources, a few members, balanced and dominated folds
    def measured_peak(method, members, fold_sizes, meta_size=0):
        symbols = ("AAA", "BBB", "CCC", "DDD", "EEE")
        gen = np.random.default_rng(11)
        caps: List[Dict[int, FoldRows]] = [{} for _ in range(members)]
        metas: List[FoldRows] = [{} for _ in range(members)]
        offset = 0
        for fold, size in enumerate(fold_sizes):
            per = size // len(symbols)
            labels = {s: gen.integers(0, 3, per) for s in symbols}
            for m_index in range(members):
                caps[m_index][fold] = {s: (np.arange(per) + offset, labels[s],
                                           _fake_rows(gen, np.arange(per), labels[s], 1.0).astype(CAPTURE_DTYPE))
                                       for s in symbols}
            offset += per + 10
        if meta_size:
            per = meta_size // len(symbols)
            labels = {s: gen.integers(0, 3, per) for s in symbols}
            for m_index in range(members):
                metas[m_index] = {s: (np.arange(per) + offset, labels[s],
                                      _fake_rows(gen, np.arange(per), labels[s], 1.0).astype(CAPTURE_DTYPE)) for s in symbols}
        held = sum(p.nbytes for cap in caps for rows in cap.values() for _, _, p in rows.values())
        held += sum(p.nbytes for rows in metas for _, _, p in rows.values())
        tracemalloc.start()
        try:
            evaluate(method, len(fold_sizes), caps, metas if meta_size else None)
            peak = tracemalloc.get_traced_memory()[1]
        finally:
            tracemalloc.stop()
        rows = sum(fold_sizes) + meta_size
        return peak + held, evaluation_peak_bytes(members, [rows], 3)
    for shape in ((METHOD_BAGGING, 2, [3000] * 4, 0), (METHOD_BAGGING, 6, [1000, 1000, 1000, 9000], 0),
                  (METHOD_STACKING, 2, [3000] * 4, 0), (METHOD_STACKING, 5, [3000] * 4, 0),
                  (METHOD_STACKING, 6, [9000, 1000, 1000, 1000], 0), (METHOD_STACKING, 6, [1000, 1000, 1000, 9000], 0),
                  (METHOD_STACKING, 4, [3000] * 3, 3000), (METHOD_STACKING, 4, [1000] * 3, 9000)):
        really, estimated = measured_peak(*shape)
        assert really <= estimated <= 3 * really, (shape, really, estimated)
    # ONNX output is float32: capturing it as float32 changes nothing the mixture or the EM weights can see
    wide: List[Dict[int, FoldRows]] = [
        {k: {s: (a, y, p.astype(np.float32).astype(np.float64)) for s, (a, y, p) in rows.items()}
         for k, rows in capture.items()} for capture in captures]
    narrow: List[Dict[int, FoldRows]] = [
        {k: {s: (a, y, p.astype(CAPTURE_DTYPE)) for s, (a, y, p) in rows.items()} for k, rows in capture.items()}
        for capture in wide]
    assert all(p.dtype == np.float32 for capture in narrow for rows in capture.values() for _, _, p in rows.values())
    for k in range(n_splits):
        assert np.array_equal(pool_fold([capture[k] for capture in wide])[2], pool_fold([capture[k] for capture in narrow])[2])
    wide_eval, narrow_eval = evaluate(METHOD_STACKING, n_splits, wide), evaluate(METHOD_STACKING, n_splits, narrow)
    assert np.allclose(wide_eval["weights"], narrow_eval["weights"], atol=1e-9, rtol=0)
    assert wide_eval["weights_fit"]["fit_rows_sha256"] == narrow_eval["weights_fit"]["fit_rows_sha256"]
    # (9) job-level bagging tunables: defaults, parsing, `-`/`_` spelling, rejection, and the remaining hyperparameters
    assert split_bagging_options({}) == (ds.BAGGING_FRACTION, None, {})
    assert split_bagging_options({"bag_fraction": "0.5", "bag_block_length": "20", "epochs": "3"}) == (0.5, 20, {"epochs": "3"})
    assert split_bagging_options({"--bag-fraction": "1", "bag-block-length": " 7 "}) == (1.0, 7, {})
    assert split_bagging_options({"lr": "0.1"})[2] == {"lr": "0.1"}
    for bad_options in ({"bag_fraction": "0"}, {"bag_fraction": "1.01"}, {"bag_fraction": "-0.5"}, {"bag_fraction": "nan"},
                        {"bag_fraction": "inf"}, {"bag_fraction": "half"}, {"bag_block_length": "0"},
                        {"bag_block_length": "-4"}, {"bag_block_length": "2.5"}, {"bag_block_length": ""},
                        {"bag_block_length": "٣"}, {"bag_fraction": "0.5", "bag-fraction": "0.6"}):
        try:
            split_bagging_options(bad_options)
        except ValueError:
            pass
        else:  # pragma: no cover
            raise AssertionError(f"bagging options {bad_options} must be rejected")
    # (10) stacking weights fitted on a release-fold meta segment (D2). The past folds favour member 0, the release fold
    # and the meta segment favour member 1: the meta weights follow the deployed members and do better on the release test.
    def member_captures(release_sharpness):
        caps: List[Dict[int, FoldRows]] = [{}, {}]
        for fold in range(n_splits):
            for m_index in range(2):
                sharp = release_sharpness[m_index] if fold == n_splits - 1 else (2.0, 0.3)[m_index]
                caps[m_index][fold] = {}
                for si, symbol in enumerate(("AAA", "BBB")):
                    yy = np.random.default_rng(100 * fold + si).integers(0, 3, 70)
                    caps[m_index][fold][symbol] = (
                        np.arange(70) + 1000 * fold + 100 * si, yy,
                        _fake_rows(np.random.default_rng(7 * fold + m_index + si), np.arange(70), yy, sharp))
        return caps

    def meta_rows(release_sharpness, rows_per_symbol=70, anchor_base=90_000):
        out: List[FoldRows] = []
        for m_index in range(2):
            rows: FoldRows = {}
            for si, symbol in enumerate(("AAA", "BBB")):
                yy = np.random.default_rng(5000 + si).integers(0, 3, rows_per_symbol)
                rows[symbol] = (np.arange(rows_per_symbol) + anchor_base + 1000 * si, yy,
                                _fake_rows(np.random.default_rng(6000 + 10 * m_index + si), np.arange(rows_per_symbol), yy,
                                           release_sharpness[m_index]))
            out.append(rows)
        return out

    flipped = (0.3, 2.0)
    flip_captures, flip_meta = member_captures(flipped), meta_rows(flipped)
    conventional = evaluate(METHOD_STACKING, n_splits, flip_captures)
    meta = evaluate(METHOD_STACKING, n_splits, flip_captures, flip_meta)
    assert conventional["weights"][0] > conventional["weights"][1]  # past folds rank member 0 first
    assert meta["weights"][1] > meta["weights"][0] and abs(sum(meta["weights"]) - 1.0) < 1e-12
    fit_meta = meta["weights_fit"]
    assert fit_meta["population"] == META_FIT_POPULATION and fit_meta["fit_folds"] == [] and fit_meta["fit_samples"] == 140
    flat_meta = [(sym, int(a)) for sym in ("AAA", "BBB") for a in flip_meta[0][sym][0]]
    assert fit_meta["fit_rows_sha256"] == rows_sha256(flat_meta) and fit_meta["row_overlap"] == 0
    assert fit_meta["release_rows_sha256"] == conventional["weights_fit"]["release_rows_sha256"]
    past_oof = fit_meta["past_oof"]
    assert past_oof["status"] == "fitted" and past_oof["fit_folds"] == [0, 1, 2]
    assert np.allclose(past_oof["weights"], conventional["weights"], atol=1e-15, rtol=0)  # the same fit, kept as a comparison
    shift = float(np.abs(np.array(meta["weights"]) - np.array(conventional["weights"])).sum())
    assert abs(fit_meta["weight_shift_l1"] - shift) < 1e-12 and shift > 0.5
    # the meta weights do better on the release test than the conventional ones, and that score is the plain mixture score
    assert meta["folds"][-1]["metrics"]["fold_multi_logloss"] < past_oof["release_metrics"]["fold_multi_logloss"]
    _unused_keys, release_y_, release_p_ = pool_fold([c[n_splits - 1] for c in flip_captures])
    assert meta["folds"][-1]["metrics"] == fold_metrics(n_splits - 1, n_splits, release_y_, mix(np.array(meta["weights"]), release_p_),
                                                        holdout=True)
    # earlier folds are untouched by the meta segment
    assert [f["weights"] for f in meta["folds"][:-1]] == [f["weights"] for f in conventional["folds"][:-1]]
    # rejection: overlap with the release test rows, too few meta rows, bagging, wrong member count
    leaking = [{sym: (flip_captures[m][n_splits - 1][sym][0], flip_captures[m][n_splits - 1][sym][1],
                      flip_meta[m][sym][2]) for sym in ("AAA", "BBB")} for m in range(2)]
    for bad_meta in (leaking, meta_rows(flipped, rows_per_symbol=40)):
        try:
            evaluate(METHOD_STACKING, n_splits, flip_captures, bad_meta)
        except ValueError:
            pass
        else:  # pragma: no cover
            raise AssertionError("an overlapping or too small meta segment must be rejected")
    for bad_call in ((METHOD_BAGGING, n_splits, flip_captures, flip_meta), (METHOD_STACKING, n_splits, flip_captures, flip_meta[:1])):
        try:
            evaluate(*bad_call)
        except ValueError:
            pass
        else:  # pragma: no cover
            raise AssertionError("meta rows need stacking and one capture per member")
    # too little past history: the conventional release fit is impossible, but the meta fit does not need it
    tiny_past = [{k: (rows if k == n_splits - 1 else {s: (a[:10], y[:10], p[:10]) for s, (a, y, p) in rows.items()})
                  for k, rows in c.items()} for c in flip_captures]
    thin = evaluate(METHOD_STACKING, n_splits, tiny_past, flip_meta)["weights_fit"]
    assert thin["past_oof"]["status"] == "unavailable" and thin["weight_shift_l1"] is None and thin["fit_samples"] == 140
    # manifest: meta weights carry the meta population and no weight-transfer warning
    meta_manifest = build_manifest(method=METHOD_STACKING, run_id="r", timeframe="daily", horizon=5,
                                   class_order="Up,Down,Neutral",
                                   members=[dict(m, weight=w) for m, w in zip(members, meta["weights"])],
                                   evaluation=meta, bagging=None, resource_limits={"max_ensemble_members": 8},
                                   weight_source=WEIGHT_SOURCE_META)
    json.dumps(meta_manifest, allow_nan=False)
    assert meta_manifest["weight_source"] == WEIGHT_SOURCE_META and "weight_transfer_warning" not in meta_manifest
    assert meta_manifest["weight_fit_population"] == META_FIT_POPULATION
    assert stack_manifest["weight_source"] == WEIGHT_SOURCE_PAST_OOF  # the conventional manifest records its source too
    for corrupt in (dict(meta_manifest, weight_transfer_warning=True),
                    dict(meta_manifest, weight_fit_population=WEIGHT_FIT_POPULATION),
                    dict(meta_manifest, weights_fit=dict(meta_manifest["weights_fit"], population=WEIGHT_FIT_POPULATION)),
                    dict(meta_manifest, weight_source="elsewhere"),
                    dict(stack_manifest, weight_source=WEIGHT_SOURCE_META),
                    dict(manifest, weight_source=WEIGHT_SOURCE_PAST_OOF),
                    dict(meta_manifest, weights_fit=dict(meta_manifest["weights_fit"], weight_shift_l1=-0.1))):
        try:
            validate_manifest(corrupt)
        except ValueError:
            pass
        else:  # pragma: no cover
            raise AssertionError("an inconsistent weight-source record must be rejected")
    try:
        build_manifest(method=METHOD_BAGGING, run_id="r", timeframe="daily", horizon=5, class_order="Up,Down,Neutral",
                       members=members, evaluation=bag, bagging={"members": 2}, resource_limits={"max_ensemble_members": 8},
                       weight_source=WEIGHT_SOURCE_META)
    except ValueError:
        pass
    else:  # pragma: no cover
        raise AssertionError("bagging has no weight source")
    print("ensemble.py selfcheck: OK (10 groups passed)")


def _parse_args(argv: List[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="T13 ensemble helpers.")
    parser.add_argument("--selfcheck", action="store_true", help="Run the assertions and exit.")
    return parser.parse_args(argv)


if __name__ == "__main__":
    if _parse_args(sys.argv[1:]).selfcheck:
        _run_selfcheck()
