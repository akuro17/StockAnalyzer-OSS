"""Owned T16 tensor-only snapshots; undo constructor RNG use before continuation."""
from __future__ import annotations
from collections import deque
import os
import random
from pathlib import Path
import numpy as np
import torch

class Paused(Exception):
    pass

def pack(value):
    if isinstance(value, np.ndarray):
        return {"__ndarray__": True, "dtype": value.dtype.str, "shape": list(value.shape),
                "bytes": torch.from_numpy(np.frombuffer(value.tobytes(), np.uint8).copy())}
    if isinstance(value, np.generic):
        return value.item()
    if isinstance(value, dict):
        return {key: pack(item) for key, item in value.items()}
    if isinstance(value, (list, tuple, deque)):
        return tuple(pack(item) for item in value) if isinstance(value, tuple) else [pack(item) for item in value]
    if isinstance(value, torch.Tensor):
        return value.detach().cpu().clone()
    if value is None or isinstance(value, (str, bool, int, float)):
        return value
    raise TypeError("Unsupported checkpoint value: " + type(value).__name__)

def unpack(value):
    if isinstance(value, dict) and value.get("__ndarray__") is True:
        dtype = np.dtype(value["dtype"])
        if dtype.kind not in "bifu" or dtype.hasobject:
            raise ValueError("Unsafe checkpoint dtype")
        return np.frombuffer(value["bytes"].numpy().tobytes(), dtype).reshape(value["shape"]).copy()
    if isinstance(value, dict):
        return {key: unpack(item) for key, item in value.items()}
    if isinstance(value, (tuple, list)):
        return tuple(unpack(item) for item in value) if isinstance(value, tuple) else [unpack(item) for item in value]
    return value

ATTRIBUTES = ("num_timesteps", "_n_updates", "_episode_num", "_total_timesteps",
              "_current_progress_remaining", "_last_obs", "_last_episode_starts", "_last_original_obs")
MONITOR = ("rewards", "needs_reset", "episode_returns", "episode_lengths", "episode_times", "total_steps", "current_reset_info")

def snapshot(model, environment, state, schema):
    vector = model.env
    monitor = vector.envs[0]
    # Read lazily created generators before capturing global RNG.
    environment_rng = environment.np_random.bit_generator.state
    action_rng = environment.action_space.np_random.bit_generator.state
    observation_rng = environment.observation_space.np_random.bit_generator.state
    return pack(dict(schema=schema, state=state, policy=model.policy.state_dict(), optimizer=model.policy.optimizer.state_dict(),
        model={key: getattr(model, key, None) for key in ATTRIBUTES},
        ep_info=list(model.ep_info_buffer or []), ep_success=list(model.ep_success_buffer or []),
        environment=dict(cursor=environment.cursor, created=environment.created, session_id=environment.session_id,
            rng=environment_rng, action_rng=action_rng, observation_rng=observation_rng),
        monitor={key: getattr(monitor, key) for key in MONITOR},
        vector=dict(obs=vector.buf_obs, dones=vector.buf_dones, rewards=vector.buf_rews,
                    infos=vector.buf_infos, reset_infos=vector.reset_infos, seeds=vector._seeds, options=vector._options),
        rng=dict(python=random.getstate(), numpy=np.random.get_state(), torch=torch.get_rng_state())))

def restore(model, environment, saved, sessions):
    model.policy.load_state_dict(saved["policy"], strict=True)
    model.policy.optimizer.load_state_dict(saved["optimizer"])
    for key, value in saved["model"].items():
        setattr(model, key, value)
    model.ep_info_buffer = deque(saved["ep_info"], maxlen=model._stats_window_size)
    model.ep_success_buffer = deque(saved["ep_success"], maxlen=model._stats_window_size)
    env = saved["environment"]
    environment.cursor, environment.created = env["cursor"], env["created"]
    environment.session_id = sessions[env["session_id"]] if env["created"] else environment.session_id
    environment.np_random.bit_generator.state = env["rng"]
    environment.action_space.np_random.bit_generator.state = env["action_rng"]
    environment.observation_space.np_random.bit_generator.state = env["observation_rng"]
    vector = model.env
    for key, value in saved["monitor"].items():
        setattr(vector.envs[0], key, value)
    vec = saved["vector"]
    vector.buf_obs, vector.buf_dones, vector.buf_rews, vector.buf_infos = vec["obs"], vec["dones"], vec["rewards"], vec["infos"]
    vector.reset_infos, vector._seeds, vector._options = vec["reset_infos"], vec["seeds"], vec["options"]
    random.setstate(saved["rng"]["python"])
    np.random.set_state(saved["rng"]["numpy"])
    torch.set_rng_state(saved["rng"]["torch"])

def load(path, contract):
    saved = unpack(torch.load(Path(path) / contract["python_file"], map_location="cpu", weights_only=True))
    if saved.get("schema") != contract["schema_version"] or saved["state"]["phase"] not in contract["phases"]:
        raise ValueError("Unsupported T16 checkpoint schema")
    return saved

def save(client, model, environment, state):
    prepared = client.call("PrepareCheckpoint", dict(seed=model.candidate_seed, steps=model.num_timesteps, phase=state["phase"]))
    contract = client.job["checkpoint_contract"]
    with (Path(prepared["directory"]) / contract["python_file"]).open("xb") as stream:
        torch.save(snapshot(model, environment, state, contract["schema_version"]), stream)
        stream.flush()
        os.fsync(stream.fileno())
    if client.call("CommitCheckpoint", dict(id=prepared["id"]))["pause"]:
        raise Paused()
