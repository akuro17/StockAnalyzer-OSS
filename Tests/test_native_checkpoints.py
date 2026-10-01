"""Real T08 training acceptance cases, invoked by the xUnit training integration test."""
from __future__ import annotations

import copy
import argparse
import json
import sys
import tempfile
from pathlib import Path
from unittest.mock import patch

import numpy as np
import polars as pl
import torch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "StockAnalyzer.Python" / "training"))
import checkpoints as cp
import train_pytorch as pt
import train_lightgbm as gb
import run_training as runner

torch.set_num_threads(1)


def dataset(path, start="2020-01-01"):
    path.mkdir()
    n = 800
    rng = np.random.default_rng(941)
    close = 100 * np.exp(np.cumsum(rng.normal(0, .02, n)))
    dates = np.datetime64(start) + np.arange(n).astype("timedelta64[D]")
    pl.DataFrame(dict(date=pl.Series(dates).cast(pl.Date), open=close*.999,
                      high=close*1.01, low=close*.99, close=close,
                      volume=rng.uniform(1000, 3000, n))).write_parquet(path / "X.parquet")


def args(data, root, *, arch="cnn", target="classification", epochs=2, extra=()):
    return pt._parse_args(["--data-dir", str(data), "--out", str(root / "model.onnx"),
        "--checkpoint-dir", str(root), "--run-id", "exact_run", "--arch", arch,
        "--target-type", target, "--window", "4", "--horizon", "1", "--wf-splits", "2",
        "--hidden", "8", "--layers", "1", "--dropout", ".3", "--patience", "20",
        "--batch", "64", "--epochs", str(epochs), *extra])


def state(root, slot="last"):
    pointer, blob = cp.checked_blob(root / (slot + ".json"))
    return pointer, torch.load(blob, weights_only=True)


def equal(a, b):
    if isinstance(a, torch.Tensor):
        assert torch.equal(a, b), "native tensors differ after Resume"
    elif isinstance(a, dict):
        assert a.keys() == b.keys()
        for k in a: equal(a[k], b[k])
    elif isinstance(a, (list, tuple)):
        assert len(a) == len(b)
        for x, y in zip(a, b): equal(x, y)
    else:
        assert a == b, (a, b)


def rejects(call, text):
    try:
        call()
    except (ValueError, RuntimeError, OSError) as error:
        assert text in str(error), str(error)
    else:
        raise AssertionError("incompatible checkpoint unexpectedly accepted")


def pytorch_cases(data, child, root):
    for arch in ("lstm", "cnn"):
        for target in ("classification", "regression"):
            full, split = root / f"full_{arch}_{target}", root / f"split_{arch}_{target}"
            common = dict(arch=arch, target=target)
            pt.train_model(args(data, full, epochs=4, **common))
            pt.train_model(args(data, split, epochs=2, **common))
            source = split / "last.json"
            pt.train_model(args(data, split, epochs=2, extra=("--initialization-mode", "resume",
                           "--init-from", str(source)), **common))
            _, full_state = state(full)
            _, resumed = state(split)
            for key in ("model", "optimizer", "completed_epoch", "best_state", "best_val",
                        "patience_counter", "torch_rng", "numpy_rng", "numpy_global_rng", "python_rng"):
                equal(full_state[key], resumed[key])
            # Real export + ORT verification binds the best native checkpoint to this ONNX.
            pt.main([str(v) for v in ["--data-dir", data, "--out", split / "model.onnx",
                "--checkpoint-dir", split, "--run-id", "exact_run", "--arch", arch,
                "--target-type", target, "--window", "4", "--horizon", "1", "--wf-splits", "2",
                "--hidden", "8", "--layers", "1", "--dropout", ".3", "--patience", "20",
                "--batch", "64", "--epochs", "1", "--initialization-mode", "resume", "--init-from", source]])
            parent, parent_state = state(split, "best")
            frozen = "lstm" if arch == "lstm" else "features"
            child_root = root / f"child_{arch}_{target}"
            transfer = ("--initialization-mode", "fine_tune", "--init-from", str(split / "best.json"),
                        "--parent-model-id", "a"*64, "--parent-model-hash", parent["exported_model_sha256"],
                        "--freeze-paths", json.dumps([frozen]))
            pt.train_model(args(child, child_root, epochs=1, extra=transfer, **common))
            child_pointer, child_state = state(child_root)
            assert child_state["completed_epoch"] == 1
            assert child_pointer["context"]["lineage"]["parent_checkpoint_sha256"] == parent["sha256"]
            for key, value in parent_state["model"].items():
                if key.startswith(frozen + "."):
                    assert torch.equal(value, child_state["model"][key]), key
            assert all(int(v["step"]) < int(next(iter(resumed["optimizer"]["state"].values()))["step"])
                       for v in child_state["optimizer"]["state"].values())
            rejects(lambda: pt.train_model(args(data, root / "overlap", extra=transfer, **common)), "overlaps")
            rejects(lambda: pt.train_model(args(child, root / "unknown", extra=transfer[:-1] + ('["unknown"]',),
                                               **common)), "unknown freeze")
            rejects(lambda: pt.train_model(args(data, split, extra=("--initialization-mode", "resume",
                        "--init-from", str(source), "--lr", ".002"), **common)), "training_config mismatch")
            print(f"native {arch}/{target}: exact Resume and frozen FineTune PASS")
    # Corruption/path traversal/state absence fails before fitting.
    pointer, blob = cp.checked_blob(source)
    broken = copy.deepcopy(pointer)
    broken["blob"] = "../" + broken["blob"]
    cp.atomic_json(split / "bad.json", broken)
    rejects(lambda: cp.checked_blob(split / "bad.json"), "invalid native")
    broken["blob"] = pointer["blob"]
    broken["sha256"] = "b"*64
    cp.atomic_json(split / "bad.json", broken)
    rejects(lambda: cp.checked_blob(split / "bad.json"), "invalid native")
    incomplete = torch.load(blob, weights_only=True)
    del incomplete["optimizer"]
    broken = copy.deepcopy(pointer)
    bad_blob = split / "incomplete.pt"
    torch.save(incomplete, bad_blob)
    digest = cp.hash_file(bad_blob)
    final = split / (digest + ".pt")
    bad_blob.rename(final)
    broken.update(blob=final.name, sha256=digest)
    cp.atomic_json(split / "bad.json", broken)
    rejects(lambda: pt.train_model(args(data, split, arch=arch, target=target, extra=(
        "--initialization-mode", "resume", "--init-from", str(split / "bad.json")))), "incomplete native")


def lightgbm_cases(data, child, root):
    parent = root / "gb_parent"
    base = ["--window", "4", "--horizon", "1", "--wf-splits", "2", "--n-estimators", "4",
            "--num-leaves", "5", "--min-child-samples", "2", "--early-stopping", "20"]
    gb.main(["--data-dir", str(data), "--out", str(parent / "model.onnx"),
             "--checkpoint-dir", str(parent), "--run-id", "gb_parent", *base])
    pointer, _ = cp.checked_blob(parent / "best.json")
    child_root = root / "gb_child"
    gb.main(["--data-dir", str(child), "--out", str(child_root / "model.onnx"),
             "--checkpoint-dir", str(child_root), "--run-id", "gb_child", *base,
             "--initialization-mode", "fine_tune", "--init-from", str(parent / "best.json"),
             "--parent-model-id", "a"*64, "--parent-model-hash", pointer["exported_model_sha256"]])
    child_pointer, _ = cp.checked_blob(child_root / "last.json")
    assert child_pointer["completed_iteration"] > pointer["completed_iteration"]
    rejects(lambda: gb._parse_args(["--initialization-mode", "resume", "--init-from", str(parent / "last.json")]),
            "does not support")
    print("native lightgbm: additional trees and ONNX parity PASS")


def interrupted_and_scaler_cases(data, child, root):
    full, split = root / "interrupted_full", root / "interrupted_split"
    controls = ("--fixed-zscore", "--lags", "[1]", "--clip-sigma", "3")
    pt.train_model(args(data, full, epochs=4, extra=controls))
    pt.train_model(args(data, split, epochs=2, extra=controls))
    committed = (split / "last.json").read_bytes()
    real_publish = cp.atomic_json

    def interrupted_publish(path, value):
        if path.name == "last.json":
            raise OSError("injected interrupted epoch publication")
        return real_publish(path, value)

    continuation = (*controls, "--initialization-mode", "resume", "--init-from", str(split / "last.json"))
    with patch.object(cp, "atomic_json", side_effect=interrupted_publish):
        rejects(lambda: pt.train_model(args(data, split, epochs=1, extra=continuation)), "interrupted epoch")
    assert (split / "last.json").read_bytes() == committed
    pt.train_model(args(data, split, epochs=2, extra=continuation))
    _, expected = state(full)
    _, actual = state(split)
    for key in ("model", "optimizer", "best_state", "best_val", "torch_rng", "numpy_rng"):
        equal(expected[key], actual[key])
    assert expected["context"]["scaler"] == actual["context"]["scaler"]
    # Exhausted early stopping is preserved, rather than reset to allow new steps.
    stopped = root / "stopped"
    pt.train_model(args(data, stopped, epochs=2, extra=("--patience", "1", "--lr", "0")))
    _, before = state(stopped)
    pt.train_model(args(data, stopped, epochs=2, extra=("--patience", "1", "--lr", "0",
        "--initialization-mode", "resume", "--init-from", str(stopped / "last.json"))))
    _, after = state(stopped)
    assert before["patience_counter"] == after["patience_counter"] == 1
    equal(before["optimizer"], after["optimizer"])
    equal(before["model"], after["model"])
    print("native interruption: atomic epoch recovery, fixed scaler and exhausted stopping PASS")


def orchestrator_cases(data, root):
    cfg = dict(symbols=["X"], architecture="cnn", window_size=4, horizon=1, n_splits=2,
               run_id="fold_run", hyperparameters=dict(epochs="1", hidden="8", layers="1", patience="20"))
    config = root / "job.json"
    config.write_text(json.dumps(cfg))
    with patch.object(runner, "ARTIFACTS_DIR", root / "artifacts"), \
         patch.object(runner, "_resolve_dataset_dir", return_value=data):
        runner.main(["--config", str(config)])
        manifest = root / "artifacts" / "checkpoints" / "fold_run" / "run.json"
        assert manifest.exists()
        cfg.update(initialization_mode="resume", checkpoint_path=str(manifest))
        config.write_text(json.dumps(cfg))
        runner.main(["--config", str(config)])
        for k in range(2):
            pointer, _ = cp.checked_blob(manifest.parent / f"fold_{k}" / "last.json")
            assert pointer["completed_epoch"] == 2 and pointer["context"]["fold"] == k
        # Missing a completed fold may never silently restart it as a new model.
        last = manifest.parent / "fold_1" / "last.json"
        saved = last.read_bytes()
        last.unlink()
        rejects(lambda: runner.main(["--config", str(config)]), "state is missing")
        last.write_bytes(saved)
        # A corrupt later fold is detected before even the earlier optimizer runs.
        pointer, blob = cp.checked_blob(last)
        payload = torch.load(blob, weights_only=True)
        del payload["optimizer"]
        corrupt = last.parent / "incomplete.pt"
        torch.save(payload, corrupt)
        digest = cp.hash_file(corrupt)
        corrupt.rename(last.parent / (digest + ".pt"))
        pointer.update(blob=digest + ".pt", sha256=digest)
        cp.atomic_json(last, pointer)
        with patch.object(torch.optim.AdamW, "step", side_effect=AssertionError("fit occurred before preflight")):
            rejects(lambda: runner.main(["--config", str(config)]), "incomplete native")
        last.write_bytes(saved)
        cfg["hyperparameters"]["lr"] = ".002"
        config.write_text(json.dumps(cfg))
        rejects(lambda: runner.main(["--config", str(config)]), "identical run configuration")
    print("native orchestrator: per-fold durable Resume and configuration guard PASS")


def registry_bundle(root, parent_id, parent_hash):
    root.mkdir(parents=True, exist_ok=True)
    data, child = root / "data", root / "later"
    if not data.exists(): dataset(data)
    if not child.exists(): dataset(child, "2030-01-01")
    run_id = "registry_child" if parent_id else "registry_parent"
    cfg = dict(symbols=["X"], architecture="cnn", window_size=4, horizon=1, n_splits=2,
               run_id=run_id, hyperparameters=dict(epochs="1", hidden="8", layers="1", patience="20"))
    if parent_id:
        cfg.update(initialization_mode="fine_tune", parent_model_id=parent_id, parent_model_hash=parent_hash,
                   checkpoint_path=str(root / "artifacts" / "checkpoints" / "registry_parent" / "fold_1" / "best.json"),
                   freeze_paths=["features"])
    config = root / "job.json"
    config.write_text(json.dumps(cfg))
    with patch.object(runner, "ARTIFACTS_DIR", root / "artifacts"), \
         patch.object(runner, "_resolve_dataset_dir", return_value=child if parent_id else data):
        runner.main(["--config", str(config)])
    print("REGISTRY_MODEL:" + str(root / "artifacts" / f"x_daily_clf_cnn_ohlcv_{run_id}.onnx"))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--registry-bundle", type=Path)
    parser.add_argument("--parent-model-id")
    parser.add_argument("--parent-model-hash")
    cli = parser.parse_args()
    if cli.registry_bundle:
        registry_bundle(cli.registry_bundle, cli.parent_model_id, cli.parent_model_hash)
        raise SystemExit(0)
    with tempfile.TemporaryDirectory(prefix="sa_t08_acceptance_") as directory:
        root = Path(directory)
        data, child = root / "data", root / "later"
        dataset(data)
        dataset(child, "2030-01-01")
        pytorch_cases(data, child, root)
        interrupted_and_scaler_cases(data, child, root)
        lightgbm_cases(data, child, root)
        orchestrator_cases(data, root)
    print("T08 native checkpoint acceptance: PASS")
