"""Writes Tests/TrainingRequirementParity.json: the raw-bar history one symbol needs for a whole training run.

Run (from the repo root, with the training venv):
    StockAnalyzer.Python/.venv/Scripts/python.exe Tests/StockAnalyzer.Core.Tests/Assets/generate_training_requirement_parity.py

``run_training.py`` validates EVERY outer fold (0 .. n_splits-1) with ``dataset.independent_fold_data(..., inner=True)``
before it trains, and the release fold also carves a calibration / meta segment when one is requested. The value written
per case is the smallest raw bar count B such that this validation succeeds for every bar count >= B (feasibility is not
monotone in the bar count: a floor division makes it saw-tooth near the boundary).

The boundary is located with a compact index simulation of the split rules (``simulate``) and then CONFIRMED against the real
function: every count in a band around the boundary, and a coarse sample of larger counts, must agree with the simulation.
The C# ``WalkForwardDataRequirement.MinimumRawBarsForTraining`` is a third, independent implementation that must reproduce
the written values (Tests/StockAnalyzer.Core.Tests/Models/Training/WalkForwardDataRequirementTests.cs).
"""
import json
import sys
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "StockAnalyzer.Python" / "training"))
import dataset as ds  # noqa: E402

# (window, horizon, splits, gap, max_lag, calibration)
CASES = [
    (5, 1, 5, None, 0, False), (5, 5, 5, None, 0, False), (10, 3, 3, None, 0, False), (10, 3, 3, None, 0, True),
    (20, 5, 5, None, 0, False), (20, 5, 5, None, 0, True), (20, 5, 5, 0, 0, False), (20, 5, 5, 40, 0, False),
    (20, 5, 5, None, 8, False), (20, 5, 5, None, 8, True), (30, 10, 2, None, 0, False), (30, 10, 6, None, 0, False),
    (1, 1, 5, None, 0, False), (75, 5, 5, None, 0, False), (75, 5, 5, None, 0, True),
]
SCAN_FACTOR = 4          # the simulation scan runs up to SCAN_FACTOR * (splits + 1) * (window + horizon + gap + lag + margin)
SCAN_MARGIN = 20
BAND_BLOCKS = 3          # the real function is called for every count within this many blocks around the boundary
COARSE_STEPS = 12        # ... and for this many evenly spread larger counts
SERIES_SEED = 1          # the values are irrelevant to the split rules; any positive OHLCV works


def carve(length, splits, gap, depth):
    """The purged head left when a train side of ``length`` contiguous windows is split by its last existing fold; None if none."""
    block = length // (splits + 1)
    if block == 0:
        return None
    start = None
    for k in range(splits):
        first = block * (k + 1) + gap
        end = length if k == splits - 1 else block * (k + 2)
        if first < end:
            start = first
    if start is None:
        return None
    head = min(start - gap, max(0, start - depth))
    return head if head > 0 else None


def simulate(n_raw, window, horizon, splits, gap, max_lag, calibration):
    resolved = window + horizon - 1 if gap is None else gap
    samples = n_raw - (window + horizon - 1) - max_lag
    if samples <= 0:
        return False
    depth = window + horizon - 1 + max_lag
    block = samples // (splits + 1)
    if block == 0:
        return False
    for k in range(splits):
        start = block * (k + 1) + resolved
        end = samples if k == splits - 1 else block * (k + 2)
        if start >= end:
            return False
        length = min(block * (k + 1), max(0, start - depth))
        for _ in range(2 if (calibration and k == splits - 1) else 1):
            if length <= 0:
                return False
            length = carve(length, splits, resolved, depth)
            if length is None:
                return False
        if length <= 0:
            return False
    return True


def real(n_raw, window, horizon, splits, gap, max_lag, calibration):
    rng = np.random.default_rng(SERIES_SEED)
    close = 100 * np.exp(np.cumsum(rng.normal(0, 0.01, n_raw)))
    arr = np.column_stack([close, close * 1.01, close * 0.99, close, rng.integers(1000, 5000, n_raw).astype(float)])
    dates = np.datetime64("2000-01-03") + np.arange(n_raw).astype("timedelta64[D]")
    kw = dict(feature_mode="ohlcv_minmax", window=window, horizon=horizon, threshold=ds.DEFAULT_THRESHOLD, n_splits=splits,
              gap=gap, lags=[max_lag] if max_lag else None, fixed_zscore=bool(max_lag))
    try:
        for fold in range(splits):
            ds.independent_fold_data({"X": arr}, {"X": dates}, fold_index=fold, inner=True,
                                     calibration=bool(calibration and fold == splits - 1), **kw)
    except ValueError:
        return False
    return True


def minimum(window, horizon, splits, gap, max_lag, calibration):
    resolved = window + horizon - 1 if gap is None else gap
    bound = SCAN_FACTOR * (splits + 1) * (window + horizon + resolved + max_lag + SCAN_MARGIN)
    args = (window, horizon, splits, gap, max_lag, calibration)
    last_bad = 0
    for n in range(1, bound + 1):
        if not simulate(n, *args):
            last_bad = n
    assert last_bad < bound - (splits + 1) * BAND_BLOCKS, "the scan bound is too close to the boundary"
    threshold = last_bad + 1
    blocks = splits + 1
    band = range(max(1, threshold - BAND_BLOCKS * blocks), threshold + BAND_BLOCKS * blocks + 1)
    coarse = np.linspace(threshold, bound, COARSE_STEPS).astype(int).tolist()
    for n in sorted(set(band) | set(coarse)):
        # below the threshold a count may still succeed (an island of the saw-tooth); the simulation must say the same
        assert real(n, *args) == simulate(n, *args), f"real function disagrees with the simulation at n={n} for {args}"
        assert n < threshold or simulate(n, *args), f"count {n} above the threshold {threshold} fails in {args}"
    return threshold


def main():
    cases = [{"window": w, "horizon": h, "splits": s, "gap": g, "max_lag": lag, "calibration": cal,
              "minimum_raw_bars": minimum(w, h, s, g, lag, cal)} for (w, h, s, g, lag, cal) in CASES]
    out = ROOT / "Tests" / "TrainingRequirementParity.json"
    out.write_text(json.dumps({"schema": 1, "cases": cases}, indent=2) + "\n", encoding="utf-8", newline="\n")
    for case in cases:
        print(case)
    print("wrote", out)


if __name__ == "__main__":
    main()
