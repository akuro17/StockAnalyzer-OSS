"""T15: DQN ML only. Core owns chronology, orders, decimal reward and evaluation."""
from __future__ import annotations

import argparse
import copy
import ctypes
import hashlib
import importlib.metadata
import json
import os
import operator
from pathlib import Path
import sys
import threading
import time
import uuid
from decimal import Decimal


def validate_action(action, action_count):
    if isinstance(action, bool) or type(action).__name__ == "bool_":
        raise ValueError("Boolean values are not RL actions")
    try:
        value = operator.index(action)
    except TypeError as error:
        raise ValueError("RL actions must be integer enum values") from error
    if not 0 <= value < action_count:
        raise ValueError("RL action is outside the declared range")
    return value


def validate_evaluation_catalog(job):
    """Consume the frozen Core evaluation contract without choosing or truncating blocks."""
    key = "evaluation_specification_id"
    spec = job.get(key)
    catalog = job["catalog"]
    if spec is None:
        if key in catalog or "schema_version" in catalog or key in job["metadata"]:
            raise ValueError("Evaluation specification downgrade")
        return
    contract = job["evaluation_contract"]
    if (not job.get("continuous_defaults") or contract["specification_id"] != spec
            or catalog.get(key) != spec or job["metadata"].get(key) != spec
            or catalog.get("schema_version") != contract["schema_version"]):
        raise ValueError("Inconsistent frozen all-block evaluation contract")
    if not catalog["validation"] or (not job["smoke_only"] and not catalog["oos"]):
        raise ValueError("Complete evaluation support is missing")
    for partition in ("validation", "oos"):
        blocks = catalog[partition]
        if len({block["id"] for block in blocks}) != len(blocks):
            raise ValueError("Duplicate evaluation block")


class CoreClient:
    def __init__(self, job):
        validate_evaluation_catalog(job)
        self.job = job
        self.sequence = 0
        self.lock = threading.Lock()

    def call(self, method, payload, session_id=""):
        with self.lock:
            self.sequence += 1
            request = dict(protocol_version=self.job["defaults"]["SchemaVersion"],
                           run_id=self.job["run_id"], contract_hash=self.job["contract_hash"],
                           sequence=self.sequence, session_id=session_id, method=method, payload=payload)
            encoded = json.dumps(request, allow_nan=False, separators=(",", ":")).encode("utf-8") + b"\n"
            if len(encoded) > self.job["defaults"]["IpcFrameByteLimit"]:
                raise ValueError("Core request exceeds frame cap")
            sys.stdout.buffer.write(encoded)
            sys.stdout.buffer.flush()
            response_box = []
            def read():
                try:
                    response_box.append(sys.stdin.buffer.readline(self.job["defaults"]["IpcFrameByteLimit"] + 1))
                except BaseException as error:
                    response_box.append(error)
            reader = threading.Thread(target=read, daemon=True)
            reader.start()
            reader.join(self.job["defaults"]["IpcTimeoutSeconds"])
            if reader.is_alive():
                # A mutating Step is never retried. Abort the entire interpreter/session.
                os._exit(70)
            if not response_box or isinstance(response_box[0], BaseException):
                raise RuntimeError("Core IPC read failed")
            raw = response_box[0]
            if not raw.endswith(b"\n") or len(raw) > self.job["defaults"]["IpcFrameByteLimit"]:
                raise ValueError("Core response is incomplete/oversized")
            response = json.loads(raw.decode("utf-8"))
            for field in ("protocol_version", "run_id", "contract_hash", "sequence", "session_id"):
                if response.get(field) != request[field]:
                    raise ValueError("Stale/incompatible Core response: " + field)
            return response["result"]


def watch_parent(parent_pid, interval):
    """The owned worker exits if its Core owner disappears, including during ML imports."""
    def watch():
        if os.name == "nt":
            kernel = ctypes.WinDLL("kernel32", use_last_error=True)
            kernel.OpenProcess.restype = ctypes.c_void_p
            handle = kernel.OpenProcess(0x00100000, False, parent_pid)
            if not handle:
                os._exit(71)
            try:
                while True:
                    time.sleep(interval)
                    if kernel.WaitForSingleObject(ctypes.c_void_p(handle), 0) != 258:
                        os._exit(71)
            finally:
                kernel.CloseHandle(ctypes.c_void_p(handle))
        else:
            while True:
                time.sleep(interval)
                if os.getppid() != parent_pid:
                    os._exit(71)
    threading.Thread(target=watch, daemon=True).start()


def train(job):
    d = job["defaults"]
    for key in ("temporal_specification_id", "temporal_producer_revision"):
        if not job.get(key) or job["metadata"].get(key) != job[key]:
            raise RuntimeError("Missing/inconsistent Core temporal producer contract: " + key)
    watch_parent(job["parent_pid"], d["IpcTimeoutSeconds"])
    try:
        from . import rl_dependencies
    except ImportError:
        import rl_dependencies
    dependency_path = Path(job["artifact_directory"]) / job["files"]["dependencies"]
    captured_dependencies = rl_dependencies.validate(dependency_path)
    if hashlib.sha256(dependency_path.read_bytes()).hexdigest() != job["dependencies_sha256"]:
        raise RuntimeError("Frozen dependency manifest hash mismatch")
    # Third-party diagnostics must never corrupt the stdout request stream.
    import numpy as np
    import torch
    import gymnasium as gym
    from stable_baselines3 import DQN
    import onnx
    import onnxruntime as ort
    try:
        from . import fixed_scaler
    except ImportError:
        import fixed_scaler
    for package, expected in (("stable-baselines3", d["StableBaselinesVersion"]),
                              ("gymnasium", d["GymnasiumVersion"])):
        if importlib.metadata.version(package) != expected:
            raise RuntimeError("Pinned dependency mismatch: " + package)
    torch.set_num_threads(d["CpuThreads"])
    torch.set_num_interop_threads(d["CpuThreads"])
    torch.use_deterministic_algorithms(True)
    torch.backends.cuda.matmul.allow_tf32 = False
    torch.backends.cudnn.allow_tf32 = False
    destination = Path(job["artifact_directory"])
    bars = json.loads((destination / job["files"]["bars"]).read_text(encoding="utf-8"))
    catalog = job["catalog"]
    raw = np.asarray([[float(b[name.lower()]) for name in d["MarketChannelNames"]] for b in bars], dtype=np.float64)
    train_blocks = catalog["train"][:1] if job["smoke_only"] else catalog["train"]
    samples = len(train_blocks) * d["EpisodeHorizon"]
    allocation = samples * d["WindowLength"] * d["MarketChannelCount"] * np.dtype(np.float64).itemsize
    if allocation > min(d["BufferByteLimit"], job["resource_limits"]["max_tensor_size_mib"] * 1024 * 1024):
        raise ValueError("Train-only scaler windows exceed captured memory cap")
    windows = np.empty((samples, d["WindowLength"], d["MarketChannelCount"]), dtype=np.float64)
    sample = 0
    for block in train_blocks:
        for decision in range(d["EpisodeHorizon"]):
            start = block["start_index"] + decision
            windows[sample] = raw[start:start + d["WindowLength"]]
            sample += 1
    spec = json.loads(job["metadata"]["feature_spec"])
    scaler = fixed_scaler.fit(windows, job["metadata"]["feature_mode"], spec, (), None)
    fixed_scaler.write_sidecar(destination / job["files"]["model"], scaler, job["metadata"])
    del windows, raw
    client = CoreClient(job)
    client.call("Ready", {"temporal_producer_revision": job["temporal_producer_revision"]})
    observations = []

    class CoreEnvironment(gym.Env):
        metadata = {"render_modes": []}
        def __init__(self, blocks, role, seed, checkpoint=0):
            self.blocks, self.role, self.seed_value, self.checkpoint = blocks, role, seed, checkpoint
            self.cursor = 0
            self.session_id = uuid.uuid4().hex
            self.created = False
            self.action_space = gym.spaces.Discrete(d["ActionCount"])
            self.observation_space = gym.spaces.Box(-np.inf, np.inf, (d["ObservationLength"],), np.float32)
        def observation(self, values):
            obs = np.asarray(values, dtype=np.float32)
            if obs.shape != self.observation_space.shape or not np.isfinite(obs).all():
                raise ValueError("Core observation violates Gym contract")
            if len(observations) < d["WindowLength"]:
                observations.append(obs.copy())
            return obs
        def reset(self, *, seed=None, options=None):
            super().reset(seed=self.seed_value + d["EnvironmentSeedOffset"] if seed is None else seed)
            block = self.blocks[self.cursor % len(self.blocks)]
            self.cursor += 1
            result = client.call("Reset", dict(block_id=block["id"], role=self.role, seed=self.seed_value,
                                              checkpoint=self.checkpoint), self.session_id)
            self.created = True
            return self.observation(result["observation"]), result["info"]
        def step(self, action):
            value = validate_action(action, d["ActionCount"])
            if not self.action_space.contains(value):
                raise ValueError("Illegal Gym action")
            result = client.call("Step", {"action": value}, self.session_id)
            return self.observation(result["observation"]), float(result["reward"]), result["terminated"], result["truncated"], result["info"]
        def close(self):
            if self.created:
                client.call("Dispose", {}, self.session_id)
                self.created = False

    total = d["SmokeTrainingTransitions"] if job["smoke_only"] else d["TrainingTransitionsPerSeed"]
    interval = d["SmokeCheckpointInterval"] if job["smoke_only"] else d["ValidationCheckpointInterval"]
    seeds = d["CandidateSeeds"][:1] if job["smoke_only"] else d["CandidateSeeds"]
    winner = None
    winner_key = None
    def evaluate(q_net, blocks, role, seed, checkpoint):
        was_training = q_net.training
        q_net.eval()
        environment = CoreEnvironment(blocks, role, seed, checkpoint)
        mean = None
        try:
            for _ in blocks:
                obs, _ = environment.reset()
                ended = False
                while not ended:
                    with torch.no_grad():
                        q = q_net(torch.as_tensor(obs.reshape(1, -1), device="cpu")).cpu().numpy()[0]
                    if not np.isfinite(q).all():
                        raise ValueError("Native Q output is nonfinite")
                    obs, _, terminated, truncated, info = environment.step(int(np.argmax(q)))
                    if truncated:
                        raise ValueError("Successful evaluation must not truncate")
                    ended = terminated
                    if "mean_return" in info:
                        mean = Decimal(info["mean_return"])
            if mean is None:
                raise RuntimeError("Core evaluation mean was not received")
            return mean
        finally:
            environment.close()
            q_net.train(was_training)

    class ValidatedDQN(DQN):
        def predict(self, observation, state=None, episode_start=None, deterministic=False):
            if not deterministic and self.exploration_rng.random() < self.exploration_rate:
                return np.asarray([self.action_space.sample()]), state
            return super().predict(observation, state, episode_start, deterministic=True)
        def train(self, gradient_steps, batch_size):
            nonlocal winner, winner_key
            super().train(gradient_steps, batch_size)
            if self.num_timesteps % interval == 0:
                mean = evaluate(self.q_net, catalog["validation"], "validation", self.candidate_seed, self.num_timesteps)
                key = (mean, -self.num_timesteps, -self.candidate_seed)
                if winner_key is None or key > winner_key:
                    winner_key = key
                    winner = (copy.deepcopy(self.q_net).cpu().eval(), self.candidate_seed, self.num_timesteps)
                client.call("Progress", {"steps": self.num_timesteps, "message": f"seed={self.candidate_seed}; validation={mean}; optimizer-complete"})

    for seed in seeds:
        environment = CoreEnvironment(train_blocks, "train", seed)
        model = ValidatedDQN("MlpPolicy", environment, learning_rate=float(d["LearningRate"]),
            buffer_size=d["ReplayCapacity"], learning_starts=d["LearningStarts"], batch_size=d["BatchSize"],
            tau=float(d["TargetTau"]), gamma=float(d["Gamma"]), train_freq=(d["TrainFrequency"], "step"),
            gradient_steps=d["GradientSteps"], target_update_interval=d["TargetUpdateInterval"],
            n_steps=d["TdSteps"],
            exploration_fraction=float(d["ExplorationFraction"]), exploration_initial_eps=float(d["ExplorationInitial"]),
            exploration_final_eps=float(d["ExplorationFinal"]), max_grad_norm=float(d["MaxGradientNorm"]),
            optimize_memory_usage=False, replay_buffer_kwargs={"handle_timeout_termination": True},
            policy_kwargs=dict(net_arch=[d["NetworkWidth"]] * d["NetworkHiddenLayerCount"], activation_fn=torch.nn.ReLU,
                optimizer_class=torch.optim.Adam, optimizer_kwargs=dict(betas=(float(d["AdamBeta1"]), float(d["AdamBeta2"])),
                    eps=float(d["AdamEpsilon"]), weight_decay=float(d["AdamWeightDecay"]), amsgrad=d["AdamAmsGrad"])),
            seed=seed, device="cpu", verbose=0)
        model.candidate_seed = seed
        model.exploration_rng = np.random.default_rng(seed + d["ExplorationSeedOffset"])
        model.action_space.seed(seed + d["ExplorationSeedOffset"])
        try:
            model.learn(total_timesteps=total, progress_bar=False)
            if model.num_timesteps != total:
                raise RuntimeError("DQN silently changed required transition count")
        finally:
            environment.close()
        del model
    if winner is None:
        raise RuntimeError("Required checkpoint selection missing")
    q_net, selected_seed, selected_step = winner
    client.call("Select", {"seed": selected_seed, "checkpoint": selected_step})
    if not job["smoke_only"]:
        evaluate(q_net, catalog["oos"], "oos", selected_seed, selected_step)
    torch.save(q_net.state_dict(), destination / job["files"]["native"])
    model_path = destination / job["files"]["model"]
    torch.onnx.export(q_net, torch.zeros((1, d["ObservationLength"]), dtype=torch.float32), model_path,
        input_names=[d["OnnxInputName"]], output_names=[d["OnnxOutputName"]], opset_version=d["OnnxOpset"],
        dynamo=False, training=torch.onnx.TrainingMode.EVAL, do_constant_folding=True,
        export_params=True, keep_initializers_as_inputs=False, dynamic_axes=None, external_data=False)
    graph = onnx.load(model_path)
    if [(entry.domain, entry.version) for entry in graph.opset_import] != [("", d["OnnxOpset"])]:
        raise RuntimeError("ONNX export changed the required opset")
    onnx.helper.set_model_props(graph, job["metadata"])
    onnx.checker.check_model(graph)
    onnx.save(graph, model_path)
    session_options = ort.SessionOptions()
    session_options.intra_op_num_threads = session_options.inter_op_num_threads = d["CpuThreads"]
    session = ort.InferenceSession(str(model_path), session_options, providers=["CPUExecutionProvider"])
    observations.extend([np.zeros(d["ObservationLength"], np.float32), np.ones(d["ObservationLength"], np.float32),
                         -np.ones(d["ObservationLength"], np.float32)])
    references = []
    for obs in observations:
        with torch.no_grad():
            reference = q_net(torch.as_tensor(obs.reshape(1, -1))).cpu().numpy()
        actual = session.run([d["OnnxOutputName"]], {d["OnnxInputName"]: obs.reshape(1, -1)})[0]
        if not np.isfinite(reference).all() or not np.isfinite(actual).all() or not np.allclose(actual, reference,
            atol=float(d["AbsoluteTolerance"]), rtol=float(d["RelativeTolerance"])) or int(np.argmax(actual)) != int(np.argmax(reference)):
            raise RuntimeError("Native/Python ORT Q/action parity failed")
        references.append(reference[0].tolist())
    (destination / job["files"]["parity"]).write_text(json.dumps(dict(observations=[o.tolist() for o in observations],
        q_values=references), allow_nan=False, separators=(",", ":")), encoding="utf-8")
    if captured_dependencies["source_script_sha256"] != hashlib.sha256(Path(__file__).read_bytes()).hexdigest():
        raise RuntimeError("Training implementation changed during the run")
    rl_dependencies.validate(dependency_path)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    args = parser.parse_args()
    train(json.loads(Path(args.config).read_text(encoding="utf-8")))


if __name__ == "__main__":
    main()
