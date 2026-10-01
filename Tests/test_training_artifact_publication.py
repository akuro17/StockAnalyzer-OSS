"""Publication recovery checks for the final scored training artifact bundle."""

from __future__ import annotations

import json
import os
import sys
import tempfile
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "StockAnalyzer.Python" / "training"))
import run_training as runner  # noqa: E402


def bundle(source_root: Path, *, fixed: bool = False):
    model = source_root / "fold" / "candidate.onnx"
    model.parent.mkdir()
    model.write_bytes(b"new-scored-model")
    report = {"evaluation_status": "independent_outer",
              "scored_model_sha256": runner._file_sha256(model), "fold_macro_f1": 0.5}
    metrics = model.with_name(model.name + ".metrics.json")
    metrics.write_text(json.dumps(report), encoding="utf-8")
    scaler = None
    if fixed:
        scaler = model.with_name(model.name + ".scaler.json")
        scaler.write_text(json.dumps({"schema_version": 1,
                                      "feature_contract_hash": "validated-source-hash"}),
                          encoding="utf-8")
    return model, metrics, scaler, report


def old_bundle(target: Path):
    target.parent.mkdir(parents=True, exist_ok=True)
    paths = (target, target.with_name(target.name + ".metrics.json"),
             target.with_name(target.name + ".scaler.json"))
    for path, content in zip(paths, (b"old-model", b"old-metrics", b"old-scaler")):
        path.write_bytes(content)
    return {path: path.read_bytes() for path in paths}


def assert_old_unchanged(contents: dict[Path, bytes]):
    for path, expected in contents.items():
        assert path.read_bytes() == expected, path


def test_cross_volume_stage_and_success() -> bool:
    with tempfile.TemporaryDirectory() as source_dir, tempfile.TemporaryDirectory(dir=ROOT / "Tests") as dest_dir:
        source, metrics, scaler, report = bundle(Path(source_dir), fixed=True)
        target = Path(dest_dir) / "artifacts" / source.name
        real_replace = os.replace

        def same_volume_replace(src, dst):
            assert Path(src).anchor.lower() == Path(dst).anchor.lower(), (src, dst)
            return real_replace(src, dst)

        with patch.object(runner.os, "replace", side_effect=same_volume_replace):
            runner._publish_release_bundle(source, metrics, scaler, target, report)
        assert target.read_bytes() == source.read_bytes()
        assert json.loads(target.with_name(target.name + ".metrics.json").read_text()) == report
        assert target.with_name(target.name + ".scaler.json").read_bytes() == scaler.read_bytes()
        assert not list(target.parent.glob(".*_publish_*"))
        return Path(source_dir).anchor.lower() != Path(dest_dir).anchor.lower()


def test_failed_commit_restores_prior_bundle():
    for failed_name in (".metrics.json", ".scaler.json", ".onnx"):
        with tempfile.TemporaryDirectory() as source_dir, tempfile.TemporaryDirectory(dir=ROOT / "Tests") as dest_dir:
            source, metrics, scaler, report = bundle(Path(source_dir), fixed=True)
            target = Path(dest_dir) / "artifacts" / source.name
            previous = old_bundle(target)
            failed = False
            real_replace = os.replace

            def fail_once(src, dst):
                nonlocal failed
                if not failed and str(dst).endswith(failed_name) and Path(dst).parent == target.parent:
                    failed = True
                    raise OSError(f"injected {failed_name} publish failure")
                return real_replace(src, dst)

            with patch.object(runner.os, "replace", side_effect=fail_once):
                try:
                    runner._publish_release_bundle(source, metrics, scaler, target, report)
                except OSError as error:
                    assert "injected" in str(error)
                else:
                    raise AssertionError(f"{failed_name} publication unexpectedly succeeded")
            assert failed
            assert_old_unchanged(previous)
            assert not list(target.parent.glob(".*_publish_*"))


def test_new_bundle_failure_and_invalid_inputs_leave_no_publication():
    with tempfile.TemporaryDirectory() as source_dir, tempfile.TemporaryDirectory(dir=ROOT / "Tests") as dest_dir:
        source, metrics, scaler, report = bundle(Path(source_dir), fixed=True)
        target = Path(dest_dir) / "artifacts" / source.name
        real_replace = os.replace

        def fail_model(src, dst):
            if Path(dst) == target:
                raise OSError("injected model publish failure")
            return real_replace(src, dst)

        with patch.object(runner.os, "replace", side_effect=fail_model):
            try:
                runner._publish_release_bundle(source, metrics, scaler, target, report)
            except OSError:
                pass
            else:
                raise AssertionError("model publication unexpectedly succeeded")
        assert not any(target.parent.iterdir())

        for bad_scaler in (source.with_name("missing.scaler.json"), scaler):
            if bad_scaler == scaler:
                bad_scaler.write_text("not JSON", encoding="utf-8")
            try:
                runner._publish_release_bundle(source, metrics, bad_scaler, target, report)
            except (OSError, ValueError, json.JSONDecodeError):
                pass
            else:
                raise AssertionError("invalid bundle unexpectedly published")
            assert not any(target.parent.iterdir())


def test_score_evidence_mismatch_preserves_prior_bundle():
    with tempfile.TemporaryDirectory() as source_dir, tempfile.TemporaryDirectory(dir=ROOT / "Tests") as dest_dir:
        source, metrics, scaler, report = bundle(Path(source_dir), fixed=True)
        target = Path(dest_dir) / "artifacts" / source.name
        previous = old_bundle(target)
        source.write_bytes(b"model-changed-after-scoring")
        try:
            runner._publish_release_bundle(source, metrics, scaler, target, report)
        except ValueError as error:
            assert "score evidence" in str(error)
        else:
            raise AssertionError("model changed after scoring was published")
        assert_old_unchanged(previous)
        assert not list(target.parent.glob(".*_publish_*"))


def test_nonfixed_overwrite_retires_stale_scaler():
    with tempfile.TemporaryDirectory() as source_dir, tempfile.TemporaryDirectory(dir=ROOT / "Tests") as dest_dir:
        source, metrics, _scaler, report = bundle(Path(source_dir))
        target = Path(dest_dir) / "artifacts" / source.name
        old_bundle(target)
        runner._publish_release_bundle(source, metrics, None, target, report)
        assert target.read_bytes() == source.read_bytes()
        assert not target.with_name(target.name + ".scaler.json").exists()


def test_failed_stale_scaler_retirement_restores_prior_bundle():
    with tempfile.TemporaryDirectory() as source_dir, tempfile.TemporaryDirectory(dir=ROOT / "Tests") as dest_dir:
        source, metrics, _scaler, report = bundle(Path(source_dir))
        target = Path(dest_dir) / "artifacts" / source.name
        previous = old_bundle(target)
        real_replace = os.replace

        def fail_retirement(src, dst):
            if Path(src) == target.with_name(target.name + ".scaler.json"):
                raise OSError("injected stale scaler retirement failure")
            return real_replace(src, dst)

        with patch.object(runner.os, "replace", side_effect=fail_retirement):
            try:
                runner._publish_release_bundle(source, metrics, None, target, report)
            except OSError as error:
                assert "stale scaler retirement" in str(error)
            else:
                raise AssertionError("failed stale scaler retirement was not reported")
        assert_old_unchanged(previous)
        assert not list(target.parent.glob(".*_publish_*"))


def test_failed_restoration_retains_recovery_copies():
    with tempfile.TemporaryDirectory() as source_dir, tempfile.TemporaryDirectory(dir=ROOT / "Tests") as dest_dir:
        source, metrics, scaler, report = bundle(Path(source_dir), fixed=True)
        target = Path(dest_dir) / "artifacts" / source.name
        previous = old_bundle(target)
        metric_target = target.with_name(target.name + ".metrics.json")
        real_replace = os.replace
        metric_published = False

        def fail_publish_and_restore(src, dst):
            nonlocal metric_published
            if Path(dst) == metric_target:
                if metric_published:
                    raise OSError("injected metrics restore failure")
                metric_published = True
            if Path(dst) == target:
                raise OSError("injected model publish failure")
            return real_replace(src, dst)

        with patch.object(runner.os, "replace", side_effect=fail_publish_and_restore):
            try:
                runner._publish_release_bundle(source, metrics, scaler, target, report)
            except RuntimeError as error:
                assert "recovery copies remain" in str(error)
            else:
                raise AssertionError("failed restoration was not reported")
        stages = list(target.parent.glob(".*_publish_*"))
        assert len(stages) == 1
        for path, contents in previous.items():
            assert (stages[0] / "previous" / path.name).read_bytes() == contents
        assert target.read_bytes() == previous[target]


def test_real_fixed_lightgbm_publication():
    """Exercise the published scaler reference after a real cross-drive training run."""
    import datetime

    import polars as pl
    sys.path.insert(0, str(ROOT / "Tests" / "StockAnalyzer.Core.Tests" / "Assets"))
    import check_composed_trainers as prior

    with prior.workspace_case_dir() as workspace:
        data, exports = prior.make_data(workspace)
        dates = [datetime.date(2015, 1, 1) + datetime.timedelta(days=7 * i)
                 for i in range(90)]
        for path in [*data.glob("*.parquet"), *workspace.glob("*_indicator.parquet")]:
            pl.read_parquet(path).with_columns(pl.Series("date", dates)).write_parquet(path)
        spec = {"schema_version": 1, "channels": [
            {"kind": "price", "price": "close", "normalization": "none"},
            {"kind": "indicator", "indicator": "RSI", "normalization": "none"},
        ], "lags": [1]}
        config = {"symbols": ["AAA", "BBB"], "architecture": "gbdt", "window_size": 6,
                  "horizon": 2, "timeframe": "weekly", "framework": "lightgbm",
                  "feature_mode": "composed_features", "feature_spec": spec,
                  "indicator_channel_export_paths": exports,
                  "prepared_input_dir": str(data), "fixed_zscore": True,
                  "clip_sigma": 3.0, "n_splits": 2, "gap": 0,
                  "hyperparameters": {"n_estimators": "12", "early_stopping": "3"},
                  "run_id": "publication_fixed_real"}
        config_path = workspace / "job.json"
        config_path.write_text(json.dumps(config), encoding="utf-8")
        with patch.object(runner, "ARTIFACTS_DIR", workspace / "artifacts"):
            assert runner.main(["--config", str(config_path)]) == 0
        model = next((workspace / "artifacts").glob("*.onnx"))
        published_metrics = json.loads(model.with_name(model.name + ".metrics.json").read_text())
        assert published_metrics["scored_model_sha256"] == runner._file_sha256(model)
        assert model.with_name(model.name + ".scaler.json").is_file()
        prior.run([sys.executable, str(ROOT / "StockAnalyzer.Python" / "training" / "verify_model_strict.py"),
                   "--model", str(model), "--data-dir", str(data),
                   "--feature-spec", json.dumps(spec),
                   "--indicator-channel-export-paths", json.dumps(exports)])


if __name__ == "__main__":
    different_drives = test_cross_volume_stage_and_success()
    test_failed_commit_restores_prior_bundle()
    test_new_bundle_failure_and_invalid_inputs_leave_no_publication()
    test_score_evidence_mismatch_preserves_prior_bundle()
    test_nonfixed_overwrite_retires_stale_scaler()
    test_failed_stale_scaler_retirement_restores_prior_bundle()
    test_failed_restoration_retains_recovery_copies()
    if "--real-fixed" in sys.argv[1:]:
        test_real_fixed_lightgbm_publication()
    print("training artifact publication: PASS",
          "(different source/destination drives)" if different_drives else "(same-drive source fixture)")
