"""T23 meta-learning (Reptile / MAML): learn an initialization that adapts to a NEW symbol from a few samples.

Approved decisions (2026-10-11, ``Y:\\Temp\\sa_decision_options_onnx_meta_attention_gnn_t23_t25.md`` star rows C1..C5, M1..M9, S1..S8):

* M1 A ``--algorithm {reptile,maml}``, default reptile. M2 A MAML is first order unless ``--second-order`` (supported on CPU: the probe of this
  task differentiated a 2-layer ``nn.LSTM`` twice); Reptile has no second derivative at all. First-order MAML (FOMAML) approximates the Jacobian
  dphi/dtheta = I - alpha*H of the inner update by the identity; the exact Jacobian is used only with --second-order.
* M3 A one symbol = one task. M4 A inside a task the samples are chronological: a support block, then a purge gap of
  ``dataset.resolve_purge_gap`` bars (so the information intervals of support and query are disjoint), then a query block. Meta-test symbols are disjoint
  from the meta-train symbols. The out-of-sample tail (``--oos-tail-days`` >= 1, mandatory) is never read; synthetic data is refused (exit 3).
* M5 A a task with fewer than ``--min-task-samples`` samples, or one that cannot form a support+gap+query episode, is skipped and listed with its reason;
  no valid task fails the run (nothing is published); a single task trains but the report says ``single_task``.
* M6 A hyperparameters are job options with named defaults that have NO empirical basis (``thresholds_basis``), validated to their mathematical range and
  recorded with the artifact. M7 A standalone tool: ``train`` publishes the meta-initialization, ``adapt`` adapts it to one new symbol (optionally exporting a
  normal single-output ONNX). It does not touch the FineTune path, the model registry or any other trainer. M8 A the report compares three conditions on the
  query block (meta-init unadapted, meta-init adapted, random-init adapted with the same steps); there is no pass/fail gate and no advice. M9 A the model is
  ``train_pytorch.build_model`` (lstm / cnn), 3-class labels as ``dataset.make_label``, per-window feature modes only (no fitted scaler).
* S5/S6 the run is a derived artifact under ``<DataRoot>/TrainingArtifacts/Meta/<runId>`` (``MetaAdapt`` for ``adapt``), built as ``<runId>.incoming`` and renamed
  once; an existing folder is never reused. Exit codes: 0 ok, 1 run failure (timeout, non-finite value), 2 invalid argument, 3 prerequisite (data, synthetic data,
  independence, unsupported timeframe, existing output). S7 sampling uses ``numpy.random.SeedSequence(seed, spawn_key=...)`` per task and slot
  (``rng_policy = per_task_seed_sequence``), so a draw never depends on the other tasks.

* Web-AI review adoption (2026-10-11, DEC-7 / DEC-8 A): ``--eval-episodes E`` scores a held-out symbol on E chronological support+query episodes (default 1 = the
  single episode of before); ``--inner-class-weights inverse_frequency`` weights the INNER (support) loss by 1/N_c of the episode's support labels (default none);
  ``--task-sampling without_replacement`` draws the tasks of a meta-batch without replacement when enough tasks exist (default with replacement). The meta-train
  episodes may postdate the meta-test period of a held-out symbol: the run says so in its notices. With the defaults nothing changes (same weights, same document).

Nothing here changes a prediction path. The result is a descriptive comparison, not a statement about real-market usefulness and not advice.

Usage::

    python train_meta.py train --symbols AAA,BBB,CCC --meta-test-symbols DDD --oos-tail-days 60
    python train_meta.py adapt --meta-run <DataRoot>/TrainingArtifacts/Meta/<runId> --symbol EEE --export-onnx
    python train_meta.py --selfcheck
"""

from __future__ import annotations

import argparse
import copy
import datetime
import hashlib
import json
import math
import shutil
import statistics
import sys
import tempfile
import time
import zlib
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Dict, List, Sequence, Tuple

import numpy as np
import torch
from torch import nn
from torch.func import functional_call

sys.path.insert(0, str(Path(__file__).resolve().parent))
import checkpoints  # noqa: E402
import dataset as ds  # noqa: E402
import explain  # noqa: E402  (make_header)
import metrics  # noqa: E402
import model_analysis  # noqa: E402  (channel_names)
import onnx_meta  # noqa: E402
import train_pytorch as tp  # noqa: E402  (build_model, export_onnx, verify_onnx)

SCHEMA_VERSION: int = 1
TOOL: str = "train_meta.py"
TOOL_VERSION: str = "1"
KIND_TRAIN: str = "Meta"
KIND_ADAPT: str = "MetaAdapt"
INCOMING_SUFFIX: str = ".incoming"
META_INIT_NAME: str = "meta_init.pt"
META_DOC_NAME: str = "meta.json"
ADAPT_DOC_NAME: str = "adapt.json"
ADAPTED_ONNX_NAME: str = "adapted.onnx"
ALGORITHM_REPTILE, ALGORITHM_MAML = "reptile", "maml"
ALGORITHMS: Tuple[str, str] = (ALGORITHM_REPTILE, ALGORITHM_MAML)
RNG_POLICY: str = "per_task_seed_sequence"
THRESHOLDS_BASIS_DEFAULT: str = "planning_default_uncalibrated"
THRESHOLDS_BASIS_USER: str = "user_supplied"
STATUS_AVAILABLE, STATUS_INSUFFICIENT = "available", "insufficient_data"
SUPPORTED_TIMEFRAME: str = onnx_meta.DEFAULT_TIMEFRAME
NUM_CLASSES: int = len(ds.CLASS_LABELS)
ELAPSED_DIGITS: int = 3
CURVE_POINTS: int = 50                      # query-loss curve samples kept in the document
OPSET_DEFAULT: int = tp.DEFAULT_OPSET
EXIT_OK, EXIT_FAILED, EXIT_PREREQUISITE = 0, 1, 3
# SeedSequence stream numbers (spawn_key[0]); a stream never shares a draw with another.
STREAM_TASK_CHOICE, STREAM_EPISODE, STREAM_TORCH_EPISODE, STREAM_RANDOM_INIT = 0, 1, 2, 3

# Layer 1 (job hyperparameters, planning values without an empirical basis; overridable on the command line).
DEFAULT_INNER_STEPS: int = 5
DEFAULT_INNER_LR: float = 0.01
DEFAULT_META_LR: float = 0.001              # MAML outer Adam
DEFAULT_REPTILE_EPSILON: float = 0.1
DEFAULT_META_BATCH_TASKS: int = 4
DEFAULT_SUPPORT_SIZE: int = 32
DEFAULT_QUERY_SIZE: int = 64
DEFAULT_META_ITERATIONS: int = 200
DEFAULT_MIN_TASK_SAMPLES: int = 16
DEFAULT_SHOTS: int = 32
DEFAULT_EVAL_QUERY: int = 2048
DEFAULT_MIN_QUERY_SAMPLES: int = 30
DEFAULT_DROPOUT: float = 0.0
DEFAULT_SEED: int = tp.DEFAULT_SEED
DEFAULT_EVAL_EPISODES: int = 1
INNER_WEIGHTS_NONE, INNER_WEIGHTS_INVERSE_FREQUENCY = "none", "inverse_frequency"
INNER_WEIGHTS_CHOICES: Tuple[str, str] = (INNER_WEIGHTS_NONE, INNER_WEIGHTS_INVERSE_FREQUENCY)
SAMPLING_WITH_REPLACEMENT, SAMPLING_WITHOUT_REPLACEMENT = "with_replacement", "without_replacement"
SAMPLING_CHOICES: Tuple[str, str] = (SAMPLING_WITH_REPLACEMENT, SAMPLING_WITHOUT_REPLACEMENT)
# The three conditions of the held-out comparison (M8) and the metrics summarised over several evaluation episodes.
CONDITION_UNADAPTED, CONDITION_ADAPTED, CONDITION_RANDOM = "meta_init_unadapted", "meta_init_adapted", "random_init_adapted"
EVAL_CONDITIONS: Tuple[str, str, str] = (CONDITION_UNADAPTED, CONDITION_ADAPTED, CONDITION_RANDOM)
EVAL_SUMMARY_METRICS: Tuple[str, ...] = ("accuracy", "macro_f1", "accuracy_over_baseline", "multi_logloss")


class Limits:
    """Layer 3: safety bounds no option can widen (exit 2 / 3 above them)."""

    MAX_TASKS: int = 10_000
    MAX_ITERATIONS: int = 100_000
    MAX_INNER_STEPS: int = 100
    MAX_EPISODE_SAMPLES: int = 10_000          # support_size / query_size / shots / eval_query each
    MAX_META_BATCH: int = 256
    MAX_EVAL_EPISODES: int = 100               # --eval-episodes
    MAX_TENSOR_BYTES: int = 512 * 1024 * 1024  # one float32 window block (a task, or an episode)
    MAX_TOTAL_BYTES: int = 2 * 1024 * 1024 * 1024  # all task blocks together


# Layer 4: run duration (hard / standard band as in checkpoints.resolve_band; same form as SA_CF_MAX_SECONDS / SA_EV_MAX_SECONDS).
MAX_SECONDS_ENV: str = "SA_MT_MAX_SECONDS"
MAX_SECONDS: int = checkpoints.resolve_band(MAX_SECONDS_ENV, 3600, (1, 86_400), (60, 7_200))

NOTICES: Tuple[str, ...] = (
    "descriptive_comparison_not_advice", "no_empirical_basis_for_default_hyperparameters", "real_world_validation_unverified",
    "symbols_are_contemporaneous_cross_symbol_time_overlap_is_not_purged", "meta_train_episodes_may_postdate_meta_test_period")


class PrerequisiteError(Exception):
    """A prerequisite is missing or violated (exit code 3). Nothing is published."""


# --- Small helpers ------------------------------------------------------------------------------------------------------------------


def _symbol_key(symbol: str) -> int:
    return zlib.crc32(symbol.encode("utf-8"))


def _sequence(seed: int, *key: int) -> np.random.SeedSequence:
    return np.random.SeedSequence(int(seed), spawn_key=tuple(int(k) for k in key))


def _generator(seed: int, *key: int) -> np.random.Generator:
    return np.random.default_rng(_sequence(seed, *key))


def _torch_seed(seed: int, *key: int) -> int:
    return int(_sequence(seed, *key).generate_state(1, dtype=np.uint32)[0]) & 0x7FFFFFFF


def _finite_float(text: str) -> float:
    value = float(text)
    if not math.isfinite(value):
        raise argparse.ArgumentTypeError(f"{text!r} is not a finite number")
    return value


def _positive_int(text: str) -> int:
    value = int(text)
    if value < 1:
        raise argparse.ArgumentTypeError(f"{text!r} must be a positive integer")
    return value


def _csv(text: str) -> List[str]:
    return [part.strip() for part in text.split(",") if part.strip()]


def _sha_of(value) -> str:
    import hashlib
    return hashlib.sha256(checkpoints.canonical(value).encode("utf-8")).hexdigest()


def _utc_now() -> datetime.datetime:
    return datetime.datetime.now(datetime.timezone.utc)


def _check_bytes(count: int, window: int, channels: int, limit: int, what: str) -> None:
    needed = int(count) * int(window) * int(channels) * 4
    if needed > limit:
        raise PrerequisiteError(f"{what} needs {needed} bytes of float32 windows, above the bound of {limit}")


# --- Tasks (M3 / M4 / M5) ---------------------------------------------------------------------------------------------------------


@dataclass(frozen=True)
class Task:
    """One symbol's chronological samples: ``x`` (n, window, channels) float32, ``y`` (n,) int64, ``anchor_rows`` (n,) and the anchor ``dates``."""

    symbol: str
    x: np.ndarray
    y: np.ndarray
    anchor_rows: np.ndarray
    dates: np.ndarray


def build_task(symbol: str, ohlcv: np.ndarray, dates: np.ndarray, *, feature_mode: str, window: int, horizon: int, threshold: float,
               oos_tail_days: int, common_end: datetime.date) -> Task | None:
    """Samples of the MAIN block only: rows after the out-of-sample cutoff are removed before any window or label is built. ``None`` = no main block."""
    main_ohlcv, main_dates, _, _ = ds.oos_split(ohlcv, dates, oos_tail_days, common_end=common_end)
    if main_ohlcv.shape[0] == 0:
        return None
    candidates = max(main_ohlcv.shape[0] - window - horizon + 1, 0)
    _check_bytes(candidates, window, ds.feature_channels(feature_mode), Limits.MAX_TENSOR_BYTES, f"task {symbol}")
    x, y, anchors = ds.build_dataset(main_ohlcv, feature_mode, window, horizon, threshold, return_anchor_rows=True)
    return Task(symbol, x, y, anchors, np.asarray(main_dates).astype("datetime64[D]")[anchors] if anchors.size else np.empty(0, "datetime64[D]"))


def episode_starts(anchor_rows: np.ndarray, support_size: int, query_size: int, gap: int) -> np.ndarray:
    """Support start positions p that leave room for a query block: the first query sample is the first one whose anchor row is at least ``gap + 1``
    rows after the last support anchor (so the information intervals ``[anchor-window+1, anchor+horizon]`` do not intersect for the default gap)."""
    n = anchor_rows.shape[0]
    if n < support_size + query_size:
        return np.empty(0, dtype=np.int64)
    last_support = anchor_rows[support_size - 1:]
    first_query = np.searchsorted(anchor_rows, last_support + gap + 1, side="left")
    valid = first_query + query_size <= n
    return np.nonzero(valid)[0].astype(np.int64)


def episode_indices(anchor_rows: np.ndarray, start: int, support_size: int, query_size: int, gap: int) -> Tuple[np.ndarray, np.ndarray]:
    support = np.arange(start, start + support_size, dtype=np.int64)
    first_query = int(np.searchsorted(anchor_rows, anchor_rows[start + support_size - 1] + gap + 1, side="left"))
    query = np.arange(first_query, first_query + query_size, dtype=np.int64)
    if query[-1] >= anchor_rows.shape[0]:
        raise ValueError("episode does not fit the task")
    return support, query


def classify_tasks(tasks: Dict[str, Task | None], *, support_size: int, query_size: int, gap: int, min_task_samples: int) -> Tuple[List[Task], List[dict], List[dict]]:
    """(usable tasks, task records for the document, skipped records). A skipped task carries its reason (M5)."""
    usable: List[Task] = []
    records: List[dict] = []
    skipped: List[dict] = []
    for symbol in sorted(tasks):
        task = tasks[symbol]
        if task is None:
            reason, samples = "no_main_block", 0
        else:
            samples = int(task.y.shape[0])
            if samples < min_task_samples:
                reason = "below_min_task_samples"
            elif episode_starts(task.anchor_rows, support_size, query_size, gap).size == 0:
                reason = "episode_does_not_fit"
            else:
                reason = ""
        if reason:
            skipped.append(dict(symbol=symbol, reason=reason, samples=samples))
            records.append(dict(symbol=symbol, status="skipped", reason=reason, samples=samples))
        else:
            usable.append(task)
            records.append(dict(symbol=symbol, status="used", samples=samples))
    return usable, records, skipped


# --- Inner / outer updates (M1 / M2) ---------------------------------------------------------------------------------------------


def cross_entropy(logits: torch.Tensor, labels: torch.Tensor) -> torch.Tensor:
    return nn.functional.cross_entropy(logits, labels)


def inverse_frequency_weights(labels: torch.Tensor) -> torch.Tensor:
    """Class weights ``1 / N_c`` for the labels of one support block (``0`` for a class that does not occur). PyTorch divides a weighted mean by the sum of the
    target weights, so every class that occurs contributes equally to the loss."""
    counts = torch.bincount(labels, minlength=NUM_CLASSES).to(torch.float32)
    return torch.where(counts > 0, 1.0 / counts.clamp(min=1.0), torch.zeros_like(counts))


def support_loss(labels: torch.Tensor, mode: str) -> Callable:
    """The loss of the INNER (support) steps of one episode: plain cross-entropy, or the inverse-frequency weighted one. The query (meta) loss and every
    evaluation score stay plain cross-entropy."""
    if mode == INNER_WEIGHTS_NONE:
        return cross_entropy
    weights = inverse_frequency_weights(labels)
    return lambda logits, targets: nn.functional.cross_entropy(logits, targets, weight=weights)


def choose_tasks(seed: int, iteration: int, task_count: int, batch: int, sampling: str) -> np.ndarray:
    """The task indices of one meta-batch: with replacement (the default), or without replacement when there are at least ``batch`` tasks (otherwise the
    with-replacement draw). Both draws come from the generator ``(seed, STREAM_TASK_CHOICE, iteration)``."""
    generator = _generator(seed, STREAM_TASK_CHOICE, iteration)
    if sampling == SAMPLING_WITHOUT_REPLACEMENT and task_count >= batch:
        return generator.permutation(task_count)[:batch]
    return generator.integers(0, task_count, size=batch)


def inner_adapt(model: nn.Module, params: Dict[str, torch.Tensor], loss_fn: Callable, xs: torch.Tensor, ys: torch.Tensor,
                steps: int, lr: float, *, create_graph: bool) -> Dict[str, torch.Tensor]:
    """``steps`` full-batch SGD steps on the support block. With ``create_graph`` the update stays differentiable (second order); without it the
    gradient is a constant, so ``d fast / d params`` is the identity (first order). ``params`` are not modified."""
    fast = dict(params)
    for _ in range(steps):
        loss = loss_fn(functional_call(model, fast, (xs,)), ys)
        grads = torch.autograd.grad(loss, list(fast.values()), create_graph=create_graph)
        fast = {name: value - lr * grad for (name, value), grad in zip(fast.items(), grads)}
    return fast


def detached_adapt(model: nn.Module, params: Dict[str, torch.Tensor], loss_fn: Callable, xs: torch.Tensor, ys: torch.Tensor,
                   steps: int, lr: float) -> Dict[str, torch.Tensor]:
    """Plain SGD on detached copies of ``params`` (Reptile and evaluation): the result is a new leaf dict and no graph survives."""
    fast = {name: value.detach().clone().requires_grad_(True) for name, value in params.items()}
    for _ in range(steps):
        loss = loss_fn(functional_call(model, fast, (xs,)), ys)
        grads = torch.autograd.grad(loss, list(fast.values()))
        fast = {name: (value - lr * grad).detach().requires_grad_(True) for (name, value), grad in zip(fast.items(), grads)}
    return {name: value.detach() for name, value in fast.items()}


def reptile_interpolate(theta: Dict[str, torch.Tensor], adapted: Sequence[Dict[str, torch.Tensor]], epsilon: float) -> Dict[str, torch.Tensor]:
    """``theta + epsilon * mean_i(adapted_i - theta)`` (the Reptile outer step over a meta-batch)."""
    count = len(adapted)
    return {name: theta[name] + epsilon * sum(a[name] - theta[name] for a in adapted) / count for name in theta}


@dataclass(frozen=True)
class TrainSpec:
    algorithm: str
    second_order: bool
    inner_steps: int
    inner_lr: float
    meta_lr: float
    reptile_epsilon: float
    meta_batch_tasks: int
    support_size: int
    query_size: int
    meta_iterations: int
    gap: int
    seed: int
    inner_class_weights: str = INNER_WEIGHTS_NONE
    task_sampling: str = SAMPLING_WITH_REPLACEMENT


def _episode_tensors(task: Task, spec: TrainSpec, iteration: int, slot: int) -> Tuple[torch.Tensor, torch.Tensor, torch.Tensor, torch.Tensor]:
    starts = episode_starts(task.anchor_rows, spec.support_size, spec.query_size, spec.gap)
    rng = _generator(spec.seed, STREAM_EPISODE, iteration, slot, _symbol_key(task.symbol))
    start = int(starts[int(rng.integers(0, starts.size))])
    support, query = episode_indices(task.anchor_rows, start, spec.support_size, spec.query_size, spec.gap)
    return (torch.from_numpy(task.x[support]), torch.from_numpy(task.y[support]),
            torch.from_numpy(task.x[query]), torch.from_numpy(task.y[query]))


def _check_deadline(deadline: float | None) -> None:
    if deadline is not None and time.monotonic() > deadline:
        raise TimeoutError("the run exceeded its run-duration bound")


def meta_train(model: nn.Module, tasks: Sequence[Task], spec: TrainSpec, *, deadline: float | None = None) -> dict:
    """Run ``spec.meta_iterations`` meta-iterations; ``model`` holds the meta-initialization afterwards. Returns the training summary."""
    params = dict(model.named_parameters())
    optimizer = torch.optim.Adam(model.parameters(), lr=spec.meta_lr) if spec.algorithm == ALGORITHM_MAML else None
    losses: List[float] = []
    model.train()
    for iteration in range(spec.meta_iterations):
        _check_deadline(deadline)
        chosen = choose_tasks(spec.seed, iteration, len(tasks), spec.meta_batch_tasks, spec.task_sampling)
        adapted: List[Dict[str, torch.Tensor]] = []
        query_losses: List[float] = []
        if optimizer is not None:
            optimizer.zero_grad(set_to_none=True)
        for slot, task_index in enumerate(chosen):
            task = tasks[int(task_index)]
            xs, ys, xq, yq = _episode_tensors(task, spec, iteration, slot)
            inner_loss = support_loss(ys, spec.inner_class_weights)
            with torch.random.fork_rng(devices=[]):
                torch.manual_seed(_torch_seed(spec.seed, STREAM_TORCH_EPISODE, iteration, slot, _symbol_key(task.symbol)))
                if spec.algorithm == ALGORITHM_REPTILE:
                    fast = detached_adapt(model, params, inner_loss, xs, ys, spec.inner_steps, spec.inner_lr)
                    with torch.no_grad():
                        query_loss = cross_entropy(functional_call(model, fast, (xq,)), yq)
                    adapted.append(fast)
                else:
                    fast = inner_adapt(model, params, inner_loss, xs, ys, spec.inner_steps, spec.inner_lr, create_graph=spec.second_order)
                    query_loss = cross_entropy(functional_call(model, fast, (xq,)), yq)
                    (query_loss / spec.meta_batch_tasks).backward()
            value = float(query_loss.detach())
            if not math.isfinite(value):
                raise ArithmeticError("non-finite query loss; nothing is published")
            query_losses.append(value)
        if spec.algorithm == ALGORITHM_REPTILE:
            theta = {name: p.detach().clone() for name, p in params.items()}
            updated = reptile_interpolate(theta, adapted, spec.reptile_epsilon)
            with torch.no_grad():
                for name, p in params.items():
                    p.copy_(updated[name])
        else:
            assert optimizer is not None
            optimizer.step()
        losses.append(float(np.mean(query_losses)))
    for p in params.values():
        if not torch.isfinite(p).all():
            raise ArithmeticError("non-finite meta-initialization; nothing is published")
    stride = max(1, len(losses) // CURVE_POINTS)
    return dict(completed_iterations=len(losses), query_loss_first=losses[0], query_loss_last=losses[-1], query_loss_mean=float(np.mean(losses)),
                query_loss_curve=[round(v, 6) for v in losses[::stride]], query_loss_curve_stride=stride)


# --- Adaptation scoring (M8) -------------------------------------------------------------------------------------------------------


def _scores(model: nn.Module, params: Dict[str, torch.Tensor], xq: torch.Tensor, yq: torch.Tensor) -> dict:
    model.eval()
    with torch.no_grad():
        probs = torch.softmax(functional_call(model, params, (xq,)).double(), dim=-1).numpy()
    report = metrics.classification_report_dict(yq.numpy(), probs.argmax(axis=1), probs=probs)
    out = {"n_samples": int(report["n_samples"])}
    for key in ("accuracy", "macro_f1", "majority_baseline_accuracy", "accuracy_over_baseline", "multi_logloss"):
        value = report.get(key)
        out[key] = float(value) if isinstance(value, (int, float)) and math.isfinite(value) else None
    return out


def eval_split(task: Task, shots: int, eval_query: int, gap: int, start: int = 0) -> Tuple[np.ndarray, np.ndarray] | None:
    """Chronological support (``shots`` samples from sample ``start``, the first ones by default) and query (after the purge gap, at most ``eval_query``
    samples) of a held-out symbol."""
    n = task.y.shape[0]
    if n < start + shots + 1:
        return None
    first_query = int(np.searchsorted(task.anchor_rows, task.anchor_rows[start + shots - 1] + gap + 1, side="left"))
    if first_query >= n:
        return None
    return np.arange(start, start + shots, dtype=np.int64), np.arange(first_query, min(n, first_query + eval_query), dtype=np.int64)


def _adapt_episode(model: nn.Module, meta_state: Dict[str, torch.Tensor], build_fresh: Callable[[], nn.Module], task: Task, support: np.ndarray,
                   query: np.ndarray, *, steps: int, lr: float, seed: int, episode: int, inner_class_weights: str
                   ) -> Tuple[dict, Dict[str, torch.Tensor]]:
    """Three conditions on one episode of a held-out symbol (the shared core of ``adapt_and_score`` and ``adapt_and_score_episodes``). Episode 0 uses the seed
    keys of the single-episode evaluation; episode ``k >= 1`` appends ``k`` as the last element of each spawn key. Returns (result fields, adapted meta-init)."""
    xs, ys = torch.from_numpy(task.x[support]), torch.from_numpy(task.y[support])
    xq, yq = torch.from_numpy(task.x[query]), torch.from_numpy(task.y[query])
    loss_fn = support_loss(ys, inner_class_weights)
    key_tail = (episode,) if episode else ()
    model.load_state_dict(meta_state)
    meta_params = {k: v.detach().clone() for k, v in model.named_parameters()}
    with torch.random.fork_rng(devices=[]):
        torch.manual_seed(_torch_seed(seed, STREAM_TORCH_EPISODE, 0, 0, _symbol_key(task.symbol), *key_tail))
        model.train()
        meta_adapted = detached_adapt(model, meta_params, loss_fn, xs, ys, steps, lr)
        torch.manual_seed(_torch_seed(seed, STREAM_RANDOM_INIT, _symbol_key(task.symbol), *key_tail))
        fresh = build_fresh()
        fresh_params = {k: v.detach().clone() for k, v in fresh.named_parameters()}
        fresh.train()
        fresh_adapted = detached_adapt(fresh, fresh_params, loss_fn, xs, ys, steps, lr)
    fields = dict(
        status=STATUS_AVAILABLE, n_support=int(support.size), n_query=int(query.size),
        support_dates=[str(task.dates[support[0]]), str(task.dates[support[-1]])], query_dates=[str(task.dates[query[0]]), str(task.dates[query[-1]])],
        conditions={CONDITION_UNADAPTED: _scores(model, meta_params, xq, yq), CONDITION_ADAPTED: _scores(model, meta_adapted, xq, yq),
                    CONDITION_RANDOM: _scores(fresh, fresh_adapted, xq, yq)})
    return fields, meta_adapted


def adapt_and_score(model: nn.Module, meta_state: Dict[str, torch.Tensor], build_fresh: Callable[[], nn.Module], task: Task, *, shots: int,
                    eval_query: int, min_query: int, steps: int, lr: float, gap: int, seed: int, inner_class_weights: str = INNER_WEIGHTS_NONE
                    ) -> Tuple[dict, Dict[str, torch.Tensor] | None]:
    """Three conditions on the held-out symbol's query block. Returns (block, adapted meta-init parameters or None)."""
    split = eval_split(task, shots, eval_query, gap)
    block: dict = dict(symbol=task.symbol, shots=shots, adapt_steps=steps, inner_lr=lr, purge_gap=gap)
    if split is None or split[1].size < min_query:
        block.update(status=STATUS_INSUFFICIENT, n_support=int(shots if split is not None else 0), n_query=int(split[1].size if split is not None else 0),
                     min_query_samples=min_query)
        return block, None
    fields, meta_adapted = _adapt_episode(model, meta_state, build_fresh, task, split[0], split[1], steps=steps, lr=lr, seed=seed, episode=0,
                                          inner_class_weights=inner_class_weights)
    block.update(fields)
    return block, meta_adapted


def _summarize_episodes(episodes: Sequence[dict]) -> dict:
    """Per condition and metric: the mean, the sample standard deviation (divisor ``n - 1``; ``None`` below two values) and the number of valid values over
    the valid episodes whose metric is not null."""
    summary: dict = {}
    for condition in EVAL_CONDITIONS:
        per_metric: dict = {}
        for metric in EVAL_SUMMARY_METRICS:
            values = [e["conditions"][condition][metric] for e in episodes if e["status"] == STATUS_AVAILABLE and e["conditions"][condition][metric] is not None]
            per_metric[metric] = dict(mean=statistics.fmean(values) if values else None, std=statistics.stdev(values) if len(values) >= 2 else None,
                                      n_valid=len(values))
        summary[condition] = per_metric
    return summary


def adapt_and_score_episodes(model: nn.Module, meta_state: Dict[str, torch.Tensor], build_fresh: Callable[[], nn.Module], task: Task, *, shots: int,
                             eval_query: int, min_query: int, steps: int, lr: float, gap: int, seed: int, episodes: int,
                             inner_class_weights: str = INNER_WEIGHTS_NONE) -> Tuple[dict, Dict[str, torch.Tensor] | None]:
    """The held-out evaluation over ``episodes`` chronological episodes. Episode ``k`` takes the support from sample ``k * stride`` with
    ``stride = shots + gap + eval_query`` and its query by the rule of ``eval_split`` from there; it is valid when the support fits and the query has at least
    ``min_query`` samples, otherwise ``insufficient_data`` and left out of ``summary``. The fields of the single-episode block (status of the first valid
    episode aside) keep episode 0, so a reader of the old block still works; ``status`` is available when at least one episode is valid."""
    stride = shots + gap + eval_query
    entries: List[dict] = []
    first_adapted: Dict[str, torch.Tensor] | None = None
    for index in range(episodes):
        split = eval_split(task, shots, eval_query, gap, start=index * stride)
        entry: dict = dict(index=index)
        if split is None or split[1].size < min_query:
            entry.update(status=STATUS_INSUFFICIENT, n_support=int(shots if split is not None else 0), n_query=int(split[1].size if split is not None else 0))
        else:
            fields, adapted = _adapt_episode(model, meta_state, build_fresh, task, split[0], split[1], steps=steps, lr=lr, seed=seed, episode=index,
                                             inner_class_weights=inner_class_weights)
            entry.update(fields)
            if index == 0:
                first_adapted = adapted
        entries.append(entry)
    block: dict = dict(symbol=task.symbol, shots=shots, adapt_steps=steps, inner_lr=lr, purge_gap=gap)
    head = entries[0]
    if head["status"] == STATUS_AVAILABLE:
        block.update({key: head[key] for key in ("n_support", "n_query", "support_dates", "query_dates", "conditions")})
    else:
        block.update(n_support=head["n_support"], n_query=head["n_query"], min_query_samples=min_query)
    block["status"] = STATUS_AVAILABLE if any(e["status"] == STATUS_AVAILABLE for e in entries) else STATUS_INSUFFICIENT
    block["episodes"] = entries
    block["summary"] = _summarize_episodes(entries)
    return block, first_adapted


# --- Publication (S5) ---------------------------------------------------------------------------------------------------------------


def publish_run(root: Path, kind: str, config_sha256: str, writer: Callable[[Path], None], *, now: datetime.datetime | None = None) -> Path:
    """Build ``<root>/<kind>/<runId>.incoming`` with ``writer`` and rename it once; an existing run is never reused, a failure leaves nothing."""
    stamp = (now or _utc_now()).strftime("%Y%m%dT%H%M%SZ")
    run_id = f"{stamp}_{config_sha256[:8]}"
    final = Path(root) / kind / run_id
    incoming = final.with_name(run_id + INCOMING_SUFFIX)
    if final.exists() or incoming.exists():
        raise PrerequisiteError(f"{final} already exists; an existing run is never reused")
    incoming.mkdir(parents=True)
    try:
        writer(incoming)
        checkpoints.replace_with_retry(incoming, final)
    except BaseException:
        shutil.rmtree(incoming, ignore_errors=True)
        raise
    return final


def _write_json(path: Path, value: dict) -> None:
    path.write_text(json.dumps(value, sort_keys=True, indent=2, allow_nan=False), encoding="utf-8")


# --- Data -----------------------------------------------------------------------------------------------------------------------------


def _load_symbols(args, wanted: Sequence[str], common_end: datetime.date | None) -> Tuple[Dict[str, Task | None], datetime.date, Dict[str, np.ndarray], Dict[str, np.ndarray]]:
    """Load ``wanted`` symbols (synthetic data refused) and build their main-block tasks."""
    try:
        symbols, dates = ds.load_parquet_dir(args.data_dir, return_dates=True, symbols=list(wanted), start=args.data_start, end=args.data_end)
    except (FileNotFoundError, ValueError) as exc:
        raise PrerequisiteError(str(exc)) from exc
    by_lower = {name.lower(): name for name in symbols}
    missing = [s for s in wanted if s.lower() not in by_lower]
    if missing:
        raise PrerequisiteError(f"no usable parquet data for: {', '.join(missing)}")
    end = common_end if common_end is not None else ds.common_end_date({name: dates[name] for name in symbols})
    tasks: Dict[str, Task | None] = {}
    for name in sorted(symbols):
        tasks[name] = build_task(name, symbols[name], dates[name], feature_mode=args.feature_mode, window=args.window, horizon=args.horizon,
                                 threshold=args.threshold, oos_tail_days=args.oos_tail_days, common_end=end)
    return tasks, end, symbols, dates


def _tunables(args) -> dict:
    return dict(inner_steps=args.inner_steps, inner_lr=args.inner_lr, meta_lr=args.meta_lr, reptile_epsilon=args.reptile_epsilon,
                meta_batch_tasks=args.meta_batch_tasks, support_size=args.support_size, query_size=args.query_size, meta_iterations=args.meta_iterations,
                min_task_samples=args.min_task_samples, shots=args.shots, adapt_steps=args.adapt_steps, eval_query=args.eval_query,
                min_query_samples=args.min_query_samples, hidden=args.hidden, layers=args.layers, dropout=args.dropout)


def _optional_config(args) -> dict:
    """The options added after the first release; each enters the config (and so the run id) only when it differs from its default, so a default run is
    byte-identical to what it was before they existed."""
    out: dict = {}
    if args.eval_episodes != DEFAULT_EVAL_EPISODES:
        out["eval_episodes"] = args.eval_episodes
    if args.inner_class_weights != INNER_WEIGHTS_NONE:
        out["inner_class_weights"] = args.inner_class_weights
    if args.task_sampling != SAMPLING_WITH_REPLACEMENT:
        out["task_sampling"] = args.task_sampling
    return out


def _thresholds_basis(args) -> str:
    defaults = dict(inner_steps=DEFAULT_INNER_STEPS, inner_lr=DEFAULT_INNER_LR, meta_batch_tasks=DEFAULT_META_BATCH_TASKS, support_size=DEFAULT_SUPPORT_SIZE,
                    query_size=DEFAULT_QUERY_SIZE, meta_iterations=DEFAULT_META_ITERATIONS, min_task_samples=DEFAULT_MIN_TASK_SAMPLES,
                    shots=DEFAULT_SHOTS, eval_query=DEFAULT_EVAL_QUERY, min_query_samples=DEFAULT_MIN_QUERY_SAMPLES,
                    hidden=tp.DEFAULT_HIDDEN, layers=tp.DEFAULT_LAYERS, dropout=DEFAULT_DROPOUT)
    if args.algorithm == ALGORITHM_MAML:
        defaults["meta_lr"] = DEFAULT_META_LR
    else:
        defaults["reptile_epsilon"] = DEFAULT_REPTILE_EPSILON
    tunables = _tunables(args)
    user = any(tunables[k] != v for k, v in defaults.items()) or bool(_optional_config(args))
    return THRESHOLDS_BASIS_USER if user else THRESHOLDS_BASIS_DEFAULT


# --- train ---------------------------------------------------------------------------------------------------------------------------


def run_train(args) -> Path:
    started = time.monotonic()
    deadline = started + args.max_seconds
    if args.timeframe != SUPPORTED_TIMEFRAME:
        raise PrerequisiteError(f"timeframe_not_supported: {args.timeframe!r}; the window and horizon are bars of the daily series")
    overlap = sorted({s.lower() for s in args.symbols} & {s.lower() for s in args.meta_test_symbols})
    if overlap:
        raise PrerequisiteError(f"meta-test symbols must be disjoint from the meta-train symbols: {', '.join(overlap)}")
    if len(args.symbols) > Limits.MAX_TASKS:
        raise PrerequisiteError(f"{len(args.symbols)} tasks exceed the bound of {Limits.MAX_TASKS}")
    gap = ds.resolve_purge_gap(args.window, args.horizon, args.gap)
    channels = ds.feature_channels(args.feature_mode)
    _check_bytes(args.support_size + args.query_size, args.window, channels, Limits.MAX_TENSOR_BYTES, "one episode")
    tasks, common_end, symbols, dates = _load_symbols(args, list(args.symbols) + list(args.meta_test_symbols), None)
    stems = {name.lower(): name for name in tasks}          # the file stem is the symbol identity; the command line may differ in case
    train_symbols = sorted({stems[s.lower()] for s in args.symbols})
    test_symbols = sorted({stems[s.lower()] for s in args.meta_test_symbols})
    total_candidates = sum(max(symbols[name].shape[0] - args.window - args.horizon + 1, 0) for name in symbols)
    _check_bytes(total_candidates, args.window, channels, Limits.MAX_TOTAL_BYTES, "all tasks")
    usable, records, skipped = classify_tasks({s: tasks[s] for s in train_symbols if s in tasks}, support_size=args.support_size,
                                              query_size=args.query_size, gap=gap, min_task_samples=args.min_task_samples)
    if not usable:
        raise PrerequisiteError("no usable task: " + "; ".join(f"{s['symbol']} {s['reason']}" for s in skipped))
    spec = TrainSpec(algorithm=args.algorithm, second_order=args.second_order, inner_steps=args.inner_steps, inner_lr=args.inner_lr,
                     meta_lr=args.meta_lr if args.algorithm == ALGORITHM_MAML else DEFAULT_META_LR,
                     reptile_epsilon=args.reptile_epsilon if args.algorithm == ALGORITHM_REPTILE else DEFAULT_REPTILE_EPSILON,
                     meta_batch_tasks=args.meta_batch_tasks, support_size=args.support_size, query_size=args.query_size,
                     meta_iterations=args.meta_iterations, gap=gap, seed=args.seed, inner_class_weights=args.inner_class_weights,
                     task_sampling=args.task_sampling)
    torch.manual_seed(args.seed)
    model = tp.build_model(args.arch, channels, args.hidden, args.layers, args.dropout, NUM_CLASSES)
    summary = meta_train(model, usable, spec, deadline=deadline)
    meta_state = {k: v.detach().cpu().clone() for k, v in model.state_dict().items()}

    def fresh() -> nn.Module:
        return tp.build_model(args.arch, channels, args.hidden, args.layers, args.dropout, NUM_CLASSES)

    meta_test: List[dict] = []
    for symbol in test_symbols:
        _check_deadline(deadline)
        task = tasks.get(symbol)
        if task is None:
            meta_test.append(dict(symbol=symbol, status=STATUS_INSUFFICIENT, reason="no_main_block"))
            continue
        scoring = dict(shots=args.shots, eval_query=args.eval_query, min_query=args.min_query_samples, steps=args.adapt_steps, lr=args.inner_lr, gap=gap,
                       seed=args.seed, inner_class_weights=args.inner_class_weights)
        if args.eval_episodes > 1:
            block, _ = adapt_and_score_episodes(model, meta_state, fresh, task, episodes=args.eval_episodes, **scoring)
        else:
            block, _ = adapt_and_score(model, meta_state, fresh, task, **scoring)
        meta_test.append(block)
    data_revision = checkpoints.source_revision({s: symbols[s] for s in symbols}, {s: dates[s] for s in symbols})
    config = dict(algorithm=args.algorithm, second_order=args.second_order, arch=args.arch, feature_mode=args.feature_mode, window=args.window,
                  horizon=args.horizon, threshold=args.threshold, gap=gap, seed=args.seed, oos_tail_days=args.oos_tail_days,
                  common_end=str(common_end), train_symbols=train_symbols, meta_test_symbols=test_symbols, data_revision=data_revision,
                  **_tunables(args), **_optional_config(args))
    config_sha256 = _sha_of(config)
    runtime = {"torch": str(torch.__version__), "numpy": np.__version__, "device": "cpu"}

    def write(folder: Path) -> None:
        blob = folder / META_INIT_NAME
        torch.save(meta_state, blob)
        document = dict(
            schema_version=SCHEMA_VERSION, tool=TOOL, tool_version=TOOL_VERSION, created_utc=_utc_now().isoformat(),
            elapsed_seconds=round(time.monotonic() - started, ELAPSED_DIGITS), status=STATUS_AVAILABLE, kind=KIND_TRAIN,
            algorithm=args.algorithm, second_order=bool(args.second_order and args.algorithm == ALGORITHM_MAML),
            config=config, config_sha256=config_sha256, thresholds_basis=_thresholds_basis(args), rng_policy=RNG_POLICY, runtime=runtime,
            model=dict(arch=args.arch, hidden=args.hidden, layers=args.layers, dropout=args.dropout, channels=channels, window=args.window,
                       horizon=args.horizon, threshold=args.threshold, feature_mode=args.feature_mode, timeframe=args.timeframe, out_dim=NUM_CLASSES,
                       class_order=list(ds.CLASS_LABELS), parameter_count=int(sum(p.numel() for p in model.parameters()))),
            meta_init=dict(file=META_INIT_NAME, sha256=checkpoints.hash_file(blob)),
            data=dict(data_revision=data_revision, oos_tail_days=args.oos_tail_days, oos_consumed=False, common_end=str(common_end),
                      timeframe=args.timeframe, train_symbols=train_symbols, meta_test_symbols=test_symbols),
            tasks=records, skipped_tasks=skipped, single_task=len(usable) == 1,
            independence=dict(support_before_query=True, purge_gap=gap, information_intervals_disjoint=gap >= args.window + args.horizon - 1,
                              meta_test_disjoint_from_meta_train=True, oos_tail_read=False),
            training=summary, meta_test=meta_test, notices=list(NOTICES), verification=dict(real_world_validation="unverified"))
        _write_json(folder / META_DOC_NAME, document)

    final = publish_run(args.out_root, KIND_TRAIN, config_sha256, write)
    print(f"wrote {final} (tasks used={len(usable)} skipped={len(skipped)} iterations={summary['completed_iterations']} "
          f"query_loss {summary['query_loss_first']:.4f} -> {summary['query_loss_last']:.4f})")
    return final


# --- adapt ---------------------------------------------------------------------------------------------------------------------------


def load_meta_run(folder: Path) -> Tuple[dict, Dict[str, torch.Tensor]]:
    """Read and verify a published meta run: schema, blob hash, and the model block."""
    folder = Path(folder)
    document_path = folder / META_DOC_NAME
    if not document_path.is_file():
        raise PrerequisiteError(f"{folder} is not a meta run (no {META_DOC_NAME})")
    try:
        document = json.loads(document_path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        raise PrerequisiteError(f"{document_path} cannot be read: {exc}") from exc
    if not isinstance(document, dict) or document.get("schema_version") != SCHEMA_VERSION or document.get("kind") != KIND_TRAIN:
        raise PrerequisiteError(f"{document_path} is not a schema {SCHEMA_VERSION} meta run document")
    blob = folder / document["meta_init"]["file"]
    if Path(document["meta_init"]["file"]).name != document["meta_init"]["file"] or not blob.is_file():
        raise PrerequisiteError("the meta-initialization file is missing")
    if checkpoints.hash_file(blob) != document["meta_init"]["sha256"]:
        raise PrerequisiteError("the meta-initialization file does not match its recorded SHA-256")
    state = torch.load(blob, map_location="cpu", weights_only=True)
    if not isinstance(state, dict) or any(not isinstance(v, torch.Tensor) or not torch.isfinite(v).all() for v in state.values()):
        raise PrerequisiteError("the meta-initialization holds invalid weights")
    return document, state


def run_adapt(args) -> Path:
    started = time.monotonic()
    deadline = started + args.max_seconds
    document, state = load_meta_run(args.meta_run)
    model_info = document["model"]
    data_info = document["data"]
    if args.symbol.lower() in {s.lower() for s in data_info["train_symbols"]}:
        raise PrerequisiteError(f"{args.symbol} is a meta-train symbol of this run; adapting to it would not be a new-symbol test")
    if model_info["timeframe"] != SUPPORTED_TIMEFRAME:
        raise PrerequisiteError("timeframe_not_supported")
    # the feature definition is the run's, never the command line's
    args.feature_mode, args.window, args.horizon, args.threshold = (model_info["feature_mode"], model_info["window"], model_info["horizon"],
                                                                    model_info["threshold"])
    args.oos_tail_days = args.oos_tail_days if args.oos_tail_days is not None else data_info["oos_tail_days"]
    if args.oos_tail_days < 1:
        raise PrerequisiteError("--oos-tail-days must be at least 1")
    common_end = datetime.date.fromisoformat(data_info["common_end"])
    gap = document["independence"]["purge_gap"]
    tasks, _, symbols, dates = _load_symbols(args, [args.symbol], common_end)
    task = next(iter(tasks.values()))
    if task is None:
        raise PrerequisiteError("the symbol has no rows before the out-of-sample cutoff")
    arch, channels = model_info["arch"], model_info["channels"]

    def fresh() -> nn.Module:
        return tp.build_model(arch, channels, model_info["hidden"], model_info["layers"], model_info["dropout"], NUM_CLASSES)

    model = fresh()
    model.load_state_dict(state, strict=True)
    config_in = document["config"]
    steps = args.adapt_steps if args.adapt_steps is not None else config_in["adapt_steps"]
    lr = args.inner_lr if args.inner_lr is not None else config_in["inner_lr"]
    shots = args.shots if args.shots is not None else config_in["shots"]
    # the inner loss of the meta run it adapts (absent in a run made before the option existed, or with the default)
    inner_weights = config_in.get("inner_class_weights", INNER_WEIGHTS_NONE)
    if inner_weights not in INNER_WEIGHTS_CHOICES:
        raise PrerequisiteError(f"the meta run records an unknown inner_class_weights: {inner_weights}")
    block, adapted = adapt_and_score(model, state, fresh, task, shots=shots, eval_query=args.eval_query, min_query=args.min_query_samples,
                                     steps=steps, lr=lr, gap=gap, seed=document["config"]["seed"], inner_class_weights=inner_weights)
    _check_deadline(deadline)
    if args.export_onnx and adapted is None:
        raise PrerequisiteError("cannot export: the symbol has too few samples for a support and query block")
    config = dict(meta_run_config_sha256=document["config_sha256"], meta_init_sha256=document["meta_init"]["sha256"], symbol=args.symbol,
                  shots=shots, adapt_steps=steps, inner_lr=lr, eval_query=args.eval_query, min_query_samples=args.min_query_samples,
                  oos_tail_days=args.oos_tail_days, export_onnx=bool(args.export_onnx),
                  data_revision=checkpoints.source_revision({args.symbol: next(iter(symbols.values()))}, {args.symbol: next(iter(dates.values()))}))
    if inner_weights != INNER_WEIGHTS_NONE:
        config["inner_class_weights"] = inner_weights
    config_sha256 = _sha_of(config)

    def write(folder: Path) -> None:
        export = dict(requested=bool(args.export_onnx))
        if args.export_onnx and adapted is not None:
            model.load_state_dict({k: v.detach() for k, v in adapted.items()})
            model.eval()
            support_dates, query_dates = block["support_dates"], block["query_dates"]
            contract = onnx_meta.build_contract(
                feature_mode=model_info["feature_mode"], window_size=model_info["window"], channels=channels, horizon=model_info["horizon"],
                threshold=model_info["threshold"], wf_splits=1, seed=document["config"]["seed"], producer=f"{TOOL} {document['algorithm']} adapt",
                gap=gap, timeframe=model_info["timeframe"], date_ranges=dict(zip(ds.DATE_RANGE_KEYS, (support_dates[0], support_dates[1],
                                                                                                    query_dates[0], query_dates[1]))))
            path = folder / ADAPTED_ONNX_NAME
            tp.export_onnx(model, channels, model_info["window"], path, OPSET_DEFAULT, contract)
            tp.verify_onnx(path, model, channels, model_info["window"])
            import onnx
            metadata = {p.key: p.value for p in onnx.load(str(path)).metadata_props}
            base, expanded = model_analysis.channel_names(metadata)
            export.update(file=ADAPTED_ONNX_NAME, sha256=checkpoints.hash_file(path), registered=False, activated=False,
                          header=explain.make_header(model_sha256=checkpoints.hash_file(path), data_revision=config["data_revision"], metadata=metadata,
                                                     timeframe=model_info["timeframe"], window_size=model_info["window"], base_channels=base,
                                                     ordered_channels=expanded))
        adapt_document = dict(
            schema_version=SCHEMA_VERSION, tool=TOOL, tool_version=TOOL_VERSION, created_utc=_utc_now().isoformat(),
            elapsed_seconds=round(time.monotonic() - started, ELAPSED_DIGITS), status=block["status"], kind=KIND_ADAPT,
            meta_run=dict(config_sha256=document["config_sha256"], meta_init_sha256=document["meta_init"]["sha256"], algorithm=document["algorithm"]),
            config=config, config_sha256=config_sha256, thresholds_basis=document["thresholds_basis"], rng_policy=RNG_POLICY,
            result=block, export=export,
            independence=dict(symbol_not_in_meta_train=True, support_before_query=True, purge_gap=gap, oos_tail_read=False, oos_consumed=False),
            notices=list(NOTICES), verification=dict(real_world_validation="unverified"))
        _write_json(folder / ADAPT_DOC_NAME, adapt_document)

    final = publish_run(args.out_root, KIND_ADAPT, config_sha256, write)
    print(f"wrote {final} (status={block['status']})")
    return final


# --- CLI -----------------------------------------------------------------------------------------------------------------------------


def _add_common(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--data-dir", type=Path, default=ds.DEFAULT_DATA_DIR)
    parser.add_argument("--data-start", default=None, help="Inclusive first calendar date (YYYY-MM-DD).")
    parser.add_argument("--data-end", default=None, help="Inclusive last calendar date (YYYY-MM-DD).")
    parser.add_argument("--timeframe", choices=tuple(ds.TIMEFRAME_DIRS), default=SUPPORTED_TIMEFRAME)
    parser.add_argument("--out-root", type=Path, default=ds.DATA_ROOT / "TrainingArtifacts")
    parser.add_argument("--max-seconds", type=_positive_int, default=MAX_SECONDS, help=f"Run-duration bound (default {MAX_SECONDS}, env {MAX_SECONDS_ENV}).")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="T23 meta-learning (Reptile / MAML) over per-symbol tasks.")
    sub = parser.add_subparsers(dest="command", required=True)
    train = sub.add_parser("train", help="Learn and publish a meta-initialization.")
    _add_common(train)
    train.add_argument("--symbols", type=_csv, required=True, help="Meta-train symbols (comma separated); one symbol is one task.")
    train.add_argument("--meta-test-symbols", type=_csv, default=[], help="Held-out symbols adapted and scored after training (disjoint from --symbols).")
    train.add_argument("--oos-tail-days", type=_positive_int, required=True, help="Calendar days of the out-of-sample tail that is never read (>= 1).")
    train.add_argument("--algorithm", choices=ALGORITHMS, default=ALGORITHM_REPTILE)
    train.add_argument("--second-order", action="store_true", help="MAML only: differentiate through the inner steps.")
    train.add_argument("--arch", choices=tp.ARCHES, default=tp.DEFAULT_ARCH)
    train.add_argument("--feature-mode", choices=ds.FEATURE_MODES, default="ohlcv_minmax")
    train.add_argument("--window", type=_positive_int, default=ds.DEFAULT_WINDOW)
    train.add_argument("--horizon", type=_positive_int, default=ds.DEFAULT_HORIZON)
    train.add_argument("--threshold", type=_finite_float, default=ds.DEFAULT_THRESHOLD)
    train.add_argument("--gap", type=int, default=None, help="Purge gap in bars (default: dataset.resolve_purge_gap).")
    train.add_argument("--hidden", type=_positive_int, default=tp.DEFAULT_HIDDEN)
    train.add_argument("--layers", type=_positive_int, default=tp.DEFAULT_LAYERS)
    train.add_argument("--dropout", type=_finite_float, default=DEFAULT_DROPOUT)
    train.add_argument("--seed", type=int, default=DEFAULT_SEED)
    train.add_argument("--inner-steps", type=_positive_int, default=DEFAULT_INNER_STEPS)
    train.add_argument("--inner-lr", type=_finite_float, default=DEFAULT_INNER_LR)
    train.add_argument("--meta-lr", type=_finite_float, default=None, help=f"MAML outer Adam rate (default {DEFAULT_META_LR}).")
    train.add_argument("--reptile-epsilon", type=_finite_float, default=None, help=f"Reptile outer step size (default {DEFAULT_REPTILE_EPSILON}).")
    train.add_argument("--meta-batch-tasks", type=_positive_int, default=DEFAULT_META_BATCH_TASKS)
    train.add_argument("--support-size", type=_positive_int, default=DEFAULT_SUPPORT_SIZE)
    train.add_argument("--query-size", type=_positive_int, default=DEFAULT_QUERY_SIZE)
    train.add_argument("--meta-iterations", type=_positive_int, default=DEFAULT_META_ITERATIONS)
    train.add_argument("--min-task-samples", type=_positive_int, default=DEFAULT_MIN_TASK_SAMPLES)
    train.add_argument("--shots", type=_positive_int, default=DEFAULT_SHOTS, help="Support samples of a held-out symbol.")
    train.add_argument("--adapt-steps", type=_positive_int, default=None, help="Adaptation steps of the held-out evaluation (default: --inner-steps).")
    train.add_argument("--eval-query", type=_positive_int, default=DEFAULT_EVAL_QUERY)
    train.add_argument("--min-query-samples", type=_positive_int, default=DEFAULT_MIN_QUERY_SAMPLES)
    train.add_argument("--eval-episodes", type=_positive_int, default=DEFAULT_EVAL_EPISODES,
                       help=f"Chronological support+query episodes per held-out symbol (default {DEFAULT_EVAL_EPISODES}; at most {Limits.MAX_EVAL_EPISODES}).")
    train.add_argument("--inner-class-weights", choices=INNER_WEIGHTS_CHOICES, default=INNER_WEIGHTS_NONE,
                       help="inverse_frequency weights the inner (support) loss by 1/N_c of the episode's support labels; the query loss and the scores stay plain.")
    train.add_argument("--task-sampling", choices=SAMPLING_CHOICES, default=SAMPLING_WITH_REPLACEMENT,
                       help="without_replacement draws the tasks of a meta-batch without replacement when there are at least --meta-batch-tasks tasks.")
    adapt = sub.add_parser("adapt", help="Adapt a published meta-initialization to one new symbol.")
    _add_common(adapt)
    adapt.add_argument("--meta-run", type=Path, required=True, help="A published <DataRoot>/TrainingArtifacts/Meta/<runId> folder.")
    adapt.add_argument("--symbol", required=True)
    adapt.add_argument("--oos-tail-days", type=_positive_int, default=None, help="Default: the meta run's value.")
    adapt.add_argument("--shots", type=_positive_int, default=None)
    adapt.add_argument("--adapt-steps", type=_positive_int, default=None)
    adapt.add_argument("--inner-lr", type=_finite_float, default=None)
    adapt.add_argument("--eval-query", type=_positive_int, default=DEFAULT_EVAL_QUERY)
    adapt.add_argument("--min-query-samples", type=_positive_int, default=DEFAULT_MIN_QUERY_SAMPLES)
    adapt.add_argument("--export-onnx", action="store_true", help="Also export the adapted model as an ordinary single-output ONNX (not registered).")
    return parser


def parse_args(argv: Sequence[str]) -> argparse.Namespace:
    parser = build_parser()
    args = parser.parse_args(list(argv))
    if args.max_seconds > 86_400:
        parser.error("--max-seconds is above the hard bound of 86400")
    if args.command == "train":
        if not args.symbols:
            parser.error("--symbols must name at least one symbol")
        if len(set(args.symbols)) != len(args.symbols) or len(set(args.meta_test_symbols)) != len(args.meta_test_symbols):
            parser.error("a symbol is listed twice")
        if args.second_order and args.algorithm != ALGORITHM_MAML:
            parser.error("--second-order applies to --algorithm maml only (Reptile has no second derivative)")
        if args.algorithm == ALGORITHM_REPTILE and args.meta_lr is not None:
            parser.error("--meta-lr applies to --algorithm maml only")
        if args.algorithm == ALGORITHM_MAML and args.reptile_epsilon is not None:
            parser.error("--reptile-epsilon applies to --algorithm reptile only")
        args.meta_lr = DEFAULT_META_LR if args.meta_lr is None else args.meta_lr
        args.reptile_epsilon = DEFAULT_REPTILE_EPSILON if args.reptile_epsilon is None else args.reptile_epsilon
        args.adapt_steps = args.inner_steps if args.adapt_steps is None else args.adapt_steps
        if args.inner_lr <= 0 or args.meta_lr <= 0:
            parser.error("learning rates must be positive")
        if not 0 < args.reptile_epsilon <= 1:
            parser.error("--reptile-epsilon must be in (0, 1]")
        if not 0 <= args.dropout < 1:
            parser.error("--dropout must be in [0, 1)")
        if args.threshold < 0:
            parser.error("--threshold must be non-negative")
        if args.gap is not None and args.gap < 0:
            parser.error("--gap must be non-negative")
        bounds = (("--meta-iterations", args.meta_iterations, Limits.MAX_ITERATIONS), ("--inner-steps", args.inner_steps, Limits.MAX_INNER_STEPS),
                  ("--adapt-steps", args.adapt_steps, Limits.MAX_INNER_STEPS), ("--meta-batch-tasks", args.meta_batch_tasks, Limits.MAX_META_BATCH),
                  ("--support-size", args.support_size, Limits.MAX_EPISODE_SAMPLES), ("--query-size", args.query_size, Limits.MAX_EPISODE_SAMPLES),
                  ("--shots", args.shots, Limits.MAX_EPISODE_SAMPLES), ("--eval-query", args.eval_query, Limits.MAX_EPISODE_SAMPLES),
                  ("--eval-episodes", args.eval_episodes, Limits.MAX_EVAL_EPISODES))
    else:
        bounds = (("--eval-query", args.eval_query, Limits.MAX_EPISODE_SAMPLES), ("--shots", args.shots or 1, Limits.MAX_EPISODE_SAMPLES),
                  ("--adapt-steps", args.adapt_steps or 1, Limits.MAX_INNER_STEPS))
        if args.inner_lr is not None and args.inner_lr <= 0:
            parser.error("--inner-lr must be positive")
    for name, value, limit in bounds:
        if value > limit:
            parser.error(f"{name} {value} is above the safety bound of {limit}")
    return args


def main(argv: Sequence[str]) -> int:
    args = parse_args(argv)
    try:
        if args.command == "train":
            run_train(args)
        else:
            run_adapt(args)
        return EXIT_OK
    except PrerequisiteError as exc:
        print(f"prerequisite: {exc}", file=sys.stderr)
        return EXIT_PREREQUISITE
    except (TimeoutError, ArithmeticError) as exc:
        print(f"failed: {exc}", file=sys.stderr)
        return EXIT_FAILED


# --- self-verification ---------------------------------------------------------------------------------------------------------------


def _random_walk(rows: int, seed: int) -> Tuple[np.ndarray, np.ndarray]:
    rng = np.random.default_rng(seed)
    close = 100.0 * np.exp(np.cumsum(rng.normal(0.0003, 0.015, rows)))
    open_ = close * np.exp(rng.normal(0.0, 0.004, rows))
    high = np.maximum(open_, close) * (1.0 + np.abs(rng.normal(0.0, 0.005, rows)))
    low = np.minimum(open_, close) * (1.0 - np.abs(rng.normal(0.0, 0.005, rows)))
    volume = rng.integers(1_000, 50_000, rows).astype(np.float64)
    dates = (np.datetime64("2022-01-03") + np.arange(rows)).astype("datetime64[D]")
    return np.stack([open_, high, low, close, volume], axis=1), dates


def _write_parquet(folder: Path, names: Sequence[str], rows: int) -> None:
    import polars as pl
    folder.mkdir(parents=True, exist_ok=True)
    for index, name in enumerate(names):
        values, dates = _random_walk(rows, 500 + index)
        frame = pl.DataFrame({"date": [d.astype(datetime.date) for d in dates], "open": values[:, 0], "high": values[:, 1], "low": values[:, 2],
                              "close": values[:, 3], "volume": values[:, 4]})
        frame.write_parquet(folder / f"{name}.parquet")


def _expect_exit(argv: Sequence[str], code: int) -> None:
    try:
        parse_args(argv)
    except SystemExit as exc:
        assert exc.code == code, f"{argv} exited {exc.code}, expected {code}"
        return
    raise AssertionError(f"{list(argv)} must be rejected")


def _tiny_spec(algorithm: str, **override) -> TrainSpec:
    base = dict(algorithm=algorithm, second_order=False, inner_steps=2, inner_lr=0.05, meta_lr=0.01, reptile_epsilon=0.5, meta_batch_tasks=2,
                support_size=8, query_size=8, meta_iterations=3, gap=9, seed=11)
    base.update(override)
    return TrainSpec(**base)


def _tiny_tasks(count: int, rows: int = 120, window: int = 8, horizon: int = 2, oos: int = 10) -> List[Task]:
    out = []
    for index in range(count):
        values, dates = _random_walk(rows, 40 + index)
        end = dates[-1].astype(datetime.date)
        task = build_task(f"S{index}", values, dates, feature_mode="ohlcv_minmax", window=window, horizon=horizon, threshold=0.005,
                          oos_tail_days=oos, common_end=end)
        assert task is not None
        out.append(task)
    return out


def _run_selfcheck() -> None:
    torch.manual_seed(0)
    base = ["train", "--symbols", "A,B", "--oos-tail-days", "30"]
    ok = parse_args(base)
    assert ok.algorithm == ALGORITHM_REPTILE and ok.adapt_steps == DEFAULT_INNER_STEPS and ok.meta_lr == DEFAULT_META_LR

    # (a) argument validation: type, range, algorithm-specific options, bounds, independence of the lists
    for bad in (base + ["--inner-steps", "0"], base + ["--inner-steps", "True"], base + ["--inner-steps", "2.5"], base + ["--inner-lr", "nan"],
                base + ["--inner-lr", "-0.1"], base + ["--inner-lr", "inf"], base + ["--algorithm", "sgd"], base + ["--second-order"],
                base + ["--meta-lr", "0.1"], base + ["--algorithm", "maml", "--reptile-epsilon", "0.1"], base + ["--reptile-epsilon", "0"],
                base + ["--reptile-epsilon", "1.5"], base + ["--dropout", "1"], base + ["--meta-iterations", str(Limits.MAX_ITERATIONS + 1)],
                base + ["--support-size", str(Limits.MAX_EPISODE_SAMPLES + 1)], base + ["--max-seconds", "86401"], base + ["--gap", "-1"],
                ["train", "--symbols", "A,A", "--oos-tail-days", "30"], ["train", "--symbols", "A"], ["train", "--symbols", "A", "--oos-tail-days", "0"],
                ["adapt", "--meta-run", "x"], base + ["--feature-mode", "composed_features"],
                base + ["--eval-episodes", str(Limits.MAX_EVAL_EPISODES + 1)], base + ["--eval-episodes", "0"], base + ["--eval-episodes", "2.5"],
                base + ["--inner-class-weights", "balanced"], base + ["--task-sampling", "stratified"]):
        _expect_exit(bad, 2)
    defaults = parse_args(base)
    assert (defaults.eval_episodes, defaults.inner_class_weights, defaults.task_sampling) == (1, INNER_WEIGHTS_NONE, SAMPLING_WITH_REPLACEMENT)
    assert parse_args(base + ["--eval-episodes", str(Limits.MAX_EVAL_EPISODES)]).eval_episodes == Limits.MAX_EVAL_EPISODES
    assert parse_args(base + ["--algorithm", "maml", "--second-order"]).second_order is True

    # (b) task independence: support before query, disjoint information intervals, no row at or after the out-of-sample cutoff
    window, horizon, oos = 8, 2, 10
    gap = ds.resolve_purge_gap(window, horizon)
    values, dates = _random_walk(120, 3)
    end = dates[-1].astype(datetime.date)
    task = build_task("T", values, dates, feature_mode="ohlcv_minmax", window=window, horizon=horizon, threshold=0.005, oos_tail_days=oos, common_end=end)
    cutoff = np.datetime64(ds.resolve_oos_cutoff(end, oos))
    main_rows = int((dates <= cutoff).sum())
    assert task is not None and int(task.anchor_rows.max()) + horizon < main_rows and (task.dates <= cutoff).all(), "the OOS tail must not be read"
    starts = episode_starts(task.anchor_rows, 8, 8, gap)
    assert starts.size > 0
    for start in starts[:: max(1, starts.size // 25)]:
        support, query = episode_indices(task.anchor_rows, int(start), 8, 8, gap)
        a_s, a_q = task.anchor_rows[support], task.anchor_rows[query]
        assert support.max() < query.min() and a_q.min() - window + 1 > a_s.max() + horizon, "information intervals of support and query intersect"
    assert not np.isin(support, query).any()

    # (c) few / empty / single tasks (M5)
    tiny = {"OK": Task("OK", task.x, task.y, task.anchor_rows, task.dates), "NONE": None,
            "SHORT": Task("SHORT", task.x[:5], task.y[:5], task.anchor_rows[:5], task.dates[:5]),
            "NOFIT": Task("NOFIT", task.x[:20], task.y[:20], task.anchor_rows[:20], task.dates[:20])}
    usable, records, skipped = classify_tasks(tiny, support_size=8, query_size=8, gap=gap, min_task_samples=16)
    reasons = {s["symbol"]: s["reason"] for s in skipped}
    assert [t.symbol for t in usable] == ["OK"] and reasons == {"NONE": "no_main_block", "SHORT": "below_min_task_samples", "NOFIT": "episode_does_not_fit"}
    assert classify_tasks({"NONE": None}, support_size=8, query_size=8, gap=gap, min_task_samples=1)[0] == []

    # (d) inner update on a closed form: loss (w-2)^2, grad 2(w-2): w 0 -> 0.4 -> 0.72 for lr 0.1
    lin = nn.Linear(1, 1, bias=False).double()
    with torch.no_grad():
        lin.weight.zero_()
    mse = lambda out, tgt: ((out - tgt) ** 2).mean()  # noqa: E731
    x1, y1 = torch.ones(1, 1, dtype=torch.float64), torch.full((1, 1), 2.0, dtype=torch.float64)
    p0 = dict(lin.named_parameters())
    assert math.isclose(float(detached_adapt(lin, p0, mse, x1, y1, 1, 0.1)["weight"]), 0.4, rel_tol=1e-12)
    assert math.isclose(float(detached_adapt(lin, p0, mse, x1, y1, 2, 0.1)["weight"]), 0.72, rel_tol=1e-12)
    assert float(lin.weight.detach()) == 0.0, "the inner update must not modify the model"
    # Reptile outer step by hand: theta 1.0, adapted 3.0 and 5.0, epsilon 0.5 -> 1 + 0.5 * mean(2, 4) = 2.5
    out = reptile_interpolate({"w": torch.tensor([1.0])}, [{"w": torch.tensor([3.0])}, {"w": torch.tensor([5.0])}], 0.5)
    assert math.isclose(float(out["w"]), 2.5, rel_tol=1e-12)

    # (e) MAML gradients: second order equals a central finite difference of the meta-objective; first order differs
    def meta_objective(theta: float, steps: int) -> float:
        with torch.no_grad():
            lin.weight.fill_(theta)
        fast = detached_adapt(lin, dict(lin.named_parameters()), mse, x1, y1, steps, 0.1)
        return float(mse(functional_call(lin, fast, (x1 * 3.0,)), y1 * 0.5))

    for steps in (1, 2):
        with torch.no_grad():
            lin.weight.fill_(0.7)
        params = dict(lin.named_parameters())
        second = inner_adapt(lin, params, mse, x1, y1, steps, 0.1, create_graph=True)
        grad2 = float(torch.autograd.grad(mse(functional_call(lin, second, (x1 * 3.0,)), y1 * 0.5), params["weight"])[0])
        first = inner_adapt(lin, params, mse, x1, y1, steps, 0.1, create_graph=False)
        grad1 = float(torch.autograd.grad(mse(functional_call(lin, first, (x1 * 3.0,)), y1 * 0.5), params["weight"])[0])
        h = 1e-6
        numeric = (meta_objective(0.7 + h, steps) - meta_objective(0.7 - h, steps)) / (2 * h)
        assert math.isclose(grad2, numeric, rel_tol=1e-5), (steps, grad2, numeric)
        assert not math.isclose(grad1, numeric, rel_tol=1e-3), "first order must differ from the exact meta-gradient"

    # (f) reproducibility (C5): the same seed gives bit-identical weights, another seed does not, per algorithm; the draw ignores the other tasks
    tasks3 = _tiny_tasks(3)

    def trained(algorithm: str, seed: int, pool: Sequence[Task], **override) -> Dict[str, torch.Tensor]:
        torch.manual_seed(seed)
        net = tp.build_model("lstm", 5, 4, 1, 0.0, NUM_CLASSES)
        summary = meta_train(net, pool, _tiny_spec(algorithm, seed=seed, **override))
        assert summary["completed_iterations"] == 3 and np.isfinite(summary["query_loss_last"])
        return {k: v.detach().clone() for k, v in net.state_dict().items()}

    for algorithm, extra in ((ALGORITHM_REPTILE, {}), (ALGORITHM_MAML, {}), (ALGORITHM_MAML, {"second_order": True})):
        a, b, c = trained(algorithm, 5, tasks3, **extra), trained(algorithm, 5, tasks3, **extra), trained(algorithm, 6, tasks3, **extra)
        assert all(torch.equal(a[k], b[k]) for k in a), f"{algorithm} {extra} is not reproducible"
        assert any(not torch.equal(a[k], c[k]) for k in a), f"{algorithm} ignores the seed"
    spec = _tiny_spec(ALGORITHM_REPTILE)
    xs_a = _episode_tensors(tasks3[0], spec, 1, 0)[0]
    xs_b = _episode_tensors(tasks3[0], spec, 1, 0)[0]
    assert torch.equal(xs_a, xs_b), "an episode must depend on (seed, iteration, slot, symbol) only"

    # (f2) defaults are bit-identical to the code before --eval-episodes / --inner-class-weights / --task-sampling existed. The values were taken from that code with
    # the same data and seeds (CPU, float32); they pin this machine's torch numerics, so regenerate them if a torch upgrade changes the arithmetic.
    def digest(tensors: Dict[str, torch.Tensor]) -> str:
        h = hashlib.sha256()
        for name in sorted(tensors):
            h.update(name.encode("ascii"))
            h.update(tensors[name].detach().cpu().numpy().astype(np.float32).tobytes())
        return h.hexdigest()

    pre_change = {(ALGORITHM_REPTILE, False): "b272b7d910ebe1a756d2ec367e07c7132507214f16ed13419b21304096b2d30d",
                  (ALGORITHM_MAML, False): "ed9a373d1b1d375017813b683a44e0ee1a6e65732dd52010786c0144f6b75480",
                  (ALGORITHM_MAML, True): "5c774b62457359f380406a85b4ac42685c5b3ca2c57fd85ad206c04246e1eef9"}
    for (algorithm, second), expected_digest in pre_change.items():
        torch.manual_seed(5)
        pinned = tp.build_model("lstm", 5, 4, 1, 0.0, NUM_CLASSES)
        meta_train(pinned, tasks3, _tiny_spec(algorithm, seed=5, second_order=second))
        assert digest(pinned.state_dict()) == expected_digest, f"{algorithm} second_order={second}: the default meta-initialization changed"

    torch.manual_seed(5)
    seeded = tp.build_model("lstm", 5, 4, 1, 0.0, NUM_CLASSES)
    meta_train(seeded, tasks3, _tiny_spec(ALGORITHM_REPTILE, seed=5))
    seeded_state = {k: v.detach().cpu().clone() for k, v in seeded.state_dict().items()}
    held_out = _tiny_tasks(4)[3]

    def fresh_model() -> nn.Module:
        return tp.build_model("lstm", 5, 4, 1, 0.0, NUM_CLASSES)

    single_kwargs = dict(shots=8, eval_query=40, min_query=10, steps=3, lr=0.05, gap=9, seed=11)
    single, single_adapted = adapt_and_score(seeded, seeded_state, fresh_model, held_out, **single_kwargs)
    expected_block = dict(symbol="S3", shots=8, adapt_steps=3, inner_lr=0.05, purge_gap=9, status=STATUS_AVAILABLE, n_support=8, n_query=40,
                          support_dates=["2022-01-10", "2022-01-17"], query_dates=["2022-01-27", "2022-03-07"])
    expected_scores = {CONDITION_UNADAPTED: (0.325, 0.16352201257861637, -0.024999999999999967, 1.1333475368892258),
                       CONDITION_ADAPTED: (0.325, 0.16352201257861637, -0.024999999999999967, 1.1253772092612617),
                       CONDITION_RANDOM: (0.35, 0.1728395061728395, 0.0, 1.1054224479221948)}
    assert list(single) == list(expected_block) + ["conditions"], "the single-episode block must keep its keys (no episodes / summary)"
    assert all(single[k] == v for k, v in expected_block.items())
    for condition, values in expected_scores.items():
        got = single["conditions"][condition]
        assert got["n_samples"] == 40 and got["majority_baseline_accuracy"] == 0.35
        assert all(math.isclose(got[m], v, rel_tol=0, abs_tol=1e-12) for m, v in zip(EVAL_SUMMARY_METRICS, values)), condition
    adapted_bytes = b"".join(single_adapted[k].numpy().astype(np.float32).tobytes() for k in sorted(single_adapted))
    assert hashlib.sha256(adapted_bytes).hexdigest() == "9b725f6feccea2e763012454c5d5b38acf46ac9cb9a631aae15acfc39f75a878"

    # (f3) several evaluation episodes: stride, validity, the summary by hand, episode 0 = the single episode, a shorter history
    episode_kwargs = dict(shots=8, eval_query=20, min_query=10, steps=3, lr=0.05, gap=9, seed=11)
    stride = 8 + 9 + 20
    three, _ = adapt_and_score_episodes(seeded, seeded_state, fresh_model, held_out, episodes=3, **episode_kwargs)
    assert three["status"] == STATUS_AVAILABLE and [e["index"] for e in three["episodes"]] == [0, 1, 2]
    assert all(e["status"] == STATUS_AVAILABLE for e in three["episodes"])
    for e in three["episodes"]:
        start = e["index"] * stride
        assert e["support_dates"] == [str(held_out.dates[start]), str(held_out.dates[start + 7])], "episode k supports from sample k * stride"
        assert e["n_support"] == 8 and 10 <= e["n_query"] <= 20 and set(e["conditions"]) == set(EVAL_CONDITIONS)
    zero, _ = adapt_and_score(seeded, seeded_state, fresh_model, held_out, **episode_kwargs)
    assert three["conditions"] == zero["conditions"] and three["support_dates"] == zero["support_dates"], "the first episode is the single-episode evaluation"
    assert three["episodes"][0]["conditions"] == zero["conditions"]
    for condition in EVAL_CONDITIONS:
        for metric in EVAL_SUMMARY_METRICS:
            values = [e["conditions"][condition][metric] for e in three["episodes"]]
            cell = three["summary"][condition][metric]
            assert cell["n_valid"] == 3 and abs(cell["mean"] - statistics.fmean(values)) <= 1e-12 and abs(cell["std"] - statistics.stdev(values)) <= 1e-12
    assert three["episodes"][1]["conditions"] != three["episodes"][0]["conditions"], "another episode is another data block"
    four, _ = adapt_and_score_episodes(seeded, seeded_state, fresh_model, held_out, episodes=4, **episode_kwargs)
    assert four["episodes"][3]["status"] == STATUS_INSUFFICIENT and "conditions" not in four["episodes"][3] and four["status"] == STATUS_AVAILABLE
    assert four["summary"][CONDITION_ADAPTED]["accuracy"]["n_valid"] == 3 and four["episodes"][:3] == three["episodes"], "an episode beyond the data is left out"
    none_valid, none_adapted = adapt_and_score_episodes(seeded, seeded_state, fresh_model, held_out, episodes=2, **{**episode_kwargs, "min_query": 10_000})
    assert none_valid["status"] == STATUS_INSUFFICIENT and none_adapted is None and "conditions" not in none_valid
    assert none_valid["summary"][CONDITION_ADAPTED]["accuracy"] == dict(mean=None, std=None, n_valid=0)
    one_valid, _ = adapt_and_score_episodes(seeded, seeded_state, fresh_model, held_out, episodes=1, **episode_kwargs)
    assert one_valid["summary"][CONDITION_ADAPTED]["accuracy"]["std"] is None and one_valid["summary"][CONDITION_ADAPTED]["accuracy"]["n_valid"] == 1

    # (f4) inner class weights: 1/N_c by hand, the weighted loss by hand, a class that does not occur gets 0, a rare class gains from the weighting
    weights = inverse_frequency_weights(torch.tensor([0, 1, 2, 2, 2, 2]))
    assert torch.allclose(weights, torch.tensor([1.0, 1.0, 0.25])), weights
    assert torch.allclose(inverse_frequency_weights(torch.tensor([0, 0])), torch.tensor([0.5, 0.0, 0.0]))
    labels = torch.tensor([0, 1, 2, 2, 2, 2])
    logits = torch.tensor([[2.0, 0.0, 0.0], [0.0, 1.0, 0.0], [0.0, 0.0, 3.0], [1.0, 0.0, 2.0], [0.5, 0.5, 0.5], [0.0, 2.0, 0.0]])
    nll = -torch.log_softmax(logits, dim=-1)[torch.arange(6), labels]
    by_hand = float((weights[labels] * nll).sum() / weights[labels].sum())
    assert math.isclose(float(support_loss(labels, INNER_WEIGHTS_INVERSE_FREQUENCY)(logits, labels)), by_hand, rel_tol=1e-6)
    assert support_loss(labels, INNER_WEIGHTS_NONE) is cross_entropy
    gen = torch.Generator().manual_seed(0)
    imbalanced_x = torch.randn(32, 4, generator=gen)
    imbalanced_y = torch.tensor([0] + [1] + [2] * 30)
    linear = nn.Linear(4, NUM_CLASSES)
    with torch.no_grad():
        linear.weight.zero_()
        linear.bias.zero_()
    start_params = dict(linear.named_parameters())

    def minority_probability(mode: str) -> float:
        tuned = detached_adapt(linear, start_params, support_loss(imbalanced_y, mode), imbalanced_x, imbalanced_y, 5, 0.1)
        probs = torch.softmax(functional_call(linear, tuned, (imbalanced_x,)), dim=-1)
        return float(probs[:, :2].sum(dim=-1).mean())

    assert minority_probability(INNER_WEIGHTS_INVERSE_FREQUENCY) > minority_probability(INNER_WEIGHTS_NONE), "weighting must raise the rare classes"
    weighted_pool = trained(ALGORITHM_REPTILE, 5, tasks3, inner_class_weights=INNER_WEIGHTS_INVERSE_FREQUENCY)
    plain = trained(ALGORITHM_REPTILE, 5, tasks3)
    assert any(not torch.equal(weighted_pool[k], plain[k]) for k in plain), "the option must reach the inner steps"

    # (f5) task sampling: without replacement gives distinct tasks when there are enough, the old draw otherwise and by default
    for iteration in range(50):
        distinct = choose_tasks(7, iteration, 3, 2, SAMPLING_WITHOUT_REPLACEMENT)
        assert distinct.shape == (2,) and len(set(distinct.tolist())) == 2 and distinct.min() >= 0 and distinct.max() < 3
    assert np.array_equal(choose_tasks(7, 4, 3, 4, SAMPLING_WITHOUT_REPLACEMENT), _generator(7, STREAM_TASK_CHOICE, 4).integers(0, 3, size=4))
    assert np.array_equal(choose_tasks(7, 4, 3, 2, SAMPLING_WITH_REPLACEMENT), _generator(7, STREAM_TASK_CHOICE, 4).integers(0, 3, size=2))

    # (g) atomic publication: a failing writer leaves nothing, an existing run is never reused
    with tempfile.TemporaryDirectory() as raw:
        root = Path(raw)
        when = datetime.datetime(2026, 1, 1, tzinfo=datetime.timezone.utc)

        def boom(folder: Path) -> None:
            (folder / "partial.txt").write_text("x")
            raise RuntimeError("writer failed")

        try:
            publish_run(root, KIND_TRAIN, "a" * 64, boom, now=when)
        except RuntimeError:
            pass
        else:
            raise AssertionError("the writer error must propagate")
        assert not any((root / KIND_TRAIN).iterdir()), "a failed run must leave no folder"
        first = publish_run(root, KIND_TRAIN, "a" * 64, lambda f: (f / "ok.txt").write_text("1"), now=when)
        assert first.name == "20260101T000000Z_aaaaaaaa" and (first / "ok.txt").is_file()
        try:
            publish_run(root, KIND_TRAIN, "a" * 64, lambda f: None, now=when)
        except PrerequisiteError:
            pass
        else:
            raise AssertionError("an existing run must not be reused")

    # (h) end to end on observed-like parquet: train, verify, adapt with ONNX export, independence and synthetic refusal
    with tempfile.TemporaryDirectory() as raw:
        root = Path(raw)
        data = root / "data"
        _write_parquet(data, ["AAA", "BBB", "CCC", "DDD"], 220)
        common = ["--data-dir", str(data), "--out-root", str(root / "out"), "--oos-tail-days", "20", "--window", "10", "--horizon", "2",
                  "--hidden", "4", "--layers", "1", "--support-size", "8", "--query-size", "8", "--min-task-samples", "16", "--meta-iterations", "3",
                  "--meta-batch-tasks", "2", "--shots", "8", "--min-query-samples", "10", "--eval-query", "40", "--seed", "3"]
        for algorithm in ("reptile", "maml"):
            argv = ["train", "--symbols", "AAA,BBB,CCC", "--meta-test-symbols", "DDD", "--algorithm", algorithm] + common
            assert main(argv) == EXIT_OK
        runs = sorted((root / "out" / KIND_TRAIN).iterdir())
        assert len(runs) == 2 and not any(r.name.endswith(INCOMING_SUFFIX) for r in runs)
        meta_doc = json.loads((runs[0] / META_DOC_NAME).read_text(encoding="utf-8"))
        assert meta_doc["data"]["oos_consumed"] is False and meta_doc["independence"]["oos_tail_read"] is False
        assert meta_doc["rng_policy"] == RNG_POLICY and meta_doc["thresholds_basis"] == THRESHOLDS_BASIS_USER
        assert meta_doc["meta_test"][0]["status"] == STATUS_AVAILABLE and set(meta_doc["meta_test"][0]["conditions"]) == {
            "meta_init_unadapted", "meta_init_adapted", "random_init_adapted"}
        assert meta_doc["tasks"] and not meta_doc["single_task"]
        # defaults: the same run id, weights and meta-test block as before the new options, and the new notice
        reptile_run = next(r for r in runs if json.loads((r / META_DOC_NAME).read_text(encoding="utf-8"))["algorithm"] == ALGORITHM_REPTILE)
        default_doc = json.loads((reptile_run / META_DOC_NAME).read_text(encoding="utf-8"))
        assert default_doc["config_sha256"] == "f385d5bc1ceb948887c50406311266e4a96b3d21dbfcaa7af06639ba608e232b", "a default run must keep its config hash"
        assert digest(torch.load(reptile_run / META_INIT_NAME, map_location="cpu", weights_only=True)) == "7254e8d106e5f673b05b8faf82f74b7a738532c9830e5b740b90e7118abc8467"
        assert not {"eval_episodes", "inner_class_weights", "task_sampling"} & set(default_doc["config"])
        assert "episodes" not in default_doc["meta_test"][0] and "summary" not in default_doc["meta_test"][0]
        assert NOTICES[-1] == "meta_train_episodes_may_postdate_meta_test_period" and default_doc["notices"] == list(NOTICES)
        loaded_doc, loaded_state = load_meta_run(runs[0])
        assert loaded_doc["meta_init"]["sha256"] == checkpoints.hash_file(runs[0] / META_INIT_NAME) and loaded_state
        # no row at or after the cutoff is part of any task: the recorded common end and the tail length bound the data
        assert meta_doc["data"]["common_end"] and meta_doc["data"]["oos_tail_days"] == 20
        # the new options: recorded only when set, the held-out symbol gets its episodes and summary
        assert main(["train", "--symbols", "AAA,BBB,CCC", "--meta-test-symbols", "DDD", "--eval-episodes", "2", "--inner-class-weights", "inverse_frequency",
                     "--task-sampling", "without_replacement"] + common) == EXIT_OK
        optioned_run = next(r for r in sorted((root / "out" / KIND_TRAIN).iterdir())
                            if "eval_episodes" in json.loads((r / META_DOC_NAME).read_text(encoding="utf-8"))["config"])
        optioned = json.loads((optioned_run / META_DOC_NAME).read_text(encoding="utf-8"))
        assert (optioned["config"]["eval_episodes"], optioned["config"]["inner_class_weights"], optioned["config"]["task_sampling"]) == (
            2, INNER_WEIGHTS_INVERSE_FREQUENCY, SAMPLING_WITHOUT_REPLACEMENT) and optioned["thresholds_basis"] == THRESHOLDS_BASIS_USER
        assert [e["index"] for e in optioned["meta_test"][0]["episodes"]] == [0, 1] and set(optioned["meta_test"][0]["summary"]) == set(EVAL_CONDITIONS)
        # a tampered blob is refused
        copy_run = root / "tampered"
        shutil.copytree(runs[0], copy_run)
        with (copy_run / META_INIT_NAME).open("ab") as stream:
            stream.write(b"x")
        try:
            load_meta_run(copy_run)
        except PrerequisiteError:
            pass
        else:
            raise AssertionError("a modified meta-initialization must be refused")
        # adapt: a meta-train symbol is refused (exit 3), a held-out symbol adapts and exports an ordinary single-output ONNX
        adapt_common = ["--data-dir", str(data), "--out-root", str(root / "out"), "--meta-run", str(runs[0]), "--shots", "8", "--min-query-samples", "10",
                        "--eval-query", "40"]
        assert main(["adapt", "--symbol", "AAA"] + adapt_common) == EXIT_PREREQUISITE
        assert main(["adapt", "--symbol", "DDD", "--export-onnx"] + adapt_common) == EXIT_OK
        adapted_run = next((root / "out" / KIND_ADAPT).iterdir())
        adapt_doc = json.loads((adapted_run / ADAPT_DOC_NAME).read_text(encoding="utf-8"))
        assert adapt_doc["status"] == STATUS_AVAILABLE and adapt_doc["export"]["registered"] is False and adapt_doc["export"]["activated"] is False
        assert "inner_class_weights" not in adapt_doc["config"], "adapting a default run keeps its config (and so its run id)"
        # adapting a run trained with inverse-frequency inner weights uses the same inner loss and records it
        weighted_adapt = ["--data-dir", str(data), "--out-root", str(root / "out"), "--meta-run", str(optioned_run), "--shots", "8", "--min-query-samples", "10",
                          "--eval-query", "40"]
        assert main(["adapt", "--symbol", "DDD"] + weighted_adapt) == EXIT_OK
        weighted_doc = next(json.loads((p / ADAPT_DOC_NAME).read_text(encoding="utf-8")) for p in sorted((root / "out" / KIND_ADAPT).iterdir())
                            if "inner_class_weights" in json.loads((p / ADAPT_DOC_NAME).read_text(encoding="utf-8"))["config"])
        assert weighted_doc["config"]["inner_class_weights"] == INNER_WEIGHTS_INVERSE_FREQUENCY and weighted_doc["status"] == STATUS_AVAILABLE
        import onnxruntime as ort
        session = ort.InferenceSession(str(adapted_run / ADAPTED_ONNX_NAME), providers=["CPUExecutionProvider"])
        assert [o.name for o in session.get_outputs()] == ["output"], "the adapted model keeps the single-output contract"
        probs = session.run(["output"], {"input": np.random.default_rng(0).standard_normal((2, 10, 5)).astype(np.float32)})[0]
        assert probs.shape == (2, NUM_CLASSES) and np.allclose(probs.sum(axis=1), 1.0, atol=1e-4)
        # too few samples for a support + query block: insufficient_data without metric keys (never zeros); an export request then fails (exit 3)
        few = ["--data-dir", str(data), "--out-root", str(root / "out"), "--meta-run", str(runs[0]), "--shots", "200", "--min-query-samples", "10",
               "--eval-query", "40"]
        known_adapts = {p.name for p in (root / "out" / KIND_ADAPT).iterdir()}
        before_adapt = len(known_adapts)
        assert main(["adapt", "--symbol", "DDD"] + few) == EXIT_OK
        newest = next(p for p in (root / "out" / KIND_ADAPT).iterdir() if p.name not in known_adapts)
        thin = json.loads((newest / ADAPT_DOC_NAME).read_text(encoding="utf-8"))
        assert thin["status"] == STATUS_INSUFFICIENT and "conditions" not in thin["result"], thin["result"]
        assert len(list((root / "out" / KIND_ADAPT).iterdir())) == before_adapt + 1
        assert main(["adapt", "--symbol", "DDD", "--export-onnx"] + few) == EXIT_PREREQUISITE
        assert len(list((root / "out" / KIND_ADAPT).iterdir())) == before_adapt + 1, "a refused export must publish nothing"
        # synthetic data is refused (exit 3) and nothing is published
        synthetic = root / "synthetic"
        _write_parquet(synthetic, ["AAA", "BBB"], 220)
        (synthetic / ds.SYNTHETIC_MANIFEST).write_text(json.dumps({"data_origin": ds.SYNTHETIC_ORIGIN}), encoding="utf-8")
        before = sorted(p.name for p in (root / "out" / KIND_TRAIN).iterdir())
        assert main(["train", "--symbols", "AAA,BBB", "--data-dir", str(synthetic), "--out-root", str(root / "out"), "--oos-tail-days", "20"]) == EXIT_PREREQUISITE
        assert sorted(p.name for p in (root / "out" / KIND_TRAIN).iterdir()) == before
        # overlapping lists, weekly timeframe, no usable task, and an exhausted time budget all publish nothing
        assert main(["train", "--symbols", "AAA,BBB", "--meta-test-symbols", "BBB"] + common) == EXIT_PREREQUISITE
        assert main(["train", "--symbols", "AAA,BBB", "--timeframe", "weekly"] + common) == EXIT_PREREQUISITE
        trained_before = sorted(p.name for p in (root / "out" / KIND_TRAIN).iterdir())
        no_task = [a for a in common]
        no_task[no_task.index("--min-task-samples") + 1] = "100000"
        assert main(["train", "--symbols", "AAA,BBB"] + no_task) == EXIT_PREREQUISITE, "no usable task must fail without publishing"
        assert sorted(p.name for p in (root / "out" / KIND_TRAIN).iterdir()) == trained_before
        try:
            meta_train(tp.build_model("lstm", 5, 4, 1, 0.0, NUM_CLASSES), _tiny_tasks(2), _tiny_spec(ALGORITHM_REPTILE), deadline=time.monotonic() - 1)
        except TimeoutError:
            pass
        else:
            raise AssertionError("an exhausted time budget must stop the run")
        assert not any(p.name.endswith(INCOMING_SUFFIX) for kind in (KIND_TRAIN, KIND_ADAPT) for p in (root / "out" / kind).iterdir())
    print("train_meta.py selfcheck: OK")


if __name__ == "__main__":
    if "--selfcheck" in sys.argv[1:]:
        _run_selfcheck()
    else:
        raise SystemExit(main(sys.argv[1:]))
