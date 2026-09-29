"""Integration cases launched by StrictOnnxVerifierTests with the training Python."""

from __future__ import annotations

import datetime
import json
import os
import shutil
import subprocess
import sys
import uuid
from contextlib import contextmanager
from pathlib import Path

import numpy as np
import onnx
import polars as pl
from onnx import numpy_helper

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
TRAINING = ROOT / "StockAnalyzer.Python/training"
sys.path.insert(0, str(TRAINING))
import onnx_meta  # noqa: E402
import feature_spec_strict  # noqa: E402
from generate_onnx_fixtures import _build, _build_regression  # noqa: E402

WINDOW = 10
HORIZON = 5
SPEC = {"channels": [
    {"kind": "price", "price": "close", "normalization": "none"},
    {"kind": "indicator", "indicator": "RSI", "normalization": "none"},
]}


def check(result: subprocess.CompletedProcess, code: int, case: str) -> None:
    if result.returncode != code:
        raise RuntimeError(f"{case}: expected exit {code}, got {result.returncode}\n"
                           f"stdout={result.stdout}\nstderr={result.stderr}")


@contextmanager
def test_directory():
    # tempfile.mkdtemp creates mode-0700 folders which the Windows sandbox cannot
    # enter. A normal workspace folder inherits the expected writable ACL.
    path = ROOT / f"sa_strict_onnx_{uuid.uuid4().hex}"
    path.mkdir()
    try:
        yield path
    finally:
        if path.resolve().parent != ROOT.resolve() or not path.name.startswith("sa_strict_onnx_"):
            raise RuntimeError("refusing to remove a directory outside the test workspace")
        shutil.rmtree(path)


def main() -> None:
    with test_directory() as root:
        expected_names = set(json.loads(os.environ["SA_EXPECTED_INDICATOR_NAMES"]))
        canonical = ROOT / "StockAnalyzer.Core/Models/IndicatorType.cs"
        assert feature_spec_strict._indicator_names() == expected_names
        assert feature_spec_strict._read_indicator_names(canonical) == expected_names
        # The published layout has no sibling Core source tree or source working directory.
        isolated = root / "published/StockAnalyzer.Python/training"
        contracts = isolated / "contracts"
        contracts.mkdir(parents=True)
        for name in ("feature_spec_strict.py", "dataset.py"):
            shutil.copy2(TRAINING / name, isolated / name)
        shutil.copy2(canonical, contracts / "IndicatorType.cs")
        portable_check = """
import json
import os
import feature_spec_strict as fs

expected = json.loads(os.environ["SA_EXPECTED_INDICATOR_NAMES"])
assert fs._indicator_names() == set(expected)
spec = {"channels": [{"kind": "indicator", "indicator": expected[-1]}]}
assert fs.parse_feature_spec(json.dumps(spec))[0] == spec
spec["channels"][0]["indicator"] = "NotAnIndicator"
try:
    fs.parse_feature_spec(json.dumps(spec))
except ValueError:
    pass
else:
    raise AssertionError("unknown indicator accepted")
"""
        portable_env = os.environ.copy()
        portable_env["PYTHONPATH"] = str(isolated)
        portable = subprocess.run([sys.executable, "-c", portable_check], cwd=root,
                                  env=portable_env, capture_output=True, text=True, timeout=60)
        check(portable, 0, "published indicator contract")

        data = root / "data"
        data.mkdir()
        dates = [datetime.date(2024, 1, 1) + datetime.timedelta(days=i) for i in range(30)]
        frame = pl.DataFrame({
            "date": dates,
            "open": [100.0 + i for i in range(30)],
            "high": [102.0 + i for i in range(30)],
            "low": [99.0 + i for i in range(30)],
            "close": [101.0 + i for i in range(30)],
            "volume": [1000 + i for i in range(30)],
        })
        frame.write_parquet(data / "TEST.parquet")
        export = root / "indicator.parquet"
        pl.DataFrame({"date": dates, "channel_1": [float(i) for i in range(30)]}).write_parquet(export)
        spec_json = json.dumps(SPEC)
        export_json = json.dumps({"TEST": str(export)})

        def model(name: str, mode: str, channels: int, target: str = "classification",
                  spec: str | None = None) -> Path:
            proto = _build_regression(channels) if target == "regression" else _build(3, channels)
            meta = onnx_meta.build_contract(
                feature_mode=mode, window_size=WINDOW, channels=channels,
                horizon=HORIZON, threshold=0.005, wf_splits=5, seed=42,
                producer="check_verify_model_strict.py", target_type=target,
                feature_spec_json=spec,
            )
            onnx_meta.apply(proto, meta)
            path = root / f"{name}.onnx"
            onnx.save(proto, path)
            return path

        def run(path: Path, *args: str, optimized: bool = False,
                data_dir: Path = data) -> subprocess.CompletedProcess:
            cmd = [sys.executable]
            if optimized:
                cmd.append("-O")
            cmd += [str(TRAINING / "verify_model_strict.py"), "--model", str(path),
                    "--data-dir", str(data_dir), *args]
            return subprocess.run(cmd, capture_output=True, text=True, timeout=60)

        fixed1 = model("fixed1", "log_return", 1)
        fixed5 = model("fixed5", "ohlcv_minmax", 5)
        regression = model("regression", "ohlcv_minmax", 5, "regression")
        composed = model("composed", "composed_features", 2, spec=spec_json)
        indicator_only_spec = json.dumps({"channels": [SPEC["channels"][1]]})
        indicator_only_export = root / "indicator_only.parquet"
        pl.DataFrame({"date": dates, "channel_0": [float(i) for i in range(30)]}).write_parquet(
            indicator_only_export)
        indicator_only = model("indicator_only", "composed_features", 1,
                               spec=indicator_only_spec)
        price5_spec = json.dumps({"channels": [
            {"kind": "price", "price": price, "normalization": "none"}
            for price in ("open", "high", "low", "close", "typical")
        ]})
        composed5 = model("composed5", "composed_features", 5, spec=price5_spec)
        fixed_batch = onnx.load(str(fixed5))
        fixed_batch.graph.input[0].type.tensor_type.shape.dim[0].ClearField("dim_param")
        fixed_batch.graph.input[0].type.tensor_type.shape.dim[0].dim_value = 1
        fixed_batch.graph.output[0].type.tensor_type.shape.dim[0].ClearField("dim_param")
        fixed_batch.graph.output[0].type.tensor_type.shape.dim[0].dim_value = 1
        fixed_batch_path = root / "fixed_batch.onnx"
        onnx.save(fixed_batch, fixed_batch_path)
        for name, path, flags in (
            ("fixed C1", fixed1, ()),
            ("fixed C5", fixed5, ()),
            ("regression", regression, ()),
            ("composed C2", composed,
             ("--feature-spec", json.dumps(SPEC, indent=2),
              "--indicator-channel-export-paths", export_json)),
            ("composed Indicator-only C1", indicator_only,
             ("--feature-spec", indicator_only_spec, "--indicator-channel-export-paths",
              json.dumps({"TEST": str(indicator_only_export)}))),
            ("composed Price-only C5", composed5, ("--feature-spec", price5_spec)),
        ):
            result = run(path, *flags)
            check(result, 0, name)
            if "result: PASS" not in result.stdout or "batch equivalence: PASS" not in result.stdout:
                raise RuntimeError(f"{name}: missing successful diagnostic stages: {result.stdout}")
        fixed_result = run(fixed_batch_path)
        check(fixed_result, 0, "fixed batch=1")
        if "batch equivalence: N/A" not in fixed_result.stdout:
            raise RuntimeError("fixed batch=1 must report equivalence as N/A")

        failures = (
            ("same width different spec", composed,
             ("--feature-spec", json.dumps({"channels": [SPEC["channels"][0],
                                             {**SPEC["channels"][1], "indicator": "SMA"}]}),
              "--indicator-channel-export-paths", export_json)),
            ("channel order", composed,
             ("--feature-spec", json.dumps({"channels": list(reversed(SPEC["channels"]))}),
              "--indicator-channel-export-paths", export_json)),
            ("key order", composed,
             ("--feature-spec", json.dumps({"channels": [
                 {"price": "close", "kind": "price", "normalization": "none"}, SPEC["channels"][1]]}),
              "--indicator-channel-export-paths", export_json)),
            ("escape spelling", composed,
             ("--feature-spec", spec_json.replace('"RSI"', '"\\u0052SI"'),
              "--indicator-channel-export-paths", export_json)),
            ("missing export", composed, ("--feature-spec", spec_json)),
            ("invalid indicator enum", composed,
             ("--feature-spec", json.dumps({"channels": [SPEC["channels"][0],
                                             {**SPEC["channels"][1], "indicator": "NotAnIndicator"}]}),
              "--indicator-channel-export-paths", export_json)),
            ("fixed rejects spec", fixed5, ("--feature-spec", spec_json)),
        )
        for name, path, flags in failures:
            check(run(path, *flags), 1, name)
            check(run(path, *flags, optimized=True), 1, f"{name} under -O")

        bad = onnx.load(str(fixed5))
        del bad.metadata_props[:]
        bad_path = root / "no_metadata.onnx"
        onnx.save(bad, bad_path)
        check(run(bad_path), 1, "missing metadata")
        bad = onnx.load(str(fixed5))
        next(item for item in bad.metadata_props if item.key == "target_type").value = "unknown"
        bad_path = root / "bad_target.onnx"
        onnx.save(bad, bad_path)
        check(run(bad_path), 1, "unknown target")
        bad = onnx.load(str(regression))
        bias = next(i for i, item in enumerate(bad.graph.initializer) if item.name == "B")
        bad.graph.initializer[bias].CopyFrom(
            numpy_helper.from_array(np.array([np.nan], dtype=np.float32), name="B"))
        bad_path = root / "nonfinite_output.onnx"
        onnx.save(bad, bad_path)
        check(run(bad_path), 1, "nonfinite runtime output")
        for name, mutate in (
            ("bad input name", lambda proto: setattr(proto.graph.input[0], "name", "wrong")),
            ("bad input rank", lambda proto: proto.graph.input[0].type.tensor_type.shape.dim.pop()),
            ("bad output type", lambda proto: setattr(proto.graph.output[0].type.tensor_type,
                                                        "elem_type", onnx.TensorProto.DOUBLE)),
        ):
            bad = onnx.load(str(fixed5))
            mutate(bad)
            bad_path = root / f"{name.replace(' ', '_')}.onnx"
            onnx.save(bad, bad_path)
            check(run(bad_path), 1, name)
        empty = root / "empty"
        empty.mkdir()
        check(run(fixed5, data_dir=empty), 1, "empty data")
        short = root / "short"
        short.mkdir()
        frame.head(1).write_parquet(short / "TEST.parquet")
        check(run(fixed5, data_dir=short), 1, "one-bar data")

        frame.with_columns(pl.lit(1e50).alias("close")).write_parquet(data / "TEST.parquet")
        price_spec = json.dumps({"channels": [{"kind": "price", "price": "close",
                                                 "normalization": "none"}]})
        price_model = model("price_nonfinite", "composed_features", 1, spec=price_spec)
        check(run(price_model, "--feature-spec", price_spec), 1, "nonfinite float32 feature")

        syntax = subprocess.run([sys.executable, str(TRAINING / "verify_model_strict.py")],
                                capture_output=True, text=True, timeout=60)
        check(syntax, 2, "CLI syntax")
        print("strict ONNX verifier integration cases: PASS")


if __name__ == "__main__":
    main()
