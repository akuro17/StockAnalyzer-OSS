"""Fail-closed checks around reviewed SB3 arithmetic; no numerical repair or alternative update."""
from __future__ import annotations

import hashlib
import inspect
from pathlib import Path
import textwrap

import numpy as np
import torch


# Reviewed SB3 2.7.0 sources. Source drift requires review, never a best-effort patch.
UPSTREAM = {
    "ppo_train_sha256": "961ff1a5752c6032719aa7272c3412cf23f784be6cd33b998451cabb7a4475c3",
    "ppo_collector_sha256": "0c49643252af746c92d34cc34216652690b8fccb99e535f75155ba66dc8b6e43",
    "ppo_gae_sha256": "2743905c5f1d234691a73f6e773b1634b4746159ca06c56b09420d811c9d9fe2",
}


class NumericalFailure(RuntimeError):
    pass


def finite(name, value):
    valid = torch.isfinite(value).all().item() if isinstance(value, torch.Tensor) else np.isfinite(value).all()
    if not valid:
        raise NumericalFailure("PPO nonfinite: " + name)


def tree(name, value):
    if isinstance(value, dict):
        for index, (key, child) in enumerate(value.items()):
            # Adam keys are Parameter tensors. Their repr formats weights and can dominate training.
            label = "parameter_state[" + str(index) + "]" if isinstance(key, torch.Tensor) else str(key)
            tree(name + "." + label, child)
    elif isinstance(value, (tuple, list)):
        for index, child in enumerate(value):
            tree(name + "." + str(index), child)
    elif isinstance(value, (torch.Tensor, np.ndarray, int, float)):
        finite(name, value)


def policy_state(policy):
    for name, parameter in policy.named_parameters():
        finite("parameter." + name, parameter)
    finite("log_std", policy.log_std)
    std = torch.exp(policy.log_std)
    finite("exp(log_std)", std)
    if not (std > 0).all().item():
        raise NumericalFailure("PPO exp(log_std) must be strictly positive")
    tree("optimizer", policy.optimizer.state)
    tree("optimizer_groups", [{k: v for k, v in group.items() if k != "params"}
                              for group in policy.optimizer.param_groups])


def gradients(policy):
    for name, parameter in policy.named_parameters():
        if parameter.grad is not None:
            finite("gradient." + name, parameter.grad)


def rollout_sample(observations, actions, values, log_probs):
    for name, value in (("observations", observations), ("raw_actions", actions),
                        ("values", values), ("log_prob", log_probs)):
        finite(name, value)


def rollout_buffer(buffer):
    for name in ("observations", "actions", "rewards", "values", "log_probs", "advantages", "returns"):
        finite("rollout." + name, getattr(buffer, name))


def minibatch(data):
    for name in data._fields:
        finite("minibatch." + name, getattr(data, name))


class UpdateSummary:
    """Constant memory; one small summary per rollout, no minibatch history."""
    def __init__(self):
        self.metrics = {}
        self.optimizer_steps = 0
        self.minibatches = 0
        self.early_stopped = False

    def observe(self, **values):
        for name, value in values.items():
            finite(name, value)
            if isinstance(value, torch.Tensor):
                value = value.detach().cpu().numpy()
            array = np.asarray(value).reshape(-1)
            row = self.metrics.setdefault(name, dict(count=0, minimum=None, maximum=None, mean=0.))
            for item in array:
                scalar = float(item)
                row["count"] += 1
                row["minimum"] = scalar if row["minimum"] is None else min(row["minimum"], scalar)
                row["maximum"] = scalar if row["maximum"] is None else max(row["maximum"], scalar)
                # Diagnostic accumulation only; never feeds PPO or financial calculations.
                row["mean"] += (scalar - row["mean"]) / row["count"]
                finite("summary." + name, row["mean"])

    def before_backward(self, policy, policy_loss, value_loss, entropy_loss, loss, ratio, kl, log_prob, entropy):
        policy_state(policy)
        for name, value in (("ratio", ratio), ("log_prob", log_prob), ("entropy", entropy)):
            finite(name, value)
        self.observe(policy_loss=policy_loss, value_loss=value_loss, entropy_loss=entropy_loss,
                     entropy=entropy.mean(), total_loss=loss, approximate_kl=kl, log_std=policy.log_std)
        self.minibatches += 1

    def payload(self):
        return dict(minibatches=self.minibatches, optimizer_steps=self.optimizer_steps,
                    early_stopped=self.early_stopped, metrics=self.metrics)


def _source(function, key):
    source = textwrap.dedent(inspect.getsource(function))
    if hashlib.sha256(source.encode("utf-8")).hexdigest() != UPSTREAM[key]:
        raise RuntimeError("Unreviewed pinned SB3 source: " + key)
    return source


def identities():
    from stable_baselines3.ppo.ppo import PPO
    from stable_baselines3.common.on_policy_algorithm import OnPolicyAlgorithm
    from stable_baselines3.common.buffers import RolloutBuffer
    for function, key in ((PPO.train, "ppo_train_sha256"),
                          (OnPolicyAlgorithm.collect_rollouts, "ppo_collector_sha256"),
                          (RolloutBuffer.compute_returns_and_advantage, "ppo_gae_sha256")):
        _source(function, key)
    return dict(UPSTREAM, ppo_guard_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest())


def _patch(source, anchor, replacement):
    if source.count(anchor) != 1:
        raise RuntimeError("Incompatible pinned PPO guard insertion: " + anchor)
    return source.replace(anchor, replacement)


def adapted_functions():
    from stable_baselines3.ppo import ppo
    from stable_baselines3.common import on_policy_algorithm
    identities()
    collector = _source(on_policy_algorithm.OnPolicyAlgorithm.collect_rollouts, "ppo_collector_sha256")
    collector = _patch(collector, "clipped_actions = np.clip(actions, self.action_space.low, self.action_space.high)",
                       "clipped_actions = actions")
    collector = _patch(collector, "            actions, values, log_probs = self.policy(obs_tensor)",
                       "            _guard.policy_state(self.policy)\n            _guard.finite('observations', obs_tensor)\n"
                       "            actions, values, log_probs = self.policy(obs_tensor)\n"
                       "            _guard.rollout_sample(obs_tensor, actions, values, log_probs)")
    collector = _patch(collector, "        new_obs, rewards, dones, infos = env.step(clipped_actions)",
                       "        new_obs, rewards, dones, infos = env.step(clipped_actions)\n"
                       "        _guard.finite('new_observations', new_obs)\n        _guard.finite('rewards', rewards)")
    collector = _patch(collector, "rollout_buffer.compute_returns_and_advantage(last_values=values, dones=dones)",
                       "_guard.finite('bootstrap_values', values)\n"
                       "    rollout_buffer.compute_returns_and_advantage(last_values=values, dones=dones)\n"
                       "    _guard.rollout_buffer(rollout_buffer)")
    train = _source(ppo.PPO.train, "ppo_train_sha256")
    train = _patch(train, "self.policy.set_training_mode(True)",
                   "_guard.policy_state(self.policy)\n    _guard.rollout_buffer(self.rollout_buffer)\n"
                   "    self.guard_summary = _guard.UpdateSummary()\n    self.policy.set_training_mode(True)")
    train = _patch(train, "            actions = rollout_data.actions\n", "            _guard.minibatch(rollout_data)\n"
                   "            _guard.policy_state(self.policy)\n            actions = rollout_data.actions\n")
    train = _patch(train, "            values = values.flatten()", "            _guard.rollout_sample(rollout_data.observations, actions, values, log_prob)\n"
                   "            _guard.finite('entropy', entropy)\n            values = values.flatten()")
    train = _patch(train, "            ratio = th.exp(log_prob - rollout_data.old_log_prob)",
                   "            _guard.finite('normalized_advantages', advantages)\n"
                   "            ratio = th.exp(log_prob - rollout_data.old_log_prob)\n            _guard.finite('ratio', ratio)")
    train = _patch(train, "                advantages = (advantages - advantages.mean()) / (advantages.std() + 1e-8)",
                   "                _guard.finite('advantage_mean', advantages.mean())\n"
                   "                _guard.finite('advantage_sample_std', advantages.std())\n"
                   "                advantages = (advantages - advantages.mean()) / (advantages.std() + 1e-8)")
    train = _patch(train, "            if self.target_kl is not None and approx_kl_div > 1.5 * self.target_kl:",
                   "            self.guard_summary.before_backward(self.policy, policy_loss, value_loss, entropy_loss, loss, "
                   "ratio, approx_kl_div, log_prob, entropy)\n"
                   "            if self.target_kl is not None and approx_kl_div > 1.5 * self.target_kl:\n"
                   "                self.guard_summary.early_stopped = True")
    train = _patch(train, "            th.nn.utils.clip_grad_norm_(self.policy.parameters(), self.max_grad_norm)",
                   "            _guard.gradients(self.policy)\n"
                   "            grad_norm = th.nn.utils.clip_grad_norm_(self.policy.parameters(), self.max_grad_norm)\n"
                   "            self.guard_summary.observe(gradient_norm=grad_norm)\n            _guard.gradients(self.policy)")
    train = _patch(train, "            self.policy.optimizer.step()", "            self.policy.optimizer.step()\n"
                   "            _guard.policy_state(self.policy)\n            self.guard_summary.optimizer_steps += 1")
    import sys
    functions = []
    for source, module, name in ((collector, on_policy_algorithm, "collect_rollouts"), (train, ppo, "train")):
        namespace = dict(vars(module), _guard=sys.modules[__name__])
        exec(compile(source, "<repo R02 pinned PPO guard>", "exec"), namespace)
        functions.append(namespace[name])
    return tuple(functions)
