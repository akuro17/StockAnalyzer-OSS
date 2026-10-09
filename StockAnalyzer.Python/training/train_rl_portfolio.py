"""Portfolio PPO worker: raw logit transport; Core owns observation, masks, weights and all money (evaluation only, no continuation)."""
from __future__ import annotations

import argparse
import copy
import hashlib
import importlib.metadata
import json
import math
import threading
from decimal import Decimal
from pathlib import Path
import uuid

try:
    from .train_rl import CoreClient, watch_parent
    from . import rl_dependencies
except ImportError:
    from train_rl import CoreClient, watch_parent
    import rl_dependencies


# Parity probe rows appended after the recorded observations: zeros and constant inputs of this magnitude in both signs.
PARITY_PROBE_LEVEL = 0.5


class PortfolioClient(CoreClient):
    """Same wire protocol as T15/T16. The all-block catalog validation of the base constructor does not apply to the portfolio job
    (its metadata legitimately carries evaluation_specification_id), so the base initialiser is deliberately not called."""

    def __init__(self, job):
        self.job = job
        self.sequence = 0
        self.lock = threading.Lock()


def build_policy_classes(torch, gym, np):
    """Policy classes are created after the pinned imports so that this module stays importable without the ML stack."""
    from stable_baselines3.common.policies import ActorCriticPolicy
    from stable_baselines3.common.torch_layers import BaseFeaturesExtractor

    def init_linear(module, gain):
        if isinstance(module, torch.nn.Linear):
            torch.nn.init.orthogonal_(module.weight, gain=gain)
            module.bias.data.fill_(0.0)

    class SlotEncoder(BaseFeaturesExtractor):
        """One encoder shared by every asset slot. Masked and padded slots (mask channel 0) contribute exactly zero to the context,
        so the logits of the available slots do not depend on how the symbols are ordered."""

        def __init__(self, observation_space, slots, slot_length, global_length, width, hidden_layers, actor_gain):
            super().__init__(observation_space, slots * width + width + global_length)
            self.slots, self.slot_length, self.global_length, self.width, self.actor_gain = slots, slot_length, global_length, width, actor_gain
            sizes = [slot_length] + [width] * hidden_layers
            layers = []
            for before, after in zip(sizes[:-1], sizes[1:]):
                layers += [torch.nn.Linear(before, after), torch.nn.Tanh()]
            self.encoder = torch.nn.Sequential(*layers)

        def forward(self, observation):
            batch = observation.shape[0]
            slots = observation[:, :self.slots * self.slot_length].reshape(batch, self.slots, self.slot_length)
            context = observation[:, self.slots * self.slot_length:]
            mask = slots[:, :, self.slot_length - 1:self.slot_length]
            embedding = self.encoder(slots) * mask
            pooled = embedding.sum(dim=1) / mask.sum(dim=1).clamp(min=1.0)
            return torch.cat([embedding.reshape(batch, self.slots * self.width), pooled, context], dim=1)

    class SlotLogitHead(torch.nn.Module):
        """Per-slot logit from the slot's own embedding plus the shared context, and a cash logit from the context alone."""

        def __init__(self, slots, width, global_length, actor_gain):
            super().__init__()
            self.slots, self.width = slots, width
            self.slot_head = torch.nn.Sequential(torch.nn.Linear(2 * width + global_length, width), torch.nn.Tanh(), torch.nn.Linear(width, 1))
            self.cash_head = torch.nn.Sequential(torch.nn.Linear(width + global_length, width), torch.nn.Tanh(), torch.nn.Linear(width, 1))
            for head in (self.slot_head, self.cash_head):
                head.apply(lambda module: init_linear(module, math.sqrt(2)))
                init_linear(head[-1], actor_gain)

        def forward(self, latent):
            batch = latent.shape[0]
            embedding = latent[:, :self.slots * self.width].reshape(batch, self.slots, self.width)
            context = latent[:, self.slots * self.width:]
            expanded = context.unsqueeze(1).expand(-1, self.slots, -1)
            slot_logits = self.slot_head(torch.cat([embedding, expanded], dim=2)).squeeze(-1)
            cash = self.cash_head(context)
            return torch.cat([cash, slot_logits], dim=1)

    class PortfolioPolicy(ActorCriticPolicy):
        def _build(self, lr_schedule):
            super()._build(lr_schedule)
            extractor = self.features_extractor
            self.action_net = SlotLogitHead(extractor.slots, extractor.width, extractor.global_length, extractor.actor_gain)
            self.optimizer = self.optimizer_class(self.parameters(), lr=lr_schedule(1), **self.optimizer_kwargs)

    return SlotEncoder, PortfolioPolicy


def build_ppo(ppo_class, torch, policy_class, slot_encoder_class, environment, d, p, pf, slots, slot_length, seed):
    """The one construction of the portfolio PPO (also used by the policy self-check test): pinned SB3 arguments from the Core contract."""
    return ppo_class(policy_class, environment, learning_rate=float(p["LearningRate"]), n_steps=p["RolloutSteps"],
        batch_size=d["BatchSize"], n_epochs=p["Epochs"], gamma=float(d["Gamma"]), gae_lambda=float(p["GaeLambda"]),
        clip_range=float(p["ClipRange"]), clip_range_vf=None, normalize_advantage=True,
        ent_coef=float(p["EntropyCoefficient"]), vf_coef=float(p["ValueCoefficient"]),
        max_grad_norm=float(p["MaxGradientNorm"]), use_sde=False, sde_sample_freq=-1, target_kl=float(p["TargetKl"]),
        policy_kwargs=dict(net_arch=dict(pi=[], vf=[d["NetworkWidth"]] * d["NetworkHiddenLayerCount"]), activation_fn=torch.nn.Tanh,
            ortho_init=True, log_std_init=float(p["LogStdInitial"]), full_std=True, use_expln=False, squash_output=False,
            normalize_images=False, share_features_extractor=True, optimizer_class=torch.optim.Adam,
            features_extractor_class=slot_encoder_class,
            features_extractor_kwargs=dict(slots=slots, slot_length=slot_length, global_length=pf["GlobalFeatureCount"],
                width=d["NetworkWidth"], hidden_layers=d["NetworkHiddenLayerCount"], actor_gain=float(p["ActorOutputGain"])),
            optimizer_kwargs=dict(betas=(float(d["AdamBeta1"]), float(d["AdamBeta2"])), eps=float(p["AdamEpsilon"]),
                weight_decay=float(d["AdamWeightDecay"]), amsgrad=d["AdamAmsGrad"])),
        seed=seed, device="cpu", verbose=0)


def export_actor(torch, copy, policy):
    """The deterministic mean actor: shared features, the (empty) policy trunk and the slot logit head, detached from the training policy."""
    return torch.nn.Sequential(copy.deepcopy(policy.features_extractor), copy.deepcopy(policy.mlp_extractor.policy_net),
                               copy.deepcopy(policy.action_net)).cpu().eval()


def apply_transfer(torch, copy, policy, job, destination):
    """Fine-tune start (only when the job carries a transfer block): loads the copied base actor weights into the freshly built policy.

    The file must be the one Core hashed into the job and its key set must be exactly the key set of the exported actor of this very
    architecture (shared encoder under "0.", the empty policy trunk contributes nothing, the slot logit head under "2."). The critic, the log
    standard deviation and the optimizer state stay freshly initialised. Returns the frozen encoder tensors (a snapshot to verify after the
    training) when the encoder is frozen, else None."""
    transfer, names = job["transfer"], job["transfer_defaults"]
    name = transfer["init_weights"]
    if Path(name).name != name:
        raise RuntimeError("Transfer weights must be a file of the run folder")
    path = Path(destination) / name
    if hashlib.sha256(path.read_bytes()).hexdigest() != transfer["init_weights_sha256"]:
        raise RuntimeError("Transfer weights changed after the job was written")
    freeze = transfer["freeze"]
    if freeze not in (names["EncoderFrozen"], names["NoFreeze"]):
        raise RuntimeError("Unknown transfer freeze: " + str(freeze))
    state = torch.load(path, map_location="cpu", weights_only=True)
    expected = export_actor(torch, copy, policy).state_dict()
    if (not isinstance(state, dict) or set(state.keys()) != set(expected.keys())
            or any(not torch.is_tensor(state[key]) or state[key].shape != expected[key].shape or state[key].dtype != expected[key].dtype for key in expected)):
        raise RuntimeError("Transfer weights do not match the actor architecture")

    def part(prefix):
        return {key[len(prefix):]: value for key, value in state.items() if key.startswith(prefix)}

    policy.features_extractor.load_state_dict(part("0."), strict=True)
    policy.action_net.load_state_dict(part("2."), strict=True)
    if freeze != names["EncoderFrozen"]:
        return None
    frozen = {key: tensor.detach().clone() for key, tensor in policy.features_extractor.named_parameters()}
    for parameter in policy.features_extractor.parameters():
        parameter.requires_grad_(False)
    return frozen


def verify_frozen(torch, policy, frozen):
    """A frozen encoder must end the training bit-for-bit equal to what it started with."""
    current = dict(policy.features_extractor.named_parameters())
    if set(current) != set(frozen) or any(parameter.requires_grad or not torch.equal(parameter.detach(), frozen[key]) for key, parameter in current.items()):
        raise RuntimeError("Frozen encoder changed")


def train(job):
    d, p, pf = job["defaults"], job["ppo_defaults"], job["portfolio_defaults"]
    watch_parent(job["parent_pid"], d["IpcTimeoutSeconds"])
    destination = Path(job["artifact_directory"])
    dependency_path = destination / job["files"]["dependencies"]
    dependencies = rl_dependencies.validate(dependency_path)
    if hashlib.sha256(dependency_path.read_bytes()).hexdigest() != job["dependencies_sha256"]:
        raise RuntimeError("Frozen dependency manifest changed")
    if dependencies["source_script_sha256"] != hashlib.sha256(Path(__file__).read_bytes()).hexdigest():
        raise RuntimeError("Portfolio trainer changed after the dependency capture")
    import numpy as np
    import torch
    import gymnasium as gym
    import onnx
    import onnxruntime as ort
    from stable_baselines3 import PPO
    for package, expected in (("stable-baselines3", d["StableBaselinesVersion"]), ("gymnasium", d["GymnasiumVersion"])):
        if importlib.metadata.version(package) != expected:
            raise RuntimeError("Pinned PPO dependency mismatch: " + package)
    torch.set_num_threads(d["CpuThreads"])
    torch.set_num_interop_threads(d["CpuThreads"])
    torch.use_deterministic_algorithms(True)
    torch.backends.cuda.matmul.allow_tf32 = False
    torch.backends.cudnn.allow_tf32 = False

    slots, length, logit_count = job["n_max"], job["observation_length"], job["n_max"] + 1
    slot_length = d["WindowLength"] * job.get("market_channels", d["MarketChannelCount"]) + pf["AssetAuxiliaryChannelCount"]  # only a job of another observation variant than v1 carries market_channels
    if slots * slot_length + pf["GlobalFeatureCount"] != length or logit_count != slots + 1:
        raise RuntimeError("Core observation/action shape disagrees with the portfolio contract")
    SlotEncoder, PortfolioPolicy = build_policy_classes(torch, gym, np)
    client = PortfolioClient(job)
    client.call("Ready", {})
    catalog = job["catalog"]
    train_blocks = catalog["train"][:1] if job["smoke_only"] else catalog["train"]
    observations = []

    class PortfolioEnvironment(gym.Env):
        metadata = {"render_modes": []}

        def __init__(self, blocks, role, seed, checkpoint=0):
            self.blocks, self.role, self.seed_value, self.checkpoint = blocks, role, seed, checkpoint
            self.cursor, self.created, self.session_id = 0, False, uuid.uuid4().hex
            # SB3 requires finite Box bounds; the float32 extremes make the pinned collector's transport clip a no-op, so only Core ever
            # shapes the proposal (masks and softmax weights).
            bound = np.finfo(np.float32).max
            self.action_space = gym.spaces.Box(-bound, bound, (logit_count,), np.float32)
            self.observation_space = gym.spaces.Box(-np.inf, np.inf, (length,), np.float32)

        def observation(self, values):
            obs = np.asarray(values, np.float32)
            if obs.shape != self.observation_space.shape or not np.isfinite(obs).all():
                raise ValueError("Core observation violates the portfolio contract")
            if len(observations) < d["WindowLength"]:
                observations.append(obs.copy())
            return obs

        def reset(self, *, seed=None, options=None):
            super().reset(seed=self.seed_value + d["EnvironmentSeedOffset"] if seed is None else seed)
            block = self.blocks[self.cursor % len(self.blocks)]
            self.cursor += 1
            result = client.call("Reset", dict(block_id=block["id"], role=self.role, seed=self.seed_value, checkpoint=self.checkpoint), self.session_id)
            self.created = True
            return self.observation(result["observation"]), result["info"]

        def step(self, raw_actions):
            raw = np.asarray(raw_actions, np.float32)
            if raw.shape != (logit_count,) or not np.isfinite(raw).all():
                raise ValueError("The policy must propose one finite logit per slot")
            result = client.call("Step", {"actions": raw.tolist()}, self.session_id)
            return self.observation(result["observation"]), float(result["reward"]), result["terminated"], result["truncated"], result["info"]

        def close(self):
            if self.created:
                client.call("Dispose", {}, self.session_id)
                self.created = False

    def actor(policy):
        return export_actor(torch, copy, policy)

    def evaluate(mean_actor, blocks, role, seed, checkpoint):
        environment = PortfolioEnvironment(blocks, role, seed, checkpoint)
        mean = None
        try:
            for _ in blocks:
                obs, _info = environment.reset()
                ended = False
                while not ended:
                    with torch.no_grad():
                        raw = mean_actor(torch.as_tensor(obs.reshape(1, -1))).cpu().numpy()[0]
                    obs, _reward, ended, truncated, info = environment.step(raw)
                    if truncated:
                        raise RuntimeError("Portfolio evaluation must not truncate")
                    if "mean_return" in info:
                        mean = Decimal(info["mean_return"])
        finally:
            environment.close()
        if mean is None:
            raise RuntimeError("Core portfolio validation mean missing")
        return mean

    total = d["SmokeTrainingTransitions"] if job["smoke_only"] else p["TrainingTransitionsPerSeed"]
    interval = d["SmokeCheckpointInterval"] if job["smoke_only"] else p["ValidationCheckpointInterval"]
    seeds = d["CandidateSeeds"][:1] if job["smoke_only"] else d["CandidateSeeds"]
    seeds = [seed + job.get("seed_offset", 0) for seed in seeds]  # only a run of another run-level seed carries seed_offset
    best = {}

    class ValidatedPPO(PPO):
        def train(self):
            super().train()
            if self.num_timesteps % interval == 0:
                mean_actor = actor(self.policy)
                mean = evaluate(mean_actor, catalog["validation"], "validation", self.candidate_seed, self.num_timesteps)
                key = (mean, -self.num_timesteps, -self.candidate_seed)
                if "key" not in best or key > best["key"]:
                    best.update(key=key, actor=mean_actor, seed=self.candidate_seed, step=self.num_timesteps)
                client.call("Progress", dict(steps=self.num_timesteps, message=f"PPO seed={self.candidate_seed}; validation={mean}"))

    for seed in seeds:
        environment = PortfolioEnvironment(train_blocks, "train", seed)
        model = build_ppo(ValidatedPPO, torch, PortfolioPolicy, SlotEncoder, environment, d, p, pf, slots, slot_length, seed)
        model.candidate_seed = seed
        frozen = apply_transfer(torch, copy, model.policy, job, destination) if "transfer" in job else None
        try:
            model.learn(total_timesteps=total, progress_bar=False)
            if model.num_timesteps != total:
                raise RuntimeError("PPO changed the required exact rollout count")
            if frozen is not None:
                verify_frozen(torch, model.policy, frozen)
        finally:
            environment.close()
    if "actor" not in best:
        raise RuntimeError("Portfolio PPO produced no validated candidate")
    mean_actor, selected_seed, selected_step = best["actor"], best["seed"], best["step"]
    client.call("Select", dict(seed=selected_seed, checkpoint=selected_step))
    torch.save(mean_actor.state_dict(), destination / job["files"]["native"])
    model_path = destination / job["files"]["model"]
    torch.onnx.export(mean_actor, torch.zeros((1, length), dtype=torch.float32), model_path,
        input_names=[pf["OnnxInputName"]], output_names=[pf["OnnxOutputName"]], opset_version=d["OnnxOpset"], dynamo=False,
        training=torch.onnx.TrainingMode.EVAL, do_constant_folding=True, export_params=True,
        keep_initializers_as_inputs=False, dynamic_axes=None, external_data=False)
    graph = onnx.load(model_path)
    if [(entry.domain, entry.version) for entry in graph.opset_import] != [("", d["OnnxOpset"])]:
        raise RuntimeError("Portfolio ONNX opset changed")
    onnx.helper.set_model_props(graph, job["metadata"])
    onnx.checker.check_model(graph)
    onnx.save(graph, model_path)
    options = ort.SessionOptions()
    options.intra_op_num_threads = options.inter_op_num_threads = d["CpuThreads"]
    session = ort.InferenceSession(str(model_path), options, providers=["CPUExecutionProvider"])
    rows = list(observations) + [np.zeros(length, np.float32), np.full(length, PARITY_PROBE_LEVEL, np.float32), np.full(length, -PARITY_PROBE_LEVEL, np.float32)]
    references = []
    for obs in rows:
        with torch.no_grad():
            reference = mean_actor(torch.as_tensor(obs.reshape(1, -1))).numpy()
        actual = session.run([pf["OnnxOutputName"]], {pf["OnnxInputName"]: obs.reshape(1, -1)})[0]
        if actual.shape != (1, logit_count) or not np.isfinite(actual).all() or not np.isfinite(reference).all() or not np.allclose(
                actual, reference, atol=float(d["AbsoluteTolerance"]), rtol=float(d["RelativeTolerance"])):
            raise RuntimeError("Native/Python ORT portfolio actor parity failed")
        references.append(actual[0].tolist())
    (destination / job["files"]["parity"]).write_text(json.dumps(dict(observations=[row.tolist() for row in rows], logits=references),
        allow_nan=False, separators=(",", ":")), encoding="utf-8")
    rl_dependencies.validate(dependency_path)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    train(json.loads(Path(parser.parse_args().config).read_text(encoding="utf-8")))
