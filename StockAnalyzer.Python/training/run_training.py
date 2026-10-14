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
import ensemble  # noqa: E402
import fixed_scaler  # noqa: E402
import metrics  # noqa: E402
import onnx_meta  # noqa: E402  (sibling module, path inserted above)
import checkpoints  # noqa: E402

# <DataRoot>/TrainingArtifacts/TW (the Training Wizard folder), not a path relative to this script: this file lives under
# StockAnalyzer.Python/training/, which in a release build is copied beside the executable
# (see PythonScriptLocator) rather than being the app's actual Data root. dataset.DATA_ROOT
# already resolves the real Data root correctly in both dev and release (it is how the OHLCV
# parquet reads work), so reuse it here instead of introducing a second, divergent resolution
# scheme for this one output directory.
ARTIFACTS_DIR: Path = ds.TRAINING_WIZARD_ARTIFACTS_DIR
MAX_EVALUATION_FOLDS = ds._env_int("SA_MAX_EVALUATION_FOLDS", 10)
MAX_RUN_DURATION_SECONDS = ds._env_int("SA_MAX_RUN_DURATION_SECONDS", 120 * 60)
EXPLANATION_FAILED_STATUS = "failed"  # metrics.json "explanation".status of a run whose optional explanation raised
MIN_MEMBER_BUDGET_SECONDS = 1e-3  # a member of an ensemble run is never handed a zero or negative budget

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

# Unknown keys stay an error (a typo guard); the hint names the other likely cause, a newer app writing a key this
# older training package does not know (the app omits its newer keys while they hold their defaults).
UNKNOWN_KEYS_HINT = "this training package may be older than the application that wrote the config"


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
    # T12: carve a calibration segment from the release fold's train tail and fit a temperature.
    calibration: bool = False
    # T13: an ensemble of independent members (bagging / stacking) and self-supervised pretraining.
    ensemble_method: str = ensemble.METHOD_NONE
    ensemble_members: int = ensemble.DEFAULT_BAGGING_MEMBERS
    stacking_bases: List[str] = dataclasses.field(default_factory=list)
    # D2: where the stacking weights are fitted (stacking only): the strictly earlier outer folds' out-of-fold rows, or a meta
    # segment carved from the release fold's train side that the release members are not trained on.
    stacking_weight_source: str = ensemble.WEIGHT_SOURCE_PAST_OOF
    pretraining: str = onnx_meta.PRETRAINING_NONE
    # T14: evaluation-only combinatorial purged cross-validation (results go into the release metrics' ``cpcv`` block).
    cpcv: bool = False
    cpcv_groups: int = ds.CPCV_DEFAULT_GROUPS
    cpcv_test_groups: int = ds.CPCV_DEFAULT_TEST_GROUPS

    @staticmethod
    def from_json(text: str) -> "JobConfig":
        raw = json.loads(text)
        if not isinstance(raw, dict):
            raise ValueError("config JSON must be an object")
        known = {f.name for f in dataclasses.fields(JobConfig)}
        unknown = set(raw) - known
        if unknown:
            raise ValueError(f"config has unknown keys: {sorted(unknown)}; "
                             f"{UNKNOWN_KEYS_HINT}")
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
                calibration=raw.get("calibration", False),
                ensemble_method=str(raw.get("ensemble_method", ensemble.METHOD_NONE)).strip().lower(),
                ensemble_members=raw.get("ensemble_members", ensemble.DEFAULT_BAGGING_MEMBERS),
                stacking_bases=raw.get("stacking_bases") or [],
                stacking_weight_source=str(raw.get("stacking_weight_source", ensemble.WEIGHT_SOURCE_PAST_OOF)).strip().lower(),
                pretraining=str(raw.get("pretraining", onnx_meta.PRETRAINING_NONE)).strip().lower(),
                cpcv=raw.get("cpcv", False),
                cpcv_groups=raw.get("cpcv_groups", ds.CPCV_DEFAULT_GROUPS),
                cpcv_test_groups=raw.get("cpcv_test_groups", ds.CPCV_DEFAULT_TEST_GROUPS),
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
        if type(self.calibration) is not bool:
            raise ValueError("config.calibration must be boolean")
        if self.calibration and (self.target_type != "classification" or self.prepared_input_dir):
            raise ValueError("calibration requires a classification target without prepared_input_dir")
        if not isinstance(self.stacking_bases, list) or any(not isinstance(b, str) for b in self.stacking_bases):
            raise ValueError("config.stacking_bases must be a list of strings")
        ensemble.validate_plan(self.ensemble_method, self.ensemble_members, self.stacking_bases,
                               self.target_type, _effective_resource_limits()["max_ensemble_members"])
        if self.stacking_weight_source not in ensemble.WEIGHT_SOURCES:
            raise ValueError(f"config.stacking_weight_source {self.stacking_weight_source!r} not in {list(ensemble.WEIGHT_SOURCES)}")
        if self.stacking_weight_source != ensemble.WEIGHT_SOURCE_PAST_OOF and self.ensemble_method != ensemble.METHOD_STACKING:
            raise ValueError("config.stacking_weight_source applies to ensemble_method 'stacking' only")
        if self.pretraining not in onnx_meta.PRETRAINING_METHODS:
            raise ValueError(f"config.pretraining {self.pretraining!r} not in {list(onnx_meta.PRETRAINING_METHODS)}")
        if self.ensemble_method != ensemble.METHOD_NONE and (
                self.initialization_mode != "fresh" or self.calibration
                or self.pretraining != onnx_meta.PRETRAINING_NONE):
            raise ValueError("an ensemble trains fresh members and cannot be combined with calibration, "
                             "pretraining, FineTune or Resume")
        if self.pretraining != onnx_meta.PRETRAINING_NONE and (
                self.framework != "pytorch" or self.initialization_mode == "fine_tune"):
            raise ValueError("self-supervised pretraining requires pytorch and a fresh or resumed run")
        if type(self.cpcv) is not bool:
            raise ValueError("config.cpcv must be boolean")
        if self.cpcv:
            # the count is compared with the cap before it is formed (13-21=A): an oversized request fails here
            ds.cpcv_combination_count(self.cpcv_groups, self.cpcv_test_groups)
            if self.ensemble_method != ensemble.METHOD_NONE or self.initialization_mode != "fresh":
                raise ValueError("CPCV evaluates a fresh single-model recipe: no ensemble, FineTune or Resume")
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


# Percent range one ``_train_one`` call may report; an ensemble run gives every member its own slice.
_progress_window: Tuple[int, int] = (0, 100)


def _progress(pct: int) -> None:
    low, high = _progress_window
    _emit(f"PROGRESS:{low + max(0, min(100, int(pct))) * (high - low) // 100}")


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
        tail = cfg.oos_tail_days or 0
        cutoff = ds.resolve_oos_cutoff(common_end, tail) if tail > 0 else common_end
        firsts = {s: str(np.asarray(dates[s]).astype("datetime64[D]")[0]) for s in dropped if s in dates}
        raise ValueError(
            f"selected symbols lack training rows at the common boundary: {dropped} "
            f"(first row of each: {firsts}; training rows must be dated on or before {cutoff} = "
            f"the earliest last date among the selected symbols, {common_end}, minus the out-of-sample tail of {tail} day(s)). "
            "Remove these symbols from the ticker lists, or shorten the tail."
        )
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
    "pretrain", "bag-index", "bag-count", "bag-seed", "bag-fraction", "bag-block-length", "meta-segment",
    "cpcv-groups", "cpcv-test-groups", "cpcv-combination",
})


def _normalize_flag(key: object) -> str:
    """Turn a hyperparameter key into the CLI flag name it collides with / emits as.

    Shared by the reserved-flag conflict check and argv emission below so the two stay in
    lockstep by construction instead of by two independently maintained copies of the rule.
    """
    return str(key).strip().lstrip("-").replace("_", "-")


def _build_trainer_argv(cfg: JobConfig, data_dir: Path, out_path: Path,
                        bagging: ds.BaggingSpec | None = None, seed: int | None = None,
                        cpcv: ds.CpcvSpec | None = None) -> List[str]:
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
    if cfg.pretraining != onnx_meta.PRETRAINING_NONE:
        argv += ["--pretrain", cfg.pretraining]
    if bagging is not None:
        argv += ["--bag-index", str(bagging.index), "--bag-count", str(bagging.count),
                 "--bag-seed", str(bagging.seed), "--bag-fraction", repr(float(bagging.fraction))]
        if bagging.block_length is not None:
            argv += ["--bag-block-length", str(bagging.block_length)]
    if seed is not None:
        argv += ["--seed", str(seed)]
    if cpcv is not None:
        argv += ["--cpcv-groups", str(cpcv.groups), "--cpcv-test-groups", str(cpcv.test_groups),
                 "--cpcv-combination", str(cpcv.combination)]

    conflicts = sorted(
        key for key in cfg.hyperparameters
        if _normalize_flag(key) in _RESERVED_TRAINER_FLAGS
    )
    if conflicts:
        raise SystemExit(
            f"config.hyperparameters reuse reserved trainer flag(s) {conflicts}; "
            "set these via the wizard's own Window/Horizon/Splits/Gap/Architecture fields instead"
        )
    if seed is not None and any(_normalize_flag(key) == "seed" for key in cfg.hyperparameters):
        raise SystemExit("an ensemble member's seed is derived from the run seed; remove the seed hyperparameter")
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
    capture: Dict[str, Tuple[np.ndarray, np.ndarray, np.ndarray]] | None = None,
    cpcv: ds.CpcvSpec | None = None,
) -> List[Dict[str, float]]:
    """One flat metric dict per walk-forward fold, pooled over every symbol.

    ``capture`` (independent fold evaluation only) receives, per symbol, the scored rows
    ``(anchor_rows, y, probabilities)`` so an ensemble can mix members on identical rows.

    ``cpcv`` scores the test windows of one CPCV combination instead of an outer fold: the row is keyed by the
    combination index, is never a holdout row, and carries ``fold_train_n`` (train windows left after purge and embargo).

    With ``fold_index`` set, the predictor must be the model fitted for that
    original fold; the data selector applies the identical cross-symbol purge
    used by the trainer and the returned row is an independent holdout score.
    Without it, this remains a descriptive same-model reference helper for
    older callers. Pass OOS-excluded symbols to avoid duplicate evaluation.
    """
    import metrics as _metrics  # sibling module; imported here to keep module import light

    is_regression = target_type == "regression"
    resolved_gap = ds.resolve_purge_gap(window, horizon, gap)
    fold_true: Dict[int, List[np.ndarray]] = {}
    fold_pred: Dict[int, List[np.ndarray]] = {}
    fold_prob: Dict[int, List[np.ndarray]] = {}
    independent = None
    train_windows = 0
    if cpcv is not None:
        if dates is None or fold_index is not None:
            raise ValueError("CPCV evaluation requires dates and no outer fold")
        independent = ds.cpcv_fold_data(
            symbols, dates, feature_mode=feature_mode, window=window, horizon=horizon,
            threshold=threshold, n_splits=n_splits, gap=gap, spec=cpcv,
            feature_spec=feature_spec, target_type=target_type,
            indicator_export_paths=indicator_export_paths, fixed_zscore=fixed_zscore, lags=lags,
        )
        train_windows = sum(len(parts[3]) for parts in independent.values())
        fold_index = cpcv.combination
    elif fold_index is not None:
        if dates is None:
            raise ValueError("independent fold evaluation requires dates")
        independent = ds.independent_fold_data(
            symbols, dates, feature_mode=feature_mode, window=window, horizon=horizon,
            threshold=threshold, n_splits=n_splits, gap=gap, fold_index=fold_index,
            feature_spec=feature_spec, target_type=target_type,
            indicator_export_paths=indicator_export_paths,
            fixed_zscore=fixed_zscore, lags=lags,
        )

    if capture is not None and independent is None:
        raise ValueError("capturing scored rows requires independent fold evaluation")
    for sym, arr in symbols.items():
        if independent is not None:
            if sym not in independent:
                continue  # CPCV: a symbol without test windows in this combination is absent from it
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
            if capture is not None:
                # float32 is the ONNX output type (lossless); ensemble.pool_fold widens it to float64 for the mixture
                capture[sym] = (np.asarray(_anchors)[val_idx], np.asarray(y[val_idx]),
                                np.asarray(probs, dtype=ensemble.CAPTURE_DTYPE))
            if is_regression:
                fold_true.setdefault(k, []).append(y[val_idx].reshape(-1))
                fold_pred.setdefault(k, []).append(np.asarray(probs).reshape(-1))
            else:
                fold_true.setdefault(k, []).append(y[val_idx])
                fold_pred.setdefault(k, []).append(np.asarray(probs).argmax(axis=1))
                fold_prob.setdefault(k, []).append(np.asarray(probs))

    rows: List[Dict[str, float]] = []
    for k in sorted(fold_true):
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
                "fold_is_holdout": 1.0 if (fold_index is not None and cpcv is None) or k == n_splits - 1 else 0.0,
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
                "fold_is_holdout": 1.0 if (fold_index is not None and cpcv is None) or k == n_splits - 1 else 0.0,
            })
    if cpcv is not None:
        for row in rows:
            row["fold_train_n"] = float(train_windows)
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


def _attention_module():
    """``attention_export`` imports torch at module level: an environment without it (LightGBM only) has no attention sidecar to publish or retire."""
    try:
        import attention_export
    except ImportError:
        return None
    return attention_export


def _release_attention_sidecar(cfg: "JobConfig", release_path: Path) -> Path | None:
    """The T24 attention sidecar the PyTorch trainer wrote beside the selected release model, or None (it is an optional derived file)."""
    module = _attention_module() if cfg.framework == "pytorch" else None
    if module is None:
        return None
    sidecar = module.sidecar_path(release_path)
    return sidecar if sidecar.is_file() else None


def _publish_release_bundle(release_path: Path, staged_metrics: Path,
                            release_scaler: Path | None, out_path: Path,
                            expected_report: dict, release_analysis: Path | None = None,
                            release_calibration: Path | None = None,
                            release_explain: Path | None = None,
                            release_attention: Path | None = None) -> None:
    """Copy a scored bundle beside its destination, then commit with local recovery copies."""
    out_path.parent.mkdir(parents=True, exist_ok=True)
    metrics_path = out_path.with_name(out_path.name + ".metrics.json")
    scaler_path = out_path.with_name(out_path.name + ".scaler.json")
    analysis_path = out_path.with_name(out_path.name + ".analysis.json")
    calibration_path = out_path.with_name(out_path.name + ".calibration.json")
    explain_path = out_path.with_name(out_path.name + ".explain.json")
    attention_module = _attention_module()
    attention_path = None if attention_module is None else attention_module.sidecar_path(out_path)
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
        calibration_copy = None
        if release_calibration is not None:
            calibration_copy = incoming / calibration_path.name
            shutil.copy2(release_calibration, calibration_copy)
        explain_copy = None
        if release_explain is not None:
            explain_copy = incoming / explain_path.name
            shutil.copy2(release_explain, explain_copy)
        attention_copy = None
        if release_attention is not None:
            if attention_module is None:
                raise ValueError("an attention sidecar was selected but attention_export is unavailable")
            attention_copy = incoming / attention_path.name
            shutil.copy2(release_attention, attention_copy)

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
        if calibration_copy is not None:
            calibration_document = json.loads(calibration_copy.read_text(encoding="utf-8"))
            if (calibration_document.get("model_sha256") != model_hash
                    or calibration_document.get("data_revision") != expected_report.get("data_revision")
                    or _file_sha256(calibration_copy) != _file_sha256(release_calibration)):
                raise ValueError("staged calibration disagrees with the selected model evidence")
        if explain_copy is not None:
            explain_document = json.loads(explain_copy.read_text(encoding="utf-8"))
            if (explain_document.get("model_sha256") != model_hash
                    or _file_sha256(explain_copy) != _file_sha256(release_explain)):
                raise ValueError("staged explanation disagrees with the selected model")
        if attention_copy is not None:
            # the sidecar is named <model>.attention.onnx and carries the SHA-256 of exactly this model file (T24 binding)
            attention_module.check_binding(attention_copy, model_copy)
            if _file_sha256(attention_copy) != _file_sha256(release_attention):
                raise ValueError("staged attention sidecar differs from the selected one")
        finals = (out_path, metrics_path, scaler_path, analysis_path, calibration_path, explain_path)
        if attention_path is not None:
            finals += (attention_path,)
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
            if calibration_copy is not None:
                operations.append((calibration_copy, calibration_path))
            if explain_copy is not None:
                operations.append((explain_copy, explain_path))
            if attention_copy is not None:
                operations.append((attention_copy, attention_path))
            operations.append((model_copy, out_path))
            for source, destination in operations:
                attempted.append(destination)
                checkpoints.replace_with_retry(source, destination)
            if scaler_copy is None and scaler_path.exists():
                attempted.append(scaler_path)
                checkpoints.replace_with_retry(scaler_path, stage / "retired.scaler.json")
            if analysis_copy is None and analysis_path.exists():
                attempted.append(analysis_path)
                checkpoints.replace_with_retry(analysis_path, stage / "retired.analysis.json")
            if calibration_copy is None and calibration_path.exists():
                attempted.append(calibration_path)
                checkpoints.replace_with_retry(calibration_path, stage / "retired.calibration.json")
            if explain_copy is None and explain_path.exists():
                attempted.append(explain_path)
                checkpoints.replace_with_retry(explain_path, stage / "retired.explain.json")
            if attention_copy is None and attention_path is not None and attention_path.exists():
                attempted.append(attention_path)
                checkpoints.replace_with_retry(attention_path, stage / "retired.attention.onnx")
        except OSError as publish_error:
            restoration_errors = []
            for destination in reversed(attempted):
                try:
                    if destination in backups:
                        restore_copy = stage / ("restore_" + destination.name)
                        shutil.copy2(backups[destination], restore_copy)
                        checkpoints.replace_with_retry(restore_copy, destination)
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
    if cfg.ensemble_method != ensemble.METHOD_NONE:
        return _run_ensemble(cfg, started=started, run_limits=run_limits,
                             run_budget_seconds=run_budget_seconds, deadline=deadline)
    _train_one(cfg, started=started, run_limits=run_limits,
               run_budget_seconds=run_budget_seconds, deadline=deadline)
    _stage("done")
    _progress(100)
    return 0


# --- T14 CPCV evaluation ------------------------------------------------------------------------------

CPCV_METHOD = "combinatorial_purged_cv"
CPCV_PATHS = "not_constructed"  # 13-17=B: no backtest path is built, no path formula is introduced
# Interpretation labels of the ``cpcv`` block (WebAI review A1): a CPCV score is a robustness diagnostic, never an
# estimate of deployed performance -- the combinations share train windows and train on both sides of a test group.
CPCV_EVALUATION_PROTOCOL = "cpcv_combinatorial"
CPCV_TRAINING_TEMPORALITY = "bidirectional_excluding_test"
CPCV_DEPLOYMENT_INTERPRETATION = "robustness_diagnostic"
CPCV_DISTRIBUTION_SEMANTICS = "dependent_split_scores"


def _cpcv_statistics(values: List[float]) -> Dict[str, float]:
    """Descriptive statistics over the combinations that ran (they share training data: not independent samples)."""
    array = np.asarray(values, dtype=np.float64)
    return {"count": float(array.size), "mean": float(array.mean()), "std": float(array.std()),
            "min": float(array.min()), "max": float(array.max())}


def _run_cpcv(cfg: JobConfig, train_dir: Path, train_symbols: Dict[str, np.ndarray],
              train_dates: Dict[str, np.ndarray], *, common: dict, deadline: float, tmp_root: Path) -> dict:
    """Fit and score every CPCV combination. Evaluation only: nothing here is published or becomes a generation.

    Each combination fits a fresh model (the job's own recipe) on its purged train windows and is scored on the
    windows of its test groups. Only each combination's own metrics are reported (13-17=B).
    """
    total = ds.cpcv_combination_count(cfg.cpcv_groups, cfg.cpcv_test_groups)
    bounds = ds.cpcv_group_bounds(np.concatenate([np.asarray(d).astype("datetime64[D]") for d in train_dates.values()]),
                                  cfg.cpcv_groups)
    group_window_counts = ds.cpcv_group_window_counts(
        train_symbols, train_dates, groups=cfg.cpcv_groups, indicator_export_paths=cfg.indicator_channel_export_paths,
        **{key: common[key] for key in ("feature_mode", "window", "horizon", "threshold", "feature_spec",
                                        "target_type", "fixed_zscore", "lags")})
    _stage("cpcv")
    combinations = []
    for index in range(total):
        if time.monotonic() >= deadline:
            raise TimeoutError("CPCV evaluation exceeded the run duration limit")
        spec = ds.CpcvSpec(cfg.cpcv_groups, cfg.cpcv_test_groups, index)
        staged = tmp_root / f"cpcv_{index}" / "cpcv_model.onnx"
        staged.parent.mkdir(parents=True, exist_ok=True)
        rc = _dispatch(cfg, _build_trainer_argv(cfg, train_dir, staged, cpcv=spec))
        if rc != 0 or not staged.is_file():
            raise RuntimeError(f"trainer failed to produce CPCV combination {index} (exit {rc})")
        if time.monotonic() >= deadline:
            raise TimeoutError("CPCV evaluation exceeded the run duration limit")
        rows = evaluate_folds(
            _onnx_predict_fn(staged), train_symbols, n_splits=cfg.n_splits, gap=cfg.gap, dates=train_dates,
            indicator_export_paths=cfg.indicator_channel_export_paths, cpcv=spec, **common)
        if len(rows) != 1:
            raise RuntimeError(f"CPCV combination {index} has no test score")
        row = {key[5:] if key.startswith("fold_") else key: value for key, value in rows[0].items()
               if key not in ("fold", "n_splits", "fold_is_holdout") and math.isfinite(value)}
        required = "rmse" if cfg.target_type == "regression" else "macro_f1"
        if required not in row or row.get("n", 0) <= 0 or row.get("train_n", 0) <= 0:
            raise RuntimeError(f"CPCV combination {index} has no finite required metric")
        combination = {"combination": index, "test_groups": list(spec.test_set),
                       "n_test": int(row.pop("n")), "n_train": int(row.pop("train_n")), **row}
        combinations.append(combination)
        _metric({"cpcv_combination": float(index), "cpcv_combinations": float(total),
                 **{f"cpcv_{key}": float(value) for key, value in combination.items()
                    if key not in ("combination", "test_groups")}})
        _progress(85 + 6 * (index + 1) // total)
    names = [key for key in combinations[0] if key not in ("combination", "test_groups", "n_test", "n_train")]
    return {
        "method": CPCV_METHOD, "evaluation_only": True, "groups": cfg.cpcv_groups,
        "test_groups": cfg.cpcv_test_groups, "combination_count": total, "paths": CPCV_PATHS,
        "group_assignment": "common_calendar_remainder_to_first_groups",
        "group_bounds": [[str(first), str(last)] for first, last in bounds],
        "group_window_counts": group_window_counts,
        "evaluation_protocol": CPCV_EVALUATION_PROTOCOL, "training_temporality": CPCV_TRAINING_TEMPORALITY,
        "deployment_interpretation": CPCV_DEPLOYMENT_INTERPRETATION,
        "distribution_semantics": CPCV_DISTRIBUTION_SEMANTICS,
        "purge": "information_interval_intersects_test_span", "embargo": {"unit": "bars", "length": cfg.horizon},
        "released_model": "not_a_cpcv_member", "combinations": combinations,
        "summary": {name: _cpcv_statistics([c[name] for c in combinations if name in c]) for name in names},
    }


def _carve_meta_segment(cfg: JobConfig, symbols, dates, common: dict):
    """The release fold's selected windows and its meta segment (``symbol -> sample indices``): the identical carve the
    calibration segment uses (:func:`dataset.independent_fold_data` with ``calibration=True``)."""
    segment: Dict[str, np.ndarray] = {}
    selected = ds.independent_fold_data(
        symbols, dates, n_splits=cfg.n_splits, gap=cfg.gap, fold_index=cfg.n_splits - 1,
        indicator_export_paths=cfg.indicator_channel_export_paths, inner=True, calibration=True,
        calibration_out=segment, **common)
    return selected, segment


def _score_meta_segment(predict, cfg: JobConfig, symbols, dates, common: dict
                        ) -> Dict[str, Tuple[np.ndarray, np.ndarray, np.ndarray]]:
    """The published release model's probabilities on the meta segment, per symbol: ``(anchors, y, probs)``."""
    selected, segment = _carve_meta_segment(cfg, symbols, dates, common)
    rows: Dict[str, Tuple[np.ndarray, np.ndarray, np.ndarray]] = {}
    for symbol in sorted(symbols):
        x, y, anchors, _fit, _validation = selected[symbol]
        index = segment[symbol]
        rows[symbol] = (anchors[index], y[index], np.asarray(predict(x[index]), dtype=ensemble.CAPTURE_DTYPE))
    return rows


def _train_one(cfg: JobConfig, *, started: datetime.datetime, run_limits: dict, run_budget_seconds: float,
               deadline: float, member: Optional[dict] = None) -> dict:
    """Train, independently evaluate and publish ONE model (the whole historical single-model run).

    ``member`` is set only by :func:`_run_ensemble`: ``bagging`` / ``seed`` select the member's resample and
    trainer seed, ``capture`` (a dict) receives every outer fold's scored rows, ``explain`` False skips the
    optional Shapley sidecar. A member never emits the run-level artifact lines or the terminal stage.
    """
    member = member or {}
    standalone = not member
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
                           ARTIFACTS_DIR / checkpoints.DIRECTORY_NAME / run_id)
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
            if not cfg.calibration:
                job_contract.pop("calibration", None)
            # T13 keys join the resume contract only when used, so a run started before them still resumes.
            for key in ("ensemble_method", "ensemble_members", "stacking_bases", "stacking_weight_source"):
                job_contract.pop(key, None)  # ensembles never resume; a member is a plain single-model run
            if cfg.pretraining == onnx_meta.PRETRAINING_NONE:
                job_contract.pop("pretraining", None)
            for key in ("cpcv", "cpcv_groups", "cpcv_test_groups"):
                job_contract.pop(key, None)  # CPCV needs a fresh run, so it never belongs to a resume contract
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
            if standalone:
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
        calibration_plan = None
        if cfg.calibration:
            import calibration
            calibration_plan = calibration.plan(cfg, train_symbols, train_dates)
            _note(f"calibration segment preflight: {calibration_plan}")
        release_calibrated = bool(calibration_plan and calibration_plan["status"] == "available")
        # D2: a stacking member of a meta-segment run also leaves the carved tail out of its release-fold fit (never both:
        # an ensemble cannot be calibrated), but stamps no calibration reference.
        meta_segment = bool(member.get("meta_segment"))
        release_holdout = release_calibrated or meta_segment
        if member.get("capture_members"):
            # Ensemble preflight (first member only: every member scores the same rows): the captured probabilities of all
            # members and folds must fit the limit before any model is trained.
            fold_rows = [
                sum(len(parts[4]) for parts in ds.independent_fold_data(
                    train_symbols, train_dates, n_splits=cfg.n_splits, gap=cfg.gap, fold_index=k,
                    indicator_export_paths=cfg.indicator_channel_export_paths, **common).values())
                for k in range(cfg.n_splits)]
            if meta_segment:
                meta_rows = sum(len(index) for index in _carve_meta_segment(cfg, train_symbols, train_dates, common)[1].values())
                if meta_rows < ensemble.MIN_META_SAMPLES:
                    raise ValueError(f"the stacking meta segment has {meta_rows} rows; it needs at least "
                                     f"{ensemble.MIN_META_SAMPLES} (use more history, fewer splits or a shorter window)")
                fold_rows.append(meta_rows)
            peak = ensemble.check_capture_budget(member["capture_members"], fold_rows, len(ds.CLASS_LABELS))
            _note(f"ensemble evaluation peak estimate: {peak} bytes over {sum(fold_rows)} test windows "
                  f"(captured {ensemble.capture_bytes(member['capture_members'], fold_rows, len(ds.CLASS_LABELS))} bytes, "
                  f"limit {ensemble.MAX_EVALUATION_PEAK_BYTES})")
        for fold_index in range(cfg.n_splits):
            if time.monotonic() >= deadline:
                raise TimeoutError("independent evaluation exceeded the run duration limit")
            # Validate the original fold and the inner fit/validation boundary before
            # invoking a framework. A missing selected symbol is a failed run.
            selected_inner = ds.independent_fold_data(
                train_symbols, train_dates, n_splits=cfg.n_splits, gap=cfg.gap,
                fold_index=fold_index,
                indicator_export_paths=cfg.indicator_channel_export_paths,
                inner=True, calibration=release_holdout and fold_index == cfg.n_splits - 1,
                **common,
            )
            if fold_index == cfg.n_splits - 1:
                import model_analysis
                model_analysis.validate_population(selected_inner, run_limits)
            staged_model = tmp_root / f"fold_{fold_index}" / out_path.name
            staged_model.parent.mkdir(parents=True, exist_ok=True)
            trainer_argv = _build_trainer_argv(cfg, train_dir, staged_model,
                                               bagging=member.get("bagging"), seed=member.get("seed"))
            trainer_argv += ["--outer-fold-index", str(fold_index)]
            if fold_index == cfg.n_splits - 1 and (release_calibrated or meta_segment):
                trainer_argv += ["--calibration" if release_calibrated else "--meta-segment"]
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
            fold_capture: Dict[str, Tuple[np.ndarray, np.ndarray, np.ndarray]] | None = None
            if member.get("capture") is not None:
                fold_capture = member["capture"][fold_index] = {}
            rows = evaluate_folds(
                _onnx_predict_fn(staged_model), train_symbols,
                n_splits=cfg.n_splits, gap=cfg.gap, dates=train_dates,
                indicator_export_paths=cfg.indicator_channel_export_paths,
                fold_index=fold_index, capture=fold_capture, **common,
            )
            if meta_segment and fold_index == cfg.n_splits - 1:
                member["meta_capture"].update(_score_meta_segment(
                    _onnx_predict_fn(staged_model), cfg, train_symbols, train_dates, common))
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
        cpcv_flat: Dict[str, float] = {}
        if cfg.cpcv and standalone:
            release_report["cpcv"] = _run_cpcv(cfg, train_dir, train_symbols, train_dates, common=common,
                                               deadline=deadline, tmp_root=tmp_root)
            cpcv_flat = {"cpcv_combination_count": float(release_report["cpcv"]["combination_count"])}
            for name, stats in release_report["cpcv"]["summary"].items():
                cpcv_flat[f"cpcv_{name}_mean"] = stats["mean"]
                cpcv_flat[f"cpcv_{name}_std"] = stats["std"]
        release_report["resource_limits"] = run_limits
        if cfg.framework != "tensorflow":
            pointer = checkpoint_root / f"fold_{cfg.n_splits - 1}" / "best.json"
            if pointer.exists():
                state, _ = checkpoints.checked_blob(pointer)
                release_report["training_lineage"] = dict(state["context"]["lineage"], run_id=run_id)
            release_report["checkpoint_run_id"] = run_id
            if cfg.pretraining != onnx_meta.PRETRAINING_NONE:
                ssl_pointer = checkpoint_root / f"fold_{cfg.n_splits - 1}" / "ssl_pretrain.json"
                if not ssl_pointer.is_file():
                    raise RuntimeError("pretraining was requested but the release fold has no verified encoder checkpoint")
                ssl_state = checkpoints.read_json(ssl_pointer)
                release_report["pretraining"] = dict(ssl_state["report"], checkpoint_sha256=ssl_state["sha256"])
        release_report["run_budget_seconds_at_child_start"] = run_budget_seconds
        import model_analysis
        _note("computing train-only regimes and selected-model channel importance")
        analysis = model_analysis.build(
            cfg, release_path, train_symbols, train_dates, full_symbols, full_dates,
            predict=_onnx_predict_fn(release_path), checkpoint_root=checkpoint_root, deadline=deadline,
            resource_limits=run_limits, calibration=release_holdout)
        release_analysis = release_path.with_name(release_path.name + ".analysis.json")
        checkpoints.atomic_json(release_analysis, analysis)
        release_report["data_revision"] = analysis["data_revision"]
        if time.monotonic() >= deadline:
            raise TimeoutError("model analysis exceeded the training run budget")
        release_calibration = None
        if cfg.calibration:
            release_report["calibration"] = dict(calibration_plan)
        if release_calibrated:
            import calibration
            _note("fitting the temperature on the dedicated calibration segment")
            calibration_document = calibration.build(
                cfg, release_path, train_symbols, train_dates, full_symbols, full_dates,
                predict=_onnx_predict_fn(release_path), deadline=deadline)
            release_calibration = release_path.with_name(release_path.name + ".calibration.json")
            checkpoints.atomic_json(release_calibration, calibration_document)
            release_report["calibration"].update(
                status=calibration_document["status"],
                temperature=calibration_document["temperature"],
                outer_test=calibration_document["evaluation"]["outer_test"])
            if calibration_document["status"] != calibration.STATUS_AVAILABLE:
                _note(f"calibration is not applied: {calibration_document['status']} "
                      f"(T = {calibration_document['temperature']}); the sidecar is published for diagnosis only")
        release_explain = None
        try:
            if not member.get("explain", True):
                raise LookupError("not requested for an ensemble member")
            import explain
            _note("computing offline Shapley contributions and partial dependence")
            explanation = explain.build(
                cfg, release_path, train_symbols, train_dates, full_symbols, full_dates,
                deadline=deadline, calibration=release_calibrated)
            release_explain = release_path.with_name(release_path.name + ".explain.json")
            checkpoints.atomic_json(release_explain, explanation)
        except TimeoutError:
            raise
        except LookupError:
            release_explain = None
        except Exception as explain_error:  # explanation is optional; the model must still publish
            _note(f"explanation not generated: {type(explain_error).__name__}: {explain_error}")
            release_explain = None
            # recorded, so the application can say "failed" instead of "never generated" (mirror: C# ModelExplanationContract.FailedStatus)
            release_report["explanation"] = {"status": EXPLANATION_FAILED_STATUS, "error": type(explain_error).__name__}
        if time.monotonic() >= deadline:
            raise TimeoutError("model explanation exceeded the training run budget")
        staged_metrics = Path(metrics.write_report(release_report, release_path))
        release_attention = _release_attention_sidecar(cfg, release_path)
        _publish_release_bundle(release_path, staged_metrics,
                                release_scaler if cfg.fixed_zscore else None,
                                out_path, release_report, release_analysis,
                                release_calibration, release_explain, release_attention)
        scaler_path = out_path.with_name(out_path.name + ".scaler.json")
        if standalone:
            _stage("export")
            _progress(92)
            _artifact("onnx", out_path)
            _artifact("metrics", metrics_path)
            _artifact("analysis", out_path.with_name(out_path.name + ".analysis.json"))
            if release_calibration is not None:
                _artifact("calibration", out_path.with_name(out_path.name + ".calibration.json"))
            if release_explain is not None:
                _artifact("explain", out_path.with_name(out_path.name + ".explain.json"))
            if release_attention is not None:
                _artifact("attention", _attention_module().sidecar_path(out_path))
            if cfg.fixed_zscore:
                _artifact("scaler", scaler_path)
            _metric({**{key: value for key, value in release_report.items()
                        if isinstance(value, (int, float)) and math.isfinite(value)}, **cpcv_flat})
    finally:
        ownership.close()
        shutil.rmtree(tmp_root, ignore_errors=True)
    return {"out_path": out_path, "metrics_path": metrics_path, "report": release_report}


def _ensemble_plan(cfg: JobConfig, run_id: str, group_stem: str, deadline: float) -> List[dict]:
    """One entry per member: its derived single-model config and its trainer-side bagging / seed."""
    run_seed = int(cfg.hyperparameters.get("seed", ensemble.DEFAULT_RUN_SEED))
    if run_seed < 0:
        raise ValueError("the run seed must be non-negative")
    remaining = None if cfg.resource_limits is None else max(deadline - time.monotonic(), MIN_MEMBER_BUDGET_SECONDS)
    plan = []
    if cfg.ensemble_method == ensemble.METHOD_BAGGING:
        slots = [(cfg.framework, cfg.architecture.strip().lower())] * cfg.ensemble_members
    else:
        slots = [ensemble.parse_base(text) for text in cfg.stacking_bases]
    bag_fraction, bag_block_length, tuned = ensemble.split_bagging_options(cfg.hyperparameters)
    if cfg.ensemble_method != ensemble.METHOD_BAGGING and len(tuned) != len(cfg.hyperparameters):
        raise ValueError("hyperparameters bag_fraction / bag_block_length apply to bagging only")
    own_hyperparameters = {key: value for key, value in tuned.items() if _normalize_flag(key) != "seed"}
    for index, (framework, architecture) in enumerate(slots):
        member_cfg = dataclasses.replace(
            cfg, framework=framework, architecture=architecture,
            ensemble_method=ensemble.METHOD_NONE, ensemble_members=ensemble.DEFAULT_BAGGING_MEMBERS,
            stacking_bases=[], stacking_weight_source=ensemble.WEIGHT_SOURCE_PAST_OOF,
            output_name=ensemble.member_stem(group_stem, index),
            run_id=ensemble.member_run_id(run_id, index),
            hyperparameters=dict(own_hyperparameters) if framework == cfg.framework else {},
            remaining_run_budget_seconds=remaining)
        member_cfg.validate()
        entry = {"cfg": member_cfg, "framework": framework, "architecture": architecture, "bagging": None,
                 "seed": None}
        if cfg.ensemble_method == ensemble.METHOD_BAGGING:
            entry["bagging"] = ds.BaggingSpec(index=index, count=cfg.ensemble_members, seed=run_seed,
                                              fraction=bag_fraction, block_length=bag_block_length)
            entry["bagging"].validate()
            entry["seed"] = ds.bagging_member_seed(run_seed, index)
        elif "seed" in cfg.hyperparameters:
            entry["seed"] = run_seed
        plan.append(entry)
    return plan


def _run_ensemble(cfg: JobConfig, *, started: datetime.datetime, run_limits: dict, run_budget_seconds: float,
                  deadline: float) -> int:
    """Train every member as an ordinary independently evaluated model, then weigh and describe the ensemble.

    The release artifact of each member is a normal generation (ONNX + metrics + analysis sidecars); the
    ensemble itself is only ``<stem>.ensemble.json`` (see :mod:`ensemble`). ``ARTIFACT:onnx`` names member 1
    so older consumers still see one valid model; ``ARTIFACT:ensemble_member`` is printed once per member as soon
    as that member is published.
    """
    global _progress_window
    run_id = cfg.run_id or f"{started:%Y%m%d-%H%M%S}{started.microsecond // 1000:03d}"
    if not checkpoints._RUN.fullmatch(run_id):
        raise ValueError("unsafe run identifier")
    group_stem = _derive_output_stem(cfg, run_id)
    _note(f"ensemble run_id={run_id} method={cfg.ensemble_method} members="
          f"{cfg.ensemble_members if cfg.ensemble_method == ensemble.METHOD_BAGGING else len(cfg.stacking_bases)}")
    ARTIFACTS_DIR.mkdir(parents=True, exist_ok=True)
    # Where the members' checkpoint directories (`<run_id>-mNN`, see ensemble.member_run_id) are created, announced before
    # any member starts so the host can roll them back when the run fails, even when the first member never publishes.
    _artifact("ensemble_checkpoints", ARTIFACTS_DIR / checkpoints.DIRECTORY_NAME)
    plan = _ensemble_plan(cfg, run_id, group_stem, deadline)
    results: List[dict] = []
    captures: List[Dict[int, Dict[str, Tuple[np.ndarray, np.ndarray, np.ndarray]]]] = []
    meta_segment = cfg.stacking_weight_source == ensemble.WEIGHT_SOURCE_META
    meta_captures: List[Dict[str, Tuple[np.ndarray, np.ndarray, np.ndarray]]] = []
    count = len(plan)
    try:
        for index, entry in enumerate(plan):
            if time.monotonic() >= deadline:
                raise TimeoutError("ensemble training exceeded the run duration limit")
            _progress_window = (90 * index // count, 90 * (index + 1) // count)
            captured: Dict[int, Dict[str, Tuple[np.ndarray, np.ndarray, np.ndarray]]] = {}
            captures.append(captured)
            meta_captures.append({})
            results.append(_train_one(
                entry["cfg"], started=started, run_limits=run_limits,
                run_budget_seconds=run_budget_seconds, deadline=deadline,
                member={"bagging": entry["bagging"], "seed": entry["seed"], "capture": captured,
                        "explain": False, "capture_members": count if index == 0 else 0,
                        "meta_segment": meta_segment, "meta_capture": meta_captures[-1]}))
            # Announced the moment the member is published (not at the end of the run), so the host can remove the
            # published members of an ensemble that later fails, is cancelled or is killed.
            _artifact("ensemble_member", Path(results[-1]["out_path"]))
    finally:
        _progress_window = (0, 100)
    _stage("evaluate")
    evaluation = ensemble.evaluate(cfg.ensemble_method, cfg.n_splits, captures, meta_captures if meta_segment else None)
    fit = evaluation["weights_fit"]
    if meta_segment:
        shift = fit["weight_shift_l1"]
        _note(f"stacking weights fitted on the release meta segment ({fit['fit_samples']} rows); "
              + ("the past-OOF comparison is unavailable (too few earlier out-of-fold rows)" if shift is None
                 else f"L1 distance to the past-OOF weights: {shift:.4f}"))
    if fit.get("converged") is False:
        _note(f"WARNING: stacking weight EM did not converge within {fit['iterations']} iterations "
              f"(final_delta={fit['final_delta']:.3e}, kkt_violation={fit['kkt_violation']:.3e}); "
              "the weights are an improving approximation and the diagnostics are recorded in the manifest")
    members = []
    for index, (entry, result) in enumerate(zip(plan, results)):
        out_path = Path(result["out_path"])
        report = result["report"]
        members.append({
            "index": index, "file": out_path.name, "sha256": ensemble.file_sha256(out_path),
            "metrics_file": Path(result["metrics_path"]).name, "weight": evaluation["weights"][index],
            "framework": entry["framework"], "architecture": entry["architecture"],
            "release_fold_macro_f1": report.get("fold_macro_f1"), "release_fold_accuracy": report.get("fold_accuracy"),
            "bagging": None if entry["bagging"] is None else {
                "index": entry["bagging"].index, "run_seed": entry["bagging"].seed, "trainer_seed": entry["seed"],
                "fraction": entry["bagging"].fraction},
        })
    bagging_doc = None
    if cfg.ensemble_method == ensemble.METHOD_BAGGING:
        # every member shares the job-level tunables, so the first member's spec is the group's
        group_spec = plan[0]["bagging"]
        bagging_doc = {"members": count, "resample": "time_series_block_bootstrap",
                       "block_length": group_spec.resolved_block_length(cfg.window_size, cfg.horizon),
                       "block_length_source": "default" if group_spec.block_length is None else "hyperparameter",
                       "fraction": group_spec.fraction,
                       "fraction_source": "default" if group_spec.fraction == ds.BAGGING_FRACTION else "hyperparameter",
                       "rounding": "ceil_blocks",
                       "seed": "SeedSequence(run_seed, spawn_key=(member, fold, symbol_rank))",
                       "early_stopping": "member_inner_validation_tail"}
    manifest = ensemble.build_manifest(
        method=cfg.ensemble_method, run_id=run_id, timeframe=cfg.timeframe, horizon=cfg.horizon,
        class_order=",".join(ds.CLASS_LABELS), members=members, evaluation=evaluation, bagging=bagging_doc,
        resource_limits=run_limits, weight_source=cfg.stacking_weight_source)
    manifest_path = ARTIFACTS_DIR / f"{group_stem}{ensemble.ENSEMBLE_FILE_SUFFIX}"
    checkpoints.atomic_json(manifest_path, manifest)
    _artifact("ensemble", manifest_path)  # announced at once so the host can roll the whole run back if it fails later
    _stage("export")
    _progress(95)
    first = results[0]
    _artifact("onnx", Path(first["out_path"]))
    _artifact("metrics", Path(first["metrics_path"]))
    release_row = evaluation["folds"][-1]["metrics"]
    _metric({**{key: value for key, value in release_row.items() if math.isfinite(value)},
             "ensemble_members": float(count)})
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
            assert "SHORT" in str(exc) and "2019-06-01" in str(exc) and "out-of-sample tail of 60" in str(exc)
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
        import calibration as _calibration_mod
        import explain as _explain_mod
        import model_analysis as _analysis_mod
        original_stage_builders = (_analysis_mod.build, _explain_mod.build, _calibration_mod.build)

        def stub_analysis(_cfg, model_path, *_args, **_kwargs):
            # The substitute trainer writes bytes, not an ONNX graph, so the ORT-backed builders cannot run.
            return {"model_sha256": _file_sha256(model_path), "data_revision": "0" * 64}

        def stub_explain(*_args, **_kwargs):
            raise RuntimeError("substitute model is not an ONNX graph")

        calibration_status = ["available"]

        def stub_calibration(_cfg, model_path, *_args, **_kwargs):
            return {"model_sha256": _file_sha256(model_path), "data_revision": "0" * 64,
                    "status": calibration_status[0], "temperature": 1.0 if calibration_status[0] == "available" else 0.01,
                    "evaluation": {"outer_test": {"sample_count": 1}}}
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
            _analysis_mod.build, _explain_mod.build, _calibration_mod.build = (
                stub_analysis, stub_explain, stub_calibration)
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

            # An explanation that cannot be generated never blocks publication (it is optional).
            assert not list((root / "artifacts").glob("*.explain.json"))
            assert not list((root / "artifacts").glob("*.calibration.json"))

            # T12: a requested calibration is applied to the release fold's trainer only and its
            # sidecar is published beside the model; without the flag nothing changes.
            globals()["_publish_release_bundle"] = original_publish
            calibration_calls = []
            def recording_dispatch(_cfg, argv):
                if "--preflight" not in argv:
                    calibration_calls.append(
                        (int(argv[argv.index("--outer-fold-index") + 1]), "--calibration" in argv))
                return fake_dispatch(_cfg, argv)
            globals()["_dispatch"] = recording_dispatch
            config_file.write_text(json.dumps({
                "symbols": ["A", "B"], "architecture": "lstm", "window_size": 30, "horizon": 5,
                "n_splits": 3, "gap": 0, "run_id": "selfcheck_calibrated", "calibration": True,
            }), encoding="utf-8")
            protocol = io.StringIO()
            with contextlib.redirect_stdout(protocol):
                assert main(["--config", str(config_file)]) == 0
            assert calibration_calls == [(0, False), (1, False), (2, True)], calibration_calls
            calibrated = [p for p in (root / "artifacts").glob("*selfcheck_calibrated*.onnx")]
            assert len(calibrated) == 1
            sidecar = Path(str(calibrated[0]) + ".calibration.json")
            assert json.loads(sidecar.read_text(encoding="utf-8"))["temperature"] == 1.0
            assert "ARTIFACT:calibration:" in protocol.getvalue()
            assert "ARTIFACT:explain:" not in protocol.getvalue()
            calibrated_report = json.loads(Path(str(calibrated[0]) + ".metrics.json").read_text())
            assert calibrated_report["calibration"]["status"] == "available"
            # the selfcheck's stand-in model cannot be explained: the run still publishes and records why there is no sidecar
            assert calibrated_report["explanation"]["status"] == EXPLANATION_FAILED_STATUS
            assert isinstance(calibrated_report["explanation"]["error"], str) and calibrated_report["explanation"]["error"]
            assert calibrated_report["calibration"]["temperature"] == 1.0
            # A fit at the temperature boundary still publishes its sidecar and records the declared status.
            calibration_status[0] = "temperature_at_boundary"
            config_file.write_text(json.dumps({
                "symbols": ["A", "B"], "architecture": "lstm", "window_size": 30, "horizon": 5,
                "n_splits": 3, "gap": 0, "run_id": "selfcheck_boundary", "calibration": True,
            }), encoding="utf-8")
            with contextlib.redirect_stdout(io.StringIO()):
                assert main(["--config", str(config_file)]) == 0
            boundary = list((root / "artifacts").glob("*selfcheck_boundary*.onnx"))
            assert len(boundary) == 1
            boundary_sidecar = json.loads(Path(str(boundary[0]) + ".calibration.json").read_text(encoding="utf-8"))
            assert boundary_sidecar["status"] == "temperature_at_boundary" and boundary_sidecar["temperature"] == 0.01
            boundary_report = json.loads(Path(str(boundary[0]) + ".metrics.json").read_text())
            assert boundary_report["calibration"]["status"] == "temperature_at_boundary"
            calibration_status[0] = "available"
            # JobConfig contract: calibration must be a boolean on a classification job.
            for bad_calibration in ({"calibration": 1}, {"calibration": True, "target_type": "regression"}):
                try:
                    JobConfig.from_json(json.dumps({**base, **bad_calibration}))
                except ValueError:
                    pass
                else:
                    raise AssertionError(f"invalid calibration request accepted: {bad_calibration}")

            # T24: the attention sidecar the PyTorch trainer wrote beside the release model is published beside it and bound to
            # it (parent SHA-256); a later run without one retires a stale sidecar; a sidecar of another model is refused.
            import attention_export as _attention_mod
            import train_pytorch as _pytorch_mod
            attention_model = _attention_mod.AttentionLstmClassifier(3, 8, 1, 0.0, 3, 2).eval()
            sidecar_for = {"mode": "own"}

            def attention_dispatch(_cfg, argv):
                code = fake_dispatch(_cfg, argv)
                if "--preflight" in argv or _cfg.architecture != _attention_mod.ARCH:
                    return code
                staged = Path(argv[argv.index("--out") + 1])
                parent = staged
                if sidecar_for["mode"] == "other":  # a sidecar that belongs to different model bytes
                    parent = staged.with_name("other_model.onnx")
                    parent.write_bytes(b"another model")
                written = _attention_mod.export_sidecar(attention_model, parent, window=4, channels=3,
                                                        opset=_pytorch_mod.DEFAULT_OPSET, feature_contract_hash="0" * 64)
                if parent != staged:
                    written.replace(_attention_mod.sidecar_path(staged))
                return code
            globals()["_dispatch"] = attention_dispatch
            attention_job = {"symbols": ["A", "B"], "architecture": _attention_mod.ARCH, "window_size": 30, "horizon": 5,
                             "n_splits": 3, "gap": 0, "run_id": "selfcheck_attention", "output_name": "selfcheck_attention_model"}
            config_file.write_text(json.dumps(attention_job), encoding="utf-8")
            protocol = io.StringIO()
            with contextlib.redirect_stdout(protocol):
                assert main(["--config", str(config_file)]) == 0
            attention_onnx = root / "artifacts" / "selfcheck_attention_model.onnx"
            attention_side = _attention_mod.sidecar_path(attention_onnx)
            assert attention_onnx.read_bytes() == b"model-2" and attention_side.is_file()
            assert _attention_mod.check_binding(attention_side, attention_onnx)[_attention_mod.KEY_HEADS] == "2"
            assert f"ARTIFACT:attention:{attention_side}" in protocol.getvalue()
            assert not list((root / "artifacts").glob("*.incoming")) and len(list((root / "artifacts").glob("*.attention.onnx"))) == 1
            # the same output without a sidecar (another architecture) retires the stale one and announces none
            config_file.write_text(json.dumps({**attention_job, "architecture": "lstm", "run_id": "selfcheck_attention_retire"}),
                                   encoding="utf-8")
            protocol = io.StringIO()
            with contextlib.redirect_stdout(protocol):
                assert main(["--config", str(config_file)]) == 0
            assert attention_onnx.is_file() and not attention_side.exists() and "ARTIFACT:attention:" not in protocol.getvalue()
            # a sidecar bound to other bytes never reaches the artifacts folder and the published model stays as it was
            sidecar_for["mode"] = "other"
            config_file.write_text(json.dumps({**attention_job, "run_id": "selfcheck_attention_other"}), encoding="utf-8")
            try:
                with contextlib.redirect_stdout(io.StringIO()):
                    main(["--config", str(config_file)])
            except ValueError as exc:
                assert "another generation" in str(exc), exc
            else:
                raise AssertionError("a sidecar of another model must be refused")
            assert not attention_side.exists() and attention_onnx.read_bytes() == b"model-2"
            globals()["_dispatch"] = fake_dispatch

            # T13: bagging trains M independent members (own bootstrap + seed per member), mixes them with
            # equal weights and describes the group in <stem>.ensemble.json; artifact lines name every member.
            member_calls = []
            def member_dispatch(_cfg, argv):
                if "--preflight" not in argv:
                    member_calls.append((_cfg.framework, _cfg.architecture,
                                         argv[argv.index("--bag-index") + 1] if "--bag-index" in argv else None,
                                         argv[argv.index("--seed") + 1] if "--seed" in argv else None,
                                         int(argv[argv.index("--outer-fold-index") + 1])))
                return fake_dispatch(_cfg, argv)
            globals()["_dispatch"] = member_dispatch
            ensemble_job = {"symbols": ["A", "B"], "architecture": "lstm", "window_size": 30, "horizon": 5,
                            "n_splits": 3, "gap": 0, "ensemble_method": "bagging", "ensemble_members": 2}
            config_file.write_text(json.dumps({**ensemble_job, "run_id": "selfcheck_bagging"}), encoding="utf-8")
            protocol = io.StringIO()
            with contextlib.redirect_stdout(protocol):
                assert main(["--config", str(config_file)]) == 0
            lines = protocol.getvalue().splitlines()
            assert sum(line.startswith("ARTIFACT:ensemble_member:") for line in lines) == 2
            # the checkpoint root is announced once, before the first member, so the host can roll `<run_id>-mNN` back
            announced_roots = [i for i, line in enumerate(lines) if line.startswith("ARTIFACT:ensemble_checkpoints:")]
            first_member = min(i for i, line in enumerate(lines) if line.startswith("ARTIFACT:ensemble_member:"))
            assert len(announced_roots) == 1 and announced_roots[0] < first_member
            assert Path(lines[announced_roots[0]].split(":", 2)[2]).name == "checkpoints"
            assert sum(line.startswith("ARTIFACT:ensemble:") for line in lines) == 1
            assert sum(line.startswith("ARTIFACT:onnx:") for line in lines) == 1
            assert [line for line in lines if line.startswith("ARTIFACT:onnx:")][0].endswith("_m01.onnx")
            assert lines.count("STAGE:done") == 1
            progress = [int(line.split(":")[1]) for line in lines if line.startswith("PROGRESS:")]
            assert progress == sorted(progress) and progress[-1] == 100, progress
            seeds = {(call[2], call[3]) for call in member_calls}
            assert len(seeds) == 2 and all(bag is not None and seed is not None for bag, seed in seeds), member_calls
            assert [call[4] for call in member_calls] == [0, 1, 2, 0, 1, 2], member_calls
            manifest_file = next((root / "artifacts").glob("*selfcheck_bagging*.ensemble.json"))
            manifest = json.loads(manifest_file.read_text(encoding="utf-8"))
            ensemble.validate_manifest(manifest)
            assert manifest["method"] == "bagging" and manifest["weights"] == [0.5, 0.5]
            assert manifest["bagging"]["block_length"] == ds.bagging_block_length(30, 5) == 34
            assert [m["file"].endswith(f"_m0{i + 1}.onnx") for i, m in enumerate(manifest["members"])] == [True, True]
            assert all((root / "artifacts" / m["file"]).is_file() and (root / "artifacts" / m["metrics_file"]).is_file()
                       for m in manifest["members"])
            assert all(m["sha256"] == _file_sha256(root / "artifacts" / m["file"]) for m in manifest["members"])
            assert [f["status"] for f in manifest["folds"]] == ["evaluated"] * 3
            assert manifest["folds"][-1]["metrics"]["fold_is_holdout"] == 1.0
            # a published member is announced when it is published: with the second member failing, the first is
            # already named (the host can remove it) and no ensemble manifest is announced
            def second_member_fails(_cfg, argv):
                if "--bag-index" in argv and argv[argv.index("--bag-index") + 1] == "1":
                    return 1
                return fake_dispatch(_cfg, argv)
            globals()["_dispatch"] = second_member_fails
            config_file.write_text(json.dumps({**ensemble_job, "run_id": "selfcheck_bagging_failed"}), encoding="utf-8")
            protocol = io.StringIO()
            with contextlib.redirect_stdout(protocol), contextlib.redirect_stderr(io.StringIO()):
                try:
                    failed_exit = main(["--config", str(config_file)])
                except ValueError:  # the member's preflight failure surfaces as the run error
                    failed_exit = 1
            assert failed_exit != 0
            failed_lines = protocol.getvalue().splitlines()
            announced = [line for line in failed_lines if line.startswith("ARTIFACT:ensemble_member:")]
            assert len(announced) == 1 and announced[0].endswith("_m01.onnx"), announced
            assert not any(line.startswith("ARTIFACT:ensemble:") for line in failed_lines)
            assert Path(announced[0].split(":", 2)[2]).is_file()
            globals()["_dispatch"] = member_dispatch

            # an ensemble whose captured probabilities cannot fit the limit fails before any member is trained
            member_calls.clear()
            real_limit = ensemble.MAX_EVALUATION_PEAK_BYTES
            ensemble.MAX_EVALUATION_PEAK_BYTES = 1
            try:
                config_file.write_text(json.dumps({**ensemble_job, "run_id": "selfcheck_bagging_big"}), encoding="utf-8")
                with contextlib.redirect_stdout(io.StringIO()):
                    try:
                        main(["--config", str(config_file)])
                    except ValueError as error:
                        assert "above the limit" in str(error), error
                    else:  # pragma: no cover
                        raise AssertionError("an oversized capture must fail the run")
                assert member_calls == [], member_calls
            finally:
                ensemble.MAX_EVALUATION_PEAK_BYTES = real_limit

            # tunable bagging: the job-level hyperparameters reach every member's trainer once, are recorded in the
            # manifest, are validated before anything runs, and apply to bagging only
            assert manifest["bagging"]["block_length_source"] == "default" and manifest["bagging"]["fraction"] == 0.8
            tuned_argv: list = []
            def tuned_dispatch(_cfg, argv):
                if "--preflight" not in argv:
                    tuned_argv.append(list(argv))
                return fake_dispatch(_cfg, argv)
            globals()["_dispatch"] = tuned_dispatch
            config_file.write_text(json.dumps({**ensemble_job, "run_id": "selfcheck_bagging_tuned",
                                               "hyperparameters": {"bag_fraction": "0.5", "bag_block_length": "20"}}),
                                   encoding="utf-8")
            with contextlib.redirect_stdout(io.StringIO()):
                assert main(["--config", str(config_file)]) == 0
            assert tuned_argv and all(
                a.count("--bag-fraction") == 1 and a[a.index("--bag-fraction") + 1] == "0.5"
                and a.count("--bag-block-length") == 1 and a[a.index("--bag-block-length") + 1] == "20"
                for a in tuned_argv), tuned_argv
            tuned_doc = json.loads(next((root / "artifacts").glob("*selfcheck_bagging_tuned*.ensemble.json")
                                        ).read_text(encoding="utf-8"))["bagging"]
            assert (tuned_doc["block_length"], tuned_doc["fraction"]) == (20, 0.5)
            assert tuned_doc["block_length_source"] == tuned_doc["fraction_source"] == "hyperparameter"
            tuned_argv.clear()
            for index, bad_tuning in enumerate(({"bag_fraction": "0"}, {"bag_fraction": "1.5"}, {"bag_fraction": "x"},
                                                {"bag_fraction": "nan"}, {"bag_block_length": "0"},
                                                {"bag_block_length": "2.5"}, {"bag_block_length": "-3"})):
                config_file.write_text(json.dumps({**ensemble_job, "run_id": f"selfcheck_bad_tuning_{index}",
                                                   "hyperparameters": bad_tuning}), encoding="utf-8")
                try:
                    with contextlib.redirect_stdout(io.StringIO()):
                        main(["--config", str(config_file)])
                except ValueError:
                    pass
                else:  # pragma: no cover
                    raise AssertionError(f"bagging tuning {bad_tuning} must be rejected")
            config_file.write_text(json.dumps({**ensemble_job, "run_id": "selfcheck_stack_tuning",
                                               "ensemble_method": "stacking", "stacking_bases": ["pytorch:lstm", "lightgbm"],
                                               "hyperparameters": {"bag_fraction": "0.5"}}), encoding="utf-8")
            try:
                with contextlib.redirect_stdout(io.StringIO()):
                    main(["--config", str(config_file)])
            except ValueError as error:
                assert "bagging only" in str(error), error
            else:  # pragma: no cover
                raise AssertionError("bagging tuning must be rejected for stacking")
            assert tuned_argv == [], "nothing is trained for a rejected tuning"
            globals()["_dispatch"] = member_dispatch

            # stacking: heterogeneous bases, weights fitted on the strictly earlier folds, release fold unfitted.
            member_calls.clear()
            config_file.write_text(json.dumps({**ensemble_job, "run_id": "selfcheck_stacking",
                                               "ensemble_method": "stacking",
                                               "stacking_bases": ["pytorch:lstm", "lightgbm"]}), encoding="utf-8")
            with contextlib.redirect_stdout(io.StringIO()):
                assert main(["--config", str(config_file)]) == 0
            assert {(c[0], c[1]) for c in member_calls} == {("pytorch", "lstm"), ("lightgbm", "gbdt")}
            assert all(c[2] is None for c in member_calls)  # stacking bases are not resampled
            stacking = json.loads(next((root / "artifacts").glob("*selfcheck_stacking*.ensemble.json")
                                       ).read_text(encoding="utf-8"))
            ensemble.validate_manifest(stacking)
            assert stacking["weights_fit"]["method"] == "simplex_nll_em" and stacking["weights_fit"]["fit_folds"] == [0, 1]
            assert stacking["weights_fit"]["row_overlap"] == 0 and stacking["folds"][0]["status"] == "no_past_fold"
            assert abs(sum(stacking["weights"]) - 1.0) < 1e-12
            assert stacking["weight_source"] == "past_out_of_fold" and stacking["weight_transfer_warning"] is True

            # D2: stacking weights fitted on a release-fold meta segment. Only the release fold's trainer is asked to leave the
            # carved tail out (--meta-segment, never --calibration), and the manifest records the meta population.
            meta_flags = []
            def meta_dispatch(_cfg, argv):
                if "--preflight" not in argv:
                    meta_flags.append((int(argv[argv.index("--outer-fold-index") + 1]), "--meta-segment" in argv,
                                       "--calibration" in argv))
                return fake_dispatch(_cfg, argv)
            globals()["_dispatch"] = meta_dispatch
            meta_job = {**ensemble_job, "ensemble_method": "stacking", "stacking_bases": ["pytorch:lstm", "lightgbm"],
                        "stacking_weight_source": "release_meta_segment"}
            config_file.write_text(json.dumps({**meta_job, "run_id": "selfcheck_stacking_meta"}), encoding="utf-8")
            with contextlib.redirect_stdout(io.StringIO()):
                assert main(["--config", str(config_file)]) == 0
            assert sorted(meta_flags) == [(0, False, False)] * 2 + [(1, False, False)] * 2 + [(2, True, False)] * 2, meta_flags
            meta_manifest = json.loads(next((root / "artifacts").glob("*selfcheck_stacking_meta*.ensemble.json")
                                            ).read_text(encoding="utf-8"))
            ensemble.validate_manifest(meta_manifest)
            assert meta_manifest["weight_source"] == "release_meta_segment" and "weight_transfer_warning" not in meta_manifest
            meta_fit = meta_manifest["weights_fit"]
            assert meta_fit["population"] == "release_meta_segment" and meta_fit["fit_folds"] == [] and meta_fit["row_overlap"] == 0
            assert meta_fit["fit_samples"] >= ensemble.MIN_META_SAMPLES and "past_oof" in meta_fit and "weight_shift_l1" in meta_fit
            assert abs(sum(meta_manifest["weights"]) - 1.0) < 1e-12
            # a meta segment below the minimum fails before any member is trained
            real_minimum = ensemble.MIN_META_SAMPLES
            ensemble.MIN_META_SAMPLES = 10 ** 9
            try:
                meta_flags.clear()
                config_file.write_text(json.dumps({**meta_job, "run_id": "selfcheck_stacking_meta_small"}), encoding="utf-8")
                try:
                    with contextlib.redirect_stdout(io.StringIO()):
                        main(["--config", str(config_file)])
                except ValueError as error:
                    assert "meta segment" in str(error), error
                else:  # pragma: no cover
                    raise AssertionError("a too small meta segment must fail the run")
                assert meta_flags == [], "nothing is trained for a too small meta segment"
            finally:
                ensemble.MIN_META_SAMPLES = real_minimum
            # the key is validated: unknown value, and any non-default value outside stacking
            for bad_source in ({**meta_job, "stacking_weight_source": "elsewhere"},
                               {**ensemble_job, "stacking_weight_source": "release_meta_segment"},
                               {"symbols": ["A"], "architecture": "lstm", "window_size": 30, "horizon": 5,
                                "stacking_weight_source": "release_meta_segment"}):
                try:
                    JobConfig.from_json(json.dumps(bad_source))
                except ValueError:
                    pass
                else:  # pragma: no cover
                    raise AssertionError(f"invalid weight source accepted: {bad_source}")
            assert JobConfig.from_json(json.dumps(meta_job)).stacking_weight_source == "release_meta_segment"
            globals()["_dispatch"] = member_dispatch

            # T14: CPCV scores every combination of a fresh single-model recipe; nothing is published for them and
            # the release model / outer-fold evidence are unchanged. No path is constructed (13-17=B).
            cpcv_calls = []
            def cpcv_dispatch(_cfg, argv):
                if "--cpcv-combination" in argv:
                    assert "--outer-fold-index" not in argv and "--calibration" not in argv, argv
                    cpcv_calls.append((int(argv[argv.index("--cpcv-combination") + 1]),
                                       int(argv[argv.index("--cpcv-groups") + 1]),
                                       int(argv[argv.index("--cpcv-test-groups") + 1])))
                    Path(argv[argv.index("--out") + 1]).write_bytes(b"cpcv-model")
                    return 0
                return fake_dispatch(_cfg, argv)
            globals()["_dispatch"] = cpcv_dispatch
            config_file.write_text(json.dumps({
                "symbols": ["A", "B"], "architecture": "lstm", "window_size": 30, "horizon": 5, "n_splits": 3,
                "gap": 0, "run_id": "selfcheck_cpcv", "cpcv": True, "cpcv_groups": 4, "cpcv_test_groups": 2,
            }), encoding="utf-8")
            protocol = io.StringIO()
            with contextlib.redirect_stdout(protocol):
                assert main(["--config", str(config_file)]) == 0
            assert cpcv_calls == [(i, 4, 2) for i in range(6)], cpcv_calls
            lines = protocol.getvalue().splitlines()
            assert "STAGE:cpcv" in lines and lines.count("STAGE:done") == 1
            progress = [int(line.split(":")[1]) for line in lines if line.startswith("PROGRESS:")]
            assert progress == sorted(progress) and progress[-1] == 100, progress
            cpcv_published = next((root / "artifacts").glob("*selfcheck_cpcv*.onnx"))
            assert cpcv_published.read_bytes() == b"model-2", "the release model is the outer-fold model, not a CPCV member"
            cpcv_report = json.loads(Path(str(cpcv_published) + ".metrics.json").read_text())
            assert len(cpcv_report["outer_folds"]) == 3 and cpcv_report["evaluation_status"] == "independent_outer"
            block = cpcv_report["cpcv"]
            assert block["method"] == "combinatorial_purged_cv" and block["evaluation_only"] is True
            assert block["combination_count"] == 6 and block["paths"] == "not_constructed"
            assert [c["combination"] for c in block["combinations"]] == list(range(6))
            assert [tuple(c["test_groups"]) for c in block["combinations"]] == [
                (0, 1), (0, 2), (0, 3), (1, 2), (1, 3), (2, 3)]
            assert all(c["n_test"] > 0 and c["n_train"] > 0 and "macro_f1" in c for c in block["combinations"])
            assert len(block["group_bounds"]) == 4 and block["embargo"] == {"unit": "bars", "length": 5}
            # interpretation labels: a CPCV score is a robustness diagnostic, not a deployed-performance estimate
            assert block["evaluation_protocol"] == "cpcv_combinatorial"
            assert block["training_temporality"] == "bidirectional_excluding_test"
            assert block["deployment_interpretation"] == "robustness_diagnostic"
            assert block["distribution_semantics"] == "dependent_split_scores"
            counts = block["group_window_counts"]
            assert [c["group"] for c in counts] == [0, 1, 2, 3]
            assert all(c["windows"] == sum(c["symbol_windows"].values()) > 0 for c in counts)
            assert all(c["n_test"] == sum(counts[g]["windows"] for g in c["test_groups"])
                       for c in block["combinations"]), "the counted group windows are the combination's test windows"
            stats = block["summary"]["macro_f1"]
            values = [c["macro_f1"] for c in block["combinations"]]
            assert stats["count"] == 6.0 and abs(stats["mean"] - sum(values) / 6) < 1e-12
            assert stats["min"] == min(values) and stats["max"] == max(values)
            flat = [line for line in lines if line.startswith("METRIC:") and "cpcv_combination_count" in line]
            assert len(flat) == 1
            assert sum(1 for line in lines if line.startswith("METRIC:") and '"cpcv_combination"' in line) == 6
            # a request over the cap or with a bad shape fails before anything starts (13-21=A)
            for bad_cpcv in ({"cpcv": True, "cpcv_groups": 10, "cpcv_test_groups": 3},
                             {"cpcv": True, "cpcv_groups": 6, "cpcv_test_groups": 0},
                             {"cpcv": True, "cpcv_groups": 6, "cpcv_test_groups": 6},
                             {"cpcv": True, "cpcv_groups": 21, "cpcv_test_groups": 1},
                             {"cpcv": 1}, {"cpcv": True, "ensemble_method": "bagging"},
                             {"cpcv": True, "initialization_mode": "resume", "checkpoint_path": "run.json"}):
                try:
                    JobConfig.from_json(json.dumps({**base, **bad_cpcv}))
                except ValueError:
                    pass
                else:
                    raise AssertionError(f"invalid CPCV request accepted: {bad_cpcv}")
            try:
                JobConfig.from_json(json.dumps({**base, "future_option": 1}))
            except ValueError as error:
                assert "unknown keys: ['future_option']" in str(error) and UNKNOWN_KEYS_HINT in str(error), error
            else:  # pragma: no cover
                raise AssertionError("an unknown config key must still be rejected")
            assert "--cpcv-combination" not in _build_trainer_argv(defaults, Path("d"), Path("o.onnx"))
            cp_argv = _build_trainer_argv(defaults, Path("d"), Path("o.onnx"), cpcv=ds.CpcvSpec(6, 2, 14))
            assert cp_argv[cp_argv.index("--cpcv-combination") + 1] == "14"

            # config contract of the new keys
            for bad_ensemble in (
                {"ensemble_method": "boosting"}, {"ensemble_method": "bagging", "ensemble_members": 1},
                {"ensemble_method": "bagging", "ensemble_members": 99},
                {"ensemble_method": "bagging", "ensemble_members": True},
                {"ensemble_method": "bagging", "target_type": "regression"},
                {"ensemble_method": "bagging", "calibration": True},
                {"ensemble_method": "bagging", "pretraining": "masked_reconstruction"},
                {"ensemble_method": "stacking", "stacking_bases": ["lightgbm"]},
                {"ensemble_method": "stacking", "stacking_bases": ["pytorch:lstm", "pytorch"]},
                {"stacking_bases": ["lightgbm", "pytorch:cnn"]},
                {"pretraining": "contrastive"},
                {"pretraining": "masked_reconstruction", "framework": "lightgbm"},
            ):
                try:
                    JobConfig.from_json(json.dumps({**base, **bad_ensemble}))
                except ValueError:
                    pass
                else:
                    raise AssertionError(f"invalid ensemble/pretraining request accepted: {bad_ensemble}")
            pretrained = JobConfig.from_json(json.dumps({**base, "pretraining": "masked_reconstruction"}))
            pre_argv = _build_trainer_argv(pretrained, Path("d"), Path("o.onnx"))
            assert pre_argv[pre_argv.index("--pretrain") + 1] == "masked_reconstruction"
            assert "--pretrain" not in _build_trainer_argv(defaults, Path("d"), Path("o.onnx"))
            bag_argv = _build_trainer_argv(defaults, Path("d"), Path("o.onnx"),
                                           bagging=ds.BaggingSpec(1, 3, 9), seed=77)
            assert bag_argv[bag_argv.index("--bag-index") + 1] == "1" and bag_argv[bag_argv.index("--seed") + 1] == "77"
            seeded = JobConfig.from_json(json.dumps({**base, "hyperparameters": {"seed": "5"}}))
            try:
                _build_trainer_argv(seeded, Path("d"), Path("o.onnx"), seed=1)
            except SystemExit:
                pass
            else:
                raise AssertionError("a member seed must not be silently overridden by a hyperparameter")
        finally:
            _analysis_mod.build, _explain_mod.build, _calibration_mod.build = original_stage_builders
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
