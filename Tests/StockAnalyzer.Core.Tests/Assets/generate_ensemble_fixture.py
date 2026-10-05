"""Regenerate the C# test fixture for the T13 ensemble manifest and the mixture parity vectors.

Two tiny classification generations (the same graph as the other prediction fixtures, differing only in their
bias) are scored on synthetic rows, and the *production Python code* (``ensemble.evaluate`` /
``ensemble.build_manifest``) writes the manifests the C# loader and deployment service consume:

    ensemble_fixture_m01.onnx / .metrics.json       member 1 and its independent-outer evidence
    ensemble_fixture_m02.onnx / .metrics.json       member 2
    ensemble_fixture_bagging.ensemble.json          equal weights (bagging)
    ensemble_fixture_stacking.ensemble.json         weights fitted on the OOF fold (stacking)
    Tests/EnsembleMixtureParity.json                Python mixtures the C# EnsemblePredictionService must reproduce

Run with the training interpreter:
    StockAnalyzer.Python/.venv/Scripts/python.exe Tests/StockAnalyzer.Core.Tests/Assets/generate_ensemble_fixture.py

The generated files are committed; re-run only when the ensemble contract changes.
"""

from __future__ import annotations

import hashlib
import json
import sys
from pathlib import Path

import numpy as np
import onnx
import onnxruntime as ort
from onnx import numpy_helper

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parents[2] / "StockAnalyzer.Python" / "training"))
sys.path.insert(0, str(HERE))
import checkpoints  # noqa: E402
import dataset as ds  # noqa: E402
import ensemble  # noqa: E402
import generate_onnx_fixtures as base  # noqa: E402
import onnx_meta  # noqa: E402

STEM = "ensemble_fixture"
DATA_REVISION = "c" * 64
EVALUATION_REVISION = "d" * 64
SEED = 20261004
OOF_ROWS = 160
RELEASE_ROWS = 120
MAX_ENSEMBLE_MEMBERS = 8
BIAS_STEP = (0.35, -0.2, 0.0)


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _member(index: int) -> Path:
    model = base._build(3)
    for initializer in model.graph.initializer:
        if initializer.name == "B":
            bias = numpy_helper.to_array(initializer) + np.array(BIAS_STEP, dtype=np.float32) * index
            initializer.CopyFrom(numpy_helper.from_array(bias.astype(np.float32), "B"))
    onnx.checker.check_model(model)
    mapping = onnx_meta.build_contract(
        feature_mode="ohlcv_minmax", window_size=base.FIXTURE_WINDOW, channels=base.CHANNELS,
        horizon=5, threshold=0.005, wf_splits=2, seed=42, producer="generate_ensemble_fixture.py")
    onnx_meta.apply(model, mapping)
    path = HERE / f"{STEM}_m{index + 1:02d}.onnx"
    onnx.save(model, str(path))
    return path


def _metrics(path: Path, model_hash: str) -> None:
    report = {
        "class_order": "Up,Down,Neutral", "data_revision": DATA_REVISION,
        "evaluation_revision": EVALUATION_REVISION, "evaluation_status": "independent_outer",
        "fold": 1.0, "fold_accuracy": 0.5, "fold_is_holdout": 1.0, "fold_macro_f1": 0.5, "fold_n": 10.0,
        "horizon": 5, "n_splits": 2.0,
        "outer_folds": [
            {"evaluation_revision": EVALUATION_REVISION, "fold": 0.0, "fold_accuracy": 0.5, "fold_macro_f1": 0.5,
             "scored_model_sha256": model_hash},
            {"evaluation_revision": EVALUATION_REVISION, "fold": 1.0, "fold_accuracy": 0.5, "fold_macro_f1": 0.5,
             "scored_model_sha256": model_hash},
        ],
        "scored_model_sha256": model_hash, "target_type": "classification", "timeframe": "daily",
    }
    Path(str(path) + ".metrics.json").write_text(
        json.dumps(report, separators=(",", ":"), sort_keys=True, allow_nan=False), encoding="utf-8")


def main() -> None:
    rng = np.random.default_rng(SEED)
    paths = [_member(i) for i in range(2)]
    hashes = [_sha256(p) for p in paths]
    assert hashes[0] != hashes[1]
    for path, digest in zip(paths, hashes):
        _metrics(path, digest)
    sessions = [ort.InferenceSession(str(p), providers=["CPUExecutionProvider"]) for p in paths]

    def predict(session, x):
        (p,) = session.run(["output"], {"input": x.astype(np.float32)})
        return np.asarray(p, dtype=np.float64)

    captures = [{}, {}]
    for fold, rows in ((0, OOF_ROWS), (1, RELEASE_ROWS)):
        x = rng.normal(0.0, 1.0, (rows, base.FIXTURE_WINDOW, base.CHANNELS)).astype(np.float32)
        anchors = np.arange(rows) + 1000 * fold
        reference = predict(sessions[0], x)
        y = np.array([rng.choice(3, p=row / row.sum()) for row in reference], dtype=np.int64)
        for member, session in enumerate(sessions):
            captures[member][fold] = {"AAA": (anchors, y, predict(session, x))}

    resource_limits = {"max_ensemble_members": MAX_ENSEMBLE_MEMBERS}
    for method in (ensemble.METHOD_BAGGING, ensemble.METHOD_STACKING):
        evaluation = ensemble.evaluate(method, 2, captures)
        members = [{
            "index": i, "file": p.name, "sha256": hashes[i], "metrics_file": p.name + ".metrics.json",
            "weight": evaluation["weights"][i], "framework": "pytorch", "architecture": "lstm",
            "release_fold_macro_f1": 0.5, "release_fold_accuracy": 0.5, "bagging": None,
        } for i, p in enumerate(paths)]
        bagging = None
        if method == ensemble.METHOD_BAGGING:
            bagging = {"members": 2, "resample": "time_series_block_bootstrap",
                       "block_length": ds.bagging_block_length(base.FIXTURE_WINDOW, 5),
                       "fraction": ds.BAGGING_FRACTION, "rounding": "ceil_blocks"}
        manifest = ensemble.build_manifest(
            method=method, run_id="fixture-run", timeframe="daily", horizon=5, class_order="Up,Down,Neutral",
            members=members, evaluation=evaluation, bagging=bagging, resource_limits=resource_limits)
        target = HERE / f"{STEM}_{method}.ensemble.json"
        target.write_bytes((json.dumps(manifest, indent=2, allow_nan=False) + chr(10)).encode("utf-8"))
        print("wrote", target.name, "weights", manifest["weights"])

    # mixture parity: float32-representable member rows, weights on the simplex, Python result in float64
    cases = []
    for weights in ([0.5, 0.5], [0.25, 0.75], [0.2, 0.3, 0.5], [1.0, 0.0], [0.6, 0.4]):
        member_count = len(weights)
        raw = rng.dirichlet(np.ones(3), size=member_count).astype(np.float32).astype(np.float64)
        mixed = ensemble.mix(np.array(weights), raw[:, None, :])[0]
        cases.append({"weights": weights, "rows": raw.tolist(), "mixed": mixed.tolist()})
    parity = {
        "schema_version": ensemble.SCHEMA_VERSION, "kind": ensemble.KIND, "mixture": "linear_normalized",
        "sum_tolerance": ensemble.SUM_TOLERANCE, "min_members": ensemble.MIN_MEMBERS,
        "default_bagging_members": ensemble.DEFAULT_BAGGING_MEMBERS,
        "max_ensemble_members": MAX_ENSEMBLE_MEMBERS,
        # the names the host's rollback of a failed run relies on
        "layout": {"member_stem_example": ensemble.member_stem("grp", 0), "member_run_id_example": ensemble.member_run_id("run", 0),
                   "ensemble_file_suffix": ensemble.ENSEMBLE_FILE_SUFFIX, "checkpoint_directory": checkpoints.DIRECTORY_NAME,
                   "lightgbm_architecture": ensemble.LIGHTGBM_ARCHITECTURE},
        "cases": cases,
    }
    parity_path = HERE.parents[1] / "EnsembleMixtureParity.json"
    parity_path.write_bytes((json.dumps(parity, indent=2, allow_nan=False) + chr(10)).encode("utf-8"))
    print("wrote", parity_path.name, len(cases), "cases")


if __name__ == "__main__":
    main()
