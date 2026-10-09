"""Self-check of the portfolio PPO policy wiring (opt-in xUnit test RlPortfolioPolicySelfCheckTests runs it with the managed venv).

It builds the policy exactly as the trainer does (train_rl_portfolio.build_ppo / build_policy_classes / export_actor) on a tiny seeded
environment and prints one JSON line per check: {"check": ..., "ok": true|false, "detail": ...}. It proves wiring that C# cannot see:

  optimizer_covers_policy        the optimizer holds exactly the parameters of the final policy (the SB3 action_net is replaced after SB3 built its optimizer)
  log_std                        shape, initial value and trainability of the Gaussian log standard deviation
  orthogonal_init                encoder / head / value layers follow the orthogonal initialisation with the documented gains, zero biases
  learning_signal                one PPO update on a fixed linear reward produces finite gradients for every part and changes the weights
  slot_order_equivariance        permuting the asset slots permutes the asset logits identically and leaves the cash logit unchanged
  masked_slot_isolation          the content of a masked slot (mask channel 0) changes no logit
  exported_actor_is_policy_mean  the exported actor equals the deterministic mean of the SB3 policy distribution

It does NOT compare numbers with Stable-Baselines3 internals (the pinned library is the implementation) and says nothing about training quality.
Usage: python rl_portfolio_policy_selfcheck.py --training-dir <StockAnalyzer.Python/training> --config <json with defaults, ppo_defaults, portfolio_defaults>
"""
import argparse
import json
import sys
from pathlib import Path

SLOTS = 3                 # asset slots of the tiny universe
ENVIRONMENT_STEPS = 16    # decisions per episode of the tiny environment
BATCH = 4                 # observations used by the structural checks
SEED = 7


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--training-dir", required=True)
    parser.add_argument("--config", required=True)
    args = parser.parse_args()
    sys.path.insert(0, str(Path(args.training_dir).resolve()))
    config = json.loads(Path(args.config).read_text(encoding="utf-8"))
    d, p, pf = config["defaults"], config["ppo_defaults"], config["portfolio_defaults"]

    import copy
    import numpy as np
    import torch
    import gymnasium as gym
    from stable_baselines3 import PPO
    import train_rl_portfolio as trainer

    torch.set_num_threads(d["CpuThreads"])
    torch.set_num_interop_threads(d["CpuThreads"])
    torch.use_deterministic_algorithms(True)
    torch.backends.cuda.matmul.allow_tf32 = False
    torch.backends.cudnn.allow_tf32 = False

    absolute, relative = float(d["AbsoluteTolerance"]), float(d["RelativeTolerance"])
    slot_length = d["WindowLength"] * d["MarketChannelCount"] + pf["AssetAuxiliaryChannelCount"]
    length = SLOTS * slot_length + pf["GlobalFeatureCount"]
    logit_count = SLOTS + 1
    results = []

    def record(name, ok, detail):
        results.append(dict(check=name, ok=bool(ok), detail=detail))
        print(json.dumps(results[-1], allow_nan=False), flush=True)

    SlotEncoder, PortfolioPolicy = trainer.build_policy_classes(torch, gym, np)

    class TinyEnvironment(gym.Env):
        """Seeded random observations (mask channel 1 on every slot) and a fixed linear reward on the squashed logits."""
        metadata = {"render_modes": []}

        def __init__(self):
            bound = np.finfo(np.float32).max
            self.action_space = gym.spaces.Box(-bound, bound, (logit_count,), np.float32)
            self.observation_space = gym.spaces.Box(-np.inf, np.inf, (length,), np.float32)
            self.rng = np.random.default_rng(SEED)
            self.target = self.rng.normal(size=logit_count)
            self.step_count = 0

        def observation(self):
            obs = self.rng.normal(size=length).astype(np.float32)
            for slot in range(SLOTS):
                obs[slot * slot_length + slot_length - 1] = 1.0
            return obs

        def reset(self, *, seed=None, options=None):
            super().reset(seed=seed)
            self.step_count = 0
            return self.observation(), {}

        def step(self, action):
            self.step_count += 1
            reward = float(np.dot(np.tanh(np.asarray(action, np.float64)), self.target))
            return self.observation(), reward, self.step_count >= ENVIRONMENT_STEPS, False, {}

    environment = TinyEnvironment()
    model = trainer.build_ppo(PPO, torch, PortfolioPolicy, SlotEncoder, environment, d, p, pf, SLOTS, slot_length, SEED)
    policy = model.policy
    before = {name: tensor.detach().clone() for name, tensor in policy.named_parameters()}

    # --- optimizer_covers_policy
    optimizer_ids = {id(parameter) for group in policy.optimizer.param_groups for parameter in group["params"]}
    policy_ids = {id(parameter) for parameter in policy.parameters()}
    head_ids = {id(parameter) for parameter in policy.action_net.parameters()}
    record("optimizer_covers_policy", optimizer_ids == policy_ids and head_ids <= optimizer_ids and type(policy.action_net).__name__ == "SlotLogitHead",
           dict(optimizer=len(optimizer_ids), policy=len(policy_ids), head=len(head_ids), action_net=type(policy.action_net).__name__))

    # --- log_std
    log_std = policy.log_std
    initial = float(p["LogStdInitial"])
    record("log_std", tuple(log_std.shape) == (logit_count,) and bool(torch.all(log_std == initial)) and log_std.requires_grad,
           dict(shape=list(log_std.shape), initial=initial, requires_grad=bool(log_std.requires_grad)))

    # --- orthogonal_init (rows < columns: W W^T = gain^2 I; a single row has norm = gain)
    def deviation(linear, gain):
        weight = linear.weight.detach().double()
        if weight.shape[0] <= weight.shape[1]:
            return float((weight @ weight.T - gain * gain * torch.eye(weight.shape[0], dtype=torch.float64)).abs().max())
        return float((weight.T @ weight - gain * gain * torch.eye(weight.shape[1], dtype=torch.float64)).abs().max())

    root2 = float(np.sqrt(2.0))
    actor_gain = float(p["ActorOutputGain"])
    encoder_linears = [m for m in policy.features_extractor.encoder if isinstance(m, torch.nn.Linear)]
    value_linears = [m for m in policy.mlp_extractor.value_net if isinstance(m, torch.nn.Linear)]
    head = policy.action_net
    checks = ([("encoder", m, root2) for m in encoder_linears] + [("slot_head_hidden", head.slot_head[0], root2), ("slot_head_out", head.slot_head[-1], actor_gain),
              ("cash_head_hidden", head.cash_head[0], root2), ("cash_head_out", head.cash_head[-1], actor_gain)]
              + [("value_hidden", m, root2) for m in value_linears] + [("value_out", policy.value_net, 1.0)])
    deviations = {f"{name}#{i}": deviation(module, gain) for i, (name, module, gain) in enumerate(checks)}
    biases = max(float(m.bias.detach().abs().max()) for _, m, _ in checks)
    record("orthogonal_init", max(deviations.values()) <= relative and biases == 0.0, dict(max_deviation=max(deviations.values()), max_abs_bias=biases,
           tolerance=relative, per_layer=deviations))

    # --- structural checks use the trainer's own exported actor
    actor = trainer.export_actor(torch, copy, policy)
    generator = np.random.default_rng(SEED + 1)

    def batch():
        obs = generator.normal(size=(BATCH, length)).astype(np.float32)
        for slot in range(SLOTS):
            obs[:, slot * slot_length + slot_length - 1] = 1.0
        return obs

    def logits_of(model_actor, obs):
        with torch.no_grad():
            return model_actor(torch.as_tensor(obs)).numpy()

    def permute(obs, order):
        slots = obs[:, :SLOTS * slot_length].reshape(BATCH, SLOTS, slot_length)[:, order]
        return np.concatenate([slots.reshape(BATCH, -1), obs[:, SLOTS * slot_length:]], axis=1)

    def equivariance_error(model_actor):
        obs, order = batch(), [2, 0, 1]
        base, moved = logits_of(model_actor, obs), logits_of(model_actor, permute(obs, order))
        cash = float(np.abs(moved[:, 0] - base[:, 0]).max())
        assets = float(np.abs(moved[:, 1:] - base[:, 1:][:, order]).max())
        return max(cash, assets)

    def masked_isolation_error(model_actor):
        obs = batch()
        other = obs.copy()
        slot = 1
        low, high = slot * slot_length, (slot + 1) * slot_length
        obs[:, high - 1] = 0.0; other[:, high - 1] = 0.0                  # mask channel 0 in both
        other[:, low:high - 1] = generator.normal(size=(BATCH, slot_length - 1)).astype(np.float32) * 5.0
        return float(np.abs(logits_of(model_actor, obs) - logits_of(model_actor, other)).max())

    # --- learning_signal: one rollout + update
    model.learn(total_timesteps=int(p["RolloutSteps"]), progress_bar=False)
    groups = dict(encoder=policy.features_extractor, slot_head=head.slot_head, cash_head=head.cash_head, value_net=policy.value_net,
                  value_hidden=policy.mlp_extractor.value_net)
    missing, nonfinite = [], []
    for name, module in groups.items():
        for parameter in module.parameters():
            if parameter.grad is None:
                missing.append(name)
            elif not bool(torch.isfinite(parameter.grad).all()):
                nonfinite.append(name)
    if policy.log_std.grad is None:
        missing.append("log_std")
    elif not bool(torch.isfinite(policy.log_std.grad).all()):
        nonfinite.append("log_std")
    unchanged = sorted({name.split(".")[0] + "." + name.split(".")[1] if "." in name else name
                        for name, tensor in policy.named_parameters() if torch.equal(tensor.detach(), before[name])})
    finite = all(bool(torch.isfinite(tensor).all()) for tensor in policy.parameters())
    record("learning_signal", not missing and not nonfinite and not unchanged and finite,
           dict(missing_gradients=sorted(set(missing)), nonfinite_gradients=sorted(set(nonfinite)), unchanged_parts=unchanged, all_parameters_finite=finite,
                timesteps=int(model.num_timesteps)))

    trained_actor = trainer.export_actor(torch, copy, policy)
    # --- slot_order_equivariance / masked_slot_isolation on the initial-like structure and after training
    equivariance = max(equivariance_error(actor), equivariance_error(trained_actor))
    record("slot_order_equivariance", equivariance <= absolute, dict(max_abs_difference=equivariance, tolerance=absolute, permutation=[2, 0, 1]))
    isolation = max(masked_isolation_error(actor), masked_isolation_error(trained_actor))
    record("masked_slot_isolation", isolation <= absolute, dict(max_abs_difference=isolation, tolerance=absolute, masked_slot=1))

    # --- exported_actor_is_policy_mean
    obs = batch()
    with torch.no_grad():
        mean = policy.get_distribution(torch.as_tensor(obs)).distribution.mean.numpy()
    exported = logits_of(trained_actor, obs)
    error = float(np.abs(mean - exported).max())
    record("exported_actor_is_policy_mean", error <= absolute and exported.shape == (BATCH, logit_count), dict(max_abs_difference=error, tolerance=absolute,
           shape=list(exported.shape)))

    # --- fine-tune start (trainer.apply_transfer / trainer.verify_frozen): a donor actor is written as the init file of a fresh policy
    import hashlib
    import tempfile
    names = config["transfer_defaults"]

    def fresh(seed):
        return trainer.build_ppo(PPO, torch, PortfolioPolicy, SlotEncoder, TinyEnvironment(), d, p, pf, SLOTS, slot_length, seed)

    def raises(action, message):
        try:
            action()
        except RuntimeError as error:
            return message in str(error)
        return False

    def equal_state(a, b):
        return list(a.keys()) == list(b.keys()) and all(torch.equal(a[key], b[key]) for key in a)

    with tempfile.TemporaryDirectory() as folder:
        donor = fresh(SEED + 10)
        donor_state = trainer.export_actor(torch, copy, donor.policy).state_dict()

        def job_for(file_name, state=None, freeze=None, digest=None):
            payload = donor_state if state is None else state
            torch.save(payload, Path(folder) / file_name)
            sha = hashlib.sha256((Path(folder) / file_name).read_bytes()).hexdigest()
            return dict(transfer=dict(init_weights=file_name, init_weights_sha256=digest or sha, freeze=freeze or names["NoFreeze"]), transfer_defaults=names)

        target = fresh(SEED + 11)
        critic_before = {k: v.detach().clone() for k, v in target.policy.named_parameters() if k.startswith(("value_net", "mlp_extractor.value_net", "log_std"))}
        record_before = trainer.export_actor(torch, copy, target.policy).state_dict()
        frozen = trainer.apply_transfer(torch, copy, target.policy, job_for("donor.pt"), folder)
        loaded = trainer.export_actor(torch, copy, target.policy).state_dict()
        critic_after = {k: v.detach() for k, v in target.policy.named_parameters() if k in critic_before}
        record("transfer_loads_exactly", frozen is None and equal_state(loaded, donor_state) and not equal_state(loaded, record_before)
               and all(torch.equal(critic_before[k], critic_after[k]) for k in critic_before) and bool(critic_before),
               dict(actor_equals_donor=equal_state(loaded, donor_state), untouched_parameters=sorted(critic_before)))

        tampered = job_for("tampered.pt")
        (Path(folder) / "tampered.pt").write_bytes((Path(folder) / "tampered.pt").read_bytes() + b"\0")
        wrong_name = job_for("donor.pt"); wrong_name["transfer"]["init_weights"] = "../donor.pt"
        record("transfer_rejects_a_changed_file", raises(lambda: trainer.apply_transfer(torch, copy, fresh(SEED + 12).policy, tampered, folder), "Transfer weights changed")
               and raises(lambda: trainer.apply_transfer(torch, copy, fresh(SEED + 12).policy, wrong_name, folder), "file of the run folder"), dict())

        missing = {k: v for k, v in donor_state.items() if k != next(iter(donor_state))}
        extra = dict(donor_state); extra["2.extra.weight"] = torch.zeros(1)
        reshaped = dict(donor_state); first_key = next(iter(donor_state)); reshaped[first_key] = torch.zeros(1)
        architecture = "do not match the actor architecture"
        record("transfer_rejects_a_wrong_key_set_or_shape",
               all(raises(lambda s=state, n=name: trainer.apply_transfer(torch, copy, fresh(SEED + 12).policy, job_for(n, s), folder), architecture)
                   for n, state in (("missing.pt", missing), ("extra.pt", extra), ("reshaped.pt", reshaped), ("notadict.pt", [1, 2, 3]))), dict())
        record("transfer_rejects_an_unknown_freeze", raises(lambda: trainer.apply_transfer(torch, copy, fresh(SEED + 12).policy,
               job_for("donor.pt", freeze="heads"), folder), "Unknown transfer freeze"), dict())

        pinned = fresh(SEED + 13)
        snapshot = trainer.apply_transfer(torch, copy, pinned.policy, job_for("donor.pt", freeze=names["EncoderFrozen"]), folder)
        heads_before = {k: v.detach().clone() for k, v in pinned.policy.action_net.named_parameters()}
        pinned.learn(total_timesteps=int(p["RolloutSteps"]), progress_bar=False)
        trainer.verify_frozen(torch, pinned.policy, snapshot)
        encoder_is_donor = all(torch.equal(tensor, donor.policy.features_extractor.state_dict()[key]) for key, tensor in pinned.policy.features_extractor.state_dict().items())
        heads_moved = any(not torch.equal(v.detach(), heads_before[k]) for k, v in pinned.policy.action_net.named_parameters())
        record("frozen_encoder_stays_and_heads_train", snapshot is not None and encoder_is_donor and heads_moved
               and all(not q.requires_grad for q in pinned.policy.features_extractor.parameters()), dict(heads_moved=heads_moved, encoder_is_donor=encoder_is_donor))
        next(iter(pinned.policy.features_extractor.parameters())).data.add_(1.0)
        record("frozen_encoder_change_is_detected", raises(lambda: trainer.verify_frozen(torch, pinned.policy, snapshot), "Frozen encoder changed"), dict())

        free = fresh(SEED + 14)
        trainer.apply_transfer(torch, copy, free.policy, job_for("donor.pt"), folder)
        free.learn(total_timesteps=int(p["RolloutSteps"]), progress_bar=False)
        record("unfrozen_encoder_trains", not all(torch.equal(tensor, donor.policy.features_extractor.state_dict()[key])
               for key, tensor in free.policy.features_extractor.state_dict().items()), dict())

    sys.exit(0 if all(r["ok"] for r in results) else 1)


if __name__ == "__main__":
    main()
