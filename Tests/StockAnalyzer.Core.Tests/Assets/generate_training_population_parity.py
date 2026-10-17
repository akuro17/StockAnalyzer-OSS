"""Writes Tests/TrainingPopulationParity.json: the sample figure the Training Wizard warns with, against what the trainer really counts.

Run (from the repo root, with the training venv):
    StockAnalyzer.Python/.venv/Scripts/python.exe Tests/StockAnalyzer.Core.Tests/Assets/generate_training_population_parity.py

The trainer refuses a run when the counted samples are strictly above the sample cap:
  * every feature mode: ``model_analysis.validate_population`` counts, over all symbols, the fit plus validation windows of the LAST outer fold
    (``dataset.independent_fold_data(..., fold_index=splits - 1, inner=True)``);
  * composed features or a fixed z-score (``retained``): ``dataset._checked_cumulative_samples`` counts every window ``dataset.build_dataset`` keeps.
The C# ``TrainingDataSufficiency.EstimateSamples`` is an UPPER BOUND of the first and the exact count of the second:
    windows = max(0, bars - window - horizon + 1 - max_lag)
    estimate = windows                                   (retained)
    estimate = splits * (windows // (splits + 1))        (otherwise: the outer train side of the last fold, before purging)
This generator measures ``real`` with the real functions on one aligned synthetic symbol for a grid of settings and asserts the relation; the
C# test (Tests/StockAnalyzer.Core.Tests/Services/TrainingDataSufficiencyTests.cs) requires its own estimate to equal ``estimate`` here.
"""
import json
import sys
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "StockAnalyzer.Python" / "training"))
import dataset as ds  # noqa: E402

# (window, horizon) pairs, split counts, and (retained, max_lag) modes
WINDOW_HORIZON = [(60, 5), (30, 5), (20, 3)]
SPLITS = [3, 5]
MODES = [(False, 0), (True, 0), (True, 8)]
BAR_START, BAR_STOP, BAR_STEP = 50, 3000, 53   # the grid of bar counts; infeasible counts (too few bars for the settings) are skipped
SERIES_SEED = 1                                # the values are irrelevant to the split rules; any positive OHLCV works


def series(n_raw):
    rng = np.random.default_rng(SERIES_SEED)
    close = 100 * np.exp(np.cumsum(rng.normal(0, 0.01, n_raw)))
    arr = np.column_stack([close, close * 1.01, close * 0.99, close, rng.integers(1000, 5000, n_raw).astype(float)])
    dates = np.datetime64("2000-01-03") + np.arange(n_raw).astype("timedelta64[D]")
    return arr, dates


def estimate(bars, window, horizon, splits, max_lag, retained):
    windows = max(0, bars - window - horizon + 1 - max_lag)
    return windows if retained else splits * (windows // (splits + 1))


def measure(bars, window, horizon, splits, retained, max_lag):
    arr, dates = series(bars)
    kw = dict(feature_mode="ohlcv_minmax", window=window, horizon=horizon, threshold=ds.DEFAULT_THRESHOLD, n_splits=splits,
              gap=None, lags=[max_lag] if max_lag else None, fixed_zscore=retained)
    try:
        selected = ds.independent_fold_data({"X": arr}, {"X": dates}, fold_index=splits - 1, inner=True, **kw)
    except ValueError:
        return None
    _x, _y, _anchors, fit, validation = selected["X"]
    if not retained:
        return len(fit) + len(validation)
    x, _y = ds.build_dataset(arr, kw["feature_mode"], window, horizon, ds.DEFAULT_THRESHOLD, fixed_zscore=True, lags=kw["lags"])
    return int(x.shape[0])


def main():
    rows = []
    for window, horizon in WINDOW_HORIZON:
        for splits in SPLITS:
            for retained, max_lag in MODES:
                for bars in range(BAR_START, BAR_STOP + 1, BAR_STEP):
                    real = measure(bars, window, horizon, splits, retained, max_lag)
                    if real is None:
                        continue
                    bound = estimate(bars, window, horizon, splits, max_lag, retained)
                    assert real <= bound, (bars, window, horizon, splits, retained, max_lag, real, bound)
                    if retained:
                        assert real == bound, (bars, window, horizon, splits, retained, max_lag, real, bound)
                    rows.append({"bars": bars, "window": window, "horizon": horizon, "splits": splits, "max_lag": max_lag,
                                 "retained": retained, "real": real, "estimate": bound})
    assert rows, "the grid produced no feasible row"
    slack = max((r["estimate"] - r["real"]) / r["estimate"] for r in rows if not r["retained"] and r["estimate"])
    out = ROOT / "Tests" / "TrainingPopulationParity.json"
    out.write_text(json.dumps({"schema": 1, "rows": rows}, indent=1) + "\n", encoding="utf-8", newline="\n")
    print(f"wrote {len(rows)} rows to {out}; largest relative slack of the non-retained estimate: {slack:.3f}")


if __name__ == "__main__":
    main()
