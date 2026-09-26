"""Dataset construction for ONNX trend-predictor training.

Every feature transform in this module is a deliberate mirror of the authoritative
C# implementation so that training-time and inference-time feature distributions
match exactly (no data drift):

    StockAnalyzer.Core/Services/MLDataProcessor.cs
        NormalizeCandles      -> normalize_ohlcv_minmax
        ComputeLogReturns     -> compute_log_returns
        ComputeZScore         -> zscore_standardized (per channel)
    StockAnalyzer.Core/Services/PredictionService.cs
        NormalizeZScoreOhlcv  -> zscore_standardized (channel interleave)

Where the WebAI review proposal and the shipped C# code disagree (e.g. the price
Min-Max uses the Low series for the minimum and the High series for the maximum,
not the min/max over all four OHLC series), the C# code is the source of truth
per the project's "specification authority = implementation" rule.

Class index order mirrors PredictionSettings.ClassLabels = ["Up", "Down", "Neutral"].

Run ``python dataset.py --selfcheck`` to verify the boundary math, or
``python dataset.py --data-dir Data/Daily --feature-mode ohlcv_minmax`` to build
a dataset from real parquet data and print its shape and class distribution.
"""

from __future__ import annotations

import argparse
import datetime
import json
import os
import shutil
import sys
import tempfile
from pathlib import Path
from typing import Dict, Iterable, List, Tuple

import numpy as np
import polars as pl

# --- Constants mirroring the C# single source of truth -----------------------

# mirrors IMLDataProcessor.Epsilon (1e-7f): boundary guard for degenerate ranges.
EPSILON: float = 1e-7
# mirrors IMLDataProcessor.SoftmaxSumTolerance (1e-4f): max |1 - sum(p)| before a
# re-softmax is forced. Single source of truth for downstream verification scripts.
SOFTMAX_SUM_TOLERANCE: float = 1e-4
# canonical window length: matches StockAnalyzer.Avalonia PredictionSettings.WindowSize
# and PredictionSettingsManager.DefaultWindowSize (both 75). Single source of truth for
# the default look-back; override per run with --window.
DEFAULT_WINDOW: int = 75
# default number of expanding-window folds; also the SSoT for the trainers' --wf-splits.
DEFAULT_WF_SPLITS: int = 5
# a bar gap flags a discontinuous history (halt / delist / months missing) only when it
# is BOTH this many times the typical step AND at least MIN_GAP_WARNING_DAYS wide, so
# routine long-weekend / national-holiday gaps stay silent. window and horizon are bar
# counts, so a genuine calendar hole silently distorts the look-back / horizon span.
MODAL_DATE_STEP_TOLERANCE: int = 5
MIN_GAP_WARNING_DAYS: int = 30
# a single bar whose |close / prev_close - 1| exceeds this is a suspected unadjusted
# corporate action (split / large special dividend). The dataset assumes adjusted OHLCV;
# an unadjusted split otherwise trains as a genuine crash / spike. 0.45 catches a clean
# 2:1 split (exactly -0.50) with margin; it will also flag the rare true >45% single-day
# move (VIX-style instruments, halted-then-resumed names) -- a warning to eyeball, not an error.
SUSPECTED_CORP_ACTION_RETURN: float = 0.45
# mirrors PredictionService: OhlcvMinMax/ZScoreStandardized -> 5 channels/bar, LogReturn -> 1.
FEATURES_OHLCV: int = 5
FEATURES_LOGRETURN: int = 1
# intrabar OHLC log-return mode: gap, high/open, low/open, close/open.
FEATURES_LOGRETURN_OHLC: int = 4
# mirrors PredictionSettings.ClassLabels index order ["Up", "Down", "Neutral"].
CLASS_UP: int = 0
CLASS_DOWN: int = 1
CLASS_NEUTRAL: int = 2
CLASS_LABELS: Tuple[str, str, str] = ("Up", "Down", "Neutral")

# --- Training-side only parameters (no C# equivalent) ------------------------

# forward horizon (bars) and neutral band half-width for label generation.
DEFAULT_HORIZON: int = 5
DEFAULT_THRESHOLD: float = 0.005

# triple-barrier labeling (label_triple_barrier). The anchor-time volatility scale is a
# Wilder-smoothed ATR over this many bars -- the same convention as C#
# CoreAtrIndicator / ChartConstants.DefaultAtrPeriod (TR = max(H-L, |H-Cprev|, |L-Cprev|),
# seeded by the SMA of the first `period` true ranges). The take-profit / stop-loss
# barriers sit k_tp / k_sl ATRs above / below the anchor close; a bar that touches both
# in the same session is resolved to the stop (conservative).
DEFAULT_TB_ATR_PERIOD: int = 14
DEFAULT_TB_K_TP: float = 2.0
DEFAULT_TB_K_SL: float = 2.0

# bars per window emitted by --emit-parity-vectors for the C#/Python parity test.
PARITY_VECTOR_WINDOW: int = 12
# real-symbol slices to include in that file, alongside the fixed synthetic edge cases.
PARITY_REAL_SLICES: int = 4

# parquet column order used for every ndarray in this module.
OHLCV_COLUMNS: Tuple[str, str, str, str, str] = ("open", "high", "low", "close", "volume")
# public channel indices into an OHLCV row (mirrors OHLCV_COLUMNS order) for downstream consumers.
OPEN, HIGH, LOW, CLOSE, VOLUME = 0, 1, 2, 3, 4
PRICE_SLICE: slice = slice(OPEN, CLOSE + 1)  # the O, H, L, C channels

# feature-mode wire string -> channels per bar. Single source of truth for the set of
# modes and their tensor width; mirrored on the C# side by
# PredictionModelMetadata.ParseFeatureMode / PredictionService.ResolvedFeaturesPerBar.
_MODE_CHANNELS: Dict[str, int] = {
    "ohlcv_minmax": FEATURES_OHLCV,
    "log_return": FEATURES_LOGRETURN,
    "zscore": FEATURES_OHLCV,
    "zscore_joint": FEATURES_OHLCV,
    "log_return_ohlc": FEATURES_LOGRETURN_OHLC,
}
FEATURE_MODES: Tuple[str, ...] = tuple(_MODE_CHANNELS)
# distinct tensor widths across all modes; consumed by verify_model.check_contract.
CHANNEL_COUNTS: Tuple[int, ...] = tuple(sorted(set(_MODE_CHANNELS.values())))

# Variable-width composed-feature mode (mirrors C# PredictionFeatureMode.ComposedFeatures /
# StockAnalyzer.Core.Models.Training.FeatureSpec). Deliberately kept OUT of _MODE_CHANNELS /
# FEATURE_MODES: those two stay the fixed-width SSoT for the 5 modes above; this mode's
# channel count depends on the caller-supplied feature_spec instead.
COMPOSED_FEATURES_MODE: str = "composed_features"
# Price types this release can compose (mirrors PriceDataHelper.ExtractPrice's non-Heikin-Ashi
# branch, plus true_high/true_low which need only a 1-bar lookback). The 4 Heikin-Ashi
# PriceType wire strings are intentionally excluded: PriceDataHelper's Heikin-Ashi recursion
# carries state across the *entire* candle history, which this module's per-window builder
# does not yet thread -- composing a Heikin-Ashi channel is deferred to a follow-up task and
# rejected with an explicit error rather than silently approximated.
COMPOSED_PRICE_TYPES: Tuple[str, ...] = (
    "open", "high", "low", "close", "median", "midpoint", "typical", "weighted", "average",
    "true_high", "true_low",
)
# mirrors C# ChannelNormalization wire strings (TrainingConfigJson.ChannelNormalizationJsonConverter).
COMPOSED_NORMALIZATIONS: Tuple[str, ...] = ("none", "window_min_max", "window_zscore")

# D03 resource bounds (StockAnalyzer/CLAUDE.md decision register), scoped to composed_features
# only: the other 5 fixed-width modes' small, constant channel counts cannot realistically
# approach these bounds, so they are left unguarded rather than widening this task's scope.
# Configuration-driven per the Change Policy's hardcoded-value prohibition: each reads an
# environment variable, falling back to the same default as its C# mirror if unset/invalid.
# MAX_COMPOSED_CHANNELS mirrors IStockAnalyzerSettings.PredictionMaxComposedChannels;
# MAX_COMPOSED_TENSOR_SIZE_MB mirrors PredictionMaxComposedTensorSizeMB (that C# guard applies
# to every mode; this one is composed_features-only, per the scope note above).
# MAX_COMPOSED_BATCH_SAMPLES has no C# mirror -- inference is always exactly one window, so no
# "batch" concept exists there; it is Python-only, hence env-var-only (no appsettings.json entry).
def _env_int(name: str, default: int) -> int:
    """Read a positive-int environment variable, falling back to *default* if unset or invalid."""
    raw = os.environ.get(name)
    if raw is None:
        return default
    try:
        value = int(raw)
    except ValueError:
        return default
    return value if value > 0 else default


MAX_COMPOSED_CHANNELS: int = _env_int("SA_MAX_COMPOSED_CHANNELS", 1024)
MAX_COMPOSED_TENSOR_SIZE_MB: int = _env_int("SA_MAX_COMPOSED_TENSOR_SIZE_MB", 256)
MAX_COMPOSED_BATCH_SAMPLES: int = _env_int("SA_MAX_COMPOSED_BATCH_SAMPLES", 10_000)


def _resolve_composed_price(
    o: float, h: float, l: float, c: float, price_type: str, prev_close: float | None,
) -> float:
    """One-bar price selection; mirror of ``PriceDataHelper.ExtractPrice``'s non-Heikin-Ashi
    branch. ``prev_close`` is the previous bar's Close (``None`` only at the very first bar of
    the full series), used solely by ``true_high`` / ``true_low``.
    """
    if price_type == "open":
        return o
    if price_type == "high":
        return h
    if price_type == "low":
        return l
    if price_type == "close":
        return c
    if price_type == "median":
        return (h + l) / 2.0
    if price_type == "midpoint":
        return (o + c) / 2.0
    if price_type == "typical":
        return (h + l + c) / 3.0
    if price_type == "weighted":
        return (h + l + 2.0 * c) / 4.0
    if price_type == "average":
        return (o + h + l + c) / 4.0
    if price_type == "true_high":
        return h if prev_close is None else max(h, prev_close)
    if price_type == "true_low":
        return l if prev_close is None else min(l, prev_close)
    raise ValueError(
        f"Unsupported composed price_type {price_type!r}; expected one of {COMPOSED_PRICE_TYPES} "
        "(Heikin-Ashi price types are not yet supported for composed features)"
    )


def compose_price_channel_window(
    full_ohlcv: np.ndarray, start: int, window: int, price_type: str,
) -> np.ndarray:
    """Builds one channel's raw (unnormalized) ``(window,)`` float64 series for the bars
    ``[start, start + window)`` of *full_ohlcv* (the symbol's full array, not just the window
    slice -- ``true_high``/``true_low`` need the bar immediately before ``start`` when
    ``start > 0``, matching ``PriceDataHelper.ExtractPriceSeries``'s full-series ``i > 0`` rule).
    """
    if price_type not in COMPOSED_PRICE_TYPES:
        raise ValueError(
            f"Unsupported composed price_type {price_type!r}; expected one of {COMPOSED_PRICE_TYPES} "
            "(Heikin-Ashi price types are not yet supported for composed features)"
        )
    out = np.empty(window, dtype=np.float64)
    for i in range(window):
        row = start + i
        o, h, low_, c = (
            full_ohlcv[row, OPEN], full_ohlcv[row, HIGH], full_ohlcv[row, LOW], full_ohlcv[row, CLOSE],
        )
        prev_close = full_ohlcv[row - 1, CLOSE] if row > 0 else None
        out[i] = _resolve_composed_price(o, h, low_, c, price_type, prev_close)
    return out


def _apply_channel_normalization(raw: np.ndarray, normalization: str) -> np.ndarray:
    """Per-channel, per-window normalization for one composed-feature channel.

    ``window_min_max`` mirrors :func:`normalize_ohlcv_minmax`'s price-branch convention (flat
    range -> 0.5, else clipped ``(x-min)/(max-min)`` to ``[0,1]``). ``window_zscore`` is
    :func:`zscore_standardized`'s per-channel population Z-Score (``ddof=0``, flat -> 0.0),
    generalized to an arbitrary single channel instead of the fixed 5 OHLCV columns.
    """
    if normalization == "none":
        return raw
    if normalization == "window_min_max":
        lo, hi = raw.min(), raw.max()
        rng = hi - lo
        if rng <= EPSILON:
            return np.full_like(raw, 0.5)
        return np.clip((raw - lo) / rng, 0.0, 1.0)
    if normalization == "window_zscore":
        mu = raw.mean()
        sigma = np.sqrt(np.mean((raw - mu) ** 2))
        return np.zeros_like(raw) if sigma <= EPSILON else (raw - mu) / sigma
    raise ValueError(
        f"Unsupported channel normalization {normalization!r}; expected one of {COMPOSED_NORMALIZATIONS}"
    )


def compose_features_window(
    full_ohlcv: np.ndarray, start: int, window: int, channels: List[dict],
    indicator_columns: Dict[int, np.ndarray] | None = None,
) -> np.ndarray:
    """Builds the ``(window, len(channels))`` float32 tensor for one window from a parsed
    ``feature_spec["channels"]`` list. Each entry is either ``{"kind": "price", "price":
    <price_type>, "normalization": <normalization>}`` or ``{"kind": "indicator", "indicator":
    <name>, "normalization": <normalization>}``.

    An Indicator-kind channel's raw (unnormalized) window comes from
    ``indicator_columns[index]`` (``index`` = the channel's position in *channels*, matching the
    ``channel_{index}`` parquet columns ``IndicatorChannelExporter.cs`` writes) -- this function
    only slices ``[start, start + window)`` out of it and applies the channel's normalization,
    exactly as a Price-kind channel does with :func:`compose_price_channel_window`. The caller
    (:func:`build_dataset`) is responsible for building *indicator_columns* once per symbol,
    already date-aligned against *full_ohlcv* (single formula source: C#'s ``ICoreIndicator``
    implementations compute the values; this function never re-derives indicator math). A missing
    ``indicator_columns`` entry for an Indicator-kind channel raises ``ValueError`` rather than
    silently defaulting to zero or skipping the channel.
    """
    tensor_bytes = window * len(channels) * 4  # float32
    max_bytes = MAX_COMPOSED_TENSOR_SIZE_MB * 1024 * 1024
    if tensor_bytes > max_bytes:
        raise ValueError(
            f"composed_features: window={window} x channels={len(channels)} "
            f"({tensor_bytes:,} bytes) exceeds the MaxTensorSizeMB bound of "
            f"{MAX_COMPOSED_TENSOR_SIZE_MB} MB"
        )

    cols: List[np.ndarray] = []
    for index, ch in enumerate(channels):
        kind = ch.get("kind")
        if kind == "price":
            raw = compose_price_channel_window(full_ohlcv, start, window, ch["price"])
        elif kind == "indicator":
            if indicator_columns is None or index not in indicator_columns:
                raise ValueError(
                    f"composed_features: indicator channel [{index}] has no matching "
                    "indicator_columns entry (missing or failed export alignment)"
                )
            raw = np.asarray(indicator_columns[index][start:start + window], dtype=np.float64)
            if raw.shape[0] != window:
                raise ValueError(
                    f"composed_features: indicator channel [{index}] has {raw.shape[0]} aligned "
                    f"value(s) for bars [{start}, {start + window}); expected {window}"
                )
        else:
            raise ValueError(f"composed_features: unsupported channel kind {kind!r}")
        cols.append(_apply_channel_normalization(raw, ch.get("normalization", "none")))
    return np.stack(cols, axis=1).astype(np.float32)


def _build_indicator_columns(
    channels: List[dict], dates: np.ndarray | None, indicator_export_path: str | Path | None,
) -> Dict[int, np.ndarray] | None:
    """One-time, per-symbol date-aligned load of every Indicator-kind channel's exported values,
    keyed by the channel's position in *channels* (matching the ``channel_{index}`` columns
    ``IndicatorChannelExporter.cs`` writes). Called once by :func:`build_dataset` per symbol, not
    once per window -- ``compose_features_window`` only slices the result.

    Returns ``None`` when *channels* has no Indicator-kind entry (a Price-only composed_features
    run never touches the export parquet). Otherwise requires both *dates* (the requesting
    caller's own ``datetime64[D]`` date column -- the symbol's full OHLCV history for
    :func:`build_dataset`, but only a *subset* of it for ``run_training.py``'s ``evaluate_oos``,
    which scores a fixed OOS tail slice) and *indicator_export_path*; missing either, a missing
    export file, or any requested date absent from the export's ``date`` column all raise
    ``ValueError`` (fail closed -- never silently falls back to Price-only or zero-fills).

    Each requested date is looked up independently by value (not by row position), so *dates*
    need not equal the export's full row count or order -- it may be any subset, as long as every
    requested date is present exactly once in the export. This is a backward-compatible
    generalization of the original exact-row-count-and-order check: a caller whose *dates* is the
    export's full date range, in the export's own order, gets byte-identical results either way.
    """
    indicator_indices = [i for i, ch in enumerate(channels) if ch.get("kind") == "indicator"]
    if not indicator_indices:
        return None

    if dates is None or indicator_export_path is None:
        raise ValueError(
            "composed_features: an Indicator-kind channel requires both `dates` and "
            "`indicator_export_path` (TrainingJobConfig.IndicatorChannelExportPaths, set by "
            "TrainingOrchestrator before run_training.py starts)"
        )

    export_path = Path(indicator_export_path)
    if not export_path.exists():
        raise ValueError(f"composed_features: indicator export file not found: {export_path}")

    frame = pl.read_parquet(export_path)
    exported_dates = frame.get_column("date").to_numpy().astype("datetime64[D]")
    requested_dates = np.asarray(dates).astype("datetime64[D]").reshape(-1)

    date_to_row: Dict[np.datetime64, int] = {}
    for row, d in enumerate(exported_dates):
        if d in date_to_row:
            raise ValueError(
                f"composed_features: indicator export {export_path.name} has duplicate date "
                f"{d} (expected at most one row per date)"
            )
        date_to_row[d] = row

    row_indices = np.empty(requested_dates.shape[0], dtype=np.int64)
    for i, d in enumerate(requested_dates):
        row = date_to_row.get(d)
        if row is None:
            raise ValueError(
                f"composed_features: date {d} is missing from indicator export "
                f"{export_path.name}"
            )
        row_indices[i] = row

    indicator_columns: Dict[int, np.ndarray] = {}
    for index in indicator_indices:
        column_name = f"channel_{index}"
        if column_name not in frame.columns:
            raise ValueError(
                f"composed_features: indicator export {export_path.name} is missing column "
                f"{column_name!r} for channel [{index}]"
            )
        full_col = frame.get_column(column_name).to_numpy().astype(np.float64)
        indicator_columns[index] = full_col[row_indices]

    return indicator_columns


def _validate_feature_spec_channels(feature_spec: dict | None) -> List[dict]:
    """Structural guard shared by :func:`build_dataset` and its multi-symbol wrappers: a
    ``composed_features`` run must carry a non-empty channel list. Per-channel content (kind /
    price / normalization) is validated by :func:`compose_features_window` as each window is
    built, so a bad value fails on the first window rather than being silently coerced.
    """
    if not feature_spec or not feature_spec.get("channels"):
        raise ValueError("composed_features requires a non-empty feature_spec['channels']")
    channels = list(feature_spec["channels"])
    if len(channels) > MAX_COMPOSED_CHANNELS:
        raise ValueError(
            f"composed_features: {len(channels)} channels exceeds the MaxChannels bound of "
            f"{MAX_COMPOSED_CHANNELS}"
        )
    return channels

# ONNX metadata_props keys holding the training / validation calendar spans. Single
# source of truth: onnx_meta._DATE_RANGE_KEYS aliases this. train_val_date_ranges
# fills them from the dataset's own `date` column.
DATE_RANGE_KEYS: Tuple[str, str, str, str] = (
    "training_start",
    "training_end",
    "validation_start",
    "validation_end",
)

# repo root = .../i:/stock ; this file is .../StockAnalyzer.Python/training/dataset.py
_REPO_ROOT: Path = Path(__file__).resolve().parents[2]
DATA_ROOT: Path = _REPO_ROOT / "Data"
DEFAULT_DATA_DIR: Path = DATA_ROOT / "Daily"

# timeframe wire string (mirrors C# StockAnalyzer.Core.Models.Training.TrainingTimeframe) ->
# the parquet subdirectory produced by generate_timeframes.py. Single source of truth for the
# Daily/Weekly/Monthly mapping on the Python side.
TIMEFRAME_DIRS: Dict[str, str] = {"daily": "Daily", "weekly": "Weekly", "monthly": "Monthly"}


def resolve_timeframe_dir(timeframe: str, data_root: Path | str | None = None) -> Path:
    """Map a timeframe wire string to its parquet directory.

    ``daily`` -> ``<repo>/Data/Daily``; ``weekly`` / ``monthly`` map to the directories
    written by ``generate_timeframes.py``. The lookup is case-insensitive. Raises
    ``ValueError`` on an unrecognized value.
    """
    key = str(timeframe).strip().lower()
    if key not in TIMEFRAME_DIRS:
        raise ValueError(
            f"Unknown timeframe {timeframe!r}; expected one of {sorted(TIMEFRAME_DIRS)}"
        )
    root = Path(data_root) if data_root is not None else DATA_ROOT
    return root / TIMEFRAME_DIRS[key]


def _parse_date_bound(value: str | datetime.date | None) -> datetime.date | None:
    """Normalize a ``--start`` / ``--end`` bound to ``datetime.date`` (or ``None``)."""
    if value is None:
        return None
    if isinstance(value, datetime.datetime):
        return value.date()
    if isinstance(value, datetime.date):
        return value
    return datetime.date.fromisoformat(str(value).strip())


# --- Parquet loading --------------------------------------------------------


def _date_gap_warning(symbol: str, dates: np.ndarray | None) -> str | None:
    """Return a warning string if *dates* has a bar gap far larger than the typical
    step, else ``None``.

    ``window`` and ``horizon`` are counted in bars (rows), so a trading halt,
    delisting gap or long holiday shrinks neither the effective look-back nor the
    label horizon in calendar terms -- this surfaces that instead of hiding it.
    """
    if dates is None or dates.shape[0] <= 2:
        return None
    steps = np.diff(dates).astype("timedelta64[D]").astype(np.int64)
    steps = steps[steps > 0]
    if steps.size == 0:
        return None
    modal = int(np.bincount(steps).argmax())
    worst = int(steps.max())
    if modal > 0 and worst > max(MODAL_DATE_STEP_TOLERANCE * modal, MIN_GAP_WARNING_DAYS):
        return (f"{symbol}: largest bar gap {worst}d vs typical {modal}d "
                f"(discontinuous history; window/horizon count bars, not days)")
    return None


def _corp_action_warning(symbol: str, close: np.ndarray) -> str | None:
    """Return a warning string if any consecutive-bar close return exceeds
    :data:`SUSPECTED_CORP_ACTION_RETURN`, else ``None``.

    The dataset assumes split- and dividend-adjusted OHLCV. An unadjusted split
    shows up here as a single ~-67% (1:3) or ~+100% (2:1) bar and, unflagged,
    trains as a genuine crash or spike.
    """
    close = np.asarray(close, dtype=np.float64)
    if close.shape[0] < 2:
        return None
    prev, curr = close[:-1], close[1:]
    valid = (prev > 0.0) & (curr > 0.0) & np.isfinite(prev) & np.isfinite(curr)
    if not np.any(valid):
        return None
    ret = np.abs(curr[valid] / prev[valid] - 1.0)
    worst = float(ret.max())
    if worst > SUSPECTED_CORP_ACTION_RETURN:
        row = int(np.flatnonzero(valid)[int(ret.argmax())] + 1)
        return (f"{symbol}: bar return {worst:.0%} at row {row} exceeds "
                f"{SUSPECTED_CORP_ACTION_RETURN:.0%} (suspected unadjusted split/dividend; "
                f"dataset expects adjusted OHLCV)")
    return None


def load_parquet_dir(
    data_dir: Path | str,
    *,
    strict_corp_actions: bool = False,
    return_dates: bool = False,
    symbols: Iterable[str] | None = None,
    start: str | datetime.date | None = None,
    end: str | datetime.date | None = None,
):
    """Load every ``*.parquet`` under *data_dir* into ``{symbol: ndarray(rows, 5)}``.

    Input OHLCV MUST be split- and dividend-adjusted. Columns are ordered per
    :data:`OHLCV_COLUMNS` and rows are sorted by ``date`` ascending so
    chronological order is guaranteed regardless of file layout. A symbol whose
    ``date`` column has a large discontinuity prints a one-line warning (see
    :func:`_date_gap_warning`); a symbol with a bar return that looks like an
    unadjusted corporate action prints a warning, or raises ``ValueError`` when
    *strict_corp_actions* is true (see :func:`_corp_action_warning`).

    When *return_dates* is true the return value is ``(symbols, dates)`` where
    ``dates[symbol]`` is a ``datetime64[D]`` array aligned row-for-row with
    ``symbols[symbol]``; a file with no ``date`` column then raises ``ValueError``.
    The default return is the ``{symbol: ndarray}`` mapping alone (unchanged).

    Optional filters (all default to "no filter", so existing callers are
    unaffected):

    * *symbols* -- restrict to these tickers (matched case-insensitively against
      the parquet file stem). An explicit but empty filter raises ``ValueError``
      rather than silently loading everything.
    * *start* / *end* -- inclusive calendar bounds (``date`` / ``YYYY-MM-DD``)
      applied to the ``date`` column before any window is built; a file with no
      ``date`` column raises ``ValueError`` when either bound is given.
    """
    directory = Path(data_dir)
    if not directory.is_dir():
        raise FileNotFoundError(f"Data directory not found: {directory}")

    symbol_filter: set[str] | None = None
    if symbols is not None:
        symbol_filter = {str(s).strip().lower() for s in symbols if str(s).strip()}
        if not symbol_filter:
            raise ValueError(
                "load_parquet_dir: a symbols filter was given but contains no usable entries"
            )

    start_bound = _parse_date_bound(start)
    end_bound = _parse_date_bound(end)

    out: Dict[str, np.ndarray] = {}
    dates: Dict[str, np.ndarray] = {}
    for path in sorted(directory.glob("*.parquet")):
        if symbol_filter is not None and path.stem.lower() not in symbol_filter:
            continue
        frame = pl.read_parquet(path).sort("date")
        missing = [c for c in OHLCV_COLUMNS if c not in frame.columns]
        if missing:
            raise ValueError(f"{path.name}: missing columns {missing}")
        has_date = "date" in frame.columns
        if (start_bound is not None or end_bound is not None):
            if not has_date:
                raise ValueError(
                    f"{path.name}: 'date' column required when start/end is given"
                )
            date_expr = pl.col("date").cast(pl.Date)
            if start_bound is not None:
                frame = frame.filter(date_expr >= start_bound)
            if end_bound is not None:
                frame = frame.filter(date_expr <= end_bound)
        if has_date:
            date_col = frame.get_column("date").to_numpy().astype("datetime64[D]")
            msg = _date_gap_warning(path.stem, date_col)
            if msg:
                print(f"warning: {msg}")
        elif return_dates:
            raise ValueError(f"{path.name}: 'date' column required when return_dates=True")
        arr = frame.select(OHLCV_COLUMNS).to_numpy().astype(np.float64)

        corp_msg = _corp_action_warning(path.stem, arr[:, CLOSE])
        if corp_msg is not None:
            if strict_corp_actions:
                raise ValueError(corp_msg)
            print(f"warning: {corp_msg}")

        if arr.shape[0] > 0:
            out[path.stem] = arr
            if has_date:
                dates[path.stem] = date_col.reshape(-1)

    if return_dates:
        return out, dates
    return out


def materialize_filtered_dir(
    src_dir: Path | str,
    dst_dir: Path | str,
    *,
    symbols: Iterable[str] | None = None,
    start: str | datetime.date | None = None,
    end: str | datetime.date | None = None,
) -> Path:
    """Write the *symbols* / calendar subset of *src_dir*'s parquet files into *dst_dir*.

    Lets an unmodified trainer (which only takes ``--data-dir``) train on a scoped
    subset: point it at the returned directory. A file is copied byte-for-byte when
    no calendar bound is given, so every column -- including user-managed
    ``.meta``-style columns -- is preserved; a calendar bound triggers a row filter
    on the ``date`` column and rewrites the file. *dst_dir* is created if absent and
    is returned. The empty-symbols guard and the ``date``-column requirement match
    :func:`load_parquet_dir`.
    """
    source = Path(src_dir)
    if not source.is_dir():
        raise FileNotFoundError(f"Data directory not found: {source}")
    destination = Path(dst_dir)
    destination.mkdir(parents=True, exist_ok=True)

    symbol_filter: set[str] | None = None
    if symbols is not None:
        symbol_filter = {str(s).strip().lower() for s in symbols if str(s).strip()}
        if not symbol_filter:
            raise ValueError(
                "materialize_filtered_dir: a symbols filter was given but contains no usable entries"
            )

    start_bound = _parse_date_bound(start)
    end_bound = _parse_date_bound(end)
    has_bound = start_bound is not None or end_bound is not None

    for path in sorted(source.glob("*.parquet")):
        if symbol_filter is not None and path.stem.lower() not in symbol_filter:
            continue
        target = destination / path.name
        if not has_bound:
            shutil.copy2(path, target)
            continue
        frame = pl.read_parquet(path).sort("date")
        if "date" not in frame.columns:
            raise ValueError(f"{path.name}: 'date' column required when start/end is given")
        date_expr = pl.col("date").cast(pl.Date)
        if start_bound is not None:
            frame = frame.filter(date_expr >= start_bound)
        if end_bound is not None:
            frame = frame.filter(date_expr <= end_bound)
        if frame.height == 0:
            continue
        tmp = target.with_name(target.name + ".tmp")
        frame.write_parquet(tmp)
        tmp.replace(target)
    return destination


# --- Feature transforms (C#-equivalent) ------------------------------------


def normalize_ohlcv_minmax(window: np.ndarray) -> np.ndarray:
    """Mirror of ``MLDataProcessor.NormalizeCandles`` for one window.

    ``window`` shape ``(W, 5)`` -> returns ``(W, 5)`` float32, bar-interleaved
    O, H, L, C, V. Price min is taken from the Low series, price max from the
    High series; a flat price range yields 0.5, a flat volume range yields 0.0.
    """
    w = np.asarray(window, dtype=np.float64)
    out = np.empty((w.shape[0], FEATURES_OHLCV), dtype=np.float64)

    min_price = w[:, LOW].min()
    max_price = w[:, HIGH].max()
    price_range = max_price - min_price

    prices = w[:, PRICE_SLICE]
    if price_range <= EPSILON:
        out[:, PRICE_SLICE] = 0.5
    else:
        out[:, PRICE_SLICE] = np.clip((prices - min_price) / price_range, 0.0, 1.0)

    vol_log = np.log(1.0 + np.maximum(0.0, w[:, VOLUME]))
    vol_range = vol_log.max() - vol_log.min()
    if vol_range <= EPSILON:
        out[:, VOLUME] = 0.0
    else:
        out[:, VOLUME] = np.clip((vol_log - vol_log.min()) / vol_range, 0.0, 1.0)

    return out.astype(np.float32)


def compute_log_returns(window: np.ndarray) -> np.ndarray:
    """Mirror of ``MLDataProcessor.ComputeLogReturns`` for one window.

    ``window`` shape ``(W, 5)`` -> returns ``(W, 1)`` float32. The first bar of
    the window is 0.0; any bar whose current or previous Close is non-positive is
    0.0; otherwise ``ln(Close_i / Close_{i-1})``.
    """
    close = np.asarray(window, dtype=np.float64)[:, CLOSE]
    out = np.zeros((close.shape[0], FEATURES_LOGRETURN), dtype=np.float64)
    for i in range(1, close.shape[0]):
        prev, curr = close[i - 1], close[i]
        if prev <= 0.0 or curr <= 0.0:
            out[i, 0] = 0.0
        else:
            out[i, 0] = np.log(curr / prev)
    return out.astype(np.float32)


def zscore_standardized(window: np.ndarray) -> np.ndarray:
    """Mirror of ``ComputeZScore`` applied per channel, interleaved per
    ``NormalizeZScoreOhlcv``.

    ``window`` shape ``(W, 5)`` -> returns ``(W, 5)`` float32. Each of the 5
    OHLCV channels is population Z-Score standardized independently across the
    window; a channel whose standard deviation is <= EPSILON becomes all 0.0.
    """
    w = np.asarray(window, dtype=np.float64)
    out = np.empty((w.shape[0], FEATURES_OHLCV), dtype=np.float64)
    for c in range(FEATURES_OHLCV):
        col = w[:, c]
        mu = col.mean()
        sigma = np.sqrt(np.mean((col - mu) ** 2))  # population std (ddof=0)
        out[:, c] = 0.0 if sigma <= EPSILON else (col - mu) / sigma
    return out.astype(np.float32)


def zscore_joint_standardized(window: np.ndarray) -> np.ndarray:
    """Mirror of ``MLDataProcessor.ComputeJointZScoreOhlcv`` for one window.

    O/H/L/C are standardized against a single pooled mean/std taken over all four
    price channels in the window at once; Volume is standardized on its own. A
    pooled affine transform is monotonic, so unlike per-channel
    :func:`zscore_standardized` this preserves candle geometry (Z(High) stays
    above Z(Open) when High > Open).

    ``window`` shape ``(W, 5)`` -> ``(W, 5)`` float32, interleaved O, H, L, C, V.
    A pooled price std <= EPSILON, or a flat volume channel, yields 0.0 there.
    """
    w = np.asarray(window, dtype=np.float64)
    out = np.empty((w.shape[0], FEATURES_OHLCV), dtype=np.float64)

    prices = w[:, PRICE_SLICE]
    mu = prices.mean()
    sigma = np.sqrt(np.mean((prices - mu) ** 2))
    out[:, PRICE_SLICE] = 0.0 if sigma <= EPSILON else (prices - mu) / sigma

    vol = w[:, VOLUME]
    vmu = vol.mean()
    vsigma = np.sqrt(np.mean((vol - vmu) ** 2))
    out[:, VOLUME] = 0.0 if vsigma <= EPSILON else (vol - vmu) / vsigma

    return out.astype(np.float32)


def compute_log_returns_ohlc(window: np.ndarray) -> np.ndarray:
    """Mirror of ``MLDataProcessor.ComputeLogReturnsOhlc`` for one window.

    Four intrabar log-return channels per bar, all dimensionless and (for the
    gap) causal:

    * ``gap  = ln(Open_i / Close_{i-1})``  (0.0 on the first bar)
    * ``hi   = ln(High_i / Open_i)``       (upper reach / buying pressure)
    * ``lo   = ln(Low_i / Open_i)``        (lower reach / selling pressure)
    * ``cl   = ln(Close_i / Open_i)``      (bar body)

    Any channel whose numerator or denominator is non-positive yields 0.0.

    ``window`` shape ``(W, 5)`` -> ``(W, 4)`` float32, columns [gap, hi, lo, cl].
    """
    w = np.asarray(window, dtype=np.float64)
    n = w.shape[0]
    out = np.zeros((n, FEATURES_LOGRETURN_OHLC), dtype=np.float64)
    for i in range(n):
        o, h, low, c = w[i, OPEN], w[i, HIGH], w[i, LOW], w[i, CLOSE]
        if i > 0:
            prev_close = w[i - 1, CLOSE]
            if prev_close > 0.0 and o > 0.0:
                out[i, 0] = np.log(o / prev_close)
        if o > 0.0:
            if h > 0.0:
                out[i, 1] = np.log(h / o)
            if low > 0.0:
                out[i, 2] = np.log(low / o)
            if c > 0.0:
                out[i, 3] = np.log(c / o)
    return out.astype(np.float32)


_FEATURE_DISPATCH = {
    "ohlcv_minmax": normalize_ohlcv_minmax,
    "log_return": compute_log_returns,
    "zscore": zscore_standardized,
    "zscore_joint": zscore_joint_standardized,
    "log_return_ohlc": compute_log_returns_ohlc,
}


def get_transform(feature_mode: str):
    """Return the C#-equivalent per-window feature transform callable for *feature_mode*.

    Public accessor so downstream modules never reach into the private dispatch table.
    """
    if feature_mode not in _FEATURE_DISPATCH:
        raise ValueError(f"Unknown feature_mode {feature_mode!r}; expected one of {FEATURE_MODES}")
    return _FEATURE_DISPATCH[feature_mode]


def feature_channels(feature_mode: str, feature_spec: dict | None = None) -> int:
    """Channels-per-bar for *feature_mode* (matches the ONNX input tensor's last dim).

    ``feature_spec`` is required (and its channel count is authoritative) when
    *feature_mode* is :data:`COMPOSED_FEATURES_MODE`; ignored for every other mode.
    """
    if feature_mode == COMPOSED_FEATURES_MODE:
        return len(_validate_feature_spec_channels(feature_spec))
    try:
        return _MODE_CHANNELS[feature_mode]
    except KeyError:
        raise ValueError(
            f"Unknown feature_mode {feature_mode!r}; expected one of {FEATURE_MODES}"
        ) from None


# --- Labeling -------------------------------------------------------------------


def make_label(anchor_close: float, future_close: float, threshold: float) -> int:
    """3-class label from the forward return ``(future - anchor) / anchor``.

    ``r > threshold`` -> Up(0); ``r < -threshold`` -> Down(1); else Neutral(2).
    ``future_close`` is the Close ``horizon`` *bars* (rows) after the anchor, not
    ``horizon`` calendar days -- see :func:`build_dataset`.
    """
    r = (future_close - anchor_close) / anchor_close
    if r > threshold:
        return CLASS_UP
    if r < -threshold:
        return CLASS_DOWN
    return CLASS_NEUTRAL


def make_regression_target(anchor_close: float, future_close: float) -> float:
    """Forward log return target: ``y = ln(future_close / anchor_close)``.

    ``future_close`` is the Close ``horizon`` *bars* after the anchor (see
    :func:`build_dataset`). Both prices must be strictly positive -- callers
    apply that guard before calling this (see ``target_type == "regression"``
    branch of :func:`build_dataset`), so this function itself does not re-check.
    """
    return float(np.log(future_close / anchor_close))


def wilder_atr(ohlcv: np.ndarray, period: int = DEFAULT_TB_ATR_PERIOD) -> np.ndarray:
    """Wilder-smoothed Average True Range, row-aligned with ``ohlcv``.

    Matches C# ``CoreAtrIndicator``: ``TR[i] = max(H[i]-L[i], |H[i]-C[i-1]|,
    |L[i]-C[i-1]|)`` for ``i >= 1``; ``ATR[period]`` is the simple mean of
    ``TR[1..period]`` and every later bar is ``(ATR[i-1]*(period-1) + TR[i]) /
    period``. Bars with no defined value (``i < period``) are ``nan``. Uses only
    bars at or before each index -- no look-ahead.
    """
    if period <= 0:
        raise ValueError("period must be positive")
    arr = np.asarray(ohlcv, dtype=np.float64)
    n = arr.shape[0]
    atr = np.full(n, np.nan, dtype=np.float64)
    if n < period + 1:
        return atr

    high, low, close = arr[:, HIGH], arr[:, LOW], arr[:, CLOSE]
    prev_close = close[:-1]
    tr = np.maximum.reduce([
        high[1:] - low[1:],
        np.abs(high[1:] - prev_close),
        np.abs(low[1:] - prev_close),
    ])  # tr[k] is the true range of bar k+1 (k = 0 .. n-2)

    atr[period] = tr[:period].mean()  # mean of TR[1..period]
    for i in range(period + 1, n):
        atr[i] = (atr[i - 1] * (period - 1) + tr[i - 1]) / period
    return atr


def label_triple_barrier(
    highs: np.ndarray,
    lows: np.ndarray,
    *,
    anchor: int,
    tp_price: float,
    sl_price: float,
    max_horizon: int,
) -> int:
    """3-class triple-barrier label for the anchor at index ``anchor``.

    Scans the ``max_horizon`` bars strictly after ``anchor`` (never the anchor bar
    itself and never a bar before it -- no look-ahead) using each bar's intraday
    high / low:

    * high reaches ``tp_price`` first  -> Up(0)   (take-profit)
    * low reaches ``sl_price`` first   -> Down(1) (stop-loss)
    * a single bar touches both        -> Down(1) (stop takes precedence)
    * neither barrier is touched in the window -> Neutral(2) (time-out)

    Returns ``CLASS_NEUTRAL`` when the barriers are not usable (non-finite, or
    ``tp_price <= sl_price``), so a caller with a degenerate ATR still gets a
    label rather than an exception.
    """
    if max_horizon <= 0:
        raise ValueError("max_horizon must be positive")
    if not (np.isfinite(tp_price) and np.isfinite(sl_price)) or tp_price <= sl_price:
        return CLASS_NEUTRAL

    last = min(anchor + max_horizon, highs.shape[0] - 1)
    for j in range(anchor + 1, last + 1):
        sl_hit = lows[j] <= sl_price
        tp_hit = highs[j] >= tp_price
        if sl_hit:
            return CLASS_DOWN
        if tp_hit:
            return CLASS_UP
    return CLASS_NEUTRAL


# --- Dataset assembly --------------------------------------------------------


def build_dataset(
    ohlcv: np.ndarray,
    feature_mode: str,
    window: int = DEFAULT_WINDOW,
    horizon: int = DEFAULT_HORIZON,
    threshold: float = DEFAULT_THRESHOLD,
    *,
    feature_spec: dict | None = None,
    target_type: str = "classification",
    return_anchor_rows: bool = False,
    dates: np.ndarray | None = None,
    indicator_export_path: str | Path | None = None,
):
    """Slide a *window* over one symbol's ``(rows, 5)`` OHLCV array.

    The prediction anchor is the window's last bar (index ``t + window - 1``);
    its label compares that Close against the Close *horizon* bars later. The
    window itself never references a bar beyond ``t + window - 1`` (no look-ahead).

    ``window`` and ``horizon`` are counts of bars (rows), never calendar days: a
    row missing from a trading halt or holiday shortens neither span. Use
    :func:`_date_gap_warning` output to spot histories where that matters.

    When ``feature_mode`` is :data:`COMPOSED_FEATURES_MODE`, *feature_spec* (a parsed
    ``{"channels": [...]}`` dict, mirroring C# ``Training.FeatureSpec``) selects the channels
    via :func:`compose_features_window` instead of a fixed transform; every other mode ignores
    *feature_spec* and behaves exactly as before. When *feature_spec* contains an Indicator-kind
    channel, *dates* (this symbol's full ``datetime64[D]`` OHLCV date column) and
    *indicator_export_path* (``TrainingJobConfig.IndicatorChannelExportPaths[symbol]``) are
    required and used once, up front, to build a date-aligned ``indicator_columns`` map via
    :func:`_build_indicator_columns` (see FR-04); every other mode/spec ignores both. A window
    whose Indicator-kind channel value is not yet available (warmup, i.e. ``NaN``) is skipped like
    any other not-yet-finite window (see FR-05) -- never zero-filled or forward-filled.

    ``target_type`` selects the learning objective (mirrors C# ``Training.TargetType`` /
    ``onnx_meta.TARGET_TYPES``): ``"classification"`` (default) keeps the existing 3-class
    :func:`make_label` behavior byte-for-byte. ``"regression"`` labels each window with the
    forward log return :func:`make_regression_target` and additionally excludes a window
    whose ``future`` Close is not strictly positive (ln() is undefined there; the existing
    non-finite/non-positive-anchor guard below still applies to both).

    Returns ``(X, y)`` with ``X`` shape ``(N, window, C)`` float32. For classification ``y``
    is shape ``(N,)`` int64 (class ids); for regression ``y`` is shape ``(N, 1)`` float32
    (log-return values), matching the ONNX trainer's ``[N, 1]`` target/output contract. When
    *return_anchor_rows* is true a third array is returned: ``anchor_rows`` int64 shape
    ``(N,)``, holding the source row index ``t + window - 1`` of each kept sample (skipped
    windows are absent, so this is not a fixed offset from the row).
    """
    if window <= 0 or horizon <= 0:
        raise ValueError("window and horizon must be positive")
    if target_type not in ("classification", "regression"):
        raise ValueError(
            f"target_type: unknown {target_type!r}; expected 'classification' or 'regression'"
        )

    if feature_mode == COMPOSED_FEATURES_MODE:
        channels_spec = _validate_feature_spec_channels(feature_spec)
        channels = len(channels_spec)
        transform = None
        indicator_columns = _build_indicator_columns(channels_spec, dates, indicator_export_path)
    else:
        transform = get_transform(feature_mode)  # also validates feature_mode
        channels = feature_channels(feature_mode)
        indicator_columns = None
    data = np.asarray(ohlcv, dtype=np.float64)
    n = data.shape[0]

    features: List[np.ndarray] = []
    labels: List[int] = []
    anchor_rows: List[int] = []
    last_start = n - window - horizon
    if feature_mode == COMPOSED_FEATURES_MODE and last_start + 1 > MAX_COMPOSED_BATCH_SAMPLES:
        raise ValueError(
            f"composed_features: {last_start + 1} candidate samples exceeds the MaxBatchSize "
            f"bound of {MAX_COMPOSED_BATCH_SAMPLES}"
        )
    for t in range(0, last_start + 1):
        w = data[t:t + window]
        if not np.all(np.isfinite(w)):
            continue
        if indicator_columns is not None and any(
            not np.all(np.isfinite(values[t:t + window])) for values in indicator_columns.values()
        ):
            # FR-05: at least one Indicator-kind channel has no value yet (warmup) somewhere in
            # this window -- exclude the sample, exactly like the raw-OHLCV finiteness check
            # above, rather than zero-filling or forward-filling it.
            continue
        anchor = data[t + window - 1, CLOSE]
        future = data[t + window - 1 + horizon, CLOSE]
        if anchor <= 0.0 or not np.isfinite(future):
            continue
        if target_type == "regression" and future <= 0.0:
            continue
        if transform is None:
            features.append(compose_features_window(data, t, window, channels_spec, indicator_columns))
        else:
            features.append(transform(w))
        if target_type == "regression":
            labels.append(make_regression_target(anchor, future))
        else:
            labels.append(make_label(anchor, future, threshold))
        anchor_rows.append(t + window - 1)

    if not features:
        x = np.empty((0, window, channels), dtype=np.float32)
        y = (
            np.empty((0, 1), dtype=np.float32)
            if target_type == "regression"
            else np.empty((0,), dtype=np.int64)
        )
        if return_anchor_rows:
            return x, y, np.empty((0,), dtype=np.int64)
        return x, y

    x = np.stack(features).astype(np.float32)
    y = (
        np.asarray(labels, dtype=np.float32).reshape(-1, 1)
        if target_type == "regression"
        else np.asarray(labels, dtype=np.int64)
    )
    if return_anchor_rows:
        return x, y, np.asarray(anchor_rows, dtype=np.int64)
    return x, y


def build_dataset_multi(
    symbols: Dict[str, np.ndarray],
    feature_mode: str,
    window: int = DEFAULT_WINDOW,
    horizon: int = DEFAULT_HORIZON,
    threshold: float = DEFAULT_THRESHOLD,
    *,
    feature_spec: dict | None = None,
    target_type: str = "classification",
    dates: Dict[str, np.ndarray] | None = None,
    indicator_export_paths: Dict[str, str] | None = None,
) -> Tuple[np.ndarray, np.ndarray]:
    """Concatenate :func:`build_dataset` over many symbols (per-symbol windows only).

    For ``feature_mode == COMPOSED_FEATURES_MODE``, the cumulative sample count across all
    symbols is checked against :data:`MAX_COMPOSED_BATCH_SAMPLES` as each symbol is appended --
    a per-symbol-only check (already applied inside :func:`build_dataset`) cannot catch a
    multi-symbol aggregate that overflows the bound even though no single symbol does.

    ``target_type`` is forwarded to :func:`build_dataset` unchanged (default
    ``"classification"``; see that function for the ``"regression"`` shape/label change).

    ``dates``/``indicator_export_paths`` (both keyed by symbol, mirroring
    ``TrainingJobConfig.IndicatorChannelExportPaths``) are forwarded per-symbol to
    :func:`build_dataset`; a symbol missing from either map when *feature_spec* has an
    Indicator-kind channel surfaces as that symbol's own ``ValueError`` from
    :func:`_build_indicator_columns`, not a silent skip.
    """
    xs: List[np.ndarray] = []
    ys: List[np.ndarray] = []
    total_samples = 0
    for symbol, arr in symbols.items():
        x, y = build_dataset(
            arr, feature_mode, window, horizon, threshold,
            feature_spec=feature_spec, target_type=target_type,
            dates=dates.get(symbol) if dates is not None else None,
            indicator_export_path=(
                indicator_export_paths.get(symbol) if indicator_export_paths is not None else None
            ),
        )
        if x.shape[0] > 0:
            total_samples += x.shape[0]
            if feature_mode == COMPOSED_FEATURES_MODE and total_samples > MAX_COMPOSED_BATCH_SAMPLES:
                raise ValueError(
                    f"composed_features: cumulative sample count {total_samples} across symbols "
                    f"exceeds the MaxBatchSize bound of {MAX_COMPOSED_BATCH_SAMPLES}"
                )
            xs.append(x)
            ys.append(y)
    channels = (
        len(_validate_feature_spec_channels(feature_spec))
        if feature_mode == COMPOSED_FEATURES_MODE
        else feature_channels(feature_mode)
    )
    if not xs:
        empty_y = (
            np.empty((0, 1), dtype=np.float32)
            if target_type == "regression"
            else np.empty((0,), dtype=np.int64)
        )
        return np.empty((0, window, channels), dtype=np.float32), empty_y
    return np.concatenate(xs, axis=0), np.concatenate(ys, axis=0)


def walk_forward_split(
    n_samples: int, n_splits: int = DEFAULT_WF_SPLITS, gap: int = 0
) -> List[Tuple[np.ndarray, np.ndarray]]:
    """Expanding-window time-series splits with an optional purge *gap*.

    Fold ``k`` trains on ``[0, end_k)`` and tests on the block that follows after
    ``gap`` samples are dropped, so every test index is at least ``gap + 1`` ahead
    of the last training index of the same fold. No shuffling.

    Sliding windows overlap: two samples that start ``d`` bars apart share
    ``window - d`` bars, and each label looks ``horizon`` bars ahead, so a naive
    train/test boundary leaks up to ``window + horizon - 1`` samples' worth of
    shared bars and future returns. Pass ``gap = window + horizon - 1`` to purge
    that overlap. A fold whose test block would be empty after the gap is dropped.
    """
    if n_samples <= 0 or n_splits <= 0:
        return []
    if gap < 0:
        raise ValueError("gap must be non-negative")
    fold_size = n_samples // (n_splits + 1)
    if fold_size == 0:
        return []

    splits: List[Tuple[np.ndarray, np.ndarray]] = []
    for k in range(n_splits):
        train_end = fold_size * (k + 1)
        test_start = train_end + gap
        test_end = n_samples if k == n_splits - 1 else train_end + fold_size
        if test_start >= test_end:
            continue
        train_idx = np.arange(0, train_end, dtype=np.int64)
        test_idx = np.arange(test_start, test_end, dtype=np.int64)
        splits.append((train_idx, test_idx))
    return splits


def resolve_purge_gap(window: int, horizon: int, gap: int | None = None) -> int:
    """Purge margin in bars a walk-forward split drops between each fold's train
    and test blocks.

    ``None`` resolves to ``window + horizon - 1`` -- the overlap a sliding window
    plus its forward label spans, which is the smallest gap that fully purges
    boundary leakage (see :func:`walk_forward_split`). An explicit value passes
    through unchanged; non-negativity is enforced downstream by
    :func:`walk_forward_split` (``gap < 0`` -> ``ValueError``). Single source of
    truth for this default, mirrored on the C# side by
    ``WalkForwardDataRequirement.MinimumRawBars``'s ``windowOffset``.
    """
    return (window + horizon - 1) if gap is None else gap


def resolve_oos_cutoff(max_date: datetime.date, tail_days: int) -> datetime.date:
    """The boundary date that splits one symbol's rows into the main block and the
    fixed out-of-sample tail.

    ``cutoff = max_date - tail_days`` calendar days. Rows with ``date <= cutoff``
    are the main block (training + walk-forward cross-validation); rows with
    ``date > cutoff`` are the out-of-sample tail, scored exactly once. Single
    source of truth for this boundary, shared by :func:`oos_split` (NumPy,
    whole-array masking) and :func:`materialize_main_dir` (Polars, row filtering)
    so the date arithmetic and the ``<=`` / ``>`` convention cannot drift between
    the two. Callers own the ``tail_days <= 0`` passthrough short-circuit; this
    function always computes the offset.
    """
    return max_date - datetime.timedelta(days=int(tail_days))


def oos_split(
    ohlcv: np.ndarray, dates: np.ndarray, tail_days: int
) -> Tuple[np.ndarray, np.ndarray, np.ndarray, np.ndarray]:
    """Carve a fixed out-of-sample tail off the end of one symbol's rows.

    Every row whose ``date`` lies within ``tail_days`` calendar days of the most
    recent date goes to the out-of-sample block; the rest is the main block used
    for training and walk-forward cross-validation. The tail is by construction
    the newest rows, so nothing in the main block sees it (no look-ahead), and it
    is meant to be scored exactly once. ``dates`` MUST be row-aligned with
    ``ohlcv``. ``tail_days <= 0`` returns the whole input as the main block and
    empty out-of-sample arrays.

    Returns ``(main_ohlcv, main_dates, oos_ohlcv, oos_dates)`` with chronological
    order preserved on both sides.
    """
    arr = np.asarray(ohlcv)
    d = np.asarray(dates).astype("datetime64[D]").reshape(-1)
    if d.shape[0] != arr.shape[0]:
        raise ValueError(
            f"oos_split: dates ({d.shape[0]}) and ohlcv ({arr.shape[0]}) rows disagree"
        )
    if tail_days <= 0 or arr.shape[0] == 0:
        empty_rows = arr[:0]
        return arr, d, empty_rows, d[:0]

    cutoff = np.datetime64(resolve_oos_cutoff(d.max().astype(object), tail_days))
    oos_mask = d > cutoff
    main_mask = ~oos_mask
    return arr[main_mask], d[main_mask], arr[oos_mask], d[oos_mask]


def materialize_main_dir(src_dir: Path | str, dst_dir: Path | str, *, oos_tail_days: int) -> Path:
    """Write only the "main" (non-out-of-sample) rows of every symbol in *src_dir*
    into *dst_dir*, using the same per-symbol boundary :func:`oos_split` uses --
    both call :func:`resolve_oos_cutoff`, so this Polars row filter and that NumPy
    mask cannot diverge.

    Meant to run before a trainer (or fold evaluation) sees the directory, so the
    tail that :func:`oos_split` later carves off for a one-time out-of-sample score
    was never available for fitting, early stopping, or fold-wise validation in the
    first place. ``oos_tail_days <= 0`` copies every file through unchanged --
    mirrors :func:`oos_split`'s own ``tail_days <= 0`` passthrough (no tail
    withheld). A file with no ``date`` column raises ``ValueError`` (the same
    requirement :func:`oos_split` has for locating the boundary).
    """
    source = Path(src_dir)
    if not source.is_dir():
        raise FileNotFoundError(f"Data directory not found: {source}")
    destination = Path(dst_dir)
    destination.mkdir(parents=True, exist_ok=True)

    for path in sorted(source.glob("*.parquet")):
        target = destination / path.name
        if oos_tail_days <= 0:
            shutil.copy2(path, target)
            continue
        frame = pl.read_parquet(path).sort("date")
        if "date" not in frame.columns:
            raise ValueError(f"{path.name}: 'date' column required to exclude an out-of-sample tail")
        if frame.height == 0:
            continue
        date_expr = pl.col("date").cast(pl.Date)
        d_max = frame.select(date_expr.max()).item()
        cutoff = resolve_oos_cutoff(d_max, oos_tail_days)
        frame = frame.filter(date_expr <= cutoff)
        if frame.height == 0:
            continue
        tmp = target.with_name(target.name + ".tmp")
        frame.write_parquet(tmp)
        tmp.replace(target)
    return destination


def _last_fold(
    arr: np.ndarray,
    feature_mode: str,
    window: int,
    horizon: int,
    threshold: float,
    n_splits: int,
    resolved_gap: int,
    feature_spec: dict | None = None,
    target_type: str = "classification",
    dates: np.ndarray | None = None,
    indicator_export_path: str | Path | None = None,
):
    """Windows for one symbol plus its final expanding-window fold.

    Shared by :func:`split_symbols_chronological` and :func:`train_val_date_ranges`
    so the "build windows, take the last walk-forward fold" rule has a single
    implementation. Returns ``(x, y, anchor_rows, train_idx, val_idx)``, or
    ``None`` when the symbol yields no windows or no usable fold.

    ``dates``/``indicator_export_path`` are forwarded to :func:`build_dataset` unchanged
    (only meaningful when *feature_spec* has an Indicator-kind channel).
    """
    x, y, anchor_rows = build_dataset(
        arr, feature_mode, window, horizon, threshold,
        feature_spec=feature_spec, target_type=target_type, return_anchor_rows=True,
        dates=dates, indicator_export_path=indicator_export_path,
    )
    if x.shape[0] == 0:
        return None
    folds = walk_forward_split(x.shape[0], n_splits=n_splits, gap=resolved_gap)
    if not folds:
        return None
    train_idx, val_idx = folds[-1]
    return x, y, anchor_rows, train_idx, val_idx


def split_symbols_chronological(
    symbols: Dict[str, np.ndarray],
    feature_mode: str,
    window: int = DEFAULT_WINDOW,
    horizon: int = DEFAULT_HORIZON,
    threshold: float = DEFAULT_THRESHOLD,
    n_splits: int = DEFAULT_WF_SPLITS,
    gap: int | None = None,
    feature_spec: dict | None = None,
    target_type: str = "classification",
    dates: Dict[str, np.ndarray] | None = None,
    indicator_export_paths: Dict[str, str] | None = None,
) -> Tuple[np.ndarray, np.ndarray, np.ndarray, np.ndarray]:
    """Per-symbol chronological train/validation split, then pool.

    For each symbol the sliding windows from :func:`build_dataset` are split by
    :func:`walk_forward_split` (last fold), which places the validation block
    strictly after the training block of *that same symbol* with a purge ``gap``
    (default ``window + horizon - 1``) between them. Training parts are then
    concatenated, validation parts are concatenated.

    This replaces ``walk_forward_split`` applied to :func:`build_dataset_multi`
    output, where a positional split over a symbol-major concatenation puts whole
    symbols in train and other whole symbols in validation -- a symbol hold-out
    mislabelled as walk-forward, with no chronological separation inside a symbol.

    ``feature_spec`` is forwarded to :func:`build_dataset` unchanged (only meaningful
    when ``feature_mode`` is :data:`COMPOSED_FEATURES_MODE`). ``target_type`` is forwarded
    unchanged too (default ``"classification"``; see :func:`build_dataset` for the
    ``"regression"`` shape/label change). ``dates``/``indicator_export_paths`` (both keyed by
    symbol) are forwarded per-symbol to :func:`build_dataset` via :func:`_last_fold`; both
    default to ``None`` so an existing caller with no Indicator-kind channel (including
    ``train_lightgbm.py``/``train_tensorflow.py``, which never pass either) is unaffected.

    Returns ``(x_train, y_train, x_val, y_val)`` -- ``x`` float32 ``(N, window, C)``,
    ``y`` int64 ``(N,)`` for classification or float32 ``(N, 1)`` for regression.
    """
    resolved_gap = resolve_purge_gap(window, horizon, gap)
    channels = (
        len(_validate_feature_spec_channels(feature_spec))
        if feature_mode == COMPOSED_FEATURES_MODE
        else feature_channels(feature_mode)
    )

    xs_tr: List[np.ndarray] = []
    ys_tr: List[np.ndarray] = []
    xs_va: List[np.ndarray] = []
    ys_va: List[np.ndarray] = []
    for sym, arr in symbols.items():
        fold = _last_fold(
            arr, feature_mode, window, horizon, threshold, n_splits, resolved_gap,
            feature_spec, target_type,
            dates=dates.get(sym) if dates is not None else None,
            indicator_export_path=(
                indicator_export_paths.get(sym) if indicator_export_paths is not None else None
            ),
        )
        if fold is None:
            continue
        x, y, _anchor_rows, train_idx, val_idx = fold
        xs_tr.append(x[train_idx])
        ys_tr.append(y[train_idx])
        xs_va.append(x[val_idx])
        ys_va.append(y[val_idx])

    def _cat(parts_x: List[np.ndarray], parts_y: List[np.ndarray]) -> Tuple[np.ndarray, np.ndarray]:
        if not parts_x:
            empty_y = (
                np.empty((0, 1), dtype=np.float32)
                if target_type == "regression"
                else np.empty((0,), dtype=np.int64)
            )
            return np.empty((0, window, channels), dtype=np.float32), empty_y
        return np.concatenate(parts_x, axis=0), np.concatenate(parts_y, axis=0)

    x_tr, y_tr = _cat(xs_tr, ys_tr)
    x_va, y_va = _cat(xs_va, ys_va)
    return x_tr, y_tr, x_va, y_va


def train_val_date_ranges(
    symbols: Dict[str, np.ndarray],
    dates: Dict[str, np.ndarray],
    *,
    feature_mode: str,
    window: int = DEFAULT_WINDOW,
    horizon: int = DEFAULT_HORIZON,
    threshold: float = DEFAULT_THRESHOLD,
    n_splits: int = DEFAULT_WF_SPLITS,
    gap: int | None = None,
    feature_spec: dict | None = None,
    target_type: str = "classification",
    indicator_export_paths: Dict[str, str] | None = None,
) -> Dict[str, str]:
    """Calendar span of the training and validation blocks of the chronological split.

    Applies the same per-symbol last-fold rule as
    :func:`split_symbols_chronological` (:func:`_last_fold`), maps each fold's
    sample indices back to the window's anchor row and then to that row's
    ``date``, and reduces to a global min/max per side. ``dates[symbol]`` MUST be
    row-aligned with ``symbols[symbol]`` (as produced by
    ``load_parquet_dir(..., return_dates=True)``).

    ``feature_spec``/``target_type`` are forwarded to :func:`build_dataset` unchanged
    (``target_type="regression"`` excludes a few more windows via its extra label guard,
    which can shift these spans slightly versus the classification default).
    ``indicator_export_paths`` (keyed by symbol) is forwarded per-symbol to
    :func:`build_dataset` via :func:`_last_fold`, alongside ``dates[symbol]`` (already a
    required parameter here); defaults to ``None`` for a *feature_spec* with no
    Indicator-kind channel.

    Returns a mapping over :data:`DATE_RANGE_KEYS` with ``YYYY-MM-DD`` values; a
    side that received no samples yields an empty string. This is the source of
    the ONNX ``metadata_props`` training/validation date keys.
    """
    resolved_gap = resolve_purge_gap(window, horizon, gap)
    tr_min = tr_max = va_min = va_max = None

    for sym, arr in symbols.items():
        if sym not in dates:
            raise ValueError(f"train_val_date_ranges: no dates for symbol {sym!r}")
        fold = _last_fold(
            arr, feature_mode, window, horizon, threshold, n_splits, resolved_gap,
            feature_spec, target_type,
            dates=np.asarray(dates[sym]).astype("datetime64[D]").reshape(-1),
            indicator_export_path=(
                indicator_export_paths.get(sym) if indicator_export_paths is not None else None
            ),
        )
        if fold is None:
            continue
        _x, _y, anchor_rows, train_idx, val_idx = fold
        d = np.asarray(dates[sym]).astype("datetime64[D]").reshape(-1)
        tr_rows = anchor_rows[train_idx]
        va_rows = anchor_rows[val_idx]
        if tr_rows.size and int(tr_rows.max()) < d.shape[0]:
            lo, hi = d[tr_rows].min(), d[tr_rows].max()
            tr_min = lo if tr_min is None else min(tr_min, lo)
            tr_max = hi if tr_max is None else max(tr_max, hi)
        if va_rows.size and int(va_rows.max()) < d.shape[0]:
            lo, hi = d[va_rows].min(), d[va_rows].max()
            va_min = lo if va_min is None else min(va_min, lo)
            va_max = hi if va_max is None else max(va_max, hi)

    def _iso(v) -> str:
        return "" if v is None else str(np.datetime64(v, "D"))

    spans = (tr_min, tr_max, va_min, va_max)
    return {key: _iso(value) for key, value in zip(DATE_RANGE_KEYS, spans)}


# --- C# / Python feature parity vectors ------------------------------------


def build_parity_vectors(
    data_dir: Path | str,
    window: int = PARITY_VECTOR_WINDOW,
    n_real: int = PARITY_REAL_SLICES,
) -> List[dict]:
    """Raw OHLCV windows plus their three feature transforms, for the C#/Python
    parity test (``MLDataProcessorParityTests``).

    Each entry carries the raw window (O/H/L/C as numbers, Volume as integers) and
    the ``ohlcv_minmax`` / ``log_return`` / ``zscore`` outputs flattened bar-major
    then channel-minor -- the exact layout ``MLDataProcessor`` writes into its
    destination span. The C# side rebuilds ``CandleData`` from ``raw`` and asserts
    its transforms match within a float32 tolerance (Python computes in float64).
    """
    out: List[dict] = []

    def add(name: str, raw: np.ndarray) -> None:
        raw = np.asarray(raw, dtype=np.float64).copy()
        # Volume is emitted as integers and the C# side rebuilds CandleData.Volume
        # (a long) from them, so the features must be computed from the same rounded
        # values or the Z-Score volume channel drifts past the parity tolerance.
        raw[:, VOLUME] = np.round(raw[:, VOLUME])
        out.append({
            "name": name,
            "window": int(raw.shape[0]),
            "open": [float(v) for v in raw[:, OPEN]],
            "high": [float(v) for v in raw[:, HIGH]],
            "low": [float(v) for v in raw[:, LOW]],
            "close": [float(v) for v in raw[:, CLOSE]],
            "volume": [int(round(v)) for v in raw[:, VOLUME]],
            "ohlcv_minmax": [float(v) for v in normalize_ohlcv_minmax(raw).reshape(-1)],
            "log_return": [float(v) for v in compute_log_returns(raw).reshape(-1)],
            "zscore": [float(v) for v in zscore_standardized(raw).reshape(-1)],
            "zscore_joint": [float(v) for v in zscore_joint_standardized(raw).reshape(-1)],
            "log_return_ohlc": [float(v) for v in compute_log_returns_ohlc(raw).reshape(-1)],
        })

    try:
        symbols = load_parquet_dir(data_dir)
    except FileNotFoundError:
        symbols = {}
    for sym, arr in list(sorted(symbols.items()))[:n_real]:
        if arr.shape[0] < window + DEFAULT_HORIZON:
            continue
        start = arr.shape[0] // 2
        raw = arr[start:start + window].copy()
        raw[:, OPEN:CLOSE + 1] = np.round(raw[:, OPEN:CLOSE + 1], 4)
        add(f"real_{sym}", raw)

    add("flat_price", np.tile([100.0, 100.0, 100.0, 100.0, 1000.0], (window, 1)))
    add("zero_volume", np.tile([10.0, 12.0, 9.0, 11.0, 0.0], (window, 1)))
    add("constant_open_channel", np.column_stack([
        np.full(window, 50.0),
        np.linspace(48.0, 55.0, window),
        np.linspace(47.0, 54.0, window),
        np.linspace(48.5, 54.5, window),
        np.linspace(1_000.0, 5_000.0, window),
    ]))

    rng = np.random.default_rng(20260828)
    big = np.empty((window, 5))
    big[:, CLOSE] = 100.0 + np.cumsum(rng.normal(0.0, 1.5, window))
    big[:, OPEN] = big[:, CLOSE] + rng.normal(0.0, 0.3, window)
    big[:, HIGH] = np.maximum(big[:, OPEN], big[:, CLOSE]) + np.abs(rng.normal(0.0, 0.5, window))
    big[:, LOW] = np.minimum(big[:, OPEN], big[:, CLOSE]) - np.abs(rng.normal(0.0, 0.5, window))
    big[:, VOLUME] = rng.integers(1_000_000, 5_000_000, window)
    big[:, OPEN:CLOSE + 1] = np.round(big[:, OPEN:CLOSE + 1], 4)
    add("synthetic_large_volume", big)

    nonpos = big.copy()
    nonpos[window // 2, CLOSE] = 0.0
    add("nonpositive_close", nonpos)

    # High nominal price with a small window range (~0.1%), e.g. a BRK.A-style quote.
    # Here (price - pooled_mean) is a small difference of large numbers: computing the
    # pooled/per-channel mean and std in float32 loses precision past the 1e-4 parity
    # budget, so this vector guards that MLDataProcessor keeps those statistics in double.
    hp = np.empty((window, 5))
    hp[:, CLOSE] = 550_000.0 + np.cumsum(rng.normal(0.0, 45.0, window))
    hp[:, OPEN] = hp[:, CLOSE] + rng.normal(0.0, 12.0, window)
    hp[:, HIGH] = np.maximum(hp[:, OPEN], hp[:, CLOSE]) + np.abs(rng.normal(0.0, 20.0, window))
    hp[:, LOW] = np.minimum(hp[:, OPEN], hp[:, CLOSE]) - np.abs(rng.normal(0.0, 20.0, window))
    hp[:, VOLUME] = rng.integers(200, 2_000, window)
    hp[:, OPEN:CLOSE + 1] = np.round(hp[:, OPEN:CLOSE + 1], 4)
    add("high_nominal_price", hp)

    return out


def _run_emit_parity(args: argparse.Namespace) -> None:
    vectors = build_parity_vectors(args.data_dir)
    path = Path(args.emit_parity_vectors)
    path.write_text(json.dumps(vectors, indent=2), encoding="utf-8")
    names = ", ".join(v["name"] for v in vectors)
    print(f"wrote {path} ({len(vectors)} vectors: {names})")


# --- Self-verification -------------------------------------------------------


def _run_selfcheck() -> None:
    # (a) flat price window -> normalized prices are exactly 0.5
    flat = np.tile(np.array([[100.0, 100.0, 100.0, 100.0, 1000.0]]), (DEFAULT_WINDOW, 1))
    fn = normalize_ohlcv_minmax(flat)
    assert np.allclose(fn[:, PRICE_SLICE], 0.5), fn[:, PRICE_SLICE]

    # (b) zero / negative volume window -> normalized volume is exactly 0.0
    volw = np.tile(np.array([[10.0, 12.0, 9.0, 11.0, 0.0]]), (DEFAULT_WINDOW, 1))
    volw[3, VOLUME] = -5.0
    assert np.allclose(normalize_ohlcv_minmax(volw)[:, VOLUME], 0.0)

    # (b2) real price range -> monotonic min/max mapping to [0, 1]
    ramp = np.zeros((5, 5))
    ramp[:, LOW] = [10, 11, 12, 13, 14]
    ramp[:, HIGH] = [11, 12, 13, 14, 15]
    ramp[:, CLOSE] = [10.5, 11.5, 12.5, 13.5, 14.5]
    ramp[:, OPEN] = ramp[:, CLOSE]
    rn = normalize_ohlcv_minmax(ramp)
    assert abs(rn[:, LOW].min() - 0.0) < 1e-6 and abs(rn[:, HIGH].max() - 1.0) < 1e-6

    # (c) constant channel -> Z-Score column is exactly 0.0; varying channel -> mean 0 / std 1
    zw = np.column_stack([
        np.full(DEFAULT_WINDOW, 42.0),                 # open: constant
        np.arange(DEFAULT_WINDOW, dtype=np.float64),   # high: varying
        np.full(DEFAULT_WINDOW, 7.0),
        np.full(DEFAULT_WINDOW, 7.0),
        np.full(DEFAULT_WINDOW, 7.0),
    ])
    z = zscore_standardized(zw)
    assert np.allclose(z[:, OPEN], 0.0)
    assert abs(z[:, HIGH].mean()) < 1e-5 and abs(np.std(z[:, HIGH]) - 1.0) < 1e-5

    # (d) log return: first bar 0.0, known ratio matches np.log
    lrw = np.zeros((4, 5))
    lrw[:, CLOSE] = [100.0, 110.0, 99.0, 99.0]
    lr = compute_log_returns(lrw)
    assert lr.shape == (4, 1) and lr[0, 0] == 0.0
    assert abs(lr[1, 0] - np.log(110.0 / 100.0)) < 1e-6
    lrw[2, CLOSE] = 0.0
    assert compute_log_returns(lrw)[2, 0] == 0.0  # non-positive close guard

    # (e) build_dataset dtype / shape per mode
    rng = np.random.default_rng(0)
    synth = np.empty((200, 5))
    synth[:, CLOSE] = 100.0 + np.cumsum(rng.normal(0, 1, 200))
    synth[:, OPEN] = synth[:, CLOSE]
    synth[:, HIGH] = synth[:, CLOSE] + 1.0
    synth[:, LOW] = synth[:, CLOSE] - 1.0
    synth[:, VOLUME] = rng.integers(1_000, 10_000, 200)
    for mode, expected_c in (("ohlcv_minmax", 5), ("log_return", 1), ("zscore", 5), ("zscore_joint", 5), ("log_return_ohlc", 4)):
        x, y = build_dataset(synth, mode, window=DEFAULT_WINDOW, horizon=DEFAULT_HORIZON)
        assert x.dtype == np.float32 and y.dtype == np.int64, (mode, x.dtype, y.dtype)
        assert x.ndim == 3 and x.shape[1:] == (DEFAULT_WINDOW, expected_c), (mode, x.shape)
        assert x.shape[0] == y.shape[0] == 200 - DEFAULT_WINDOW - DEFAULT_HORIZON + 1
        assert set(np.unique(y)).issubset({CLASS_UP, CLASS_DOWN, CLASS_NEUTRAL})

    # (f) make_label boundaries
    assert make_label(100.0, 100.0 * (1 + DEFAULT_THRESHOLD + 1e-6), DEFAULT_THRESHOLD) == CLASS_UP
    assert make_label(100.0, 100.0 * (1 - DEFAULT_THRESHOLD - 1e-6), DEFAULT_THRESHOLD) == CLASS_DOWN
    assert make_label(100.0, 100.0, DEFAULT_THRESHOLD) == CLASS_NEUTRAL
    assert make_label(100.0, 100.0 * (1 + DEFAULT_THRESHOLD), DEFAULT_THRESHOLD) == CLASS_NEUTRAL  # not strictly greater

    # (g) walk-forward: every test index is future relative to its training block
    splits = walk_forward_split(120, n_splits=5)
    assert len(splits) == 5
    for train_idx, test_idx in splits:
        assert test_idx.min() > train_idx.max()
        assert train_idx.min() == 0
    assert splits[-1][1].max() == 119  # final fold covers the tail

    # (g2) purge gap: test block starts >= gap+1 past the last training index, and
    # the two index sets never intersect; a gap wider than a fold drops that fold.
    gapped = walk_forward_split(1200, n_splits=5, gap=79)
    assert gapped, "expected non-empty gapped splits for 1200 samples"
    for train_idx, test_idx in gapped:
        assert test_idx.min() >= train_idx.max() + 1 + 79
        assert np.intersect1d(train_idx, test_idx).size == 0
    assert walk_forward_split(120, n_splits=5, gap=1000) == []

    # (h) per-symbol chronological split: both parts non-empty, correct shape/dtype,
    # and (by construction of walk_forward_split) val is time-after-train per symbol.
    syn_rng = np.random.default_rng(1)
    multi: Dict[str, np.ndarray] = {}
    for name in ("A", "B", "C"):
        a = np.empty((600, 5))
        a[:, CLOSE] = 100.0 + np.cumsum(syn_rng.normal(0, 1, 600))
        a[:, OPEN] = a[:, CLOSE]
        a[:, HIGH] = a[:, CLOSE] + 1.0
        a[:, LOW] = a[:, CLOSE] - 1.0
        a[:, VOLUME] = syn_rng.integers(1_000, 5_000, 600)
        multi[name] = a
    for mode, expected_c in (("ohlcv_minmax", 5), ("log_return", 1), ("zscore", 5), ("zscore_joint", 5), ("log_return_ohlc", 4)):
        x_tr, y_tr, x_va, y_va = split_symbols_chronological(
            multi, mode, window=DEFAULT_WINDOW, horizon=DEFAULT_HORIZON
        )
        assert x_tr.shape[0] > 0 and x_va.shape[0] > 0, (mode, x_tr.shape, x_va.shape)
        assert x_tr.shape[1:] == (DEFAULT_WINDOW, expected_c), (mode, x_tr.shape)
        assert x_va.shape[1:] == (DEFAULT_WINDOW, expected_c), (mode, x_va.shape)
        assert x_tr.dtype == np.float32 and y_tr.dtype == np.int64
        assert x_tr.shape[0] == y_tr.shape[0] and x_va.shape[0] == y_va.shape[0]
    assert split_symbols_chronological({}, "ohlcv_minmax")[0].shape == (0, DEFAULT_WINDOW, 5)

    # (i) date-gap warning: contiguous daily history and routine holiday gaps are
    # silent; only a months-wide hole fires.
    d_ok = np.arange("2020-01-01", "2020-06-01", dtype="datetime64[D]")
    assert _date_gap_warning("OK", d_ok) is None
    d_holiday = np.array(["2020-01-01", "2020-01-02", "2020-01-09", "2020-01-10"], dtype="datetime64[D]")
    assert _date_gap_warning("HOLIDAY", d_holiday) is None  # 7d gap stays under the 30d floor
    d_gap = np.array(["2020-01-01", "2020-01-02", "2020-01-03", "2021-01-05"], dtype="datetime64[D]")
    assert _date_gap_warning("GAP", d_gap) is not None
    assert _date_gap_warning("SHORT", np.arange("2020-01-01", "2020-01-03", dtype="datetime64[D]")) is None

    # (j) unadjusted corporate-action guard: an adjusted ramp is silent; a raw 1:3
    # split (close 300 -> 100) fires; non-positive / <2 closes are silent.
    adj = np.linspace(100.0, 130.0, 40)
    assert _corp_action_warning("ADJ", adj) is None
    split = np.concatenate([np.full(10, 300.0), np.full(10, 100.0)])  # -67% single bar
    assert _corp_action_warning("SPLIT", split) is not None
    two_for_one = np.concatenate([np.full(5, 100.0), np.full(5, 50.0)])  # exactly -50%
    assert _corp_action_warning("2FOR1", two_for_one) is not None
    merge = np.concatenate([np.full(5, 50.0), np.full(5, 110.0)])  # +120% single bar
    assert _corp_action_warning("MERGE", merge) is not None
    volatile = np.array([100.0, 130.0, 105.0, 140.0])  # <=40% moves stay silent
    assert _corp_action_warning("VOL", volatile) is None
    assert _corp_action_warning("ONE", np.array([100.0])) is None
    assert _corp_action_warning("NONPOS", np.array([0.0, 0.0, 100.0])) is None

    # (k) zscore_joint: a pooled affine transform preserves candle geometry; flat price
    # -> 0.0 price channels; volume standardized separately.
    geo = np.column_stack([
        np.linspace(100.0, 108.0, DEFAULT_WINDOW),      # open
        np.linspace(102.0, 112.0, DEFAULT_WINDOW),      # high (always the max)
        np.linspace(98.0, 104.0, DEFAULT_WINDOW),       # low  (always the min)
        np.linspace(101.0, 110.0, DEFAULT_WINDOW),      # close
        np.linspace(1_000.0, 3_000.0, DEFAULT_WINDOW),  # volume
    ])
    zj = zscore_joint_standardized(geo)
    assert np.all(zj[:, HIGH] >= zj[:, OPEN]) and np.all(zj[:, HIGH] >= zj[:, CLOSE])
    assert np.all(zj[:, LOW] <= zj[:, OPEN]) and np.all(zj[:, LOW] <= zj[:, CLOSE])
    assert abs(zj[:, PRICE_SLICE].mean()) < 1e-5
    assert abs(np.std(zj[:, PRICE_SLICE].ravel()) - 1.0) < 1e-5
    flat_j = np.tile([50.0, 50.0, 50.0, 50.0, 500.0], (DEFAULT_WINDOW, 1))
    fj = zscore_joint_standardized(flat_j)
    assert np.allclose(fj[:, PRICE_SLICE], 0.0) and np.allclose(fj[:, VOLUME], 0.0)

    # (l) log_return_ohlc: 4 channels; bar 0 gap == 0; known ratios; non-positive guard.
    lro_w = np.zeros((3, 5))
    lro_w[:, OPEN] = [100.0, 110.0, 120.0]
    lro_w[:, HIGH] = [105.0, 121.0, 120.0]
    lro_w[:, LOW] = [95.0, 99.0, 108.0]
    lro_w[:, CLOSE] = [102.0, 108.0, 120.0]
    lro = compute_log_returns_ohlc(lro_w)
    assert lro.shape == (3, 4) and lro[0, 0] == 0.0
    assert abs(lro[1, 0] - np.log(110.0 / 102.0)) < 1e-6   # gap = Open_1 / Close_0
    assert abs(lro[0, 1] - np.log(105.0 / 100.0)) < 1e-6   # hi
    assert abs(lro[0, 2] - np.log(95.0 / 100.0)) < 1e-6    # lo
    assert abs(lro[2, 3] - 0.0) < 1e-12                    # Close_2 == Open_2 -> ln 1 == 0
    lro_w[1, CLOSE] = 0.0
    assert compute_log_returns_ohlc(lro_w)[2, 0] == 0.0    # prev close 0 -> gap 0

    # (m) train_val_date_ranges: keys, ISO format, chronological order, and
    # empty-input -> empty strings.
    n_rows = 700
    def _sym(base: float) -> np.ndarray:
        p = np.linspace(base, base + 60.0, n_rows)
        a = np.empty((n_rows, 5))
        a[:, OPEN] = p
        a[:, HIGH] = p + 1.0
        a[:, LOW] = p - 1.0
        a[:, CLOSE] = p
        a[:, VOLUME] = np.linspace(1_000_000.0, 2_000_000.0, n_rows)
        return a
    _days = np.arange(n_rows).astype("timedelta64[D]")
    d_a = np.datetime64("2015-01-01") + _days
    # same era: the pooled training block precedes the purged validation block.
    dr = train_val_date_ranges(
        {"A": _sym(100.0), "B": _sym(80.0)}, {"A": d_a, "B": d_a.copy()},
        feature_mode="ohlcv_minmax", window=DEFAULT_WINDOW, horizon=DEFAULT_HORIZON,
    )
    assert set(dr) == set(DATE_RANGE_KEYS), dr
    assert all(len(v) == 10 for v in dr.values()), dr
    assert dr["training_start"] <= dr["training_end"] < dr["validation_start"] <= dr["validation_end"], dr
    # first training anchor is row window-1 of the earliest-dated symbol (no skips).
    assert dr["training_start"] == str(
        np.datetime64("2015-01-01") + np.timedelta64(DEFAULT_WINDOW - 1, "D")
    ), dr
    # disjoint eras: each side stays internally ordered even though the pooled
    # min/max spans can overlap across symbols.
    d_late = np.datetime64("2019-01-01") + _days
    dr2 = train_val_date_ranges(
        {"A": _sym(100.0), "B": _sym(90.0)}, {"A": d_a, "B": d_late},
        feature_mode="ohlcv_minmax", window=DEFAULT_WINDOW, horizon=DEFAULT_HORIZON,
    )
    assert dr2["training_start"] <= dr2["training_end"], dr2
    assert dr2["validation_start"] <= dr2["validation_end"], dr2
    assert dr2["training_start"] == dr["training_start"], dr2
    assert train_val_date_ranges({}, {}, feature_mode="ohlcv_minmax") == {k: "" for k in DATE_RANGE_KEYS}
    try:
        train_val_date_ranges({"A": _sym(100.0)}, {}, feature_mode="ohlcv_minmax")
    except ValueError:
        pass
    else:  # pragma: no cover
        raise AssertionError("expected ValueError when a symbol has no dates")

    # (n) load_parquet_dir symbol / calendar filters and resolve_timeframe_dir mapping.
    with tempfile.TemporaryDirectory() as _tmp:
        _tmpdir = Path(_tmp)
        _dates = pl.date_range(
            datetime.date(2021, 1, 1), datetime.date(2021, 1, 10), interval="1d", eager=True
        )
        for _name in ("AAA", "BBB"):
            pl.DataFrame({
                "date": _dates,
                "open": np.arange(10, dtype=np.float64) + 100.0,
                "high": np.arange(10, dtype=np.float64) + 101.0,
                "low": np.arange(10, dtype=np.float64) + 99.0,
                "close": np.arange(10, dtype=np.float64) + 100.5,
                "volume": np.full(10, 1_000.0),
            }).write_parquet(_tmpdir / f"{_name}.parquet")
        assert set(load_parquet_dir(_tmpdir)) == {"AAA", "BBB"}
        assert set(load_parquet_dir(_tmpdir, symbols=["aaa"])) == {"AAA"}  # case-insensitive
        ranged = load_parquet_dir(_tmpdir, symbols=["AAA"], start="2021-01-04", end="2021-01-06")
        assert ranged["AAA"].shape[0] == 3, ranged["AAA"].shape  # Jan 4, 5, 6 inclusive
        assert load_parquet_dir(_tmpdir, start="2021-02-01") == {}  # whole range filtered out
        try:
            load_parquet_dir(_tmpdir, symbols=[])
        except ValueError:
            pass
        else:  # pragma: no cover
            raise AssertionError("expected ValueError for an empty symbols filter")
        # materialize_filtered_dir: verbatim copy for a symbol-only filter, row filter for a bound.
        _staged = _tmpdir / "staged_sym"
        materialize_filtered_dir(_tmpdir, _staged, symbols=["AAA"])
        assert (_staged / "AAA.parquet").is_file() and not (_staged / "BBB.parquet").exists()
        _staged2 = _tmpdir / "staged_range"
        materialize_filtered_dir(_tmpdir, _staged2, start="2021-01-05", end="2021-01-07")
        assert load_parquet_dir(_staged2)["AAA"].shape[0] == 3  # Jan 5, 6, 7 inclusive
    assert resolve_timeframe_dir("daily") == DEFAULT_DATA_DIR
    assert resolve_timeframe_dir("WEEKLY").name == "Weekly"  # case-insensitive
    assert resolve_timeframe_dir("monthly", data_root=Path("x")).parts[-2:] == ("x", "Monthly")
    try:
        resolve_timeframe_dir("hourly")
    except ValueError:
        pass
    else:  # pragma: no cover
        raise AssertionError("expected ValueError for an unknown timeframe")

    # (o) wilder_atr: matches a hand-computed Wilder ATR and never looks ahead.
    atr_bars = np.zeros((20, 5))
    atr_bars[:, CLOSE] = np.arange(20, dtype=np.float64) + 100.0
    atr_bars[:, OPEN] = atr_bars[:, CLOSE]
    atr_bars[:, HIGH] = atr_bars[:, CLOSE] + 2.0
    atr_bars[:, LOW] = atr_bars[:, CLOSE] - 1.0
    atr = wilder_atr(atr_bars, period=5)
    assert np.all(np.isnan(atr[:5])) and np.isfinite(atr[5])
    # TR is constant: H-L = 3 every bar, |H-Cprev| = 3, |L-Cprev| = 0 -> TR = 3.
    assert abs(atr[5] - 3.0) < 1e-9 and abs(atr[-1] - 3.0) < 1e-9, atr[-1]
    # truncating the history after the anchor never changes an earlier ATR value.
    assert abs(wilder_atr(atr_bars[:12], period=5)[10] - wilder_atr(atr_bars, period=5)[10]) < 1e-12
    assert np.all(np.isnan(wilder_atr(atr_bars[:3], period=5)))

    # (p) label_triple_barrier: TP hit, SL hit, time-out, same-bar double touch (-> stop),
    # and a degenerate (non-finite) barrier -> Neutral. Anchor bar is never inspected.
    hi = np.array([10, 10, 12, 10, 10, 10], dtype=np.float64)
    lo = np.array([10, 10, 10, 8, 10, 10], dtype=np.float64)
    assert label_triple_barrier(hi, lo, anchor=0, tp_price=11.0, sl_price=9.0, max_horizon=5) == CLASS_UP
    assert label_triple_barrier(hi, lo, anchor=2, tp_price=13.0, sl_price=9.0, max_horizon=3) == CLASS_DOWN
    assert label_triple_barrier(hi, lo, anchor=0, tp_price=99.0, sl_price=-99.0, max_horizon=5) == CLASS_NEUTRAL
    both = label_triple_barrier(
        np.array([10.0, 12.0]), np.array([10.0, 8.0]),
        anchor=0, tp_price=11.0, sl_price=9.0, max_horizon=1,
    )
    assert both == CLASS_DOWN  # stop-loss precedence on a simultaneous touch
    assert label_triple_barrier(hi, lo, anchor=0, tp_price=float("nan"), sl_price=9.0, max_horizon=5) == CLASS_NEUTRAL
    # an anchor whose own bar breaches a barrier is ignored; only later bars count.
    assert label_triple_barrier(
        np.array([50.0, 10.0, 10.0]), np.array([50.0, 10.0, 10.0]),
        anchor=0, tp_price=11.0, sl_price=9.0, max_horizon=2,
    ) == CLASS_NEUTRAL

    # (q) oos_split: the tail is the newest `tail_days`, the split is disjoint and
    # covers every row, order is preserved, and tail_days <= 0 keeps everything.
    _oos_days = (np.datetime64("2022-01-01") + np.arange(100).astype("timedelta64[D]"))
    _oos_arr = np.column_stack([np.arange(100, dtype=np.float64)] * 5)
    m_arr, m_d, o_arr, o_d = oos_split(_oos_arr, _oos_days, tail_days=10)
    assert m_arr.shape[0] + o_arr.shape[0] == 100
    assert o_d.min() > m_d.max() and o_d.max() == _oos_days.max()
    assert (o_d.max() - o_d.min()) <= np.timedelta64(10, "D")
    assert np.array_equal(np.concatenate([m_arr[:, 0], o_arr[:, 0]]), np.arange(100, dtype=np.float64))
    m2_arr, _m2_d, o2_arr, _o2_d = oos_split(_oos_arr, _oos_days, tail_days=0)
    assert m2_arr.shape[0] == 100 and o2_arr.shape[0] == 0
    try:
        oos_split(_oos_arr, _oos_days[:50], tail_days=10)
    except ValueError:
        pass
    else:  # pragma: no cover
        raise AssertionError("expected ValueError when dates and ohlcv rows disagree")

    # (r) resolve_purge_gap: None -> window + horizon - 1; an explicit value passes through.
    assert resolve_purge_gap(30, 5) == 34
    assert resolve_purge_gap(30, 5, None) == 34
    assert resolve_purge_gap(1, 1) == 1
    assert resolve_purge_gap(30, 5, 0) == 0
    assert resolve_purge_gap(30, 5, 12) == 12

    # (r2) resolve_oos_cutoff: single source of truth for the OOS boundary date.
    # `cutoff = max_date - tail_days` calendar days (leap day inclusive), and both
    # oos_split and materialize_main_dir must land on that same boundary.
    assert resolve_oos_cutoff(datetime.date(2022, 3, 1), 10) == datetime.date(2022, 2, 19)
    assert resolve_oos_cutoff(datetime.date(2020, 3, 1), 1) == datetime.date(2020, 2, 29)  # leap day
    _cut_days = np.datetime64("2022-01-01") + np.arange(60).astype("timedelta64[D]")
    _cut_arr = np.column_stack([np.arange(60, dtype=np.float64) + 100.0] * 5)
    _cm_arr, _cm_d, _co_arr, _co_d = oos_split(_cut_arr, _cut_days, tail_days=7)
    _cut = resolve_oos_cutoff(_cut_days.max().astype(object), 7)
    assert _cm_d.max() <= np.datetime64(_cut) < _co_d.min()  # oos_split honours the shared cutoff
    with tempfile.TemporaryDirectory() as _tmp_cut:
        _tmpdir_cut = Path(_tmp_cut)
        pl.DataFrame({
            "date": pl.Series(_cut_days).cast(pl.Date),
            "open": _cut_arr[:, 0], "high": _cut_arr[:, 1], "low": _cut_arr[:, 2],
            "close": _cut_arr[:, 3], "volume": _cut_arr[:, 4],
        }).write_parquet(_tmpdir_cut / "CUT.parquet")
        materialize_main_dir(_tmpdir_cut, _tmpdir_cut / "staged", oos_tail_days=7)
        _cut_main, _cut_main_d = load_parquet_dir(_tmpdir_cut / "staged", return_dates=True)
        assert np.array_equal(_cut_main_d["CUT"], _cm_d)  # same boundary as oos_split

    # (s) materialize_main_dir: keeps exactly the rows oos_split would call "main"
    # (same per-symbol boundary), drops the rest, and is a no-op for tail_days <= 0.
    with tempfile.TemporaryDirectory() as _tmp_main:
        _tmpdir_main = Path(_tmp_main)
        _dates100 = np.datetime64("2022-01-01") + np.arange(100).astype("timedelta64[D]")
        _dates100_pl = pl.Series(_dates100).cast(pl.Date)
        for _name in ("AAA", "BBB"):
            pl.DataFrame({
                "date": _dates100_pl,
                "open": np.arange(100, dtype=np.float64) + 100.0,
                "high": np.arange(100, dtype=np.float64) + 101.0,
                "low": np.arange(100, dtype=np.float64) + 99.0,
                "close": np.arange(100, dtype=np.float64) + 100.0,
                "volume": np.full(100, 1_000.0),
            }).write_parquet(_tmpdir_main / f"{_name}.parquet")
        _orig_arrs, _orig_dates = load_parquet_dir(_tmpdir_main, return_dates=True)
        _staged_main = _tmpdir_main / "staged_main"
        materialize_main_dir(_tmpdir_main, _staged_main, oos_tail_days=10)
        _main_loaded, _main_dates = load_parquet_dir(_staged_main, return_dates=True)
        assert set(_main_loaded) == {"AAA", "BBB"}
        for _name in ("AAA", "BBB"):
            _expect_main, _expect_main_d, _expect_oos, _expect_oos_d = oos_split(
                _orig_arrs[_name], _orig_dates[_name], tail_days=10,
            )
            assert _main_loaded[_name].shape[0] == _expect_main.shape[0]
            assert np.array_equal(_main_dates[_name], _expect_main_d)
        _staged_nop = _tmpdir_main / "staged_nop"
        materialize_main_dir(_tmpdir_main, _staged_nop, oos_tail_days=0)
        assert load_parquet_dir(_staged_nop)["AAA"].shape[0] == 100  # tail_days <= 0 keeps everything

    # (22) composed_features (Price-only): price selection + per-channel window normalization
    cf_data = np.array([
        [10.0, 12.0, 9.0, 11.0, 100.0],
        [11.0, 13.0, 10.0, 12.0, 100.0],
        [12.0, 14.0, 11.0, 13.0, 100.0],
        [13.0, 15.0, 12.0, 14.0, 100.0],
        [14.0, 16.0, 13.0, 15.0, 100.0],
        [15.0, 17.0, 14.0, 16.0, 100.0],
    ])
    cf_channels = [
        {"kind": "price", "price": "close", "normalization": "none"},
        {"kind": "price", "price": "high", "normalization": "window_min_max"},
    ]
    cf_win0 = compose_features_window(cf_data, 0, 4, cf_channels)
    assert np.allclose(cf_win0[:, 0], [11.0, 12.0, 13.0, 14.0]), cf_win0[:, 0]
    assert np.allclose(cf_win0[:, 1], [0.0, 1 / 3, 2 / 3, 1.0]), cf_win0[:, 1]

    # true_high / true_low: previous-close clamp, including the "no previous bar" first row.
    th_data = np.array([
        [10.0, 10.0, 10.0, 10.0, 0.0],
        [9.0, 9.5, 8.0, 9.0, 0.0],
        [8.0, 8.5, 7.0, 7.5, 0.0],
    ])
    assert np.allclose(compose_price_channel_window(th_data, 0, 3, "true_high"), [10.0, 10.0, 9.0])
    assert np.allclose(compose_price_channel_window(th_data, 0, 3, "true_low"), [10.0, 8.0, 7.0])

    # Heikin-Ashi price types are rejected, not approximated (still out of scope).
    try:
        compose_price_channel_window(cf_data, 0, 4, "heikin_ashi_close")
        raise AssertionError("expected ValueError for a Heikin-Ashi composed price_type")
    except ValueError as exc:
        assert "Heikin-Ashi" in str(exc)

    # Indicator-kind channels: an index missing from indicator_columns is rejected outright
    # (never silently defaulted to zero), but a *matching* index is accepted (Task4).
    try:
        compose_features_window(cf_data, 0, 4, [{"kind": "indicator", "indicator": "Rsi"}])
        raise AssertionError("expected ValueError for an Indicator-kind channel with no indicator_columns")
    except ValueError:
        pass

    ind_channels = [
        {"kind": "price", "price": "close", "normalization": "none"},
        {"kind": "indicator", "indicator": "Rsi", "normalization": "none"},
    ]
    # channel_1 (index 1, matching ind_channels[1]) supplied pre-aligned, one value per cf_data row.
    ind_columns = {1: np.array([50.0, 55.0, 60.0, 65.0, 70.0, 75.0])}
    ind_win = compose_features_window(cf_data, 0, 4, ind_channels, ind_columns)
    assert np.allclose(ind_win[:, 0], [11.0, 12.0, 13.0, 14.0]), ind_win[:, 0]
    assert np.allclose(ind_win[:, 1], [50.0, 55.0, 60.0, 65.0]), ind_win[:, 1]

    try:
        compose_features_window(cf_data, 0, 4, ind_channels, {0: ind_columns[1]})
        raise AssertionError("expected ValueError when indicator_columns lacks the channel's own index")
    except ValueError:
        pass

    # build_dataset(feature_mode=composed_features) with an Indicator-kind channel: a temp export
    # parquet stands in for IndicatorChannelExporter.cs's output, exercising the real
    # _build_indicator_columns date-alignment path (FR-04) end-to-end.
    ind_dates = np.array(["2024-01-01"], dtype="datetime64[D]") + np.arange(cf_data.shape[0])
    with tempfile.TemporaryDirectory() as _ind_tmpdir:
        ind_export_path = Path(_ind_tmpdir) / "sa_indicator_channels_test_S1.parquet"
        pl.DataFrame({
            "date": pl.Series(ind_dates).cast(pl.Date),
            "channel_1": ind_columns[1],
        }).write_parquet(ind_export_path)

        ind_x, ind_y = build_dataset(
            cf_data, COMPOSED_FEATURES_MODE, window=4, horizon=1,
            feature_spec={"channels": ind_channels}, dates=ind_dates, indicator_export_path=ind_export_path,
        )
        assert ind_x.shape == (2, 4, 2), ind_x.shape
        assert np.allclose(ind_x[0, :, 1], [50.0, 55.0, 60.0, 65.0])
        assert np.allclose(ind_x[1, :, 1], [55.0, 60.0, 65.0, 70.0])

        # FR-05: a warmup (NaN) indicator value excludes that window rather than raising or
        # zero-filling -- here row 0's indicator value is not yet available, so the first
        # candidate window [0,4) is skipped and only [1,5) remains.
        warmup_columns = np.array([np.nan, 55.0, 60.0, 65.0, 70.0, 75.0])
        pl.DataFrame({
            "date": pl.Series(ind_dates).cast(pl.Date),
            "channel_1": warmup_columns,
        }).write_parquet(ind_export_path)
        warm_x, warm_y = build_dataset(
            cf_data, COMPOSED_FEATURES_MODE, window=4, horizon=1,
            feature_spec={"channels": ind_channels}, dates=ind_dates, indicator_export_path=ind_export_path,
        )
        assert warm_x.shape == (1, 4, 2), warm_x.shape
        assert np.allclose(warm_x[0, :, 1], [55.0, 60.0, 65.0, 70.0])

        # FR-04: a requested date absent from the export parquet raises ValueError rather than
        # silently misaligning rows. Shifting the *last* date forward (rather than the first)
        # avoids colliding with an existing date and instead genuinely drops the original last
        # date from the export.
        mismatched_dates = ind_dates.copy()
        mismatched_dates[-1] = mismatched_dates[-1] + np.timedelta64(1, "D")
        pl.DataFrame({
            "date": pl.Series(mismatched_dates).cast(pl.Date),
            "channel_1": ind_columns[1],
        }).write_parquet(ind_export_path)
        try:
            build_dataset(
                cf_data, COMPOSED_FEATURES_MODE, window=4, horizon=1,
                feature_spec={"channels": ind_channels}, dates=ind_dates, indicator_export_path=ind_export_path,
            )
            raise AssertionError("expected ValueError for a date missing from the indicator export")
        except ValueError as exc:
            assert "missing from indicator export" in str(exc)

        # Task8: a duplicate date within the export raises ValueError (fail closed rather than
        # picking an arbitrary matching row).
        pl.DataFrame({
            "date": pl.Series(np.concatenate([ind_dates, ind_dates[-1:]])).cast(pl.Date),
            "channel_1": np.concatenate([ind_columns[1], ind_columns[1][-1:]]),
        }).write_parquet(ind_export_path)
        try:
            build_dataset(
                cf_data, COMPOSED_FEATURES_MODE, window=4, horizon=1,
                feature_spec={"channels": ind_channels}, dates=ind_dates, indicator_export_path=ind_export_path,
            )
            raise AssertionError("expected ValueError for a duplicate date in the indicator export")
        except ValueError as exc:
            assert "duplicate date" in str(exc)

        # Restore the well-formed export before the OOS-style partial-subset lookup below.
        pl.DataFrame({
            "date": pl.Series(ind_dates).cast(pl.Date),
            "channel_1": ind_columns[1],
        }).write_parquet(ind_export_path)

        # Task8: `_build_indicator_columns` must support a *subset* of the export's dates, not
        # just its full row count/order -- this is what run_training.py's `evaluate_oos` needs,
        # since it calls build_dataset on a sliced OOS tail (ds.oos_split's output) while the
        # export parquet still holds the symbol's full history.
        oos_dates = ind_dates[2:]  # a subset, and not starting at row 0 of the export
        oos_x, oos_y = build_dataset(
            cf_data[2:], COMPOSED_FEATURES_MODE, window=2, horizon=1,
            feature_spec={"channels": ind_channels}, dates=oos_dates, indicator_export_path=ind_export_path,
        )
        assert oos_x.shape == (2, 2, 2), oos_x.shape
        # ind_columns[1] = [50, 55, 60, 65, 70, 75]; oos slice starts at export row 2 (value 60.0).
        assert np.allclose(oos_x[0, :, 1], [60.0, 65.0])
        assert np.allclose(oos_x[1, :, 1], [65.0, 70.0])

        # A date the export doesn't have at all (not just out of order) still raises ValueError.
        unknown_date = np.array(["2099-01-01"], dtype="datetime64[D]")
        try:
            build_dataset(
                cf_data[:1], COMPOSED_FEATURES_MODE, window=1, horizon=1,
                feature_spec={"channels": ind_channels}, dates=unknown_date, indicator_export_path=ind_export_path,
            )
            raise AssertionError("expected ValueError for a wholly-unknown requested date")
        except ValueError as exc:
            assert "missing from indicator export" in str(exc)

    try:
        build_dataset(
            cf_data, COMPOSED_FEATURES_MODE, window=4, horizon=1,
            feature_spec={"channels": ind_channels}, dates=ind_dates,
            indicator_export_path=Path(tempfile.gettempdir()) / "sa_selfcheck_does_not_exist.parquet",
        )
        raise AssertionError("expected ValueError for a missing indicator export file")
    except ValueError as exc:
        assert "not found" in str(exc)

    try:
        build_dataset(
            cf_data, COMPOSED_FEATURES_MODE, window=4, horizon=1,
            feature_spec={"channels": ind_channels},
        )
        raise AssertionError("expected ValueError when an Indicator-kind channel has no dates/export path")
    except ValueError:
        pass

    # build_dataset(feature_mode=composed_features): shape/values match the hand-derived windows
    # above, and the classification label is unaffected by the feature composition.
    cf_x, cf_y = build_dataset(
        cf_data, COMPOSED_FEATURES_MODE, window=4, horizon=1,
        threshold=DEFAULT_THRESHOLD, feature_spec={"channels": cf_channels},
    )
    assert cf_x.shape == (2, 4, 2), cf_x.shape
    assert np.array_equal(cf_y, np.array([CLASS_UP, CLASS_UP], dtype=np.int64)), cf_y
    assert np.allclose(cf_x[0, :, 0], [11.0, 12.0, 13.0, 14.0])
    assert np.allclose(cf_x[1, :, 0], [12.0, 13.0, 14.0, 15.0])
    assert np.allclose(cf_x[0, :, 1], [0.0, 1 / 3, 2 / 3, 1.0])

    # composed_features requires a non-empty feature_spec; every other mode requires none.
    try:
        build_dataset(cf_data, COMPOSED_FEATURES_MODE, window=4, horizon=1)
        raise AssertionError("expected ValueError for composed_features with no feature_spec")
    except ValueError:
        pass

    # D03 resource bounds (composed_features only): MaxChannels / MaxTensorSizeMB / MaxBatchSize
    # are checked before any allocation. The module-level bounds are monkeypatched down to small
    # numbers for the duration of this block so the boundary logic itself is exercised without
    # needing real-world-sized channel lists, windows, or histories.
    global MAX_COMPOSED_CHANNELS, MAX_COMPOSED_TENSOR_SIZE_MB, MAX_COMPOSED_BATCH_SAMPLES
    _orig_max_channels, _orig_max_tensor_mb, _orig_max_batch = (
        MAX_COMPOSED_CHANNELS, MAX_COMPOSED_TENSOR_SIZE_MB, MAX_COMPOSED_BATCH_SAMPLES,
    )
    try:
        MAX_COMPOSED_CHANNELS = 2
        too_many_channels = [{"kind": "price", "price": "close", "normalization": "none"}] * 3
        try:
            _validate_feature_spec_channels({"channels": too_many_channels})
            raise AssertionError("expected ValueError for channel count over MaxChannels")
        except ValueError as exc:
            assert "MaxChannels" in str(exc)
        assert len(_validate_feature_spec_channels({"channels": too_many_channels[:2]})) == 2

        MAX_COMPOSED_TENSOR_SIZE_MB = 1
        oversized_channels = [{"kind": "price", "price": "close", "normalization": "none"}] * 300
        try:
            compose_features_window(cf_data, 0, 1000, oversized_channels)
            raise AssertionError("expected ValueError for tensor size over MaxTensorSizeMB")
        except ValueError as exc:
            assert "MaxTensorSizeMB" in str(exc)

        MAX_COMPOSED_BATCH_SAMPLES = 3
        bs_data = np.tile(cf_data[0], (10, 1))
        try:
            build_dataset(
                bs_data, COMPOSED_FEATURES_MODE, window=1, horizon=1,
                feature_spec={"channels": cf_channels},
            )
            raise AssertionError("expected ValueError for sample count over MaxBatchSize")
        except ValueError as exc:
            assert "MaxBatchSize" in str(exc)

        s1 = np.tile(cf_data[0], (4, 1))
        s2 = np.tile(cf_data[0], (4, 1))
        try:
            build_dataset_multi(
                {"S1": s1, "S2": s2}, COMPOSED_FEATURES_MODE, window=1, horizon=1,
                feature_spec={"channels": cf_channels},
            )
            raise AssertionError("expected ValueError for cumulative sample count over MaxBatchSize")
        except ValueError as exc:
            assert "MaxBatchSize" in str(exc)
    finally:
        MAX_COMPOSED_CHANNELS, MAX_COMPOSED_TENSOR_SIZE_MB, MAX_COMPOSED_BATCH_SAMPLES = (
            _orig_max_channels, _orig_max_tensor_mb, _orig_max_batch,
        )

    # (24) target_type="regression" (T04): make_regression_target's forward log-return
    # formula, build_dataset's (N,1) float32 shape / extra future<=0 LabelGuard, and the
    # existing classification path staying byte-for-byte unchanged (regression guard).
    assert abs(make_regression_target(100.0, 105.0) - np.log(1.05)) < 1e-12
    assert abs(make_regression_target(50.0, 50.0)) < 1e-12  # flat -> ln(1) == 0

    closes = np.array([100.0, 101.0, 102.0, 103.0, 104.0, 105.0, 106.0, 107.0, 108.0, 109.0])
    reg_ohlcv = np.stack([closes, closes, closes, closes, np.full_like(closes, 1000.0)], axis=1)

    x_cls, y_cls = build_dataset(reg_ohlcv, "ohlcv_minmax", window=3, horizon=2)
    assert y_cls.dtype == np.int64 and y_cls.shape == (6,)  # unaffected by target_type default

    x_reg, y_reg = build_dataset(reg_ohlcv, "ohlcv_minmax", window=3, horizon=2, target_type="regression")
    assert y_reg.dtype == np.float32 and y_reg.shape == (6, 1)
    # anchor=data[2]=102, future=data[4]=104 for the first kept window (t=0)
    assert abs(float(y_reg[0, 0]) - np.log(104.0 / 102.0)) < 1e-5

    # future<=0 is excluded for regression only (ln() undefined there); classification keeps it
    # (its guard only requires future to be finite, not positive).
    # Row 9 is the last row: it is the "future" reference for t=5 only (future index =
    # t+window-1+horizon = 9) and is never an anchor or feature-window row for any valid t
    # (t max = 5, anchor index = t+2 <= 7), so corrupting it isolates the regression-only guard.
    neg_future = reg_ohlcv.copy()
    neg_future[9, CLOSE] = -5.0
    x_cls2, y_cls2 = build_dataset(neg_future, "ohlcv_minmax", window=3, horizon=2)
    assert y_cls2.shape == (6,)  # classification: no change, still 6 windows
    x_reg2, y_reg2 = build_dataset(neg_future, "ohlcv_minmax", window=3, horizon=2, target_type="regression")
    assert y_reg2.shape == (5, 1)  # regression: the t=5 window (future=-5) is dropped

    # empty-result shape stays target_type-specific too (too-short array -> no windows at all).
    tiny = reg_ohlcv[:2]
    _, y_empty_cls = build_dataset(tiny, "ohlcv_minmax", window=3, horizon=2)
    assert y_empty_cls.shape == (0,) and y_empty_cls.dtype == np.int64
    _, y_empty_reg = build_dataset(tiny, "ohlcv_minmax", window=3, horizon=2, target_type="regression")
    assert y_empty_reg.shape == (0, 1) and y_empty_reg.dtype == np.float32

    try:
        build_dataset(reg_ohlcv, "ohlcv_minmax", window=3, horizon=2, target_type="ranking")
        raise AssertionError("expected ValueError for unknown target_type")
    except ValueError as exc:
        assert "target_type" in str(exc)

    # split_symbols_chronological / train_val_date_ranges / build_dataset_multi forward
    # target_type through to build_dataset unchanged (plumbing-only regression guard).
    reg_dates = np.array(["2024-01-01"], dtype="datetime64[D]") + np.arange(len(closes))
    x_tr, y_tr, x_va, y_va = split_symbols_chronological(
        {"S1": reg_ohlcv}, "ohlcv_minmax", window=3, horizon=2, n_splits=2, target_type="regression",
    )
    assert y_tr.dtype == np.float32 and (y_tr.ndim == 1 or y_tr.shape[1] == 1)
    assert y_va.dtype == np.float32 and (y_va.ndim == 1 or y_va.shape[1] == 1)
    ranges_reg = train_val_date_ranges(
        {"S1": reg_ohlcv}, {"S1": reg_dates}, feature_mode="ohlcv_minmax",
        window=3, horizon=2, n_splits=2, target_type="regression",
    )
    assert set(ranges_reg.keys()) == set(DATE_RANGE_KEYS)
    x_multi, y_multi = build_dataset_multi(
        {"S1": reg_ohlcv}, "ohlcv_minmax", window=3, horizon=2, target_type="regression",
    )
    assert y_multi.dtype == np.float32 and y_multi.shape[1:] == (1,)

    print("dataset.py selfcheck: OK (28 groups passed)")


def _run_build(args: argparse.Namespace) -> None:
    data_dir = args.data_dir
    if args.timeframe is not None:
        data_dir = resolve_timeframe_dir(args.timeframe)
    want_dates = args.oos_tail_days is not None
    loaded = load_parquet_dir(
        data_dir,
        strict_corp_actions=args.strict_adjustment,
        symbols=args.symbols,
        start=args.start,
        end=args.end,
        return_dates=want_dates,
    )
    symbols, dates = loaded if want_dates else (loaded, None)
    if not symbols:
        print(f"No parquet files under {data_dir}")
        sys.exit(1)

    if want_dates:
        main_rows = oos_rows = 0
        for sym, arr in symbols.items():
            m_arr, _m_d, o_arr, _o_d = oos_split(arr, dates[sym], args.oos_tail_days)
            main_rows += m_arr.shape[0]
            oos_rows += o_arr.shape[0]
        print(
            f"oos_tail_days={args.oos_tail_days}: main rows={main_rows} "
            f"out-of-sample rows={oos_rows} (held out from training and CV)"
        )

    x, y = build_dataset_multi(
        symbols, args.feature_mode, window=args.window,
        horizon=args.horizon, threshold=args.threshold,
    )
    counts = {CLASS_LABELS[c]: int((y == c).sum()) for c in (CLASS_UP, CLASS_DOWN, CLASS_NEUTRAL)}
    print(f"symbols={len(symbols)} feature_mode={args.feature_mode}")
    print(f"X.shape={x.shape} dtype={x.dtype}")
    print(f"y.shape={y.shape} dtype={y.dtype}")
    print(f"class distribution={counts}")


def _parse_args(argv: List[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Build/verify the ONNX trend-predictor training dataset.")
    parser.add_argument("--data-dir", type=Path, default=DEFAULT_DATA_DIR)
    parser.add_argument(
        "--timeframe", choices=sorted(TIMEFRAME_DIRS), default=None,
        help="Resolve --data-dir from Data/{Daily,Weekly,Monthly} instead of passing a path.",
    )
    parser.add_argument("--feature-mode", choices=FEATURE_MODES, default="ohlcv_minmax")
    parser.add_argument("--window", type=int, default=DEFAULT_WINDOW)
    parser.add_argument("--horizon", type=int, default=DEFAULT_HORIZON)
    parser.add_argument("--threshold", type=float, default=DEFAULT_THRESHOLD)
    parser.add_argument(
        "--symbols", type=lambda s: [t.strip() for t in s.split(",") if t.strip()], default=None,
        metavar="SYM[,SYM...]", help="Restrict the build to these tickers (case-insensitive).",
    )
    parser.add_argument(
        "--start", type=str, default=None, metavar="YYYY-MM-DD",
        help="Inclusive lower bound on the date column.",
    )
    parser.add_argument(
        "--end", type=str, default=None, metavar="YYYY-MM-DD",
        help="Inclusive upper bound on the date column.",
    )
    parser.add_argument(
        "--oos-tail-days", type=int, default=None, metavar="N",
        help="Report how many rows the newest N calendar days (the fixed out-of-sample "
             "holdout, excluded from training and cross-validation) would carry.",
    )
    parser.add_argument("--selfcheck", action="store_true", help="Run boundary-math assertions and exit.")
    parser.add_argument(
        "--strict-adjustment", action="store_true",
        help="Raise (instead of warn) when a symbol has a bar return that looks like an unadjusted split/dividend.",
    )
    parser.add_argument(
        "--emit-parity-vectors", type=Path, default=None, metavar="PATH",
        help="Write raw windows + their feature transforms as JSON for the C#/Python parity test.",
    )
    return parser.parse_args(argv)


if __name__ == "__main__":
    ns = _parse_args(sys.argv[1:])
    if ns.selfcheck:
        _run_selfcheck()
    elif ns.emit_parity_vectors is not None:
        _run_emit_parity(ns)
    else:
        _run_build(ns)
