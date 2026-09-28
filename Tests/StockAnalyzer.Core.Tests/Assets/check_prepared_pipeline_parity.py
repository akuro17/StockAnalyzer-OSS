"""Inspect pre-fit tensors from the real C# prepared-source and indicator exports."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "StockAnalyzer.Python" / "training"))

import composed_training as composed  # noqa: E402
import dataset as ds  # noqa: E402
import feature_spec_strict  # noqa: E402
import run_training as rt  # noqa: E402


def _nested(array: np.ndarray) -> list:
    return array.tolist()


def inspect(config_path: Path, report_path: Path, threshold: float) -> None:
    cfg = rt.JobConfig.from_json(config_path.read_text(encoding="utf-8"))
    scratch = report_path.parent / "python_scratch"
    source_dir = rt._resolve_dataset_dir(cfg, scratch)
    if source_dir.resolve() != Path(cfg.prepared_input_dir).resolve():
        raise AssertionError("prepared source resolver did not use the C# dataset")
    full_symbols, full_dates = ds.load_parquet_dir(source_dir, return_dates=True)
    training_dir = rt._resolve_training_dir(cfg, source_dir, scratch)
    symbols, dates = ds.load_parquet_dir(training_dir, return_dates=True)
    spec, _ = feature_spec_strict.parse_feature_spec(json.dumps(cfg.feature_spec))
    args = argparse.Namespace(
        data_dir=training_dir,
        max_symbols=None,
        feature_mode=cfg.feature_mode,
        composed_spec=spec,
        composed_export_paths=cfg.indicator_channel_export_paths,
        window=cfg.window_size,
        horizon=cfg.horizon,
        threshold=threshold,
        wf_splits=cfg.n_splits,
        gap=cfg.gap,
    )
    prepared = composed.prepare(args)
    symbol_reports: dict[str, dict] = {}
    small_train: list[np.ndarray] = []
    small_validation: list[np.ndarray] = []
    for symbol in prepared.symbols:
        x, y, anchors = ds.build_dataset(
            symbols[symbol], cfg.feature_mode, cfg.window_size, cfg.horizon,
            threshold, feature_spec=spec, target_type=cfg.target_type,
            return_anchor_rows=True, dates=dates[symbol],
            indicator_export_path=cfg.indicator_channel_export_paths[symbol],
        )
        regression_x, regression_y, regression_anchors = ds.build_dataset(
            symbols[symbol], cfg.feature_mode, cfg.window_size, cfg.horizon,
            threshold, feature_spec=spec, target_type="regression",
            return_anchor_rows=True, dates=dates[symbol],
            indicator_export_path=cfg.indicator_channel_export_paths[symbol],
        )
        if not np.array_equal(x, regression_x) or not np.array_equal(anchors, regression_anchors):
            raise AssertionError(f"{symbol}: positive-price fixture changed features for regression")
        default_folds = ds.walk_forward_split(
            len(x), cfg.n_splits,
            ds.resolve_purge_gap(cfg.window_size, cfg.horizon, cfg.gap),
        )
        small_folds = ds.walk_forward_split(len(x), cfg.n_splits, gap=0)
        if not default_folds or not small_folds:
            raise AssertionError(f"{symbol}: fixture has no usable split")
        train_idx, validation_idx = default_folds[-1]
        small_train_idx, small_validation_idx = small_folds[-1]
        small_train.append(x[small_train_idx])
        small_validation.append(x[small_validation_idx])
        symbol_reports[symbol] = {
            "full_dates": [str(d) for d in full_dates[symbol]],
            "training_dates": [str(d) for d in dates[symbol]],
            "projection": _nested(symbols[symbol]),
            "anchors": _nested(anchors),
            "labels": _nested(y),
            "regression_labels": _nested(regression_y.reshape(-1)),
            "tensors": _nested(x),
            "train_indices": _nested(train_idx),
            "validation_indices": _nested(validation_idx),
            "small_train_indices": _nested(small_train_idx),
            "small_validation_indices": _nested(small_validation_idx),
        }

    small_args = argparse.Namespace(**{**vars(args), "gap": 0})
    small = composed.prepare(small_args)
    if not np.array_equal(small.x_train, np.concatenate(small_train)) or not np.array_equal(
        small.x_validation, np.concatenate(small_validation)
    ):
        raise AssertionError("explicit smaller gap was not applied by composed.prepare")

    report = {
        "symbol_order": list(prepared.symbols),
        "symbols": symbol_reports,
        "fit_train": _nested(prepared.x_train),
        "fit_validation": _nested(prepared.x_validation),
        "fit_train_labels": _nested(prepared.y_train),
        "fit_validation_labels": _nested(prepared.y_validation),
        "small_train_count": int(len(small.x_train)),
        "small_validation_count": int(len(small.x_validation)),
        "date_ranges": prepared.date_ranges,
        "full_row_count": {symbol: int(len(rows)) for symbol, rows in full_symbols.items()},
    }
    report_path.write_text(json.dumps(report, allow_nan=False), encoding="utf-8")
    print("prepared pipeline parity report: PASS")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("config", type=Path)
    parser.add_argument("report", type=Path)
    parser.add_argument("threshold", type=float)
    flags = parser.parse_args()
    inspect(flags.config, flags.report, flags.threshold)
