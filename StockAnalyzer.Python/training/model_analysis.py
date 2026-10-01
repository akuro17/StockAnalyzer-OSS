"""D09 causal regimes and explicitly typed, generation-bound model analysis.

Fit populations are sample anchors, never repeated bars from overlapping windows.
All analysis runs offline, before the recoverable release publication transaction.
"""
from __future__ import annotations

import hashlib
import json
import time
from pathlib import Path

import numpy as np

import dataset as ds
import fixed_scaler
import checkpoints

SCHEMA_VERSION = 1
REFERENCE_KEY = "com.stockanalyzer.analysis.ref"
K = 3
VOLATILITY_BARS = 20
REPEATS = 10
SEED = 42
N_INIT = 10
MAX_ITER = 300
TOL = 1e-4
STANDARDIZATION_DDOF = 0
FEATURE_ORDER = ["one_bar_log_return", "realized_volatility_20"]


def channel_names(metadata: dict[str, str]) -> tuple[list[str], list[str]]:
    spec = json.loads(metadata["feature_spec"]) if "feature_spec" in metadata else None
    lags = json.loads(metadata.get("lags", "[]"))
    if metadata["feature_mode"] in ("ohlcv_minmax", ds.COMPOSED_FEATURES_MODE):
        expanded = fixed_scaler.identities(metadata["feature_mode"], spec, tuple(lags))
        base = [name.removeprefix("lag0:") for name in expanded[:len(expanded) // (1 + len(lags))]]
    else:
        base = metadata["channel_order"].split(",")
        expanded = [f"lag{lag}:{name}" for lag in (0, *lags) for name in base]
    if len(expanded) != int(metadata["channels"]) or len(set(expanded)) != len(expanded):
        raise ValueError("analysis channel order does not match the model tensor")
    return base, expanded


def vectors(ohlcv: np.ndarray, anchors: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """Match the existing ML float32 one-bar return boundary; reject invalid windows."""
    returns = ds.compute_log_returns(ohlcv).reshape(-1).astype(np.float32).astype(np.float64)
    result, retained = [], []
    for anchor in anchors:
        a = int(anchor)
        if a < VOLATILITY_BARS:
            continue
        closes = ohlcv[a - VOLATILITY_BARS:a + 1, ds.CLOSE]
        if not np.isfinite(closes).all() or (closes <= 0).any():
            continue
        recent = returns[a - VOLATILITY_BARS + 1:a + 1]
        if not np.isfinite(recent).all():
            continue
        result.append((returns[a], float(np.std(recent, ddof=0))))
        retained.append(a)
    return np.asarray(result, dtype=np.float64).reshape(-1, 2), np.asarray(retained, dtype=np.int64)


def period(symbol: str, kind: str, dates: np.ndarray, anchors: np.ndarray) -> dict:
    a = np.asarray(anchors, dtype=np.int64)
    d = np.asarray(dates).astype("datetime64[D]")
    if not len(a) or (a < 0).any() or (a >= len(d)).any() or np.any(a[1:] <= a[:-1]):
        raise ValueError("period anchors must be nonempty, distinct and chronological")
    selected = d[a]
    if np.isnat(selected).any() or np.any(selected[1:] <= selected[:-1]):
        raise ValueError("period dates must be chronological")
    revision = hashlib.sha256(np.ascontiguousarray(selected.astype("<i8")).tobytes()).hexdigest()
    return dict(symbol=symbol, kind=kind, anchor_start=str(selected[0]),
                anchor_end=str(selected[-1]), sample_count=len(a), anchor_revision=revision)


def fit_regime(training_vectors: np.ndarray, fit_periods: list[dict]) -> dict:
    x = np.asarray(training_vectors, dtype=np.float64)
    state = dict(algorithm="kmeans", k=K, init="k-means++", n_init=N_INIT,
                 seed=SEED, max_iter=MAX_ITER, tol=TOL, solver="lloyd",
                 volatility_bars=VOLATILITY_BARS, standardization_ddof=STANDARDIZATION_DDOF,
                 feature_order=FEATURE_ORDER, fit_sample_count=len(x), fit_periods=fit_periods,
                 fit_data_revision=hashlib.sha256(np.ascontiguousarray(x, dtype="<f8").tobytes()
                     + checkpoints.canonical(fit_periods).encode()).hexdigest())
    if x.ndim != 2 or x.shape[1] != 2 or not np.isfinite(x).all():
        raise ValueError("regime requires finite two-axis training observations")
    if len(x) < K or len(np.unique(x, axis=0)) < K:
        return dict(state, status="insufficient_training_observations")
    from sklearn.cluster import KMeans
    mean, std = x.mean(axis=0), x.std(axis=0, ddof=STANDARDIZATION_DDOF)
    constant = np.all(x == x[0], axis=0)
    mean[constant], std[constant] = x[0, constant], 0.0
    divisor = np.where(std == 0, 1.0, std)
    fitted = KMeans(n_clusters=K, init=state["init"], n_init=N_INIT,
                    random_state=SEED, max_iter=MAX_ITER, tol=TOL,
                    algorithm=state["solver"]).fit((x - mean) / divisor)
    unscaled = fitted.cluster_centers_ * divisor + mean
    order = sorted(range(K), key=lambda i: (unscaled[i, 0], unscaled[i, 1], i))
    if not np.isfinite(fitted.cluster_centers_).all():
        raise ValueError("nonfinite regime centroids")
    return dict(state, status="available", mean=mean.tolist(), std=std.tolist(),
                centroids=fitted.cluster_centers_[order].tolist(),
                unscaled_centroids=unscaled[order].tolist(), original_components=order)


def gain_importance(gains: np.ndarray, window: int, expanded: list[str], base: list[str],
                    selected_iteration: int) -> dict:
    gain = np.asarray(gains, dtype=np.float64)
    if gain.shape != (window * len(expanded),) or not np.isfinite(gain).all() or (gain < 0).any():
        raise ValueError("selected-iteration gain disagrees with the input tensor")
    grouped = gain.reshape(window, -1).sum(axis=0).reshape(-1, len(base)).sum(axis=0)
    total = float(grouped.sum())
    state = dict(method="split_gain", unit="relative_gain_percent", population="training",
                 selected_iteration=selected_iteration)
    if total == 0:
        return dict(state, status="zero_total_gain", values=[])
    return dict(state, status="available", values=[dict(channel=name, value=float(100 * g / total))
                                                    for name, g in zip(base, grouped)])


def permutation_importance(predict, x: np.ndarray, y: np.ndarray, base: list[str],
                           *, deadline: float | None = None) -> dict:
    x = np.asarray(x)
    y = np.asarray(y).reshape(-1)
    if x.ndim != 3 or not len(x) or len(x) != len(y) or x.shape[2] % len(base):
        raise ValueError("permutation requires the fixed nonempty validation population")
    def accuracy(tensor):
        if deadline is not None and time.monotonic() >= deadline:
            raise TimeoutError("model analysis exceeded the training run budget")
        scores = np.asarray(predict(tensor))
        if scores.shape != (len(y), len(ds.CLASS_LABELS)) or not np.isfinite(scores).all():
            raise ValueError("invalid classification scores during permutation")
        return float((scores.argmax(axis=1) == y).mean())
    baseline = accuracy(x)
    rng = np.random.default_rng(SEED)
    shuffled = x.copy()
    values = []
    for b, name in enumerate(base):
        indices = np.arange(b, x.shape[2], len(base))
        group = x[:, :, indices]
        drops = []
        for _ in range(REPEATS):
            shuffled[:] = x
            order = rng.permutation(len(x))
            shuffled[:, :, indices] = group[order]
            drops.append(100 * (baseline - accuracy(shuffled)))
        values.append(dict(channel=name, value=float(np.mean(drops))))
    return dict(status="available", method="grouped_permutation", unit="accuracy_percentage_points",
                population="inner_validation", metric="classification_accuracy", repeats=REPEATS,
                seed=SEED, baseline_accuracy=baseline, values=values)


def validate_population(selected: dict, limits: dict) -> None:
    samples = sum(len(fit) + len(validation) for _, _, _, fit, validation in selected.values())
    elements = sum((len(fit) + len(validation)) * x.shape[1] * x.shape[2]
                   for x, _, _, fit, validation in selected.values())
    if samples > limits["max_samples"] or elements > limits["max_tensor_elements"]:
        raise ValueError("selected analysis population exceeds the resolved training resource limits")


def build(cfg, model_path: Path, train_symbols, train_dates, full_symbols, full_dates,
          *, predict, checkpoint_root: Path, deadline: float, resource_limits: dict) -> dict:
    import onnxruntime as ort
    metadata = ort.InferenceSession(str(model_path), providers=["CPUExecutionProvider"]).get_modelmeta().custom_metadata_map
    base, expanded = channel_names(metadata)
    selected = ds.independent_fold_data(
        train_symbols, train_dates, feature_mode=cfg.feature_mode, window=cfg.window_size,
        horizon=cfg.horizon, threshold=ds.DEFAULT_THRESHOLD, n_splits=cfg.n_splits,
        gap=cfg.gap, fold_index=cfg.n_splits - 1, feature_spec=cfg.feature_spec,
        target_type=cfg.target_type, indicator_export_paths=cfg.indicator_channel_export_paths,
        inner=True, fixed_zscore=cfg.fixed_zscore, lags=cfg.lags)
    validate_population(selected, resource_limits)
    periods, fit_periods, observations, xs, ys = [], [], [], [], []
    for sym in train_symbols:  # same pooling order as the trainer
        x, y, anchors, fit, validation = selected[sym]
        periods += [period(sym, "train", train_dates[sym], anchors[fit]),
                    period(sym, "validation", train_dates[sym], anchors[validation])]
        vector, retained = vectors(train_symbols[sym], anchors[fit])
        observations.append(vector)
        if len(retained):
            fit_periods.append(period(sym, "train", train_dates[sym], retained))
        xs.append(x[validation]); ys.append(y[validation])
        if cfg.oos_tail_days:
            _, _, tail, tail_dates = ds.oos_split(full_symbols[sym], full_dates[sym], cfg.oos_tail_days,
                                                common_end=ds.common_end_date(full_dates))
            _, _, oos_anchors = ds.build_dataset(
                tail, cfg.feature_mode, cfg.window_size, cfg.horizon, ds.DEFAULT_THRESHOLD,
                feature_spec=cfg.feature_spec, target_type=cfg.target_type, return_anchor_rows=True,
                dates=tail_dates, indicator_export_path=(cfg.indicator_channel_export_paths or {}).get(sym),
                fixed_zscore=cfg.fixed_zscore, lags=cfg.lags)
            periods.append(period(sym, "oos", tail_dates, oos_anchors))
    if cfg.target_type == "regression":
        importance = dict(status="regression_metric_not_adopted", method="unavailable", unit="none",
                          population="none", values=[])
    elif cfg.framework == "lightgbm":
        import lightgbm as lgb
        state, blob = checkpoints.checked_blob(checkpoint_root / f"fold_{cfg.n_splits - 1}" / "best.json")
        if state.get("exported_model_sha256") != checkpoints.hash_file(model_path):
            raise ValueError("selected native gain checkpoint does not match the exported model")
        booster = lgb.Booster(model_file=str(blob))
        iteration = int(state["completed_iteration"])
        importance = gain_importance(booster.feature_importance("gain", iteration=iteration),
                                     cfg.window_size, expanded, base, iteration)
    else:
        importance = permutation_importance(predict, np.concatenate(xs), np.concatenate(ys), base, deadline=deadline)
    revision = checkpoints.source_revision(full_symbols, full_dates, cfg.indicator_channel_export_paths)
    return dict(schema_version=SCHEMA_VERSION, model_sha256=checkpoints.hash_file(model_path),
                data_revision=revision, feature_contract_hash=fixed_scaler.contract_hash(metadata),
                timeframe=cfg.timeframe, window_size=cfg.window_size, ordered_channels=expanded,
                base_channels=base, periods=periods, regime=fit_regime(np.concatenate(observations), fit_periods),
                importance=importance)
