"""T08 native training state. Inference never depends on these training files."""
from __future__ import annotations

import contextlib
import hashlib
import json
import os
import re
import sys
import tempfile
import time
import uuid
from pathlib import Path

import numpy as np
import fixed_scaler
import dataset as ds
import semi_supervised

SCHEMA_VERSION = 1
LINEAGE_KEY = "com.stockanalyzer.training.lineage"
# directory under the training artifacts that holds every run's native checkpoints (mirror of C# TrainingCheckpointContract.DirectoryName)
DIRECTORY_NAME = "checkpoints"
MODES = ("fresh", "fine_tune", "resume")
# Architecture options that exist for ONE architecture only (T24a attn_lstm heads). Set (not None) they join the checkpoint contract and the training config;
# unset they are absent, so every contract and training config written before they existed compares byte-identically.
OPTIONAL_ARCH_KEYS = ("heads",)
_HASH = re.compile(r"[0-9a-f]{64}\Z")
_RUN = re.compile(r"[A-Za-z0-9_-]+\Z")
_MODULE = re.compile(r"[A-Za-z_][A-Za-z0-9_]*(?:\.(?:[A-Za-z_][A-Za-z0-9_]*|[0-9]+))*\Z")

# Windows briefly denies replacing a file that a scanner or watcher has just opened (WinError 5 access denied,
# WinError 32 sharing violation). The replace is retried a bounded number of times; every other failure is final.
# Tunable operational values (environment variables, the Python-side configuration channel) with a three-tier check:
# outside the hard band the process fails naming the key, outside the standard band it runs and warns once.
REPLACE_ATTEMPTS_ENV = "SA_CHECKPOINT_REPLACE_ATTEMPTS"
REPLACE_DELAY_MS_ENV = "SA_CHECKPOINT_REPLACE_DELAY_MS"
REPLACE_ATTEMPTS_DEFAULT, REPLACE_ATTEMPTS_HARD, REPLACE_ATTEMPTS_STANDARD = 10, (1, 50), (1, 20)
REPLACE_DELAY_MS_DEFAULT, REPLACE_DELAY_MS_HARD, REPLACE_DELAY_MS_STANDARD = 100, (1, 1000), (10, 500)
# Safety bound: the checkpoint is saved inside the training loop, so one failing save may never stall it for longer.
REPLACE_TOTAL_WAIT_MAX_MS = 10_000
_LOCK_WINERRORS = (5, 32)
_IS_WINDOWS = os.name == "nt"


def canonical(value) -> str:
    return json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False)


def hash_file(path: Path) -> str:
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def resolve_band(name: str, default: int, hard: tuple[int, int], standard: tuple[int, int]) -> int:
    """Read an integer setting from the environment: *default* if unset, ValueError outside *hard*, a warning outside *standard*."""
    raw = os.environ.get(name)
    if raw is None:
        return default
    if re.fullmatch(r"[+-]?[0-9]+", raw.strip()) is None:
        raise ValueError(f"{name} must be an integer, got {raw!r}")
    value = int(raw)
    if not hard[0] <= value <= hard[1]:
        raise ValueError(f"{name}={value} is outside the allowed range {hard[0]}..{hard[1]}")
    if not standard[0] <= value <= standard[1]:
        print(f"WARNING: {name}={value} is outside the standard range {standard[0]}..{standard[1]}", file=sys.stderr)
    return value


def resolve_replace_policy() -> tuple[int, int]:
    """The (attempts, delay in milliseconds) of a checkpoint replace, validated as one policy."""
    attempts = resolve_band(REPLACE_ATTEMPTS_ENV, REPLACE_ATTEMPTS_DEFAULT, REPLACE_ATTEMPTS_HARD, REPLACE_ATTEMPTS_STANDARD)
    delay = resolve_band(REPLACE_DELAY_MS_ENV, REPLACE_DELAY_MS_DEFAULT, REPLACE_DELAY_MS_HARD, REPLACE_DELAY_MS_STANDARD)
    if attempts * delay > REPLACE_TOTAL_WAIT_MAX_MS:
        raise ValueError(f"{REPLACE_ATTEMPTS_ENV} x {REPLACE_DELAY_MS_ENV} = {attempts * delay} ms exceeds {REPLACE_TOTAL_WAIT_MAX_MS} ms")
    return attempts, delay


REPLACE_ATTEMPTS, REPLACE_DELAY_MS = resolve_replace_policy()


def replace_with_retry(source, target, sleep=time.sleep) -> None:
    for attempt in range(1, REPLACE_ATTEMPTS + 1):
        try:
            os.replace(source, target)
            return
        except PermissionError as error:
            if not _IS_WINDOWS or getattr(error, "winerror", None) not in _LOCK_WINERRORS:
                raise
            if attempt == REPLACE_ATTEMPTS:
                raise PermissionError(error.errno, f"{error.strerror} (gave up after {attempt} attempts)",
                                      error.filename, error.winerror, error.filename2) from error
            if attempt == 1:
                print(f"WARNING: replacing {target} was denied; retrying up to {REPLACE_ATTEMPTS} attempts "
                      f"every {REPLACE_DELAY_MS} ms", file=sys.stderr)
            sleep(REPLACE_DELAY_MS / 1000)


def atomic_json(path: Path, value) -> None:
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, name = tempfile.mkstemp(prefix=".incoming_", dir=path.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            stream.write(canonical(value))
            stream.flush()
            os.fsync(stream.fileno())
        replace_with_retry(name, path)
    finally:
        if os.path.exists(name):
            os.unlink(name)


def read_json(path: Path) -> dict:
    value = json.loads(Path(path).read_text(encoding="utf-8-sig"))
    if not isinstance(value, dict) or value.get("schema_version") != SCHEMA_VERSION:
        raise ValueError("unsupported or missing native checkpoint schema")
    return value


def checked_blob(path: Path, value: dict | None = None) -> tuple[dict, Path]:
    path = Path(path).resolve()
    value = read_json(path) if value is None else value
    if value.get("schema_version") != SCHEMA_VERSION:
        raise ValueError("unsupported native checkpoint schema")
    name, digest = value.get("blob"), value.get("sha256")
    if (not isinstance(name, str) or Path(name).name != name
            or "/" in name or "\\" in name or not isinstance(digest, str)
            or not _HASH.fullmatch(digest) or not name.startswith(digest + ".")):
        raise ValueError("invalid native checkpoint reference")
    blob = (path.parent / name).resolve()
    if blob.parent != path.parent or not blob.is_file() or hash_file(blob) != digest:
        raise ValueError("native checkpoint missing or hash mismatch")
    if not isinstance(value.get("context"), dict):
        raise ValueError("native checkpoint context missing")
    return value, blob


def add_arguments(parser) -> None:
    parser.add_argument("--preflight", action="store_true", help="Validate transfer/data/state without fitting or export.")
    parser.add_argument("--initialization-mode", choices=MODES, default="fresh")
    parser.add_argument("--init-from", type=Path)
    parser.add_argument("--checkpoint-dir", type=Path)
    parser.add_argument("--run-id")
    parser.add_argument("--parent-model-id")
    parser.add_argument("--parent-model-hash")
    parser.add_argument("--freeze-paths", default="[]")


def validate_controls(mode: str, framework: str, source, parent_id, parent_hash, freezes) -> None:
    if mode not in MODES or not isinstance(freezes, list) or any(
        not isinstance(p, str) or not _MODULE.fullmatch(p) for p in freezes
    ) or len(set(freezes)) != len(freezes):
        raise ValueError("invalid initialization mode or normalized freeze paths")
    if mode == "fresh":
        if source or parent_id or parent_hash or freezes:
            raise ValueError("fresh training cannot have transfer controls")
        return
    if framework == "tensorflow" or (mode == "resume" and framework != "pytorch"):
        raise ValueError("selected framework does not support this transfer mode")
    if not source or Path(source).suffix.lower() != ".json":
        raise ValueError("a versioned native checkpoint JSON is required; ONNX is not restorable")
    if mode == "resume":
        if parent_id or parent_hash or freezes:
            raise ValueError("Resume restores its original lineage and freeze paths")
    elif not isinstance(parent_id, str) or not _HASH.fullmatch(parent_id):
        raise ValueError("FineTune requires a registered parent ModelId")
    elif not isinstance(parent_hash, str) or not _HASH.fullmatch(parent_hash):
        raise ValueError("FineTune requires the registered parent ONNX hash")
    if freezes and framework != "pytorch":
        raise ValueError("module freezing is supported only by PyTorch FineTune")


def freeze_modules(model, paths: list[str]) -> None:
    modules = dict(model.named_modules())
    for path in paths:
        if not _MODULE.fullmatch(path) or path not in modules:
            raise ValueError(f"unknown freeze module path: {path}")
        parameters = list(modules[path].parameters())
        if not parameters:
            raise ValueError(f"freeze module has no parameters: {path}")
        for parameter in parameters:
            parameter.requires_grad_(False)
    if not any(p.requires_grad for p in model.parameters()):
        raise ValueError("at least one trainable parameter is required")


def source_revision(symbols: dict, dates: dict, indicator_paths: dict | None = None) -> str:
    """One owner for the complete run source identity, also used by offline analysis."""
    revision = tensor_revision([symbols[s] for s in sorted(symbols)], dates)
    if indicator_paths:
        revision = hashlib.sha256(canonical([revision, [(s, hash_file(Path(p)))
                                   for s, p in sorted(indicator_paths.items())]]).encode()).hexdigest()
    return revision


def tensor_revision(arrays, dates: dict) -> str:
    digest = hashlib.sha256()
    for array in arrays:
        array = np.ascontiguousarray(array)
        digest.update(canonical([str(array.dtype), list(array.shape)]).encode())
        digest.update(array.tobytes())
    for symbol, values in sorted(dates.items()):
        digest.update(canonical([symbol, [str(d) for d in values]]).encode())
    return digest.hexdigest()


class Session:
    """One fold's exact state. Native blobs precede atomic logical slot pointers."""

    def __init__(self, args, framework: str, arrays, dates: dict, feature_spec, lags, runtime: dict):
        self.args = args
        self.mode = args.initialization_mode
        freezes = json.loads(args.freeze_paths)
        validate_controls(self.mode, framework, args.init_from, args.parent_model_id,
                          args.parent_model_hash, freezes if self.mode != "resume" else [])
        self.root = args.checkpoint_dir or args.out.with_name(args.out.name + ".checkpoints")
        self.root = Path(self.root)
        self.root.mkdir(parents=True, exist_ok=True)
        if self.mode != "resume" and ((self.root / "last.json").exists() or (self.root / "best.json").exists()):
            raise ValueError("existing checkpoints require Resume or a new checkpoint directory")
        self.source = None
        self.blob = None
        contract = {key: getattr(args, key, None) for key in (
            "arch", "hidden", "layers", "dropout", "window", "horizon", "timeframe",
            "threshold", "target_type", "feature_mode", "fixed_zscore", "clip_sigma",
            "num_leaves", "max_depth")}
        identities = (fixed_scaler.identities(args.feature_mode, feature_spec, lags)
                      if args.feature_mode in ("ohlcv_minmax", ds.COMPOSED_FEATURES_MODE) else
                      [f"{args.feature_mode}:{i}" for i in range(ds.feature_channels(args.feature_mode))])
        contract.update(framework=framework, feature_spec=feature_spec, lags=list(lags),
                        ordered_channels=identities,
                        class_order=list(ds.CLASS_LABELS) if contract["target_type"] == "classification" else None)
        contract.update({key: getattr(args, key) for key in OPTIONAL_ARCH_KEYS if getattr(args, key, None) is not None})
        ignored = {"epochs", "out", "data_dir", "init_from", "checkpoint_dir", "run_id",
                   "parent_model_id", "parent_model_hash", "freeze_paths", "initialization_mode",
                   "no_verify", "smoke", "indicator_channel_export_paths", "feature_spec",
                   "composed_export_paths", "composed_spec", "calibration"}
        ignored.add("preflight")
        ignored.update(semi_supervised.ARGUMENT_KEYS)  # T19 options never change the checkpoint contract of an existing run
        config = {key: value for key, value in vars(args).items() if key not in ignored
                  and not key.startswith("_") and not (key in OPTIONAL_ARCH_KEYS and value is None)}
        config = json.loads(canonical(config))
        run_id = args.run_id
        if not run_id and self.mode == "resume":
            run_id = checked_blob(args.init_from)[0]["context"]["run_id"]
        run_id = run_id or uuid.uuid4().hex
        if not _RUN.fullmatch(run_id):
            raise ValueError("unsafe run identifier")
        all_dates = [str(d) for values in dates.values() for d in values]
        if not all_dates:
            raise ValueError("checkpoint provenance requires dated observations")
        lineage = {"mode": self.mode, "parent_model_id": args.parent_model_id,
                   "parent_checkpoint_sha256": None, "parent_model_sha256": args.parent_model_hash}
        self.context = dict(framework=framework, contract=contract, training_config=config,
                            data_revision=tensor_revision(arrays, dates), run_id=run_id,
                            fold=getattr(args, "outer_fold_index", None), runtime=runtime,
                            freeze_paths=freezes, data_end=max(all_dates), lineage=lineage, scaler=None)
        if self.mode != "fresh":
            self.source, self.blob = checked_blob(args.init_from)
            parent = self.source["context"]
            if parent.get("contract") != contract or parent.get("runtime") != runtime:
                raise ValueError("checkpoint framework/architecture/ordered feature/target/runtime mismatch")
            if self.mode == "resume":
                if self.root.resolve() != Path(args.init_from).resolve().parent:
                    raise ValueError("Resume must retain the original checkpoint directory")
                for key in ("training_config", "data_revision", "run_id", "fold"):
                    if parent.get(key) != self.context[key]:
                        raise ValueError(f"Resume checkpoint {key} mismatch")
                if self.source.get("slot") != "last" or freezes != parent.get("freeze_paths"):
                    raise ValueError("Resume requires last state and unchanged frozen modules")
                self.context = parent
            else:
                if self.source.get("slot") != "best" or self.source.get("exported_model_sha256") != args.parent_model_hash:
                    raise ValueError("FineTune requires a best checkpoint of the registered parent ONNX")
                if not parent.get("data_end") or min(all_dates) <= parent["data_end"]:
                    raise ValueError("parent training data overlaps child independent evaluation; use a later child period")
                lineage["parent_checkpoint_sha256"] = self.source["sha256"]

    def preprocess(self, x_train, x_validation, *, feature_spec, lags):
        if self.source is not None:
            scaler = self.source["context"].get("scaler")
            if bool(scaler) != bool(self.args.fixed_zscore):
                raise ValueError("native checkpoint preprocessing state missing")
            self.context["scaler"] = scaler
            if scaler is not None:
                return fixed_scaler.transform(x_train, scaler), fixed_scaler.transform(x_validation, scaler), scaler
            return x_train, x_validation, None
        train, validation, scaler = fixed_scaler.prepare(
            x_train, x_validation, enabled=self.args.fixed_zscore, mode=self.args.feature_mode,
            feature_spec=feature_spec, lags=lags, clip_sigma=self.args.clip_sigma)
        self.context["scaler"] = scaler
        return train, validation, scaler

    def save(self, writer, extension: str, slot: str, summary: dict) -> Path:
        fd, name = tempfile.mkstemp(prefix=".native_", suffix=extension, dir=self.root)
        os.close(fd)
        temporary = Path(name)
        try:
            writer(temporary)
            with temporary.open("r+b") as stream:
                os.fsync(stream.fileno())
            digest = hash_file(temporary)
            blob = self.root / (digest + extension)
            if blob.exists():
                if hash_file(blob) != digest:
                    raise ValueError("immutable checkpoint blob was corrupted")
            else:
                replace_with_retry(temporary, blob)
            pointer = dict(schema_version=SCHEMA_VERSION, slot=slot, blob=blob.name,
                           sha256=digest, context=self.context, **summary)
            path = self.root / (slot + ".json")
            atomic_json(path, pointer)
            return path
        finally:
            temporary.unlink(missing_ok=True)

    def metadata(self) -> dict[str, str]:
        return {LINEAGE_KEY: canonical(dict(self.context["lineage"], run_id=self.context["run_id"]))}

    def exported(self, path: Path) -> None:
        for slot in ("last", "best"):
            pointer = self.root / (slot + ".json")
            if pointer.exists():
                value, _ = checked_blob(pointer)
                value["exported_model_sha256"] = hash_file(path)
                atomic_json(pointer, value)


@contextlib.contextmanager
def run_lock(root: Path):
    """OS-owned lock releases on process exit; stale files never block recovery."""
    root.mkdir(parents=True, exist_ok=True)
    with (root / ".lock").open("a+b") as stream:
        stream.seek(0)
        if stream.read(1) == b"":
            stream.write(b"0")
            stream.flush()
        stream.seek(0)
        if os.name == "nt":
            import msvcrt
            msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl
            fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
        try:
            yield
        finally:
            stream.seek(0)
            if os.name == "nt":
                msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                fcntl.flock(stream, fcntl.LOCK_UN)
