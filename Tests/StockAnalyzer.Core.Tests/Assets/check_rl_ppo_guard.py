"""Actual pinned PPO updates, failure boundaries and unchanged upstream equations."""
import copy
from pathlib import Path
import sys
from unittest.mock import patch

import gymnasium as gym
import numpy as np
import torch
from stable_baselines3 import PPO
from stable_baselines3.common.buffers import RolloutBuffer
from stable_baselines3.common.logger import configure

sys.path.insert(0, str(Path(__file__).resolve().parents[3] / "StockAnalyzer.Python" / "training"))
import rl_ppo_guard as guard

torch.set_num_threads(1)
torch.use_deterministic_algorithms(True)
collector, guarded_train = guard.adapted_functions()


class FixtureEnvironment(gym.Env):
    observation_space = gym.spaces.Box(-np.inf, np.inf, (4,), np.float32)
    action_space = gym.spaces.Box(-1., 1., (2,), np.float32)
    def reset(self, *, seed=None, options=None):
        super().reset(seed=seed)
        self.steps = 0
        return np.zeros(4, np.float32), {}
    def step(self, action):
        self.steps += 1
        return np.ones(4, np.float32), 0., False, False, {}


def model(constant=False):
    result = PPO("MlpPolicy", FixtureEnvironment(), seed=42, n_steps=4, batch_size=4, n_epochs=2,
                 ent_coef=.01, vf_coef=.5, target_kl=.02, device="cpu", verbose=0)
    result.set_logger(configure(folder=None, format_strings=[]))
    buffer = result.rollout_buffer
    for i in range(4):
        buffer.add(np.full((1, 4), i / 4., np.float32), np.array([[.125, -.25]], np.float32),
                   np.array([.01], np.float32), np.array([i == 0]), torch.tensor([.2]), torch.tensor([-1.]))
    buffer.advantages[:] = 1. if constant else np.array([-.2, .1, .3, .4], np.float32).reshape(4, 1)
    buffer.returns[:] = .3
    # Use true original likelihood, so first ratio is 1, and target KL does not mask updates.
    with torch.no_grad():
        _, log_prob, _ = result.policy.evaluate_actions(torch.tensor(buffer.observations[:, 0]), torch.tensor(buffer.actions[:, 0]))
    buffer.log_probs[:] = log_prob.numpy().reshape(4, 1)
    result._current_progress_remaining = 1.
    return result


def same_tree(a, b):
    if isinstance(a, torch.Tensor):
        assert torch.equal(a, b)
    elif isinstance(a, np.ndarray):
        assert np.array_equal(a, b)
    elif isinstance(a, dict):
        assert a.keys() == b.keys()
        for key in a:
            same_tree(a[key], b[key])
    elif isinstance(a, (tuple, list)):
        assert len(a) == len(b)
        for x, y in zip(a, b):
            same_tree(x, y)
    else:
        assert a == b


for constant in (False, True):
    original, guarded = model(constant), model(constant)
    np.random.seed(123)
    PPO.train(original)
    rng = np.random.get_state()
    np.random.seed(123)
    guarded_train(guarded)
    same_tree(original.policy.state_dict(), guarded.policy.state_dict())
    same_tree(original.policy.optimizer.state_dict(), guarded.policy.optimizer.state_dict())
    same_tree(rng, np.random.get_state())
    for key in ("train/loss", "train/policy_gradient_loss", "train/value_loss", "train/entropy_loss", "train/approx_kl"):
        assert original.logger.name_to_value[key] == guarded.logger.name_to_value[key]
    assert guarded.guard_summary.optimizer_steps == 2


def rejected(case):
    candidate = model()
    before = copy.deepcopy(candidate.policy.state_dict())
    calls = dict(step=0, backward=0)
    real_step = candidate.policy.optimizer.step
    def step(*args, **kwargs):
        calls["step"] += 1
        return real_step(*args, **kwargs)
    candidate.policy.optimizer.step = step
    real_backward = torch.Tensor.backward
    def backward(self, *args, **kwargs):
        calls["backward"] += 1
        return real_backward(self, *args, **kwargs)
    handle = None
    if case.startswith("log_std"):
        with torch.no_grad():
            candidate.policy.log_std.fill_({"log_std_nan": float("nan"), "log_std_overflow": 1000., "log_std_underflow": -1000.}[case])
    if case in ("ratio", "kl"):
        evaluate = candidate.policy.evaluate_actions
        def extreme(obs, actions):
            values, log_prob, entropy = evaluate(obs, actions)
            return values, log_prob + (1000. if case == "ratio" else 88.), entropy
        candidate.policy.evaluate_actions = extreme
    if case == "gradient":
        handle = next(candidate.policy.parameters()).register_hook(lambda grad: torch.full_like(grad, float("inf")))
    if case == "optimizer":
        parameter = next(candidate.policy.parameters())
        candidate.policy.optimizer.state[parameter]["fixture"] = torch.tensor(float("nan"))
    loss_patch = patch("torch.nn.functional.mse_loss", return_value=torch.tensor(float("nan"), requires_grad=True))
    from contextlib import nullcontext
    try:
        with patch.object(torch.Tensor, "backward", backward), loss_patch if case == "loss" else nullcontext():
            guarded_train(candidate)
    except guard.NumericalFailure:
        pass
    else:
        raise AssertionError("Injected " + case + " was accepted")
    assert calls["step"] == 0, case
    assert calls["backward"] == (1 if case == "gradient" else 0), case
    if not case.startswith("log_std"):
        same_tree(before, candidate.policy.state_dict())
    if handle is not None:
        handle.remove()


for case in ("loss", "ratio", "kl", "log_std_nan", "log_std_overflow", "log_std_underflow", "gradient", "optimizer"):
    rejected(case)

# An optimizer that corrupts output is detected immediately, before qualification/export.
for corrupt in ("parameter", "state"):
    candidate = model()
    actual = candidate.policy.optimizer.step
    def corrupted_step():
        actual()
        parameter = next(candidate.policy.parameters())
        if corrupt == "parameter":
            with torch.no_grad():
                parameter.flatten()[0] = float("nan")
        else:
            candidate.policy.optimizer.state[parameter]["exp_avg"].flatten()[0] = float("inf")
    candidate.policy.optimizer.step = corrupted_step
    try:
        guarded_train(candidate)
    except guard.NumericalFailure:
        assert candidate.guard_summary.optimizer_steps == 0
    else:
        raise AssertionError("Corrupt optimizer output accepted")

# Extreme finite input is accepted at the boundary; overflow of its arithmetic fails closed.
guard.finite("extreme finite", torch.tensor(torch.finfo(torch.float32).max))
candidate = model()
candidate.rollout_buffer.returns[:] = np.finfo(np.float32).max
with patch.object(candidate.policy.optimizer, "step") as optimizer_step:
    try:
        guarded_train(candidate)
    except guard.NumericalFailure:
        assert optimizer_step.call_count == 0
    else:
        raise AssertionError("Overflowed finite target accepted")

# Pinned GAE hand fixture, including final bootstrap, no new GAE implementation.
buffer = RolloutBuffer(3, FixtureEnvironment.observation_space, FixtureEnvironment.action_space, gamma=.99, gae_lambda=.95)
buffer.rewards[:] = np.array([1., 2., 3.]).reshape(3, 1)
buffer.values[:] = np.array([.1, .2, .3]).reshape(3, 1)
buffer.episode_starts[:] = np.array([1., 0., 0.]).reshape(3, 1)
buffer.compute_returns_and_advantage(torch.tensor([.4]), np.array([False]))
np.testing.assert_allclose(buffer.advantages[:, 0], [5.808765114, 5.008788, 3.096], rtol=1e-6)
np.testing.assert_allclose(buffer.returns[:, 0], [5.908765114, 5.208788, 3.396], rtol=1e-6)
guard.rollout_buffer(buffer)
assert torch.tensor([1., 3.]).std().item() == np.float32(np.sqrt(2.))
entropy_coefficient = .01
loss_low_entropy = 2. + entropy_coefficient * -3. + .5 * 4.
loss_high_entropy = 2. + entropy_coefficient * -3.001 + .5 * 4.
assert loss_high_entropy < loss_low_entropy

# Actual upstream normalization: a nonuniform ratio exposes sample vs population std.
candidate = model()
candidate.n_epochs = 1
evaluate = candidate.policy.evaluate_actions
def prescribed_ratio(obs, actions):
    values, log_prob, entropy = evaluate(obs, actions)
    # Ratio attached to observation identity, independent of the pinned random minibatch permutation.
    multipliers = torch.tensor([.8, 1.1, 1.2, 1.])[torch.round(obs[:, 0] * 4).long()]
    return values, log_prob + multipliers.log(), entropy
candidate.policy.evaluate_actions = prescribed_ratio
guarded_train(candidate)
advantages = torch.tensor([-.2, .1, .3, .4])
normalized = (advantages - advantages.mean()) / (advantages.std() + 1e-8)
ratios = torch.tensor([.8, 1.1, 1.2, 1.])
expected_policy_loss = -torch.minimum(normalized * ratios, normalized * torch.clamp(ratios, .8, 1.2)).mean()
assert abs(candidate.guard_summary.metrics["policy_loss"]["mean"] - expected_policy_loss.item()) < 1e-6
population = (advantages - advantages.mean()) / (advantages.std(correction=0) + 1e-8)
assert abs(expected_policy_loss.item() - (-torch.minimum(population * ratios, population * torch.clamp(ratios, .8, 1.2)).mean()).item()) > 1e-4

# Finite difference through the actual total-loss expression, with all other terms fixed.
losses = []
for entropy_offset in (0., .001):
    candidate = model()
    candidate.n_epochs = 1
    evaluate = candidate.policy.evaluate_actions
    def shifted_entropy(obs, actions):
        values, log_prob, entropy = evaluate(obs, actions)
        return values, log_prob, entropy + entropy_offset
    candidate.policy.evaluate_actions = shifted_entropy
    np.random.seed(123)
    guarded_train(candidate)
    losses.append(candidate.guard_summary.metrics["total_loss"]["mean"])
assert losses[1] < losses[0]
assert abs((losses[1] - losses[0]) / .001 + candidate.ent_coef) < 1e-5

# Collector gates values/actions/log-probability before any environment mutation.
from types import MethodType
for fault in ("raw_actions", "values", "log_prob", "bootstrap", "gae"):
    candidate = model()
    candidate.collect_rollouts = MethodType(collector, candidate)
    candidate.train = MethodType(guarded_train, candidate)
    if fault in ("raw_actions", "values", "log_prob"):
        original_forward = candidate.policy.forward
        def invalid_forward(obs, deterministic=False):
            actions, values, log_prob = original_forward(obs, deterministic)
            if fault == "raw_actions": actions = torch.full_like(actions, float("inf"))
            if fault == "values": values = torch.full_like(values, float("nan"))
            if fault == "log_prob": log_prob = torch.full_like(log_prob, float("nan"))
            return actions, values, log_prob
        candidate.policy.forward = invalid_forward
    elif fault == "bootstrap":
        candidate.policy.predict_values = lambda obs: torch.tensor([[float("nan")]])
    else:
        original_gae = candidate.rollout_buffer.compute_returns_and_advantage
        def invalid_gae(*args, **kwargs):
            original_gae(*args, **kwargs)
            candidate.rollout_buffer.advantages[0] = float("inf")
        candidate.rollout_buffer.compute_returns_and_advantage = invalid_gae
    with patch.object(candidate.policy.optimizer, "step") as step:
        try:
            candidate.learn(total_timesteps=4)
        except guard.NumericalFailure:
            assert step.call_count == 0
            expected_steps = 0 if fault in ("raw_actions", "values", "log_prob") else 4
            assert candidate.env.envs[0].unwrapped.steps == expected_steps
        else:
            raise AssertionError("Invalid collector " + fault + " accepted")

# Drift is a hard error and leaves installed sources alone.
with patch.dict(guard.UPSTREAM, {"ppo_train_sha256": "0" * 64}):
    try:
        guard.adapted_functions()
    except RuntimeError:
        pass
    else:
        raise AssertionError("Unreviewed source accepted")
print("R02 PPO guard equivalence, injection, GAE and entropy: PASS")
