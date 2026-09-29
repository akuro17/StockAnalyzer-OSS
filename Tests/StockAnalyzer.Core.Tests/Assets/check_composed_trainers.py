"""End-to-end composed LightGBM/TensorFlow cases launched by xUnit."""

from __future__ import annotations

import datetime
import json
import math
import os
import shutil
import subprocess
import sys
import uuid
from argparse import Namespace
from contextlib import contextmanager
from pathlib import Path

import numpy as np
import onnx
import polars as pl

ROOT = Path(__file__).resolve().parents[3]
TRAINING = ROOT / "StockAnalyzer.Python/training"
sys.path.insert(0, str(TRAINING))
import composed_training as ct  # noqa: E402
import dataset as ds  # noqa: E402

WINDOW = 6
HORIZON = 2
SPLITS = 2
GAP = 0
OPSET = 17
THRESHOLD = ds.DEFAULT_THRESHOLD
PRICE = {"kind": "price", "price": "close", "normalization": "window_min_max"}
INDICATOR = {"kind": "indicator", "indicator": "RSI", "normalization": "none"}


@contextmanager
def workspace_case_dir():
    directory = ROOT / f"sa_composed_trainers_{uuid.uuid4().hex}"
    directory.mkdir()
    try:
        yield directory
    finally:
        if directory.resolve().parent != ROOT.resolve() or not directory.name.startswith(
            "sa_composed_trainers_"
        ):
            raise RuntimeError("refusing to remove test files outside the workspace")
        shutil.rmtree(directory)


def demand(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


def run(command: list[str], expected: int = 0) -> subprocess.CompletedProcess:
    env = os.environ.copy()
    env.update({"OMP_NUM_THREADS": "1", "TF_NUM_INTRAOP_THREADS": "1",
                "TF_NUM_INTEROP_THREADS": "1", "TF_CPP_MIN_LOG_LEVEL": "3"})
    result = subprocess.run(command, cwd=ROOT, env=env, capture_output=True,
                            text=True, timeout=240)
    demand(result.returncode == expected,
           f"exit={result.returncode} expected={expected}: {' '.join(command)}\n"
           f"stdout={result.stdout}\nstderr={result.stderr}")
    return result


def make_data(root: Path) -> tuple[Path, dict[str, str]]:
    data = root / "data"
    data.mkdir()
    exports = {}
    for symbol, offset in (("AAA", 0.0), ("BBB", 1.5)):
        dates = [datetime.date(2024, 1, 1) + datetime.timedelta(days=i) for i in range(90)]
        cycle = (100, 100, 100, 102, 104, 106, 106, 106, 104, 102, 100)
        closes = [cycle[i % len(cycle)] + offset for i in range(90)]
        pl.DataFrame({
            "date": dates,
            "open": closes,
            "high": [value + 1 for value in closes],
            "low": [value - 1 for value in closes],
            "close": closes,
            "volume": [1000 + i for i in range(90)],
        }).write_parquet(data / f"{symbol}.parquet")
        export = root / f"{symbol}_indicator.parquet"
        pl.DataFrame({
            "date": dates,
            "channel_0": [None if i < 3 else float(math.cos(i / 4)) for i in range(90)],
            "channel_1": [None if i < 3 else float(math.cos(i / 4)) for i in range(90)],
        }).write_parquet(export)
        exports[symbol] = str(export)
    return data, exports


def check_aggregate_sample_cap(root: Path) -> None:
    """Exercise the composed trainer's real split route before fitting."""
    data = root / "sample_cap"
    data.mkdir()
    dates = [datetime.date(2024, 1, 1) + datetime.timedelta(days=i) for i in range(82)]
    for symbol in ("CAP_A", "CAP_B"):
        closes = [100.0 + i for i in range(82)]
        pl.DataFrame({
            "date": dates, "open": closes, "high": closes,
            "low": closes, "close": closes, "volume": [1000] * 82,
        }).write_parquet(data / f"{symbol}.parquet")
    spec = json.dumps({"channels": [{"kind": "price", "price": "close"}]})
    args = Namespace(data_dir=data, feature_mode=ds.COMPOSED_FEATURES_MODE,
                     feature_spec=spec, indicator_channel_export_paths=None,
                     window=2, horizon=1, threshold=THRESHOLD,
                     wf_splits=2, gap=None, max_symbols=None, opset=OPSET,
                     no_verify=False)
    ct.validate_flags(args)
    previous = ds.MAX_COMPOSED_BATCH_SAMPLES
    try:
        # Each symbol has 80 valid candidates. Last-fold train + validation retains 78.
        ds.MAX_COMPOSED_BATCH_SAMPLES = 156
        prepared = ct.prepare(args)
        retained = len(prepared.x_train) + len(prepared.x_validation)
        demand(retained == 156, f"unexpected retained sample count: {retained}")
        ds.MAX_COMPOSED_BATCH_SAMPLES = retained - 1
        try:
            ct.prepare(args)
        except ValueError as exc:
            demand(f"cumulative sample count {retained}" in str(exc),
                   f"aggregate rejection lacked observed count: {exc}")
        else:
            raise RuntimeError("one-over cumulative sample limit was accepted")
        ds.MAX_COMPOSED_BATCH_SAMPLES = 100
        try:
            ct.prepare(args)
        except ValueError as exc:
            demand("cumulative sample count 156" in str(exc),
                   f"the reviewed multi-symbol counterexample was not rejected: {exc}")
        else:
            raise RuntimeError("multi-symbol cap bypass reached fitting")
    finally:
        ds.MAX_COMPOSED_BATCH_SAMPLES = previous

    child_check = """
import sys
from argparse import Namespace
sys.path.insert(0, sys.argv[1])
import composed_training as ct
import dataset as ds

args = Namespace(data_dir=sys.argv[2], feature_mode=ds.COMPOSED_FEATURES_MODE,
                 feature_spec=sys.argv[3], indicator_channel_export_paths=None,
                 window=2, horizon=1, threshold=ds.DEFAULT_THRESHOLD,
                 wf_splits=2, gap=None, max_symbols=None, opset=17,
                 no_verify=False)
ct.validate_flags(args)
prepared = ct.prepare(args)
print(len(prepared.x_train) + len(prepared.x_validation))
"""
    child_env = os.environ.copy()
    child_env.pop("SA_MAX_COMPOSED_BATCH_SAMPLES", None)
    child_args = [sys.executable, "-c", child_check, str(TRAINING), str(data), spec]
    inherited = subprocess.run(child_args, cwd=root, env=child_env,
                               capture_output=True, text=True, timeout=60)
    demand(inherited.returncode == 0 and inherited.stdout.strip() == "156",
           f"inherited sample limit failed: {inherited.stderr}")
    child_env["SA_MAX_COMPOSED_BATCH_SAMPLES"] = "100"
    explicit = subprocess.run(child_args, cwd=root, env=child_env,
                              capture_output=True, text=True, timeout=60)
    demand(explicit.returncode != 0 and "cumulative sample count 156" in explicit.stderr,
           f"explicit child sample limit failed: {explicit.stderr}")

    # Warmup removes actual candidate windows before the final fold is pooled.
    symbols, symbol_dates = ds.load_parquet_dir(data, return_dates=True)
    warmup = root / "warmup.parquet"
    pl.DataFrame({"date": dates, "channel_0":
                  [None if i < 5 else float(i) for i in range(82)]}).write_parquet(warmup)
    indicator_spec = {"channels": [{"kind": "indicator", "indicator": "RSI"}]}
    exports = {symbol: str(warmup) for symbol in symbols}
    folds = [ds._last_fold(arr, ds.COMPOSED_FEATURES_MODE, 2, 1, THRESHOLD,
                           2, ds.resolve_purge_gap(2, 1, None), indicator_spec,
                           dates=symbol_dates[symbol], indicator_export_path=exports[symbol])
             for symbol, arr in symbols.items()]
    demand(all(fold is not None for fold in folds), "warmup fixture lost a fold")
    expected = sum(len(fold[3]) + len(fold[4]) for fold in folds)
    demand(expected < 156, "warmup did not reduce the retained sample count")
    try:
        ds.MAX_COMPOSED_BATCH_SAMPLES = expected
        split = ds.split_symbols_chronological(
            symbols, ds.COMPOSED_FEATURES_MODE, window=2, horizon=1,
            n_splits=2, feature_spec=indicator_spec,
            dates=symbol_dates, indicator_export_paths=exports)
        demand(len(split[0]) + len(split[2]) == expected,
               "warmup aggregate limit counted discarded candidates")
    finally:
        ds.MAX_COMPOSED_BATCH_SAMPLES = previous


def check_dataset_parity(data: Path, spec: str, exports: dict[str, str] | None) -> ct.PreparedComposedData:
    args = Namespace(data_dir=data, feature_mode=ds.COMPOSED_FEATURES_MODE,
                     feature_spec=spec, indicator_channel_export_paths=(
                         json.dumps(exports) if exports is not None else None),
                     window=WINDOW, horizon=HORIZON, threshold=THRESHOLD,
                     wf_splits=SPLITS, gap=GAP, max_symbols=None, opset=OPSET,
                     no_verify=False)
    ct.validate_flags(args)
    prepared = ct.prepare(args)
    split = ds.split_symbols_chronological(
        prepared.symbols, args.feature_mode, window=WINDOW, horizon=HORIZON,
        threshold=THRESHOLD, n_splits=SPLITS, gap=GAP,
        feature_spec=prepared.feature_spec, target_type="classification",
        dates=prepared.dates, indicator_export_paths=exports)
    for actual, reference in zip((prepared.x_train, prepared.y_train,
                                  prepared.x_validation, prepared.y_validation), split):
        demand(np.array_equal(actual, reference), "composed trainer split differs from PyTorch dataset path")
    ranges = ds.train_val_date_ranges(
        prepared.symbols, prepared.dates, feature_mode=args.feature_mode,
        window=WINDOW, horizon=HORIZON, threshold=THRESHOLD,
        n_splits=SPLITS, gap=GAP, feature_spec=prepared.feature_spec,
        target_type="classification", indicator_export_paths=exports)
    demand(prepared.date_ranges == ranges, "composed date ranges differ from dataset/PyTorch path")
    demand(prepared.channels == len(prepared.feature_spec["channels"]),
           "prepared C differs from FeatureSpec")
    for symbol, source in prepared.symbols.items():
        fold = ds._last_fold(
            source, args.feature_mode, WINDOW, HORIZON, THRESHOLD, SPLITS,
            ds.resolve_purge_gap(WINDOW, HORIZON, GAP), prepared.feature_spec,
            "classification", dates=prepared.dates[symbol],
            indicator_export_path=exports.get(symbol) if exports is not None else None)
        demand(fold is not None, f"{symbol}: expected a chronological last fold")
        x, _y, anchors, train_indices, validation_indices = fold
        demand(np.all(np.diff(anchors) > 0), f"{symbol}: sample anchors are not chronological")
        demand(int(anchors[train_indices].max()) < int(anchors[validation_indices].min()),
               f"{symbol}: validation anchors overlap training anchors")
        demand(x.shape[2] == prepared.channels, f"{symbol}: anchor tensor C differs")
    demand(np.array_equal(prepared.x_train[0].reshape(-1),
                          np.concatenate([prepared.x_train[0, bar, :]
                                          for bar in range(WINDOW)])),
           "bar-major flattening differs from the training tensor order")
    return prepared


def check_artifact_protection(root: Path) -> None:
    model = root / "protected.onnx"
    sidecar = root / "protected.onnx.metrics.json"
    model.write_bytes(b"previous-good-model")
    sidecar.write_bytes(b"previous-good-metrics")

    def conversion_failure(staged: Path) -> None:
        staged.write_bytes(b"incomplete-model")
        raise ValueError("conversion failed")

    for name, export, verify in (
        ("conversion", conversion_failure, lambda _staged: None),
        ("verification", lambda staged: staged.write_bytes(b"new-model"),
         lambda _staged: (_ for _ in ()).throw(ValueError("validation failed"))),
    ):
        try:
            ct.publish_verified(model, {"n_samples": 1}, export, verify)
        except ValueError:
            pass
        else:
            raise RuntimeError(f"staged {name} failure was not reported")
        demand(model.read_bytes() == b"previous-good-model" and
               sidecar.read_bytes() == b"previous-good-metrics",
               f"failed staged {name} replaced an existing model or metrics")
        demand(not list(root.glob(".protected_stage_*")),
               f"staging directory remained after {name} failure")


def main() -> None:
    with workspace_case_dir() as root:
        check_aggregate_sample_cap(root)
        data, exports = make_data(root)
        check_artifact_protection(root)
        python = sys.executable
        for name, channels in (("price", [PRICE]), ("indicator", [INDICATOR]),
                               ("mixed", [PRICE, INDICATOR])):
            spec = json.dumps({"schema_version": 1, "channels": channels})
            selected_exports = exports if name != "price" else None
            prepared = check_dataset_parity(data, spec, selected_exports)
            flags = ["--feature-mode", ds.COMPOSED_FEATURES_MODE,
                     "--feature-spec", spec, "--data-dir", str(data),
                     "--window", str(WINDOW), "--horizon", str(HORIZON),
                     "--wf-splits", str(SPLITS), "--gap", str(GAP)]
            if selected_exports is not None:
                flags += ["--indicator-channel-export-paths", json.dumps(selected_exports)]
            for framework in ("lightgbm", "tensorflow"):
                output = root / f"{framework}_{name}.onnx"
                train = [python, str(TRAINING / f"train_{framework}.py"), *flags,
                         "--out", str(output)]
                train += (["--n-estimators", "12", "--early-stopping", "3"]
                          if framework == "lightgbm" else
                          ["--arch", "cnn", "--epochs", "1", "--batch", "32"])
                result = run(train)
                demand("composed ONNX verify: PASS" in result.stdout,
                       f"{framework}/{name} did not run native/ORT verification")
                demand(output.is_file() and output.with_name(output.name + ".metrics.json").is_file(),
                       f"{framework}/{name} did not publish model and metrics")
                metadata = {item.key: item.value for item in onnx.load(output).metadata_props}
                demand(metadata.get("feature_spec") == spec and
                       metadata.get("channels") == str(prepared.channels) and
                       all(metadata.get(key) == value for key, value in prepared.date_ranges.items()),
                       f"{framework}/{name} metadata differs from prepared split/spec")
                diagnosis = run([python, str(TRAINING / "verify_model_strict.py"),
                                 "--model", str(output), "--data-dir", str(data),
                                 "--feature-spec", spec, *(["--indicator-channel-export-paths",
                                                            json.dumps(selected_exports)]
                                                           if selected_exports is not None else [])])
                demand("result: PASS" in diagnosis.stdout,
                       f"{framework}/{name} strict diagnosis did not pass")
                print(f"{framework}/{name}: PASS")

        for framework in ("lightgbm", "tensorflow"):
            output = root / f"{framework}_fixed.onnx"
            fixed_flags = ["--feature-mode", "ohlcv_minmax", "--data-dir", str(data),
                           "--window", str(WINDOW), "--horizon", str(HORIZON),
                           "--wf-splits", str(SPLITS), "--gap", str(GAP),
                           "--out", str(output)]
            fixed_flags += (["--n-estimators", "12", "--early-stopping", "3"]
                            if framework == "lightgbm" else
                            ["--arch", "cnn", "--epochs", "1", "--batch", "32"])
            run([python, str(TRAINING / f"train_{framework}.py"), *fixed_flags])
            metadata = {item.key: item.value for item in onnx.load(output).metadata_props}
            demand("feature_spec" not in metadata,
                   f"{framework}: fixed model gained composed-only metadata")
            run([python, str(TRAINING / "verify_model_strict.py"),
                 "--model", str(output), "--data-dir", str(data)])
            print(f"{framework}/fixed: PASS")

        fixed_cli_code = (
            "import sys; sys.path.insert(0, sys.argv[1]); "
            "import dataset as ds; module=__import__(sys.argv[2]); "
            "parsed=[module._parse_args(['--feature-mode', mode]) for mode in ds.FEATURE_MODES]; "
            "sys.exit(0 if all(args.feature_mode == mode and args.feature_spec is None "
            "for args, mode in zip(parsed, ds.FEATURE_MODES)) else 1)"
        )
        for framework in ("lightgbm", "tensorflow"):
            run([python, "-c", fixed_cli_code, str(TRAINING), f"train_{framework}"])
            for invalid_flags in (
                ["--feature-mode", "not_a_mode"],
                ["--feature-mode", "ohlcv_minmax", "--feature-spec", "{}"],
                ["--feature-mode", "ohlcv_minmax", "--indicator-channel-export-paths", "{}"],
                ["--feature-mode", ds.COMPOSED_FEATURES_MODE, "--no-verify",
                 "--feature-spec", json.dumps({"channels": [PRICE]})],
            ):
                run([python, str(TRAINING / f"train_{framework}.py"), *invalid_flags],
                    expected=2)

        bad = run([python, str(TRAINING / "train_lightgbm.py"),
                   "--feature-mode", ds.COMPOSED_FEATURES_MODE,
                   "--feature-spec", json.dumps({"channels": [INDICATOR]}),
                   "--data-dir", str(data), "--window", str(WINDOW),
                   "--horizon", str(HORIZON), "--wf-splits", str(SPLITS)], expected=2)
        demand("Indicator channels require" in bad.stderr, "missing export was not rejected")
        print("composed trainers end-to-end cases: PASS")


if __name__ == "__main__":
    main()
