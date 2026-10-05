"""Test-owned startup injection, copied to an isolated PYTHONPATH by opt-in qualification.

No application fault setting, installed file edit or training-script edit is required.
"""
from pathlib import Path
import os
import sys

if Path(sys.argv[0]).name == "train_rl_ppo.py":
    sys.path.insert(0, str(Path(sys.argv[0]).resolve().parent))
    import torch
    from stable_baselines3.common.policies import ActorCriticPolicy

    case = os.environ["SA_RL_R02_TEST_FAULT"]
    output = Path(os.environ["SA_RL_R02_TEST_FAULT_OUTPUT"])
    original_step = torch.optim.Adam.step
    def watched_step(self, *args, **kwargs):
        with (output / "optimizer_step_called.txt").open("a", encoding="utf-8") as stream:
            stream.write("step\n")
        return original_step(self, *args, **kwargs)
    torch.optim.Adam.step = watched_step
    if case == "loss":
        torch.nn.functional.mse_loss = lambda *args, **kwargs: torch.tensor(float("nan"), requires_grad=True)
    elif case == "ratio":
        evaluate = ActorCriticPolicy.evaluate_actions
        def overflow_ratio(self, obs, actions):
            values, log_prob, entropy = evaluate(self, obs, actions)
            return values, log_prob + 1000., entropy
        ActorCriticPolicy.evaluate_actions = overflow_ratio
    elif case in ("log_std", "gradient"):
        initialize = ActorCriticPolicy.__init__
        def injected_initialize(self, *args, **kwargs):
            initialize(self, *args, **kwargs)
            if case == "log_std":
                with torch.no_grad():
                    self.log_std.fill_(-1000.)
            else:
                next(self.parameters()).register_hook(lambda grad: torch.full_like(grad, float("inf")))
        ActorCriticPolicy.__init__ = injected_initialize
    else:
        raise RuntimeError("Unknown test-owned numerical fault")
    (output / "injection_started.txt").write_text(case, encoding="utf-8")
