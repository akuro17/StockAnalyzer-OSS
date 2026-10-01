"""GUI-triggered ONNX training orchestrator.

Reads a ``TrainingJobConfig`` JSON document -- the contract defined by
``StockAnalyzer.Core/Models/Training/TrainingJobConfig.cs`` and serialized by
``TrainingConfigJson`` -- dispatches to the matching trainer in-process, and
streams a line protocol on stdout for the C# ``ITrainingOrchestrator``:

    STAGE:<name>            pipeline phase entered: load, dataset, train, export, done
    PROGRESS:<0-100>        coarse overall percent
    METRIC:<json>           a flat metrics dict (the final aggregated report)
    ARTIFACT:<kind>:<path>  a produced file; kind is ``onnx``, ``metrics`` or ``scaler``

Every other diagnostic line is prefixed ``STDERR:`` (kept on stdout so ordering is
preserved). This module dispatches to a trainer CLI (``train_pytorch.py`` /
``train_lightgbm.py`` / ``train_tensorflow.py``) in-process by calling its public
``main(argv)``, and, when a scope narrows the symbol set or the calendar range,
stages a filtered copy of the parquet directory via
``dataset.materialize_filtered_dir`` so the trainer trains on the subset.
Indicator-kind ``composed_features`` channels use C#-exported values through
``--indicator-channel-export-paths`` for all supported classification trainers.
Weekly/Monthly indicator jobs opt into ``prepared_input_dir``, a run-owned OHLCV
projection already validated against a provider finality manifest by C#.

    python run_training.py --config job.json
"""

from __future__ import annotations

import argparse
import contextlib
import dataclasses
import datetime
import hashlib
import json
import math
import os
import shutil
import sys
import tempfile
import time
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import dataset as ds  # noqa: E402  (sibling module, path inserted above)
import fixed_scaler  # noqa: E402
import metrics  # noqa: E402
import onnx_meta  # noqa: E402  (sibling module, path inserted above)
import checkpoints  # noqa: E402

# <DataRoot>/TrainingArtifacts, not a path relative to this script: this file lives under
# StockAnalyzer.Python/training/, which in a release build is copied beside the executable
# (see PythonScriptLocator) rather than being the app's actual Data root. dataset.DATA_ROOT
# already resolves the real Data root correctly in both dev and release (it is how the OHLCV
# parquet reads work), so reuse it here instead of introducing a second, divergent resolution
# scheme for this one output directory.
ARTIFACTS_DIR: Path = ds.DATA_ROOT / "TrainingArtifacts"
MAX_EVALUATION_FOLDS = ds._env_int("SA_MAX_EVALUATION_FOLDS", 10)
MAX_RUN_DURATION_SECONDS = ds._env_int("SA_MAX_RUN_DURATION_SECONDS", 120 * 60)

_RESOURCE_ENV = {
    "contract_version": "SA_TRAINING_RESOURCE_CONTRACT_VERSION",
    "max_channels": "SA_MAX_COMPOSED_CHANNELS",
    "max_tensor_size_mib": "SA_MAX_COMPOSED_TENSOR_SIZE_MB",
    "max_samples": "SA_MAX_COMPOSED_BATCH_SAMPLES",
    "max_evaluation_folds": "SA_MAX_EVALUATION_FOLDS",
    "max_feature_lag": "SA_MAX_FEATURE_LAG",
    "max_ensemble_members": "SA_MAX_ENSEMBLE_MEMBERS",
    "max_run_duration_seconds": "SA_MAX_RUN_DURATION_SECONDS",
}


def _effective_resource_limits() -> dict[str, int]:
    return {
        "contract_version": 1,
        "max_channels": ds.MAX_COMPOSED_CHANNELS,
        "max_tensor_size_mib": ds.MAX_COMPOSED_TENSOR_SIZE_MB,
        "max_samples": ds.MAX_COMPOSED_BATCH_SAMPLES,
        "max_evaluation_folds": MAX_EVALUATION_FOLDS,
        "max_feature_lag": fixed_scaler.MAX_LAG,
        "max_ensemble_members": ds._env_int("SA_MAX_ENSEMBLE_MEMBERS", 8),
        "max_run_duration_seconds": MAX_RUN_DURATION_SECONDS,
        "max_tensor_elements": ds.MAX_COMPOSED_TENSOR_SIZE_MB * 1024 * 1024 // 4,
    }


def _validate_resource_limits(limits: dict | None, budget: float | None) -> None:
    if limits is None:
        if budget is not None or os.environ.get(_RESOURCE_ENV["contract_version"]) is not None:
            raise ValueError("managed training resource contract is missing")
        return
    expected = _effective_resource_limits()
    if (not isinstance(limits, dict) or set(limits) != set(expected)
            or any(type(limits[key]) is not int or limits[key] != value
                   for key, value in expected.items())):
        raise ValueError("training resource contract disagrees with effective limits")
    for key, env_name in _RESOURCE_ENV.items():
        if os.environ.get(env_name) != str(limits[key]):
            raise ValueError(f"training resource environment disagrees at {key}")
    if (type(budget) not in (int, float) or not math.isfinite(budget)
            or budget <= 0 or budget > limits["max_run_duration_seconds"]):
        raise ValueError("remaining training run budget is invalid")

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
    fixed_zscore: bool = False
    clip_sigma: Optional[float] = None
    lags: List[int] = dataclasses.field(default_factory=list)
    # symbol -> exported Indicator-channel parquet path (mirror of C#
    # TrainingJobConfig.IndicatorChannelExportPaths, set by TrainingOrchestrator before this
    # module starts). None/empty unless feature_spec has an Indicator-kind channel.
    indicator_channel_export_paths: Optional[Dict[str, str]] = None
    source_manifest_path: Optional[str] = None
    prepared_input_dir: Optional[str] = None
    source_provenance: Optional[dict] = None
    hyperparameters: Dict[str, str] = dataclasses.field(default_factory=dict)
    start_date: Optional[str] = None
    end_date: Optional[str] = None
    output_name: Optional[str] = None
    # Orchestrator-assigned run identifier, threaded through from C# (see
    # TrainingOrchestrator.StartTrainingAsync). None only for a hand-written config used in a
    # manual/standalone invocation outside the C# orchestrator; main() falls back to a locally
    # derived timestamp in that case.
    run_id: Optional[str] = None
    resource_limits: Optional[dict] = None
    remaining_run_budget_seconds: Optional[float] = None
    # validation / target-definition contract (mirror of C# TrainingJobConfig).
    target_type: str = "classification"
    n_splits: int = ds.DEFAULT_WF_SPLITS
    gap: Optional[int] = None
    oos_tail_days: Optional[int] = None
    initialization_mode: str = "fresh"
    checkpoint_path: Optional[str] = None
    parent_model_id: Optional[str] = None
    parent_model_hash: Optional[str] = None
    freeze_paths: List[str] = dataclasses.field(default_factory=list)

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
                fixed_zscore=raw.get("fixed_zscore", False),
                clip_sigma=raw.get("clip_sigma"),
                lags=raw.get("lags", []),
                indicator_channel_export_paths=_opt_str_dict(
                    raw.get("indicator_channel_export_paths")
                ),
                source_manifest_path=_opt_str(raw.get("source_manifest_path")),
                prepared_input_dir=_opt_str(raw.get("prepared_input_dir")),
                source_provenance=raw.get("source_provenance"),
                hyperparameters={
                    str(k): str(v) for k, v in (raw.get("hyperparameters") or {}).items()
                },
                start_date=_opt_str(raw.get("start_date")),
                end_date=_opt_str(raw.get("end_date")),
                output_name=_opt_str(raw.get("output_name")),
                run_id=_opt_str(raw.get("run_id")),
                resource_limits=raw.get("resource_limits"),
                remaining_run_budget_seconds=raw.get("remaining_run_budget_seconds"),
                target_type=str(raw.get("target_type", "classification")).strip().lower(),
                n_splits=int(raw.get("n_splits", ds.DEFAULT_WF_SPLITS)),
                gap=_opt_int(raw.get("gap")),
                oos_tail_days=_opt_int(raw.get("oos_tail_days")),
                initialization_mode=raw.get("initialization_mode", "fresh"),
                checkpoint_path=raw.get("checkpoint_path"),
                parent_model_id=raw.get("parent_model_id"),
                parent_model_hash=raw.get("parent_model_hash"),
                freeze_paths=raw.get("freeze_paths", []),
            )
        except KeyError as exc:
            raise ValueError(f"config is missing required key: {exc}") from exc
        cfg.validate()
        return cfg

    def validate(self) -> None:
        checkpoints.validate_controls(self.initialization_mode, self.framework, self.checkpoint_path,
                                      self.parent_model_id, self.parent_model_hash, self.freeze_paths)
        _validate_resource_limits(self.resource_limits, self.remaining_run_budget_seconds)
        if type(self.fixed_zscore) is not bool:
            raise ValueError("config.fixed_zscore must be boolean")
        if self.fixed_zscore and self.feature_mode not in ("ohlcv_minmax", ds.COMPOSED_FEATURES_MODE):
            raise ValueError("fixed z-score supports raw OHLCV or composed features")
        fixed_scaler.validate_lags(self.lags)
        composed_lags = fixed_scaler.validate_lags(
            self.feature_spec.get("lags", []) if self.feature_spec is not None else []
        )
        if self.feature_mode == ds.COMPOSED_FEATURES_MODE and self.lags:
            raise ValueError("composed lags belong in feature_spec.lags")
        effective_lags = composed_lags if self.feature_mode == ds.COMPOSED_FEATURES_MODE else self.lags
        if not self.fixed_zscore and (self.clip_sigma is not None or effective_lags):
            raise ValueError("clipping and lags require fixed z-score")
        if self.clip_sigma is not None and (
            type(self.clip_sigma) not in (int, float) or not math.isfinite(self.clip_sigma)
            or self.clip_sigma <= 0
        ):
            raise ValueError("clip_sigma must be finite and positive")
        if self.fixed_zscore and self.feature_spec and any(
            channel.get("normalization", "none") != "none"
            for channel in self.feature_spec.get("channels", [])
        ):
            raise ValueError("fixed z-score cannot follow per-window channel normalization")
        if self.prepared_input_dir and not (
            self.timeframe in ("weekly", "monthly")
            and self.feature_mode == ds.COMPOSED_FEATURES_MODE
            and self.feature_spec
            and any(ch.get("kind") == "indicator" for ch in self.feature_spec.get("channels", []))
        ):
            raise ValueError("prepared_input_dir requires weekly/monthly composed indicator training")
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
        if self.target_type == "regression" and self.framework != "pytorch":
            raise ValueError("regression training currently requires pytorch")
        if self.n_splits < 2:
            raise ValueError("config.n_splits must be at least 2")
        if self.n_splits > MAX_EVALUATION_FOLDS:
            raise ValueError(f"config.n_splits must not exceed {MAX_EVALUATION_FOLDS}")
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
    if cfg.prepared_input_dir:
        return _resolve_prepared_dataset_dir(cfg, tmp_root)
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


def _resolve_prepared_dataset_dir(cfg: JobConfig, tmp_root: Path) -> Path:
    """Use only the C#-validated, run-owned OHLCV projection for an opted-in job."""
    base = Path(cfg.prepared_input_dir).resolve()
    if not base.is_dir():
        raise FileNotFoundError(f"prepared training input directory not found: {base}")
    available = {path.stem.lower() for path in base.glob("*.parquet")}
    wanted = {symbol.strip().lower() for symbol in cfg.symbols}
    missing = sorted(wanted - available)
    if missing:
        raise FileNotFoundError(f"prepared training input is missing symbols: {missing}")
    if available == wanted and not cfg.start_date and not cfg.end_date:
        return base
    staged = tmp_root / "dataset"
    ds.materialize_filtered_dir(base, staged, symbols=cfg.symbols,
                                start=cfg.start_date, end=cfg.end_date)
    if not list(staged.glob("*.parquet")):
        raise SystemExit("no prepared parquet rows matched the calendar range")
    return staged


def _resolve_training_dir(cfg: JobConfig, raw_data_dir: Path, tmp_root: Path) -> Path:
    """Stage data through one selected-symbol calendar boundary before fitting.

    Every symbol ends at the earliest selected-symbol last date. A configured
    OOS tail is then excluded by the same cutoff for every symbol. Missing main
    rows fail the run rather than silently changing its symbol set.
    """
    symbols, dates = ds.load_parquet_dir(raw_data_dir, return_dates=True)
    if not symbols:
        raise ValueError("selected training data has no symbols")
    available = {symbol.lower() for symbol in symbols}
    missing = sorted(symbol for symbol in cfg.symbols if symbol.lower() not in available)
    if missing:
        raise ValueError(f"selected training symbols have no data: {missing}")
    common_end = ds.common_end_date(dates)
    if not cfg.oos_tail_days and all(
        np.asarray(d).astype("datetime64[D]")[-1] == np.datetime64(common_end)
        for d in dates.values()
    ):
        return raw_data_dir
    staged = tmp_root / "dataset_main"
    ds.materialize_main_dir(raw_data_dir, staged, oos_tail_days=cfg.oos_tail_days or 0,
                            common_end=common_end)
    staged_stems = {p.stem for p in staged.glob("*.parquet")}
    dropped = sorted(
        p.stem for p in raw_data_dir.glob("*.parquet") if p.stem not in staged_stems
    )
    if dropped:
        raise ValueError(f"selected symbols lack training rows at the common boundary: {dropped}")
    if not staged_stems:
        raise SystemExit(
            "out-of-sample tail exclusion left no parquet files; oos_tail_days is too "
            "large for the selected symbols/date range"
        )
    _note(f"common end={common_end}, out-of-sample tail={cfg.oos_tail_days or 0}d -> {staged}")
    return staged


# Flags this function itself emits. A hyperparameter reusing one of these would silently win
# ("last flag wins" is argparse's own rule for a repeated option) and override a wizard field
# the user set through its own UI control, so it is rejected instead -- see _build_trainer_argv.
# A future flag is added here in the same PR that wires it into the trainers, not before.
_RESERVED_TRAINER_FLAGS: frozenset = frozenset({
    "data-dir", "feature-mode", "feature-spec", "target-type", "window", "horizon", "timeframe", "wf-splits",
    "gap", "out", "arch", "indicator-channel-export-paths", "outer-fold-index",
    "max-symbols", "smoke",
    "fixed-zscore", "clip-sigma", "lags",
    "initialization-mode", "init-from", "checkpoint-dir", "run-id",
    "parent-model-id", "parent-model-hash", "freeze-paths",
    "preflight",
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
        "--timeframe", cfg.timeframe,
        "--wf-splits", str(cfg.n_splits),
    ]
    if cfg.feature_mode == ds.COMPOSED_FEATURES_MODE:
        argv += ["--feature-spec", json.dumps(cfg.feature_spec)]
        if cfg.indicator_channel_export_paths:
            argv += [
                "--indicator-channel-export-paths",
                json.dumps(cfg.indicator_channel_export_paths),
            ]
    if cfg.fixed_zscore:
        argv += ["--fixed-zscore"]
        if cfg.clip_sigma is not None:
            argv += ["--clip-sigma", repr(float(cfg.clip_sigma))]
        if cfg.feature_mode != ds.COMPOSED_FEATURES_MODE:
            argv += ["--lags", json.dumps(cfg.lags)]
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


# --- independent outer-fold evaluation ---------------------------------------
# Each trainer invocation receives one original outer fold index, fits only its
# purged train side, and exports a model that is scored on that fold's test side.
# The final fold's scored model is published as the release candidate.


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
    metadata = session.get_modelmeta().custom_metadata_map
    scaler = fixed_scaler.load_sidecar(onnx_path, metadata) if fixed_scaler.requires_fixed_scaler(metadata) else None

    def predict(x: np.ndarray) -> np.ndarray:
        tensor = fixed_scaler.transform(x, scaler) if scaler is not None else np.asarray(x, dtype=np.float32)
        (probs,) = session.run(["output"], {"input": tensor})
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
    fold_index: int | None = None,
    fixed_zscore: bool = False,
    lags: list[int] | None = None,
) -> List[Dict[str, float]]:
    """One flat metric dict per walk-forward fold, pooled over every symbol.

    With ``fold_index`` set, the predictor must be the model fitted for that
    original fold; the data selector applies the identical cross-symbol purge
    used by the trainer and the returned row is an independent holdout score.
    Without it, this remains a descriptive same-model reference helper for
    older callers. Pass OOS-excluded symbols to avoid duplicate evaluation.
    """
    import metrics as _metrics  # sibling module; imported here to keep module import light

    is_regression = target_type == "regression"
    resolved_gap = ds.resolve_purge_gap(window, horizon, gap)
    fold_true: List[List[np.ndarray]] = [[] for _ in range(n_splits)]
    fold_pred: List[List[np.ndarray]] = [[] for _ in range(n_splits)]
    fold_prob: List[List[np.ndarray]] = [[] for _ in range(n_splits)]
    independent = None
    if fold_index is not None:
        if dates is None:
            raise ValueError("independent fold evaluation requires dates")
        independent = ds.independent_fold_data(
            symbols, dates, feature_mode=feature_mode, window=window, horizon=horizon,
            threshold=threshold, n_splits=n_splits, gap=gap, fold_index=fold_index,
            feature_spec=feature_spec, target_type=target_type,
            indicator_export_paths=indicator_export_paths,
            fixed_zscore=fixed_zscore, lags=lags,
        )

    for sym, arr in symbols.items():
        if independent is not None:
            x, y, _anchors, _train_idx, val_idx = independent[sym]
            folds = [(fold_index, val_idx)]
        else:
            x, y = ds.build_dataset(
            arr, feature_mode, window, horizon, threshold, feature_spec=feature_spec, target_type=target_type,
            dates=dates.get(sym) if dates is not None else None,
            indicator_export_path=(
                indicator_export_paths.get(sym) if indicator_export_paths is not None else None
            ),
            fixed_zscore=fixed_zscore, lags=lags,
            )
            if x.shape[0] == 0:
                continue
            folds = [(k, val) for k, _train, val in
                     ds.walk_forward_split_indexed(x.shape[0], n_splits=n_splits, gap=resolved_gap)]
        for k, val_idx in folds:
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
                "fold_is_holdout": 1.0 if fold_index is not None or k == n_splits - 1 else 0.0,
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
                "fold_is_holdout": 1.0 if fold_index is not None or k == n_splits - 1 else 0.0,
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
    common_end: datetime.date | None = None,
    fixed_zscore: bool = False,
    lags: list[int] | None = None,
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
            if common_end is not None:
                raise ValueError(f"out-of-sample dates missing for selected symbol {sym!r}")
            continue
        _main_arr, _main_d, oos_arr, oos_d = ds.oos_split(
            arr, dates[sym], oos_tail_days, common_end=common_end)
        if oos_arr.shape[0] == 0:
            if common_end is not None:
                raise ValueError(f"out-of-sample tail empty for selected symbol {sym!r}")
            continue
        x, y = ds.build_dataset(
            oos_arr, feature_mode, window, horizon, threshold, feature_spec=feature_spec, target_type=target_type,
            dates=oos_d,
            indicator_export_path=(
                indicator_export_paths.get(sym) if indicator_export_paths is not None else None
            ),
            fixed_zscore=fixed_zscore, lags=lags,
        )
        if x.shape[0] == 0:
            if common_end is not None:
                raise ValueError(f"out-of-sample window unavailable for selected symbol {sym!r}")
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


def _file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _publish_release_bundle(release_path: Path, staged_metrics: Path,
                            release_scaler: Path | None, out_path: Path,
                            expected_report: dict, release_analysis: Path | None = None) -> None:
    """Copy a scored bundle beside its destination, then commit with local recovery copies."""
    out_path.parent.mkdir(parents=True, exist_ok=True)
    metrics_path = out_path.with_name(out_path.name + ".metrics.json")
    scaler_path = out_path.with_name(out_path.name + ".scaler.json")
    analysis_path = out_path.with_name(out_path.name + ".analysis.json")
    stage = Path(tempfile.mkdtemp(prefix=f".{out_path.stem}_publish_", dir=out_path.parent))
    preserve_recovery = False
    try:
        incoming = stage / "incoming"
        previous = stage / "previous"
        incoming.mkdir()
        previous.mkdir()
        model_copy = incoming / out_path.name
        metrics_copy = incoming / metrics_path.name
        shutil.copy2(release_path, model_copy)
        shutil.copy2(staged_metrics, metrics_copy)
        scaler_copy = None
        if release_scaler is not None:
            scaler_copy = incoming / scaler_path.name
            shutil.copy2(release_scaler, scaler_copy)
        analysis_copy = None
        if release_analysis is not None:
            analysis_copy = incoming / analysis_path.name
            shutil.copy2(release_analysis, analysis_copy)

        model_hash = _file_sha256(model_copy)
        if (model_copy.stat().st_size == 0 or model_hash != _file_sha256(release_path)
                or model_hash != expected_report.get("scored_model_sha256")):
            raise ValueError("selected release model disagrees with its independent score evidence")
        report_copy = json.loads(metrics_copy.read_text(encoding="utf-8"))
        if (report_copy != expected_report or report_copy.get("evaluation_status") != "independent_outer"
                or report_copy.get("scored_model_sha256") != model_hash):
            raise ValueError("staged metrics disagree with the selected release model")
        if scaler_copy is not None:
            scaler_document = json.loads(scaler_copy.read_text(encoding="utf-8"))
            if (not isinstance(scaler_document, dict)
                    or scaler_document.get("schema_version") != fixed_scaler.SCHEMA_VERSION
                    or not scaler_document.get("feature_contract_hash")
                    or _file_sha256(scaler_copy) != _file_sha256(release_scaler)):
                raise ValueError("staged scaler is invalid or differs from the selected model sidecar")

        if analysis_copy is not None:
            analysis_document = json.loads(analysis_copy.read_text(encoding="utf-8"))
            if (analysis_document.get("model_sha256") != model_hash
                    or analysis_document.get("data_revision") != expected_report.get("data_revision")
                    or _file_sha256(analysis_copy) != _file_sha256(release_analysis)):
                raise ValueError("staged analysis disagrees with the selected model evidence")
        finals = (out_path, metrics_path, scaler_path, analysis_path)
        backups = {path: previous / path.name for path in finals if path.exists()}
        for path, backup in backups.items():
            shutil.copy2(path, backup)
            if _file_sha256(path) != _file_sha256(backup):
                raise OSError(f"prior artifact backup changed while copying: {path}")

        attempted: list[Path] = []
        try:
            operations = [(metrics_copy, metrics_path)]
            if scaler_copy is not None:
                operations.append((scaler_copy, scaler_path))
            if analysis_copy is not None:
                operations.append((analysis_copy, analysis_path))
            operations.append((model_copy, out_path))
            for source, destination in operations:
                attempted.append(destination)
                os.replace(source, destination)
            if scaler_copy is None and scaler_path.exists():
                attempted.append(scaler_path)
                os.replace(scaler_path, stage / "retired.scaler.json")
            if analysis_copy is None and analysis_path.exists():
                attempted.append(analysis_path)
                os.replace(analysis_path, stage / "retired.analysis.json")
        except OSError as publish_error:
            restoration_errors = []
            for destination in reversed(attempted):
                try:
                    if destination in backups:
                        restore_copy = stage / ("restore_" + destination.name)
                        shutil.copy2(backups[destination], restore_copy)
                        os.replace(restore_copy, destination)
                    else:
                        destination.unlink(missing_ok=True)
                except OSError as restore_error:
                    restoration_errors.append(f"{destination}: {restore_error}")
            if restoration_errors:
                preserve_recovery = True
                raise RuntimeError(
                    f"release publication failed; recovery copies remain in {previous}: "
                    + "; ".join(restoration_errors)) from publish_error
            raise
    finally:
        if not preserve_recovery:
            if stage.resolve().parent != out_path.parent.resolve():
                raise RuntimeError("refusing to remove publication stage outside artifact directory")
            try:
                shutil.rmtree(stage)
            except OSError as cleanup_error:
                _note(f"publication stage cleanup failed at {stage}: {cleanup_error}")


def _evaluation_revision(cfg: JobConfig, symbols: Dict[str, np.ndarray],
                         dates: Dict[str, np.ndarray], fold_index: int) -> str:
    """Hash data, target and evaluated observations under the outer_v2 contract.

    The version separates corrected lag purging from older outer_v1 evidence.
    Feature/scaler choices identify artifacts elsewhere, not the evaluation set.
    """
    digest = hashlib.sha256()
    contract = {
        "metric_definition": "outer_v2", "timeframe": cfg.timeframe,
        "target_type": cfg.target_type, "window": cfg.window_size,
        "horizon": cfg.horizon, "threshold": ds.DEFAULT_THRESHOLD,
        "n_splits": cfg.n_splits,
        "gap": ds.resolve_purge_gap(cfg.window_size, cfg.horizon, cfg.gap),
        "oos_tail_days": cfg.oos_tail_days, "fold": fold_index,
    }
    digest.update(json.dumps(contract, sort_keys=True, separators=(",", ":")).encode("utf-8"))
    selected = ds.independent_fold_data(
        symbols, dates, feature_mode=cfg.feature_mode, window=cfg.window_size,
        horizon=cfg.horizon, threshold=ds.DEFAULT_THRESHOLD, n_splits=cfg.n_splits,
        gap=cfg.gap, fold_index=fold_index, feature_spec=cfg.feature_spec,
        target_type=cfg.target_type,
        indicator_export_paths=cfg.indicator_channel_export_paths,
        fixed_zscore=cfg.fixed_zscore, lags=cfg.lags,
    )
    for sym in sorted(symbols):
        digest.update(sym.encode("utf-8"))
        digest.update(np.ascontiguousarray(symbols[sym], dtype="<f8").tobytes())
        d = np.asarray(dates[sym]).astype("datetime64[D]")
        digest.update(np.ascontiguousarray(d.astype("<i8")).tobytes())
        _x, y, anchors, _train, test = selected[sym]
        digest.update(np.ascontiguousarray(anchors[test], dtype="<i8").tobytes())
        digest.update(np.ascontiguousarray(y[test]).tobytes())
    return digest.hexdigest()


def main(argv: Optional[List[str]] = None) -> int:
    started_at = time.monotonic()
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
    run_limits = dict(cfg.resource_limits) if cfg.resource_limits is not None else _effective_resource_limits()
    run_budget_seconds = (cfg.remaining_run_budget_seconds if cfg.remaining_run_budget_seconds is not None
                          else MAX_RUN_DURATION_SECONDS)
    deadline = started_at + run_budget_seconds
    # Prefer the orchestrator-assigned RunId (threaded through via the wire config) so C# and
    # Python share one identifier end-to-end for the experiment-log folder, this log line, and
    # the artifact filename stem. Falls back to a locally-derived timestamp only for a
    # hand-written config that omits run_id (manual/standalone invocation outside the C#
    # orchestrator).
    resumed = checkpoints.read_json(Path(cfg.checkpoint_path)) if cfg.initialization_mode == "resume" else None
    if resumed is not None and (resumed.get("kind") != "training_run"
            or not isinstance(resumed.get("run_id"), str)
            or not checkpoints._RUN.fullmatch(resumed["run_id"])):
        raise ValueError("Resume requires a native training run manifest")
    run_id = (resumed["run_id"] if resumed else cfg.run_id) or f"{started:%Y%m%d-%H%M%S}{started.microsecond // 1000:03d}"
    if not checkpoints._RUN.fullmatch(run_id) or (resumed and cfg.run_id and cfg.run_id != run_id):
        raise ValueError("unsafe or mismatched run identifier")
    _note(
        f"run_id={run_id} framework={cfg.framework} arch={cfg.architecture} "
        f"feature_mode={cfg.feature_mode} timeframe={cfg.timeframe} "
        f"symbols={len(cfg.symbols)} window={cfg.window_size} horizon={cfg.horizon} "
        f"target_type={cfg.target_type} n_splits={cfg.n_splits} "
        f"gap={cfg.gap} oos_tail_days={cfg.oos_tail_days}"
    )
    _note(f"resolved training resource limits: {run_limits}")

    stem = _derive_output_stem(cfg, run_id)
    out_path = ARTIFACTS_DIR / f"{stem}.onnx"
    metrics_path = out_path.with_name(out_path.name + ".metrics.json")
    ARTIFACTS_DIR.mkdir(parents=True, exist_ok=True)

    tmp_root = Path(tempfile.mkdtemp(prefix="sa_train_"))
    ownership = contextlib.ExitStack()
    try:
        checkpoint_root = (Path(cfg.checkpoint_path).resolve().parent if resumed else
                           ARTIFACTS_DIR / "checkpoints" / run_id)
        if cfg.framework != "tensorflow":
            ownership.enter_context(checkpoints.run_lock(checkpoint_root))
        _stage("dataset")
        _progress(5)
        data_dir = _resolve_dataset_dir(cfg, tmp_root)
        train_dir = _resolve_training_dir(cfg, data_dir, tmp_root)
        train_symbols, train_dates = ds.load_parquet_dir(train_dir, return_dates=True)
        full_symbols, full_dates = ds.load_parquet_dir(data_dir, return_dates=True)
        if cfg.framework != "tensorflow":
            job_contract = dataclasses.asdict(cfg)
            for key in ("initialization_mode", "checkpoint_path", "parent_model_id", "parent_model_hash",
                        "freeze_paths", "run_id", "remaining_run_budget_seconds", "resource_limits",
                        "prepared_input_dir", "source_manifest_path", "indicator_channel_export_paths", "output_name"):
                job_contract.pop(key, None)
            job_contract["hyperparameters"] = {
                _normalize_flag(key): value for key, value in cfg.hyperparameters.items()
                if _normalize_flag(key) != "epochs"}
            if job_contract.get("source_provenance"):
                job_contract["source_provenance"].pop("as_of_utc", None)
            revision = checkpoints.source_revision(full_symbols, full_dates, cfg.indicator_channel_export_paths)
            manifest_path = checkpoint_root / "run.json"
            if resumed:
                if resumed.get("job_contract") != job_contract or resumed.get("data_revision") != revision:
                    raise ValueError("Resume requires identical run configuration and data revision")
                started_folds = resumed.get("started_folds")
                if (not isinstance(started_folds, list) or len(set(started_folds)) != len(started_folds)
                        or any(type(k) is not int or not 0 <= k < cfg.n_splits for k in started_folds)):
                    raise ValueError("Resume fold execution journal is missing or invalid")
                for fold_index in range(cfg.n_splits):
                    if fold_index in started_folds and not (checkpoint_root / f"fold_{fold_index}" / "last.json").exists():
                        raise ValueError("Resume state is missing for a started fold")
                    for slot in ("last", "best"):
                        pointer = checkpoint_root / f"fold_{fold_index}" / (slot + ".json")
                        if pointer.exists():
                            checkpoints.checked_blob(pointer)
                            import train_pytorch
                            train_pytorch.validate_checkpoint(pointer)
            else:
                if manifest_path.exists():
                    raise ValueError("new training cannot overwrite an existing run; use Resume")
                initialization = {"mode": cfg.initialization_mode, "checkpoint_path": cfg.checkpoint_path,
                                  "parent_model_id": cfg.parent_model_id, "parent_model_hash": cfg.parent_model_hash,
                                  "freeze_paths": cfg.freeze_paths}
                resumed = dict(schema_version=checkpoints.SCHEMA_VERSION, kind="training_run", run_id=run_id,
                               job_contract=job_contract, data_revision=revision, initialization=initialization,
                               started_folds=[])
                checkpoints.atomic_json(manifest_path, resumed)
            _artifact("checkpoint", manifest_path)
        common = dict(
            feature_mode=cfg.feature_mode, window=cfg.window_size,
            horizon=cfg.horizon, threshold=ds.DEFAULT_THRESHOLD,
            feature_spec=cfg.feature_spec, target_type=cfg.target_type,
            fixed_zscore=cfg.fixed_zscore, lags=cfg.lags,
        )
        release_path = None
        release_report = None
        fold_reports = []
        fold_commands = []
        for fold_index in range(cfg.n_splits):
            if time.monotonic() >= deadline:
                raise TimeoutError("independent evaluation exceeded the run duration limit")
            # Validate the original fold and the inner fit/validation boundary before
            # invoking a framework. A missing selected symbol is a failed run.
            selected_inner = ds.independent_fold_data(
                train_symbols, train_dates, n_splits=cfg.n_splits, gap=cfg.gap,
                fold_index=fold_index,
                indicator_export_paths=cfg.indicator_channel_export_paths,
                inner=True, **common,
            )
            if fold_index == cfg.n_splits - 1:
                import model_analysis
                model_analysis.validate_population(selected_inner, run_limits)
            staged_model = tmp_root / f"fold_{fold_index}" / out_path.name
            staged_model.parent.mkdir(parents=True, exist_ok=True)
            trainer_argv = _build_trainer_argv(cfg, train_dir, staged_model)
            trainer_argv += ["--outer-fold-index", str(fold_index)]
            if cfg.framework != "tensorflow":
                fold_root = checkpoint_root / f"fold_{fold_index}"
                last = fold_root / "last.json"
                initialization = resumed["initialization"]
                mode = "resume" if cfg.initialization_mode == "resume" and last.exists() else initialization["mode"]
                trainer_argv += ["--initialization-mode", mode, "--checkpoint-dir", str(fold_root),
                                 "--run-id", run_id]
                if mode == "resume":
                    parent, _ = checkpoints.checked_blob(last)
                    trainer_argv += ["--init-from", str(last),
                                     "--freeze-paths", json.dumps(parent["context"]["freeze_paths"])]
                elif mode == "fine_tune":
                    trainer_argv += ["--init-from", initialization["checkpoint_path"],
                                     "--parent-model-id", initialization["parent_model_id"],
                                     "--parent-model-hash", initialization["parent_model_hash"],
                                     "--freeze-paths", json.dumps(initialization["freeze_paths"])]
            fold_commands.append((fold_index, staged_model, trainer_argv))
            if cfg.framework != "tensorflow":
                _stage("preflight")
                if _dispatch(cfg, trainer_argv + ["--preflight"]) != 0:
                    raise ValueError(f"checkpoint preflight failed for fold {fold_index}")
        for fold_index, staged_model, trainer_argv in fold_commands:
            if time.monotonic() >= deadline:
                raise TimeoutError("checkpoint preflight exceeded the run duration limit")
            _stage("train")
            _progress(15 + (fold_index * 70 // cfg.n_splits))
            if cfg.framework != "tensorflow" and fold_index not in resumed["started_folds"]:
                resumed["started_folds"].append(fold_index)
                checkpoints.atomic_json(manifest_path, resumed)
            rc = _dispatch(cfg, trainer_argv)
            if rc != 0 or not staged_model.is_file():
                raise RuntimeError(f"trainer failed to produce outer fold {fold_index} (exit {rc})")
            if time.monotonic() >= deadline:
                raise TimeoutError("independent evaluation exceeded the run duration limit")
            _stage("evaluate")
            rows = evaluate_folds(
                _onnx_predict_fn(staged_model), train_symbols,
                n_splits=cfg.n_splits, gap=cfg.gap, dates=train_dates,
                indicator_export_paths=cfg.indicator_channel_export_paths,
                fold_index=fold_index, **common,
            )
            if len(rows) != 1:
                raise RuntimeError(f"outer fold {fold_index} has no independent score")
            invalid = [key for key, value in rows[0].items()
                       if key != "fold_multi_logloss" and not math.isfinite(value)]
            if invalid:
                raise RuntimeError(f"outer fold {fold_index} has nonfinite required metrics: {invalid}")
            row = {key: value for key, value in rows[0].items() if math.isfinite(value)}
            required = "fold_rmse" if cfg.target_type == "regression" else "fold_macro_f1"
            if required not in row or row.get("fold_n", 0) <= 0:
                raise RuntimeError(f"outer fold {fold_index} has no finite required metric")
            _metric(row)
            fold_report = {
                **row,
                "evaluation_revision": _evaluation_revision(
                    cfg, train_symbols, train_dates, fold_index),
                "scored_model_sha256": _file_sha256(staged_model),
            }
            fold_reports.append(fold_report)
            if fold_index == cfg.n_splits - 1:
                release_path = staged_model
                release_report = {
                    "evaluation_status": "independent_outer",
                    "evaluation_revision": fold_report["evaluation_revision"],
                    "scored_model_sha256": fold_report["scored_model_sha256"],
                    "target_type": cfg.target_type,
                    "timeframe": cfg.timeframe,
                    "horizon": cfg.horizon,
                    "class_order": ",".join(ds.CLASS_LABELS) if cfg.target_type == "classification" else "",
                    "outer_folds": fold_reports,
                    "n_samples": int(row["fold_n"]),
                    **row,
                }
                if "fold_multi_logloss" not in row and cfg.target_type == "classification":
                    release_report["optional_metric_status"] = "multi_logloss_unavailable"
                for key, value in row.items():
                    if key.startswith("fold_") and key not in ("fold_is_holdout",):
                        release_report[key[5:]] = value

        if release_path is None or release_report is None:
            raise RuntimeError("no independently evaluated release model")
        release_scaler = release_path.with_name(release_path.name + ".scaler.json")
        if cfg.fixed_zscore and not release_scaler.is_file():
            raise RuntimeError("selected release model has no fitted scaler sidecar")
        if cfg.oos_tail_days:
            oos = evaluate_oos(
                _onnx_predict_fn(release_path), full_symbols, full_dates,
                oos_tail_days=cfg.oos_tail_days,
                common_end=ds.common_end_date(full_dates),
                indicator_export_paths=cfg.indicator_channel_export_paths, **common,
            )
            if not oos:
                raise RuntimeError("configured out-of-sample tail has no finite score")
            invalid = [key for key, value in oos.items()
                       if key != "oos_multi_logloss" and not math.isfinite(value)]
            if invalid:
                raise RuntimeError(f"out-of-sample tail has nonfinite required metrics: {invalid}")
            oos = {key: value for key, value in oos.items() if math.isfinite(value)}
            required_oos = "oos_rmse" if cfg.target_type == "regression" else "oos_macro_f1"
            if required_oos not in oos or oos.get("oos_n", 0) <= 0:
                raise RuntimeError("configured out-of-sample tail has no finite required score")
            release_report["oos"] = oos
            if "oos_multi_logloss" not in oos and cfg.target_type == "classification":
                release_report["oos_optional_metric_status"] = "multi_logloss_unavailable"
            _metric(oos)
        if time.monotonic() >= deadline:
            raise TimeoutError("independent evaluation exceeded the run duration limit")
        release_report["resource_limits"] = run_limits
        if cfg.framework != "tensorflow":
            pointer = checkpoint_root / f"fold_{cfg.n_splits - 1}" / "best.json"
            if pointer.exists():
                state, _ = checkpoints.checked_blob(pointer)
                release_report["training_lineage"] = dict(state["context"]["lineage"], run_id=run_id)
            release_report["checkpoint_run_id"] = run_id
        release_report["run_budget_seconds_at_child_start"] = run_budget_seconds
        import model_analysis
        _note("computing train-only regimes and selected-model channel importance")
        analysis = model_analysis.build(
            cfg, release_path, train_symbols, train_dates, full_symbols, full_dates,
            predict=_onnx_predict_fn(release_path), checkpoint_root=checkpoint_root, deadline=deadline,
            resource_limits=run_limits)
        release_analysis = release_path.with_name(release_path.name + ".analysis.json")
        checkpoints.atomic_json(release_analysis, analysis)
        release_report["data_revision"] = analysis["data_revision"]
        if time.monotonic() >= deadline:
            raise TimeoutError("model analysis exceeded the training run budget")
        staged_metrics = Path(metrics.write_report(release_report, release_path))
        _publish_release_bundle(release_path, staged_metrics,
                                release_scaler if cfg.fixed_zscore else None,
                                out_path, release_report, release_analysis)
        scaler_path = out_path.with_name(out_path.name + ".scaler.json")
        _stage("export")
        _progress(92)
        _artifact("onnx", out_path)
        _artifact("metrics", metrics_path)
        _artifact("analysis", out_path.with_name(out_path.name + ".analysis.json"))
        if cfg.fixed_zscore:
            _artifact("scaler", scaler_path)
        _metric({key: value for key, value in release_report.items()
                 if isinstance(value, (int, float)) and math.isfinite(value)})
    finally:
        ownership.close()
        shutil.rmtree(tmp_root, ignore_errors=True)

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
    for unsupported in ("lightgbm", "tensorflow"):
        try:
            JobConfig.from_json(json.dumps({**base, "framework": unsupported, "target_type": "regression"}))
        except ValueError as exc:
            assert "requires pytorch" in str(exc)
        else:  # pragma: no cover
            raise AssertionError("unsupported regression framework was accepted")
    defaults = JobConfig.from_json(json.dumps(base))
    assert defaults.target_type == "classification" and defaults.n_splits == ds.DEFAULT_WF_SPLITS
    assert defaults.gap is None and defaults.oos_tail_days is None
    limits = _effective_resource_limits()
    old_resource_env = {name: os.environ.get(name) for name in _RESOURCE_ENV.values()}
    try:
        for key, name in _RESOURCE_ENV.items():
            os.environ[name] = str(limits[key])
        managed = JobConfig.from_json(json.dumps({
            **base, "resource_limits": limits, "remaining_run_budget_seconds": 60,
        }))
        assert managed.resource_limits == limits
        for invalid in (
            {**limits, "max_samples": limits["max_samples"] - 1},
            {**limits, "max_tensor_elements": limits["max_tensor_elements"] - 1},
        ):
            try:
                JobConfig.from_json(json.dumps({
                    **base, "resource_limits": invalid, "remaining_run_budget_seconds": 60,
                }))
            except ValueError:
                pass
            else:
                raise AssertionError("mismatched managed resource contract was accepted")
    finally:
        for name, old in old_resource_env.items():
            if old is None:
                os.environ.pop(name, None)
            else:
                os.environ[name] = old
    for bad in ({"target_type": "ranking"}, {"n_splits": 1},
                {"n_splits": MAX_EVALUATION_FOLDS + 1}, {"gap": -1},
                {"oos_tail_days": -2}):
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
    for restricted in ("max_symbols", "smoke"):
        restricted_cfg = JobConfig.from_json(json.dumps({
            **base, "hyperparameters": {restricted: 1},
        }))
        try:
            _build_trainer_argv(restricted_cfg, Path("d"), Path("o.onnx"))
        except SystemExit:
            pass
        else:
            raise AssertionError(f"{restricted} must not change the evaluated symbol set")

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

    # Independent evaluation selects the same original fold for every symbol,
    # then removes any train label that reaches the earliest test feature date.
    long_syms = {name: np.tile(syms[name], (5, 1)) for name in syms}
    long_dates = {
        name: np.datetime64("2018-01-01") + np.arange(2000).astype("timedelta64[D]")
        for name in syms
    }
    selected = ds.independent_fold_data(
        long_syms, long_dates, n_splits=3, gap=0, fold_index=0,
        inner=True, **common,
    )
    for name, (_x, _y, anchors, fit, inner_val) in selected.items():
        assert fit.size and inner_val.size
        assert long_dates[name][anchors[fit[-1]] + common["horizon"]] < (
            long_dates[name][anchors[inner_val[0]] - common["window"] + 1])
    independent_rows = evaluate_folds(
        stub_predict, long_syms, n_splits=3, gap=0, dates=long_dates,
        fold_index=0, **common,
    )
    assert len(independent_rows) == 1
    assert independent_rows[0]["fold"] == 0.0 and independent_rows[0]["fold_is_holdout"] == 1.0

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
        _no_oos_cfg = JobConfig.from_json(json.dumps({**base, "symbols": ["A"], "oos_tail_days": None}))
        assert _resolve_training_dir(_no_oos_cfg, _tmpdir, Path(tempfile.mkdtemp())) == _tmpdir
        _oos_cfg = JobConfig.from_json(json.dumps({**base, "symbols": ["A"], "oos_tail_days": 60}))
        _train_dir = _resolve_training_dir(_oos_cfg, _tmpdir, Path(tempfile.mkdtemp()))
        assert _train_dir != _tmpdir
        _trained_rows = ds.load_parquet_dir(_train_dir)["A"].shape[0]
        _main_expect, _, _, _ = ds.oos_split(syms["A"], dts["A"], 60)
        assert _trained_rows == _main_expect.shape[0]
        assert _trained_rows < syms["A"].shape[0], "OOS tail must actually shrink the training set"

        # A later-ending symbol is truncated before the common OOS boundary.
        _pl.DataFrame({
            "date": _pl.Series(dts["B"][:330]).cast(_pl.Date),
            "open": syms["B"][:330, 0], "high": syms["B"][:330, 1],
            "low": syms["B"][:330, 2], "close": syms["B"][:330, 3],
            "volume": syms["B"][:330, 4],
        }).write_parquet(_tmpdir / "B.parquet")
        _both_cfg = JobConfig.from_json(json.dumps({
            **base, "symbols": ["A", "B"], "oos_tail_days": 60,
        }))
        _both_dir = _resolve_training_dir(_both_cfg, _tmpdir, Path(tempfile.mkdtemp()))
        _both = ds.load_parquet_dir(_both_dir, return_dates=True)
        _common_end = ds.common_end_date({"A": dts["A"], "B": dts["B"][:330]})
        for _name in ("A", "B"):
            expected, expected_dates, _, _ = ds.oos_split(
                syms[_name], dts[_name], 60, common_end=_common_end,
            )
            assert _both[0][_name].shape[0] == expected.shape[0]
            assert np.array_equal(_both[1][_name], expected_dates)

        # The selected-symbol common boundary must reject a symbol with no
        # training rows instead of silently training on fewer symbols.
        _pl.DataFrame({
            "date": _pl.Series(
                np.datetime64("2019-06-01") + np.arange(20).astype("timedelta64[D]")
            ).cast(_pl.Date),
            "open": np.full(20, 10.0), "high": np.full(20, 11.0), "low": np.full(20, 9.0),
            "close": np.full(20, 10.0), "volume": np.full(20, 1_000.0),
        }).write_parquet(_tmpdir / "SHORT.parquet")
        try:
            _resolve_training_dir(
                JobConfig.from_json(json.dumps({**base, "symbols": ["A", "SHORT"], "oos_tail_days": 60})),
                _tmpdir, Path(tempfile.mkdtemp()),
            )
        except ValueError as exc:
            assert "common boundary" in str(exc)
        else:
            raise AssertionError("a selected symbol with no main rows must fail")

    # Exercise the real orchestrator path with a deterministic trainer/predictor
    # substitute: each fold gets its own output, only the last scored output is
    # published, and a failed fold leaves no promotable artifact.
    import contextlib
    import io
    with tempfile.TemporaryDirectory() as _integration_tmp:
        root = Path(_integration_tmp)
        raw_dir = root / "raw"
        raw_dir.mkdir()
        for name in long_syms:
            sample = long_syms[name][:800]
            _pl.DataFrame({
                "date": _pl.Series(long_dates[name][:800]).cast(_pl.Date),
                "open": sample[:, 0], "high": sample[:, 1], "low": sample[:, 2],
                "close": sample[:, 3], "volume": sample[:, 4],
            }).write_parquet(raw_dir / f"{name}.parquet")
        config_file = root / "job.json"
        config_file.write_text(json.dumps({
            "symbols": ["A", "B"], "architecture": "lstm", "window_size": 30,
            "horizon": 5, "n_splits": 3, "gap": 0, "run_id": "selfcheck_independent",
        }), encoding="utf-8")
        original_resolve, original_dispatch = _resolve_dataset_dir, _dispatch
        original_predict, original_artifacts = _onnx_predict_fn, ARTIFACTS_DIR
        original_publish = _publish_release_bundle
        dispatched = []
        def fake_dispatch(_cfg, argv):
            if "--preflight" in argv:
                return 0
            k = int(argv[argv.index("--outer-fold-index") + 1])
            dispatched.append(k)
            Path(argv[argv.index("--out") + 1]).write_bytes(f"model-{k}".encode())
            return 0
        try:
            globals()["_resolve_dataset_dir"] = lambda _cfg, _root: raw_dir
            globals()["_dispatch"] = fake_dispatch
            globals()["_onnx_predict_fn"] = lambda _path: stub_predict
            globals()["ARTIFACTS_DIR"] = root / "artifacts"
            assert main(["--config", str(config_file)]) == 0
            published = list((root / "artifacts").glob("*.onnx"))
            assert dispatched == [0, 1, 2] and len(published) == 1
            assert published[0].read_bytes() == b"model-2"
            report = json.loads(Path(str(published[0]) + ".metrics.json").read_text())
            assert report["evaluation_status"] == "independent_outer"
            assert report["fold"] == 2.0 and len(report["evaluation_revision"]) == 64
            assert report["target_type"] == "classification" and report["timeframe"] == "daily"
            assert report["horizon"] == 5 and report["class_order"] == ",".join(ds.CLASS_LABELS)
            assert len(report["outer_folds"]) == 3
            assert [row["fold"] for row in report["outer_folds"]] == [0.0, 1.0, 2.0]
            assert report["scored_model_sha256"] == _file_sha256(published[0])
            assert report["resource_limits"] == _effective_resource_limits()
            assert 0 < report["run_budget_seconds_at_child_start"] <= MAX_RUN_DURATION_SECONDS
            dispatched.clear()
            globals()["_dispatch"] = lambda _cfg, argv: (1 if "--preflight" not in argv and
                int(argv[argv.index("--outer-fold-index") + 1]) == 1 else fake_dispatch(_cfg, argv))
            config_file.write_text(config_file.read_text().replace(
                "selfcheck_independent", "selfcheck_failure"), encoding="utf-8")
            try:
                main(["--config", str(config_file)])
            except RuntimeError as exc:
                assert "outer fold 1" in str(exc)
            else:
                raise AssertionError("failed independent fold must fail the run")
            assert len(list((root / "artifacts").glob("*.onnx"))) == 1
            globals()["_dispatch"] = fake_dispatch
            globals()["_publish_release_bundle"] = lambda *_args: (_ for _ in ()).throw(
                OSError("injected final publication failure"))
            config_file.write_text(config_file.read_text().replace(
                "selfcheck_failure", "selfcheck_publish_failure"), encoding="utf-8")
            protocol = io.StringIO()
            with contextlib.redirect_stdout(protocol):
                try:
                    main(["--config", str(config_file)])
                except OSError as exc:
                    assert "injected final publication failure" in str(exc)
                else:
                    raise AssertionError("failed publication must fail the run")
            assert "ARTIFACT:onnx:" not in protocol.getvalue()
            assert "ARTIFACT:metrics:" not in protocol.getvalue()
            assert "ARTIFACT:checkpoint:" in protocol.getvalue()
            assert published[0].read_bytes() == b"model-2"
        finally:
            globals()["_resolve_dataset_dir"] = original_resolve
            globals()["_dispatch"] = original_dispatch
            globals()["_onnx_predict_fn"] = original_predict
            globals()["ARTIFACTS_DIR"] = original_artifacts
            globals()["_publish_release_bundle"] = original_publish

    print("run_training.py selfcheck: OK")


if __name__ == "__main__":
    if "--selfcheck" in sys.argv[1:]:
        _run_selfcheck()
    else:
        raise SystemExit(main())
