"""Freeze the managed environment's distribution, wheel-tag and installed-record identities."""
from __future__ import annotations
import argparse
import hashlib
import importlib.metadata
import json
import platform
import os
from pathlib import Path
import sys


def execution_roots():
    return [Path(sys.prefix).resolve(), Path(sys.base_prefix).resolve(), Path(__file__).resolve().parent]


def execution_identity():
    roots = sorted(set(str(Path(root).resolve(strict=True)) for root in execution_roots()))
    files, links = {}, {}
    for root in roots:
        for directory, children, filenames in os.walk(root, followlinks=False):
            for name in children + filenames:
                path = Path(directory) / name
                if path.is_symlink():
                    resolved = path.resolve(strict=True)
                    if not any(resolved.is_relative_to(Path(owned)) for owned in roots):
                        raise RuntimeError("Executable dependency link leaves the frozen roots: " + str(path))
                    links[str(path)] = str(resolved)
            for name in filenames:
                path = Path(directory) / name
                with path.open("rb") as stream:
                    files[str(path)] = hashlib.file_digest(stream, "sha256").hexdigest()
    return dict(execution_roots=roots, execution_files=files, execution_links=links,
                python_executable=str(Path(sys.executable).resolve(strict=True)))


def inspect(strict=True):
    packages, distributions = {}, {}
    for dist in importlib.metadata.distributions():
        name = dist.metadata.get("Name")
        if not name:
            continue
        packages[name] = dist.version
        records = {}
        for filename in ("METADATA", "WHEEL", "RECORD", "INSTALLER", "direct_url.json"):
            value = dist.read_text(filename)
            if value is not None:
                records[filename] = {"sha256": hashlib.sha256(value.encode("utf-8")).hexdigest()}
                if filename in ("WHEEL", "INSTALLER", "direct_url.json"):
                    records[filename]["text"] = value
        distributions[name] = {"version": dist.version, "installed_metadata": records}
    manifest = dict(python=sys.version, platform=platform.platform(), machine=platform.machine(),
                    packages=packages, distributions=distributions, torch_device="cpu", deterministic_algorithms=True)
    if strict:
        manifest.update(execution_identity())
    return manifest


def validate(path):
    captured = json.loads(Path(path).read_text(encoding="utf-8"))
    strict = "execution_files" in captured
    current = inspect(strict=strict)
    keys = ("python", "platform", "machine", "packages", "distributions")
    if strict:
        keys += ("execution_roots", "execution_files", "execution_links", "python_executable")
    for key in keys:
        if captured.get(key) != current[key]:
            raise RuntimeError("Managed dependency snapshot changed before training: " + key)
    return captured


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    job = json.loads(Path(parser.parse_args().config).read_text(encoding="utf-8"))
    try:
        from .train_rl import watch_parent
    except ImportError:
        from train_rl import watch_parent
    watch_parent(job["parent_pid"], job["defaults"]["IpcTimeoutSeconds"])
    manifest = inspect(strict=job.get("strict_dependencies", False))
    script = Path(job.get("trainer_script", "training/train_rl.py")).name
    manifest["source_script_sha256"] = hashlib.sha256(Path(__file__).with_name(script).read_bytes()).hexdigest()
    if "continuous_defaults" in job:
        import inspect as source_inspection
        from stable_baselines3.common.policies import ActorCriticPolicy
        try:
            from .train_rl_ppo import raw_rollout_transport
            from . import rl_ppo_guard
        except ImportError:
            from train_rl_ppo import raw_rollout_transport
            import rl_ppo_guard
        _, manifest["ppo_collector_sha256"] = raw_rollout_transport()
        manifest["ppo_policy_sha256"] = hashlib.sha256(source_inspection.getsource(ActorCriticPolicy).encode("utf-8")).hexdigest()
        manifest.update(rl_ppo_guard.identities())
    path = Path(job["artifact_directory"]) / job["files"]["dependencies"]
    encoded = json.dumps(manifest, sort_keys=True, allow_nan=False, separators=(",", ":")).encode("utf-8")
    if len(encoded) > job["defaults"]["BufferByteLimit"]:
        raise RuntimeError("Execution dependency manifest exceeds the captured bound")
    temporary = path.with_name(path.name + ".tmp")
    with temporary.open("wb") as stream:
        stream.write(encoded)
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temporary, path)


if __name__ == "__main__":
    main()
