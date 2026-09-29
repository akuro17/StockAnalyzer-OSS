"""Generation-bound fixed population z-score shared by all ONNX trainers."""

from __future__ import annotations

import hashlib
import json
import math
from pathlib import Path

import numpy as np

SCHEMA_VERSION = 1
MAX_LAG = 512  # mirrors TrainingResourceOverrides.MaximumFeatureLag
EPSILON = float(np.float32(1e-7))  # mirrors the C# float constant IMLDataProcessor.Epsilon
BASE_OHLCV = ("open", "high", "low", "close", "volume")


def validate_lags(value) -> tuple[int, ...]:
    if value is None or not isinstance(value, list) or any(
        type(lag) is not int or lag < 1 or lag > MAX_LAG for lag in value
    ) or len(set(value)) != len(value):
        raise ValueError(f"lags must be a distinct ordered array of bars in [1,{MAX_LAG}]")
    return tuple(value)


def trainer_lags(args, feature_spec: dict | None) -> tuple[int, ...]:
    if args.feature_mode == "composed_features":
        if getattr(args, "lags", "[]") != "[]":
            raise ValueError("composed lags belong in feature_spec.lags")
        return validate_lags(feature_spec.get("lags", []) if feature_spec else [])
    try:
        raw = json.loads(getattr(args, "lags", "[]"))
    except json.JSONDecodeError as exc:
        raise ValueError("--lags must be a JSON array") from exc
    return validate_lags(raw)


def identities(mode: str, feature_spec: dict | None, lags: tuple[int, ...]) -> list[str]:
    if mode == "composed_features":
        if not feature_spec or not feature_spec.get("channels"):
            raise ValueError("composed features need channels")
        base = [f"{index}:{channel['kind']}:{channel.get('price') or channel.get('indicator')}"
                for index, channel in enumerate(feature_spec["channels"])]
    elif mode == "ohlcv_minmax":
        base = list(BASE_OHLCV)
    else:
        raise ValueError("fixed z-score is supported only for raw OHLCV or composed features")
    return [f"lag{lag}:{name}" for lag in (0, *lags) for name in base]


def contract_hash(metadata: dict[str, str]) -> str:
    keys = ("feature_mode", "window_size", "channels", "feature_spec", "lags", "clip_sigma")
    value = "\n".join(metadata.get(key, "") for key in keys)
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


def fit(x_train: np.ndarray, mode: str, feature_spec: dict | None,
        lags: tuple[int, ...], clip_sigma: float | None) -> dict:
    x = np.asarray(x_train, dtype=np.float64)
    if x.ndim != 3 or x.shape[0] == 0 or x.shape[1] == 0:
        raise ValueError("fixed scaler needs at least one selected training window")
    if not np.isfinite(x).all():
        raise ValueError("fixed scaler training observations must be finite")
    if clip_sigma is not None and (not math.isfinite(clip_sigma) or clip_sigma <= 0):
        raise ValueError("clip_sigma must be finite and positive")
    names = identities(mode, feature_spec, lags)
    if x.shape[2] != len(names):
        raise ValueError("expanded channel count disagrees with ordered identities")
    count = x.shape[0] * x.shape[1]
    total = np.zeros(x.shape[2], dtype=np.float64)
    minimum = np.full(x.shape[2], np.inf, dtype=np.float64)
    maximum = np.full(x.shape[2], -np.inf, dtype=np.float64)
    for sample in x:
        for row in sample:
            total += row
            np.minimum(minimum, row, out=minimum)
            np.maximum(maximum, row, out=maximum)
    mean = total / count
    variance_total = np.zeros(x.shape[2], dtype=np.float64)
    for sample in x:
        for row in sample:
            variance_total += (row - mean) ** 2
    std = np.sqrt(variance_total / count)
    if not all(np.isfinite(a).all() for a in (mean, std, minimum, maximum)):
        raise ValueError("fixed scaler statistics are nonfinite")
    return {
        "schema_version": SCHEMA_VERSION, "feature_mode": mode,
        "window_size": int(x.shape[1]), "lags": list(lags),
        "clip_sigma": clip_sigma, "channel_identities": names,
        "statistics": [
            {"mean": float(mean[j]), "std": float(std[j]),
             "min": float(minimum[j]), "max": float(maximum[j])}
            for j in range(x.shape[2])
        ],
    }


def prepare(x_train: np.ndarray, x_validation: np.ndarray, *, enabled: bool,
            mode: str, feature_spec: dict | None, lags: tuple[int, ...],
            clip_sigma: float | None) -> tuple[np.ndarray, np.ndarray, dict | None]:
    if not enabled:
        return x_train, x_validation, None
    scaler = fit(x_train, mode, feature_spec, lags, clip_sigma)
    return transform(x_train, scaler), transform(x_validation, scaler), scaler


def transform(x_raw: np.ndarray, scaler: dict) -> np.ndarray:
    x = np.asarray(x_raw, dtype=np.float64)
    stats = scaler["statistics"]
    if x.ndim != 3 or x.shape[2] != len(stats) or x.shape[1] != scaler["window_size"]:
        raise ValueError("fixed scaler tensor shape or channel count mismatch")
    mean = np.asarray([item["mean"] for item in stats], dtype=np.float64)
    std = np.asarray([item["std"] for item in stats], dtype=np.float64)
    if not np.isfinite(x).all() or not np.isfinite(mean).all() or not np.isfinite(std).all() or (std < 0).any():
        raise ValueError("fixed scaler tensor or statistics are invalid")
    result = np.empty(x.shape, dtype=np.float32)
    active = std > EPSILON
    clip = scaler["clip_sigma"]
    for sample_index, sample in enumerate(x):
        for row_index, row in enumerate(sample):
            z = np.zeros(x.shape[2], dtype=np.float64)
            z[active] = (row[active] - mean[active]) / std[active]
            if clip is not None:
                np.clip(z, -clip, clip, out=z)
            if not np.isfinite(z).all():
                raise ValueError("fixed scaler output is nonfinite")
            result[sample_index, row_index] = z
    return result


def write_sidecar(model_path: Path, scaler: dict, metadata: dict[str, str]) -> Path:
    if metadata.get("scaler_ref") != model_path.name + ".scaler.json":
        raise ValueError("ONNX scaler_ref does not name its sidecar")
    saved = {**scaler, "feature_contract_hash": contract_hash(metadata)}
    path = model_path.with_name(model_path.name + ".scaler.json")
    path.write_text(json.dumps(saved, allow_nan=False, separators=(",", ":")), encoding="utf-8")
    return path


def load_sidecar(model_path: Path, metadata: dict[str, str]) -> dict:
    ref = metadata.get("scaler_ref")
    if ref != model_path.name + ".scaler.json":
        raise ValueError("ONNX scaler_ref is missing or does not name its own sidecar")
    scaler = json.loads(model_path.with_name(ref).read_text(encoding="utf-8"))
    if scaler.get("schema_version") != SCHEMA_VERSION or scaler.get("feature_contract_hash") != contract_hash(metadata):
        raise ValueError("fixed scaler schema or feature contract hash mismatch")
    lags = validate_lags(scaler.get("lags"))
    spec = json.loads(metadata["feature_spec"]) if "feature_spec" in metadata else None
    if spec is not None and (spec.get("lags", []) != list(lags) or any(
        channel.get("normalization", "none") != "none" for channel in spec["channels"]
    )):
        raise ValueError("composed scaler conflicts with lags or window normalization")
    expected_identities = identities(metadata["feature_mode"], spec, lags)
    if (scaler.get("channel_identities") != expected_identities or
            len(expected_identities) != int(metadata["channels"])):
        raise ValueError("fixed scaler channel identities mismatch")
    if scaler.get("window_size") != int(metadata["window_size"]):
        raise ValueError("fixed scaler window mismatch")
    if scaler.get("feature_mode") != metadata["feature_mode"] or metadata.get("normalization") != "fixed_zscore":
        raise ValueError("fixed scaler feature mode mismatch")
    if metadata.get("lags") != json.dumps(list(lags), separators=(",", ":")):
        raise ValueError("fixed scaler lag metadata mismatch")
    if len(scaler.get("statistics", [])) != int(metadata["channels"]):
        raise ValueError("fixed scaler statistics width mismatch")
    clip = scaler.get("clip_sigma")
    if (float(metadata["clip_sigma"]) if metadata.get("clip_sigma") else None) != clip:
        raise ValueError("fixed scaler clipping metadata mismatch")
    if clip is not None and (type(clip) not in (int, float) or not math.isfinite(clip) or clip <= 0):
        raise ValueError("invalid scaler clip_sigma")
    for item in scaler["statistics"]:
        if any(key not in item or type(item[key]) not in (int, float) or not math.isfinite(item[key])
               for key in ("mean", "std", "min", "max")) or item["std"] < 0 or item["min"] > item["max"]:
            raise ValueError("invalid fixed scaler statistic")
    return scaler
