"""GUI-triggered ONNX training orchestrator.

Reads a ``TrainingJobConfig`` JSON document -- the contract defined by
``StockAnalyzer.Core/Models/Training/TrainingJobConfig.cs`` and serialized by
``TrainingConfigJson`` -- dispatches to the matching trainer in-process, and
streams a line protocol on stdout for the C# ``ITrainingOrchestrator``:

    STAGE:<name>            pipeline phase entered: load, dataset, train, export, done
    PROGRESS:<0-100>        coarse overall percent
    METRIC:<json>           a flat metrics dict (the final aggregated report)
    ARTIFACT:<kind>:<path>  a produced file; kind is ``onnx`` or ``metrics``

Every other diagnostic line is prefixed ``STDERR:`` (kept on stdout so ordering is
preserved). The existing trainer CLIs (``train_pytorch.py`` / ``train_lightgbm.py``
/ ``train_tensorflow.py``) are NOT modified; this module only calls their public
``main(argv)`` and, when a scope narrows the symbol set or the calendar range,
stages a filtered copy of the parquet directory via
``dataset.materialize_filtered_dir`` so the unchanged trainer trains on the subset.

    python run_training.py --config job.json
"""

from __future__ import annotations

import argparse
import dataclasses
import datetime
import json
import shutil
import sys
import tempfile
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import dataset as ds  # noqa: E402  (sibling module, path inserted above)

ARTIFACTS_DIR: Path = Path(__file__).resolve().parent / "artifacts"

# framework wire string -> (trainer module name, accepts --arch).
_FRAMEWORKS: Dict[str, Tuple[str, bool]] = {
    "pytorch": ("train_pytorch", True),
    "lightgbm": ("train_lightgbm", False),
    "tensorflow": ("train_tensorflow", True),
}

# learning-objective wire strings, mirror of C# TargetType / onnx_meta.TARGET_TYPE.
_TARGET_TYPES: Tuple[str, ...] = ("classification", "regression")

# feature-mode wire string -> short filename tag for the derived output stem.
_FEATURE_TAGS: Dict[str, str] = {
    "ohlcv_minmax": "ohlcv",
    "log_return": "logret",
    "zscore": "zscore",
    "zscore_joint": "zscorej",
    "log_return_ohlc": "logretohlc",
}


# --- Job configuration (mirror of the C# TrainingJobConfig wire contract) ----


@dataclasses.dataclass(frozen=True)
class JobConfig:
    symbols: List[str]
    architecture: str
    window_size: int
    horizon: int
    timeframe: str = "daily"
    framework: str = "pytorch"
    feature_mode: str = "ohlcv_minmax"
    hyperparameters: Dict[str, str] = dataclasses.field(default_factory=dict)
    start_date: Optional[str] = None
    end_date: Optional[str] = None
    output_name: Optional[str] = None
    # validation / target-definition contract (mirror of C# TrainingJobConfig).
    target_type: str = "classification"
    n_splits: int = ds.DEFAULT_WF_SPLITS
    gap: Optional[int] = None
    oos_tail_days: Optional[int] = None

    @staticmethod
    def from_json(text: str) -> "JobConfig":
        raw = json.loads(text)
        if not isinstance(raw, dict):
            raise ValueError("config JSON must be an object")
        known = {f.name for f in dataclasses.fields(JobConfig)}
        unknown = set(raw) - known
        if unknown:
            raise ValueError(f"config has unknown keys: {sorted(unknown)}")
        try:
            cfg = JobConfig(
                symbols=[str(s) for s in (raw.get("symbols") or [])],
                architecture=str(raw["architecture"]),
                window_size=int(raw["window_size"]),
                horizon=int(raw["horizon"]),
                timeframe=str(raw.get("timeframe", "daily")).strip().lower(),
                framework=str(raw.get("framework", "pytorch")).strip().lower(),
                feature_mode=str(raw.get("feature_mode", "ohlcv_minmax")).strip().lower(),
                hyperparameters={
                    str(k): str(v) for k, v in (raw.get("hyperparameters") or {}).items()
                },
                start_date=_opt_str(raw.get("start_date")),
                end_date=_opt_str(raw.get("end_date")),
                output_name=_opt_str(raw.get("output_name")),
                target_type=str(raw.get("target_type", "classification")).strip().lower(),
                n_splits=int(raw.get("n_splits", ds.DEFAULT_WF_SPLITS)),
                gap=_opt_int(raw.get("gap")),
                oos_tail_days=_opt_int(raw.get("oos_tail_days")),
            )
        except KeyError as exc:
            raise ValueError(f"config is missing required key: {exc}") from exc
        cfg.validate()
        return cfg

    def validate(self) -> None:
        if not self.symbols or any(not str(s).strip() for s in self.symbols):
            raise ValueError("config.symbols must be non-empty and free of blank entries")
        if not str(self.architecture).strip():
            raise ValueError("config.architecture must not be empty")
        if self.window_size <= 0:
            raise ValueError("config.window_size must be positive")
        if self.horizon <= 0:
            raise ValueError("config.horizon must be positive")
        if self.framework not in _FRAMEWORKS:
            raise ValueError(
                f"config.framework {self.framework!r} not in {sorted(_FRAMEWORKS)}"
            )
        if self.feature_mode != "ohlcv_minmax":
            raise ValueError(
                "config.feature_mode: only 'ohlcv_minmax' is supported in this release"
            )
        if self.timeframe not in ds.TIMEFRAME_DIRS:
            raise ValueError(
                f"config.timeframe {self.timeframe!r} not in {sorted(ds.TIMEFRAME_DIRS)}"
            )
        if self.target_type not in _TARGET_TYPES:
            raise ValueError(
                f"config.target_type {self.target_type!r} not in {list(_TARGET_TYPES)}"
            )
        if self.n_splits < 2:
            raise ValueError("config.n_splits must be at least 2")
        if self.gap is not None and self.gap < 0:
            raise ValueError("config.gap must be non-negative")
        if self.oos_tail_days is not None and self.oos_tail_days < 0:
            raise ValueError("config.oos_tail_days must be non-negative")


def _opt_str(value: Any) -> Optional[str]:
    if value is None:
        return None
    text = str(value).strip()
    return text or None


def _opt_int(value: Any) -> Optional[int]:
    if value is None:
        return None
    return int(value)


# --- stdout line protocol --------------------------------------------------


def _emit(line: str) -> None:
    print(line, flush=True)


def _stage(name: str) -> None:
    _emit(f"STAGE:{name}")


def _progress(pct: int) -> None:
    _emit(f"PROGRESS:{max(0, min(100, int(pct)))}")


def _metric(payload: Dict[str, Any]) -> None:
    _emit("METRIC:" + json.dumps(payload, separators=(",", ":"), sort_keys=True))


def _artifact(kind: str, path: Path) -> None:
    _emit(f"ARTIFACT:{kind}:{path}")


def _note(message: str) -> None:
    _emit(f"STDERR: {message}")


# --- orchestration -------------------------------------------------------------


def _derive_output_stem(cfg: JobConfig, started: datetime.datetime) -> str:
    """NAME-01: ``{scope}_{timeframe}_{task}_{arch}_{feattag}_{yyyyMMdd-HHmmss}``.

    ``output_name`` (when the wizard supplied one, carrying the real scope label per
    NAME-02) wins verbatim. Otherwise the scope token is best-effort: the single
    symbol, or ``multi-<count>``.
    """
    if cfg.output_name:
        return cfg.output_name
    scope = cfg.symbols[0].strip().lower() if len(cfg.symbols) == 1 else f"multi-{len(cfg.symbols)}"
    feat = _FEATURE_TAGS.get(cfg.feature_mode, cfg.feature_mode)
    arch = cfg.architecture.strip().lower()
    return f"{scope}_{cfg.timeframe}_clf_{arch}_{feat}_{started:%Y%m%d-%H%M%S}"


def _resolve_dataset_dir(cfg: JobConfig, tmp_root: Path) -> Path:
    """Return the directory the trainer should read: the timeframe dir as-is, or a
    staged filtered copy when the scope narrows the symbol set / calendar range."""
    base = ds.resolve_timeframe_dir(cfg.timeframe)
    if not base.is_dir():
        raise FileNotFoundError(f"timeframe data directory not found: {base}")

    available = {p.stem.lower() for p in base.glob("*.parquet")}
    wanted = {s.strip().lower() for s in cfg.symbols}
    missing = sorted(wanted - available)
    if missing:
        _note(f"{len(missing)} requested symbol(s) have no parquet under {base}: {missing[:10]}")
    narrows_symbols = bool(wanted) and not wanted.issuperset(available)
    narrows_dates = bool(cfg.start_date or cfg.end_date)
    if not narrows_symbols and not narrows_dates:
        return base

    staged = tmp_root / "dataset"
    ds.materialize_filtered_dir(
        base,
        staged,
        symbols=cfg.symbols if narrows_symbols else None,
        start=cfg.start_date,
        end=cfg.end_date,
    )
    staged_count = len(list(staged.glob("*.parquet")))
    if staged_count == 0:
        raise SystemExit("no parquet files matched the requested scope / calendar range")
    _note(f"staged {staged_count} parquet file(s) for the requested scope -> {staged}")
    return staged


def _build_trainer_argv(cfg: JobConfig, data_dir: Path, out_path: Path) -> List[str]:
    argv = [
        "--data-dir", str(data_dir),
        "--feature-mode", cfg.feature_mode,
        "--window", str(cfg.window_size),
        "--horizon", str(cfg.horizon),
        "--wf-splits", str(cfg.n_splits),
        "--out", str(out_path),
    ]
    _, accepts_arch = _FRAMEWORKS[cfg.framework]
    if accepts_arch:
        argv += ["--arch", cfg.architecture.strip().lower()]
    for key, value in cfg.hyperparameters.items():
        flag = "--" + str(key).strip().lstrip("-").replace("_", "-")
        argv += [flag, str(value)]
    return argv


def _dispatch(cfg: JobConfig, argv: List[str]) -> int:
    module_name, _ = _FRAMEWORKS[cfg.framework]
    try:
        trainer = __import__(module_name)
    except ImportError as exc:
        raise SystemExit(
            f"training backend '{module_name}' is not available ({exc}); "
            "install the framework packages from the AI Predictions settings."
        ) from exc
    _note(f"dispatch {module_name}.main {argv}")
    return int(trainer.main(argv) or 0)


# --- post-training evaluation --------------------------------------------------
#
# The trainers train once on the *last* walk-forward fold and report one metric
# set. To surface per-fold behaviour and a fixed out-of-sample score without
# touching the trainer CLIs, this module reloads the dataset and scores the
# exported ONNX (inference only, class order Up/Down/Neutral) across every fold's
# validation block and, when requested, across the out-of-sample tail. A
# regression `target_type` is wiring-only here: the exported model is still a
# classifier, so these passes stay classification metrics.


def _onnx_predict_fn(onnx_path: Path):
    """Return ``predict(x[N,W,C]) -> probs[N,3]`` backed by onnxruntime.

    onnxruntime is imported lazily so importing this module (and running
    ``--selfcheck``) never depends on it.
    """
    import onnxruntime as ort  # lazy: not a hard dependency of this module

    session = ort.InferenceSession(str(onnx_path), providers=["CPUExecutionProvider"])

    def predict(x: np.ndarray) -> np.ndarray:
        (probs,) = session.run(["output"], {"input": np.asarray(x, dtype=np.float32)})
        return np.asarray(probs, dtype=np.float64)

    return predict


def evaluate_folds(
    predict,
    symbols: Dict[str, np.ndarray],
    *,
    feature_mode: str,
    window: int,
    horizon: int,
    threshold: float,
    n_splits: int,
    gap: Optional[int],
) -> List[Dict[str, float]]:
    """One flat metric dict per walk-forward fold, pooled over every symbol.

    Mirrors ``dataset.split_symbols_chronological``'s per-symbol windows +
    ``walk_forward_split`` (same default purge gap via ``dataset.resolve_purge_gap``)
    but keeps *every* fold's validation block instead of only the last, and scores
    it with ``predict``. Every value is a float so the C# ``METRIC:`` parser
    (``Dictionary<string,double>``) accepts it.
    """
    import metrics as _metrics  # sibling module; imported here to keep module import light

    resolved_gap = ds.resolve_purge_gap(window, horizon, gap)
    fold_true: List[List[np.ndarray]] = [[] for _ in range(n_splits)]
    fold_pred: List[List[np.ndarray]] = [[] for _ in range(n_splits)]
    fold_prob: List[List[np.ndarray]] = [[] for _ in range(n_splits)]

    for arr in symbols.values():
        x, y = ds.build_dataset(arr, feature_mode, window, horizon, threshold)
        if x.shape[0] == 0:
            continue
        folds = ds.walk_forward_split(x.shape[0], n_splits=n_splits, gap=resolved_gap)
        for k, (_train_idx, val_idx) in enumerate(folds):
            if val_idx.size == 0:
                continue
            probs = predict(x[val_idx])
            fold_true[k].append(y[val_idx])
            fold_pred[k].append(np.asarray(probs).argmax(axis=1))
            fold_prob[k].append(np.asarray(probs))

    rows: List[Dict[str, float]] = []
    for k in range(n_splits):
        if not fold_true[k]:
            continue
        yt = np.concatenate(fold_true[k])
        yp = np.concatenate(fold_pred[k])
        pp = np.concatenate(fold_prob[k])
        rep = _metrics.classification_report_dict(yt, yp, pp)
        logloss = rep["multi_logloss"]
        rows.append({
            "fold": float(k),
            "n_splits": float(n_splits),
            "fold_n": float(rep["n_samples"]),
            "fold_accuracy": float(rep["accuracy"]),
            "fold_macro_f1": float(rep["macro_f1"]),
            "fold_baseline_accuracy": float(rep["majority_baseline_accuracy"]),
            "fold_multi_logloss": float(logloss) if logloss is not None else float("nan"),
        })
    return rows


def evaluate_oos(
    predict,
    symbols: Dict[str, np.ndarray],
    dates: Dict[str, np.ndarray],
    *,
    feature_mode: str,
    window: int,
    horizon: int,
    threshold: float,
    oos_tail_days: Optional[int],
) -> Dict[str, float]:
    """Flat metric dict for the fixed out-of-sample tail, pooled over every symbol.

    Each symbol's rows are split by ``dataset.oos_split``; windows are built on the
    tail block alone (so a meaningful score needs ``oos_tail_days`` comfortably
    larger than ``window + horizon``). Returns ``{}`` when no tail is requested or
    no tail window can be formed.
    """
    if not oos_tail_days or oos_tail_days <= 0:
        return {}
    import metrics as _metrics

    yts: List[np.ndarray] = []
    yps: List[np.ndarray] = []
    pps: List[np.ndarray] = []
    for sym, arr in symbols.items():
        if sym not in dates:
            continue
        _main_arr, _main_d, oos_arr, _oos_d = ds.oos_split(arr, dates[sym], oos_tail_days)
        if oos_arr.shape[0] == 0:
            continue
        x, y = ds.build_dataset(oos_arr, feature_mode, window, horizon, threshold)
        if x.shape[0] == 0:
            continue
        probs = predict(x)
        yts.append(y)
        yps.append(np.asarray(probs).argmax(axis=1))
        pps.append(np.asarray(probs))

    if not yts:
        return {}
    yt = np.concatenate(yts)
    yp = np.concatenate(yps)
    pp = np.concatenate(pps)
    rep = _metrics.classification_report_dict(yt, yp, pp)
    logloss = rep["multi_logloss"]
    return {
        "oos_tail_days": float(oos_tail_days),
        "oos_n": float(rep["n_samples"]),
        "oos_accuracy": float(rep["accuracy"]),
        "oos_macro_f1": float(rep["macro_f1"]),
        "oos_baseline_accuracy": float(rep["majority_baseline_accuracy"]),
        "oos_multi_logloss": float(logloss) if logloss is not None else float("nan"),
    }


def _run_post_eval(cfg: JobConfig, data_dir: Path, out_path: Path) -> None:
    """Best-effort per-fold + out-of-sample scoring of the exported model.

    Emits one ``METRIC:`` line per fold (and one for the out-of-sample tail) so
    the wizard can show a fold table. Any failure here is logged and swallowed:
    the model and its aggregate metrics are already produced.
    """
    try:
        predict = _onnx_predict_fn(out_path)
    except Exception as exc:  # noqa: BLE001 - onnxruntime missing or model unreadable
        _note(f"skipping fold / OOS evaluation: {exc}")
        return

    try:
        symbols, dates = ds.load_parquet_dir(data_dir, return_dates=True)
    except Exception as exc:  # noqa: BLE001
        _note(f"skipping fold / OOS evaluation: could not reload dataset ({exc})")
        return

    common = dict(
        feature_mode=cfg.feature_mode, window=cfg.window_size,
        horizon=cfg.horizon, threshold=ds.DEFAULT_THRESHOLD,
    )
    try:
        for row in evaluate_folds(predict, symbols, n_splits=cfg.n_splits, gap=cfg.gap, **common):
            _metric(row)
    except Exception as exc:  # noqa: BLE001
        _note(f"fold evaluation failed: {exc}")

    if cfg.gap is not None:
        _note("note: --gap applies to the fold-evaluation split only; the trainer used its default purge gap")

    try:
        oos = evaluate_oos(predict, symbols, dates, oos_tail_days=cfg.oos_tail_days, **common)
        if oos:
            _metric(oos)
        elif cfg.oos_tail_days:
            _note(f"out-of-sample tail of {cfg.oos_tail_days} day(s) yielded no scorable window")
    except Exception as exc:  # noqa: BLE001
        _note(f"out-of-sample evaluation failed: {exc}")


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="GUI-triggered ONNX training orchestrator.")
    parser.add_argument(
        "--config", type=Path, required=True, help="Path to a TrainingJobConfig JSON file."
    )
    args = parser.parse_args(sys.argv[1:] if argv is None else argv)

    started = datetime.datetime.now()
    run_id = f"{started:%Y%m%d-%H%M%S}"

    _stage("load")
    _progress(0)
    config_path = Path(args.config)
    if not config_path.is_file():
        raise SystemExit(f"config file not found: {config_path}")
    cfg = JobConfig.from_json(config_path.read_text(encoding="utf-8"))
    _note(
        f"run_id={run_id} framework={cfg.framework} arch={cfg.architecture} "
        f"feature_mode={cfg.feature_mode} timeframe={cfg.timeframe} "
        f"symbols={len(cfg.symbols)} window={cfg.window_size} horizon={cfg.horizon} "
        f"target_type={cfg.target_type} n_splits={cfg.n_splits} "
        f"gap={cfg.gap} oos_tail_days={cfg.oos_tail_days}"
    )

    stem = _derive_output_stem(cfg, started)
    out_path = ARTIFACTS_DIR / f"{stem}.onnx"
    metrics_path = out_path.with_name(out_path.name + ".metrics.json")
    ARTIFACTS_DIR.mkdir(parents=True, exist_ok=True)

    tmp_root = Path(tempfile.mkdtemp(prefix="sa_train_"))
    try:
        _stage("dataset")
        _progress(5)
        data_dir = _resolve_dataset_dir(cfg, tmp_root)

        _stage("train")
        _progress(15)
        rc = _dispatch(cfg, _build_trainer_argv(cfg, data_dir, out_path))
        if rc != 0:
            _note(f"trainer exited {rc}")
            return rc

        _stage("export")
        _progress(88)
        if not out_path.is_file():
            _note(f"expected artifact not found after training: {out_path}")
            return 1
        _artifact("onnx", out_path)
        if metrics_path.is_file():
            _artifact("metrics", metrics_path)

        _stage("evaluate")
        _progress(92)
        _run_post_eval(cfg, data_dir, out_path)
    finally:
        shutil.rmtree(tmp_root, ignore_errors=True)

    # Emitted last so the C# orchestrator's "last METRIC wins" rule keeps the
    # aggregate report -- not a per-fold / OOS line -- as the run result metrics.
    if metrics_path.is_file():
        try:
            report = json.loads(metrics_path.read_text(encoding="utf-8"))
            flat = {k: float(v) for k, v in report.items() if isinstance(v, (int, float))}
            if flat:
                _metric(flat)
        except (OSError, ValueError) as exc:
            _note(f"could not summarize metrics: {exc}")

    _stage("done")
    _progress(100)
    return 0


def _run_selfcheck() -> None:
    """Assertions for the config mirror, trainer argv, and the pure evaluation
    passes. Uses a deterministic stub predictor so no ONNX / onnxruntime is
    needed."""
    # (a) JobConfig mirror: new keys round-trip and validate like the C# side.
    base = {"symbols": ["X"], "architecture": "lstm", "window_size": 20, "horizon": 3}
    cfg = JobConfig.from_json(json.dumps({
        **base, "target_type": "regression", "n_splits": 4, "gap": 7, "oos_tail_days": 90,
    }))
    assert (cfg.target_type, cfg.n_splits, cfg.gap, cfg.oos_tail_days) == ("regression", 4, 7, 90)
    defaults = JobConfig.from_json(json.dumps(base))
    assert defaults.target_type == "classification" and defaults.n_splits == ds.DEFAULT_WF_SPLITS
    assert defaults.gap is None and defaults.oos_tail_days is None
    for bad in ({"target_type": "ranking"}, {"n_splits": 1}, {"gap": -1}, {"oos_tail_days": -2}):
        try:
            JobConfig.from_json(json.dumps({**base, **bad}))
        except ValueError:
            pass
        else:  # pragma: no cover
            raise AssertionError(f"expected ValueError for {bad}")

    # (b) trainer argv carries the configured split count.
    argv = _build_trainer_argv(cfg, Path("d"), Path("o.onnx"))
    assert "--wf-splits" in argv and argv[argv.index("--wf-splits") + 1] == "4"

    # (c) evaluate_folds / evaluate_oos on synthetic symbols with a stub predictor.
    rng = np.random.default_rng(0)
    syms: Dict[str, np.ndarray] = {}
    dts: Dict[str, np.ndarray] = {}
    for name in ("A", "B"):
        n = 400
        close = 100.0 + np.cumsum(rng.normal(0, 1, n))
        a = np.empty((n, 5))
        a[:, 0] = close
        a[:, 1] = close + 1.0
        a[:, 2] = close - 1.0
        a[:, 3] = close
        a[:, 4] = rng.integers(1_000, 5_000, n)
        syms[name] = a
        dts[name] = np.datetime64("2018-01-01") + np.arange(n).astype("timedelta64[D]")

    def stub_predict(x: np.ndarray) -> np.ndarray:
        out = np.tile(np.array([0.2, 0.3, 0.5]), (x.shape[0], 1))
        return out

    common = dict(feature_mode="ohlcv_minmax", window=30, horizon=5, threshold=ds.DEFAULT_THRESHOLD)
    rows = evaluate_folds(stub_predict, syms, n_splits=5, gap=None, **common)
    assert rows, "expected at least one fold row"
    assert len(rows) <= 5
    for i, r in enumerate(rows):
        assert r["fold"] == float(i) and r["n_splits"] == 5.0
        assert set(r) == {
            "fold", "n_splits", "fold_n", "fold_accuracy", "fold_macro_f1",
            "fold_baseline_accuracy", "fold_multi_logloss",
        }
        assert all(isinstance(v, float) for v in r.values())
        assert 0.0 <= r["fold_accuracy"] <= 1.0 and r["fold_n"] > 0.0

    oos = evaluate_oos(stub_predict, syms, dts, oos_tail_days=120, **common)
    assert oos["oos_tail_days"] == 120.0 and oos["oos_n"] > 0.0
    assert set(oos) == {
        "oos_tail_days", "oos_n", "oos_accuracy", "oos_macro_f1",
        "oos_baseline_accuracy", "oos_multi_logloss",
    }
    assert evaluate_oos(stub_predict, syms, dts, oos_tail_days=None, **common) == {}
    assert evaluate_oos(stub_predict, syms, dts, oos_tail_days=3, **common) == {}  # tail too short for a window

    print("run_training.py selfcheck: OK")


if __name__ == "__main__":
    if "--selfcheck" in sys.argv[1:]:
        _run_selfcheck()
    else:
        raise SystemExit(main())
