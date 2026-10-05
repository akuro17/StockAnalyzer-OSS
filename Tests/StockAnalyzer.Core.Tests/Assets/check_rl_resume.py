"""Compare real owned T16 continuation evidence, excluding wall-clock/identity metadata."""
import json
from pathlib import Path
import sys
import torch

def equal(left, right, path):
    if isinstance(left, torch.Tensor):
        assert isinstance(right, torch.Tensor) and left.dtype == right.dtype and left.shape == right.shape and torch.equal(left, right), path
    elif isinstance(left, dict):
        assert isinstance(right, dict) and left.keys() == right.keys(), path
        for key in left:
            equal(left[key], right[key], path + "." + str(key))
    elif isinstance(left, (list, tuple)):
        assert len(left) == len(right), path
        for index, (a, b) in enumerate(zip(left, right)):
            equal(a, b, path + "." + str(index))
    else:
        assert left == right, path

left_root, right_root = map(Path, sys.argv[1:])
left, right = (torch.load(root / "python.pt", map_location="cpu", weights_only=True) for root in (left_root, right_root))
for field in ("policy", "optimizer", "rng", "model"):
    equal(left[field], right[field], field)
equal(left["state"]["winner"], right["state"]["winner"], "winner")
for field in ("phase", "seed_index", "selected", "oos_index", "parity_index", "winner_key", "observations", "paired"):
    equal(left["state"][field], right["state"][field], "phase_state." + field)
core_a, core_b = (json.loads((root / "core.json").read_text(encoding="utf-8-sig")) for root in (left_root, right_root))
assert len(core_a["updates"]) == len(core_b["updates"]), "update_count"
for a, b in zip(core_a["updates"], core_b["updates"]):
    for field in ("seed", "checkpoint_step", "minibatches", "optimizer_steps", "early_stopped", "metrics"):
        equal(a[field], b[field], "updates." + field)
equal(core_a["actions"], core_b["actions"], "consumed_actions")
equal(core_a["last_account"], core_b["last_account"], "decimal_last_account")
equal(core_a["reports"], core_b["reports"], "committed_episode_reports")
equal(core_a["information"], core_b["information"], "information_support")
print(json.dumps(dict(policy=True, optimizer=True, rng=True, sb3_progress=True, winner=True,
                      loss_diagnostics=True, consumed_action_counts=True, decimal_account=True,
                      phase_cursors=True, episode_reports=True, information_support=True)))
