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
preserved). This module dispatches to a trainer CLI (``train_pytorch.py`` /
``train_lightgbm.py`` / ``train_tensorflow.py``) in-process by calling its public
``main(argv)``, and, when a scope narrows the symbol set or the calendar range,
stages a filtered copy of the parquet directory via
``dataset.materialize_filtered_dir`` so the trainer trains on the subset.
Indicator-kind ``composed_features`` channels (Task8) are the one exception to
"the trainer CLIs are not modified": ``train_pytorch.py`` gained a new
``--indicator-channel-export-paths`` flag this module emits when
``JobConfig.indicator_channel_export_paths`` is set; ``train_lightgbm.py`` /
``train_tensorflow.py`` remain untouched (neither supports ``composed_features`` at
all yet, an unrelated pre-existing scope limit).

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
import onnx_meta  # noqa: E402  (sibling module, path inserted above)

# <DataRoot>/TrainingArtifacts, not a path relative to this script: this file lives under
# StockAnalyzer.Python/training/, which in a release build is copied beside the executable
# (see PythonScriptLocator) rather than being the app's actual Data root. dataset.DATA_ROOT
# already resolves the real Data root correctly in both dev and release (it is how the OHLCV
# parquet reads work), so reuse it here instead of introducing a second, divergent resolution
# scheme for this one output directory.
ARTIFACTS_DIR: Path = ds.DATA_ROOT / "TrainingArtifacts"

# framework wire string -> (trainer module name, accepts --arch).
_FRAMEWORKS: Dict[str, Tuple[str, bool]] = {
    "pytorch": ("train_pytorch", True),
    "lightgbm": ("train_lightgbm", False),
    "tensorflow": ("train_tensorflow", True),
}

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
    # composed-features channel list (mirror of C# Training.FeatureSpec), required when
    # feature_mode == ds.COMPOSED_FEATURES_MODE and must be None for every other mode.
    feature_spec: Optional[dict] = None
    # symbol -> exported Indicator-channel parquet path (mirror of C#
    # TrainingJobConfig.IndicatorChannelExportPaths, set by TrainingOrchestrator before this
    # module starts). None/empty unless feature_spec has an Indicator-kind channel.
    indicator_channel_export_paths: Optional[Dict[str, str]] = None
    hyperparameters: Dict[str, str] = dataclasses.field(default_factory=dict)
    start_date: Optional[str] = None
    end_date: Optional[str] = None
    output_name: Optional[str] = None
    # Orchestrator-assigned run identifier, threaded through from C# (see
    # TrainingOrchestrator.StartTrainingAsync). None only for a hand-written config used in a
    # manual/standalone invocation outside the C# orchestrator; main() falls back to a locally
    # derived timestamp in that case.
    run_id: Optional[str] = None
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
                feature_spec=raw.get("feature_spec"),
                indicator_channel_export_paths=_opt_str_dict(
                    raw.get("indicator_channel_export_paths")
                ),
                hyperparameters={
                    str(k): str(v) for k, v in (raw.get("hyperparameters") or {}).items()
                },
                start_date=_opt_str(raw.get("start_date")),
                end_date=_opt_str(raw.get("end_date")),
                output_name=_opt_str(raw.get("output_name")),
                run_id=_opt_str(raw.get("run_id")),
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
        if self.feature_mode == ds.COMPOSED_FEATURES_MODE:
            if not self.feature_spec or not self.feature_spec.get("channels"):
                raise ValueError(
                    "config.feature_spec: composed_features requires a non-empty channels list"
                )
            for ch in self.feature_spec["channels"]:
                if not isinstance(ch, dict) or ch.get("kind") not in ("price", "indicator"):
                    raise ValueError(
                        "config.feature_spec: each channel must have kind 'price' or 'indicator'"
                    )
                if ch.get("kind") == "price":
                    if ch.get("price") not in ds.COMPOSED_PRICE_TYPES:
                        raise ValueError(
                            f"config.feature_spec: unsupported price type {ch.get('price')!r}; "
                            f"expected one of {ds.COMPOSED_PRICE_TYPES} "
                            "(Heikin-Ashi price types are not yet supported)"
                        )
                else:  # "indicator"
                    # Existence/registration and non-default Params rejection are already
                    # enforced by C# (IndicatorChannelExporter / PredictionService) at export
                    # time -- not duplicated here, only the wire shape is checked.
                    if not isinstance(ch.get("indicator"), str) or not ch.get("indicator").strip():
                        raise ValueError(
                            "config.feature_spec: an 'indicator' kind channel requires a "
                            "non-empty 'indicator' name"
                        )
                normalization = ch.get("normalization", "none")
                if normalization not in ds.COMPOSED_NORMALIZATIONS:
                    raise ValueError(
                        f"config.feature_spec: unsupported normalization {normalization!r}; "
                        f"expected one of {ds.COMPOSED_NORMALIZATIONS}"
                    )
        else:
            if self.feature_spec is not None:
                raise ValueError(
                    "config.feature_spec must be null unless feature_mode is 'composed_features'"
                )
            if self.feature_mode != "ohlcv_minmax":
                raise ValueError(
                    "config.feature_mode: only 'ohlcv_minmax' is supported in this release"
                )
        if self.timeframe not in ds.TIMEFRAME_DIRS:
            raise ValueError(
                f"config.timeframe {self.timeframe!r} not in {sorted(ds.TIMEFRAME_DIRS)}"
            )
        # learning-objective wire strings; SSoT = onnx_meta.TARGET_TYPES (mirrors C# TargetType).
        if self.target_type not in onnx_meta.TARGET_TYPES:
            raise ValueError(
                f"config.target_type {self.target_type!r} not in {list(onnx_meta.TARGET_TYPES)}"
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


def _opt_str_dict(value: Any) -> Optional[Dict[str, str]]:
    if value is None:
        return None
    if not isinstance(value, dict):
        raise ValueError("expected an object (symbol -> path) but got a different JSON type")
    return {str(k): str(v) for k, v in value.items()}


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


def _derive_output_stem(cfg: JobConfig, run_id: str) -> str:
    """NAME-01: ``{scope}_{timeframe}_{task}_{arch}_{feattag}_{run_id}``.

    ``output_name`` (when the wizard supplied one, carrying the real scope label per
    NAME-02) wins verbatim. Otherwise the scope token is best-effort: the single
    symbol, or ``multi-<count>``.

    ``run_id`` (not a freshly-derived timestamp): the C#-orchestrator-assigned RunId is
    reused verbatim so the artifact filename, the experiment-log folder, and this run's
    stdout ``run_id=`` line all share one identifier end-to-end. It already carries its
    own uniqueness (timestamp + GUID suffix, see TrainingOrchestrator.StartTrainingAsync),
    so this function does not need to guard against same-instant collisions itself.
    """
    if cfg.output_name:
        return cfg.output_name
    scope = cfg.symbols[0].strip().lower() if len(cfg.symbols) == 1 else f"multi-{len(cfg.symbols)}"
    feat = _FEATURE_TAGS.get(cfg.feature_mode, cfg.feature_mode)
    arch = cfg.architecture.strip().lower()
    return f"{scope}_{cfg.timeframe}_clf_{arch}_{feat}_{run_id}"


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


# Cap on how many dropped-symbol names _resolve_training_dir names individually in its log
# line before collapsing the rest into a "(+N more)" suffix.
_MAX_DROPPED_SYMBOLS_LOGGED = 10


def _resolve_training_dir(cfg: JobConfig, raw_data_dir: Path, tmp_root: Path) -> Path:
    """Return the directory the trainer subprocess should read: *raw_data_dir* with
    any configured out-of-sample tail excluded first.

    Reserves ``cfg.oos_tail_days`` *before* training instead of only scoring it
    after the fact, so the block :func:`evaluate_oos` later reports on on the same
    ``raw_data_dir`` was never available for fitting or early stopping. Unset/``<=
    0`` returns *raw_data_dir* unchanged (no staging cost). Uses
    :func:`dataset.materialize_main_dir`, which applies the same per-symbol
    boundary :func:`dataset.oos_split` already uses for the post-hoc OOS score, so
    this does not introduce a new date-boundary convention.

    A symbol whose entire history falls inside the tail is left out of the staged
    set by :func:`dataset.materialize_main_dir`; the count and first few names of
    such dropped symbols are logged so the exclusion is not silent.
    """
    if not cfg.oos_tail_days or cfg.oos_tail_days <= 0:
        return raw_data_dir
    staged = tmp_root / "dataset_main"
    ds.materialize_main_dir(raw_data_dir, staged, oos_tail_days=cfg.oos_tail_days)
    staged_stems = {p.stem for p in staged.glob("*.parquet")}
    dropped = sorted(
        p.stem for p in raw_data_dir.glob("*.parquet") if p.stem not in staged_stems
    )
    if dropped:
        shown = dropped[:_MAX_DROPPED_SYMBOLS_LOGGED]
        suffix = f" (+{len(dropped) - len(shown)} more)" if len(dropped) > len(shown) else ""
        _note(
            f"{len(dropped)} symbol(s) had no rows left after excluding the "
            f"{cfg.oos_tail_days}d out-of-sample tail and were dropped from training: "
            f"{shown}{suffix}"
        )
    if not staged_stems:
        raise SystemExit(
            "out-of-sample tail exclusion left no parquet files; oos_tail_days is too "
            "large for the selected symbols/date range"
        )
    _note(f"excluded out-of-sample tail ({cfg.oos_tail_days}d) before training -> {staged}")
    return staged


# Flags this function itself emits. A hyperparameter reusing one of these would silently win
# ("last flag wins" is argparse's own rule for a repeated option) and override a wizard field
# the user set through its own UI control, so it is rejected instead -- see _build_trainer_argv.
# A future flag is added here in the same PR that wires it into the trainers, not before.
_RESERVED_TRAINER_FLAGS: frozenset = frozenset({
    "data-dir", "feature-mode", "feature-spec", "target-type", "window", "horizon", "wf-splits",
    "gap", "out", "arch", "indicator-channel-export-paths",
})


def _normalize_flag(key: object) -> str:
    """Turn a hyperparameter key into the CLI flag name it collides with / emits as.

    Shared by the reserved-flag conflict check and argv emission below so the two stay in
    lockstep by construction instead of by two independently maintained copies of the rule.
    """
    return str(key).strip().lstrip("-").replace("_", "-")


def _build_trainer_argv(cfg: JobConfig, data_dir: Path, out_path: Path) -> List[str]:
    argv = [
        "--data-dir", str(data_dir),
        "--feature-mode", cfg.feature_mode,
        "--window", str(cfg.window_size),
        "--horizon", str(cfg.horizon),
        "--wf-splits", str(cfg.n_splits),
    ]
    if cfg.feature_mode == ds.COMPOSED_FEATURES_MODE:
        argv += ["--feature-spec", json.dumps(cfg.feature_spec)]
        if cfg.indicator_channel_export_paths:
            argv += [
                "--indicator-channel-export-paths",
                json.dumps(cfg.indicator_channel_export_paths),
            ]
    # Only forwarded for a non-default objective: train_lightgbm.py / train_tensorflow.py do not
    # define --target-type yet (T04 regression is PyTorch-only this release, D04), so omitting
    # the flag for the classification default keeps their argv byte-identical to before this
    # change. A regression request against either of those two frameworks still fails fast --
    # argparse itself rejects the unrecognized --target-type flag -- rather than silently
    # training a classifier.
    if cfg.target_type != "classification":
        argv += ["--target-type", cfg.target_type]
    if cfg.gap is not None:
        argv += ["--gap", str(cfg.gap)]
    argv += ["--out", str(out_path)]
    _, accepts_arch = _FRAMEWORKS[cfg.framework]
    if accepts_arch:
        argv += ["--arch", cfg.architecture.strip().lower()]

    conflicts = sorted(
        key for key in cfg.hyperparameters
        if _normalize_flag(key) in _RESERVED_TRAINER_FLAGS
    )
    if conflicts:
        raise SystemExit(
            f"config.hyperparameters reuse reserved trainer flag(s) {conflicts}; "
            "set these via the wizard's own Window/Horizon/Splits/Gap/Architecture fields instead"
        )
    for key, value in cfg.hyperparameters.items():
        flag = "--" + _normalize_flag(key)
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

    The output is ``[N,3]`` class probabilities for a classification model, or a
    raw continuous ``[N,1]`` value for a regression model (see ``evaluate_folds``
    / ``evaluate_oos``'s ``target_type`` branch, which reshapes the latter to
    ``[N]`` before scoring).

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
    feature_spec: dict | None = None,
    target_type: str = "classification",
    dates: Dict[str, np.ndarray] | None = None,
    indicator_export_paths: Dict[str, str] | None = None,
) -> List[Dict[str, float]]:
    """One flat metric dict per walk-forward fold, pooled over every symbol.

    Mirrors ``dataset.split_symbols_chronological``'s per-symbol windows +
    ``walk_forward_split`` (same default purge gap via ``dataset.resolve_purge_gap``)
    but keeps *every* fold's validation block instead of only the last, and scores
    it with ``predict``. Every value is a float so the C# ``METRIC:`` parser
    (``Dictionary<string,double>``) accepts it.

    ``predict`` wraps a *single* exported model, trained once on the last fold's
    train block (see ``train_pytorch.py`` / ``train_lightgbm.py`` /
    ``train_tensorflow.py``: they call ``dataset.split_symbols_chronological``,
    which keeps only ``walk_forward_split``'s final fold). Folds ``0 ..
    n_splits - 2`` here reuse that one model outside its own train/validation
    split -- their test blocks fall inside the rows the deployed model was
    actually fit on, so those rows are a same-model reference score, not an
    independent per-fold cross-validation result. Only fold ``n_splits - 1``'s
    test block matches the model's own held-out validation split (``fold_is_holdout
    = 1.0``); every earlier fold is ``fold_is_holdout = 0.0``. Call *symbols* with
    any out-of-sample tail already excluded (see ``_run_post_eval``) so these
    scores don't double as the separate ``evaluate_oos`` result.

    ``dates``/``indicator_export_paths`` (both keyed by symbol) are forwarded per-symbol to
    ``dataset.build_dataset``; only meaningful when *feature_spec* has an Indicator-kind
    channel, in which case *dates* must be the same date column as the *symbols* array passed
    in (the caller's own history slice, e.g. ``_run_post_eval``'s OOS-excluded main block --
    not necessarily the exported parquet's full history; see ``dataset._build_indicator_columns``).
    """
    import metrics as _metrics  # sibling module; imported here to keep module import light

    is_regression = target_type == "regression"
    resolved_gap = ds.resolve_purge_gap(window, horizon, gap)
    fold_true: List[List[np.ndarray]] = [[] for _ in range(n_splits)]
    fold_pred: List[List[np.ndarray]] = [[] for _ in range(n_splits)]
    fold_prob: List[List[np.ndarray]] = [[] for _ in range(n_splits)]

    for sym, arr in symbols.items():
        x, y = ds.build_dataset(
            arr, feature_mode, window, horizon, threshold, feature_spec=feature_spec, target_type=target_type,
            dates=dates.get(sym) if dates is not None else None,
            indicator_export_path=(
                indicator_export_paths.get(sym) if indicator_export_paths is not None else None
            ),
        )
        if x.shape[0] == 0:
            continue
        folds = ds.walk_forward_split(x.shape[0], n_splits=n_splits, gap=resolved_gap)
        for k, (_train_idx, val_idx) in enumerate(folds):
            if val_idx.size == 0:
                continue
            probs = predict(x[val_idx])
            if is_regression:
                fold_true[k].append(y[val_idx].reshape(-1))
                fold_pred[k].append(np.asarray(probs).reshape(-1))
            else:
                fold_true[k].append(y[val_idx])
                fold_pred[k].append(np.asarray(probs).argmax(axis=1))
                fold_prob[k].append(np.asarray(probs))

    rows: List[Dict[str, float]] = []
    for k in range(n_splits):
        if not fold_true[k]:
            continue
        yt = np.concatenate(fold_true[k])
        yp = np.concatenate(fold_pred[k])
        if is_regression:
            rep = _metrics.regression_metrics(yt, yp)
            rows.append({
                "fold": float(k),
                "n_splits": float(n_splits),
                "fold_n": float(rep["n_samples"]),
                "fold_rmse": float(rep["rmse"]),
                "fold_mae": float(rep["mae"]),
                "fold_directional_accuracy": float(rep["directional_accuracy"]),
                "fold_rmse_baseline": float(rep["rmse_zero_baseline"]),
                "fold_is_holdout": 1.0 if k == n_splits - 1 else 0.0,
            })
        else:
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
                "fold_is_holdout": 1.0 if k == n_splits - 1 else 0.0,
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
    feature_spec: dict | None = None,
    target_type: str = "classification",
    indicator_export_paths: Dict[str, str] | None = None,
) -> Dict[str, float]:
    """Flat metric dict for the fixed out-of-sample tail, pooled over every symbol.

    Each symbol's rows are split by ``dataset.oos_split``; windows are built on the
    tail block alone (so a meaningful score needs ``oos_tail_days`` comfortably
    larger than ``window + horizon``). Returns ``{}`` when no tail is requested or
    no tail window can be formed.

    ``indicator_export_paths`` (keyed by symbol) is forwarded per-symbol to
    ``dataset.build_dataset`` alongside the OOS tail's own ``oos_d`` date slice (from
    ``dataset.oos_split``) -- only meaningful when *feature_spec* has an Indicator-kind
    channel. ``dataset._build_indicator_columns`` looks each requested date up by value in
    the (full-history) export parquet, so a tail-only ``oos_d`` subset resolves correctly
    without needing the export's full row count/order (see Task8's redesign).
    """
    if not oos_tail_days or oos_tail_days <= 0:
        return {}
    import metrics as _metrics

    is_regression = target_type == "regression"
    yts: List[np.ndarray] = []
    yps: List[np.ndarray] = []
    pps: List[np.ndarray] = []
    for sym, arr in symbols.items():
        if sym not in dates:
            continue
        _main_arr, _main_d, oos_arr, oos_d = ds.oos_split(arr, dates[sym], oos_tail_days)
        if oos_arr.shape[0] == 0:
            continue
        x, y = ds.build_dataset(
            oos_arr, feature_mode, window, horizon, threshold, feature_spec=feature_spec, target_type=target_type,
            dates=oos_d,
            indicator_export_path=(
                indicator_export_paths.get(sym) if indicator_export_paths is not None else None
            ),
        )
        if x.shape[0] == 0:
            continue
        probs = predict(x)
        if is_regression:
            yts.append(y.reshape(-1))
            yps.append(np.asarray(probs).reshape(-1))
        else:
            yts.append(y)
            yps.append(np.asarray(probs).argmax(axis=1))
            pps.append(np.asarray(probs))

    if not yts:
        return {}
    yt = np.concatenate(yts)
    yp = np.concatenate(yps)
    if is_regression:
        rep = _metrics.regression_metrics(yt, yp)
        return {
            "oos_tail_days": float(oos_tail_days),
            "oos_n": float(rep["n_samples"]),
            "oos_rmse": float(rep["rmse"]),
            "oos_mae": float(rep["mae"]),
            "oos_directional_accuracy": float(rep["directional_accuracy"]),
            "oos_rmse_baseline": float(rep["rmse_zero_baseline"]),
        }
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
    the model and its aggregate metrics are already produced. *data_dir* is the
    full range (out-of-sample tail included): the tail is carved back out here,
    once, via ``dataset.oos_split`` -- for the fold pass so it stays excluded like
    it was for the trainer (see ``_resolve_training_dir``), and for the OOS pass so
    it has something to score.
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

    main_symbols = symbols
    main_dates = dates
    if cfg.oos_tail_days and cfg.oos_tail_days > 0:
        main_symbols = {}
        main_dates = {}
        for sym, arr in symbols.items():
            main_arr, main_d, _oos_arr, _oos_d = ds.oos_split(arr, dates[sym], cfg.oos_tail_days)
            main_symbols[sym] = main_arr
            main_dates[sym] = main_d

    common = dict(
        feature_mode=cfg.feature_mode, window=cfg.window_size,
        horizon=cfg.horizon, threshold=ds.DEFAULT_THRESHOLD, feature_spec=cfg.feature_spec,
        target_type=cfg.target_type,
    )
    # main_dates is the OOS-excluded main block's own date slice (row-aligned with
    # main_symbols), not the full history's dates -- required so an Indicator-kind channel's
    # export lookup (dataset._build_indicator_columns) resolves against the same rows the
    # fold pass actually scores.
    try:
        for row in evaluate_folds(
            predict, main_symbols, n_splits=cfg.n_splits, gap=cfg.gap,
            dates=main_dates, indicator_export_paths=cfg.indicator_channel_export_paths, **common
        ):
            _metric(row)
    except Exception as exc:  # noqa: BLE001
        _note(f"fold evaluation failed: {exc}")

    _note(
        "note: fold scores reuse one model trained once on the last fold "
        "(fold_is_holdout=0 folds are a same-model reference score, not independent "
        "cross-validation; only the fold_is_holdout=1 fold matches the model's own "
        "held-out validation split)"
    )
    # (No longer a caveat: _build_trainer_argv forwards cfg.gap to the trainer subprocess as
    # --gap, which all 3 trainers thread into the same split_symbols_chronological /
    # train_val_date_ranges calls this fold pass uses -- confirmed by an end-to-end run per
    # framework whose exported ONNX producer metadata recorded the same gap value passed here.)

    try:
        oos = evaluate_oos(
            predict, symbols, dates, oos_tail_days=cfg.oos_tail_days,
            indicator_export_paths=cfg.indicator_channel_export_paths, **common
        )
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

    _stage("load")
    _progress(0)
    config_path = Path(args.config)
    if not config_path.is_file():
        raise SystemExit(f"config file not found: {config_path}")
    cfg = JobConfig.from_json(config_path.read_text(encoding="utf-8"))
    # Prefer the orchestrator-assigned RunId (threaded through via the wire config) so C# and
    # Python share one identifier end-to-end for the experiment-log folder, this log line, and
    # the artifact filename stem. Falls back to a locally-derived timestamp only for a
    # hand-written config that omits run_id (manual/standalone invocation outside the C#
    # orchestrator).
    run_id = cfg.run_id or f"{started:%Y%m%d-%H%M%S}{started.microsecond // 1000:03d}"
    _note(
        f"run_id={run_id} framework={cfg.framework} arch={cfg.architecture} "
        f"feature_mode={cfg.feature_mode} timeframe={cfg.timeframe} "
        f"symbols={len(cfg.symbols)} window={cfg.window_size} horizon={cfg.horizon} "
        f"target_type={cfg.target_type} n_splits={cfg.n_splits} "
        f"gap={cfg.gap} oos_tail_days={cfg.oos_tail_days}"
    )

    stem = _derive_output_stem(cfg, run_id)
    out_path = ARTIFACTS_DIR / f"{stem}.onnx"
    metrics_path = out_path.with_name(out_path.name + ".metrics.json")
    ARTIFACTS_DIR.mkdir(parents=True, exist_ok=True)

    tmp_root = Path(tempfile.mkdtemp(prefix="sa_train_"))
    try:
        _stage("dataset")
        _progress(5)
        data_dir = _resolve_dataset_dir(cfg, tmp_root)
        # Trainer sees the out-of-sample tail excluded; evaluate() below still needs
        # data_dir (the full range) to score that withheld tail post-hoc.
        train_dir = _resolve_training_dir(cfg, data_dir, tmp_root)

        _stage("train")
        _progress(15)
        rc = _dispatch(cfg, _build_trainer_argv(cfg, train_dir, out_path))
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

    # (a2) composed_features (Price-only): a valid channel list round-trips; every invalid
    # shape (unsupported/Heikin-Ashi price type, bad normalization, empty channel list, or a
    # feature_spec attached to a non-composed mode) is rejected up front.
    composed_spec = {"channels": [
        {"kind": "price", "price": "close", "normalization": "none"},
        {"kind": "price", "price": "high", "normalization": "window_min_max"},
    ]}
    composed_cfg = JobConfig.from_json(json.dumps({
        **base, "feature_mode": ds.COMPOSED_FEATURES_MODE, "feature_spec": composed_spec,
    }))
    assert composed_cfg.feature_spec == composed_spec
    for bad_spec in (
        {"channels": []},
        {"channels": [{"kind": "heikin_ashi", "indicator": "Rsi"}]},
        {"channels": [{"kind": "price", "price": "heikin_ashi_close"}]},
        {"channels": [{"kind": "price", "price": "close", "normalization": "bogus"}]},
        {"channels": [{"kind": "indicator"}]},
        {"channels": [{"kind": "indicator", "indicator": ""}]},
        {"channels": [{"kind": "indicator", "indicator": "   "}]},
        {"channels": [{"kind": "indicator", "indicator": "Rsi", "normalization": "bogus"}]},
    ):
        try:
            JobConfig.from_json(json.dumps({
                **base, "feature_mode": ds.COMPOSED_FEATURES_MODE, "feature_spec": bad_spec,
            }))
        except ValueError:
            pass
        else:  # pragma: no cover
            raise AssertionError(f"expected ValueError for feature_spec {bad_spec}")
    try:
        JobConfig.from_json(json.dumps({**base, "feature_spec": composed_spec}))
    except ValueError:
        pass
    else:  # pragma: no cover
        raise AssertionError("expected ValueError for feature_spec attached to a non-composed feature_mode")

    # (a3) Task8: an Indicator-kind channel is no longer rejected by validate() -- existence /
    # registration / non-default Params are C#'s responsibility (already enforced at export
    # time), not re-validated here. indicator_channel_export_paths round-trips as a
    # symbol -> path dict, and is None when omitted (existing config JSON with no knowledge of
    # this field must keep working unchanged).
    indicator_spec = {"channels": [
        {"kind": "price", "price": "close", "normalization": "none"},
        {"kind": "indicator", "indicator": "Rsi", "normalization": "none"},
    ]}
    indicator_export_paths = {"X": "/tmp/sa_indicator_channels_run1_X.parquet"}
    indicator_cfg = JobConfig.from_json(json.dumps({
        **base, "feature_mode": ds.COMPOSED_FEATURES_MODE, "feature_spec": indicator_spec,
        "indicator_channel_export_paths": indicator_export_paths,
    }))
    assert indicator_cfg.feature_spec == indicator_spec
    assert indicator_cfg.indicator_channel_export_paths == indicator_export_paths
    assert composed_cfg.indicator_channel_export_paths is None

    # (b) trainer argv carries the configured split count and gap; gap is omitted when unset;
    # a hyperparameter reusing a reserved flag name is rejected, not silently overridden.
    argv = _build_trainer_argv(cfg, Path("d"), Path("o.onnx"))
    assert "--wf-splits" in argv and argv[argv.index("--wf-splits") + 1] == "4"
    assert "--gap" in argv and argv[argv.index("--gap") + 1] == "7"

    # _build_trainer_argv's gap handling is a pure argv-construction contract, and the
    # end-to-end purge-gap propagation fixed in T02 rides on it: an unset gap emits no
    # --gap at all (the trainer then falls back to dataset.resolve_purge_gap's default),
    # and every explicit non-negative gap -- including the falsy-but-valid 0 -- is
    # forwarded exactly once as "--gap <n>". Enumerated here so a future edit to the
    # builder cannot silently drop or duplicate the flag.
    for _gap in (None, 0, 5, 42):
        _gap_cfg = JobConfig.from_json(
            json.dumps(base if _gap is None else {**base, "gap": _gap})
        )
        _gap_argv = _build_trainer_argv(_gap_cfg, Path("d"), Path("o.onnx"))
        if _gap is None:
            assert "--gap" not in _gap_argv, _gap_argv
        else:
            assert _gap_argv.count("--gap") == 1, _gap_argv
            assert _gap_argv[_gap_argv.index("--gap") + 1] == str(_gap), _gap_argv

    conflict_cfg = JobConfig.from_json(json.dumps({**base, "hyperparameters": {"data_dir": "/evil"}}))
    try:
        _build_trainer_argv(conflict_cfg, Path("d"), Path("o.onnx"))
    except SystemExit:
        pass
    else:  # pragma: no cover
        raise AssertionError("expected SystemExit for a hyperparameter reusing a reserved flag")

    # (b2) --feature-spec is forwarded to the trainer argv only for composed_features, carrying
    # the exact JSON of cfg.feature_spec; every other mode omits the flag entirely. A
    # hyperparameter reusing "feature-spec"/"feature_spec" is rejected like any other reserved
    # flag, not silently appended twice.
    assert "--feature-spec" not in argv, argv  # `cfg` above is ohlcv_minmax (non-composed)
    composed_argv = _build_trainer_argv(composed_cfg, Path("d"), Path("o.onnx"))
    assert "--feature-spec" in composed_argv, composed_argv
    assert json.loads(composed_argv[composed_argv.index("--feature-spec") + 1]) == composed_spec
    fs_conflict_cfg = JobConfig.from_json(json.dumps({**base, "hyperparameters": {"feature_spec": "x"}}))
    try:
        _build_trainer_argv(fs_conflict_cfg, Path("d"), Path("o.onnx"))
    except SystemExit:
        pass
    else:  # pragma: no cover
        raise AssertionError("expected SystemExit for a hyperparameter reusing the feature-spec flag")

    ok_cfg = JobConfig.from_json(json.dumps({**base, "hyperparameters": {"lr": "0.01"}}))
    ok_argv = _build_trainer_argv(ok_cfg, Path("d"), Path("o.onnx"))
    assert "--lr" in ok_argv and ok_argv[ok_argv.index("--lr") + 1] == "0.01"

    # (b4) Task8: --indicator-channel-export-paths is forwarded to the trainer argv only when
    # composed_features AND a non-empty indicator_channel_export_paths is configured; every
    # other combination (Price-only composed_features, or the field simply unset) omits the
    # flag entirely, so a Price-only run's argv stays byte-identical to before Task8. A
    # hyperparameter reusing the flag name is rejected like any other reserved flag.
    assert "--indicator-channel-export-paths" not in composed_argv, composed_argv
    indicator_argv = _build_trainer_argv(indicator_cfg, Path("d"), Path("o.onnx"))
    assert "--indicator-channel-export-paths" in indicator_argv, indicator_argv
    assert json.loads(
        indicator_argv[indicator_argv.index("--indicator-channel-export-paths") + 1]
    ) == indicator_export_paths
    iep_conflict_cfg = JobConfig.from_json(json.dumps({
        **base, "hyperparameters": {"indicator_channel_export_paths": "x"},
    }))
    try:
        _build_trainer_argv(iep_conflict_cfg, Path("d"), Path("o.onnx"))
    except SystemExit:
        pass
    else:  # pragma: no cover
        raise AssertionError(
            "expected SystemExit for a hyperparameter reusing the "
            "indicator-channel-export-paths flag"
        )

    # (b3) T04: --target-type is forwarded to the trainer argv only for a non-default objective
    # (train_lightgbm.py / train_tensorflow.py do not define the flag yet -- D04 keeps T04
    # PyTorch-only this release -- so the classification default must stay byte-identical to
    # before this change). `cfg` above has target_type="regression".
    assert "--target-type" in argv and argv[argv.index("--target-type") + 1] == "regression"
    classification_argv = _build_trainer_argv(defaults, Path("d"), Path("o.onnx"))
    assert "--target-type" not in classification_argv, classification_argv
    tt_conflict_cfg = JobConfig.from_json(json.dumps({**base, "hyperparameters": {"target_type": "regression"}}))
    try:
        _build_trainer_argv(tt_conflict_cfg, Path("d"), Path("o.onnx"))
    except SystemExit:
        pass
    else:  # pragma: no cover
        raise AssertionError("expected SystemExit for a hyperparameter reusing the target-type flag")

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
            "fold_baseline_accuracy", "fold_multi_logloss", "fold_is_holdout",
        }
        assert all(isinstance(v, float) for v in r.values())
        assert 0.0 <= r["fold_accuracy"] <= 1.0 and r["fold_n"] > 0.0
        # only the model's own validation fold (the last one, index n_splits - 1) is
        # an independent holdout; every earlier fold reuses that same model's score.
        assert r["fold_is_holdout"] == (1.0 if int(r["fold"]) == 4 else 0.0)
    assert sum(r["fold_is_holdout"] for r in rows) == 1.0, "expected exactly one holdout fold"

    oos = evaluate_oos(stub_predict, syms, dts, oos_tail_days=120, **common)
    assert oos["oos_tail_days"] == 120.0 and oos["oos_n"] > 0.0
    assert set(oos) == {
        "oos_tail_days", "oos_n", "oos_accuracy", "oos_macro_f1",
        "oos_baseline_accuracy", "oos_multi_logloss",
    }
    assert evaluate_oos(stub_predict, syms, dts, oos_tail_days=None, **common) == {}
    assert evaluate_oos(stub_predict, syms, dts, oos_tail_days=3, **common) == {}  # tail too short for a window

    # (c2) evaluate_folds / evaluate_oos also work for composed_features once feature_spec is
    # forwarded -- this is the T02-era gap Task7 closes: without it, a composed_features run's
    # post-training fold/OOS metrics table would silently come back empty (both callers swallow
    # their own exceptions), even though training and export already succeeded.
    composed_common = dict(
        feature_mode=ds.COMPOSED_FEATURES_MODE, window=30, horizon=5,
        threshold=ds.DEFAULT_THRESHOLD, feature_spec=composed_spec,
    )
    composed_rows = evaluate_folds(stub_predict, syms, n_splits=5, gap=None, **composed_common)
    assert composed_rows, "expected at least one fold row for composed_features"
    composed_oos = evaluate_oos(stub_predict, syms, dts, oos_tail_days=120, **composed_common)
    assert composed_oos["oos_tail_days"] == 120.0 and composed_oos["oos_n"] > 0.0

    # (c3) T04: evaluate_folds / evaluate_oos with target_type="regression" score a raw [N,1]
    # continuous prediction via metrics.regression_metrics instead of argmax'ing it into a
    # class index -- this is the gap Task6.2 closes (the real E2E run in Task6.1 hit exactly
    # this: "fold evaluation failed: probs shape (214, 1) != (214, 3)").
    def stub_predict_regression(x: np.ndarray) -> np.ndarray:
        return np.full((x.shape[0], 1), 0.01)

    regression_common = dict(
        feature_mode="ohlcv_minmax", window=30, horizon=5,
        threshold=ds.DEFAULT_THRESHOLD, target_type="regression",
    )
    reg_rows = evaluate_folds(stub_predict_regression, syms, n_splits=5, gap=None, **regression_common)
    assert reg_rows, "expected at least one regression fold row"
    for i, r in enumerate(reg_rows):
        assert r["fold"] == float(i) and r["n_splits"] == 5.0
        assert set(r) == {
            "fold", "n_splits", "fold_n", "fold_rmse", "fold_mae",
            "fold_directional_accuracy", "fold_rmse_baseline", "fold_is_holdout",
        }
        assert "fold_accuracy" not in r and "fold_macro_f1" not in r
        assert r["fold_rmse"] >= 0.0 and r["fold_n"] > 0.0
    assert sum(r["fold_is_holdout"] for r in reg_rows) == 1.0

    reg_oos = evaluate_oos(stub_predict_regression, syms, dts, oos_tail_days=120, **regression_common)
    assert reg_oos["oos_tail_days"] == 120.0 and reg_oos["oos_n"] > 0.0
    assert set(reg_oos) == {
        "oos_tail_days", "oos_n", "oos_rmse", "oos_mae",
        "oos_directional_accuracy", "oos_rmse_baseline",
    }
    assert "oos_accuracy" not in reg_oos and "oos_macro_f1" not in reg_oos
    assert evaluate_oos(stub_predict_regression, syms, dts, oos_tail_days=None, **regression_common) == {}

    # (c4) Task8: evaluate_folds / evaluate_oos thread dates/indicator_export_paths through to
    # dataset.build_dataset for an Indicator-kind composed_features channel -- exercising the
    # real dataset._build_indicator_columns date-lookup path (not a stub), including
    # evaluate_oos's OOS-tail-only date slice (dataset.oos_split's `oos_d`, previously discarded
    # before Task8).
    import polars as _ind_pl

    indicator_channels_spec = {"channels": [
        {"kind": "price", "price": "close", "normalization": "none"},
        {"kind": "indicator", "indicator": "Rsi", "normalization": "none"},
    ]}
    with tempfile.TemporaryDirectory() as _ind_tmpdir:
        ind_export_paths: Dict[str, str] = {}
        for name in ("A", "B"):
            path = Path(_ind_tmpdir) / f"sa_indicator_channels_selfcheck_{name}.parquet"
            _ind_pl.DataFrame({
                "date": _ind_pl.Series(dts[name]).cast(_ind_pl.Date),
                # An arbitrary deterministic per-symbol series standing in for a real Rsi
                # export -- evaluate_folds/evaluate_oos only need it to be finite and
                # date-aligned, never re-derive indicator math themselves.
                "channel_1": syms[name][:, 3] / 2.0,
            }).write_parquet(path)
            ind_export_paths[name] = str(path)

        indicator_eval_common = dict(
            feature_mode=ds.COMPOSED_FEATURES_MODE, window=30, horizon=5,
            threshold=ds.DEFAULT_THRESHOLD, feature_spec=indicator_channels_spec,
        )
        ind_rows = evaluate_folds(
            stub_predict, syms, n_splits=5, gap=None,
            dates=dts, indicator_export_paths=ind_export_paths, **indicator_eval_common,
        )
        assert ind_rows, "expected at least one fold row for an Indicator-kind channel"

        ind_oos = evaluate_oos(
            stub_predict, syms, dts, oos_tail_days=120,
            indicator_export_paths=ind_export_paths, **indicator_eval_common,
        )
        assert ind_oos["oos_tail_days"] == 120.0 and ind_oos["oos_n"] > 0.0

        # A symbol missing from indicator_export_paths surfaces as that symbol's own
        # ValueError from dataset._build_indicator_columns (fail closed), not a silent skip.
        try:
            evaluate_folds(
                stub_predict, syms, n_splits=5, gap=None,
                dates=dts, indicator_export_paths={"A": ind_export_paths["A"]},
                **indicator_eval_common,
            )
            raise AssertionError("expected ValueError for a symbol missing its indicator export path")
        except ValueError:
            pass

    # (d) _resolve_training_dir: excludes the OOS tail before training (mirrors
    # dataset.oos_split's boundary via dataset.materialize_main_dir), and is a
    # cheap no-op (returns raw_data_dir itself, no staging) when unset.
    with tempfile.TemporaryDirectory() as _tmp:
        _tmpdir = Path(_tmp)
        import polars as _pl
        _dates = _pl.Series(dts["A"]).cast(_pl.Date)
        _pl.DataFrame({
            "date": _dates,
            "open": syms["A"][:, 0], "high": syms["A"][:, 1], "low": syms["A"][:, 2],
            "close": syms["A"][:, 3], "volume": syms["A"][:, 4],
        }).write_parquet(_tmpdir / "A.parquet")
        _no_oos_cfg = JobConfig.from_json(json.dumps({**base, "oos_tail_days": None}))
        assert _resolve_training_dir(_no_oos_cfg, _tmpdir, Path(tempfile.mkdtemp())) == _tmpdir
        _oos_cfg = JobConfig.from_json(json.dumps({**base, "oos_tail_days": 60}))
        _train_dir = _resolve_training_dir(_oos_cfg, _tmpdir, Path(tempfile.mkdtemp()))
        assert _train_dir != _tmpdir
        _trained_rows = ds.load_parquet_dir(_train_dir)["A"].shape[0]
        _main_expect, _, _, _ = ds.oos_split(syms["A"], dts["A"], 60)
        assert _trained_rows == _main_expect.shape[0]
        assert _trained_rows < syms["A"].shape[0], "OOS tail must actually shrink the training set"

        # A symbol whose whole history is inside its own OOS tail is dropped from the staged
        # set (and logged), not carried through empty.
        _pl.DataFrame({
            "date": _pl.Series(
                np.datetime64("2019-06-01") + np.arange(20).astype("timedelta64[D]")
            ).cast(_pl.Date),
            "open": np.full(20, 10.0), "high": np.full(20, 11.0), "low": np.full(20, 9.0),
            "close": np.full(20, 10.0), "volume": np.full(20, 1_000.0),
        }).write_parquet(_tmpdir / "SHORT.parquet")
        _train_dir_drop = _resolve_training_dir(
            JobConfig.from_json(json.dumps({**base, "oos_tail_days": 60})),
            _tmpdir, Path(tempfile.mkdtemp()),
        )
        _staged_stems = {p.stem for p in _train_dir_drop.glob("*.parquet")}
        assert "A" in _staged_stems and "SHORT" not in _staged_stems, _staged_stems

    print("run_training.py selfcheck: OK")


if __name__ == "__main__":
    if "--selfcheck" in sys.argv[1:]:
        _run_selfcheck()
    else:
        raise SystemExit(main())
