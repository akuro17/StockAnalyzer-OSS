"""T11 offline explanation of the exported classification ONNX: Shapley contributions and PDP.

Approved D11 (11-1=A, 11-2=B, 11-3=B, 11-4=B, 11-5=A/A/100/A, 11-6=A, 11-8=A, 11-9=A, 11-10=A):

* output space = the deployed ONNX probability (``[batch,3]``, Up/Down/Neutral); all 3 classes.
* feature = one *base channel*: every window bar of the channel and all of its lag copies (the same
  grouping as :func:`model_analysis.permutation_importance`); never the individual ``W*C`` cells.
* algorithm = Shapley permutation sampling with interventional baselines, no extra dependency.
  Permutation ``p`` is paired with the ``p``-th background window, so the contributions satisfy
  ``f(x) = base + sum_j phi_j`` exactly with ``base = mean_p f(background_p)``.
* background = ``BACKGROUND_SIZE`` windows drawn without replacement (seeded, uniform) from the
  model's *fit* population; the explained samples come from the early-stopping validation
  population ("representative cases"). The out-of-sample tail and the calibration segment are never read.
* reliability = every contribution carries the standard error of the permutation mean
  (``standard_error``) and every case the largest change when only the first half of the
  permutations is used (``stability_half_delta``); nothing is judged against a threshold.
* randomness = the background and every case draw from independent ``SeedSequence`` streams, so
  one case's contributions do not depend on which other cases are explained or in what order.
* PDP = mean probability over the background while the channel group is shifted by a constant
  offset in the ONNX *input* (post-scaler) space; the offsets are quantiles of the fit population's
  channel cells minus their median. The shift does not keep OHLC geometry consistent and is
  reported as such (``pdp.semantics``); ``out_of_support_rate`` states how many shifted cells leave
  the fit population's [min, max]. It is an input-shift response, not a conditional dependence.

The module runs offline only (never inside prediction or rendering) and reads the exported
ONNX itself, so it explains exactly the deployed generation. Run
``python explain.py --selfcheck`` to verify the identities.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import sys
import time
import zlib
from pathlib import Path
from typing import Callable

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import checkpoints  # noqa: E402
import dataset as ds  # noqa: E402
import fixed_scaler  # noqa: E402
import model_analysis  # noqa: E402

SCHEMA_VERSION = 1
OUTPUT_SPACE = "probability"
METHOD = "shapley_permutation_sampling"
EXPLANATION_TYPE = "grouped_interventional_permutation_shapley"
RNG_POLICY = "per_case_seed_sequence"
SEED = model_analysis.SEED
BACKGROUND_STREAM = 0  # SeedSequence spawn_key namespaces: background draw / one stream per case
CASE_STREAM = 1
BACKGROUND_SIZE = 100
REPRESENTATIVE_PER_CLASS = 2
REPRESENTATIVE_LATEST = 2
PDP_GRID_POINTS = 11
PDP_QUANTILE_LOW = 0.05
PDP_QUANTILE_HIGH = 0.95
PDP_SEMANTICS = "input_space_constant_offset_not_ohlc_consistent"
REASON_LATEST = "latest"  # why a case was explained: one of the newest anchors / one of the most confident per class
REASON_TOP_PREFIX = "top_"  # followed by the class name; mirror of C# ModelExplanationContract.ReasonLatest / ReasonTopPrefix
EVAL_BATCH_ROWS = 2048  # evaluation batching only; never changes a result

# Safety bound (mirror of C# ModelExplanationLimits.MaxModelCalls).
MAX_MODEL_CALLS = 2_000_000

Forward = Callable[[np.ndarray], np.ndarray]


def groups(base_count: int, channels: int) -> list[np.ndarray]:
    """Tensor channel indices of each base channel (lag copies share the base index modulo)."""
    if base_count <= 0 or channels <= 0 or channels % base_count:
        raise ValueError("tensor channels must be a positive multiple of the base channel count")
    return [np.arange(b, channels, base_count) for b in range(base_count)]


def total_calls(validation: int, cases: int, permutations: int, features: int, grid: int,
                background: int) -> int:
    """Model rows evaluated by one explanation: validation scoring + Shapley paths + PDP."""
    return validation + cases * permutations * (features + 1) + features * grid * background


def _forward_chunks(forward: Forward, rows: np.ndarray, deadline: float | None) -> np.ndarray:
    parts = []
    for start in range(0, len(rows), EVAL_BATCH_ROWS):
        if deadline is not None and time.monotonic() >= deadline:
            raise TimeoutError("explanation exceeded the training run budget")
        parts.append(np.asarray(forward(rows[start:start + EVAL_BATCH_ROWS]), dtype=np.float64))
    return np.concatenate(parts) if parts else np.empty((0, 0))


def background_rng() -> np.random.Generator:
    return np.random.default_rng(np.random.SeedSequence(SEED, spawn_key=(BACKGROUND_STREAM,)))


def case_rng(symbol: str, anchor_row: int) -> np.random.Generator:
    """The stream of one explained case, a function of the case alone (never of the other cases)."""
    key = (CASE_STREAM, int(anchor_row), zlib.crc32(symbol.encode("utf-8")))
    return np.random.default_rng(np.random.SeedSequence(SEED, spawn_key=key))


def reliability(contributions: np.ndarray) -> tuple[np.ndarray | None, float | None]:
    """``(standard_error[F,K], half_delta)`` of the permutation mean of ``contributions[P,F,K]``.

    The standard error is ``std(ddof=1) / sqrt(P)``; ``half_delta`` is the largest absolute difference
    between the mean over the first ``P // 2`` permutations and the mean over all of them. Fewer than
    two permutations carry no spread: ``(None, None)``."""
    count = len(contributions)
    if count < 2:
        return None, None
    standard_error = contributions.std(axis=0, ddof=1) / math.sqrt(count)
    half = np.abs(contributions[: count // 2].mean(axis=0) - contributions.mean(axis=0)).max()
    return standard_error, float(half)


def shapley(forward: Forward, x: np.ndarray, background: np.ndarray, feature_groups: list[np.ndarray],
            rng: np.random.Generator, deadline: float | None = None):
    """Permutation-sampled Shapley values of one window.

    Returns ``(phi[F,K], base[K], fx[K], contributions[P,F,K])``; ``contributions`` holds the increment
    each permutation assigned to each feature and ``phi`` is their mean."""
    permutations = len(background)
    features = len(feature_groups)
    paths = np.empty((permutations, features + 1) + x.shape, dtype=x.dtype)
    orders = np.empty((permutations, features), dtype=np.int64)
    for p in range(permutations):
        order = rng.permutation(features)
        orders[p] = order
        paths[p, 0] = background[p]
        for step, feature in enumerate(order):
            paths[p, step + 1] = paths[p, step]
            index = feature_groups[feature]
            paths[p, step + 1][:, index] = x[:, index]
    probs = _forward_chunks(forward, paths.reshape((-1,) + x.shape), deadline)
    probs = probs.reshape(permutations, features + 1, -1)
    contributions = np.zeros((permutations, features, probs.shape[2]))
    for p in range(permutations):
        for step, feature in enumerate(orders[p]):
            contributions[p, feature] = probs[p, step + 1] - probs[p, step]
    phi = contributions.mean(axis=0)
    base = probs[:, 0].mean(axis=0)
    fx = probs[:, features].mean(axis=0)
    return phi, base, fx, contributions


def partial_dependence(forward: Forward, background: np.ndarray, feature_group: np.ndarray,
                       offsets: np.ndarray, support: tuple[float, float],
                       deadline: float | None = None) -> tuple[np.ndarray, np.ndarray]:
    """Per constant input-space offset: the mean probability ``[G, K]`` over the background and the
    share ``[G]`` of shifted cells outside ``support`` = (min, max) of the fit population (inclusive)."""
    low, high = support
    values, outside = [], []
    for offset in offsets:
        shifted = background.copy()
        shifted[:, :, feature_group] = shifted[:, :, feature_group] + offset
        cells = shifted[:, :, feature_group]
        outside.append(float(np.mean((cells < low) | (cells > high))))
        values.append(_forward_chunks(forward, shifted, deadline).mean(axis=0))
    return np.asarray(values), np.asarray(outside)


def select_representative(probabilities: np.ndarray, anchor_dates: np.ndarray,
                          classes: list[str]) -> list[dict]:
    """Per class the ``REPRESENTATIVE_PER_CLASS`` highest class probabilities, then the
    ``REPRESENTATIVE_LATEST`` most recent anchors (ties by pooled index), de-duplicated in order."""
    order: list[int] = []
    reasons: dict[int, list[str]] = {}

    def add(index: int, reason: str) -> None:
        if index not in reasons:
            order.append(index)
            reasons[index] = []
        reasons[index].append(reason)

    for k, name in enumerate(classes):
        ranked = np.argsort(-probabilities[:, k], kind="stable")
        for index in ranked[:REPRESENTATIVE_PER_CLASS]:
            add(int(index), f"{REASON_TOP_PREFIX}{name}")
    dates = np.asarray(anchor_dates).astype("datetime64[D]")
    latest = np.lexsort((np.arange(len(dates)), -dates.astype("int64")))
    for index in latest[:REPRESENTATIVE_LATEST]:
        add(int(index), REASON_LATEST)
    return [dict(index=i, reasons=reasons[i]) for i in order]


def explain_arrays(forward: Forward, *, fit_tensor: np.ndarray, validation_tensor: np.ndarray,
                   validation_symbols: list[str], validation_dates: np.ndarray,
                   validation_rows: np.ndarray, classes: list[str], base_channels: list[str],
                   deadline: float | None = None) -> dict:
    """The model-independent core: background draw, representative cases, Shapley and PDP."""
    feature_groups = groups(len(base_channels), fit_tensor.shape[2])
    features = len(feature_groups)
    if len(fit_tensor) == 0:
        return dict(status="no_background")
    if len(validation_tensor) == 0:
        return dict(status="no_validation")
    size = min(BACKGROUND_SIZE, len(fit_tensor))
    background = fit_tensor[np.sort(background_rng().choice(len(fit_tensor), size=size, replace=False))]
    cases = min(len(validation_tensor),
                len(classes) * REPRESENTATIVE_PER_CLASS + REPRESENTATIVE_LATEST)
    calls = total_calls(len(validation_tensor), cases, size, features, PDP_GRID_POINTS, size)
    if calls > MAX_MODEL_CALLS:
        return dict(status="call_budget_exceeded", model_calls=calls)
    validation_probs = _forward_chunks(forward, validation_tensor, deadline)
    selected = select_representative(validation_probs, validation_dates, classes)
    records = []
    max_standard_error, max_half_delta = None, None
    for item in selected:
        i = item["index"]
        phi, base, fx, contributions = shapley(
            forward, validation_tensor[i], background, feature_groups,
            case_rng(validation_symbols[i], int(validation_rows[i])), deadline)
        standard_error, half_delta = reliability(contributions)
        outputs = []
        for k, name in enumerate(classes):
            rows = []
            for f in range(features):
                row = dict(channel=base_channels[f], value=float(phi[f, k]))
                if standard_error is not None:
                    row["standard_error"] = float(standard_error[f, k])
                rows.append(row)
            outputs.append(dict(output_id=name, base_value=float(base[k]), prediction=float(fx[k]),
                                contributions=rows))
        record = dict(
            sample=dict(symbol=validation_symbols[i], anchor_date=str(np.datetime64(validation_dates[i], "D")),
                        anchor_row=int(validation_rows[i])),
            reasons=item["reasons"], outputs=outputs)
        if standard_error is not None:
            record["stability_half_delta"] = half_delta
            max_standard_error = max(float(standard_error.max()), max_standard_error or 0.0)
            max_half_delta = max(half_delta, max_half_delta or 0.0)
        records.append(record)
    levels = np.linspace(PDP_QUANTILE_LOW, PDP_QUANTILE_HIGH, PDP_GRID_POINTS)
    pdp_channels = []
    for f, index in enumerate(feature_groups):
        cells = fit_tensor[:, :, index].reshape(-1).astype(np.float64)
        q = np.quantile(cells, levels)
        offsets = q - np.quantile(cells, 0.5)
        values, outside = partial_dependence(forward, background, index, offsets.astype(background.dtype),
                                             (float(cells.min()), float(cells.max())), deadline)
        pdp_channels.append(dict(
            channel=base_channels[f], offsets=[float(v) for v in offsets],
            values={name: [float(v) for v in values[:, k]] for k, name in enumerate(classes)},
            out_of_support_rate=[float(v) for v in outside]))
    body = dict(
        status="available", output_space=OUTPUT_SPACE, method=METHOD, seed=SEED,
        explanation_type=EXPLANATION_TYPE, rng_policy=RNG_POLICY,
        permutations=int(size), background_size=int(size), model_calls=int(calls),
        classes=list(classes),
        population=dict(background="train", explained="inner_validation",
                        validation_sample_count=int(len(validation_tensor))),
        records=records,
        pdp=dict(grid_points=PDP_GRID_POINTS, quantile_levels=[float(v) for v in levels],
                 semantics=PDP_SEMANTICS, channels=pdp_channels))
    if max_standard_error is not None:
        body["stability"] = dict(permutations_used=int(size), max_standard_error=max_standard_error,
                                 max_half_delta=max_half_delta)
    return body


def _pool(selected: dict, symbols: list[str], which: int, transform):
    tensors, names, rows, dates = [], [], [], []
    for sym in symbols:
        x, _y, anchors, fit, validation = selected[sym]
        index = (fit, validation)[which]
        tensors.append(transform(x[index]))
        names += [sym] * len(index)
        rows.append(anchors[index])
    return np.concatenate(tensors), names, np.concatenate(rows)


def target_contract_hash(metadata: dict[str, str]) -> str:
    keys = ("target_type", "prediction_horizon", "class_order",
            "com.stockanalyzer.output.target_formula", "neutral_threshold")
    return hashlib.sha256(checkpoints.canonical({k: metadata.get(k, "") for k in keys}).encode()).hexdigest()


def make_header(*, model_sha256: str, data_revision: str, metadata: dict[str, str], timeframe: str,
                window_size: int, base_channels: list[str], ordered_channels: list[str]) -> dict:
    """The generation-binding fields shared by every explanation document."""
    return dict(
        schema_version=SCHEMA_VERSION, model_sha256=model_sha256, data_revision=data_revision,
        feature_contract_hash=fixed_scaler.contract_hash(metadata),
        target_contract_hash=target_contract_hash(metadata), timeframe=timeframe,
        window_size=window_size, base_channels=base_channels, ordered_channels=ordered_channels)


def build(cfg, model_path: Path, train_symbols, train_dates, full_symbols, full_dates, *,
          deadline: float | None = None, calibration: bool = False) -> dict:
    """Explain the exported release model. ``calibration`` selects the same carved split the
    model was trained on (the fit/validation populations differ from the legacy split)."""
    import onnxruntime as ort
    session = ort.InferenceSession(str(model_path), providers=["CPUExecutionProvider"])
    metadata = session.get_modelmeta().custom_metadata_map
    base_channels, expanded = model_analysis.channel_names(metadata)
    header = make_header(
        model_sha256=checkpoints.hash_file(model_path),
        data_revision=checkpoints.source_revision(full_symbols, full_dates, cfg.indicator_channel_export_paths),
        metadata=metadata, timeframe=cfg.timeframe, window_size=cfg.window_size,
        base_channels=base_channels, ordered_channels=expanded)
    if cfg.target_type != "classification":
        return dict(header, status="regression_not_adopted")
    classes = list(ds.CLASS_LABELS)
    if metadata["class_order"].split(",") != classes:
        raise ValueError("model class order disagrees with the dataset class labels")
    scaler = fixed_scaler.load_sidecar(model_path, metadata) if fixed_scaler.requires_fixed_scaler(metadata) else None

    def transform(x: np.ndarray) -> np.ndarray:
        return (fixed_scaler.transform(x, scaler) if scaler is not None
                else np.asarray(x, dtype=np.float32))

    def forward(tensor: np.ndarray) -> np.ndarray:
        (probs,) = session.run(["output"], {"input": np.asarray(tensor, dtype=np.float32)})
        return np.asarray(probs, dtype=np.float64)

    selected = ds.independent_fold_data(
        train_symbols, train_dates, feature_mode=cfg.feature_mode, window=cfg.window_size,
        horizon=cfg.horizon, threshold=ds.DEFAULT_THRESHOLD, n_splits=cfg.n_splits, gap=cfg.gap,
        fold_index=cfg.n_splits - 1, feature_spec=cfg.feature_spec, target_type=cfg.target_type,
        indicator_export_paths=cfg.indicator_channel_export_paths, inner=True,
        fixed_zscore=cfg.fixed_zscore, lags=cfg.lags, calibration=calibration)
    symbols = list(train_symbols)
    fit_tensor, _fit_names, _fit_rows = _pool(selected, symbols, 0, transform)
    val_tensor, val_names, val_rows = _pool(selected, symbols, 1, transform)
    val_dates = np.concatenate([
        np.asarray(train_dates[sym]).astype("datetime64[D]")[selected[sym][2][selected[sym][4]]]
        for sym in symbols])
    body = explain_arrays(forward, fit_tensor=fit_tensor, validation_tensor=val_tensor,
                          validation_symbols=val_names, validation_dates=val_dates,
                          validation_rows=val_rows, classes=classes, base_channels=base_channels,
                          deadline=deadline)
    return dict(header, **body)


def parity_fixture() -> dict:
    """Shared with ``Tests/ModelExplanationParity.json``: contract constants and the target-hash case."""
    metadata = {
        "class_order": "Up,Down,Neutral", "com.stockanalyzer.output.target_formula": "thresholded_simple_return",
        "neutral_threshold": "0.005", "prediction_horizon": "5", "target_type": "classification",
    }
    return dict(
        schema_version=SCHEMA_VERSION, output_space=OUTPUT_SPACE, method=METHOD, seed=SEED,
        background_size=BACKGROUND_SIZE, representative_per_class=REPRESENTATIVE_PER_CLASS,
        representative_latest=REPRESENTATIVE_LATEST, pdp_grid_points=PDP_GRID_POINTS,
        pdp_quantile_low=PDP_QUANTILE_LOW, pdp_quantile_high=PDP_QUANTILE_HIGH,
        pdp_semantics=PDP_SEMANTICS, max_model_calls=MAX_MODEL_CALLS,
        explanation_type=EXPLANATION_TYPE, rng_policy=RNG_POLICY,
        reason_latest=REASON_LATEST, reason_top_prefix=REASON_TOP_PREFIX,
        target_contract=dict(metadata=metadata, hash=target_contract_hash(metadata)))


def _assert_finite(value) -> None:
    json.dumps(value, allow_nan=False)


def _run_selfcheck() -> None:
    rng = np.random.default_rng(11)
    window, channels_n, base_n = 6, 6, 3  # one lag copy: channels = base * 2
    classes = ["Up", "Down", "Neutral"]
    weights = rng.normal(0.0, 0.7, (window * channels_n, 3))

    def forward(tensor: np.ndarray) -> np.ndarray:
        z = tensor.reshape(len(tensor), -1) @ weights
        z -= z.max(axis=1, keepdims=True)
        e = np.exp(z)
        return e / e.sum(axis=1, keepdims=True)

    fit = rng.normal(0.0, 1.0, (400, window, channels_n))
    val = rng.normal(0.0, 1.0, (30, window, channels_n))
    dates = np.datetime64("2020-01-01") + np.arange(len(val)).astype("timedelta64[D]")
    feature_groups = groups(base_n, channels_n)
    assert [g.tolist() for g in feature_groups] == [[0, 3], [1, 4], [2, 5]]

    # (a) efficiency: f(x) = base + sum(phi) exactly, per class, with the sampled paths.
    background = fit[:BACKGROUND_SIZE]
    phi, base, fx, parts = shapley(forward, val[0], background, feature_groups, np.random.default_rng(1))
    assert np.allclose(fx, forward(val[:1])[0], atol=1e-12)
    assert np.allclose(phi.sum(axis=0), fx - base, atol=1e-10), (phi.sum(axis=0), fx - base)
    assert np.allclose(base, forward(background).mean(axis=0), atol=1e-12)

    # (b) a null channel group (zero weight) gets exactly zero; the single live group takes it all.
    live = weights.copy()
    live[:] = 0.0
    cells = np.arange(window * channels_n).reshape(window, channels_n)
    live[cells[:, feature_groups[0]].reshape(-1)] = rng.normal(0.0, 1.0, (window * 2, 3))

    def forward_live(tensor: np.ndarray) -> np.ndarray:
        z = tensor.reshape(len(tensor), -1) @ live
        z -= z.max(axis=1, keepdims=True)
        e = np.exp(z)
        return e / e.sum(axis=1, keepdims=True)

    phi2, base2, fx2, _parts2 = shapley(forward_live, val[1], background, feature_groups, np.random.default_rng(2))
    assert np.allclose(phi2[1:], 0.0, atol=1e-15) and np.allclose(phi2[0], fx2 - base2, atol=1e-10)

    # (c) determinism: the same seed gives identical contributions.
    again = shapley(forward, val[0], background, feature_groups, np.random.default_rng(1))[0]
    assert np.array_equal(phi, again)

    # (d) PDP: offset 0 equals the plain background mean; an unused channel group is flat.
    wide = (float(fit.min()), float(fit.max()))
    zero_pdp, zero_outside = partial_dependence(
        forward_live, background, feature_groups[1], np.array([0.0, 1.0, -1.0]), wide)
    assert zero_outside[0] == 0.0  # the background lies inside the fit population's range
    assert np.allclose(zero_pdp[0], forward_live(background).mean(axis=0), atol=1e-12)
    assert np.allclose(zero_pdp, zero_pdp[0], atol=1e-12)
    moved, _ = partial_dependence(forward_live, background, feature_groups[0], np.array([0.0, 2.0]), wide)
    assert not np.allclose(moved[0], moved[1])

    # (e) representative cases: top-2 per class then the 2 latest anchors, de-duplicated, stable ties.
    probs = forward(val)
    chosen = select_representative(probs, dates, classes)
    indexes = [c["index"] for c in chosen]
    assert len(indexes) == len(set(indexes)) <= len(classes) * REPRESENTATIVE_PER_CLASS + REPRESENTATIVE_LATEST
    assert any("latest" in c["reasons"] for c in chosen) and indexes[0] == int(np.argmax(probs[:, 0]))
    tied = select_representative(np.full((5, 3), 1 / 3), np.full(5, np.datetime64("2020-01-01")), classes)
    assert [c["index"] for c in tied][:2] == [0, 1]  # equal probabilities: lowest index first

    # (f) full pipeline on arrays: schema, JSON-safety, no-background / no-validation / call-budget statuses.
    names = ["S"] * len(val)
    result = explain_arrays(forward, fit_tensor=fit, validation_tensor=val, validation_symbols=names,
                            validation_dates=dates, validation_rows=np.arange(len(val)),
                            classes=classes, base_channels=["open", "high", "low"])
    assert result["status"] == "available" and result["permutations"] == BACKGROUND_SIZE
    _assert_finite(result)
    for record in result["records"]:
        assert [o["output_id"] for o in record["outputs"]] == classes
        for output in record["outputs"]:
            total = sum(c["value"] for c in output["contributions"])
            assert abs(output["base_value"] + total - output["prediction"]) < 1e-9
    assert result["pdp"]["grid_points"] == PDP_GRID_POINTS
    for channel in result["pdp"]["channels"]:
        assert len(channel["offsets"]) == PDP_GRID_POINTS and abs(channel["offsets"][PDP_GRID_POINTS // 2]) < 1e-12
    assert explain_arrays(forward, fit_tensor=fit[:0], validation_tensor=val, validation_symbols=names,
                          validation_dates=dates, validation_rows=np.arange(len(val)), classes=classes,
                          base_channels=["a", "b", "c"])["status"] == "no_background"
    assert explain_arrays(forward, fit_tensor=fit, validation_tensor=val[:0], validation_symbols=[],
                          validation_dates=dates[:0], validation_rows=np.arange(0), classes=classes,
                          base_channels=["a", "b", "c"])["status"] == "no_validation"
    assert total_calls(30, 8, 100, 3, 11, 100) == 30 + 8 * 100 * 4 + 3 * 11 * 100
    global MAX_MODEL_CALLS
    saved = MAX_MODEL_CALLS
    try:
        MAX_MODEL_CALLS = 10
        assert explain_arrays(forward, fit_tensor=fit, validation_tensor=val, validation_symbols=names,
                              validation_dates=dates, validation_rows=np.arange(len(val)), classes=classes,
                              base_channels=["a", "b", "c"])["status"] == "call_budget_exceeded"
    finally:
        MAX_MODEL_CALLS = saved

    # (g) a short fit population uses every window (permutations = available background).
    short = explain_arrays(forward, fit_tensor=fit[:7], validation_tensor=val, validation_symbols=names,
                           validation_dates=dates, validation_rows=np.arange(len(val)), classes=classes,
                           base_channels=["a", "b", "c"])
    assert short["permutations"] == 7 and short["background_size"] == 7

    # (h) misconfigured group layout is rejected.
    try:
        groups(4, 6)
    except ValueError:
        pass
    else:  # pragma: no cover
        raise AssertionError("channels not a multiple of the base count must fail")
    # (i) exact-Shapley oracle (F <= 5): every one of the F! orders, over the SAME backgrounds the estimator uses.
    import itertools

    def exact_shapley(fwd: Forward, x: np.ndarray, bg: np.ndarray, fgroups: list[np.ndarray]) -> np.ndarray:
        acc = None
        for background_row in bg:
            for order in itertools.permutations(range(len(fgroups))):
                path = background_row.copy()
                before = np.asarray(fwd(path[None]), dtype=np.float64)[0]
                if acc is None:
                    acc = np.zeros((len(fgroups), before.shape[0]))
                for feature in order:
                    path = path.copy()
                    path[:, fgroups[feature]] = x[:, fgroups[feature]]
                    after = np.asarray(fwd(path[None]), dtype=np.float64)[0]
                    acc[feature] += after - before
                    before = after
        return acc / (len(bg) * math.factorial(len(fgroups)))

    # hand value: f = x0 * x1, x = (1, 1), background (0, 0): the pair shares the interaction equally
    def product(tensor: np.ndarray) -> np.ndarray:
        return (tensor[:, 0, 0] * tensor[:, 0, 1])[:, None]

    pair = groups(2, 2)
    hand = exact_shapley(product, np.array([[1.0, 1.0]]), np.zeros((1, 1, 2)), pair)
    assert np.allclose(hand, [[0.5], [0.5]], atol=1e-15)
    assert np.allclose(shapley(product, np.array([[1.0, 1.0]]), np.zeros((1, 1, 2)), pair,
                               np.random.default_rng(0))[0].sum(axis=0), [1.0], atol=1e-15)

    # an additive model has the same contribution under every order, so the estimator IS the oracle (any seed, P = 100)
    additive_weights = np.random.default_rng(5).normal(0.0, 1.0, (4 * 3, 2))  # window 4, three single-channel groups

    def additive(tensor: np.ndarray) -> np.ndarray:
        return tensor.reshape(len(tensor), -1) @ additive_weights

    three = groups(3, 3)
    bg100 = np.random.default_rng(6).normal(0.0, 1.0, (100, 4, 3))
    point = np.random.default_rng(7).normal(0.0, 1.0, (4, 3))
    oracle3 = exact_shapley(additive, point, bg100, three)
    for seed in (0, 1, 2):
        estimate = shapley(additive, point, bg100, three, np.random.default_rng(seed))[0]
        assert np.allclose(estimate, oracle3, atol=1e-12, rtol=0), seed
    # efficiency of the oracle itself: contributions add up to f(x) - mean f(background)
    assert np.allclose(oracle3.sum(axis=0), additive(point[None])[0] - additive(bg100).mean(axis=0), atol=1e-10)

    # an interacting model: the estimator is unbiased and its error against the oracle shrinks as permutations grow
    four = groups(4, 4)
    mix_weights = np.random.default_rng(8).normal(0.0, 1.0, (4, 2))

    def interacting(tensor: np.ndarray) -> np.ndarray:
        s = tensor.sum(axis=1)  # [N, 4] one value per channel group
        return np.stack([s[:, 0] * s[:, 1] + np.sin(s[:, 2]) * s[:, 3], s @ mix_weights[:, 0] + (s[:, 1] * s[:, 3]) ** 2], axis=1)

    pool = np.random.default_rng(9).normal(0.0, 1.0, (160, 3, 4))
    target = np.random.default_rng(10).normal(0.0, 1.0, (3, 4))
    rmse = {}
    mean_error = {}
    for permutations in (10, 40, 160):
        exact = exact_shapley(interacting, target, pool[:permutations], four)
        errors = np.stack([shapley(interacting, target, pool[:permutations], four, np.random.default_rng(1000 + s))[0] - exact
                           for s in range(60)])
        rmse[permutations] = float(np.sqrt((errors ** 2).mean()))
        mean_error[permutations] = float(np.abs(errors.mean(axis=0)).max())
    assert rmse[10] > rmse[40] > rmse[160] > 0.0, rmse
    assert rmse[160] < rmse[10] / 2.5, rmse  # about 1/sqrt(16) = 1/4 of the P = 10 error
    for permutations, bias in mean_error.items():  # the mean of 60 draws is within a few standard errors of the oracle
        assert bias < 4.0 * rmse[permutations] / math.sqrt(60) * 2.0, (permutations, bias, rmse[permutations])

    # (j) reliability: hand values. Four permutations contributing 1, 3, 5, 7 to the single feature/class.
    hand_parts = np.array([1.0, 3.0, 5.0, 7.0]).reshape(4, 1, 1)
    hand_se, hand_half = reliability(hand_parts)
    assert np.allclose(hand_se, math.sqrt(20.0 / 3.0) / 2.0, atol=1e-15)  # sample std (ddof=1) / sqrt(P)
    assert hand_half == 2.0  # mean(1, 3) = 2 against the mean of all four = 4
    assert reliability(hand_parts[:1]) == (None, None)  # one permutation carries no spread
    assert reliability(np.zeros((3, 2, 2)))[1] == 0.0 and not reliability(np.zeros((3, 2, 2)))[0].any()
    # the per-permutation contributions behind phi are exposed and consistent with it
    assert np.array_equal(parts.mean(axis=0), phi) and parts.shape == (BACKGROUND_SIZE, base_n, 3)
    assert np.allclose(parts.sum(axis=1).mean(axis=0), fx - base, atol=1e-10)
    # an additive model: every permutation contributes w_f . (x - background_p), so the standard error has a closed form
    additive_parts = shapley(additive, point, bg100, three, np.random.default_rng(3))[3]
    per_background = np.stack([
        np.stack([(point[:, g] - row[:, g]).reshape(-1) @ additive_weights[np.arange(len(point) * 3).reshape(4, 3)[:, g].reshape(-1)]
                  for g in three], axis=0) for row in bg100])
    assert np.allclose(additive_parts, per_background, atol=1e-12)
    closed_form = per_background.std(axis=0, ddof=1) / math.sqrt(len(bg100))
    assert np.allclose(reliability(additive_parts)[0], closed_form, atol=1e-12)
    # the standard error shrinks with the permutation count (1/sqrt(P)) and covers the order-sampling error
    # against the exact value over the same backgrounds (it also contains the background spread, so it is never smaller)
    ses, rms_error = {}, {}
    for permutations in (10, 160):
        exact = exact_shapley(interacting, target, pool[:permutations], four)
        draws = [shapley(interacting, target, pool[:permutations], four, np.random.default_rng(2000 + s))
                 for s in range(60)]
        ses[permutations] = float(np.sqrt(np.mean([(reliability(d[3])[0] ** 2).mean() for d in draws])))
        rms_error[permutations] = float(np.sqrt(np.mean([((d[0] - exact) ** 2).mean() for d in draws])))
    assert ses[160] < ses[10] / 2.5, ses
    for permutations in (10, 160):
        assert rms_error[permutations] <= ses[permutations] * 1.25, (permutations, rms_error, ses)

    # (k) randomness: a case's stream depends on the case alone; the background has its own stream
    first = case_rng("AAA", 7).permutation(8)
    assert np.array_equal(first, case_rng("AAA", 7).permutation(8))
    assert not np.array_equal(first, case_rng("AAA", 8).permutation(8)) and         not np.array_equal(first, case_rng("BBB", 7).permutation(8))
    assert case_rng("AAA", 7).integers(1 << 62) != background_rng().integers(1 << 62)
    shuffle = np.random.default_rng(12).permutation(len(val))
    forward_run = explain_arrays(forward, fit_tensor=fit, validation_tensor=val, validation_symbols=names,
                                 validation_dates=dates, validation_rows=np.arange(len(val)),
                                 classes=classes, base_channels=["open", "high", "low"])
    shuffled_run = explain_arrays(forward, fit_tensor=fit, validation_tensor=val[shuffle],
                                  validation_symbols=[names[i] for i in shuffle], validation_dates=dates[shuffle],
                                  validation_rows=shuffle, classes=classes, base_channels=["open", "high", "low"])

    def by_case(run: dict) -> dict:
        return {(r["sample"]["symbol"], r["sample"]["anchor_row"]): r for r in run["records"]}

    common = by_case(forward_run).keys() & by_case(shuffled_run).keys()
    assert len(common) >= 4  # same data in another order selects (almost) the same cases
    for key in common:
        assert by_case(forward_run)[key]["outputs"] == by_case(shuffled_run)[key]["outputs"], key
    assert forward_run["pdp"]["channels"] == shuffled_run["pdp"]["channels"]  # background does not depend on the cases
    assert forward_run["rng_policy"] == RNG_POLICY and forward_run["explanation_type"] == EXPLANATION_TYPE
    # reliability fields are present, finite, and summarised
    stability = forward_run["stability"]
    assert stability["permutations_used"] == BACKGROUND_SIZE
    all_se = [c["standard_error"] for r in forward_run["records"] for o in r["outputs"] for c in o["contributions"]]
    assert all(se >= 0.0 for se in all_se) and stability["max_standard_error"] == max(all_se)
    assert stability["max_half_delta"] == max(r["stability_half_delta"] for r in forward_run["records"])
    assert "stability" not in explain_arrays(
        forward, fit_tensor=fit[:1], validation_tensor=val, validation_symbols=names, validation_dates=dates,
        validation_rows=np.arange(len(val)), classes=classes, base_channels=["a", "b", "c"])  # one permutation: no spread
    one = explain_arrays(forward, fit_tensor=fit[:1], validation_tensor=val, validation_symbols=names,
                         validation_dates=dates, validation_rows=np.arange(len(val)), classes=classes,
                         base_channels=["a", "b", "c"])
    assert all("standard_error" not in c and "stability_half_delta" not in r for r in one["records"]
               for o in r["outputs"] for c in o["contributions"])
    _assert_finite(forward_run)

    # (l) out-of-support rate: boundaries are inside, one step beyond is outside; the unshifted background is inside
    flat = np.zeros((5, 2, 2))
    _, outside = partial_dependence(lambda t: np.zeros((len(t), 1)), flat, np.array([0]),
                                    np.array([0.0, 0.5, 1.0, 1.0001, -2.0]), (-1.0, 1.0))
    assert outside.tolist() == [0.0, 0.0, 0.0, 1.0, 1.0]
    half_out = flat.copy()
    half_out[:2, :, 0] = 0.8  # two of the five windows are shifted out by +0.5
    _, outside = partial_dependence(lambda t: np.zeros((len(t), 1)), half_out, np.array([0]), np.array([0.5]), (-1.0, 1.0))
    assert outside.tolist() == [0.4]
    for channel in forward_run["pdp"]["channels"]:
        rates = channel["out_of_support_rate"]
        assert len(rates) == PDP_GRID_POINTS and rates[PDP_GRID_POINTS // 2] == 0.0 and all(0.0 <= r <= 1.0 for r in rates)
    print("explain.py selfcheck: OK (12 groups passed)")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Offline SHAP/PDP explanation helpers.")
    parser.add_argument("--selfcheck", action="store_true")
    parser.add_argument("--emit-parity", type=Path, default=None,
                        help="Write the C#/Python parity fixture (Tests/ModelExplanationParity.json).")
    options = parser.parse_args()
    if options.emit_parity is not None:
        text = json.dumps(parity_fixture(), indent=2, allow_nan=False)
        options.emit_parity.write_bytes((text + chr(10)).encode("utf-8"))
    elif options.selfcheck:
        _run_selfcheck()
    else:
        raise SystemExit("use --selfcheck or --emit-parity PATH")
