"""T16 PPO: raw Gaussian transport; Core owns frozen direction and all finance."""
from __future__ import annotations

import argparse
import copy
import hashlib
import inspect
import json
from pathlib import Path
import textwrap
import uuid
from decimal import Decimal

try:
    from .train_rl import CoreClient, watch_parent, validate_evaluation_catalog
    from . import rl_dependencies, rl_ppo_guard, rl_checkpoint
except ImportError:
    from train_rl import CoreClient, watch_parent, validate_evaluation_catalog
    import rl_dependencies
    import rl_ppo_guard
    import rl_checkpoint


def raw_rollout_transport():
    """Replace exactly the pinned collector's transport clip, preserving likelihood/GAE/update code."""
    from stable_baselines3.common import on_policy_algorithm
    original = textwrap.dedent(inspect.getsource(on_policy_algorithm.OnPolicyAlgorithm.collect_rollouts))
    needle = "clipped_actions = np.clip(actions, self.action_space.low, self.action_space.high)"
    if original.count(needle) != 1:
        raise RuntimeError("Pinned PPO collector transport is incompatible")
    namespace = dict(vars(on_policy_algorithm))
    exec(compile(original.replace(needle, "clipped_actions = actions"), "<T16 raw transport>", "exec"), namespace)
    return namespace["collect_rollouts"], hashlib.sha256(original.encode("utf-8")).hexdigest()


def train(job):
    d, c = job["defaults"], job["continuous_defaults"]
    validate_evaluation_catalog(job)
    watch_parent(job["parent_pid"], d["IpcTimeoutSeconds"])
    destination = Path(job["artifact_directory"])
    for key in ("temporal_specification_id", "temporal_producer_revision"):
        if not job.get(key) or job["metadata"].get(key) != job[key]:
            raise RuntimeError("Missing/inconsistent Core temporal dependency contract: " + key)
    temporal_path = destination / job["direction_temporal_file"]
    if hashlib.sha256(temporal_path.read_bytes()).hexdigest() != job["direction"]["temporal_evidence_sha256"]:
        raise RuntimeError("Frozen direction temporal evidence changed")
    dependency_path = destination / job["files"]["dependencies"]
    dependencies = rl_dependencies.validate(dependency_path)
    guard_identity = rl_ppo_guard.identities()
    if any(dependencies.get(key) != value for key, value in guard_identity.items()):
        raise RuntimeError("Frozen PPO guard/upstream identity changed")
    if job.get("diagnostics_specification_id") != job["metadata"].get("diagnostics_specification_id"):
        raise RuntimeError("Core PPO diagnostic contract mismatch")
    if hashlib.sha256(dependency_path.read_bytes()).hexdigest() != job["dependencies_sha256"]:
        raise RuntimeError("Frozen dependency manifest changed")
    import importlib.metadata
    import numpy as np
    import torch
    import gymnasium as gym
    from stable_baselines3 import PPO
    import onnx
    import onnxruntime as ort
    for package, expected in (("stable-baselines3", d["StableBaselinesVersion"]), ("gymnasium", d["GymnasiumVersion"])):
        if importlib.metadata.version(package) != expected:
            raise RuntimeError("Pinned PPO dependency mismatch: " + package)
    torch.set_num_threads(d["CpuThreads"])
    torch.set_num_interop_threads(d["CpuThreads"])
    torch.use_deterministic_algorithms(True)
    torch.backends.cuda.matmul.allow_tf32 = False
    torch.backends.cudnn.allow_tf32 = False
    # The copied sidecar is byte-identical to the selected T15 scaler; no fit or refit occurs here.
    if hashlib.sha256((destination / c["DirectionScaler"]).read_bytes()).hexdigest() != job["direction"]["scaler_sha256"]:
        raise RuntimeError("Frozen direction scaler changed")
    client = CoreClient(job)
    client.call("Ready", {"temporal_producer_revision": job["temporal_producer_revision"]})
    resumable = "checkpoint_contract" in job
    restored = rl_checkpoint.load(job["resume_path"], job["checkpoint_contract"]) if job.get("resume_path") else None
    restored_sessions = client.call("RestoreCheckpoint", {})["sessions"] if restored else {}
    if restored and restored["state"]["phase"] == "worker_complete":
        for name, expected in restored["state"]["exports"].items():
            if hashlib.sha256((destination / name).read_bytes()).hexdigest() != expected:
                raise RuntimeError("Completed export changed before qualification resume")
        return
    clip_fixtures = []
    for proposal in ((-3., 3.), (-1., -1.), (0., 0.), (1., 1.), (.125, -.125)):
        raw = np.asarray(proposal, np.float32)
        mapped = client.call("Map", {"actions": raw.tolist()})
        if not np.array_equal(np.clip(raw, c["ActionMinimum"], c["ActionMaximum"]), np.asarray([mapped["clipped_size"], mapped["clipped_stop"]], np.float32)):
            raise RuntimeError("Core sole clip differs from pinned bounded action")
        clip_fixtures.append(dict(raw=raw.tolist(), core=mapped))
    catalog = job["catalog"]
    train_blocks = catalog["train"][:1] if job["smoke_only"] else catalog["train"]
    observations = []

    class CoreEnvironment(gym.Env):
        metadata = {"render_modes": []}
        def __init__(self, blocks, role, seed, checkpoint=0):
            self.blocks, self.role, self.seed_value, self.checkpoint = blocks, role, seed, checkpoint
            self.cursor, self.created, self.session_id = 0, False, uuid.uuid4().hex
            self.action_space = gym.spaces.Box(c["ActionMinimum"], c["ActionMaximum"], (c["ActionCount"],), np.float32)
            self.observation_space = gym.spaces.Box(-np.inf, np.inf, (d["ObservationLength"],), np.float32)
        def observation(self, values):
            obs = np.asarray(values, np.float32)
            if obs.shape != self.observation_space.shape or not np.isfinite(obs).all():
                raise ValueError("Core observation violates PPO contract")
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
        def step(self, raw_actions):
            raw = np.asarray(raw_actions, np.float32)
            if raw.shape != (c["ActionCount"],) or not np.isfinite(raw).all():
                raise ValueError("PPO raw proposal must be a finite pair")
            # Do not check Box.contains: finite out-of-bounds proposals are clipped ONLY by Core.
            result = client.call("Step", {"actions": raw.tolist()}, self.session_id)
            return self.observation(result["observation"]), float(result["reward"]), result["terminated"], result["truncated"], result["info"]
        def close(self):
            if self.created:
                client.call("Dispose", {}, self.session_id)
                self.created = False

    def actor(policy):
        # FlattenExtractor is stateless and normalization is disabled; export only deterministic Gaussian means.
        return torch.nn.Sequential(copy.deepcopy(policy.features_extractor), copy.deepcopy(policy.mlp_extractor.policy_net),
            copy.deepcopy(policy.action_net)).cpu().eval()

    total = d["SmokeTrainingTransitions"] if job["smoke_only"] else c["TrainingTransitionsPerSeed"]
    interval = d["SmokeCheckpointInterval"] if job["smoke_only"] else c["ValidationCheckpointInterval"]
    seeds = d["CandidateSeeds"][:1] if job["smoke_only"] else d["CandidateSeeds"]
    winner = winner_key = None
    state = restored["state"] if restored else dict(phase="train", seed_index=0, selected=False, oos_index=0, parity_index=0, paired=[])
    if restored:
        observations.extend(state["observations"])
    current_model = current_environment = None

    def checkpoint(phase):
        if resumable:
            state.update(phase=phase, observations=observations,
                winner=(winner[0].state_dict(), winner[1], winner[2]) if winner else None,
                winner_key=tuple(str(x) if isinstance(x, Decimal) else x for x in winner_key) if winner_key else None)
            rl_checkpoint.save(client, current_model, current_environment, state)

    def evaluate(mean_actor, blocks, role, seed, checkpoint, capture=False):
        environment = CoreEnvironment(blocks, role, seed, checkpoint)
        mean, paired = None, []
        try:
            for block in blocks:
                obs, _ = environment.reset()
                ended, steps = False, []
                while not ended:
                    with torch.no_grad():
                        raw = mean_actor(torch.as_tensor(obs.reshape(1, -1))).cpu().numpy()[0]
                    before = obs.copy()
                    obs, _, terminated, truncated, info = environment.step(raw)
                    if truncated:
                        raise RuntimeError("PPO evaluation must not truncate")
                    if capture:
                        steps.append(dict(observation=before.tolist(), actions=raw.tolist(), direction=info["action"],
                            outcome=info["outcome"], account=info["account"], fills=info["fills"], financial_trace=info["financial_trace"],
                            entry_execution=info.get("entry_execution")))
                    ended = terminated
                    if "mean_return" in info:
                        mean = Decimal(info["mean_return"])
                if capture:
                    paired.append(dict(block_id=block["id"], steps=steps))
            if not capture and mean is None and role == "validation":
                raise RuntimeError("Core PPO evaluation mean missing")
            return paired if capture else mean
        finally:
            environment.close()

    collector, guarded_train = rl_ppo_guard.adapted_functions()

    class ValidatedPPO(PPO):
        collect_rollouts = collector
        def train(self):
            nonlocal winner, winner_key
            guarded_train(self)
            client.call("UpdateDiagnostics", dict(self.guard_summary.payload(), seed=self.candidate_seed,
                checkpoint_step=self.num_timesteps, source_revision=job["source_revision"],
                contract_hash=job["contract_hash"], dependencies_sha256=job["dependencies_sha256"],
                specification_id=job["diagnostics_specification_id"]))
            if self.num_timesteps % interval == 0:
                mean_actor = actor(self.policy)
                mean = evaluate(mean_actor, catalog["validation"], "validation", self.candidate_seed, self.num_timesteps)
                key = (mean, -self.num_timesteps, -self.candidate_seed)
                if winner_key is None or key > winner_key:
                    winner, winner_key = (mean_actor, self.candidate_seed, self.num_timesteps), key
                client.call("Progress", dict(steps=self.num_timesteps,
                    message=f"PPO seed={self.candidate_seed}; validation={mean}; rollout-optimizer-complete"))
            checkpoint("train")

    for seed_index, seed in enumerate(seeds):
        if seed_index < state["seed_index"]:
            continue
        state["seed_index"] = seed_index
        environment = CoreEnvironment(train_blocks, "train", seed)
        model = ValidatedPPO("MlpPolicy", environment, learning_rate=float(c["LearningRate"]), n_steps=c["RolloutSteps"],
            batch_size=d["BatchSize"], n_epochs=c["Epochs"], gamma=float(d["Gamma"]), gae_lambda=float(c["GaeLambda"]),
            clip_range=float(c["ClipRange"]), clip_range_vf=None, normalize_advantage=True,
            ent_coef=float(c["EntropyCoefficient"]), vf_coef=float(c["ValueCoefficient"]),
            max_grad_norm=float(c["MaxGradientNorm"]), use_sde=False, sde_sample_freq=-1, target_kl=float(c["TargetKl"]),
            policy_kwargs=dict(net_arch=dict(pi=[d["NetworkWidth"]] * d["NetworkHiddenLayerCount"],
                vf=[d["NetworkWidth"]] * d["NetworkHiddenLayerCount"]), activation_fn=torch.nn.Tanh, ortho_init=True,
                log_std_init=float(c["LogStdInitial"]), full_std=True, use_expln=False, squash_output=False,
                normalize_images=False, share_features_extractor=True, optimizer_class=torch.optim.Adam,
                optimizer_kwargs=dict(betas=(float(d["AdamBeta1"]), float(d["AdamBeta2"])), eps=float(c["AdamEpsilon"]),
                    weight_decay=float(d["AdamWeightDecay"]), amsgrad=d["AdamAmsGrad"])),
            seed=seed, device="cpu", verbose=0)
        model.candidate_seed = seed
        current_model, current_environment = model, environment
        if restored:
            if state.get("winner"):
                mean_actor = actor(model.policy)
                mean_actor.load_state_dict(state["winner"][0], strict=True)
                winner = (mean_actor, state["winner"][1], state["winner"][2])
                winner_key = (Decimal(state["winner_key"][0]), state["winner_key"][1], state["winner_key"][2])
            rl_checkpoint.restore(model, environment, restored, restored_sessions)
            restored = None
        try:
            if state["phase"] == "train" and model.num_timesteps < total:
                model.learn(total_timesteps=total - model.num_timesteps, progress_bar=False, reset_num_timesteps=model.num_timesteps == 0)
            if model.num_timesteps != total:
                raise RuntimeError("PPO changed required exact full-rollout count")
        finally:
            environment.close()
        if state["phase"] != "train":
            break
    if winner is None:
        raise RuntimeError("PPO winner missing")
    mean_actor, selected_seed, selected_step = winner
    rl_ppo_guard.tree("selected_actor", mean_actor.state_dict())
    if rl_ppo_guard.identities() != guard_identity:
        raise RuntimeError("PPO guard/upstream source changed during training")
    if not state["selected"]:
        client.call("Select", dict(seed=selected_seed, checkpoint=selected_step))
        state["selected"] = True
        checkpoint("selected")
    if not job["smoke_only"]:
        for index in range(state["oos_index"], len(catalog["oos"])):
            evaluate(mean_actor, catalog["oos"][index:index + 1], "oos", selected_seed, selected_step)
            state["oos_index"] = index + 1
            checkpoint("oos")
    for index in range(state["parity_index"], len(catalog["validation"])):
        state["paired"].extend(evaluate(mean_actor, catalog["validation"][index:index + 1], "parity", selected_seed, selected_step, capture=True))
        state["parity_index"] = index + 1
        checkpoint("parity")
    paired = state["paired"]
    torch.save(mean_actor.state_dict(), destination / job["files"]["native"])
    model_path = destination / job["files"]["model"]
    torch.onnx.export(mean_actor, torch.zeros((1, d["ObservationLength"]), dtype=torch.float32), model_path,
        input_names=[d["OnnxInputName"]], output_names=[c["OutputName"]], opset_version=d["OnnxOpset"], dynamo=False,
        training=torch.onnx.TrainingMode.EVAL, do_constant_folding=True, export_params=True,
        keep_initializers_as_inputs=False, dynamic_axes=None, external_data=False)
    graph = onnx.load(model_path)
    if [(entry.domain, entry.version) for entry in graph.opset_import] != [("", d["OnnxOpset"])]:
        raise RuntimeError("PPO ONNX opset changed")
    onnx.helper.set_model_props(graph, job["metadata"])
    onnx.checker.check_model(graph)
    onnx.save(graph, model_path)
    options = ort.SessionOptions()
    options.intra_op_num_threads = options.inter_op_num_threads = d["CpuThreads"]
    ort_session = ort.InferenceSession(str(model_path), options, providers=["CPUExecutionProvider"])
    observations.extend([np.zeros(d["ObservationLength"], np.float32), np.ones(d["ObservationLength"], np.float32),
                         -np.ones(d["ObservationLength"], np.float32)])
    references = []
    for obs in observations:
        with torch.no_grad():
            reference = mean_actor(torch.as_tensor(obs.reshape(1, -1))).numpy()
        actual = ort_session.run([c["OutputName"]], {d["OnnxInputName"]: obs.reshape(1, -1)})[0]
        if not np.isfinite(actual).all() or not np.isfinite(reference).all() or not np.allclose(actual, reference,
            atol=float(d["AbsoluteTolerance"]), rtol=float(d["RelativeTolerance"])):
            raise RuntimeError("Native/Python ORT actor parity failed")
        references.append(reference[0].tolist())
    # Verify Python ORT on every account-aware native episode observation, not only generic tensors.
    for episode in paired:
        for step in episode["steps"]:
            actual = ort_session.run([c["OutputName"]], {d["OnnxInputName"]: np.asarray(step["observation"], np.float32).reshape(1, -1)})[0][0]
            if not np.allclose(actual, np.asarray(step["actions"], np.float32), atol=float(d["AbsoluteTolerance"]), rtol=float(d["RelativeTolerance"])):
                raise RuntimeError("Python ORT account-aware actor parity failed")
    (destination / job["files"]["parity"]).write_text(json.dumps(dict(observations=[o.tolist() for o in observations],
        normalized_actions=references, paired_episodes=paired, clip_fixtures=clip_fixtures,
        financial_parity_specification_id=job["financial_parity_specification_id"]), allow_nan=False,
        separators=(",", ":")), encoding="utf-8")
    if dependencies["source_script_sha256"] != hashlib.sha256(Path(__file__).read_bytes()).hexdigest():
        raise RuntimeError("PPO implementation changed during training")
    rl_dependencies.validate(dependency_path)
    if rl_ppo_guard.identities() != guard_identity:
        raise RuntimeError("PPO guard/upstream source changed during export")
    state["exports"] = {job["files"][key]: hashlib.sha256((destination / job["files"][key]).read_bytes()).hexdigest()
                        for key in ("model", "native", "parity")}
    checkpoint("worker_complete")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    try:
        train(json.loads(Path(parser.parse_args().config).read_text(encoding="utf-8")))
    except rl_checkpoint.Paused:
        raise SystemExit(75)
