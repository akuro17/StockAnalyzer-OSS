"""Real framework and ONNX acceptance for the opt-in prepared input resolver."""

from __future__ import annotations

import datetime
import dataclasses
import json
import sys
from pathlib import Path

import polars as pl

ROOT = Path(__file__).resolve().parents[3]
TRAINING = ROOT / "StockAnalyzer.Python/training"
sys.path.insert(0, str(TRAINING))
sys.path.insert(0, str(Path(__file__).resolve().parent))
import run_training as rt  # noqa: E402
import check_composed_trainers as prior  # noqa: E402


def main() -> None:
    with prior.workspace_case_dir() as root:
        rt.ARTIFACTS_DIR = root / "artifacts"
        cases = (("weekly", 7, "pytorch", "lstm", {"epochs": "1", "batch": "32"}),
                 ("weekly", 7, "lightgbm", "gbdt", {"n_estimators": "12", "early_stopping": "3"}),
                 ("monthly", 30, "tensorflow", "cnn", {"epochs": "1", "batch": "32"}))
        for timeframe, stride, framework, architecture, hyperparameters in cases:
            case = root / (timeframe + "_" + framework)
            case.mkdir()
            data, exports = prior.make_data(case)
            dates = [datetime.date(2015, 1, 1) + datetime.timedelta(days=i * stride)
                     for i in range(90)]
            for path in [*data.glob("*.parquet"), *case.glob("*_indicator.parquet")]:
                pl.read_parquet(path).with_columns(pl.Series("date", dates)).write_parquet(path)
            spec = {"schema_version": 1, "channels": [prior.PRICE, prior.INDICATOR]}
            config = {
                "symbols": ["AAA", "BBB"], "architecture": architecture,
                "window_size": prior.WINDOW, "horizon": prior.HORIZON,
                "timeframe": timeframe, "framework": framework,
                "feature_mode": "composed_features", "feature_spec": spec,
                "indicator_channel_export_paths": exports,
                "prepared_input_dir": str(data), "hyperparameters": hyperparameters,
                "n_splits": prior.SPLITS, "gap": prior.GAP,
                "run_id": timeframe + "_" + framework,
            }
            config_path = case / "job.json"
            config_path.write_text(json.dumps(config), encoding="utf-8")
            parsed = rt.JobConfig.from_json(config_path.read_text(encoding="utf-8"))
            prior.demand(rt._resolve_dataset_dir(parsed, case / "filter") == data.resolve(),
                         "prepared resolver ignored the fixed input")
            filtered = rt._resolve_dataset_dir(
                dataclasses.replace(parsed, start_date=str(dates[10]), end_date=str(dates[-10])),
                case / "filter")
            prior.demand(filtered != data and all(
                len(pl.read_parquet(path)) == 71 for path in filtered.glob("*.parquet")),
                "prepared resolver did not preserve date filtering")
            prior.demand(rt.main(["--config", str(config_path)]) == 0,
                         f"{timeframe}/{framework} training failed")
            artifact = next(rt.ARTIFACTS_DIR.glob(f"*{timeframe}_{framework}.onnx"))
            prior.run([sys.executable, str(TRAINING / "verify_model_strict.py"),
                       "--model", str(artifact), "--data-dir", str(data),
                       "--feature-spec", json.dumps(spec),
                       "--indicator-channel-export-paths", json.dumps(exports)])
            print(f"prepared {timeframe}/{framework}: PASS")


if __name__ == "__main__":
    main()
