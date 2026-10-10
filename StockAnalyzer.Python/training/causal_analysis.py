"""T18 offline causal-style analysis: a cross-fitted partially linear (DML) effect estimate with an explicit assumption list.

Approved decisions (2026-10-10, ``Y:\\Temp\\sa_decision_options_onnx_research_t17_t22.md``): 18-1 (no new package, scikit-learn),
C01..C19 = star (C02 A, C03 A, C04 A, C05 A, C06 A, C07 A, C08 A, C09 A, C10 A, C16 A, C17 A), K01..K20 = star:

* K01 estimand: theta of ``Y = theta*T + g(X) + e`` -- the change of the forward log return per +1 SD of the treatment.
* K02 treatment: a price-derived feature (``log_return_1|5|20``, ``realized_volatility_20``; the definitions of
  ``model_analysis`` / ``dataset``) or an external per-symbol parquet written by the C# indicator export
  (``date`` + one column, the format of ``dataset._build_indicator_columns``). The formula of an external indicator is the C#
  shipped one; this module only reads its values.
* K03 one treatment per run. K04 standardized with the mean / SD of the cross-fit TRAINING part only.
* K05 outcome: ``dataset.make_regression_target`` over ``--horizon`` bars. K06 confounders: the other registry features
  (past-only, measured at the anchor Close). K07 treatment, confounders: rows <= anchor; outcome: rows after the anchor.
* K08 nuisance learner: ``GradientBoostingRegressor`` (table ``NUISANCE_PARAMS``). K09 per-symbol time-contiguous blocks;
  K10 purge = ``dataset.resolve_purge_gap(LOOKBACK_BARS + 1, horizon)`` bars either side of an evaluation block.
* K11 every bar is an anchor (overlapping outcomes -> clustered standard error). K12 cluster-robust influence-function SE,
  clusters = (symbol, calendar quarter), normal CI (90 / 95 / 99 %). K13 treatment and outcome are centered with the TRAINING
  symbol means, then pooled; per-symbol estimates are reported beside the pooled one.
* K14 placebos (reported, never judged): treatment shuffled inside (symbol, quarter); treatment at ``t + horizon`` instead of ``t``.
* K15 below ``--min-samples`` anchors or ``--min-fold-train`` training rows -> ``status = insufficient_data``, no estimate.
* K16 incomplete anchors are dropped and counted per reason (no fill). K17 the out-of-sample tail is excluded
  (``--oos-tail-days`` is REQUIRED so that using all rows is an explicit choice).
* K18 the keys never say ``causal_effect``: the value is ``estimated_effect_under_assumptions``. K19 the assumptions are listed
  with the ones that cannot be tested. K20 ``report.json`` (manifest, written last) and ``report.csv``.

The estimate is an association parameter identified only under the listed assumptions. It is NOT proof of causation. The tool
never touches an ONNX model and nothing here is read by prediction, rendering or a view model.

Usage::

    python causal_analysis.py --oos-tail-days 0 --symbols AAPL,MSFT --treatment log_return_1 --horizon 5
    python causal_analysis.py --oos-tail-days 0 --purge-scope global      # calendar blocks and a cross-symbol purge (I1; default: symbol)
    python causal_analysis.py --coverage-study --reps 200      # measurement only, no market data (H1)
    python causal_analysis.py --selfcheck

H1 (2026-10-10, WR1 A / WR3 A / WR2 A part / DF07..DF13, all additive): beside the headline CR0 + normal interval every ``ok`` estimate
carries ``small_sample_interval`` (CR1 ``G/(G-1)`` + t(G-1)); ``treatment_scale_by_fold`` / ``scale_ratio_max_min`` record the fold-training
SD (so theta is "per +1 fold-training SD", folds pooled with weights proportional to 1/sigma_k^2); ``fold_diagnostics`` give the evaluation
calendar span per symbol and ``cross_symbol_training_overlap`` (share of the OTHER symbols' training rows inside the evaluating block's span
widened by the purge, counted in bars of the union date axis); ``--nuisance-estimators`` / ``--nuisance-depth`` expose two learner settings.
No headline number, CSV column or default changed.

CL (2026-10-10, CL1 A / CL2 A / CL3 A, additive): every ``ok`` estimate also carries ``time_clustered_interval`` -- the same estimating
function clustered by calendar quarter ALONE (CR1 + t(G - 1)), so a shock shared by all symbols in one quarter falls into one cluster. It is a
side interval: the headline, the CSV and the defaults are unchanged, the ``min_clusters`` rule is not applied to it (``n_clusters`` and the few-clusters
notice are always stated; fewer than 2 quarters -> ``insufficient_time_clusters``). ``--coverage-study`` measures it beside the other two intervals.
"""

from __future__ import annotations

import argparse
import csv
import datetime
import hashlib
import json
import math
import os
import shutil
import sys
import time
from dataclasses import dataclass
from pathlib import Path
from statistics import NormalDist
from typing import Dict, List, Tuple

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import checkpoints  # noqa: E402
import dataset as ds  # noqa: E402
import model_analysis  # noqa: E402  (VOLATILITY_BARS and the realized-volatility definition)

SCHEMA_VERSION: int = 1
TOOL: str = "causal_analysis.py"
TOOL_VERSION: str = "1"
OUT_DIR: Path = ds.DATA_ROOT / "TrainingArtifacts" / "Causal"
REPORT_JSON: str = "report.json"
REPORT_CSV: str = "report.csv"
INCOMING_SUFFIX: str = ".incoming"
CSV_COLUMNS: Tuple[str, ...] = ("estimand", "treatment", "outcome", "horizon", "n", "theta", "se", "ci_low", "ci_high",
                                "ci_level", "folds", "status", "notices")
OUTCOME_NAME: str = "forward_log_return"

# --- Feature registry (K02 / K06): every value uses rows <= the anchor row only -------------------------------------
RETURN_BARS: Tuple[int, ...] = (1, 5, 20)
VOLATILITY_BARS: int = model_analysis.VOLATILITY_BARS          # population std (ddof 0) of the last 20 one-bar log returns
LOOKBACK_BARS: int = max(max(RETURN_BARS), VOLATILITY_BARS)    # rows before the anchor that any feature reads
FEATURE_NAMES: Tuple[str, ...] = (
    "log_return_1", "log_return_5", "log_return_20", "realized_volatility_20", "log_volume_change_1",
)
TREATMENTS: Tuple[str, ...] = ("log_return_1", "log_return_5", "log_return_20", "realized_volatility_20", "external")
EXTERNAL: str = "external"
DEFAULT_TREATMENT: str = "log_return_1"
DEFAULT_EXTERNAL_COLUMN: str = "channel_0"

# --- Estimator tables (named; no empirical basis -- sklearn defaults made explicit) ---------------------------------
NUISANCE_PARAMS: Dict[str, object] = dict(n_estimators=100, learning_rate=0.1, max_depth=3, subsample=1.0, min_samples_leaf=1)
# DF13 A: only these two entries of the table are exposed (CLI + env); bands are planning values with no empirical basis.
NUISANCE_ESTIMATORS_ENV: str = "SA_CAUSAL_NUISANCE_ESTIMATORS"
NUISANCE_DEPTH_ENV: str = "SA_CAUSAL_NUISANCE_DEPTH"
NUISANCE_ESTIMATORS_BAND: Tuple[int, int] = (10, 1000)
NUISANCE_DEPTH_BAND: Tuple[int, int] = (1, 8)
DEFAULT_NUISANCE_ESTIMATORS: int = checkpoints.resolve_band(NUISANCE_ESTIMATORS_ENV, int(NUISANCE_PARAMS["n_estimators"]),
                                                            NUISANCE_ESTIMATORS_BAND, (50, 500))
DEFAULT_NUISANCE_DEPTH: int = checkpoints.resolve_band(NUISANCE_DEPTH_ENV, int(NUISANCE_PARAMS["max_depth"]), NUISANCE_DEPTH_BAND, (2, 5))
DEFAULT_FOLDS: int = 5
MAX_FOLDS: int = 20                       # = dataset.CPCV_MAX_GROUPS
CI_LEVELS: Tuple[float, ...] = (0.90, 0.95, 0.99)
DEFAULT_CI_LEVEL: float = 0.95
DEFAULT_MIN_SAMPLES: int = 1000           # K15 (candidates 1000 / 5000)
DEFAULT_MIN_FOLD_TRAIN: int = 200         # K15 (candidates 200 / 500)
MIN_CLUSTERS_ENV: str = "SA_CAUSAL_MIN_CLUSTERS"
# Z01 (user value 30, 2026-10-10): fewer effective clusters than this -> no standard error / interval is computed. Z06: hard band 2..1000.
DEFAULT_MIN_CLUSTERS: int = checkpoints.resolve_band(MIN_CLUSTERS_ENV, 30, (2, 1000), (10, 200))
MIN_CLUSTERS_BAND: Tuple[int, int] = (2, 1000)
MIN_CLUSTERS_PER_SYMBOL_ENV: str = "SA_CAUSAL_MIN_CLUSTERS_PER_SYMBOL"
# The per-symbol estimates have their own minimum (user instruction 2026-10-10); same band and starting value as the pooled one.
DEFAULT_MIN_CLUSTERS_PER_SYMBOL: int = checkpoints.resolve_band(MIN_CLUSTERS_PER_SYMBOL_ENV, 30, (2, 1000), (10, 200))
# User instruction 2026-10-10: an interval computed from fewer clusters than this ALWAYS carries the warning below in its own `notices`.
CLUSTER_WARNING_THRESHOLD: int = 30
FEW_CLUSTERS_CODE: str = "few_clusters_interval_may_be_optimistic"
FEW_CLUSTERS_MESSAGE: str = ("Few clusters: the standard error and the confidence interval may be underestimated (optimistic) "
                             "because the cluster-robust estimate rests on a small number of clusters.")
STATUS_INSUFFICIENT_CLUSTERS: str = "insufficient_clusters"
STATUS_INSUFFICIENT_TIME_CLUSTERS: str = "insufficient_time_clusters"
TIME_CLUSTER_DEFINITION: str = "calendar_quarter"      # CL1 A: the side interval clusters by the quarter alone (the headline: symbol x quarter)
TIME_CLUSTER_NOTE: str = ("Clusters are calendar quarters only, so a shock common to all symbols in a quarter is one cluster; there are few of them "
                          "(compare n_clusters), so this interval is itself unstable. It is shown beside the headline interval, never instead of it.")
DEFAULT_SEED: int = 42                    # C08
COVERAGE_STREAM: int = 47                 # SeedSequence namespace of the coverage study
COVERAGE_THETA: float = 0.1               # Q1 A: the true effect per +1 SD of the treatment
COVERAGE_BARS_PER_CLUSTER: int = 63       # one symbol x one calendar quarter
COVERAGE_GRID: Tuple[Tuple[int, int], ...] = ((4, 5), (5, 6), (5, 10))   # (symbols, quarters): G = 20, 30, 50 clusters
COVERAGE_DGPS: Tuple[str, ...] = ("iid", "ar1_within_symbol", "common_market_shock")
COVERAGE_AR_RHO: float = 0.5              # DGP-2: stationary unit-variance AR(1) for v and eps inside a symbol
COVERAGE_COMMON_SHARE: float = 0.5        # DGP-3: share of the variance of v and of eps that all symbols share
COVERAGE_TREATMENT_LOADING: float = 0.5   # T = loading * X1 + v
COVERAGE_TREATMENT_SD: float = math.sqrt(COVERAGE_TREATMENT_LOADING ** 2 + 1.0)   # SD of T (Var v = 1); T_s = T / SD
COVERAGE_CONFOUNDERS: int = 3
DEFAULT_COVERAGE_REPS: int = 200          # DF11 A (the final run uses 1000; the selfcheck smoke test uses 10)
COVERAGE_REPS_BAND: Tuple[int, int] = (1, 10_000)
COVERAGE_MIN_CLUSTERS: int = 2            # the study asks for the interval at every G (the production minimum is 30)
COVERAGE_STATEMENT: str = ("valid for these data-generating processes only: planning values with no empirical basis (cluster length, "
                           "rho, common-shock share, theta); not a statement about market data")
PURGE_SCOPES: Tuple[str, ...] = ("symbol", "global")   # WR2 B / DF16..DF18: where the cross-fit blocks and the purge are defined
DEFAULT_PURGE_SCOPE: str = "symbol"
SCHEME_BY_SCOPE: Dict[str, str] = dict(symbol="per_symbol_contiguous_blocks", **{"global": "union_date_axis_contiguous_blocks"})
SMALL_SAMPLE_METHOD: str = "CR1_t"        # WR1 A / DF09 A: se * sqrt(G / (G - 1)) and the t quantile with G - 1 degrees of freedom
ESTIMATE_SCALE: Dict[str, str] = dict(     # WR3 A: the unit of the estimate, stated beside it (INTERPRETATION is left byte-identical)
    unit="effect_per_plus_1_sd_of_the_fold_training_treatment",
    pooling=("each cross-fit fold divides the treatment by the SD of its own training part (K04); the pooled estimate sums "
             "t_res * y_res / sum t_res^2 over all folds, so a fold weighs in with its sum of squared scaled treatment residuals, "
             "proportional to 1 / sigma_k^2 for equal raw residual variance"),
    record="treatment_scale_by_fold and scale_ratio_max_min in every estimate; no threshold is applied")
PLACEBO_STREAM: int = 31                  # SeedSequence namespace of the shuffle placebo
DEFAULT_MAX_SYMBOLS: int = 12
MAX_SECONDS_ENV: str = "SA_CAUSAL_MAX_SECONDS"
MAX_SECONDS: int = checkpoints.resolve_band(MAX_SECONDS_ENV, 3600, (1, 86_400), (60, 7_200))
EXIT_OK, EXIT_FAILED, EXIT_PREREQUISITE = 0, 1, 3

NOTICES: Tuple[str, ...] = ("not_proven_causal", "assumptions_untestable")
NOTICE_SYNTHETIC: str = "synthetic_data_not_observed"             # the input was synthetic and --allow-synthetic was given
NOTICE_NO_INTERVAL: str = "insufficient_clusters_no_interval"   # Z03 A: the point estimate is kept, its uncertainty is not claimed
INTERPRETATION: str = (
    "theta is the parameter of a partially linear model estimated by cross-fitted double machine learning. It is an "
    "association measure that equals a causal effect only if every listed assumption holds; the untestable ones are not "
    "verified here. It is not proof of causation."
)
ASSUMPTIONS: Tuple[Tuple[str, str, bool], ...] = (
    ("conditional_ignorability", "No unmeasured common cause of the treatment and the outcome remains once the listed confounders "
                                "are held fixed.", False),
    ("overlap", "The treatment varies given the confounders (see treatment_residual_variance_ratio).", True),
    ("no_interference", "The outcome of one symbol is not changed by the treatment of another symbol or of another period.", False),
    ("temporal_order", "Treatment and confounders use rows up to the anchor; the outcome uses rows after it.", True),
    ("partial_linearity", "The treatment enters the outcome linearly after the confounders are removed (K01).", False),
)


class PrerequisiteError(Exception):
    """An input is missing or unusable (exit code 3)."""


# --- Features --------------------------------------------------------------------------------------------------------


def compute_features(ohlcv: np.ndarray) -> Dict[str, np.ndarray]:
    """Registry features per row; NaN where a feature is not defined (warm-up, non-positive price or volume)."""
    arr = np.asarray(ohlcv, dtype=np.float64)
    n = arr.shape[0]
    close, volume = arr[:, ds.CLOSE], arr[:, ds.VOLUME]
    log_close = np.full(n, np.nan)
    positive = np.isfinite(close) & (close > 0.0)
    log_close[positive] = np.log(close[positive])

    def log_return(bars: int) -> np.ndarray:
        out = np.full(n, np.nan)
        if n > bars:
            out[bars:] = log_close[bars:] - log_close[:-bars]
        return out

    features = {f"log_return_{bars}": log_return(bars) for bars in RETURN_BARS}
    one_bar = features["log_return_1"]
    vol = np.full(n, np.nan)
    if n > VOLATILITY_BARS:
        windows = np.lib.stride_tricks.sliding_window_view(one_bar[1:], VOLATILITY_BARS)  # row t uses returns t-19..t
        vol[VOLATILITY_BARS:] = np.std(windows, axis=1, ddof=0)
    features["realized_volatility_20"] = vol
    log_volume = np.full(n, np.nan)
    ok = np.isfinite(volume) & (volume > 0.0)
    log_volume[ok] = np.log(volume[ok])
    change = np.full(n, np.nan)
    change[1:] = log_volume[1:] - log_volume[:-1]
    features["log_volume_change_1"] = change
    return features


def forward_log_return(ohlcv: np.ndarray, horizon: int) -> np.ndarray:
    """``dataset.make_regression_target`` for every row; NaN when the future row is missing or a price is not positive."""
    close = np.asarray(ohlcv, dtype=np.float64)[:, ds.CLOSE]
    out = np.full(close.shape[0], np.nan)
    if close.shape[0] > horizon:
        anchor, future = close[:-horizon], close[horizon:]
        ok = np.isfinite(anchor) & np.isfinite(future) & (anchor > 0.0) & (future > 0.0)
        idx = np.nonzero(ok)[0]
        out[idx] = np.log(future[idx] / anchor[idx])
    return out


def confounder_names(treatment_feature: str | None) -> Tuple[str, ...]:
    """K06: the registry features except the treatment itself."""
    return tuple(name for name in FEATURE_NAMES if name != treatment_feature)


def load_external_series(directory: Path, symbol: str, dates: np.ndarray, column: str) -> np.ndarray:
    """Values of *column* of ``<directory>/<symbol>.parquet`` aligned to *dates*; NaN for a date the export lacks."""
    import polars as pl

    path = Path(directory) / f"{symbol}.parquet"
    if not path.is_file():
        raise PrerequisiteError(f"external treatment file not found: {path}")
    frame = pl.read_parquet(path)
    if "date" not in frame.columns or column not in frame.columns:
        raise PrerequisiteError(f"{path.name} needs the columns 'date' and {column!r}")
    exported = frame.get_column("date").to_numpy().astype("datetime64[D]")
    if len(set(exported.tolist())) != exported.shape[0]:
        raise PrerequisiteError(f"{path.name} has a duplicate date")
    lookup = dict(zip(exported.tolist(), frame.get_column(column).to_numpy().astype(np.float64).tolist()))
    return np.asarray([lookup.get(d, np.nan) for d in np.asarray(dates).astype("datetime64[D]").tolist()], dtype=np.float64)


# --- Samples ------------------------------------------------------------------------------------------------------------


@dataclass
class Samples:
    """Pooled anchors, symbol-major and ascending in time inside a symbol."""

    sym: np.ndarray       # int symbol code
    row: np.ndarray       # source row index (the anchor)
    cluster: np.ndarray   # int code of (symbol, calendar quarter)
    X: np.ndarray         # (n, p) confounders
    T: np.ndarray         # treatment at the anchor
    Y: np.ndarray         # forward log return
    T_future: np.ndarray  # treatment at anchor + horizon (placebo); NaN where unavailable
    quarter: np.ndarray   # calendar quarter index (year * 4 + (month - 1) // 3)
    date: np.ndarray      # anchor calendar date (datetime64[D])
    date_pos: np.ndarray  # position of the date on the sorted union of all anchor dates (DF17 A: the bar axis of the overlap diagnostic)
    symbols: List[str]
    confounders: Tuple[str, ...]


def build_samples(symbols: Dict[str, np.ndarray], dates: Dict[str, np.ndarray], *, treatment: str, horizon: int,
                  external_dir: Path | None, external_column: str) -> Tuple[Samples, dict]:
    """Anchors with every input defined (K16: dropped and counted, never filled)."""
    names = sorted(symbols)
    conf = confounder_names(None if treatment == EXTERNAL else treatment)
    dropped = dict(warmup=0, missing_future=0, undefined_feature=0, undefined_treatment=0, undefined_outcome=0)
    parts = []
    for code, name in enumerate(names):
        arr, d = symbols[name], np.asarray(dates[name]).astype("datetime64[D]").reshape(-1)
        n = arr.shape[0]
        feats = compute_features(arr)
        y = forward_log_return(arr, horizon)
        t_full = (load_external_series(external_dir, name, d, external_column) if treatment == EXTERNAL else feats[treatment])
        t_future = np.full(n, np.nan)
        if n > horizon:
            t_future[:-horizon] = t_full[horizon:]
        rows = np.arange(n)
        warm = rows < LOOKBACK_BARS
        no_future = rows > n - 1 - horizon
        x = np.column_stack([feats[c] for c in conf]) if conf else np.empty((n, 0))
        bad_x = ~np.isfinite(x).all(axis=1) if conf else np.zeros(n, dtype=bool)
        bad_t = ~np.isfinite(t_full)
        bad_y = ~np.isfinite(y)
        remaining = np.ones(n, dtype=bool)
        for key, mask in (("warmup", warm), ("missing_future", no_future), ("undefined_feature", bad_x),
                          ("undefined_treatment", bad_t), ("undefined_outcome", bad_y)):
            hit = mask & remaining
            dropped[key] += int(hit.sum())
            remaining &= ~mask
        keep = np.nonzero(remaining)[0]
        if keep.size == 0:
            continue
        months = d[keep].astype("datetime64[M]")
        years = d[keep].astype("datetime64[Y]").astype(int) + 1970
        quarter = years * 4 + (months.astype(int) % 12) // 3
        parts.append((code, keep, quarter, x[keep], t_full[keep], y[keep], t_future[keep], d[keep]))
    if not parts:
        raise PrerequisiteError("no anchor has every input defined")
    sym = np.concatenate([np.full(p[1].shape[0], p[0], dtype=np.int64) for p in parts])
    quarter = np.concatenate([p[2] for p in parts]).astype(np.int64)
    _, cluster = np.unique(sym * 1_000_000 + quarter, return_inverse=True)
    date = np.concatenate([p[7] for p in parts]).astype("datetime64[D]")
    _, date_pos = np.unique(date, return_inverse=True)
    samples = Samples(
        sym=sym, row=np.concatenate([p[1] for p in parts]).astype(np.int64), cluster=cluster.astype(np.int64),
        X=np.concatenate([p[3] for p in parts]), T=np.concatenate([p[4] for p in parts]),
        Y=np.concatenate([p[5] for p in parts]), T_future=np.concatenate([p[6] for p in parts]), quarter=quarter,
        date=date, date_pos=date_pos.astype(np.int64), symbols=names, confounders=conf)
    return samples, dropped


# --- Cross-fitting -------------------------------------------------------------------------------------------------------


def assign_folds(sym: np.ndarray, folds: int) -> np.ndarray:
    """K09: per symbol, ``folds`` time-contiguous blocks of (nearly) equal anchor count; samples are time-ordered per symbol."""
    fold = np.empty(sym.shape[0], dtype=np.int64)
    for code in np.unique(sym):
        idx = np.nonzero(sym == code)[0]
        for k, chunk in enumerate(np.array_split(np.arange(idx.shape[0]), folds)):
            fold[idx[chunk]] = k
    return fold


def assign_folds_global(date: np.ndarray, folds: int) -> np.ndarray:
    """DF16 A (``--purge-scope global``): the sorted unique anchor dates of all symbols form one axis, split into ``folds`` consecutive
    intervals of (nearly) equal day count; every row takes the interval of its date. All symbols share the same calendar blocks."""
    position = np.unique(date, return_inverse=True)[1].astype(np.int64)
    interval = np.empty(int(position.max()) + 1, dtype=np.int64)
    for k, chunk in enumerate(np.array_split(np.arange(interval.shape[0]), folds)):
        interval[chunk] = k
    return interval[position]


def training_mask_global(date_pos: np.ndarray, fold: np.ndarray, k: int, purge: int) -> np.ndarray:
    """Training rows of fold *k* under the global scope: every row of EVERY symbol whose union-axis position lies outside the evaluation
    interval widened by *purge* bars (DF17 A: bars of the union axis). A symbol without evaluation rows in the fold loses the rows inside
    that span too (DF18 A). All False when the fold is empty."""
    ev = fold == k
    if not ev.any():
        return np.zeros(fold.shape[0], dtype=bool)
    return (date_pos < int(date_pos[ev].min()) - purge) | (date_pos > int(date_pos[ev].max()) + purge)


def purge_bars(horizon: int) -> int:
    """K10: rows excluded from the training side either end of an evaluation block (one owner: dataset.resolve_purge_gap)."""
    return ds.resolve_purge_gap(LOOKBACK_BARS + 1, horizon, None)


def training_mask(sym: np.ndarray, row: np.ndarray, fold: np.ndarray, k: int, purge: int) -> np.ndarray:
    """Training rows of fold *k*: other folds, minus the rows within *purge* bars of the evaluation block of the same symbol."""
    keep = fold != k
    for code in np.unique(sym[fold == k]):
        in_sym = sym == code
        block = row[in_sym & (fold == k)]
        lo, hi = int(block.min()) - purge, int(block.max()) + purge
        keep &= ~(in_sym & (row >= lo) & (row <= hi))
    return keep


def nuisance_params(estimators: int = DEFAULT_NUISANCE_ESTIMATORS, depth: int = DEFAULT_NUISANCE_DEPTH) -> Dict[str, object]:
    """The nuisance learner table with the two exposed entries replaced (DF13 A); the defaults reproduce ``NUISANCE_PARAMS``."""
    return dict(NUISANCE_PARAMS, n_estimators=int(estimators), max_depth=int(depth))


def _regressor(seed: int, params: Dict[str, object] | None = None):
    from sklearn.ensemble import GradientBoostingRegressor

    return GradientBoostingRegressor(random_state=seed, **(NUISANCE_PARAMS if params is None else params))


def cluster_robust_se(psi: np.ndarray, cluster: np.ndarray, jacobian: float) -> Tuple[float | None, int]:
    """``sqrt(sum_c (sum_{i in c} psi_i)^2) / (n * J)``; None when fewer than two clusters exist."""
    codes, inverse = np.unique(cluster, return_inverse=True)
    if codes.shape[0] < 2:
        return None, int(codes.shape[0])
    sums = np.bincount(inverse, weights=psi)
    return float(math.sqrt(float(np.sum(sums ** 2))) / (psi.shape[0] * jacobian)), int(codes.shape[0])


def small_sample_interval(theta: float, se: float, n_clusters: int, ci_level: float) -> dict:
    """WR1 A / DF09 A: the CR1 standard error ``se * sqrt(G / (G - 1))`` with a t(G - 1) interval, beside the headline CR0 + normal one."""
    from scipy import stats

    df = int(n_clusters) - 1
    se_cr1 = float(se) * math.sqrt(n_clusters / df)
    quantile = float(stats.t.ppf(0.5 + ci_level / 2.0, df))
    return dict(method=SMALL_SAMPLE_METHOD, se=se_cr1, df=df, t_quantile=quantile, ci_level=ci_level,
                ci_low=theta - quantile * se_cr1, ci_high=theta + quantile * se_cr1)


def time_clustered_interval(theta: float, psi: np.ndarray, time_cluster: np.ndarray, jacobian: float, ci_level: float, headline_se: float) -> dict:
    """CL1 A / CL2 A: the CR1 + t interval of the same estimating function clustered by *time_cluster* (calendar quarter) alone.

    No ``min_clusters`` gate (it is not the headline); the cluster count and the few-clusters notice are always stated. Fewer than two clusters
    -> ``insufficient_time_clusters`` without numbers. ``se_ratio_to_headline`` is the CR0 time-clustered SE over the headline SE."""
    se_cr0, n_clusters = cluster_robust_se(psi, time_cluster, jacobian)
    record = dict(cluster=TIME_CLUSTER_DEFINITION, n_clusters=int(n_clusters), note=TIME_CLUSTER_NOTE)
    if se_cr0 is None:
        return dict(record, status=STATUS_INSUFFICIENT_TIME_CLUSTERS)
    return dict(record, status="ok", se_cr0=se_cr0, se_ratio_to_headline=(se_cr0 / headline_se if headline_se > 0.0 else None),
                notices=few_clusters_notices(n_clusters), **small_sample_interval(theta, se_cr0, n_clusters, ci_level))


def cross_symbol_overlap(sym: np.ndarray, date_pos: np.ndarray, ev: np.ndarray, tr: np.ndarray, purge: int) -> Tuple[float | None, int, int]:
    """(share, hits, total) of the training rows of OTHER symbols that lie inside an evaluating symbol's block span widened by *purge* bars.

    Positions are on the union date axis (DF17 A); each evaluating symbol is counted against the training rows of the other symbols, and
    the counts are pooled over the evaluating symbols. The share is ``None`` when no such training row exists (e.g. one symbol)."""
    hits = total = 0
    for code in np.unique(sym[ev]):
        mine = ev & (sym == code)
        low, high = int(date_pos[mine].min()) - purge, int(date_pos[mine].max()) + purge
        other = tr & (sym != code)
        total += int(other.sum())
        hits += int((other & (date_pos >= low) & (date_pos <= high)).sum())
    return (hits / total if total else None), hits, total


def fold_diagnostic(k: int, sym: np.ndarray, ev: np.ndarray, tr: np.ndarray, date: np.ndarray, date_pos: np.ndarray, purge: int,
                    symbol_names: List[str] | None) -> dict:
    """WR2 A part: the calendar span of fold *k*'s evaluation block per symbol and the cross-symbol training overlap."""
    spans = {}
    for code in np.unique(sym[ev]):
        mine = date[ev & (sym == code)]
        spans[symbol_names[int(code)] if symbol_names is not None else str(int(code))] = [str(mine.min()), str(mine.max())]
    share, hits, total = cross_symbol_overlap(sym, date_pos, ev, tr, purge)
    return dict(fold=k, evaluation_span=spans, cross_symbol_training_overlap=share, overlap_rows=[hits, total])


def few_clusters_notices(n_clusters: int) -> List[dict]:
    """The notices an interval carries: the few-clusters warning when it rests on fewer than ``CLUSTER_WARNING_THRESHOLD`` clusters."""
    if n_clusters >= CLUSTER_WARNING_THRESHOLD:
        return []
    return [dict(code=FEW_CLUSTERS_CODE, message=FEW_CLUSTERS_MESSAGE, n_clusters=int(n_clusters), threshold=CLUSTER_WARNING_THRESHOLD)]


def dml_plr(X: np.ndarray, T: np.ndarray, Y: np.ndarray, sym: np.ndarray, row: np.ndarray, cluster: np.ndarray, *,
            folds: int, purge: int, seed: int, ci_level: float, min_fold_train: int, min_clusters: int, deadline: float,
            nuisance: Dict[str, object] | None = None, symbol_names: List[str] | None = None, date: np.ndarray | None = None,
            date_pos: np.ndarray | None = None, purge_scope: str = DEFAULT_PURGE_SCOPE, time_cluster: np.ndarray | None = None) -> dict:
    """Cross-fitted partialling-out estimate of theta (K01, K04, K08..K13). Returns a result dict; never raises on weak data.

    With fewer than *min_clusters* clusters (Z01..Z04, judged per call, so per pooled / per symbol / per placebo) the status is
    ``insufficient_clusters``: the point estimate is kept, no standard error and no interval are computed.

    Additive records (H1): ``treatment_scale_by_fold`` / ``scale_ratio_max_min`` (WR3 A, DF12 A: recorded, never judged); with *date* and
    *date_pos* also ``fold_diagnostics`` (per fold the evaluation calendar span per symbol and the cross-symbol training overlap) and, where
    the headline interval exists, ``small_sample_interval`` (WR1 A). Nothing here changes the headline numbers.

    With *time_cluster* (CL1 A, one code per anchor, e.g. the calendar quarter) every ``ok`` result also carries ``time_clustered_interval``.

    ``purge_scope="symbol"`` (default) is the original scheme. ``"global"`` (I1) needs *date*: the blocks are calendar intervals of the union of
    all anchor dates and the purge removes every symbol's rows near an evaluation interval (``training_mask_global``)."""
    n = int(T.shape[0])
    base = dict(n=n, folds=folds)
    if purge_scope not in PURGE_SCOPES:
        raise ValueError(f"purge_scope must be one of {PURGE_SCOPES}")
    use_global = purge_scope == "global"
    if use_global:
        if date is None:
            raise ValueError("purge_scope='global' needs the anchor dates")
        date_pos = np.unique(date, return_inverse=True)[1].astype(np.int64)       # the positions of THIS estimate's own union axis
        fold = assign_folds_global(date, folds)
    else:
        fold = assign_folds(sym, folds)
    t_res, y_res, t_scaled = np.empty(n), np.empty(n), np.empty(n)
    fold_sizes, scales, diagnostics = [], [], []
    for k in range(folds):
        if time.monotonic() > deadline:
            raise TimeoutError("causal analysis exceeded its run-duration bound")
        ev = fold == k
        tr = training_mask_global(date_pos, fold, k, purge) if use_global else training_mask(sym, row, fold, k, purge)
        fold_sizes.append([int(tr.sum()), int(ev.sum())])
        if tr.sum() < min_fold_train or not ev.any():
            return dict(base, status="insufficient_data", fold_sizes=fold_sizes)
        t_c, y_c = T.copy(), Y.copy()
        for code in np.unique(sym):
            mine = sym == code
            fit_rows = mine & tr
            if not fit_rows.any():
                return dict(base, status="insufficient_data", fold_sizes=fold_sizes)
            t_c[mine] -= T[fit_rows].mean()
            y_c[mine] -= Y[fit_rows].mean()
        scale = float(np.std(t_c[tr], ddof=0))
        if not math.isfinite(scale) or scale <= ds.EPSILON:
            return dict(base, status="degenerate_treatment", fold_sizes=fold_sizes)
        scales.append(scale)
        if date is not None and date_pos is not None:
            diagnostics.append(fold_diagnostic(k, sym, ev, tr, date, date_pos, purge, symbol_names))
        t_s = t_c / scale
        g_y = _regressor(seed + k, nuisance).fit(X[tr], y_c[tr])
        m_t = _regressor(seed + k, nuisance).fit(X[tr], t_s[tr])
        y_res[ev] = y_c[ev] - g_y.predict(X[ev])
        t_res[ev] = t_s[ev] - m_t.predict(X[ev])
        t_scaled[ev] = t_s[ev]
    denominator = float(np.sum(t_res ** 2))
    if not math.isfinite(denominator) or denominator <= 0.0:
        return dict(base, status="degenerate_treatment", fold_sizes=fold_sizes)
    theta = float(np.sum(t_res * y_res) / denominator)
    jacobian = denominator / n
    psi = t_res * (y_res - theta * t_res)
    se, n_clusters = cluster_robust_se(psi, cluster, jacobian)
    result = dict(base, status="ok", fold_sizes=fold_sizes, n_clusters=n_clusters, min_clusters=min_clusters,
                  estimated_effect_under_assumptions=theta, ci_level=ci_level,
                  treatment_residual_variance_ratio=float(np.var(t_res) / np.var(t_scaled)) if np.var(t_scaled) > 0 else None,
                  treatment_scale_by_fold=scales, scale_ratio_max_min=float(max(scales) / min(scales)))
    if diagnostics:
        result["fold_diagnostics"] = diagnostics
        shares = [d["cross_symbol_training_overlap"] for d in diagnostics if d["cross_symbol_training_overlap"] is not None]
        result["cross_symbol_training_overlap"] = float(np.mean(shares)) if shares else None
    if se is None or n_clusters < min_clusters:  # min_clusters >= 2 (validate_args), so a single cluster is always covered here
        result["status"] = STATUS_INSUFFICIENT_CLUSTERS
    else:
        z = NormalDist().inv_cdf(0.5 + ci_level / 2.0)
        result.update(se=se, ci_low=theta - z * se, ci_high=theta + z * se, notices=few_clusters_notices(n_clusters),
                      small_sample_interval=small_sample_interval(theta, se, n_clusters, ci_level))
        if time_cluster is not None:
            result["time_clustered_interval"] = time_clustered_interval(theta, psi, time_cluster, jacobian, ci_level, se)
    return result


def shuffle_within_clusters(T: np.ndarray, cluster: np.ndarray, seed: int) -> np.ndarray:
    """K14 placebo: permute the treatment inside every (symbol, quarter) cluster (seeded)."""
    rng = np.random.default_rng(np.random.SeedSequence([seed, PLACEBO_STREAM]))
    out = T.copy()
    for code in np.unique(cluster):
        idx = np.nonzero(cluster == code)[0]
        out[idx] = T[rng.permutation(idx)]
    return out


def naive_slope(T: np.ndarray, Y: np.ndarray) -> float:
    """Unadjusted regression slope of Y on T (a contrast for the report, not an estimate of anything causal).

    A (near-)constant treatment (SD <= ``ds.EPSILON``, the rule ``dml_plr`` applies to the treatment scale) has no slope: NaN, without a
    RuntimeWarning. ``_clean`` omits a NaN and lists it in ``omitted_non_finite`` (D3, DF03 A / DF04 A)."""
    if float(np.std(T)) <= ds.EPSILON:
        return float("nan")
    t = T - T.mean()
    return float(np.sum(t * (Y - Y.mean())) / np.sum(t * t))


# --- Coverage study (H1, no market data) -----------------------------------------------------------------------------------


def coverage_noise(dgp: str, symbols: int, per_symbol: int, rng: np.random.Generator) -> np.ndarray:
    """Unit-variance series ``(symbols, per_symbol)`` of the DGP: iid, AR(1) inside a symbol, or a share common to all symbols."""
    if dgp == "iid":
        return rng.normal(size=(symbols, per_symbol))
    if dgp == "ar1_within_symbol":
        shock = rng.normal(size=(symbols, per_symbol))
        out = np.empty_like(shock)
        out[:, 0] = shock[:, 0]
        innovation = math.sqrt(1.0 - COVERAGE_AR_RHO ** 2)
        for t in range(1, per_symbol):
            out[:, t] = COVERAGE_AR_RHO * out[:, t - 1] + innovation * shock[:, t]
        return out
    if dgp == "common_market_shock":
        common = rng.normal(size=per_symbol)
        return math.sqrt(COVERAGE_COMMON_SHARE) * common[None, :] + math.sqrt(1.0 - COVERAGE_COMMON_SHARE) * rng.normal(size=(symbols, per_symbol))
    raise ValueError(f"unknown DGP {dgp!r}")


def simulate_panel(symbols: int, quarters: int, dgp: str, theta: float, rng: np.random.Generator) -> Tuple[np.ndarray, ...]:
    """X, T, Y, sym, row, cluster of a balanced panel: ``Y = theta*T_s + sin(X1) + 0.5*X2^2 + eps``, ``T = 0.5*X1 + v``, ``T_s = T / SD(T)``."""
    per = quarters * COVERAGE_BARS_PER_CLUSTER
    x = rng.normal(size=(symbols, per, COVERAGE_CONFOUNDERS))
    v, eps = coverage_noise(dgp, symbols, per, rng), coverage_noise(dgp, symbols, per, rng)
    t = COVERAGE_TREATMENT_LOADING * x[:, :, 0] + v
    y = theta * t / COVERAGE_TREATMENT_SD + np.sin(x[:, :, 0]) + 0.5 * x[:, :, 1] ** 2 + eps
    sym = np.repeat(np.arange(symbols), per)
    row = np.tile(np.arange(per), symbols)
    cluster = sym * quarters + row // COVERAGE_BARS_PER_CLUSTER
    return x.reshape(-1, COVERAGE_CONFOUNDERS), t.reshape(-1), y.reshape(-1), sym.astype(np.int64), row.astype(np.int64), cluster.astype(np.int64)


def _coverage_cell(values: List[dict], reps: int, theta: float, interval_key: str | None) -> dict:
    """Coverage of one interval type over the ``ok`` repetitions (Monte Carlo SE = sqrt(p (1 - p) / n))."""
    key = (lambda r: r) if interval_key is None else (lambda r: r[interval_key])
    hits = np.array([key(r)["ci_low"] <= theta <= key(r)["ci_high"] for r in values], dtype=float)
    p = float(hits.mean()) if hits.size else None
    return dict(coverage=p, monte_carlo_se=(math.sqrt(p * (1.0 - p) / hits.size) if hits.size else None),
                mean_se=float(np.mean([key(r)["se"] for r in values])) if values else None)


def coverage_study(*, reps: int, seed: int, ci_level: float, folds: int, purge: int, min_fold_train: int, nuisance: Dict[str, object],
                   deadline: float, grid: Tuple[Tuple[int, int], ...] = COVERAGE_GRID, dgps: Tuple[str, ...] = COVERAGE_DGPS,
                   theta: float = COVERAGE_THETA) -> dict:
    """Empirical coverage of the headline CR0 + normal interval, the CR1 + t interval and (CL3 A) the calendar-quarter-clustered CR1 + t interval
    over simulated panels (DF10 A / DF11 A).

    Each (G, DGP) cell repeats ``dml_plr`` itself *reps* times with seeds from ``SeedSequence([seed, COVERAGE_STREAM, g, dgp, rep])``.
    No pass / fail is attached: the table is a measurement of these DGPs only (``COVERAGE_STATEMENT``)."""
    cells = []
    for gi, (n_symbols, quarters) in enumerate(grid):
        for di, dgp in enumerate(dgps):
            results = []
            for rep_index in range(reps):
                sequence = np.random.SeedSequence([seed, COVERAGE_STREAM, gi, di, rep_index])
                x, t, y, sym, row, cluster = simulate_panel(n_symbols, quarters, dgp, theta, np.random.default_rng(sequence))
                results.append(dml_plr(x, t, y, sym, row, cluster, folds=folds, purge=purge, seed=int(sequence.generate_state(1)[0] % 2_147_483_647),
                                       ci_level=ci_level, min_fold_train=min_fold_train, min_clusters=COVERAGE_MIN_CLUSTERS, deadline=deadline,
                                       nuisance=nuisance, time_cluster=row // COVERAGE_BARS_PER_CLUSTER))
            ok = [r for r in results if r["status"] == "ok"]
            timed = [r for r in ok if r["time_clustered_interval"]["status"] == "ok"]
            estimates = np.array([r["estimated_effect_under_assumptions"] for r in ok])
            cells.append(dict(
                clusters=n_symbols * quarters, symbols=n_symbols, quarters=quarters, dgp=dgp, reps=reps, reps_ok=len(ok), reps_failed=reps - len(ok),
                mean_bias=float(estimates.mean() - theta) if ok else None,
                empirical_sd_of_estimate=float(estimates.std(ddof=1)) if len(ok) > 1 else None,
                cr0_normal=_coverage_cell(ok, reps, theta, None), cr1_t=_coverage_cell(ok, reps, theta, "small_sample_interval"),
                time_clustered_cr1_t=dict(_coverage_cell(timed, reps, theta, "time_clustered_interval"), clusters=quarters, reps_ok=len(timed))))
    return dict(theta_true=theta, ci_level=ci_level, cells=cells)


def validate_coverage_args(args: argparse.Namespace) -> None:
    for name, low in (("seed", 0), ("min_fold_train", 2), ("horizon", 1)):
        value = getattr(args, name)
        if type(value) is not int or value < low:
            raise ValueError(f"--{name.replace('_', '-')} must be an integer >= {low}")
    if type(args.reps) is not int or not COVERAGE_REPS_BAND[0] <= args.reps <= COVERAGE_REPS_BAND[1]:
        raise ValueError(f"--reps must be an integer in {COVERAGE_REPS_BAND[0]}..{COVERAGE_REPS_BAND[1]}")
    if type(args.folds) is not int or not 2 <= args.folds <= MAX_FOLDS:
        raise ValueError(f"--folds must be an integer in 2..{MAX_FOLDS}")
    if args.ci_level not in CI_LEVELS:
        raise ValueError(f"--ci-level must be one of {CI_LEVELS}")
    if type(args.max_seconds) is not int or not 1 <= args.max_seconds <= MAX_SECONDS:
        raise ValueError(f"--max-seconds must be in 1..{MAX_SECONDS}")
    validate_nuisance_args(args)


def _run_coverage(args: argparse.Namespace, started: float) -> int:
    validate_coverage_args(args)
    deadline = started + args.max_seconds
    params = nuisance_params(args.nuisance_estimators, args.nuisance_depth)
    purge = purge_bars(args.horizon)
    study = coverage_study(reps=args.reps, seed=args.seed, ci_level=args.ci_level, folds=args.folds, purge=purge,
                           min_fold_train=args.min_fold_train, nuisance=params, deadline=deadline)
    config = dict(reps=args.reps, seed=args.seed, ci_level=args.ci_level, folds=args.folds, purge_bars=purge, min_fold_train=args.min_fold_train,
                  nuisance=dict(params, learner="GradientBoostingRegressor"), theta_true=COVERAGE_THETA, bars_per_cluster=COVERAGE_BARS_PER_CLUSTER,
                  grid=[list(g) for g in COVERAGE_GRID], dgps=list(COVERAGE_DGPS), ar_rho=COVERAGE_AR_RHO, common_share=COVERAGE_COMMON_SHARE,
                  treatment_loading=COVERAGE_TREATMENT_LOADING, treatment_sd=COVERAGE_TREATMENT_SD, confounders=COVERAGE_CONFOUNDERS,
                  min_clusters=COVERAGE_MIN_CLUSTERS, cluster_definitions=["symbol_calendar_quarter", TIME_CLUSTER_DEFINITION])
    config_sha = hashlib.sha256(checkpoints.canonical(config).encode()).hexdigest()
    created = datetime.datetime.now(datetime.timezone.utc)
    run_id = f"{created.strftime('%Y%m%dT%H%M%SZ')}_{config_sha[:8]}"
    document = dict(schema_version=SCHEMA_VERSION, tool=TOOL, tool_version=TOOL_VERSION, kind="coverage_study", created_utc=created.isoformat(),
                    run_id=run_id, config_sha256=config_sha, config=config, statement=COVERAGE_STATEMENT, judged=False, **study,
                    elapsed_seconds=round(time.monotonic() - started, 3))
    omitted: list = []
    cleaned = _clean(document, "coverage", omitted)
    if omitted:
        cleaned["omitted_non_finite"] = omitted
    root = Path(args.out_root)
    root.mkdir(parents=True, exist_ok=True)
    path = root / f"coverage_{run_id}.json"
    checkpoints.atomic_json(path, cleaned)
    print(f"causal_analysis --coverage-study: {args.reps} repetition(s) per cell -> {path}")
    for cell in study["cells"]:
        c0, c1, c2 = cell["cr0_normal"], cell["cr1_t"], cell["time_clustered_cr1_t"]
        text = "n/a" if c0["coverage"] is None else (f"CR0+z {c0['coverage']:.3f}+-{c0['monte_carlo_se']:.3f}  CR1+t {c1['coverage']:.3f}+-{c1['monte_carlo_se']:.3f}"
                                                      + (f"  time-clustered CR1+t {c2['coverage']:.3f}+-{c2['monte_carlo_se']:.3f} (G={c2['clusters']})"
                                                         if c2["coverage"] is not None else "  time-clustered n/a") + f"  bias {cell['mean_bias']:+.4f}")
        print(f"  G={cell['clusters']:>2} {cell['dgp']:<20} ok {cell['reps_ok']}/{cell['reps']}  {text}")
    print(f"  NOTE: {COVERAGE_STATEMENT}.")
    return EXIT_OK


# --- Orchestration ----------------------------------------------------------------------------------------------------------


def validate_nuisance_args(args: argparse.Namespace) -> None:
    """DF13 A: the two exposed learner settings stay inside their hard bands."""
    for name, band in (("nuisance_estimators", NUISANCE_ESTIMATORS_BAND), ("nuisance_depth", NUISANCE_DEPTH_BAND)):
        value = getattr(args, name)
        if type(value) is not int or not band[0] <= value <= band[1]:
            raise ValueError(f"--{name.replace('_', '-')} must be an integer in {band[0]}..{band[1]}")


def validate_args(args: argparse.Namespace) -> None:
    if args.treatment not in TREATMENTS:
        raise ValueError(f"--treatment must be one of {TREATMENTS}")
    if args.treatment == EXTERNAL and args.treatment_dir is None:
        raise ValueError("--treatment external requires --treatment-dir")
    for name, low in (("horizon", 1), ("max_symbols", 1), ("min_samples", 2), ("min_fold_train", 2), ("seed", 0), ("oos_tail_days", 0)):
        value = getattr(args, name)
        if type(value) is not int or value < low:
            raise ValueError(f"--{name.replace('_', '-')} must be an integer >= {low}")
    if type(args.folds) is not int or not 2 <= args.folds <= MAX_FOLDS:
        raise ValueError(f"--folds must be an integer in 2..{MAX_FOLDS}")
    if args.ci_level not in CI_LEVELS:
        raise ValueError(f"--ci-level must be one of {CI_LEVELS}")
    for name in ("min_clusters", "min_clusters_per_symbol"):
        value = getattr(args, name)
        if type(value) is not int or not MIN_CLUSTERS_BAND[0] <= value <= MIN_CLUSTERS_BAND[1]:
            raise ValueError(f"--{name.replace('_', '-')} must be an integer in {MIN_CLUSTERS_BAND[0]}..{MIN_CLUSTERS_BAND[1]}")
    if type(args.max_seconds) is not int or not 1 <= args.max_seconds <= MAX_SECONDS:
        raise ValueError(f"--max-seconds must be in 1..{MAX_SECONDS}")
    validate_nuisance_args(args)


def _row(estimand: str, treatment: str, horizon: int, result: dict) -> dict:
    return dict(
        estimand=estimand, treatment=treatment, outcome=OUTCOME_NAME, horizon=horizon, n=result.get("n", ""),
        theta=result.get("estimated_effect_under_assumptions", ""), se=result.get("se", ""),
        ci_low=result.get("ci_low", ""), ci_high=result.get("ci_high", ""), ci_level=result.get("ci_level", ""),
        folds=result.get("folds", ""), status=result["status"],
        notices=";".join(n["code"] for n in result.get("notices", [])))


def _write_csv(path: Path, rows: List[dict]) -> None:
    with path.open("w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=CSV_COLUMNS, lineterminator="\n")
        writer.writeheader()
        for row in rows:
            writer.writerow({k: (repr(v) if isinstance(v, float) else v) for k, v in row.items()})


def _clean(value, path: str, omitted: list):
    """JSON-bound copy without non-finite floats (listed in *omitted*, never replaced by 0)."""
    if isinstance(value, dict):
        out = {}
        for key, item in value.items():
            cleaned = _clean(item, f"{path}.{key}", omitted)
            if cleaned is not _OMIT:
                out[key] = cleaned
        return out
    if isinstance(value, (list, tuple)):
        return [_clean(item, f"{path}[]", omitted) for item in value]
    if isinstance(value, (np.floating, float)):
        if not math.isfinite(float(value)):
            omitted.append(path)
            return _OMIT
        return float(value)
    if isinstance(value, np.integer):
        return int(value)
    if isinstance(value, np.bool_):
        return bool(value)
    return value


_OMIT = object()


def run(args: argparse.Namespace) -> int:
    started = time.monotonic()
    try:
        return (_run_coverage if args.coverage_study else _run)(args, started)
    except PrerequisiteError as exc:
        print(f"causal_analysis: PREREQUISITE NOT MET: {exc}", file=sys.stderr)
        return EXIT_PREREQUISITE
    except TimeoutError as exc:  # D1: raised before the work folder exists, so nothing is published (DF02 A)
        print(f"causal_analysis: FAILED: {exc}", file=sys.stderr)
        return EXIT_FAILED


def _declares_synthetic(data_dir: Path, names: List[str]) -> bool:
    """True when the folder or one of the loaded files is marked synthetic. Under an explicit ``--allow-synthetic`` a mark that cannot be
    verified (a corrupt manifest, no polars metadata API) is reported as synthetic, the conservative answer (DF14 B)."""
    try:
        return ds.is_synthetic_dir(data_dir) or any(ds.is_synthetic_parquet(Path(data_dir) / f"{name}.parquet") for name in names)
    except ValueError:
        return True


def _run(args: argparse.Namespace, started: float) -> int:
    validate_args(args)
    deadline = started + args.max_seconds
    wanted = [t.strip() for t in args.symbols.split(",") if t.strip()] if args.symbols else None
    if args.symbols is not None and not wanted:
        raise PrerequisiteError("--symbols names no symbol")
    try:
        loaded, loaded_dates = ds.load_parquet_dir(args.data_dir, return_dates=True, symbols=wanted, start=args.start, end=args.end,
                                                       allow_synthetic=args.allow_synthetic)
    except (FileNotFoundError, ValueError) as exc:
        raise PrerequisiteError(str(exc)) from exc
    if wanted is not None:
        absent = sorted({w.lower() for w in wanted} - {s.lower() for s in loaded})
        if absent:
            raise PrerequisiteError(f"--symbols not found under {args.data_dir}: {absent}")
    chosen = list(loaded)[: args.max_symbols]
    symbols = {s: loaded[s] for s in chosen}
    dates = {s: loaded_dates[s] for s in chosen}
    if not symbols:
        raise PrerequisiteError(f"no data under {args.data_dir}")
    main_block_end = None
    if args.oos_tail_days > 0:
        try:
            common_end = ds.common_end_date(dates)
        except ValueError as exc:  # D4: a symbol with duplicate / reversed dates
            raise PrerequisiteError(str(exc)) from exc
        main_block_end = ds.resolve_oos_cutoff(common_end, args.oos_tail_days).isoformat()
        for s in list(symbols):
            main_arr, main_dates, _oos_arr, _oos_dates = ds.oos_split(symbols[s], dates[s], args.oos_tail_days, common_end)
            if main_arr.shape[0] == 0:
                del symbols[s], dates[s]
            else:
                symbols[s], dates[s] = main_arr, main_dates
        if not symbols:
            raise PrerequisiteError("the out-of-sample tail left no rows")
    data_revision = checkpoints.source_revision(symbols, dates)
    synthetic_input = args.allow_synthetic and _declares_synthetic(args.data_dir, list(symbols))
    samples, dropped = build_samples(symbols, dates, treatment=args.treatment, horizon=args.horizon,
                                     external_dir=args.treatment_dir, external_column=args.treatment_column)
    n = int(samples.T.shape[0])
    if n > ds.MAX_COMPOSED_BATCH_SAMPLES:
        raise PrerequisiteError(f"{n} anchors exceed the bound {ds.MAX_COMPOSED_BATCH_SAMPLES}; reduce --max-symbols or the date range")
    if (n * (samples.X.shape[1] + 4) * 8) > ds.MAX_COMPOSED_TENSOR_SIZE_MB * 1024 * 1024:
        raise PrerequisiteError(f"the sample matrix exceeds {ds.MAX_COMPOSED_TENSOR_SIZE_MB} MB")

    purge = purge_bars(args.horizon)
    nuisance = nuisance_params(args.nuisance_estimators, args.nuisance_depth)
    config = dict(
        treatment=args.treatment, treatment_column=args.treatment_column if args.treatment == EXTERNAL else None,
        outcome=OUTCOME_NAME, horizon=args.horizon, confounders=list(samples.confounders), folds=args.folds, purge_bars=purge,
        lookback_bars=LOOKBACK_BARS, ci_level=args.ci_level, min_samples=args.min_samples, min_fold_train=args.min_fold_train,
        min_clusters=args.min_clusters, min_clusters_per_symbol=args.min_clusters_per_symbol,
        nuisance=dict(nuisance, learner="GradientBoostingRegressor"), seed=args.seed, symbols=samples.symbols,
        oos_tail_days=args.oos_tail_days, start=args.start, end=args.end, data_revision=data_revision)
    if args.purge_scope != DEFAULT_PURGE_SCOPE:  # part of the config hash only when it differs, so the default hash and run id are unchanged
        config["purge_scope"] = args.purge_scope
    config_sha = hashlib.sha256(checkpoints.canonical(config).encode()).hexdigest()
    created = datetime.datetime.now(datetime.timezone.utc)
    run_id = f"{created.strftime('%Y%m%dT%H%M%SZ')}_{config_sha[:8]}"
    root = Path(args.out_root)
    final_dir, work = root / run_id, root / (run_id + INCOMING_SUFFIX)
    if final_dir.exists() or work.exists():
        raise PrerequisiteError(f"run folder already exists: {final_dir}")

    common = dict(folds=args.folds, purge=purge, seed=args.seed, ci_level=args.ci_level,
                  min_fold_train=args.min_fold_train, min_clusters=args.min_clusters, deadline=deadline, nuisance=nuisance,
                  symbol_names=samples.symbols, purge_scope=args.purge_scope)
    results: dict = {}
    if n < args.min_samples:
        results["pooled"] = dict(status="insufficient_data", n=n, folds=args.folds, minimum=args.min_samples)
    else:
        results["pooled"] = dml_plr(samples.X, samples.T, samples.Y, samples.sym, samples.row, samples.cluster,
                                    date=samples.date, date_pos=samples.date_pos, time_cluster=samples.quarter, **common)
        results["pooled"]["naive_unadjusted_effect_per_sd"] = naive_slope(samples.T, samples.Y) * float(np.std(samples.T))
        per_symbol = {}
        for code, name in enumerate(samples.symbols):
            mine = samples.sym == code
            if int(mine.sum()) < args.min_samples:
                per_symbol[name] = dict(status="insufficient_data", n=int(mine.sum()), folds=args.folds, minimum=args.min_samples)
                continue
            per_symbol[name] = dml_plr(samples.X[mine], samples.T[mine], samples.Y[mine], samples.sym[mine], samples.row[mine],
                                       samples.cluster[mine], date=samples.date[mine], date_pos=samples.date_pos[mine],
                                       time_cluster=samples.quarter[mine], **dict(common, min_clusters=args.min_clusters_per_symbol))
        results["per_symbol"] = per_symbol
        shuffled = shuffle_within_clusters(samples.T, samples.cluster, args.seed)
        results["placebo_shuffled_treatment"] = dml_plr(samples.X, shuffled, samples.Y, samples.sym, samples.row,
                                                        samples.cluster, date=samples.date, date_pos=samples.date_pos,
                                                        time_cluster=samples.quarter, **common)
        has_future = np.isfinite(samples.T_future)
        if int(has_future.sum()) < args.min_samples:
            results["placebo_future_treatment"] = dict(status="insufficient_data", n=int(has_future.sum()), folds=args.folds,
                                                       minimum=args.min_samples)
        else:
            f = has_future
            results["placebo_future_treatment"] = dml_plr(samples.X[f], samples.T_future[f], samples.Y[f], samples.sym[f],
                                                          samples.row[f], samples.cluster[f], date=samples.date[f],
                                                          date_pos=samples.date_pos[f], time_cluster=samples.quarter[f], **common)
    status = results["pooled"]["status"]

    report = dict(
        schema_version=SCHEMA_VERSION, tool=TOOL, tool_version=TOOL_VERSION, created_utc=created.isoformat(), run_id=run_id,
        status=status, data_revision=data_revision, config_sha256=config_sha, seed=args.seed, config=config,
        notices=list(NOTICES) + ([NOTICE_SYNTHETIC] if synthetic_input else [])
        + ([NOTICE_NO_INTERVAL] if status == STATUS_INSUFFICIENT_CLUSTERS else []), interpretation=INTERPRETATION,
        estimate_scale=ESTIMATE_SCALE,
        assumptions=[dict(code=c, statement=s, testable=t) for c, s, t in ASSUMPTIONS],
        data=dict(symbols=samples.symbols, synthetic=bool(synthetic_input), oos_tail_days=args.oos_tail_days, main_block_end=main_block_end,
                  oos_rows_used=False if args.oos_tail_days > 0 else "not_excluded", anchors=n, dropped=dropped,
                  anchors_per_symbol={name: int((samples.sym == i).sum()) for i, name in enumerate(samples.symbols)},
                  first_anchor_quarter=int(samples.quarter.min()), last_anchor_quarter=int(samples.quarter.max())),
        split=dict(scheme=SCHEME_BY_SCOPE[args.purge_scope], folds=args.folds, purge_bars=purge, lookback_bars=LOOKBACK_BARS,
                   horizon=args.horizon, training_rows_within_purge_of_an_evaluation_block="excluded", purge_scope=args.purge_scope),
        uncertainty=dict(method="cluster_robust_influence_function", cluster="symbol_calendar_quarter",
                         ci="normal", ci_level=args.ci_level, n_clusters=results["pooled"].get("n_clusters"),
                         min_clusters=args.min_clusters, min_clusters_per_symbol=args.min_clusters_per_symbol,
                         warn_below_clusters=CLUSTER_WARNING_THRESHOLD,
                         small_sample_interval=dict(method=SMALL_SAMPLE_METHOD, where="results.*.small_sample_interval",
                                                    note="beside the headline interval, never replacing it; coverage is measured with --coverage-study"),
                         time_clustered_interval=dict(method=SMALL_SAMPLE_METHOD, cluster=TIME_CLUSTER_DEFINITION,
                                                      where="results.*.time_clustered_interval", note=TIME_CLUSTER_NOTE)),
        results=results, elapsed_seconds=round(time.monotonic() - started, 3))
    omitted: list = []
    cleaned = _clean(report, "report", omitted)
    if omitted:
        cleaned["omitted_non_finite"] = omitted

    rows = [_row("plr_effect_pooled", args.treatment, args.horizon, results["pooled"])]
    for name, result in results.get("per_symbol", {}).items():
        rows.append(_row(f"plr_effect_symbol:{name}", args.treatment, args.horizon, result))
    for key in ("placebo_shuffled_treatment", "placebo_future_treatment"):
        if key in results:
            rows.append(_row(key, args.treatment, args.horizon, results[key]))
    work.mkdir(parents=True)
    try:
        _write_csv(work / REPORT_CSV, rows)
        checkpoints.atomic_json(work / REPORT_JSON, cleaned)  # manifest last
        os.rename(work, final_dir)
    except BaseException:
        shutil.rmtree(work, ignore_errors=True)
        raise
    pooled = results["pooled"]
    print(f"causal_analysis: {status.upper()}  run={run_id}  folder={final_dir}")
    print(f"  anchors={n} symbols={len(samples.symbols)} dropped={dropped}")
    flagged = [name for name, result in [("pooled", pooled), *results.get("per_symbol", {}).items(),
                                          ("placebo_shuffled_treatment", results.get("placebo_shuffled_treatment", {})),
                                          ("placebo_future_treatment", results.get("placebo_future_treatment", {}))]
               if result.get("notices")]
    if flagged:
        print(f"  WARNING: {FEW_CLUSTERS_MESSAGE} (fewer than {CLUSTER_WARNING_THRESHOLD} clusters: {', '.join(flagged)})")
    if status == STATUS_INSUFFICIENT_CLUSTERS:
        print(f"  {pooled['n_clusters']} clusters < --min-clusters {args.min_clusters}: the point estimate is kept, no interval is computed.")
    if "estimated_effect_under_assumptions" in pooled:
        interval = (f"  CI{int(args.ci_level * 100)} [{pooled['ci_low']:.4g}, {pooled['ci_high']:.4g}]" if "ci_low" in pooled else "")
        print(f"  estimated effect under assumptions (per +1 fold-training SD of {args.treatment}): "
              f"{pooled['estimated_effect_under_assumptions']:.4g}{interval}  clusters={pooled['n_clusters']}")
        if "small_sample_interval" in pooled:
            small = pooled["small_sample_interval"]
            print(f"  small-sample interval ({small['method']}, df={small['df']}): [{small['ci_low']:.4g}, {small['ci_high']:.4g}]"
                  f"  (beside, not replacing, the headline one)")
        if "time_clustered_interval" in pooled:
            timed = pooled["time_clustered_interval"]
            if timed["status"] == "ok":
                print(f"  calendar-quarter-clustered interval ({timed['method']}, {timed['n_clusters']} quarters, df={timed['df']}): "
                      f"[{timed['ci_low']:.4g}, {timed['ci_high']:.4g}]  (a side check for shocks common to all symbols; few clusters, unstable)")
            else:
                print(f"  calendar-quarter-clustered interval: {timed['status']} ({timed['n_clusters']} quarter(s))")
        print("  NOTE: an association estimate under untestable assumptions; not proof of causation.")
    return EXIT_OK if status == "ok" else EXIT_PREREQUISITE


# --- Self-check ----------------------------------------------------------------------------------------------------------------


def _planted(n: int, theta: float, seed: int, confound: float = 1.5) -> Tuple[np.ndarray, ...]:
    """Two symbols with nonlinear confounding (X drives both T and Y); Y depends on T by *theta* per +1 SD of T given X.
    Returns X, T, Y, sym, row, cluster."""
    rng = np.random.default_rng(seed)
    per = n // 2
    x = rng.normal(size=(n, 3))
    t = confound * np.tanh(x[:, 0]) + 0.5 * x[:, 1] ** 2 + rng.normal(size=n)
    t_sd = (t - t.mean()) / t.std()
    y = theta * t_sd + 2.0 * confound * np.tanh(x[:, 0]) + np.sin(x[:, 2]) + rng.normal(0.0, 0.5, size=n)
    sym = np.repeat([0, 1], per)
    row = np.concatenate([np.arange(per), np.arange(per)])
    cluster = sym * 1000 + (row // 63).astype(np.int64)
    return x, t, y, sym, row, cluster


def _selfcheck_args(root: Path, data_dir: Path, *extra: str) -> argparse.Namespace:
    return _parse_args(["--data-dir", str(data_dir), "--out-root", str(root), *extra])


def _run_selfcheck() -> None:
    import tempfile

    import polars as pl

    # group 1: argument rules (argparse writes its usage text to stderr; it is not interesting here)
    import contextlib
    import io

    quiet = contextlib.redirect_stderr(io.StringIO())
    quiet.__enter__()
    base = _parse_args(["--oos-tail-days", "0"])
    validate_args(base)
    for bad in (["--treatment", "bogus"], ["--treatment", "external"], ["--folds", "1"], ["--folds", str(MAX_FOLDS + 1)],
                ["--horizon", "0"], ["--ci-level", "0.8"], ["--max-seconds", "0"], ["--min-samples", "1"], ["--seed", "-1"],
                ["--min-clusters", "1"], ["--min-clusters", str(MIN_CLUSTERS_BAND[1] + 1)], ["--min-clusters-per-symbol", "1"],
                ["--min-clusters-per-symbol", str(MIN_CLUSTERS_BAND[1] + 1)]):
        try:
            validate_args(_parse_args(["--oos-tail-days", "0", *bad]))
        except (ValueError, SystemExit):
            continue
        raise AssertionError(f"argument {bad} should be rejected")
    try:
        _parse_args([])  # --oos-tail-days is required: using every row is an explicit choice (K17)
    except SystemExit:
        pass
    else:
        raise AssertionError("--oos-tail-days must be required")
    finally:
        quiet.__exit__(None, None, None)
    print("  group 1 argument rules OK")

    # group 2: feature definitions on a hand-made series, and the realized-volatility definition of model_analysis
    rng = np.random.default_rng(3)
    close = 100.0 * np.exp(np.cumsum(rng.normal(0, 0.01, 60)))
    ohlcv = np.column_stack([close, close * 1.01, close * 0.99, close, rng.integers(1000, 2000, 60).astype(float)])
    f = compute_features(ohlcv)
    assert np.isnan(f["log_return_1"][0]) and abs(f["log_return_1"][30] - math.log(close[30] / close[29])) < 1e-12
    assert abs(f["log_return_5"][30] - math.log(close[30] / close[25])) < 1e-12
    assert np.isnan(f["log_return_20"][19]) and abs(f["log_return_20"][20] - math.log(close[20] / close[0])) < 1e-12
    r = np.diff(np.log(close))
    assert abs(f["realized_volatility_20"][40] - np.std(r[20:40], ddof=0)) < 1e-12 and np.isnan(f["realized_volatility_20"][19])
    assert abs(f["log_volume_change_1"][10] - math.log(ohlcv[10, 4] / ohlcv[9, 4])) < 1e-12
    vec, kept = model_analysis.vectors(ohlcv, np.arange(20, 50))
    assert np.allclose(vec[:, 1], f["realized_volatility_20"][kept], atol=1e-6) and np.allclose(vec[:, 0], f["log_return_1"][kept], atol=1e-6)
    bad_close = ohlcv.copy()
    bad_close[30, ds.CLOSE] = -1.0
    fb = compute_features(bad_close)
    assert np.isnan(fb["log_return_1"][30]) and np.isnan(fb["log_return_1"][31]), "a non-positive price leaves no return"
    y = forward_log_return(ohlcv, 5)
    assert abs(y[10] - ds.make_regression_target(close[10], close[15])) < 1e-12 and np.isnan(y[-1]) and np.isnan(y[-5])
    assert confounder_names("log_return_5") == ("log_return_1", "log_return_20", "realized_volatility_20", "log_volume_change_1")
    print("  group 2 feature definitions OK (match dataset / model_analysis)")

    # group 3: folds partition the anchors and no training row overlaps an evaluation block's information interval
    sym = np.repeat([0, 1, 2], [300, 250, 400])
    row = np.concatenate([np.arange(m) + LOOKBACK_BARS for m in (300, 250, 400)])
    horizon = 5
    purge = purge_bars(horizon)
    assert purge == LOOKBACK_BARS + horizon == ds.resolve_purge_gap(LOOKBACK_BARS + 1, horizon)
    fold = assign_folds(sym, 5)
    assert set(fold) == set(range(5))
    for code in (0, 1, 2):
        in_sym = np.nonzero(sym == code)[0]
        assert np.all(np.diff(fold[in_sym]) >= 0), "blocks must be time-contiguous per symbol"
        sizes = np.bincount(fold[in_sym], minlength=5)
        assert sizes.max() - sizes.min() <= 1, sizes
    for k in range(5):
        tr = training_mask(sym, row, fold, k, purge)
        assert not (tr & (fold == k)).any()
        for code in (0, 1, 2):
            ev_rows, tr_rows = row[(sym == code) & (fold == k)], row[(sym == code) & tr]
            # information interval of an anchor t is [t - LOOKBACK_BARS, t + horizon]
            for t_row in tr_rows[:: max(1, tr_rows.shape[0] // 50)]:
                assert np.all((t_row + horizon < ev_rows - LOOKBACK_BARS) | (t_row - LOOKBACK_BARS > ev_rows + horizon)), (k, code, t_row)
    print("  group 3 cross-fit blocks and purge OK")

    # group 4: cluster-robust SE equals the plain sandwich when every observation is its own cluster, and None for one cluster
    psi = np.array([1.0, -2.0, 0.5, 3.0])
    se, g = cluster_robust_se(psi, np.arange(4), 2.0)
    assert g == 4 and abs(se - math.sqrt(float(np.sum(psi ** 2))) / (4 * 2.0)) < 1e-12
    se2, g2 = cluster_robust_se(psi, np.array([0, 0, 1, 1]), 2.0)
    assert abs(se2 - math.sqrt((1.0 - 2.0) ** 2 + (0.5 + 3.0) ** 2) / (4 * 2.0)) < 1e-12 and g2 == 2
    assert cluster_robust_se(psi, np.zeros(4, dtype=int), 2.0)[0] is None
    print("  group 4 cluster-robust standard error OK")

    # group 5: planted effect is recovered (and the naive slope is not); a null effect is covered; placebos sit near 0
    deadline = time.monotonic() + 600
    kw = dict(folds=5, purge=purge, seed=DEFAULT_SEED, ci_level=0.99, min_fold_train=50, min_clusters=DEFAULT_MIN_CLUSTERS, deadline=deadline)
    x, t, y, s, rw, cl = _planted(4000, 0.5, 11)
    found = dml_plr(x, t, y, s, rw, cl, **kw)
    assert found["status"] == "ok" and found["ci_low"] < 0.5 < found["ci_high"], found
    naive = naive_slope(t, y) * float(np.std(t))  # per +1 SD, to compare with theta
    assert abs(naive - 0.5) > 3 * abs(found["estimated_effect_under_assumptions"] - 0.5), (naive, found)
    xz, tz, yz, sz, rz, cz = _planted(4000, 0.0, 12)
    null = dml_plr(xz, tz, yz, sz, rz, cz, **kw)
    assert null["ci_low"] < 0.0 < null["ci_high"], null
    shuffled = dml_plr(x, shuffle_within_clusters(t, cl, 5), y, s, rw, cl, **kw)
    assert shuffled["ci_low"] < 0.0 < shuffled["ci_high"], shuffled
    assert np.array_equal(shuffle_within_clusters(t, cl, 5), shuffle_within_clusters(t, cl, 5))
    assert 0.0 < found["treatment_residual_variance_ratio"] < 1.0
    # the minimum cluster count (Z01..Z04): exactly at the minimum the interval is computed, one below it is not; the estimate stays
    assert found["n_clusters"] == 64 and found["min_clusters"] == DEFAULT_MIN_CLUSTERS == 30
    edge = dml_plr(x, t, y, s, rw, cl, **dict(kw, min_clusters=64))
    assert edge["status"] == "ok" and "se" in edge and edge["estimated_effect_under_assumptions"] == found["estimated_effect_under_assumptions"]
    short = dml_plr(x, t, y, s, rw, cl, **dict(kw, min_clusters=65))
    assert short["status"] == STATUS_INSUFFICIENT_CLUSTERS and short["n_clusters"] == 64 and not ({"se", "ci_low", "ci_high"} & set(short))
    assert short["estimated_effect_under_assumptions"] == found["estimated_effect_under_assumptions"] and "uncertainty_status" not in short
    one = dml_plr(x, t, y, s, rw, np.zeros_like(cl), **dict(kw, min_clusters=2))   # a single cluster is covered by the same rule
    assert one["status"] == STATUS_INSUFFICIENT_CLUSTERS and one["n_clusters"] == 1 and "se" not in one
    # the few-clusters warning (user instruction): an interval from fewer than 30 clusters always carries it; 30 or more never does
    assert found["notices"] == [] and edge["notices"] == []                                   # 64 clusters
    for count, expect in ((29, True), (30, False), (4, True), (200, False)):
        spread = np.arange(x.shape[0]) % count                                                  # exactly `count` clusters
        res = dml_plr(x, t, y, s, rw, spread, **dict(kw, min_clusters=4))
        assert res["status"] == "ok" and res["n_clusters"] == count and "ci_low" in res, (count, res)
        if expect:
            (note,) = res["notices"]
            assert note["code"] == FEW_CLUSTERS_CODE and note["n_clusters"] == count and note["threshold"] == CLUSTER_WARNING_THRESHOLD
            assert note["message"] == FEW_CLUSTERS_MESSAGE and "underestimated" in note["message"]
        else:
            assert res["notices"] == [], (count, res["notices"])
    below = dml_plr(x, t, y, s, rw, np.arange(x.shape[0]) % 3, **dict(kw, min_clusters=4))      # under the minimum: no interval, no warning needed
    assert below["status"] == STATUS_INSUFFICIENT_CLUSTERS and "notices" not in below and "ci_low" not in below
    assert few_clusters_notices(29)[0]["n_clusters"] == 29 and few_clusters_notices(30) == []
    thin = dml_plr(x[:300], t[:300], y[:300], s[:300], rw[:300], cl[:300], **dict(kw, min_fold_train=500))
    assert thin["status"] == "insufficient_data"
    constant = dml_plr(x, np.ones_like(t), y, s, rw, cl, **kw)
    assert constant["status"] == "degenerate_treatment", constant
    print(f"  group 5 estimator OK (planted 0.5 -> {found['estimated_effect_under_assumptions']:.3f} in "
          f"[{found['ci_low']:.3f}, {found['ci_high']:.3f}], naive {naive:.3f}; null {null['estimated_effect_under_assumptions']:.3f})")

    # group 6: end to end on synthetic OHLCV parquet
    ds.DATA_ROOT.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="sa_causal_selfcheck_", dir=str(ds.DATA_ROOT)) as tmp_name:
        tmp = Path(tmp_name)
        data = tmp / "data"
        data.mkdir()
        start = datetime.date(2018, 1, 1)
        for k, name in enumerate(("AAA", "BBB", "CCC")):
            g = np.random.default_rng(100 + k)
            n_rows = 1400
            c = 100.0 * np.exp(np.cumsum(g.normal(0.0, 0.01, n_rows)))
            o = np.concatenate([[100.0], c[:-1]])
            frame = {"date": [start + datetime.timedelta(days=i) for i in range(n_rows)], "open": o,
                     "high": np.maximum(o, c) * 1.002, "low": np.minimum(o, c) * 0.998, "close": c,
                     "volume": g.integers(1000, 5000, n_rows).astype(np.float64)}
            pl.DataFrame(frame).write_parquet(data / f"{name}.parquet")
        root = tmp / "runs"
        small = ("--min-samples", "300", "--min-fold-train", "100", "--oos-tail-days", "90", "--max-symbols", "3")
        assert run(_selfcheck_args(root, data, *small)) == EXIT_OK
        folder = next(p for p in root.iterdir() if p.is_dir())
        assert not folder.name.endswith(INCOMING_SUFFIX) and {p.name for p in folder.iterdir()} == {REPORT_JSON, REPORT_CSV}
        text = (folder / REPORT_JSON).read_text(encoding="utf-8")
        report = json.loads(text)
        assert "causal_effect" not in text and "NaN" not in text and "Infinity" not in text
        assert report["notices"] == list(NOTICES) and report["status"] == "ok" and len(report["assumptions"]) == len(ASSUMPTIONS)
        assert "estimated_effect_under_assumptions" in report["results"]["pooled"]
        assert set(report["results"]) == {"pooled", "per_symbol", "placebo_shuffled_treatment", "placebo_future_treatment"}
        last_date = start + datetime.timedelta(days=1399)
        cutoff = last_date - datetime.timedelta(days=90)
        assert report["data"]["main_block_end"] == cutoff.isoformat() and report["data"]["oos_rows_used"] is False
        assert report["split"]["purge_bars"] == purge_bars(report["config"]["horizon"])
        assert report["data"]["anchors"] == sum(report["data"]["anchors_per_symbol"].values())
        with (folder / REPORT_CSV).open(encoding="utf-8", newline="") as stream:
            table = list(csv.DictReader(stream))
        assert tuple(table[0].keys()) == CSV_COLUMNS and table[0]["estimand"] == "plr_effect_pooled" and table[0]["status"] == "ok"
        assert [r["estimand"] for r in table][-2:] == ["placebo_shuffled_treatment", "placebo_future_treatment"]
        assert any(r["estimand"] == "plr_effect_symbol:BBB" for r in table)
        # the out-of-sample tail is excluded: main rows = 1400 - 90, anchors = rows - warm-up - horizon, per symbol
        horizon_rows = report["config"]["horizon"]
        assert report["data"]["anchors"] == 3 * (1400 - 90 - LOOKBACK_BARS - horizon_rows), report["data"]
        assert report["data"]["dropped"]["warmup"] == 3 * LOOKBACK_BARS and report["data"]["dropped"]["missing_future"] == 3 * horizon_rows
        # too few clusters: every estimate is judged by its own cluster count; the report keeps the point estimate, no interval, exit 3
        root5 = tmp / "runs_clusters"
        assert run(_selfcheck_args(root5, data, *small, "--min-clusters", str(MIN_CLUSTERS_BAND[1]))) == EXIT_PREREQUISITE
        few = json.loads((next(root5.iterdir()) / REPORT_JSON).read_text(encoding="utf-8"))
        assert few["status"] == STATUS_INSUFFICIENT_CLUSTERS and NOTICE_NO_INTERVAL in few["notices"]
        assert few["uncertainty"]["min_clusters"] == MIN_CLUSTERS_BAND[1] and few["config"]["min_clusters"] == MIN_CLUSTERS_BAND[1]
        for result in [few["results"]["pooled"], few["results"]["placebo_shuffled_treatment"], *few["results"]["per_symbol"].values()]:
            assert result["status"] == STATUS_INSUFFICIENT_CLUSTERS and "estimated_effect_under_assumptions" in result
            assert not ({"se", "ci_low", "ci_high"} & set(result)), result
        with (next(root5.iterdir()) / REPORT_CSV).open(encoding="utf-8", newline="") as stream:
            few_table = list(csv.DictReader(stream))
        assert few_table[0]["status"] == STATUS_INSUFFICIENT_CLUSTERS and few_table[0]["theta"] != "" and few_table[0]["ci_low"] == ""
        assert few_table[0]["se"] == "" and few_table[0]["ci_high"] == ""
        assert report["uncertainty"]["min_clusters"] == DEFAULT_MIN_CLUSTERS and NOTICE_NO_INTERVAL not in report["notices"]
        # R4: a synthetic input is refused by default (exit 3, nothing published); with the explicit flag it runs and the report says so
        synth = tmp / "synthetic_data"
        synth.mkdir()
        for k, name in enumerate(("AAA", "BBB", "CCC")):
            pl.read_parquet(data / f"{name}.parquet").write_parquet(synth / f"{name}.parquet",
                                                                   metadata={ds.SYNTHETIC_ORIGIN_KEY: ds.SYNTHETIC_ORIGIN})
        root7 = tmp / "runs_synth_refused"
        assert run(_selfcheck_args(root7, synth, *small)) == EXIT_PREREQUISITE and not root7.exists()
        root8 = tmp / "runs_synth_allowed"
        assert run(_selfcheck_args(root8, synth, *small, "--allow-synthetic")) == EXIT_OK
        synth_report = json.loads((next(root8.iterdir()) / REPORT_JSON).read_text(encoding="utf-8"))
        assert NOTICE_SYNTHETIC in synth_report["notices"] and synth_report["data"]["synthetic"] is True
        assert NOTICE_SYNTHETIC not in report["notices"] and report["data"]["synthetic"] is False
        # a lower per-symbol minimum: the per-symbol intervals appear, each with its own warning; the pooled one (45 clusters) has none
        root6 = tmp / "runs_per_symbol"
        assert run(_selfcheck_args(root6, data, *small, "--min-clusters-per-symbol", "4")) == EXIT_OK
        folder6 = next(root6.iterdir())
        low = json.loads((folder6 / REPORT_JSON).read_text(encoding="utf-8"))
        assert low["config"]["min_clusters_per_symbol"] == 4 and low["uncertainty"]["min_clusters_per_symbol"] == 4
        assert low["uncertainty"]["warn_below_clusters"] == CLUSTER_WARNING_THRESHOLD and low["results"]["pooled"]["notices"] == []
        for name, result in low["results"]["per_symbol"].items():
            assert result["status"] == "ok" and "ci_low" in result and result["n_clusters"] < CLUSTER_WARNING_THRESHOLD, (name, result)
            assert result["notices"][0]["code"] == FEW_CLUSTERS_CODE and result["notices"][0]["n_clusters"] == result["n_clusters"]
        with (folder6 / REPORT_CSV).open(encoding="utf-8", newline="") as stream:
            low_table = {r["estimand"]: r for r in csv.DictReader(stream)}
        assert low_table["plr_effect_pooled"]["notices"] == "" and low_table["plr_effect_symbol:BBB"]["notices"] == FEW_CLUSTERS_CODE
        assert low_table["plr_effect_symbol:BBB"]["ci_low"] != ""
        # the default per-symbol minimum (30) keeps those estimates without an interval, and without a warning to carry
        assert all(r["status"] == STATUS_INSUFFICIENT_CLUSTERS and "notices" not in r for r in report["results"]["per_symbol"].values())
        assert report["config"]["min_clusters_per_symbol"] == DEFAULT_MIN_CLUSTERS_PER_SYMBOL
        # insufficient data: publishes a report with status and no estimate, exit 3
        root2 = tmp / "runs_thin"
        assert run(_selfcheck_args(root2, data, "--oos-tail-days", "0", "--min-samples", "100000", "--max-symbols", "3")) == EXIT_PREREQUISITE
        thin_report = json.loads((next(root2.iterdir()) / REPORT_JSON).read_text(encoding="utf-8"))
        assert thin_report["status"] == "insufficient_data" and "estimated_effect_under_assumptions" not in thin_report["results"]["pooled"]
        # external treatment: a per-symbol parquet with date + channel_0 (the C# export layout)
        ext = tmp / "ext"
        ext.mkdir()
        for k, name in enumerate(("AAA", "BBB", "CCC")):
            g = np.random.default_rng(500 + k)
            pl.DataFrame({"date": [start + datetime.timedelta(days=i) for i in range(1400)],
                          "channel_0": g.normal(size=1400)}).write_parquet(ext / f"{name}.parquet")
        root3 = tmp / "runs_ext"
        assert run(_selfcheck_args(root3, data, *small, "--treatment", "external", "--treatment-dir", str(ext))) == EXIT_OK
        ext_report = json.loads((next(root3.iterdir()) / REPORT_JSON).read_text(encoding="utf-8"))
        assert ext_report["config"]["confounders"] == list(FEATURE_NAMES) and ext_report["config"]["treatment"] == EXTERNAL
        # missing external file -> prerequisite, nothing published
        root4 = tmp / "runs_missing"
        (ext / "BBB.parquet").unlink()
        assert run(_selfcheck_args(root4, data, *small, "--treatment", "external", "--treatment-dir", str(ext))) == EXIT_PREREQUISITE
        assert not root4.exists() or not any(root4.iterdir())
        print(f"  group 6 end to end OK (anchors {report['data']['anchors']}, pooled effect "
              f"{report['results']['pooled']['estimated_effect_under_assumptions']:.4g})")

        # group 7 (D1, D3, D4): structured failures with an exit code and nothing published; the constant-treatment slope is NaN and silent
        import types
        import warnings
        from unittest import mock

        with warnings.catch_warnings():
            warnings.simplefilter("error")
            assert math.isnan(naive_slope(np.full(50, 2.0), np.arange(50.0)))
            assert math.isnan(naive_slope(1e-9 * np.arange(50.0), np.arange(50.0)))                  # SD ~1.4e-8 <= ds.EPSILON
            assert abs(naive_slope(np.arange(50.0), 3.0 * np.arange(50.0) + 1.0) - 3.0) < 1e-12      # an ordinary slope is unchanged
        calls = {"n": 0}

        def expired_clock() -> float:
            calls["n"] += 1
            return 0.0 if calls["n"] == 1 else 1e9          # the first reading is `started`; every later one is far past the deadline

        root9 = tmp / "runs_timeout"
        with mock.patch.object(sys.modules[__name__], "time", types.SimpleNamespace(monotonic=expired_clock)), \
                contextlib.redirect_stderr(io.StringIO()) as err:
            assert run(_selfcheck_args(root9, data, *small, "--max-seconds", "1")) == EXIT_FAILED
        assert "exceeded its run-duration bound" in err.getvalue() and not root9.exists(), "a timeout must publish nothing (no .incoming either)"
        dup = tmp / "dup_data"
        dup.mkdir()
        for name in ("AAA", "BBB", "CCC"):
            frame = pl.read_parquet(data / f"{name}.parquet")
            if name == "BBB":                                # a repeated date: not strictly increasing
                frame = frame.with_columns(pl.when(pl.int_range(pl.len()) == 5).then(pl.col("date").shift(1)).otherwise(pl.col("date")).alias("date"))
            frame.write_parquet(dup / f"{name}.parquet")
        root10 = tmp / "runs_dup"
        with contextlib.redirect_stderr(io.StringIO()) as err:
            assert run(_selfcheck_args(root10, dup, *small)) == EXIT_PREREQUISITE
        assert "strictly increasing" in err.getvalue() and not root10.exists(), err.getvalue()
        const = tmp / "const_treatment"
        const.mkdir()
        for name in ("AAA", "BBB", "CCC"):
            pl.DataFrame({"date": [start + datetime.timedelta(days=i) for i in range(1400)], "channel_0": np.full(1400, 2.0)}) \
                .write_parquet(const / f"{name}.parquet")
        root11 = tmp / "runs_const"
        with warnings.catch_warnings():
            warnings.simplefilter("error", RuntimeWarning)                                              # the 0/0 of the old slope was a RuntimeWarning
            assert run(_selfcheck_args(root11, data, *small, "--treatment", "external", "--treatment-dir", str(const))) == EXIT_PREREQUISITE
        const_report = json.loads((next(root11.iterdir()) / REPORT_JSON).read_text(encoding="utf-8"))
        assert const_report["results"]["pooled"]["status"] == "degenerate_treatment"
        print("  group 7 timeout / duplicate dates / constant treatment OK (exit codes, nothing published, no warning)")

        # group 8 (H1): additive records only -- CR1 + t interval, scale and fold diagnostics, nuisance flags, coverage study
        psi8 = np.array([1.0, 2.0, -1.0, 0.5, 0.5, -3.0])
        se8, g8 = cluster_robust_se(psi8, np.array([0, 0, 1, 1, 2, 2]), 2.0)               # cluster sums 3, -0.5, -2.5 -> sqrt(15.5) / (6 * 2)
        assert g8 == 3 and abs(se8 - math.sqrt(15.5) / 12.0) < 1e-12
        small8 = small_sample_interval(0.2, se8, 3, 0.95)                                    # hand values: CR1 factor sqrt(3 / 2), t(2) 0.975 = 4.302652729911275
        assert small8["method"] == "CR1_t" and small8["df"] == 2 and abs(small8["se"] - se8 * math.sqrt(1.5)) < 1e-15
        assert abs(small8["t_quantile"] - 4.302652729911275) < 1e-9 and abs(small8["ci_low"] - (0.2 - 4.302652729911275 * se8 * math.sqrt(1.5))) < 1e-9
        assert abs(small_sample_interval(0.0, 1.0, 30, 0.95)["t_quantile"] - 2.045229642132703) < 1e-9   # t(29) 0.975
        share8, hits8, total8 = cross_symbol_overlap(np.array([0, 0, 0, 1, 1, 1]), np.array([0, 1, 2, 0, 1, 2]),
                                                     np.array([True, False, False, True, False, False]),
                                                     np.array([False, True, True, False, True, True]), 1)
        assert (share8, hits8, total8) == (0.5, 2, 4), (share8, hits8, total8)                 # hand count: one hit per evaluating symbol out of two
        assert cross_symbol_overlap(np.zeros(6, dtype=np.int64), np.arange(6), np.arange(6) < 2, np.arange(6) >= 4, 1)[0] is None   # one symbol
        x8, t8, y8, s8, rw8, cl8 = _planted(4000, 0.5, 11)
        new_keys = {"fold_diagnostics", "cross_symbol_training_overlap"}
        plain8 = dml_plr(x8, t8, y8, s8, rw8, cl8, **kw)
        for shift, label in ((100, "shifted"), (0, "aligned")):
            pos8 = np.unique(np.datetime64("2020-01-01") + rw8 + shift * s8, return_inverse=True)[1].astype(np.int64)
            dated = dml_plr(x8, t8, y8, s8, rw8, cl8, date=np.datetime64("2020-01-01") + rw8 + shift * s8, date_pos=pos8,
                            symbol_names=["AAA", "BBB"], **kw)
            assert {k: v for k, v in dated.items() if k not in new_keys} == plain8, "the diagnostics must not change any other value"
            assert "fold_diagnostics" not in plain8 and len(dated["fold_diagnostics"]) == 5
            for k, entry in enumerate(dated["fold_diagnostics"]):
                first = np.datetime64("2020-01-01") + 400 * k                              # 2000 anchors per symbol, 5 contiguous blocks of 400
                assert entry["fold"] == k and entry["evaluation_span"]["AAA"] == [str(first), str(first + 399)], entry
                assert entry["evaluation_span"]["BBB"] == [str(first + shift), str(first + shift + 399)], entry
            if label == "shifted":
                overlap_shifted = dated["cross_symbol_training_overlap"]
            else:
                overlap_aligned = dated["cross_symbol_training_overlap"]
        # identical histories: every symbol purges the same calendar margins around its own block -> no overlap; shifted histories leak
        assert overlap_aligned == 0.0 < overlap_shifted < 1.0, (overlap_aligned, overlap_shifted)
        scales8 = plain8["treatment_scale_by_fold"]
        assert len(scales8) == 5 and plain8["scale_ratio_max_min"] == max(scales8) / min(scales8) >= 1.0
        assert plain8["small_sample_interval"]["df"] == plain8["n_clusters"] - 1
        assert abs(plain8["small_sample_interval"]["se"] - plain8["se"] * math.sqrt(plain8["n_clusters"] / (plain8["n_clusters"] - 1))) < 1e-15
        assert plain8["small_sample_interval"]["ci_high"] - plain8["small_sample_interval"]["ci_low"] > plain8["ci_high"] - plain8["ci_low"]
        assert "small_sample_interval" not in short and "small_sample_interval" not in below      # no headline interval -> none beside it
        # nuisance flags: the defaults are the table; the hard bands reject; another setting changes the config hash and the report
        assert (DEFAULT_NUISANCE_ESTIMATORS, DEFAULT_NUISANCE_DEPTH) == (NUISANCE_PARAMS["n_estimators"], NUISANCE_PARAMS["max_depth"])
        assert nuisance_params() == NUISANCE_PARAMS and nuisance_params(20, 2)["n_estimators"] == 20 and nuisance_params(20, 2)["learning_rate"] == 0.1
        for bad in (["--nuisance-estimators", "9"], ["--nuisance-estimators", "1001"], ["--nuisance-depth", "0"], ["--nuisance-depth", "9"]):
            try:
                validate_args(_parse_args(["--oos-tail-days", "0", *bad]))
            except ValueError:
                continue
            raise AssertionError(f"argument {bad} should be rejected")
        # the new keys of the default end-to-end report (group 6), and the headline interval is untouched beside them
        pooled8 = report["results"]["pooled"]
        assert pooled8["small_sample_interval"]["df"] == pooled8["n_clusters"] - 1 and pooled8["small_sample_interval"]["ci_level"] == pooled8["ci_level"]
        assert len(pooled8["fold_diagnostics"]) == 5 and set(pooled8["fold_diagnostics"][0]["evaluation_span"]) == {"AAA", "BBB", "CCC"}
        assert 0.0 <= pooled8["cross_symbol_training_overlap"] <= 1.0
        assert report["estimate_scale"] == ESTIMATE_SCALE and report["uncertainty"]["small_sample_interval"]["method"] == "CR1_t"
        assert report["interpretation"] == INTERPRETATION and report["config"]["nuisance"]["n_estimators"] == 100
        assert all("small_sample_interval" not in r for r in report["results"]["per_symbol"].values())   # insufficient clusters -> no interval
        for name, result in low["results"]["per_symbol"].items():
            assert result["small_sample_interval"]["df"] == result["n_clusters"] - 1, name
        root12 = tmp / "runs_nuisance"
        assert run(_selfcheck_args(root12, data, *small, "--nuisance-estimators", "20", "--nuisance-depth", "2")) == EXIT_OK
        tuned = json.loads((next(root12.iterdir()) / REPORT_JSON).read_text(encoding="utf-8"))
        assert tuned["config"]["nuisance"] == dict(nuisance_params(20, 2), learner="GradientBoostingRegressor")
        assert tuned["config_sha256"] != report["config_sha256"] and tuned["run_id"].split("_")[1] == tuned["config_sha256"][:8]
        # the coverage study: DGP moments, a 10-repetition smoke run (seeded), the file written by the CLI without any market data
        rng8 = np.random.default_rng(8)
        for dgp, lag1, cross in (("iid", 0.0, 0.0), ("ar1_within_symbol", COVERAGE_AR_RHO, 0.0), ("common_market_shock", 0.0, COVERAGE_COMMON_SHARE)):
            v = coverage_noise(dgp, 4, 20000, rng8)
            assert abs(float(v.var()) - 1.0) < 0.05, (dgp, float(v.var()))
            assert abs(float(np.corrcoef(v[0, 1:], v[0, :-1])[0, 1]) - lag1) < 0.03, dgp
            assert abs(float(np.corrcoef(v[0], v[1])[0, 1]) - cross) < 0.03, dgp
        px, pt, py, psy, prow, pcl = simulate_panel(5, 10, "iid", COVERAGE_THETA, np.random.default_rng(9))
        assert px.shape == (3150, 3) and np.unique(pcl).shape[0] == 50 and np.all(np.diff(prow[psy == 0]) == 1) and abs(float(pt.var()) - 1.25) < 0.15
        smoke_kw = dict(reps=10, seed=7, ci_level=0.95, folds=5, purge=purge, min_fold_train=200, nuisance=nuisance_params(30, 2),
                        deadline=time.monotonic() + 600, grid=((4, 5),))
        smoke = coverage_study(**smoke_kw)
        assert [(c["clusters"], c["dgp"]) for c in smoke["cells"]] == [(20, d) for d in COVERAGE_DGPS]
        for cell in smoke["cells"]:
            assert cell["reps_ok"] + cell["reps_failed"] == 10 and cell["reps_ok"] >= 1, cell
            for key in ("cr0_normal", "cr1_t"):
                assert 0.0 <= cell[key]["coverage"] <= 1.0 and cell[key]["mean_se"] > 0.0
                assert abs(cell[key]["monte_carlo_se"] - math.sqrt(cell[key]["coverage"] * (1 - cell[key]["coverage"]) / cell["reps_ok"])) < 1e-15
            assert cell["cr1_t"]["mean_se"] > cell["cr0_normal"]["mean_se"]                    # CR1 inflates by sqrt(G / (G - 1))
        assert coverage_study(**dict(smoke_kw, reps=2)) == coverage_study(**dict(smoke_kw, reps=2)) != coverage_study(**dict(smoke_kw, reps=2, seed=8))
        root13 = tmp / "runs_coverage"
        assert run(_selfcheck_args(root13, tmp / "no_such_data_dir", "--coverage-study", "--reps", "1", "--nuisance-estimators", "10",
                                   "--nuisance-depth", "1")) == EXIT_OK
        (study_file,) = list(root13.glob("coverage_*.json"))
        study_doc = json.loads(study_file.read_text(encoding="utf-8"))
        assert len(study_doc["cells"]) == len(COVERAGE_GRID) * len(COVERAGE_DGPS) and study_doc["judged"] is False
        assert "valid for these data-generating processes only" in study_doc["statement"] and study_doc["config"]["reps"] == 1
        assert [d.name for d in root13.iterdir()] == [study_file.name], "the coverage study writes one file and no run folder"
        print(f"  group 8 H1 additions OK (CR1+t hand case, fold spans, overlap {overlap_aligned:.3f} aligned / {overlap_shifted:.3f} shifted, "
              f"nuisance flags, coverage study smoke)")

        # group 9 (I1): --purge-scope global -- calendar blocks of the union axis and a cross-symbol purge; the default scope is untouched
        base9 = np.datetime64("2020-01-01")
        day_a, day_b = np.arange(20), np.arange(8, 20)                      # symbol B starts on day 8 of the union axis
        date9 = base9 + np.concatenate([day_a, day_b])
        sym9 = np.concatenate([np.zeros(20, dtype=np.int64), np.ones(12, dtype=np.int64)])
        fold9 = assign_folds_global(date9, 4)                               # 20 union days -> 4 intervals [0-4] [5-9] [10-14] [15-19]
        assert fold9.tolist() == (np.concatenate([day_a, day_b]) // 5).tolist()
        pos9 = np.unique(date9, return_inverse=True)[1].astype(np.int64)
        mask0 = training_mask_global(pos9, fold9, 0, 5)                     # evaluation [0-4] widened by 5 bars -> [-5, 9]
        assert mask0.tolist() == (pos9 > 9).tolist(), "DF18 A: B has no evaluation row in fold 0, yet its rows on days 8 and 9 are removed"
        assert not training_mask_global(pos9, assign_folds_global(date9, 25), 24, 5).any()      # more folds than days -> an empty fold, no mask
        # property: no training row of ANY symbol lies in an evaluation interval widened by the purge; blocks partition the rows in time order
        date_shift = np.datetime64("2020-01-01") + rw8 + 100 * s8
        pos_shift = np.unique(date_shift, return_inverse=True)[1].astype(np.int64)
        fold_shift = assign_folds_global(date_shift, 5)
        assert set(fold_shift) == set(range(5)) and all(np.all(np.diff(fold_shift[s8 == c]) >= 0) for c in (0, 1))
        last_end = -1
        for k in range(5):
            block = pos_shift[fold_shift == k]
            assert block.min() > last_end and not np.any(np.isin(pos_shift[fold_shift != k], np.arange(block.min(), block.max() + 1))), k
            last_end = int(block.max())
            tr9 = training_mask_global(pos_shift, fold_shift, k, purge)
            assert not (tr9 & (fold_shift == k)).any()
            assert not np.any((pos_shift[tr9] >= block.min() - purge) & (pos_shift[tr9] <= block.max() + purge)), k
        # the estimate: ok, no cross-symbol overlap by construction, spans inside the union intervals; the default scope is byte-identical
        glob = dml_plr(x8, t8, y8, s8, rw8, cl8, date=date_shift, date_pos=pos_shift, symbol_names=["AAA", "BBB"], purge_scope="global", **kw)
        assert glob["status"] == "ok" and glob["cross_symbol_training_overlap"] == 0.0 and len(glob["fold_diagnostics"]) == 5
        for k, entry in enumerate(glob["fold_diagnostics"]):
            union_days = np.unique(date_shift)
            interval = union_days[np.unique(pos_shift[fold_shift == k])]
            for span in entry["evaluation_span"].values():
                assert str(interval.min()) <= span[0] <= span[1] <= str(interval.max()), (k, span)
        assert dml_plr(x8, t8, y8, s8, rw8, cl8, purge_scope="symbol", **kw) == plain8
        for bad_call in (dict(purge_scope="bogus"), dict(purge_scope="global")):                   # unknown scope; global without dates
            try:
                dml_plr(x8, t8, y8, s8, rw8, cl8, **bad_call, **kw)
            except ValueError:
                continue
            raise AssertionError(f"{bad_call} should be rejected")
        quiet9 = contextlib.redirect_stderr(io.StringIO())
        with quiet9:
            try:
                _parse_args(["--oos-tail-days", "0", "--purge-scope", "bogus"])
            except SystemExit:
                pass
            else:
                raise AssertionError("an unknown --purge-scope must be refused")
        root14 = tmp / "runs_global"
        assert run(_selfcheck_args(root14, data, *small, "--purge-scope", "global")) == EXIT_OK
        glob_report = json.loads((next(root14.iterdir()) / REPORT_JSON).read_text(encoding="utf-8"))
        assert glob_report["split"]["purge_scope"] == "global" and glob_report["split"]["scheme"] == SCHEME_BY_SCOPE["global"]
        assert glob_report["config"]["purge_scope"] == "global" and glob_report["config_sha256"] != report["config_sha256"]
        assert glob_report["results"]["pooled"]["status"] == "ok" and glob_report["results"]["pooled"]["cross_symbol_training_overlap"] == 0.0
        assert report["split"]["purge_scope"] == "symbol" and "purge_scope" not in report["config"], "the default hash must not change"
        assert report["split"]["scheme"] == SCHEME_BY_SCOPE["symbol"] == "per_symbol_contiguous_blocks"
        print(f"  group 9 I1 purge scope OK (hand case, property over {len(fold_shift)} rows, pooled global theta "
              f"{glob_report['results']['pooled']['estimated_effect_under_assumptions']:.4g} vs symbol {report['results']['pooled']['estimated_effect_under_assumptions']:.4g})")

        # group 10 (CL): the calendar-quarter-clustered side interval (CL1 A / CL2 A / CL3 A); the headline numbers do not move
        hand_psi, hand_clusters = np.array([3.0, 1.0, -2.0, -2.0]), np.array([0, 0, 1, 1])
        hand = time_clustered_interval(0.5, hand_psi, hand_clusters, 2.0, 0.95, 0.4)           # sums [4, -4]: CR0 se = sqrt(32) / (4 * 2); CR1 x sqrt(2); t(1) = 12.7062047
        assert abs(hand["se_cr0"] - math.sqrt(32.0) / 8.0) < 1e-15 and abs(hand["se"] - 1.0) < 1e-12 and hand["df"] == 1 and hand["n_clusters"] == 2
        assert abs(hand["ci_high"] - (0.5 + 12.706204736432095)) < 1e-9 and abs(hand["se_ratio_to_headline"] - hand["se_cr0"] / 0.4) < 1e-15
        assert hand["status"] == "ok" and hand["cluster"] == TIME_CLUSTER_DEFINITION and [n["code"] for n in hand["notices"]] == [FEW_CLUSTERS_CODE]
        assert time_clustered_interval(0.5, hand_psi, hand_clusters, 2.0, 0.95, 0.0)["se_ratio_to_headline"] is None
        one_quarter = time_clustered_interval(0.5, hand_psi, np.zeros(4, dtype=np.int64), 2.0, 0.95, 0.4)       # CL2 A: no gate, but one cluster has no variance
        assert one_quarter["status"] == STATUS_INSUFFICIENT_TIME_CLUSTERS and one_quarter["n_clusters"] == 1 and "ci_low" not in one_quarter
        quarter8 = (rw8 // COVERAGE_BARS_PER_CLUSTER).astype(np.int64)                       # both planted symbols share the quarter index
        timed8 = dml_plr(x8, t8, y8, s8, rw8, cl8, time_cluster=quarter8, **kw)
        assert {k: v for k, v in timed8.items() if k != "time_clustered_interval"} == plain8, "the headline estimate, SE and intervals must not move"
        side = timed8["time_clustered_interval"]
        assert side["status"] == "ok" and side["n_clusters"] == int(quarter8.max()) + 1 == 32 and side["df"] == 31 and side["notices"] == []
        assert side["ci_low"] < timed8["estimated_effect_under_assumptions"] < side["ci_high"] and side["se"] > 0.0
        same = dml_plr(x8, t8, y8, s8, rw8, cl8, time_cluster=cl8, **kw)["time_clustered_interval"]       # the same clusters as the headline -> the CR1 + t interval
        for key in ("se", "df", "t_quantile", "ci_low", "ci_high"):
            assert abs(same[key] - plain8["small_sample_interval"][key]) < 1e-12, key
        assert abs(same["se_ratio_to_headline"] - 1.0) < 1e-12
        coarse = dml_plr(x8, t8, y8, s8, rw8, cl8, time_cluster=(rw8 // 200).astype(np.int64), **kw)["time_clustered_interval"]
        assert coarse["n_clusters"] == 10 and [n["code"] for n in coarse["notices"]] == [FEW_CLUSTERS_CODE] and coarse["notices"][0]["n_clusters"] == 10
        gated = dml_plr(x8, t8, y8, s8, rw8, cl8, time_cluster=quarter8, **dict(kw, min_clusters=65))     # the headline has no interval -> none beside it
        assert gated["status"] == STATUS_INSUFFICIENT_CLUSTERS and "time_clustered_interval" not in gated
        assert "time_clustered_interval" not in plain8 and "time_clustered_interval" not in dml_plr(x8, t8, y8, s8, rw8, cl8, **kw)
        pooled_report = report["results"]["pooled"]                                           # the run of group 5: every ok estimate carries it
        assert pooled_report["time_clustered_interval"]["cluster"] == TIME_CLUSTER_DEFINITION and pooled_report["time_clustered_interval"]["status"] == "ok"
        assert pooled_report["time_clustered_interval"]["n_clusters"] <= pooled_report["n_clusters"]
        assert "time_clustered_interval" in report["results"]["placebo_shuffled_treatment"]
        assert report["uncertainty"]["time_clustered_interval"]["where"] == "results.*.time_clustered_interval"
        assert report["uncertainty"]["cluster"] == "symbol_calendar_quarter" and "cluster_definitions" not in report["config"], "headline and default hash unchanged"
        for cell in smoke["cells"]:                                                           # CL3 A: the coverage study measures the third interval
            side_cell = cell["time_clustered_cr1_t"]
            assert side_cell["clusters"] == 5 and side_cell["reps_ok"] == cell["reps_ok"] and 0.0 <= side_cell["coverage"] <= 1.0 and side_cell["mean_se"] > 0.0
        assert study_doc["config"]["cluster_definitions"] == ["symbol_calendar_quarter", TIME_CLUSTER_DEFINITION] and study_doc["judged"] is False
        print(f"  group 10 CL calendar-quarter-clustered interval OK (hand case, headline unchanged, {side['n_clusters']} quarters, "
              f"SE ratio to the headline {side['se_ratio_to_headline']:.2f}, coverage axis in the study)")

    print("causal_analysis.py selfcheck: OK (10 groups passed)")


def _iso_date(text: str) -> str:
    try:
        return datetime.date.fromisoformat(text).isoformat()
    except ValueError as exc:
        raise argparse.ArgumentTypeError(f"expected YYYY-MM-DD, got {text!r}") from exc


def _parse_args(argv: List[str]) -> argparse.Namespace:
    p = argparse.ArgumentParser(description="Cross-fitted partially linear (DML) effect estimate with an explicit assumption list.")
    p.add_argument("--selfcheck", action="store_true", help="Run the assertions and exit.")
    p.add_argument("--data-dir", type=Path, default=ds.DEFAULT_DATA_DIR)
    p.add_argument("--allow-synthetic", action="store_true",
                   help="Read a synthetic folder (generate_synthetic.py output). Refused by default; the report then states the data are not observed.")
    p.add_argument("--symbols", default=None, help="Comma list of symbols (default: the first --max-symbols files).")
    p.add_argument("--max-symbols", type=int, default=DEFAULT_MAX_SYMBOLS)
    p.add_argument("--start", type=_iso_date, default=None, help="First calendar date loaded.")
    p.add_argument("--end", type=_iso_date, default=None, help="Last calendar date loaded.")
    p.add_argument("--oos-tail-days", type=int, default=None,
                   help="REQUIRED (except with --coverage-study): calendar days of the out-of-sample tail to EXCLUDE (as in the training run); "
                        "0 = use every row.")
    p.add_argument("--treatment", choices=TREATMENTS, default=DEFAULT_TREATMENT)
    p.add_argument("--treatment-dir", type=Path, default=None, help="Directory of <symbol>.parquet (date + column) for --treatment external.")
    p.add_argument("--treatment-column", default=DEFAULT_EXTERNAL_COLUMN)
    p.add_argument("--horizon", type=int, default=ds.DEFAULT_HORIZON, help="Outcome horizon in bars.")
    p.add_argument("--folds", type=int, default=DEFAULT_FOLDS)
    p.add_argument("--ci-level", type=float, default=DEFAULT_CI_LEVEL)
    p.add_argument("--min-samples", type=int, default=DEFAULT_MIN_SAMPLES)
    p.add_argument("--min-fold-train", type=int, default=DEFAULT_MIN_FOLD_TRAIN)
    p.add_argument("--min-clusters-per-symbol", type=int, default=DEFAULT_MIN_CLUSTERS_PER_SYMBOL,
                   help=f"Own minimum for the per-symbol estimates (band {MIN_CLUSTERS_BAND[0]}..{MIN_CLUSTERS_BAND[1]}; env {MIN_CLUSTERS_PER_SYMBOL_ENV}). "
                        f"An interval built from fewer than {CLUSTER_WARNING_THRESHOLD} clusters always carries a warning in its notices.")
    p.add_argument("--min-clusters", type=int, default=DEFAULT_MIN_CLUSTERS,
                   help=f"Fewer clusters -> status insufficient_clusters, no SE / interval (band {MIN_CLUSTERS_BAND[0]}..{MIN_CLUSTERS_BAND[1]}; env {MIN_CLUSTERS_ENV}).")
    p.add_argument("--seed", type=int, default=DEFAULT_SEED)
    p.add_argument("--max-seconds", type=int, default=MAX_SECONDS, help=f"Run bound; env {MAX_SECONDS_ENV}.")
    p.add_argument("--out-root", type=Path, default=OUT_DIR)
    p.add_argument("--purge-scope", choices=PURGE_SCOPES, default=DEFAULT_PURGE_SCOPE,
                   help="symbol (default): per-symbol time blocks, the purge only around the evaluating symbol's block. global: calendar blocks of the "
                        "union of all anchor dates; the purge (in bars of that axis) removes EVERY symbol's rows near an evaluation interval, so a "
                        "common market shock cannot sit on both sides of a split.")
    n = p.add_argument_group("nuisance learner (H1)")
    n.add_argument("--nuisance-estimators", type=int, default=DEFAULT_NUISANCE_ESTIMATORS,
                   help=f"Trees of each nuisance GradientBoostingRegressor (band {NUISANCE_ESTIMATORS_BAND[0]}..{NUISANCE_ESTIMATORS_BAND[1]}; "
                        f"env {NUISANCE_ESTIMATORS_ENV}). Part of the config hash.")
    n.add_argument("--nuisance-depth", type=int, default=DEFAULT_NUISANCE_DEPTH,
                   help=f"Tree depth (band {NUISANCE_DEPTH_BAND[0]}..{NUISANCE_DEPTH_BAND[1]}; env {NUISANCE_DEPTH_ENV}). Part of the config hash.")
    c = p.add_argument_group("coverage study (H1; no market data)")
    c.add_argument("--coverage-study", action="store_true",
                   help="Simulate panels with a known effect and measure the coverage of the CR0 + normal and CR1 + t intervals. "
                        "Reports a measurement, no pass / fail; valid for the built-in data-generating processes only.")
    c.add_argument("--reps", type=int, default=DEFAULT_COVERAGE_REPS,
                   help=f"Repetitions per (clusters, DGP) cell (default {DEFAULT_COVERAGE_REPS}; band {COVERAGE_REPS_BAND[0]}..{COVERAGE_REPS_BAND[1]}).")
    args = p.parse_args(argv)
    if args.oos_tail_days is None and not args.coverage_study:
        p.error("the following arguments are required: --oos-tail-days (using every row is an explicit choice, K17)")
    return args


if __name__ == "__main__":
    if "--selfcheck" in sys.argv[1:]:
        _run_selfcheck()
    else:
        _args = _parse_args(sys.argv[1:])
        try:
            (validate_coverage_args if _args.coverage_study else validate_args)(_args)
        except ValueError as _exc:
            print(f"causal_analysis: invalid argument: {_exc}", file=sys.stderr)
            raise SystemExit(2)
        raise SystemExit(run(_args))
