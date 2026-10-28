"""Single Source of Truth (SSoT) for reserved trainer CLI flags across training orchestrators."""

from __future__ import annotations
import shlex
from typing import List

# Flags emitted authoritatively by run_training.py.
# A hyperparameter reusing one of these would silently override wizard controls or output paths
# under argparse's "last flag wins" rule, so it is rejected. "epochs" is deliberately absent:
# run_training.py forwards it as a hyperparameter (it is a wizard hyperparameter key).
RESERVED_TRAINER_FLAGS: frozenset = frozenset({
    "data-dir", "feature-mode", "feature-spec", "target-type", "window", "horizon", "timeframe", "wf-splits",
    "gap", "out", "arch", "indicator-channel-export-paths", "outer-fold-index",
    "max-symbols", "smoke",
    "fixed-zscore", "clip-sigma", "lags",
    "initialization-mode", "init-from", "checkpoint-dir", "run-id",
    "parent-model-id", "parent-model-hash", "freeze-paths",
    "preflight",
    "pretrain", "bag-index", "bag-count", "bag-seed", "bag-fraction", "bag-block-length", "meta-segment",
    "cpcv-groups", "cpcv-test-groups", "cpcv-combination",
})

# sweep.py emits these two itself (--epochs from its own option, --no-verify always), so an --extra token must not repeat them.
SWEEP_RESERVED_TRAINER_FLAGS: frozenset = RESERVED_TRAINER_FLAGS | frozenset({"epochs", "no-verify"})


def normalize_flag(key: object) -> str:
    """Turn a hyperparameter key or CLI token into the normalized flag name it collides with.

    Shared by the reserved-flag conflict check and argv emission so the two stay in
    lockstep by construction instead of by two independently maintained copies of the rule.
    """
    token = str(key).strip().lstrip("-")
    if "=" in token:
        token = token.split("=")[0]
    return token.replace("_", "-")


def validate_extra_flags(extra_str: str) -> List[str]:
    """Parse and validate sweep --extra arguments, rejecting any flag sweep.py reserves."""
    tokens = shlex.split(extra_str)
    for token in tokens:
        if token.startswith("-"):
            normalized = normalize_flag(token)
            if normalized in SWEEP_RESERVED_TRAINER_FLAGS:
                raise ValueError(f"Reserved trainer flag '{token}' cannot be overridden via --extra")
    return tokens
