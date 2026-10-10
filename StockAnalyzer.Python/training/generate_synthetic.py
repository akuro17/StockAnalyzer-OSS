"""T20 synthetic OHLCV generator: a small unconditional WGAN-GP over windows of bar features, written as marked synthetic parquet.

Approved decisions (2026-10-10, ``Y:\\Temp\\sa_decision_options_onnx_research_t17_t22.md``): C01..C19 = star (C02 A, C03 A, C04 A, C05 A,
C06 A, C07 A, C08 A, C09 A, C10 A, C16 A, C17 A), G01..G20 = star:

* G01 method: WGAN-GP (a deliberate extension of the plain GAN wording of the source requirement, recorded here and in the manifest).
* G02 unconditional. G03 independent windows of ``--window-bars`` bars; a series is one window (never concatenated), so
  ``--bars <= --window-bars``.
* G04 the constraints hold by construction. Per bar the network yields r (close log return), g (open gap log), u >= 0 and d >= 0
  (softplus, log wicks) and v (log volume); then ``C_t = C_{t-1} e^r, O_t = C_{t-1} e^g, H_t = max(O,C) e^u, L_t = min(O,C) e^-d,
  V_t = round(e^v)``. Every generated series is validated anyway (G12).
* G05 the real training data is the main block (``dataset.oos_split``); ``--oos-tail-days`` is REQUIRED and >= 1, the out-of-sample tail is
  never used. G06 the start price is drawn (seeded) from the training window anchor closes. G07 ``V = round(exp(v))`` >= 0; real windows
  with a non-positive volume, a non-positive price or an OHLC that violates its own invariants are dropped and counted (no fill).
* G08 no rounding (float64). G09 business days (Mon-Fri) from ``--start-date`` (required: the source gives no default; a weekend is refused).
* G10 table ``TRAINING_DEFAULTS``. G11 ``--seed`` split with ``SeedSequence``; the same seed in the same environment gives the same series.
* G12 an invalid series is regenerated up to ``--max-retries``; beyond that the run FAILS and writes nothing.
* G13 ``--count`` (default 10) and ``--bars`` (default = window); the existing ``dataset.MAX_COMPOSED_*`` bounds apply (C10 A).
* G14 ids ``SYN_<first 8 hex digits of the config hash>_<4 digit number>`` (the hash part of the run id, amended 2026-10-10: the date part
  would repeat for runs of one day, and a symbol name is an identifier). G15 ``synthetic_manifest.json`` (``data_origin: synthetic``) plus the
  parquet key-value metadata ``sa.data_origin=synthetic``. G16 the tool refuses to write under ``Data/Daily|Weekly|Monthly`` (or the source
  directory), refuses a synthetic source directory, and offers ``is_synthetic_dir`` / ``is_synthetic_parquet`` (owned by ``dataset``, whose
  loaders refuse marked data by default since R4 / S01 B; this tool reads its own output only with ``allow_synthetic=True`` in its selfcheck).
* G17 generation and readability only: nothing here feeds a trainer. G18 only the invariants are judged; no quality claim is made.
  J1 (2026-10-10, WR10 A / DF22..DF25 / Q2 A / Q4 A) adds ``diagnostics`` to the manifest: descriptive statistics of the generated series beside
  the real training pool (moments, ACF of r and r^2, KS statistic, ``ln(1+V)`` quantiles, path diversity, the sampled loss curve). They are computed
  after generation, draw no random number and change no series or hash; there is no threshold and no pass / fail.
* G19 CPU only. G20 ``--selfcheck``.

The output is NOT observed market data. It must never be mixed into validation or out-of-sample data. The tool never touches an ONNX model.

Usage::

    python generate_synthetic.py --oos-tail-days 365 --symbols AAPL,MSFT --start-date 2000-01-03 --count 10
    python generate_synthetic.py --selfcheck
"""

from __future__ import annotations

import argparse
import datetime
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
from typing import Dict, List, Sequence, Tuple

import numpy as np
import polars as pl
import torch
from torch import nn

sys.path.insert(0, str(Path(__file__).resolve().parent))
import checkpoints  # noqa: E402
import dataset as ds  # noqa: E402

SCHEMA_VERSION: int = 1
TOOL: str = "generate_synthetic.py"
TOOL_VERSION: str = "1"
OUT_DIR: Path = ds.DATA_ROOT / "Synthetic"
MANIFEST: str = ds.SYNTHETIC_MANIFEST             # the marks have one owner: dataset (its loaders refuse marked data)
INCOMING_SUFFIX: str = ".incoming"
DATA_ORIGIN: str = ds.SYNTHETIC_ORIGIN
DATA_ORIGIN_KEY: str = ds.SYNTHETIC_ORIGIN_KEY
RUN_ID_KEY: str = "sa.synthetic_run_id"
SYMBOL_PREFIX: str = "SYN_"
CONFIG_HASH_CHARS: int = 8                  # G14: the config hash digits shared by the run id and the symbol prefix
SERIES_NUMBER_DIGITS: int = 4               # G14
PARQUET_COLUMNS: Tuple[str, ...] = ("date",) + tuple(ds.OHLCV_COLUMNS)
FEATURE_NAMES: Tuple[str, ...] = ("close_log_return", "open_gap_log", "upper_wick_log", "lower_wick_log", "log_volume")
N_FEATURES: int = len(FEATURE_NAMES)
F_RET, F_GAP, F_UP, F_DOWN, F_VOL = range(N_FEATURES)
INVARIANT_CODES: Tuple[str, ...] = (
    "non_finite", "non_positive_price", "high_below_open_close", "low_above_open_close", "negative_volume",
    "non_integer_volume", "volume_out_of_range", "dates_not_increasing", "date_not_business_day")
MAX_EXACT_VOLUME: float = float(2 ** 53)    # largest integer a float64 volume holds exactly
NOTICES: Tuple[str, ...] = ("synthetic_not_observed_data", "quality_not_measured")

EXIT_OK, EXIT_FAILED, EXIT_PREREQUISITE = 0, 1, 3     # C17

# G10: the named default table (CLI-overridable) and the validated bands.
TRAINING_DEFAULTS: Dict[str, object] = dict(
    window_bars=ds.DEFAULT_WINDOW, latent_dim=32, hidden=64, n_critic=5, gp_lambda=10.0, lr=1e-4, beta1=0.5, beta2=0.9,
    batch_size=64, steps=10_000)
LEAKY_SLOPE: float = 0.2
DEFAULT_COUNT: int = 10                     # G13
DEFAULT_MAX_RETRIES: int = 5                # G12
DEFAULT_STRIDE: int = 1
DEFAULT_SEED: int = 42                      # C08
DEFAULT_MAX_SYMBOLS: int = 12
DEFAULT_MIN_WINDOWS: int = 200              # NOT starred in the options: a plan value, flagged in the report
WINDOW_BARS_BAND: Tuple[int, int] = (2, 1024)           # G03 candidates 75 / 252 / 512
LATENT_BAND: Tuple[int, int] = (1, 512)
HIDDEN_BAND: Tuple[int, int] = (4, 2048)
N_CRITIC_BAND: Tuple[int, int] = (1, 20)
BATCH_BAND: Tuple[int, int] = (2, 4096)
MAX_RETRIES_BAND: Tuple[int, int] = (0, 100)
STRIDE_BAND: Tuple[int, int] = (1, 1024)
GP_LAMBDA_MAX: float = 1000.0
LR_MAX: float = 1.0
STD_FLOOR: float = 1e-12                    # a real feature with a smaller spread cannot be standardized
MAX_STEPS_ENV: str = "SA_SYNTH_MAX_STEPS"
MAX_STEPS: int = checkpoints.resolve_band(MAX_STEPS_ENV, 100_000, (1, 2_000_000), (1, 200_000))
MAX_SECONDS_ENV: str = "SA_SYNTH_MAX_SECONDS"
MAX_SECONDS: int = checkpoints.resolve_band(MAX_SECONDS_ENV, 3600, (1, 86_400), (60, 7_200))
DEADLINE_CHECK_STEPS: int = 20              # the run bound is tested every this many generator steps
# J1 (WR10 A, DF22..DF25, Q2 A, Q4 A): descriptive diagnostics, planning values with no empirical basis
LOSS_CURVE_POINTS: int = 100                # DF25 A: equally spaced steps of the sampled loss curve
ACF_LAGS: Tuple[int, ...] = (1, 5)          # DF22 A: autocorrelation lags of r and r^2
VOLUME_QUANTILES: Tuple[float, ...] = (0.05, 0.25, 0.5, 0.75, 0.95)
DIVERSITY_MAX_PATHS: int = 500              # the pairwise distance uses at most this many evenly spaced paths (cost bound)
PER_SERIES_MAX: int = 200                   # per-series rows are listed up to this count (the manifest stays small)
DIAGNOSTICS_STATEMENT: str = ("descriptive statistics of the generated series beside the real training pool of the same run; "
                              "NOT a quality claim, no threshold and no pass / fail")
STREAMS: Tuple[str, ...] = ("init", "batch", "noise", "price", "generate")


class PrerequisiteError(Exception):
    """The data or the requested output cannot be used (exit code 3)."""


class RunFailed(Exception):
    """The run started and failed (training diverged, the time bound, no valid series within the retry limit): exit code 1."""


# --- Isolation (G15/G16) ---------------------------------------------------------------------------------------------------------


def real_roots() -> Tuple[Path, ...]:
    """The observed-data directories (Daily / Weekly / Monthly) a synthetic run must never write under."""
    return tuple(ds.resolve_timeframe_dir(name).resolve() for name in ds.TIMEFRAME_DIRS)


def _inside(path: Path, root: Path) -> bool:
    path, root = Path(os.path.normcase(str(path.resolve()))), Path(os.path.normcase(str(root.resolve())))
    return path == root or root in path.parents


def guard_output_root(out_root: Path, source_dir: Path) -> None:
    """Refuse an output folder that is, or lies inside, an observed-data directory or the source directory."""
    for root in real_roots() + (Path(source_dir),):
        if _inside(Path(out_root), root):
            raise PrerequisiteError(f"refusing to write synthetic data under {root} (observed data is never written)")


is_synthetic_dir = ds.is_synthetic_dir
is_synthetic_parquet = ds.is_synthetic_parquet


# --- Real windows -> features ----------------------------------------------------------------------------------------------------


def bar_features(ohlcv: np.ndarray) -> Tuple[np.ndarray, np.ndarray, Dict[str, np.ndarray]]:
    """Features of bars 1..n-1 against the previous close, a validity mask and per-reason masks.

    A bar is valid only when every price and the volume are finite and positive and the OHLC respects its own invariants
    (``H >= max(O, C)``, ``L <= min(O, C)``): the generator learns from clean bars only, and a dropped bar is counted, not repaired.
    """
    o, h, l, c, v = (ohlcv[:, k] for k in (ds.OPEN, ds.HIGH, ds.LOW, ds.CLOSE, ds.VOLUME))
    with np.errstate(all="ignore"):
        prev = c[:-1]
        body_top, body_bottom = np.maximum(o, c)[1:], np.minimum(o, c)[1:]
        feats = np.stack([np.log(c[1:] / prev), np.log(o[1:] / prev), np.log(h[1:] / body_top),
                          np.log(body_bottom / l[1:]), np.log(v[1:])], axis=1)
        positive = (np.isfinite(ohlcv[1:, :4]).all(axis=1) & (ohlcv[1:, :4] > 0).all(axis=1) & (prev > 0) & np.isfinite(prev))
        volume_ok = np.isfinite(v[1:]) & (v[1:] > 0)
        wick_up_ok = h[1:] >= body_top
        wick_down_ok = l[1:] <= body_bottom
    reasons = {"non_positive_price": ~positive, "non_positive_volume": positive & ~volume_ok,
               "high_below_open_close": positive & volume_ok & ~wick_up_ok,
               "low_above_open_close": positive & volume_ok & wick_up_ok & ~wick_down_ok}
    valid = positive & volume_ok & wick_up_ok & wick_down_ok & np.isfinite(feats).all(axis=1)
    return feats, valid, reasons


@dataclass
class Pool:
    windows: np.ndarray            # (N, L, 5) float32 raw features
    start_prices: np.ndarray       # (N,) float64 anchor closes (the close before the first bar of each window)
    symbols: List[str]
    dropped: Dict[str, int]
    stride: int


def count_windows(valid: np.ndarray, window: int, stride: int) -> np.ndarray:
    """Start positions (in bar-feature index) of every length-*window* run of valid bars, taken every *stride* positions."""
    n = valid.shape[0]
    if n < window:
        return np.zeros(0, dtype=np.int64)
    bad = np.concatenate([[0], np.cumsum(~valid)])
    starts = np.arange(0, n - window + 1, stride)
    return starts[(bad[starts + window] - bad[starts]) == 0].astype(np.int64)


def build_pool(symbols: Dict[str, np.ndarray], window: int, stride: int) -> Pool:
    per_symbol = {}
    dropped = {"non_positive_price": 0, "non_positive_volume": 0, "high_below_open_close": 0, "low_above_open_close": 0,
               "windows_with_invalid_bars": 0}
    total = 0
    for name, arr in symbols.items():
        feats, valid, reasons = bar_features(arr)
        for key, mask in reasons.items():
            dropped[key] += int(mask.sum())
        starts = count_windows(valid, window, stride)
        positions = 0 if feats.shape[0] < window else len(range(0, feats.shape[0] - window + 1, stride))
        dropped["windows_with_invalid_bars"] += positions - int(starts.size)
        per_symbol[name] = (feats, starts)
        total += int(starts.size)
    wide = total * window * N_FEATURES * 8                 # python ints: no wrap
    if total > ds.MAX_COMPOSED_BATCH_SAMPLES:
        raise PrerequisiteError(f"{total} training windows exceed the bound {ds.MAX_COMPOSED_BATCH_SAMPLES}; "
                                f"reduce --max-symbols or the date range, or raise --stride")
    if wide > ds.MAX_COMPOSED_TENSOR_SIZE_MB * 1024 * 1024:
        raise PrerequisiteError(f"the training windows exceed {ds.MAX_COMPOSED_TENSOR_SIZE_MB} MB")
    names = [n for n, (_, s) in per_symbol.items() if s.size]
    windows = np.zeros((total, window, N_FEATURES), dtype=np.float32)
    prices = np.zeros(total, dtype=np.float64)
    at = 0
    offsets = np.arange(window)
    for name in names:
        feats, starts = per_symbol[name]
        k = starts.size
        windows[at:at + k] = feats[starts[:, None] + offsets[None, :]].astype(np.float32)
        prices[at:at + k] = symbols[name][starts, ds.CLOSE]      # row `start` is the close before bar `start`
        at += k
    return Pool(windows=windows, start_prices=prices, symbols=names, dropped=dropped, stride=stride)


@dataclass(frozen=True)
class FeatureStats:
    mean: np.ndarray               # (5,) float64
    std: np.ndarray

    def tensors(self) -> Tuple[torch.Tensor, torch.Tensor]:
        return torch.tensor(self.mean, dtype=torch.float32), torch.tensor(self.std, dtype=torch.float32)


def feature_stats(windows: np.ndarray) -> FeatureStats:
    mean = np.array([np.mean(windows[..., k], dtype=np.float64) for k in range(N_FEATURES)])
    std = np.array([np.std(windows[..., k], dtype=np.float64) for k in range(N_FEATURES)])
    low = [FEATURE_NAMES[k] for k in range(N_FEATURES) if not std[k] > STD_FLOOR]
    if low:
        raise PrerequisiteError(f"the training features have no spread (cannot be standardized): {low}")
    return FeatureStats(mean=mean, std=std)


# --- The network -----------------------------------------------------------------------------------------------------------------


def make_generator(latent: int, hidden: int, window: int) -> nn.Module:
    return nn.Sequential(nn.Linear(latent, hidden), nn.LeakyReLU(LEAKY_SLOPE), nn.Linear(hidden, hidden), nn.LeakyReLU(LEAKY_SLOPE),
                         nn.Linear(hidden, window * N_FEATURES))


def make_critic(hidden: int, window: int) -> nn.Module:
    return nn.Sequential(nn.Linear(window * N_FEATURES, hidden), nn.LeakyReLU(LEAKY_SLOPE), nn.Linear(hidden, hidden),
                         nn.LeakyReLU(LEAKY_SLOPE), nn.Linear(hidden, 1))


def to_features(raw: torch.Tensor, mean: torch.Tensor, std: torch.Tensor) -> torch.Tensor:
    """Raw network output (B, L, 5) -> constrained bar features: the wicks go through softplus so ``u, d >= 0`` always (G04)."""
    softplus = nn.functional.softplus
    return torch.stack([mean[F_RET] + std[F_RET] * raw[..., F_RET], mean[F_GAP] + std[F_GAP] * raw[..., F_GAP],
                        std[F_UP] * softplus(raw[..., F_UP]), std[F_DOWN] * softplus(raw[..., F_DOWN]),
                        mean[F_VOL] + std[F_VOL] * raw[..., F_VOL]], dim=-1)


def standardize(features: torch.Tensor, mean: torch.Tensor, std: torch.Tensor) -> torch.Tensor:
    return (features - mean) / std


def _seed_of(sequence: np.random.SeedSequence) -> int:
    return int(sequence.generate_state(1, dtype=np.uint64)[0] % (2 ** 63))


def stream_seeds(seed: int) -> Dict[str, int]:
    return {name: _seed_of(child) for name, child in zip(STREAMS, np.random.SeedSequence(seed).spawn(len(STREAMS)))}


@dataclass(frozen=True)
class TrainConfig:
    window_bars: int
    latent_dim: int
    hidden: int
    n_critic: int
    gp_lambda: float
    lr: float
    beta1: float
    beta2: float
    batch_size: int
    steps: int


def train_wgan_gp(windows: np.ndarray, stats: FeatureStats, cfg: TrainConfig, seeds: Dict[str, int], deadline: float):
    """WGAN-GP: Wasserstein critic loss + gradient penalty, ``n_critic`` critic steps per generator step, Adam. CPU, float32."""
    mean, std = stats.tensors()
    real_all = standardize(torch.from_numpy(windows), mean, std).reshape(windows.shape[0], -1)
    torch.manual_seed(seeds["init"])
    gen = make_generator(cfg.latent_dim, cfg.hidden, cfg.window_bars)
    critic = make_critic(cfg.hidden, cfg.window_bars)
    opt_g = torch.optim.Adam(gen.parameters(), lr=cfg.lr, betas=(cfg.beta1, cfg.beta2))
    opt_c = torch.optim.Adam(critic.parameters(), lr=cfg.lr, betas=(cfg.beta1, cfg.beta2))
    batch_rng = np.random.default_rng(seeds["batch"])
    noise = torch.Generator().manual_seed(seeds["noise"])
    n = real_all.shape[0]

    def fake_batch() -> torch.Tensor:
        z = torch.randn(cfg.batch_size, cfg.latent_dim, generator=noise)
        raw = gen(z).reshape(cfg.batch_size, cfg.window_bars, N_FEATURES)
        return standardize(to_features(raw, mean, std), mean, std).reshape(cfg.batch_size, -1)

    loss_c = loss_g = float("nan")
    curve_steps = set(np.unique(np.linspace(0, cfg.steps - 1, min(LOSS_CURVE_POINTS, cfg.steps)).round().astype(np.int64)).tolist())
    curve: List[dict] = []
    for step in range(cfg.steps):
        if step % DEADLINE_CHECK_STEPS == 0 and time.monotonic() > deadline:
            raise RunFailed(f"the run time bound was exceeded during training (step {step}/{cfg.steps})")
        for _ in range(cfg.n_critic):
            real = real_all[torch.from_numpy(batch_rng.integers(0, n, size=cfg.batch_size))]
            with torch.no_grad():
                fake = fake_batch()
            eps = torch.rand(cfg.batch_size, 1, generator=noise)
            mixed = (eps * real + (1.0 - eps) * fake).requires_grad_(True)
            grad = torch.autograd.grad(critic(mixed).sum(), mixed, create_graph=True)[0]
            penalty = ((grad.norm(2, dim=1) - 1.0) ** 2).mean()
            critic_loss = critic(fake).mean() - critic(real).mean() + cfg.gp_lambda * penalty
            opt_c.zero_grad(set_to_none=True)
            critic_loss.backward()
            opt_c.step()
        generator_loss = -critic(fake_batch()).mean()
        opt_g.zero_grad(set_to_none=True)
        generator_loss.backward()
        opt_g.step()
        loss_c, loss_g = float(critic_loss.item()), float(generator_loss.item())
        if not (math.isfinite(loss_c) and math.isfinite(loss_g)):
            raise RunFailed(f"training diverged (non-finite loss at step {step})")
        if step in curve_steps:
            curve.append(dict(step=step, critic_loss=loss_c, generator_loss=loss_g))
    return gen, dict(final_critic_loss=loss_c, final_generator_loss=loss_g, loss_curve=curve)


# --- Series construction and validation (G04 / G12) -----------------------------------------------------------------------------


def build_ohlcv(features: np.ndarray, start_price: float) -> np.ndarray:
    """(n, 5) float64 features -> (n, 5) OHLCV (volume as a float): ``C_t = C_{t-1} e^r`` ... exactly as G04."""
    r, g, u, d, v = (features[:, k] for k in range(N_FEATURES))
    with np.errstate(all="ignore"):
        close = start_price * np.exp(np.cumsum(r))
        prev = np.concatenate([[start_price], close[:-1]])
        open_ = prev * np.exp(g)
        high = np.maximum(open_, close) * np.exp(u)
        low = np.minimum(open_, close) * np.exp(-d)
        volume = np.round(np.exp(v))
    return np.stack([open_, high, low, close, volume], axis=1)


def business_dates(start: datetime.date, bars: int) -> np.ndarray:
    """*bars* consecutive business days (Mon-Fri) from *start*; a weekend start is refused, never rolled."""
    first = np.datetime64(start, "D")
    try:
        return np.busday_offset(first, np.arange(bars), roll="raise")
    except ValueError as exc:
        raise PrerequisiteError(f"--start-date {start.isoformat()} is not a business day (Mon-Fri)") from exc


def validate_series(ohlcv: np.ndarray, dates: np.ndarray) -> List[str]:
    """Violation codes of one series (empty = valid). Checks the G04/G12 invariants, including their boundary values."""
    codes = set()
    arr = np.asarray(ohlcv, dtype=np.float64)
    if arr.ndim != 2 or arr.shape[1] != 5 or arr.shape[0] != np.asarray(dates).shape[0]:
        return ["non_finite"]
    if not np.isfinite(arr).all():
        codes.add("non_finite")
        arr = np.where(np.isfinite(arr), arr, np.nan)
    o, h, l, c, v = (arr[:, k] for k in range(5))
    with np.errstate(invalid="ignore"):
        if (arr[:, :4] <= 0).any():
            codes.add("non_positive_price")
        if (h < np.maximum(o, c)).any():
            codes.add("high_below_open_close")
        if (l > np.minimum(o, c)).any():
            codes.add("low_above_open_close")
        if (v < 0).any():
            codes.add("negative_volume")
        if (v != np.round(v)).any():
            codes.add("non_integer_volume")
        if (v > MAX_EXACT_VOLUME).any():
            codes.add("volume_out_of_range")
    days = np.asarray(dates).astype("datetime64[D]").reshape(-1)
    if days.shape[0] > 1 and (days[1:] <= days[:-1]).any():
        codes.add("dates_not_increasing")
    if days.shape[0] and not np.is_busday(days).all():
        codes.add("date_not_business_day")
    return sorted(codes)


@dataclass
class Generated:
    series: List[np.ndarray]       # (bars, 5) float64 each, volume integral
    attempts: List[int]
    dates: np.ndarray


def generate_series(gen: nn.Module, stats: FeatureStats, cfg: TrainConfig, *, count: int, bars: int, start: datetime.date,
                    start_prices: np.ndarray, seeds: Dict[str, int], max_retries: int, deadline: float) -> Generated:
    """*count* validated series. An invalid one is regenerated up to *max_retries* times, then the whole run fails (G12)."""
    mean, std = stats.tensors()
    dates = business_dates(start, bars)
    noise = torch.Generator().manual_seed(seeds["generate"])
    price_rng = np.random.default_rng(seeds["price"])
    series, attempts = [], []
    gen.eval()
    for index in range(count):
        price = float(start_prices[int(price_rng.integers(0, start_prices.shape[0]))])
        codes: List[str] = []
        for attempt in range(1, max_retries + 2):
            if time.monotonic() > deadline:
                raise RunFailed("the run time bound was exceeded during generation")
            with torch.no_grad():
                raw = gen(torch.randn(1, cfg.latent_dim, generator=noise)).reshape(1, cfg.window_bars, N_FEATURES)
                features = to_features(raw, mean, std)[0, :bars].numpy().astype(np.float64)
            candidate = build_ohlcv(features, price)
            codes = validate_series(candidate, dates)
            if not codes:
                series.append(candidate)
                attempts.append(attempt)
                break
        else:
            raise RunFailed(f"series {index + 1}/{count} stayed invalid after {max_retries} retries ({', '.join(codes)}); nothing written")
    return Generated(series=series, attempts=attempts, dates=dates)


# --- Descriptive diagnostics (J1; computed AFTER generation from the finished series: no random stream, no change to any series) ----------


def _num(value) -> float | None:
    """A finite float or ``None`` (the manifest is written with ``allow_nan=False``; a statistic that does not exist is not replaced by 0)."""
    value = float(value)
    return value if math.isfinite(value) else None


def moments(x: np.ndarray) -> dict:
    """Mean, SD, skewness and EXCESS kurtosis with population moments (ddof 0, as the realized volatility of this repository)."""
    x = np.asarray(x, dtype=np.float64).reshape(-1)
    if x.size < 2:
        return dict(n=int(x.size), mean=None, std=None, skewness=None, excess_kurtosis=None)
    mean = float(x.mean())
    centered = x - mean
    var = float(np.mean(centered ** 2))
    std = math.sqrt(var)
    if not std > STD_FLOOR:
        return dict(n=int(x.size), mean=_num(mean), std=_num(std), skewness=None, excess_kurtosis=None)
    return dict(n=int(x.size), mean=_num(mean), std=_num(std), skewness=_num(np.mean(centered ** 3) / std ** 3),
                excess_kurtosis=_num(np.mean(centered ** 4) / var ** 2 - 3.0))


def acf_rows(x: np.ndarray, lag: int) -> np.ndarray:
    """Biased autocorrelation per row: ``sum_t (x_t - m)(x_{t+lag} - m) / sum_t (x_t - m)^2`` (mean removed, full-sum denominator); NaN when undefined."""
    x = np.atleast_2d(np.asarray(x, dtype=np.float64))
    if x.shape[1] <= lag:
        return np.full(x.shape[0], np.nan)
    centered = x - x.mean(axis=1, keepdims=True)
    denominator = (centered ** 2).sum(axis=1)
    with np.errstate(all="ignore"):
        return np.where(denominator > STD_FLOOR, (centered[:, lag:] * centered[:, :-lag]).sum(axis=1) / denominator, np.nan)


def _mean_or_none(values: np.ndarray) -> float | None:
    values = values[np.isfinite(values)]
    return _num(values.mean()) if values.size else None


def ks_statistic(a: np.ndarray, b: np.ndarray) -> float | None:
    """Two-sample ``sup |F_a - F_b|`` from the empirical distribution functions (statistic only: no p value)."""
    a, b = np.sort(np.asarray(a, dtype=np.float64).reshape(-1)), np.sort(np.asarray(b, dtype=np.float64).reshape(-1))
    if a.size == 0 or b.size == 0:
        return None
    grid = np.concatenate([a, b])
    return _num(np.abs(np.searchsorted(a, grid, side="right") / a.size - np.searchsorted(b, grid, side="right") / b.size).max())


def mean_pairwise_distance(paths: np.ndarray) -> float | None:
    """Mean Euclidean distance over the unordered pairs of rows (``None`` for fewer than two rows)."""
    paths = np.asarray(paths, dtype=np.float64)
    if paths.shape[0] < 2:
        return None
    squared = (paths ** 2).sum(axis=1)
    d2 = np.maximum(squared[:, None] + squared[None, :] - 2.0 * paths @ paths.T, 0.0)
    return _num(np.sqrt(d2)[np.triu_indices(paths.shape[0], k=1)].mean())


def standardized_paths(returns: np.ndarray) -> np.ndarray:
    """Each row minus its mean and divided by its SD; rows without spread are dropped (they carry no shape)."""
    returns = np.atleast_2d(np.asarray(returns, dtype=np.float64))
    std = returns.std(axis=1)
    keep = std > STD_FLOOR
    return (returns[keep] - returns[keep].mean(axis=1, keepdims=True)) / std[keep, None]


def _evenly_spaced(n: int, k: int) -> np.ndarray:
    """*k* deterministic indices over ``0 .. n-1`` (no random numbers are drawn)."""
    return np.unique(np.linspace(0, n - 1, min(k, n)).round().astype(np.int64))


def summarize(returns: np.ndarray, volume: np.ndarray) -> dict:
    """Log return moments, ACF of r and r^2 at ``ACF_LAGS`` (mean over rows) and the ``ln(1 + V)`` mean, SD and quantiles."""
    returns, volume = np.atleast_2d(returns), np.atleast_2d(volume)
    log_volume = np.log1p(volume)
    flat = log_volume.reshape(-1)
    return dict(
        log_return=dict(moments(returns), **{f"acf_lag{lag}": _mean_or_none(acf_rows(returns, lag)) for lag in ACF_LAGS}),
        log_return_squared={f"acf_lag{lag}": _mean_or_none(acf_rows(returns ** 2, lag)) for lag in ACF_LAGS},
        log1p_volume=dict(n=int(flat.size), mean=_num(flat.mean()), std=_num(flat.std()),
                          quantiles={str(q): _num(np.quantile(flat, q)) for q in VOLUME_QUANTILES}))


def quality_diagnostics(series: List[np.ndarray], pool_windows: np.ndarray, bars: int, loss_curve: List[dict]) -> dict:
    """J1: descriptive statistics of the generated series beside the real training pool (WR10 A, DF22..DF25). No threshold, no judgement.

    Generated returns come from ``bar_features`` of each finished series (``bars - 1`` returns); the real side uses the first ``bars - 1`` returns
    of every pool window (the pool windows overlap when the stride is below the window, which weights bars unevenly at the edges only).
    Diversity = the mean pairwise distance of the standardized return paths of the generated series divided by the same figure of an equal
    number of evenly spaced real windows (``null`` for one series)."""
    count = len(series)
    m = bars - 1
    generated_returns = np.stack([bar_features(arr)[0][:, F_RET] for arr in series]) if m >= 1 else np.zeros((count, 0))
    generated_volume = np.stack([arr[:, ds.VOLUME] for arr in series])
    real_returns = np.asarray(pool_windows[:, :m, F_RET], dtype=np.float64)
    real_volume = np.round(np.exp(np.asarray(pool_windows[:, :bars, F_VOL], dtype=np.float64)))
    out = dict(
        statement=DIAGNOSTICS_STATEMENT, bars_compared=int(bars), generated_series=count, real_windows=int(pool_windows.shape[0]),
        definitions=dict(
            log_return="r_t = ln(C_t / C_{t-1}); mean / SD / skewness / excess kurtosis are population moments (ddof 0)",
            acf="biased autocorrelation of r and r^2 at the listed lags, mean removed, full-sum denominator; averaged over the series",
            ks_log_return="two-sample sup |F_generated - F_real| of r (statistic only, no p value)",
            log1p_volume="ln(1 + V): mean, SD and quantiles", diversity=("mean pairwise distance of the standardized return paths of the generated "
                                                                      "series / the same figure of an equal number of evenly spaced real windows"),
            loss_curve=f"critic and generator loss at {LOSS_CURVE_POINTS} equally spaced steps (a loss value is not a quality measure)"),
        generated=summarize(generated_returns, generated_volume), real=summarize(real_returns, real_volume),
        ks_log_return=ks_statistic(generated_returns, real_returns) if m >= 1 else None, loss_curve=loss_curve)
    paths = standardized_paths(generated_returns) if m >= 2 else np.zeros((0, 0))
    diversity = dict(path_bars=int(m), generated_paths=int(paths.shape[0]), ratio_to_real=None, generated=None, real=None)
    if paths.shape[0] >= 2:
        generated_sample = paths[_evenly_spaced(paths.shape[0], DIVERSITY_MAX_PATHS)]
        real_paths = standardized_paths(real_returns)
        if real_paths.shape[0] >= generated_sample.shape[0]:
            real_sample = real_paths[_evenly_spaced(real_paths.shape[0], generated_sample.shape[0])]
            gen_d, real_d = mean_pairwise_distance(generated_sample), mean_pairwise_distance(real_sample)
            diversity.update(generated=gen_d, real=real_d, paths_used=int(generated_sample.shape[0]),
                             ratio_to_real=_num(gen_d / real_d) if gen_d is not None and real_d else None)
    out["diversity"] = diversity
    if count <= PER_SERIES_MAX:
        out["per_series"] = [dict(summarize(generated_returns[i], generated_volume[i]),
                                  ks_log_return=ks_statistic(generated_returns[i], real_returns) if m >= 1 else None) for i in range(count)]
    else:
        out["per_series_omitted"] = f"more than {PER_SERIES_MAX} series"
    return out


# --- Output ----------------------------------------------------------------------------------------------------------------------


def run_id_of(created: datetime.datetime, config_sha: str) -> str:
    return f"{created.strftime('%Y%m%dT%H%M%SZ')}_{config_sha[:CONFIG_HASH_CHARS]}"


def series_name(config_sha: str, index: int) -> str:
    return f"{SYMBOL_PREFIX}{config_sha[:CONFIG_HASH_CHARS]}_{index + 1:0{SERIES_NUMBER_DIGITS}d}"


def write_series(path: Path, ohlcv: np.ndarray, dates: np.ndarray, run_id: str) -> None:
    frame = pl.DataFrame({"date": dates.astype("datetime64[D]"), "open": ohlcv[:, 0], "high": ohlcv[:, 1], "low": ohlcv[:, 2],
                          "close": ohlcv[:, 3], "volume": ohlcv[:, 4].astype(np.int64)})
    frame.write_parquet(path, metadata={DATA_ORIGIN_KEY: DATA_ORIGIN, RUN_ID_KEY: run_id})


# --- Run -------------------------------------------------------------------------------------------------------------------------


def _bounded(name: str, value, band: Tuple[int, int]) -> None:
    if type(value) is not int or not band[0] <= value <= band[1]:
        raise ValueError(f"--{name.replace('_', '-')} must be an integer in {band[0]}..{band[1]}")


def validate_args(args: argparse.Namespace) -> None:
    _bounded("window_bars", args.window_bars, WINDOW_BARS_BAND)
    _bounded("latent_dim", args.latent_dim, LATENT_BAND)
    _bounded("hidden", args.hidden, HIDDEN_BAND)
    _bounded("n_critic", args.n_critic, N_CRITIC_BAND)
    _bounded("batch_size", args.batch_size, BATCH_BAND)
    _bounded("max_retries", args.max_retries, MAX_RETRIES_BAND)
    _bounded("stride", args.stride, STRIDE_BAND)
    _bounded("steps", args.steps, (1, MAX_STEPS))
    _bounded("max_seconds", args.max_seconds, (1, MAX_SECONDS))
    for name, low in (("seed", 0), ("max_symbols", 1), ("min_windows", 1), ("count", 1), ("oos_tail_days", 1)):
        value = getattr(args, name)
        if type(value) is not int or value < low:
            raise ValueError(f"--{name.replace('_', '-')} must be an integer >= {low}"
                             + (" (using the out-of-sample tail is not allowed)" if name == "oos_tail_days" else ""))
    if not (isinstance(args.gp_lambda, (int, float)) and 0 < args.gp_lambda <= GP_LAMBDA_MAX):
        raise ValueError(f"--gp-lambda must be in (0, {GP_LAMBDA_MAX}]")
    if not (isinstance(args.lr, (int, float)) and 0 < args.lr <= LR_MAX):
        raise ValueError(f"--lr must be in (0, {LR_MAX}]")
    bars = args.window_bars if args.bars is None else args.bars
    if type(bars) is not int or not 1 <= bars <= args.window_bars:
        raise ValueError(f"--bars must be an integer in 1..--window-bars ({args.window_bars}): a series is one generated window")
    rows = args.count * bars                                                    # python ints: no wrap
    if rows > ds.MAX_COMPOSED_BATCH_SAMPLES:
        raise ValueError(f"--count x --bars = {rows} rows exceed the bound {ds.MAX_COMPOSED_BATCH_SAMPLES}")
    if rows * 5 * 8 > ds.MAX_COMPOSED_TENSOR_SIZE_MB * 1024 * 1024:
        raise ValueError(f"the generated rows exceed {ds.MAX_COMPOSED_TENSOR_SIZE_MB} MB")
    if not isinstance(args.start_date, datetime.date):
        raise ValueError("--start-date (YYYY-MM-DD) is required")


def run(args: argparse.Namespace) -> int:
    started = time.monotonic()
    try:
        return _run(args, started)
    except PrerequisiteError as exc:
        print(f"generate_synthetic: PREREQUISITE NOT MET: {exc}", file=sys.stderr)
        return EXIT_PREREQUISITE
    except RunFailed as exc:
        print(f"generate_synthetic: FAILED: {exc}", file=sys.stderr)
        return EXIT_FAILED


def _load_main_block(args: argparse.Namespace) -> Tuple[Dict[str, np.ndarray], Dict[str, np.ndarray], str]:
    wanted = [t.strip() for t in args.symbols.split(",") if t.strip()] if args.symbols else None
    if args.symbols is not None and not wanted:
        raise PrerequisiteError("--symbols names no symbol")
    try:
        loaded, loaded_dates = ds.load_parquet_dir(args.data_dir, return_dates=True, symbols=wanted)
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
    common_end = ds.common_end_date(dates)
    main_block_end = ds.resolve_oos_cutoff(common_end, args.oos_tail_days).isoformat()
    for s in list(symbols):
        main_arr, main_dates, _oos_arr, _oos_dates = ds.oos_split(symbols[s], dates[s], args.oos_tail_days, common_end)
        if main_arr.shape[0] == 0:
            del symbols[s], dates[s]
        else:
            symbols[s], dates[s] = main_arr, main_dates
    if not symbols:
        raise PrerequisiteError("the out-of-sample tail left no rows")
    return symbols, dates, main_block_end


def _run(args: argparse.Namespace, started: float) -> int:
    validate_args(args)
    deadline = started + args.max_seconds
    out_root, source = Path(args.out_root), Path(args.data_dir)
    guard_output_root(out_root, source)
    try:
        source_is_synthetic = is_synthetic_dir(source)
    except ValueError as exc:  # a manifest that exists but cannot be read: the source cannot be classified (DF14 B)
        raise PrerequisiteError(str(exc)) from exc
    if source_is_synthetic:
        raise PrerequisiteError(f"{source} holds synthetic data; the generator learns from observed data only")
    symbols, dates, main_block_end = _load_main_block(args)
    data_revision = checkpoints.source_revision(symbols, dates)
    bars = args.window_bars if args.bars is None else args.bars
    pool = build_pool(symbols, args.window_bars, args.stride)
    n_windows = int(pool.windows.shape[0])
    if n_windows < args.min_windows:
        raise PrerequisiteError(f"insufficient_data: {n_windows} training windows < --min-windows {args.min_windows} "
                                f"(window {args.window_bars} bars; dropped {pool.dropped})")
    stats = feature_stats(pool.windows)
    cfg = TrainConfig(window_bars=args.window_bars, latent_dim=args.latent_dim, hidden=args.hidden, n_critic=args.n_critic,
                      gp_lambda=float(args.gp_lambda), lr=float(args.lr), beta1=TRAINING_DEFAULTS["beta1"],
                      beta2=TRAINING_DEFAULTS["beta2"], batch_size=args.batch_size, steps=args.steps)
    config = dict(
        method="wgan_gp", conditioning="none", parameterization="bar_features_v1", features=list(FEATURE_NAMES),
        training=dict(vars(cfg)), count=args.count, bars=bars, start_date=args.start_date.isoformat(), stride=args.stride,
        max_retries=args.max_retries, min_windows=args.min_windows, seed=args.seed, symbols=pool.symbols,
        oos_tail_days=args.oos_tail_days, data_revision=data_revision)
    config_sha = hashlib.sha256(checkpoints.canonical(config).encode()).hexdigest()
    created = datetime.datetime.now(datetime.timezone.utc)
    run_id = run_id_of(created, config_sha)
    final_dir, work = out_root / run_id, out_root / (run_id + INCOMING_SUFFIX)
    guard_output_root(final_dir, source)
    if final_dir.exists() or work.exists():
        raise PrerequisiteError(f"run folder already exists: {final_dir}")

    seeds = stream_seeds(args.seed)
    gen, training = train_wgan_gp(pool.windows, stats, cfg, seeds, deadline)
    loss_curve = training.pop("loss_curve")
    generated = generate_series(gen, stats, cfg, count=args.count, bars=bars, start=args.start_date, start_prices=pool.start_prices,
                                seeds=seeds, max_retries=args.max_retries, deadline=deadline)

    diagnostics = quality_diagnostics(generated.series, pool.windows, bars, loss_curve)
    out_root.mkdir(parents=True, exist_ok=True)
    work.mkdir(parents=True)
    try:
        files = []
        for index, ohlcv in enumerate(generated.series):
            name = series_name(config_sha, index)
            path = work / f"{name}.parquet"
            write_series(path, ohlcv, generated.dates, run_id)
            files.append(dict(name=path.name, symbol=name, rows=int(ohlcv.shape[0]), sha256=checkpoints.hash_file(path)))
        manifest = dict(
            schema_version=SCHEMA_VERSION, tool=TOOL, tool_version=TOOL_VERSION, created_utc=created.isoformat(), run_id=run_id,
            data_origin=DATA_ORIGIN, status="ok", data_revision=data_revision, config_sha256=config_sha, seed=args.seed, config=config,
            generator=dict(method="wgan_gp", conditioning="none", device="cpu", torch_version=str(torch.__version__),
                           deviation_from_source="WGAN-GP instead of the plain GAN wording (G01=B, approved 2026-10-10)"),
            training=dict(windows=n_windows, symbols=pool.symbols, dropped=pool.dropped, feature_mean=[float(x) for x in stats.mean],
                          feature_std=[float(x) for x in stats.std], **training),
            generation=dict(count=args.count, bars=bars, start_date=args.start_date.isoformat(), date_axis="business_days_mon_fri",
                            start_price_source="training_anchor_close_distribution", attempts=generated.attempts,
                            retries_used=int(sum(a - 1 for a in generated.attempts)), max_retries=args.max_retries),
            invariants=dict(checked=list(INVARIANT_CODES), violations=0),
            isolation=dict(oos_tail_days=args.oos_tail_days, main_block_end=main_block_end, oos_rows_used=False,
                           symbol_prefix=SYMBOL_PREFIX, parquet_metadata=f"{DATA_ORIGIN_KEY}={DATA_ORIGIN}",
                           refused_roots=sorted(ds.TIMEFRAME_DIRS.values())),
            diagnostics=diagnostics, notices=list(NOTICES), files=files, elapsed_seconds=round(time.monotonic() - started, 3))
        checkpoints.atomic_json(work / MANIFEST, manifest)          # manifest last
        os.rename(work, final_dir)
    except BaseException:
        shutil.rmtree(work, ignore_errors=True)
        raise
    print(f"generate_synthetic: OK  run={run_id}  folder={final_dir}")
    print(f"  series={args.count} bars={bars} training_windows={n_windows} steps={args.steps} retries={manifest['generation']['retries_used']}")
    print("  NOTE: synthetic data -- not observed market data; only the structural invariants were checked, not the realism.")
    return EXIT_OK


# --- Self-check ------------------------------------------------------------------------------------------------------------------


def _source_dir(root: Path, symbols: Sequence[str], rows: int, seed: int, tail_shock: bool = False) -> Path:
    """Adjusted-looking random-walk OHLCV per symbol (Float32 prices, Int64 volume, business days). *tail_shock* alters the last 40 rows."""
    directory = root / "src"
    directory.mkdir(parents=True, exist_ok=True)
    days = np.busday_offset(np.datetime64("2015-01-05"), np.arange(rows))
    for k, sym in enumerate(symbols):
        rng = np.random.default_rng(seed + k)
        close = 100.0 * np.exp(np.cumsum(rng.normal(0.0003, 0.012, rows)))
        prev = np.concatenate([[100.0], close[:-1]])
        open_ = prev * np.exp(rng.normal(0, 0.004, rows))
        high = np.maximum(open_, close) * np.exp(np.abs(rng.normal(0, 0.006, rows)) + 1e-4)
        low = np.minimum(open_, close) * np.exp(-np.abs(rng.normal(0, 0.006, rows)) - 1e-4)
        volume = np.exp(rng.normal(11.0, 0.5, rows)).astype(np.int64) + 1
        if tail_shock:
            close[-40:] *= 5.0
            open_[-40:] *= 5.0
            high[-40:] *= 7.0
            low[-40:] *= 3.0
        pl.DataFrame({"date": days.astype("datetime64[D]"), "open": open_.astype(np.float32), "high": high.astype(np.float32),
                      "low": low.astype(np.float32), "close": close.astype(np.float32), "volume": volume}).write_parquet(
            directory / f"{sym}.parquet")
    return directory


def _selfcheck_args(out_root: Path, data_dir: Path, *extra: str) -> argparse.Namespace:
    argv = ["--data-dir", str(data_dir), "--out-root", str(out_root), "--oos-tail-days", "60", "--start-date", "2000-01-03",
            "--window-bars", "40", "--hidden", "16", "--latent-dim", "8", "--steps", "25", "--batch-size", "16", "--n-critic", "2",
            "--count", "12", "--min-windows", "50"] + list(extra)
    return _parse_args(argv)


def _run_selfcheck() -> None:
    import contextlib
    import io
    passed = 0

    def expect_error(fn, exc=ValueError, text: str | None = None):
        try:
            fn()
        except exc as caught:
            assert text is None or text in str(caught), (text, str(caught))
            return
        raise AssertionError(f"{exc.__name__} expected")

    quiet = contextlib.redirect_stdout(io.StringIO())

    with tempfile.TemporaryDirectory(prefix="sa_synth_") as scratch_root:
        scratch = Path(scratch_root)

        # 1. arguments, bands and bounds ---------------------------------------------------------------------------------------
        base = _selfcheck_args(scratch / "o1", scratch / "none")
        validate_args(base)
        for name, value in (("window_bars", 1), ("window_bars", 2000), ("steps", 0), ("steps", MAX_STEPS + 1), ("hidden", 0),
                            ("n_critic", 0), ("batch_size", 1), ("max_retries", -1), ("count", 0), ("oos_tail_days", 0),
                            ("seed", -1), ("gp_lambda", 0.0), ("lr", 0.0), ("stride", 0), ("bars", 41), ("bars", 0)):
            bad = _selfcheck_args(scratch / "o1", scratch / "none")
            setattr(bad, name, value)
            expect_error(lambda bad=bad: validate_args(bad))
        too_many = _selfcheck_args(scratch / "o1", scratch / "none", "--count", str(ds.MAX_COMPOSED_BATCH_SAMPLES), "--bars", "2")
        expect_error(lambda: validate_args(too_many), ValueError, "exceed the bound")
        weekend = _selfcheck_args(scratch / "o1", scratch / "none", "--start-date", "2000-01-01")     # a Saturday
        expect_error(lambda: business_dates(weekend.start_date, 3), PrerequisiteError, "business day")
        assert business_dates(datetime.date(2000, 1, 7), 3).tolist() == [np.datetime64("2000-01-07"), np.datetime64("2000-01-10"),
                                                                       np.datetime64("2000-01-11")]
        assert not (set(stream_seeds(1).values()) & set(stream_seeds(2).values())) and len(set(stream_seeds(1).values())) == len(STREAMS)
        passed += 1

        # 2. the validator, with boundary values --------------------------------------------------------------------------------
        days = business_dates(datetime.date(2000, 1, 3), 3)
        good = np.array([[10.0, 11.0, 9.0, 10.5, 100.0], [10.5, 10.5, 10.5, 10.5, 0.0], [10.5, 12.0, 10.0, 12.0, 5.0]])
        assert validate_series(good, days) == []                          # H == max(O,C), L == min(O,C), V == 0 are valid
        flat = np.array([[7.0, 7.0, 7.0, 7.0, 1.0]])
        assert validate_series(flat, days[:1]) == []

        def broken(row: int, col: int, value: float) -> List[str]:
            arr = good.copy()
            arr[row, col] = value
            return validate_series(arr, days)

        assert broken(0, 1, 10.4) == ["high_below_open_close"] and broken(0, 2, 10.6) == ["low_above_open_close"]
        assert "non_positive_price" in broken(0, 0, 0.0) and "non_positive_price" in broken(1, 3, -1.0)
        assert broken(0, 4, -1.0) == ["negative_volume"] and broken(0, 4, 1.5) == ["non_integer_volume"]
        assert broken(0, 4, MAX_EXACT_VOLUME * 4) == ["volume_out_of_range"]
        assert "non_finite" in broken(0, 3, float("nan")) and "non_finite" in broken(2, 4, float("inf"))
        assert validate_series(good, days[[0, 0, 1]]) == ["dates_not_increasing"]
        assert validate_series(good, days[::-1]) == ["dates_not_increasing"]
        saturday = np.array(["2000-01-07", "2000-01-08", "2000-01-10"], dtype="datetime64[D]")
        assert validate_series(good, saturday) == ["date_not_business_day"]
        assert validate_series(good[:2], days) == ["non_finite"]          # a shape mismatch is invalid, not an exception
        passed += 1

        # 3. the construction guarantee (G04) on extreme and random features --------------------------------------------------
        rng = np.random.default_rng(7)
        thousands = rng.normal(0.0, 3.0, size=(4000, N_FEATURES))
        thousands[:, F_UP:F_DOWN + 1] = np.abs(thousands[:, F_UP:F_DOWN + 1])
        thousands[:, F_VOL] += 9.0
        thousands[:, F_RET] *= 0.02
        thousands[:, F_GAP] *= 0.01
        for k in range(0, 4000, 20):
            built = build_ohlcv(thousands[k:k + 20], 50.0)
            assert validate_series(built, business_dates(datetime.date(2000, 1, 3), 20)) == [], k
        zero_wicks = np.zeros((5, N_FEATURES))
        zero_wicks[:, F_VOL] = 3.0
        built = build_ohlcv(zero_wicks, 12.5)                              # O == C == H == L: every boundary equal
        assert (built[:, :4] == 12.5).all() and validate_series(built, business_dates(datetime.date(2000, 1, 3), 5)) == []
        negative_wick = zero_wicks.copy()
        negative_wick[2, F_UP] = -0.1                                      # the raw construction is NOT trusted: the validator still flags it
        assert "high_below_open_close" in validate_series(build_ohlcv(negative_wick, 12.5), business_dates(datetime.date(2000, 1, 3), 5))
        wild = zero_wicks.copy()
        wild[1, F_RET] = 1e6                                               # overflow -> inf -> invalid, never a silent clip
        assert "non_finite" in validate_series(build_ohlcv(wild, 12.5), business_dates(datetime.date(2000, 1, 3), 5))
        raw = torch.randn(256, 6, N_FEATURES) * 50.0
        feats = to_features(raw, torch.zeros(N_FEATURES), torch.ones(N_FEATURES))
        assert bool((feats[..., F_UP] >= 0).all()) and bool((feats[..., F_DOWN] >= 0).all())
        passed += 1

        # 4. end to end (tiny network): files, manifest, readability --------------------------------------------------------------
        source = _source_dir(scratch, ["AAA", "BBB", "CCC"], 520, 11)
        before_rows = {p.name: p.stat().st_size for p in source.glob("*.parquet")}
        out1 = scratch / "out1"
        with quiet:
            assert run(_selfcheck_args(out1, source)) == EXIT_OK
        runs = [p for p in out1.iterdir() if p.is_dir()]
        assert len(runs) == 1 and not any(p.name.endswith(INCOMING_SUFFIX) for p in runs)
        folder = runs[0]
        manifest = json.loads((folder / MANIFEST).read_text(encoding="utf-8"))
        assert manifest["data_origin"] == DATA_ORIGIN and manifest["status"] == "ok" and manifest["run_id"] == folder.name
        assert manifest["isolation"]["oos_rows_used"] is False and manifest["generator"]["method"] == "wgan_gp"
        assert len(manifest["files"]) == 12 and sum(f["rows"] for f in manifest["files"]) == 12 * 40
        for entry in manifest["files"]:
            assert checkpoints.hash_file(folder / entry["name"]) == entry["sha256"]
            assert entry["symbol"].startswith(SYMBOL_PREFIX + manifest["config_sha256"][:CONFIG_HASH_CHARS] + "_")
            assert folder.name.endswith("_" + manifest["config_sha256"][:CONFIG_HASH_CHARS])
            assert is_synthetic_parquet(folder / entry["name"])
        assert is_synthetic_dir(folder) and not is_synthetic_dir(source) and not is_synthetic_dir(scratch / "missing")
        assert not is_synthetic_parquet(source / "AAA.parquet") and not is_synthetic_parquet(scratch / "missing.parquet")
        loaded, loaded_dates = ds.load_parquet_dir(folder, return_dates=True, allow_synthetic=True)   # readable by the shared loader (G17 A)
        assert len(loaded) == 12
        for refused in (lambda: ds.load_parquet_dir(folder), lambda: ds.load_parquet_dir(folder, symbols=[sorted(loaded)[0]]),
                        lambda: ds.materialize_filtered_dir(folder, scratch / "copied"), lambda: ds.materialize_main_dir(folder, scratch / "main", oos_tail_days=3)):
            try:
                refused()
            except ValueError as exc:
                assert "synthetic data" in str(exc)
            else:
                raise AssertionError("the observed-data loaders must refuse a synthetic folder by default (R4)")
        for key, arr in loaded.items():
            assert arr.shape == (40, 5) and validate_series(arr, loaded_dates[key]) == [], key
        assert before_rows == {p.name: p.stat().st_size for p in source.glob("*.parquet")}      # the source is untouched
        passed += 1

        # 5. reproducibility under a fixed seed, and sensitivity to the seed ---------------------------------------------------------
        def frames(out: Path) -> List[pl.DataFrame]:
            (single,) = [p for p in out.iterdir() if p.is_dir()]
            return [pl.read_parquet(p) for p in sorted(single.glob("*.parquet"))]

        out2, out3 = scratch / "out2", scratch / "out3"
        with quiet:
            assert run(_selfcheck_args(out2, source)) == EXIT_OK
            assert run(_selfcheck_args(out3, source, "--seed", "43")) == EXIT_OK
        first, same, other = frames(out1), frames(out2), frames(out3)
        assert all(a.equals(b) for a, b in zip(first, same)) and len(first) == len(same)
        assert any(not a.equals(b) for a, b in zip(first, other))
        names = lambda out: {e["symbol"] for e in json.loads(next(p for p in out.iterdir() if p.is_dir()).joinpath(MANIFEST).read_text(encoding="utf-8"))["files"]}
        assert names(out1) == names(out2) and not (names(out1) & names(out3))      # same config -> same ids; another config on the same day -> disjoint ids
        passed += 1

        # 6. the retry limit: an always-invalid generator fails the run and writes nothing (G12) -------------------------------------------
        real_build = build_ohlcv

        def always_invalid(features: np.ndarray, start_price: float) -> np.ndarray:
            out = real_build(features, start_price)
            out[:, 1] = out[:, 2] - 1.0                                   # H < L
            return out

        globals()["build_ohlcv"] = always_invalid
        out4 = scratch / "out4"
        try:
            with quiet, contextlib.redirect_stderr(io.StringIO()) as err:
                assert run(_selfcheck_args(out4, source, "--max-retries", "2")) == EXIT_FAILED
            assert "stayed invalid after 2 retries" in err.getvalue()
            tiny = TrainConfig(window_bars=40, latent_dim=8, hidden=16, n_critic=2, gp_lambda=10.0, lr=1e-4, beta1=0.5, beta2=0.9,
                               batch_size=16, steps=1)
            net = make_generator(8, 16, 40)
            seen = []
            real_validate = validate_series
            globals()["validate_series"] = lambda *a, **k: (seen.append(1), real_validate(*a, **k))[1]
            try:
                expect_error(lambda: generate_series(net, FeatureStats(np.zeros(5), np.ones(5)), tiny, count=3, bars=40,
                                                     start=datetime.date(2000, 1, 3), start_prices=np.array([10.0]),
                                                     seeds=stream_seeds(1), max_retries=2, deadline=time.monotonic() + 60), RunFailed)
            finally:
                globals()["validate_series"] = real_validate
            assert len(seen) == 3                                         # 1 attempt + 2 retries, then the run fails
        finally:
            globals()["build_ohlcv"] = real_build
        assert not out4.exists() or not any(out4.iterdir())               # nothing written, no .incoming left behind
        passed += 1

        # 7. isolation: real roots, the source folder and synthetic sources are refused --------------------------------------------------
        snapshot = {root: (sorted(p.name for p in root.iterdir()) if root.exists() else None) for root in real_roots()}
        for root in real_roots():
            with contextlib.redirect_stderr(io.StringIO()) as err:
                assert run(_selfcheck_args(root, source)) == EXIT_PREREQUISITE
                assert run(_selfcheck_args(root / "nested", source)) == EXIT_PREREQUISITE
            assert "refusing to write synthetic data under" in err.getvalue()
        assert snapshot == {root: (sorted(p.name for p in root.iterdir()) if root.exists() else None) for root in real_roots()}
        with contextlib.redirect_stderr(io.StringIO()):
            assert run(_selfcheck_args(source / "inside", source)) == EXIT_PREREQUISITE
            assert run(_selfcheck_args(scratch / "o5", folder)) == EXIT_PREREQUISITE           # a synthetic source is refused
        assert not (source / "inside").exists()
        guard_output_root(scratch / "ok", source)                          # an unrelated folder passes the guard
        passed += 1

        # 8. the out-of-sample tail is never used --------------------------------------------------------------------------------------------
        shocked = _source_dir(scratch / "shock", ["AAA", "BBB", "CCC"], 520, 11, tail_shock=True)
        out5 = scratch / "out5"
        with quiet:
            assert run(_selfcheck_args(out5, shocked)) == EXIT_OK
        (folder5,) = [p for p in out5.iterdir() if p.is_dir()]
        manifest5 = json.loads((folder5 / MANIFEST).read_text(encoding="utf-8"))
        assert manifest5["data_revision"] == manifest["data_revision"]      # identical main block -> identical identity
        assert all(a.equals(b) for a, b in zip(first, [pl.read_parquet(p) for p in sorted(folder5.glob("*.parquet"))]))
        with contextlib.redirect_stderr(io.StringIO()) as err:
            thin = _selfcheck_args(scratch / "o6", source, "--min-windows", "100000")
            assert run(thin) == EXIT_PREREQUISITE
        assert "insufficient_data" in err.getvalue() and not (scratch / "o6").exists()
        passed += 1

        # 9. J1 descriptive diagnostics: hand-computed statistics, the manifest block, reproducibility, no judgement ------------------------
        m4 = moments(np.array([1.0, 2.0, 3.0, 4.0]))
        assert abs(m4["mean"] - 2.5) < 1e-12 and abs(m4["std"] - math.sqrt(1.25)) < 1e-12 and abs(m4["skewness"]) < 1e-12
        assert abs(m4["excess_kurtosis"] - (2.5625 / 1.5625 - 3.0)) < 1e-12                              # m4 = 2.5625, var^2 = 1.5625
        assert moments(np.full(5, 2.0))["skewness"] is None and moments(np.array([1.0]))["mean"] is None
        assert abs(acf_rows(np.array([1.0, 2.0, 3.0, 4.0]), 1)[0] - 0.25) < 1e-12                        # 1.25 / 5 (hand count)
        assert np.isnan(acf_rows(np.full(6, 1.0), 1)[0]) and np.isnan(acf_rows(np.arange(4.0), 5)[0])
        assert abs(ks_statistic(np.array([1.0, 2.0, 3.0]), np.array([2.0, 3.0, 4.0])) - 1 / 3) < 1e-12
        assert ks_statistic(np.arange(5.0), np.arange(5.0)) == 0.0 and ks_statistic(np.array([0.0, 1.0]), np.array([5.0, 6.0])) == 1.0
        assert ks_statistic(np.array([]), np.array([1.0])) is None
        opposite = np.array([[1.0, -1.0, 1.0, -1.0], [-1.0, 1.0, -1.0, 1.0]])
        assert abs(mean_pairwise_distance(standardized_paths(opposite)) - 4.0) < 1e-12                   # |z - (-z)| = 2 * sqrt(4)
        assert mean_pairwise_distance(opposite[:1]) is None and standardized_paths(np.ones((2, 4))).shape[0] == 0
        assert _evenly_spaced(10, 3).tolist() == [0, 4, 9] or _evenly_spaced(10, 3).tolist() == [0, 5, 9]
        assert _evenly_spaced(2, 5).tolist() == [0, 1]
        diag = manifest["diagnostics"]
        assert diag["statement"] == DIAGNOSTICS_STATEMENT and "not a quality claim" in diag["statement"].lower()
        assert diag["generated_series"] == 12 and diag["bars_compared"] == 40 and len(diag["per_series"]) == 12 and "judged" not in json.dumps(diag)
        assert diag["generated"]["log_return"]["n"] == 12 * 39 and diag["real"]["log_return"]["n"] > 0 and 0.0 <= diag["ks_log_return"] <= 1.0
        assert set(diag["generated"]["log_return"]) >= {"mean", "std", "skewness", "excess_kurtosis", "acf_lag1", "acf_lag5"}
        assert set(diag["generated"]["log1p_volume"]["quantiles"]) == {str(q) for q in VOLUME_QUANTILES}
        assert diag["diversity"]["generated_paths"] == diag["diversity"]["paths_used"] == 12 and diag["diversity"]["ratio_to_real"] > 0.0
        curve, steps = diag["loss_curve"], manifest["config"]["training"]["steps"]
        assert len(curve) == min(LOSS_CURVE_POINTS, steps) and curve[0]["step"] == 0 and curve[-1]["step"] == steps - 1
        assert all(a["step"] < b["step"] for a, b in zip(curve, curve[1:])) and all(math.isfinite(c["critic_loss"]) for c in curve)
        assert manifest["notices"] == list(NOTICES) and "loss_curve" not in manifest["training"], "the existing manifest blocks are unchanged"
        manifest_same = json.loads(next(p for p in out2.iterdir() if p.is_dir()).joinpath(MANIFEST).read_text(encoding="utf-8"))
        assert manifest_same["diagnostics"] == diag, "the same seed must give identical diagnostics"
        manifest_other = json.loads(next(p for p in out3.iterdir() if p.is_dir()).joinpath(MANIFEST).read_text(encoding="utf-8"))
        assert manifest_other["diagnostics"] != diag
        single = quality_diagnostics([loaded[sorted(loaded)[0]]], np.zeros((3, 40, N_FEATURES), dtype=np.float32), 40, [])   # one series: no diversity
        assert single["diversity"]["ratio_to_real"] is None and len(single["per_series"]) == 1
        short = quality_diagnostics([loaded[sorted(loaded)[0]][:2]], np.zeros((3, 40, N_FEATURES), dtype=np.float32), 2, [])   # 1 return: moments undefined
        assert short["generated"]["log_return"]["mean"] is None and short["generated"]["log_return"]["acf_lag1"] is None
        passed += 1

    print(f"generate_synthetic.py selfcheck: OK ({passed} groups passed)")


def _iso_date(text: str) -> datetime.date:
    try:
        return datetime.date.fromisoformat(text)
    except ValueError as exc:
        raise argparse.ArgumentTypeError(f"expected YYYY-MM-DD, got {text!r}") from exc


def _parse_args(argv: List[str]) -> argparse.Namespace:
    d = TRAINING_DEFAULTS
    p = argparse.ArgumentParser(description="Synthetic OHLCV generator (unconditional WGAN-GP). Output is marked synthetic data.")
    p.add_argument("--selfcheck", action="store_true", help="Run the assertions and exit.")
    p.add_argument("--data-dir", type=Path, default=ds.DEFAULT_DATA_DIR, help="Observed parquet directory to learn from.")
    p.add_argument("--symbols", default=None, help="Comma list of symbols (default: the first --max-symbols files).")
    p.add_argument("--max-symbols", type=int, default=DEFAULT_MAX_SYMBOLS)
    p.add_argument("--oos-tail-days", type=int, required=True,
                   help="Calendar days of the out-of-sample tail that are NEVER used (as in the training run); must be >= 1.")
    p.add_argument("--start-date", type=_iso_date, required=True, help="First business day (Mon-Fri) of every generated series.")
    p.add_argument("--count", type=int, default=DEFAULT_COUNT, help="Number of series.")
    p.add_argument("--bars", type=int, default=None, help="Bars per series (default and maximum: --window-bars).")
    p.add_argument("--window-bars", type=int, default=d["window_bars"])
    p.add_argument("--stride", type=int, default=DEFAULT_STRIDE, help="Spacing of the training windows in bars.")
    p.add_argument("--min-windows", type=int, default=DEFAULT_MIN_WINDOWS, help="Fewer training windows -> exit 3, nothing written.")
    p.add_argument("--latent-dim", type=int, default=d["latent_dim"])
    p.add_argument("--hidden", type=int, default=d["hidden"])
    p.add_argument("--n-critic", type=int, default=d["n_critic"])
    p.add_argument("--gp-lambda", type=float, default=d["gp_lambda"])
    p.add_argument("--lr", type=float, default=d["lr"])
    p.add_argument("--batch-size", type=int, default=d["batch_size"])
    p.add_argument("--steps", type=int, default=d["steps"], help=f"Generator steps (limit {MAX_STEPS}; env {MAX_STEPS_ENV}).")
    p.add_argument("--max-retries", type=int, default=DEFAULT_MAX_RETRIES, help="Regenerations of an invalid series before the run fails.")
    p.add_argument("--seed", type=int, default=DEFAULT_SEED)
    p.add_argument("--max-seconds", type=int, default=MAX_SECONDS, help=f"Run bound; env {MAX_SECONDS_ENV}.")
    p.add_argument("--out-root", type=Path, default=OUT_DIR)
    return p.parse_args(argv)


if __name__ == "__main__":
    if "--selfcheck" in sys.argv[1:]:
        _run_selfcheck()
    else:
        _args = _parse_args(sys.argv[1:])
        try:
            validate_args(_args)
        except ValueError as _exc:
            print(f"generate_synthetic: invalid argument: {_exc}", file=sys.stderr)
            raise SystemExit(2)
        raise SystemExit(run(_args))
