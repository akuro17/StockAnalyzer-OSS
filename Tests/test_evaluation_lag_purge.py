"""Regression checks for independent fold information intervals and evidence IDs."""

import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "StockAnalyzer.Python" / "training"))
import dataset  # noqa: E402
import run_training  # noqa: E402


def _observations(count=200):
    rows = np.arange(count, dtype=np.float64)
    price = 100.0 + rows
    candles = np.column_stack((price, price + 1, price - 1, price, 1000 + rows))
    first = np.datetime64("2020-01-01") + rows.astype("timedelta64[D]")
    second = first + (rows.astype(np.int64) // 10).astype("timedelta64[D]")
    return {"A": candles, "B": candles.copy()}, {"A": first, "B": second}


def _selected(symbols, dates, *, inner=False, lags=(5,)):
    return dataset.independent_fold_data(
        symbols, dates, feature_mode="ohlcv_minmax", window=2, horizon=1,
        threshold=dataset.DEFAULT_THRESHOLD, n_splits=3, gap=0, fold_index=0,
        fixed_zscore=bool(lags), lags=list(lags), inner=inner,
    )


def test_lagged_intervals_are_disjoint_across_symbols_and_inner_fold():
    symbols, dates = _observations()
    for inner in (False, True):
        selected = _selected(symbols, dates, inner=inner)
        earliest = min(
            dates[sym][anchors[test[0]] - 2 + 1 - 5]
            for sym, (_x, _y, anchors, _train, test) in selected.items()
        )
        for sym, (_x, _y, anchors, train, test) in selected.items():
            assert len(train) and len(test)
            assert np.all(dates[sym][anchors[train] + 1] < earliest)

    # An inclusive boundary touch is a genuine overlap and must be removed.
    x, _y, anchors = dataset.build_dataset(
        symbols["A"], "ohlcv_minmax", 2, 1, return_anchor_rows=True,
        fixed_zscore=True, lags=[5],
    )
    _fold, candidates, test = dataset.walk_forward_split_indexed(len(x), 3, 0)[0]
    first_feature = dates["A"][anchors[test[0]] - 1 - 5]
    assert np.any(dates["A"][anchors[candidates] + 1] == first_feature)
    assert not np.any(dates["A"][anchors[selected["A"][3]] + 1] == first_feature)

    true_high = {"channels": [{"kind": "price", "price": "true_high", "normalization": "none"}]}
    composed = dataset.independent_fold_data(
        symbols, dates, feature_mode=dataset.COMPOSED_FEATURES_MODE,
        window=2, horizon=1, threshold=dataset.DEFAULT_THRESHOLD,
        n_splits=3, gap=0, fold_index=0, fixed_zscore=True,
        feature_spec=true_high,
    )
    earliest_composed = min(dates[sym][max(0, anchors[test[0]] - 2)]
                            for sym, (_x, _y, anchors, _train, test) in composed.items())
    for sym, (_x, _y, anchors, train, _test) in composed.items():
        assert np.all(dates[sym][anchors[train] + 1] < earliest_composed)

    # Omitted lags retain the ordinary window start boundary.
    selected = _selected(symbols, dates, lags=())
    earliest = min(dates[sym][anchors[test[0]] - 1]
                   for sym, (_x, _y, anchors, _train, test) in selected.items())
    for sym, (_x, _y, anchors, train, _test) in selected.items():
        assert np.all(dates[sym][anchors[train] + 1] < earliest)


def test_evaluation_revision_tracks_evidence_not_clip_or_feature_settings():
    symbols, dates = _observations()
    base = {
        "symbols": ["A", "B"], "architecture": "lstm", "window_size": 2,
        "horizon": 1, "n_splits": 3, "gap": 0, "fixed_zscore": True,
        "lags": [5], "clip_sigma": 3.0,
    }

    def revision(settings, data=symbols):
        cfg = run_training.JobConfig.from_json(json.dumps(settings))
        return run_training._evaluation_revision(cfg, data, dates, fold_index=0)

    original = revision(base)
    assert len(original) == 64
    assert revision({**base, "clip_sigma": 4.0}) == original
    assert revision({**base, "lags": [5, 1]}) == original
    changed = {**symbols, "A": symbols["A"].copy()}
    changed["A"][55, dataset.CLOSE] += 1.0
    assert revision(base, changed) != original
    assert revision({**base, "horizon": 2}) != original


if __name__ == "__main__":
    test_lagged_intervals_are_disjoint_across_symbols_and_inner_fold()
    test_evaluation_revision_tracks_evidence_not_clip_or_feature_settings()
    print("evaluation lag purge checks passed")
