"""T12 post-hoc temperature calibration of the exported classification ONNX.

The ONNX output stays the model's own softmax probability (``[batch, 3]``, class order Up, Down,
Neutral). Calibration is a generation-bound sidecar ``<model>.onnx.calibration.json`` applied by the
C# runtime after the softmax and before confidence/entropy are computed.

Definition (approved D12: 12-4=A, 12-5=A, 12-6=A, 12-8=B)::

    s_j = log(max(p_j, EPSILON))            # score from the deployed probability
    q   = softmax(s / T)                    # T > 0 : one parameter for all classes
    T   = argmin NLL(q; calibration segment)

For ``T > 0`` the transform is monotone in every ``p_j``, so the arg-max (the predicted class) is
unchanged. The fit population is a dedicated time-ordered tail of the outer train side
(:func:`dataset.independent_fold_data` with ``calibration=True``): it is disjoint from the model's
fit and early-stopping samples and from the outer test, and is never the out-of-sample tail.

A temperature whose optimum lies on the hard band boundary (for example perfectly separated
calibration data) is not an informative calibration: the sidecar keeps its normal form but declares
``status="temperature_at_boundary"`` and the runtime does not apply it (the ONNX bytes, and with them
the independent evaluation evidence, stay untouched).

Numeric classification: EPSILON / ECE_BINS / the search constants are constants of the algorithm;
the temperature bands and the minimum sample count are safety bounds mirrored from the C#
``ModelCalibrationLimits`` (checked by ``Tests/ModelCalibrationParity.json``).

Run ``python calibration.py --selfcheck`` to verify the formulas.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import sys
import tempfile
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import checkpoints  # noqa: E402
import dataset as ds  # noqa: E402
import fixed_scaler  # noqa: E402
import model_analysis  # noqa: E402

SCHEMA_VERSION = 1
REFERENCE_KEY = "com.stockanalyzer.calibration.ref"
METHOD = "temperature"
SCORE = "log_probability"
STATUS_AVAILABLE = "available"
STATUS_BOUNDARY = "temperature_at_boundary"
NLL_DEFINITION = "probability_clamped_epsilon"
EVALUATION_POPULATION = "release_outer_test"  # evaluation only: never used to redesign the calibration
EPSILON = 1e-15  # = metrics.LOGLOSS_EPS; mirror of C# ModelCalibrationContract.Epsilon
ECE_BINS = 10  # equal-width ECE bins, equal-mass ECE bins and reliability bins share this algorithm constant
SUM_TOLERANCE = 1e-4  # mirror of C# IMLDataProcessor.SoftmaxSumTolerance
SEARCH_TOLERANCE = 1e-12
SEARCH_MAX_ITERATIONS = 200
BOUNDARY_MARGIN_RATIO = 1e-6
GOLDEN_RATIO = (math.sqrt(5.0) - 1.0) / 2.0

# Safety bounds (mirror of C# ModelCalibrationLimits).
TEMPERATURE_HARD_MIN = 0.01
TEMPERATURE_HARD_MAX = 100.0
TEMPERATURE_STANDARD_MIN = 0.25
TEMPERATURE_STANDARD_MAX = 4.0
MIN_SAMPLES = 100


def log_scores(probabilities: np.ndarray) -> np.ndarray:
    p = np.asarray(probabilities, dtype=np.float64)
    return np.log(np.maximum(p, EPSILON))


def calibrate(probabilities: np.ndarray, temperature: float) -> np.ndarray:
    """``softmax(log(max(p, EPSILON)) / T)`` row-wise, in float64."""
    if not math.isfinite(temperature) or temperature <= 0:
        raise ValueError("temperature must be finite and positive")
    s = log_scores(probabilities) / temperature
    s = s - s.max(axis=1, keepdims=True)
    e = np.exp(s)
    return e / e.sum(axis=1, keepdims=True)


def _validate(probabilities: np.ndarray, labels: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    p = np.asarray(probabilities, dtype=np.float64)
    y = np.asarray(labels).reshape(-1)
    if p.ndim != 2 or p.shape[0] != y.shape[0] or p.shape[0] == 0 or not np.isfinite(p).all():
        raise ValueError("calibration needs finite probabilities [N,K] with N matching the labels, N>0")
    if (p < 0).any() or (p > 1).any():
        raise ValueError("calibration probabilities must lie in [0,1]")
    if (np.abs(p.sum(axis=1) - 1.0) > SUM_TOLERANCE).any():
        raise ValueError(f"calibration probability rows must sum to one within {SUM_TOLERANCE}")
    if y.min() < 0 or y.max() >= p.shape[1]:
        raise ValueError("calibration labels are outside the class range")
    return p, y.astype(np.int64)


def nll(probabilities: np.ndarray, labels: np.ndarray, temperature: float = 1.0) -> float:
    p, y = _validate(probabilities, labels)
    s = log_scores(p) / temperature
    shift = s.max(axis=1, keepdims=True)
    log_norm = np.log(np.exp(s - shift).sum(axis=1)) + shift[:, 0]
    return float(np.mean(log_norm - s[np.arange(len(y)), y]))


def brier(probabilities: np.ndarray, labels: np.ndarray) -> float:
    p, y = _validate(probabilities, labels)
    onehot = np.zeros_like(p)
    onehot[np.arange(len(y)), y] = 1.0
    return float(np.mean(np.sum((p - onehot) ** 2, axis=1)))


def ece(probabilities: np.ndarray, labels: np.ndarray) -> float:
    """Top-label expected calibration error over ECE_BINS equal-width bins of [0,1]."""
    p, y = _validate(probabilities, labels)
    confidence = p.max(axis=1)
    correct = (p.argmax(axis=1) == y).astype(np.float64)
    bins = np.minimum((confidence * ECE_BINS).astype(np.int64), ECE_BINS - 1)
    total = 0.0
    for b in range(ECE_BINS):
        mask = bins == b
        if mask.any():
            total += mask.mean() * abs(correct[mask].mean() - confidence[mask].mean())
    return float(total)


def ece_equal_mass(probabilities: np.ndarray, labels: np.ndarray) -> float:
    """Top-label ECE over ECE_BINS equal-count bins of the confidence order (stable sort).

    Bin sizes follow ``np.array_split``: N // ECE_BINS each and the first N % ECE_BINS bins one more.
    Samples with equal confidence are ordered by their position, so they may straddle a bin edge."""
    p, y = _validate(probabilities, labels)
    confidence = p.max(axis=1)
    correct = (p.argmax(axis=1) == y).astype(np.float64)
    order = np.argsort(confidence, kind="stable")
    total = 0.0
    for chunk in np.array_split(order, ECE_BINS):
        if len(chunk):
            total += len(chunk) / len(y) * abs(correct[chunk].mean() - confidence[chunk].mean())
    return float(total)


def reliability_bins(probabilities: np.ndarray, labels: np.ndarray) -> list[dict]:
    """Per equal-width confidence bin: count, mean confidence and accuracy (null when the bin is empty)."""
    p, y = _validate(probabilities, labels)
    confidence = p.max(axis=1)
    correct = (p.argmax(axis=1) == y).astype(np.float64)
    bins = np.minimum((confidence * ECE_BINS).astype(np.int64), ECE_BINS - 1)
    rows = []
    for b in range(ECE_BINS):
        mask = bins == b
        count = int(mask.sum())
        rows.append(dict(count=count,
                         mean_confidence=float(confidence[mask].mean()) if count else None,
                         accuracy=float(correct[mask].mean()) if count else None))
    return rows


def fit_temperature(probabilities: np.ndarray, labels: np.ndarray) -> tuple[float, bool]:
    """Minimise the NLL over beta = 1/T by golden-section search (the objective is convex in beta).

    Returns ``(T, at_boundary)``; the optimum is searched inside the hard band, so T always lies in it.
    ``at_boundary`` is true when the optimum sits on the band edge (no interior optimum exists)."""
    p, y = _validate(probabilities, labels)
    s = log_scores(p)
    s_true = s[np.arange(len(y)), y]

    def objective(beta: float) -> float:
        z = beta * s
        shift = z.max(axis=1, keepdims=True)
        return float(np.mean(np.log(np.exp(z - shift).sum(axis=1)) + shift[:, 0] - beta * s_true))

    lo, hi = 1.0 / TEMPERATURE_HARD_MAX, 1.0 / TEMPERATURE_HARD_MIN
    a, b = lo, hi
    c = b - GOLDEN_RATIO * (b - a)
    d = a + GOLDEN_RATIO * (b - a)
    fc, fd = objective(c), objective(d)
    for _ in range(SEARCH_MAX_ITERATIONS):
        if b - a <= SEARCH_TOLERANCE:
            break
        if fc < fd:
            b, d, fd = d, c, fc
            c = b - GOLDEN_RATIO * (b - a)
            fc = objective(c)
        else:
            a, c, fc = c, d, fd
            d = a + GOLDEN_RATIO * (b - a)
            fd = objective(d)
    beta = (a + b) / 2.0
    at_boundary = beta - lo <= lo * BOUNDARY_MARGIN_RATIO or hi - beta <= hi * BOUNDARY_MARGIN_RATIO
    return 1.0 / beta, bool(at_boundary)


def metrics_block(probabilities: np.ndarray, labels: np.ndarray, temperature: float) -> dict:
    p, y = _validate(probabilities, labels)
    q = calibrate(p, temperature)
    return dict(
        sample_count=int(len(y)),
        before=dict(nll=nll(p, y), brier=brier(p, y), ece=ece(p, y), ece_equal_mass=ece_equal_mass(p, y)),
        after=dict(nll=nll(p, y, temperature), brier=brier(q, y), ece=ece(q, y), ece_equal_mass=ece_equal_mass(q, y)),
        reliability=dict(before=reliability_bins(p, y), after=reliability_bins(q, y)),
    )


def plan(cfg, train_symbols, train_dates) -> dict:
    """Preflight of the release fold: can a calibration segment of at least MIN_SAMPLES be carved?"""
    out: dict = {}
    try:
        ds.independent_fold_data(
            train_symbols, train_dates, feature_mode=cfg.feature_mode, window=cfg.window_size,
            horizon=cfg.horizon, threshold=ds.DEFAULT_THRESHOLD, n_splits=cfg.n_splits, gap=cfg.gap,
            fold_index=cfg.n_splits - 1, feature_spec=cfg.feature_spec, target_type=cfg.target_type,
            indicator_export_paths=cfg.indicator_channel_export_paths, inner=True,
            fixed_zscore=cfg.fixed_zscore, lags=cfg.lags, calibration=True, calibration_out=out)
    except ValueError:
        return dict(status="insufficient_samples", sample_count=0, minimum_samples=MIN_SAMPLES)
    count = sum(len(v) for v in out.values())
    return dict(status="available" if count >= MIN_SAMPLES else "insufficient_samples",
                sample_count=count, minimum_samples=MIN_SAMPLES)


def _pool(selected: dict, segment: dict, symbols: list[str], predict):
    xs, ys, periods_idx = [], [], []
    for sym in symbols:
        x, y, anchors, _fit, _validation = selected[sym]
        index = segment[sym]
        xs.append(x[index])
        ys.append(y[index])
        periods_idx.append((sym, anchors[index]))
    x_all = np.concatenate(xs)
    y_all = np.concatenate(ys).astype(np.int64)
    return np.asarray(predict(x_all), dtype=np.float64), y_all, periods_idx


def build(cfg, model_path: Path, train_symbols, train_dates, full_symbols, full_dates, *,
          predict, deadline: float | None = None) -> dict:
    """Fit on the calibration segment, report before/after on it and on the outer test."""
    import time
    import onnxruntime as ort
    if cfg.target_type != "classification":
        raise ValueError("calibration supports classification models only")
    metadata = ort.InferenceSession(str(model_path), providers=["CPUExecutionProvider"]
                                    ).get_modelmeta().custom_metadata_map
    common = dict(
        feature_mode=cfg.feature_mode, window=cfg.window_size, horizon=cfg.horizon,
        threshold=ds.DEFAULT_THRESHOLD, n_splits=cfg.n_splits, gap=cfg.gap,
        fold_index=cfg.n_splits - 1, feature_spec=cfg.feature_spec, target_type=cfg.target_type,
        indicator_export_paths=cfg.indicator_channel_export_paths,
        fixed_zscore=cfg.fixed_zscore, lags=cfg.lags)
    segment: dict = {}
    selected = ds.independent_fold_data(train_symbols, train_dates, inner=True, calibration=True,
                                        calibration_out=segment, **common)
    outer = ds.independent_fold_data(train_symbols, train_dates, **common)
    symbols = list(train_symbols)
    if sum(len(segment[s]) for s in symbols) < MIN_SAMPLES:
        raise ValueError("calibration segment is smaller than the minimum sample count")
    if deadline is not None and time.monotonic() >= deadline:
        raise TimeoutError("calibration exceeded the training run budget")
    cal_p, cal_y, cal_periods = _pool(selected, segment, symbols, predict)
    test_p, test_y, _ = _pool(
        {s: outer[s] for s in symbols}, {s: outer[s][4] for s in symbols}, symbols, predict)
    periods = [model_analysis.period(sym, "calibration", np.asarray(train_dates[sym]), anchors)
               for sym, anchors in cal_periods]
    return document(
        model_sha256=checkpoints.hash_file(model_path),
        data_revision=checkpoints.source_revision(full_symbols, full_dates, cfg.indicator_channel_export_paths),
        metadata=metadata, cal_p=cal_p, cal_y=cal_y, test_p=test_p, test_y=test_y, periods=periods)


def document(*, model_sha256: str, data_revision: str, metadata: dict[str, str], cal_p: np.ndarray,
             cal_y: np.ndarray, test_p: np.ndarray, test_y: np.ndarray, periods: list[dict]) -> dict:
    """Fit the temperature and assemble the generation-bound sidecar (shared with the C# test fixtures)."""
    temperature, at_boundary = fit_temperature(cal_p, cal_y)
    fit_revision = hashlib.sha256(
        np.ascontiguousarray(cal_p, dtype="<f8").tobytes() + np.asarray(cal_y).astype("<i8").tobytes()
        + checkpoints.canonical(periods).encode()).hexdigest()
    return dict(
        schema_version=SCHEMA_VERSION, model_sha256=model_sha256, data_revision=data_revision,
        feature_contract_hash=fixed_scaler.contract_hash(metadata),
        class_order=metadata["class_order"],
        method=METHOD, score=SCORE, epsilon=EPSILON, temperature=temperature,
        status=STATUS_BOUNDARY if at_boundary else STATUS_AVAILABLE,
        nll_definition=NLL_DEFINITION, evaluation_population=EVALUATION_POPULATION, evaluation_is_oos=True,
        fit=dict(population="calibration", sample_count=int(len(cal_y)), periods=periods,
                 fit_data_revision=fit_revision, ece_bins=ECE_BINS),
        evaluation=dict(calibration_set=metrics_block(cal_p, cal_y, temperature),
                        outer_test=metrics_block(test_p, test_y, temperature)),
    )


def parity_fixture() -> dict:
    """Shared with ``Tests/ModelCalibrationParity.json``: constants, bands and apply cases."""
    cases = []
    for probabilities, temperature in (
        ([0.8, 0.1, 0.1], 2.0), ([0.8, 0.1, 0.1], 1.0), ([0.34, 0.33, 0.33], 0.5),
        ([1.0, 0.0, 0.0], 3.0), ([0.0, 0.0, 1.0], 0.25), ([0.6, 0.3, 0.1], 4.0),
    ):
        q = calibrate(np.asarray([probabilities]), temperature)[0]
        cases.append(dict(probabilities=probabilities, temperature=temperature, calibrated=q.tolist()))
    return dict(
        schema_version=SCHEMA_VERSION, reference_key=REFERENCE_KEY, method=METHOD, score=SCORE,
        epsilon=EPSILON, ece_bins=ECE_BINS, softmax_sum_tolerance=SUM_TOLERANCE,
        status_available=STATUS_AVAILABLE, status_boundary=STATUS_BOUNDARY, nll_definition=NLL_DEFINITION,
        evaluation_population=EVALUATION_POPULATION,
        temperature_hard_min=TEMPERATURE_HARD_MIN, temperature_hard_max=TEMPERATURE_HARD_MAX,
        temperature_standard_min=TEMPERATURE_STANDARD_MIN, temperature_standard_max=TEMPERATURE_STANDARD_MAX,
        min_samples=MIN_SAMPLES, cases=cases)


def _run_selfcheck() -> None:
    # (a) known value: p=(0.8,0.1,0.1), T=2 -> proportional to sqrt(p).
    q = calibrate(np.array([[0.8, 0.1, 0.1]]), 2.0)[0]
    root = np.sqrt([0.8, 0.1, 0.1])
    assert np.allclose(q, root / root.sum(), atol=1e-12), q
    assert abs(q[0] - 0.585786) < 1e-6 and abs(q[1] - 0.207107) < 1e-6, q
    # (b) T=1 is the identity for strictly positive probabilities; rows sum to one; arg-max invariant.
    rng = np.random.default_rng(3)
    z = rng.normal(0.0, 2.0, (500, 3))
    p = np.exp(z - z.max(axis=1, keepdims=True))
    p /= p.sum(axis=1, keepdims=True)
    assert np.allclose(calibrate(p, 1.0), p, atol=1e-12)
    for t in (0.05, 0.5, 2.0, 7.0):
        c = calibrate(p, t)
        assert np.allclose(c.sum(axis=1), 1.0, atol=1e-12)
        assert (c.argmax(axis=1) == p.argmax(axis=1)).all()
    # (c) zero probabilities are clamped, never produce -inf/NaN.
    c = calibrate(np.array([[1.0, 0.0, 0.0]]), 3.0)
    assert np.isfinite(c).all() and abs(c.sum() - 1.0) < 1e-12 and c.argmax() == 0
    # (d) overconfident model: labels drawn from softmax(z/2); the fit recovers T~2 and lowers the NLL.
    n = 30000
    logits = rng.normal(0.0, 2.0, (n, 3))
    true_t = 2.0
    pt = np.exp(logits / true_t - (logits / true_t).max(axis=1, keepdims=True))
    pt /= pt.sum(axis=1, keepdims=True)
    labels = np.array([rng.choice(3, p=row) for row in pt])
    pm = np.exp(logits - logits.max(axis=1, keepdims=True))
    pm /= pm.sum(axis=1, keepdims=True)
    fitted, at_boundary = fit_temperature(pm, labels)
    assert not at_boundary and abs(fitted - true_t) < 0.15, fitted
    block = metrics_block(pm, labels, fitted)
    assert block["after"]["nll"] < block["before"]["nll"] and block["after"]["ece"] < block["before"]["ece"], block
    assert fit_temperature(pm, labels) == (fitted, False)  # deterministic
    # (e) a perfectly separated sample has no interior optimum: the fit reports the boundary instead of failing,
    # the temperature stays inside the hard band, and the sidecar is still produced -- declared not applicable.
    sharp = np.tile(np.array([[1.0, 0.0, 0.0]]), (200, 1))
    sharp_labels = np.zeros(200, dtype=np.int64)
    sharp_t, sharp_boundary = fit_temperature(sharp, sharp_labels)
    assert sharp_boundary and TEMPERATURE_HARD_MIN <= sharp_t <= TEMPERATURE_HARD_MAX, sharp_t
    metadata = {"class_order": "Up,Down,Neutral"}
    boundary_doc = document(model_sha256="a" * 64, data_revision="b" * 64, metadata=metadata, cal_p=sharp,
                            cal_y=sharp_labels, test_p=sharp, test_y=sharp_labels, periods=[])
    assert boundary_doc["status"] == STATUS_BOUNDARY and boundary_doc["temperature"] == sharp_t
    json.dumps(boundary_doc, allow_nan=False)
    regular_doc = document(model_sha256="a" * 64, data_revision="b" * 64, metadata=metadata, cal_p=pm,
                           cal_y=labels, test_p=pm, test_y=labels, periods=[])
    assert regular_doc["status"] == STATUS_AVAILABLE and regular_doc["nll_definition"] == NLL_DEFINITION
    assert regular_doc["evaluation_population"] == EVALUATION_POPULATION and regular_doc["evaluation_is_oos"] is True
    # (f) metrics on hand-computed values.
    pp = np.array([[1.0, 0.0, 0.0], [1.0, 0.0, 0.0]])
    assert abs(brier(pp, np.array([0, 1])) - 1.0) < 1e-12  # (0 + 2) / 2
    assert abs(ece(pp, np.array([0, 1])) - 0.5) < 1e-12  # conf 1.0, accuracy 0.5
    assert abs(nll(np.array([[0.5, 0.25, 0.25]]), np.array([0])) - math.log(2.0)) < 1e-12
    # (g) input validation.
    for bad_p, bad_y in ((np.array([[0.5, 0.5]]), np.array([2])), (np.zeros((0, 3)), np.zeros(0)),
                         (np.array([[np.nan, 0.5, 0.5]]), np.array([0]))):
        try:
            fit_temperature(bad_p, bad_y)
        except ValueError:
            pass
        else:  # pragma: no cover
            raise AssertionError("invalid calibration input accepted")
    # (h) the parity fixture round-trips through JSON without non-finite numbers.
    text = json.dumps(parity_fixture(), allow_nan=False)
    assert json.loads(text)["cases"][0]["calibrated"][0] > 0
    with tempfile.TemporaryDirectory() as folder:
        path = Path(folder) / "p.json"
        path.write_text(text, encoding="utf-8")
        assert json.loads(path.read_text(encoding="utf-8"))["epsilon"] == EPSILON
    # (j) equal-mass ECE by hand. Ten samples -> one per bin: the mean of |correct - confidence|.
    def rows(confidences, correct):
        p_ = np.array([[c, (1 - c) / 2, (1 - c) / 2] for c in confidences])
        y_ = np.array([0 if ok else 1 for ok in correct])  # the arg-max is class 0, so label 0 is a hit
        return p_, y_

    p10, y10 = rows([0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 0.95, 0.99, 0.6, 0.5], [1, 0, 1, 1, 0, 1, 1, 1, 0, 1])
    assert abs(ece_equal_mass(p10, y10) - 0.386) < 1e-12  # (.6+.5+.4+.3+.8+.1+.05+.01+.6+.5) / 10
    # Twelve samples: sizes 2,2,1,1,1,1,1,1,1,1 (the remainder goes one each to the first bins, not all to the first).
    p12, y12 = rows([0.4, 0.5, 0.6, 0.8] + [1.0] * 8, [1, 0, 0, 1] + [1] * 8)
    expected = 2 / 12 * abs(0.5 - 0.45) + 2 / 12 * abs(0.5 - 0.7)
    assert abs(ece_equal_mass(p12, y12) - expected) < 1e-12
    shuffled = np.random.default_rng(4).permutation(12)
    assert abs(ece_equal_mass(p12[shuffled], y12[shuffled]) - expected) < 1e-12  # order-free
    assert ece_equal_mass(p12[:3], y12[:3]) >= 0.0  # fewer samples than bins: empty bins are skipped
    # a calibrated sample has a small ECE under both binnings; both are bounded by [0, 1]
    assert 0.0 <= ece_equal_mass(pm, labels) <= 1.0
    # (k) reliability bins by hand: counts add up, empty bins carry null, bins match the equal-width ECE.
    bins = reliability_bins(p12, y12)
    assert [b["count"] for b in bins] == [0, 0, 0, 0, 1, 1, 1, 0, 1, 8], bins
    assert bins[4] == dict(count=1, mean_confidence=0.4, accuracy=1.0)
    assert bins[5]["accuracy"] == 0.0 and bins[9] == dict(count=8, mean_confidence=1.0, accuracy=1.0)
    assert bins[0] == dict(count=0, mean_confidence=None, accuracy=None)
    big = reliability_bins(pm, labels)
    assert sum(b["count"] for b in big) == len(labels)
    weighted = sum(b["count"] / len(labels) * abs(b["accuracy"] - b["mean_confidence"]) for b in big if b["count"])
    assert abs(weighted - ece(pm, labels)) < 1e-12
    block_out = metrics_block(pm, labels, fitted)
    assert [sum(b["count"] for b in block_out["reliability"][k]) for k in ("before", "after")] == [len(labels)] * 2
    assert "ece_equal_mass" in block_out["before"] and "ece_equal_mass" in block_out["after"]
    # (l) probability rows must sum to one within the shared tolerance (C# SoftmaxSumTolerance).
    def accepted(row):
        try:
            nll(np.array([row]), np.array([0]))
            return True
        except ValueError:
            return False
    assert accepted([0.5, 0.25, 0.25]) and accepted([0.5, 0.25, 0.25 + 0.5 * SUM_TOLERANCE])
    assert not accepted([0.5, 0.5, 0.5]) and not accepted([0.5, 0.25, 0.25 + 2 * SUM_TOLERANCE])
    assert not accepted([0.4, 0.3, 0.2])

    # (i) independent reference: scipy's softmax and bounded scalar minimiser agree with ours. scipy is NOT a declared
    # dependency (requirements.txt), so this group is skipped, loudly, when it cannot be imported.
    try:
        import scipy.optimize
        import scipy.special
    except ImportError:
        print("calibration.py selfcheck: scipy is not importable -- the external reference comparison (group 9) was SKIPPED")
        print("calibration.py selfcheck: OK (11 groups passed, 1 skipped)")
        return
    for temperature in (0.4, 1.0, 2.5):
        reference = scipy.special.softmax(log_scores(pm) / temperature, axis=1)
        assert np.allclose(calibrate(pm, temperature), reference, atol=1e-12, rtol=0), temperature
    solved = scipy.optimize.minimize_scalar(
        lambda beta: nll(pm, labels, 1.0 / beta), bounds=(1.0 / TEMPERATURE_HARD_MAX, 1.0 / TEMPERATURE_HARD_MIN),
        method="bounded", options={"xatol": 1e-12})
    assert solved.success and abs(1.0 / solved.x - fitted) < 1e-4, (1.0 / solved.x, fitted)
    assert abs(nll(pm, labels, fitted) - nll(pm, labels, 1.0 / solved.x)) < 1e-9
    assert nll(pm, labels, fitted) <= nll(pm, labels, 1.0 / solved.x) + 1e-12, "our optimum is no worse than scipy's"
    print("calibration.py selfcheck: OK (12 groups passed)")


def _parse_args(argv) -> argparse.Namespace:
    p = argparse.ArgumentParser(description="Temperature calibration helpers.")
    p.add_argument("--selfcheck", action="store_true")
    p.add_argument("--emit-parity", type=Path, default=None,
                   help="Write the C#/Python parity fixture (Tests/ModelCalibrationParity.json).")
    return p.parse_args(argv)


if __name__ == "__main__":
    args = _parse_args(sys.argv[1:])
    if args.emit_parity is not None:
        args.emit_parity.write_bytes((json.dumps(parity_fixture(), indent=2, allow_nan=False) + "\n").encode("utf-8"))
    elif args.selfcheck:
        _run_selfcheck()
    else:
        raise SystemExit("use --selfcheck or --emit-parity PATH")
