"""T09 causal/numerical and real selected-model publication acceptance (xUnit entry)."""
from __future__ import annotations

import argparse
import copy
import json
import sys
import tempfile
from pathlib import Path
from unittest.mock import patch

import numpy as np

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "StockAnalyzer.Python" / "training"))
import model_analysis as analysis
import dataset as ds
import checkpoints as cp
import onnx_meta
import fixed_scaler
import run_training as runner


def numerical_cases():
    dates = np.datetime64("2020-01-01") + np.arange(100).astype("timedelta64[D]")
    close = np.exp(np.linspace(0, 1, 100)) * 100
    arr = np.column_stack([close, close, close, close, np.ones(100)])
    before, retained = analysis.vectors(arr, np.array([19, 20, 21, 45]))
    assert retained.tolist() == [20, 21, 45]  # never use a synthetic return at row zero
    arr[46:, :4] *= 100
    after, _ = analysis.vectors(arr, retained)
    np.testing.assert_array_equal(before, after)  # future corrections cannot affect anchors
    arr[44, ds.CLOSE] = 0
    bad, ids = analysis.vectors(arr, np.array([45]))
    assert not len(ids) and bad.shape == (0, 2)

    x = np.array([[-.1, .02]] * 20 + [[0, .02]] * 20 + [[.1, .02]] * 20)
    fit = analysis.period("X", "train", dates, np.arange(60))
    state = analysis.fit_regime(x, [fit])
    other = analysis.fit_regime(x, [fit])
    assert state == other and state["standardization_ddof"] == 0 and state["std"][1] == 0
    assert np.all(np.diff(np.array(state["unscaled_centroids"])[:, 0]) > 0)
    constant_axis = analysis.fit_regime(np.column_stack([np.arange(6), np.zeros(6)]), [])
    assert constant_axis["std"][1] == 0 and np.isfinite(constant_axis["centroids"]).all()
    assert analysis.fit_regime(np.zeros((3, 2)), [fit])["status"] == "insufficient_training_observations"

    base = ["close", "volume"]
    expanded = ["lag0:close", "lag0:volume", "lag2:close", "lag2:volume"]
    gain = analysis.gain_importance(np.array([1, 0, 3, 0, 2, 2, 0, 2]), 2, expanded, base, 4)
    np.testing.assert_allclose([v["value"] for v in gain["values"]], [60, 40])
    assert analysis.gain_importance(np.zeros(8), 2, expanded, base, 4)["status"] == "zero_total_gain"

    data = np.zeros((30, 3, 4), dtype=np.float32)
    data[:, :, 0] = np.arange(30)[:, None] % 3
    data[:, :, 2] = data[:, :, 0]  # lag copies must move with every window position
    labels = (data[:, 0, 0].astype(int) + 1) % 3
    def predict(tensor):
        np.testing.assert_array_equal(tensor[:, :, 0], tensor[:, :, 2])
        np.testing.assert_array_equal(tensor[:, 0, 0], tensor[:, -1, 0])
        return np.eye(3)[tensor[:, 0, 0].astype(int)]
    result = analysis.permutation_importance(predict, data, labels, base)
    assert result == analysis.permutation_importance(predict, data, labels, base)
    assert result["values"][0]["value"] < 0 and result["values"][1]["value"] == 0
    assert result["population"] == "inner_validation" and result["unit"] == "accuracy_percentage_points"
    selected = {"X": (data, labels, np.arange(30), np.arange(10), np.arange(10, 20))}
    analysis.validate_population(selected, dict(max_samples=20, max_tensor_elements=240))
    for limits in (dict(max_samples=19, max_tensor_elements=240), dict(max_samples=20, max_tensor_elements=239)):
        try:
            analysis.validate_population(selected, limits)
        except ValueError:
            pass
        else:
            raise AssertionError("analysis exceeded its resource population bound")
    # Multi-symbol split and fitting use the same globally purged anchor selector.
    rng = np.random.default_rng(124)
    syms, dts = {}, {}
    for symbol, shift in (("X", 0), ("Y", 5)):
        close = np.exp(np.cumsum(rng.normal(0, .02, 800))) * 100
        syms[symbol] = np.column_stack([close, close, close, close, np.ones(800)])
        dts[symbol] = np.datetime64("2018-01-01") + np.arange(shift, shift + 800).astype("timedelta64[D]")
    folds = ds.independent_fold_data(syms, dts, feature_mode="ohlcv_minmax", window=4, horizon=1,
        threshold=ds.DEFAULT_THRESHOLD, n_splits=2, gap=None, fold_index=1, inner=True)
    matrices, fits = [], []
    for symbol, (_, _, anchors, fit_indices, val_indices) in folds.items():
        vector, retained = analysis.vectors(syms[symbol], anchors[fit_indices])
        matrices.append(vector); fits.append(analysis.period(symbol, "train", dts[symbol], retained))
        syms[symbol][anchors[val_indices[0]]:, :4] *= 1.2
        changed, _ = analysis.vectors(syms[symbol], anchors[fit_indices])
        np.testing.assert_array_equal(vector, changed)
    fitted = analysis.fit_regime(np.concatenate(matrices), fits)
    assert fitted["fit_sample_count"] == sum(f["sample_count"] for f in fits)
    assert len(fitted["fit_data_revision"]) == 64
    print("T09 numerical/causality acceptance: PASS")


def emit_fixture(destination: Path):
    import onnx
    destination.mkdir(parents=True, exist_ok=True)
    model_path = destination / "analysis_fixture.onnx"
    model = onnx.load(str(ROOT / "Tests" / "StockAnalyzer.Core.Tests" / "Assets" / "trend_predictor_goodmeta.onnx"))
    metadata = {p.key: p.value for p in model.metadata_props}
    metadata[analysis.REFERENCE_KEY] = model_path.name + ".analysis.json"
    onnx_meta.apply(model, metadata)
    onnx.save(model, str(model_path))
    dates = np.datetime64("2020-01-01") + np.arange(100).astype("timedelta64[D]")
    periods = [analysis.period("X", kind, dates, ids) for kind, ids in (
        ("train", np.arange(60)), ("validation", np.arange(70, 80)), ("oos", np.arange(90, 100)))]
    x = np.column_stack([np.repeat([-.1, 0, .1], 20), np.zeros(60)])
    base, expanded = analysis.channel_names(metadata)
    importance = dict(status="available", method="grouped_permutation", unit="accuracy_percentage_points",
                      population="inner_validation", metric="classification_accuracy", repeats=10, seed=42,
                      baseline_accuracy=.5, values=[dict(channel=n, value=(-2.5 if i == 0 else 5.0))
                                                     for i, n in enumerate(base)])
    document = dict(schema_version=1, model_sha256=cp.hash_file(model_path), data_revision="a"*64,
                    feature_contract_hash=fixed_scaler.contract_hash(metadata), timeframe="daily",
                    window_size=int(metadata["window_size"]), ordered_channels=expanded, base_channels=base,
                    periods=periods, regime=analysis.fit_regime(x, [periods[0]]), importance=importance)
    cp.atomic_json(Path(str(model_path) + ".analysis.json"), document)
    report = dict(evaluation_status="independent_outer", evaluation_revision="b"*64,
                  scored_model_sha256=cp.hash_file(model_path), data_revision=document["data_revision"],
                  target_type="classification", timeframe="daily", horizon=int(metadata["prediction_horizon"]),
                  class_order=metadata["class_order"], fold_macro_f1=.5, fold_is_holdout=1.0,
                  fold=1.0, fold_n=10.0, n_splits=2.0,
                  outer_folds=[dict(fold=float(i), evaluation_revision="b"*64,
                                   scored_model_sha256=cp.hash_file(model_path), fold_macro_f1=.5) for i in range(2)])
    cp.atomic_json(Path(str(model_path) + ".metrics.json"), report)
    print("T09 fixture: " + str(model_path))


def real_training(root: Path, framework: str, target="classification"):
    from test_native_checkpoints import dataset
    data = root / "data"
    dataset(data)
    if framework == "lightgbm":
        arch, hp = "gbdt", {"n_estimators": 8, "early_stopping": 3, "min_child_samples": 5}
    else:
        arch, hp = "cnn", {"epochs": 1, "hidden": 8, "layers": 1, "dropout": .1, "batch": 64}
        if framework == "tensorflow":
            hp.pop("layers")
    config = dict(symbols=["X"], architecture=arch, framework=framework, target_type=target,
                  window_size=4, horizon=1, n_splits=2, oos_tail_days=60,
                  run_id="analysis_run", hyperparameters=hp)
    path = root / "config.json"
    path.write_text(json.dumps(config), encoding="utf-8")
    with patch.object(runner, "ARTIFACTS_DIR", root / "artifacts"), patch.object(runner, "MAX_RUN_DURATION_SECONDS", 240), \
         patch.object(runner, "_resolve_dataset_dir", return_value=data):
        runner.main(["--config", str(path)])
    model = next((root / "artifacts").glob("*.onnx"))
    document = json.loads(Path(str(model) + ".analysis.json").read_text())
    report = json.loads(Path(str(model) + ".metrics.json").read_text())
    assert document["model_sha256"] == report["scored_model_sha256"] == cp.hash_file(model)
    assert document["data_revision"] == report["data_revision"]
    assert [p["kind"] for p in document["periods"]] == ["train", "validation", "oos"]
    assert document["periods"][0]["anchor_end"] < document["periods"][1]["anchor_start"]
    assert document["periods"][1]["anchor_end"] < document["periods"][2]["anchor_start"]
    assert document["regime"]["status"] == "available"
    expected = "unavailable" if target == "regression" else "split_gain" if framework == "lightgbm" else "grouped_permutation"
    assert document["importance"]["method"] == expected
    # Final publication rollback includes the newly required analysis member.
    before = {p.name: p.read_bytes() for p in model.parent.glob(model.name + "*")}
    original_replace = runner.os.replace
    fail_at = str(model)
    injected = False
    def fail_model(source, destination):
        nonlocal injected
        if str(destination) == fail_at and not injected:
            injected = True
            raise OSError("injected model publication failure")
        return original_replace(source, destination)
    try:
        with patch.object(runner.os, "replace", side_effect=fail_model):
            runner._publish_release_bundle(model, Path(str(model) + ".metrics.json"), None, model, report,
                                            Path(str(model) + ".analysis.json"))
    except (OSError, RuntimeError):
        pass
    else:
        raise AssertionError("expected publication failure")
    assert before == {p.name: p.read_bytes() for p in model.parent.glob(model.name + "*")}
    print(f"T09 real {framework}/{target} train/export/ORT/publication: PASS")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--emit-fixture", type=Path)
    parser.add_argument("--framework", choices=["pytorch", "lightgbm", "tensorflow"])
    parser.add_argument("--regression", action="store_true")
    args = parser.parse_args()
    numerical_cases()
    if args.emit_fixture: emit_fixture(args.emit_fixture)
    if args.framework:
        with tempfile.TemporaryDirectory(prefix="sa_t09_acceptance_") as directory:
            real_training(Path(directory), args.framework, "regression" if args.regression else "classification")
