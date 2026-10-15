"""T19 pseudo-label (semi-supervised) training for ``train_pytorch.py --semi-supervised``.

Approved decisions (2026-10-10, ``Y:\\Temp\\sa_decision_options_onnx_research_t17_t22.md``): P01..P22 = star, C01..C19 = star.

* P01 "unlabeled" = the symbols of ``--unlabeled-symbols``. Their labels are never read; they are removed from the labeled
  symbol set, so they never reach the fit, the early-stopping validation, the scaler fit or any metric.
* P02 time border: an unlabeled window may be used only if its information interval (anchor row .. anchor row + horizon)
  ends at least ``purge`` rows (= ``dataset.resolve_purge_gap``) before the row of the earliest validation date among the labeled
  symbols (``train_val_date_ranges.validation_start``). The out-of-sample tail never reaches this tool (the trainer sees the main
  block only) and lies after the validation span anyway.
* P03 validation and out-of-sample sets are exactly the labeled ones of the supervised run; pseudo-labels only enter the fit side.
* P04 teacher = the supervised model of the same run (round 0, best validation weights). P05 hard labels (argmax).
  P06 confidence = max softmax probability, P07 fixed threshold ``--pseudo-threshold``, P08 class cap = ``--pseudo-class-cap`` times the
  MEDIAN accepted class count over the classes that have any accepted window (seeded thinning; N03 B, 2026-10-10, replacing the smallest
  count, which let one near-empty class thin the others hard), P09 ``--pseudo-rounds`` rounds, stop early on an empty selection,
  P10 each student starts from a fresh initialization, P11 pseudo-label loss weight ``--pseudo-weight``.
* P12 nothing accepted -> ``status = no_pseudo_labels`` and the supervised model is exported. P13 the final model is the last student.
* P14 classification only; CPCV, bagging, calibration / meta segment and non-fresh initialization (resume, fine-tune) are refused.
* P16 the record is the sidecar ``<model>.onnx.pseudolabels.json`` (an optional offline artifact; no ONNX metadata key, no C# change).
  P17 sample id = ``{symbol, anchor_date, anchor_row}``. P20 the supervised-only (round 0) validation metrics are reported beside each student's.

The tool has no C# surface: the wizard / ``TrainingJobConfig`` do not know it and ``run_training.py`` never passes it.
Run ``python semi_supervised.py --selfcheck`` to verify it (needs torch).
"""

from __future__ import annotations

import argparse
import copy
import datetime
import json
import math
import sys
from pathlib import Path
from typing import Dict, List, Tuple

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import dataset as ds  # noqa: E402
import fixed_scaler  # noqa: E402

SCHEMA_VERSION: int = 1
TOOL: str = "semi_supervised.py"
TOOL_VERSION: str = "1"
SIDECAR_SUFFIX: str = ".pseudolabels.json"          # appended to the model file name (<model>.onnx.pseudolabels.json)
ARGUMENT_KEYS: Tuple[str, ...] = (                  # excluded from the checkpoint training_config (checkpoints.Session)
    "semi_supervised", "unlabeled_symbols", "pseudo_threshold", "pseudo_rounds", "pseudo_weight", "pseudo_class_cap",
)
NUM_CLASSES: int = len(ds.CLASS_LABELS)
DEFAULT_THRESHOLD: float = 0.90      # no empirical basis (candidates 0.80 / 0.90 / 0.95)
DEFAULT_ROUNDS: int = 1
MAX_ROUNDS: int = 5                  # safety bound (code limit), validated at parse time
DEFAULT_WEIGHT: float = 0.5          # P11 (candidates 1.0 / 0.5 / 0.25)
CLASS_CAP_BASIS: str = "median_accepted_class_count"   # recorded in the sidecar config (before 2026-10-10 the smallest count was used)
DEFAULT_CLASS_CAP: int = 3           # P08 (candidates 1 / 2 / 3); no empirical basis
MAX_CLASS_CAP: int = 10
PSEUDO_STREAM: int = 41              # SeedSequence namespace of the class thinning
RELIABILITY_BINS: int = 10           # DF19 A: equal-width bins of the max softmax probability over [0, 1] (the last one includes 1.0)
INFERENCE_BATCH: int = 2048          # windows per teacher forward pass
VALIDATION_KEYS: Tuple[str, ...] = ("n_samples", "accuracy", "macro_f1", "multi_logloss", "auc_ovr", "brier")
EXIT_REFUSED = 2


# --- Arguments --------------------------------------------------------------------------------------------------------------


def add_arguments(parser: argparse.ArgumentParser) -> None:
    g = parser.add_argument_group("semi-supervised pseudo-label training (T19)")
    g.add_argument("--semi-supervised", action="store_true",
                   help="Train a supervised teacher, pseudo-label the unlabeled symbols, retrain students (classification only).")
    g.add_argument("--unlabeled-symbols", default=None,
                   help="Comma list of parquet stems in --data-dir whose labels are never read; required with --semi-supervised.")
    g.add_argument("--pseudo-threshold", type=float, default=None,
                   help=f"Accept a window when max softmax probability >= this value, in (1/{NUM_CLASSES}, 1] (default {DEFAULT_THRESHOLD}).")
    g.add_argument("--pseudo-rounds", type=int, default=None, help=f"Student rounds, 1..{MAX_ROUNDS} (default {DEFAULT_ROUNDS}).")
    g.add_argument("--pseudo-weight", type=float, default=None,
                   help=f"Loss weight of a pseudo-labeled window relative to a labeled one, in (0, 1] (default {DEFAULT_WEIGHT}).")
    g.add_argument("--pseudo-class-cap", type=int, default=None,
                   help=f"Keep at most N times the median accepted class count per class (classes with any accepted window), 1..{MAX_CLASS_CAP} (default {DEFAULT_CLASS_CAP}).")


def unlabeled_names(text: str | None) -> List[str]:
    names = [t.strip() for t in (text or "").split(",") if t.strip()]
    return names


def validate_and_default(args: argparse.Namespace) -> None:
    """Refuse unsupported combinations (P14) and fill the defaults; raises ValueError with the reason."""
    options = dict(unlabeled_symbols=args.unlabeled_symbols, pseudo_threshold=args.pseudo_threshold,
                   pseudo_rounds=args.pseudo_rounds, pseudo_weight=args.pseudo_weight, pseudo_class_cap=args.pseudo_class_cap)
    if not args.semi_supervised:
        given = sorted(k for k, v in options.items() if v is not None)
        if given:
            raise ValueError(f"{', '.join('--' + k.replace('_', '-') for k in given)} require --semi-supervised")
        return
    if args.target_type != "classification":
        raise ValueError("--semi-supervised supports classification only (a regression target has no confidence)")
    if ds.cpcv_from_args(args) is not None:
        raise ValueError("--semi-supervised cannot be combined with CPCV")
    if ds.bagging_from_args(args) is not None:
        raise ValueError("--semi-supervised cannot be combined with bagging")
    if args.calibration or ds.holdout_segment_requested(args):
        raise ValueError("--semi-supervised cannot be combined with a calibration / meta segment")
    if args.initialization_mode != "fresh":
        raise ValueError("--semi-supervised needs a fresh initialization (no resume, no fine-tune)")
    names = unlabeled_names(args.unlabeled_symbols)
    if not names:
        raise ValueError("--semi-supervised requires --unlabeled-symbols")
    if len({n.lower() for n in names}) != len(names):
        raise ValueError("--unlabeled-symbols names a symbol twice")
    args.unlabeled_symbols = ",".join(names)
    if args.pseudo_threshold is None:
        args.pseudo_threshold = DEFAULT_THRESHOLD
    if (type(args.pseudo_threshold) is not float or not math.isfinite(args.pseudo_threshold)
            or not (1.0 / NUM_CLASSES) < args.pseudo_threshold <= 1.0):
        raise ValueError(f"--pseudo-threshold must be a finite number in (1/{NUM_CLASSES}, 1]")
    if args.pseudo_rounds is None:
        args.pseudo_rounds = DEFAULT_ROUNDS
    if type(args.pseudo_rounds) is not int or not 1 <= args.pseudo_rounds <= MAX_ROUNDS:
        raise ValueError(f"--pseudo-rounds must be an integer in 1..{MAX_ROUNDS}")
    if args.pseudo_weight is None:
        args.pseudo_weight = DEFAULT_WEIGHT
    if (type(args.pseudo_weight) is not float or not math.isfinite(args.pseudo_weight)
            or not 0.0 < args.pseudo_weight <= 1.0):
        raise ValueError("--pseudo-weight must be a finite number in (0, 1]")
    if args.pseudo_class_cap is None:
        args.pseudo_class_cap = DEFAULT_CLASS_CAP
    if type(args.pseudo_class_cap) is not int or not 1 <= args.pseudo_class_cap <= MAX_CLASS_CAP:
        raise ValueError(f"--pseudo-class-cap must be an integer in 1..{MAX_CLASS_CAP}")


# --- Labeled / unlabeled separation -------------------------------------------------------------------------------------------


def exclude_unlabeled(symbols: Dict[str, np.ndarray], dates: Dict[str, np.ndarray], args: argparse.Namespace):
    """The labeled symbol set: everything in the data directory except the unlabeled symbols (P01)."""
    excluded = {n.lower() for n in unlabeled_names(args.unlabeled_symbols)}
    present = {s.lower() for s in symbols}
    missing = sorted(excluded - present)
    if missing:
        raise SystemExit(f"--unlabeled-symbols not found under {args.data_dir}: {missing}")
    keep = [s for s in symbols if s.lower() not in excluded]
    if not keep:
        raise SystemExit("--unlabeled-symbols leaves no labeled symbol")
    return {s: symbols[s] for s in keep}, {s: dates[s] for s in keep}


class Pool:
    """Candidate unlabeled windows: raw features as ``build_dataset`` returns them, with their identity."""

    def __init__(self, x: np.ndarray, symbol: np.ndarray, anchor_row: np.ndarray, anchor_date: np.ndarray,
                 counts: dict):
        self.x, self.symbol, self.anchor_row, self.anchor_date, self.counts = x, symbol, anchor_row, anchor_date, counts

    def __len__(self) -> int:
        return int(self.x.shape[0])

    def ids(self, index: np.ndarray) -> List[dict]:
        return [dict(symbol=str(self.symbol[i]), anchor_date=str(self.anchor_date[i]), anchor_row=int(self.anchor_row[i]))
                for i in index]


def border_row(dates: np.ndarray, validation_start: str) -> int:
    """First row of one symbol whose date is on or after the earliest labeled validation date."""
    return int(np.searchsorted(np.asarray(dates).astype("datetime64[D]"), np.datetime64(validation_start, "D"), side="left"))


def build_pool(args: argparse.Namespace, validation_start: str) -> Pool:
    """Windows of the unlabeled symbols that satisfy the time border (P02). Their labels are not read: the label array that
    ``build_dataset`` returns is discarded and only the anchor rows are kept."""
    if not validation_start:
        raise SystemExit("the supervised run produced no validation span; pseudo-labeling needs one")
    names = unlabeled_names(args.unlabeled_symbols)
    symbols, dates = ds.load_parquet_dir(args.data_dir, return_dates=True, symbols=names)
    absent = sorted({n.lower() for n in names} - {s.lower() for s in symbols})
    if absent:
        raise SystemExit(f"--unlabeled-symbols not found under {args.data_dir}: {absent}")
    feature_spec = json.loads(args.feature_spec) if args.feature_mode == ds.COMPOSED_FEATURES_MODE else None
    lags = fixed_scaler.trainer_lags(args, feature_spec)
    paths = json.loads(args.indicator_channel_export_paths) if args.indicator_channel_export_paths else None
    gap = ds.resolve_purge_gap(args.window, args.horizon, args.gap)
    parts_x, parts_sym, parts_row, parts_date = [], [], [], []
    counts = dict(candidate_windows=0, excluded_after_border=0, kept=0, symbols={})
    for name in sorted(symbols):
        x, _discarded_labels, anchors = ds.build_dataset(
            symbols[name], args.feature_mode, args.window, args.horizon, args.threshold, feature_spec=feature_spec,
            target_type="classification", return_anchor_rows=True, dates=dates[name],
            indicator_export_path=paths.get(name) if paths else None, fixed_zscore=args.fixed_zscore, lags=lags)
        border = border_row(dates[name], validation_start)
        allowed = anchors + args.horizon + gap < border
        counts["candidate_windows"] += int(anchors.shape[0])
        counts["excluded_after_border"] += int((~allowed).sum())
        counts["symbols"][name] = int(allowed.sum())
        if allowed.any():
            parts_x.append(x[allowed])
            parts_sym.append(np.full(int(allowed.sum()), name, dtype=object))
            parts_row.append(anchors[allowed])
            parts_date.append(np.asarray(dates[name]).astype("datetime64[D]")[anchors[allowed]])
    counts["kept"] = int(sum(p.shape[0] for p in parts_x))
    counts["purge_rows"], counts["validation_start"] = int(gap), validation_start
    if not parts_x:
        return Pool(np.empty((0, args.window, 0), dtype=np.float32), np.empty(0, dtype=object),
                    np.empty(0, dtype=np.int64), np.empty(0, dtype="datetime64[D]"), counts)
    x_all = np.concatenate(parts_x)
    if x_all.nbytes > ds.MAX_COMPOSED_TENSOR_SIZE_MB * 1024 * 1024:
        raise SystemExit(f"the unlabeled pool of {x_all.nbytes} bytes exceeds {ds.MAX_COMPOSED_TENSOR_SIZE_MB} MB")
    return Pool(x_all, np.concatenate(parts_sym), np.concatenate(parts_row).astype(np.int64),
                np.concatenate(parts_date), counts)


# --- Selection ------------------------------------------------------------------------------------------------------------------


def select(probs: np.ndarray, threshold: float, class_cap: int, seed: int, round_index: int) -> dict:
    """Pseudo-label selection (P05..P08): hard label = argmax (lowest index wins an exact tie), confidence = max probability."""
    p = np.asarray(probs, dtype=np.float64)
    if p.ndim != 2 or p.shape[1] != NUM_CLASSES or not np.isfinite(p).all():
        raise ValueError("teacher probabilities must be a finite [n, 3] array")
    label, confidence = p.argmax(axis=1), p.max(axis=1)
    confident = confidence >= threshold
    below = int((~confident).sum())
    per_class = np.array([int((confident & (label == c)).sum()) for c in range(NUM_CLASSES)])
    keep = confident.copy()
    capped = 0
    removed = {ds.CLASS_LABELS[c]: 0 for c in range(NUM_CLASSES)}
    present = per_class[per_class > 0]
    if present.size:
        cap = max(1, math.floor(class_cap * float(np.median(present))))   # N03 B: median of the non-empty classes (even count: mean of the middle two)
        rng = np.random.default_rng(np.random.SeedSequence([seed, PSEUDO_STREAM, round_index]))
        for c in range(NUM_CLASSES):
            idx = np.nonzero(confident & (label == c))[0]
            if idx.shape[0] > cap:
                drop = np.setdiff1d(idx, np.sort(rng.choice(idx, size=cap, replace=False)))
                keep[drop] = False
                capped += int(drop.shape[0])
                removed[ds.CLASS_LABELS[c]] = int(drop.shape[0])
    index = np.nonzero(keep)[0]
    accepted = {ds.CLASS_LABELS[c]: int((label[index] == c).sum()) for c in range(NUM_CLASSES)}
    return dict(index=index, labels=label[index].astype(np.int64), considered=int(p.shape[0]), below_threshold=below,
                class_cap_removed=capped, removed_by_cap_per_class=removed, accepted=int(index.shape[0]), accepted_per_class=accepted,
                min_confidence=float(confidence[index].min()) if index.size else None,
                mean_confidence=float(confidence[index].mean()) if index.size else None)


def predict_probabilities(model, x: np.ndarray) -> np.ndarray:
    """Softmax probabilities of the teacher for already-transformed windows."""
    import torch

    model.eval()
    out = []
    with torch.no_grad():
        for start in range(0, x.shape[0], INFERENCE_BATCH):
            out.append(torch.softmax(model(torch.from_numpy(np.ascontiguousarray(x[start:start + INFERENCE_BATCH]))), dim=-1).cpu().numpy())
    return np.concatenate(out) if out else np.empty((0, NUM_CLASSES))


def weighted_cross_entropy(per_sample_loss, class_weights, targets, sample_weights):
    """Mean cross entropy with per-sample weights, normalised like ``CrossEntropyLoss(weight=...)``'s ``mean``
    (so unit sample weights reproduce it exactly). ``per_sample_loss`` already contains the class weight."""
    denominator = (sample_weights * class_weights[targets]).sum()
    return (per_sample_loss * sample_weights).sum() / denominator


def reliability_table(probs: np.ndarray, y: np.ndarray, threshold: float) -> dict:
    """DF19 A: max-softmax confidence vs accuracy on the validation windows. ``ece`` is the standard top-label ``sum_b (n_b / n) |acc_b - conf_b|``;
    ``at_or_above_threshold`` is the measured accuracy of the windows the selection rule would accept (confidence >= *threshold*)."""
    p = np.asarray(probs, dtype=np.float64)
    confidence, correct = p.max(axis=1), (p.argmax(axis=1) == np.asarray(y))
    bin_index = np.minimum((confidence * RELIABILITY_BINS).astype(np.int64), RELIABILITY_BINS - 1)
    bins, ece = [], 0.0
    for b in range(RELIABILITY_BINS):
        mine = bin_index == b
        n_b = int(mine.sum())
        accuracy = float(correct[mine].mean()) if n_b else None
        mean_confidence = float(confidence[mine].mean()) if n_b else None
        if n_b:
            ece += n_b / p.shape[0] * abs(accuracy - mean_confidence)
        bins.append(dict(low=b / RELIABILITY_BINS, high=(b + 1) / RELIABILITY_BINS, n=n_b, mean_confidence=mean_confidence, accuracy=accuracy))
    above = confidence >= threshold
    return dict(n=int(p.shape[0]), bins=bins, ece=float(ece), confidence="max_softmax_probability",
                at_or_above_threshold=dict(threshold=threshold, n=int(above.sum()), accuracy=float(correct[above].mean()) if above.any() else None))


def comparable_validation_loss(probs: np.ndarray, y: np.ndarray, y_labeled_train: np.ndarray) -> dict:
    """DF20 A: validation cross entropy of one model that can be compared across rounds. ``labeled_weights`` weights each window by the class
    weight of the LABELED training windows only (``train_pytorch._class_weights``, the weights the supervised teacher trains with; normalised like
    ``CrossEntropyLoss(weight=...)``); ``unweighted`` is the plain mean. The trainer's own early-stopping loss is left alone."""
    import train_pytorch

    p = np.asarray(probs, dtype=np.float64)
    target = np.asarray(y, dtype=np.int64)
    weights = train_pytorch._class_weights(np.asarray(y_labeled_train)).numpy().astype(np.float64)
    nll = -np.log(p[np.arange(p.shape[0]), target])
    return dict(labeled_weights=float((weights[target] * nll).sum() / weights[target].sum()), unweighted=float(nll.mean()),
                class_weights={ds.CLASS_LABELS[c]: float(weights[c]) for c in range(NUM_CLASSES)}, n=int(p.shape[0]))


def to_features(x: np.ndarray, scaler) -> np.ndarray:
    """Pool windows -> model input: the fitted fixed scaler when there is one, otherwise the features as built."""
    return fixed_scaler.transform(x, scaler) if scaler is not None else np.asarray(x, dtype=np.float32)


# --- Orchestration --------------------------------------------------------------------------------------------------------------


def _subset(report: dict) -> dict:
    out = {}
    for key in VALIDATION_KEYS:
        value = report.get(key)
        if isinstance(value, (int, float, np.integer, np.floating)) and math.isfinite(float(value)):
            out[key] = float(value) if key != "n_samples" else int(value)
    return out


def _delta(student: dict, teacher: dict) -> dict:
    return {k: student[k] - teacher[k] for k in ("accuracy", "macro_f1", "multi_logloss") if k in student and k in teacher}


def run(args: argparse.Namespace, train_model):
    """Teacher -> pseudo-labels -> students. Returns the ``train_model`` tuple of the final model and stores the record on
    ``args._pseudo_summary`` (written next to the ONNX by :func:`write_sidecar`)."""
    args._collect_validation = True            # I2: train_model hands over its validation probabilities (read only)
    teacher = train_model(args)
    model, channels, report, date_ranges, scaler = teacher
    teacher_validation = _subset(report)
    summary = dict(
        schema_version=SCHEMA_VERSION, tool=TOOL, tool_version=TOOL_VERSION,
        created_utc=datetime.datetime.now(datetime.timezone.utc).isoformat(), status="ok",
        config=dict(threshold=args.pseudo_threshold, rounds=args.pseudo_rounds, weight=args.pseudo_weight,
                    class_cap=args.pseudo_class_cap, class_cap_basis=CLASS_CAP_BASIS, seed=args.seed, unlabeled_symbols=unlabeled_names(args.unlabeled_symbols),
                    confidence="max_softmax_probability", label="argmax", student_initialization="fresh"),
        isolation=dict(unlabeled_symbols_in_labeled_set=False, validation_pseudo_labeled=False, out_of_sample_pseudo_labeled=False,
                       scaler_fit="labeled training windows only", early_stopping="labeled validation windows only"),
        teacher=dict(round=0, kind="supervised_same_run", validation=teacher_validation), rounds=[], accepted_ids=[])
    teacher_out = args._validation_outputs
    summary["teacher"]["comparable_validation_loss"] = comparable_validation_loss(teacher_out["probs"], teacher_out["y"], teacher_out["y_labeled_train"])
    summary["teacher"]["reliability"] = reliability_table(teacher_out["probs"], teacher_out["y"], args.pseudo_threshold)
    pool = build_pool(args, date_ranges.get("validation_start", ""))
    summary["pool"] = dict(pool.counts, windows=len(pool),
                           first_anchor=str(pool.anchor_date.min()) if len(pool) else None,
                           last_anchor=str(pool.anchor_date.max()) if len(pool) else None)
    final = teacher
    last_args = args
    current = teacher
    for round_index in range(1, args.pseudo_rounds + 1):
        if len(pool) == 0:
            summary["status"] = "no_pseudo_labels"
            summary["reason"] = "no unlabeled window satisfies the time border"
            break
        model_r, _channels_r, _report_r, _ranges_r, scaler_r = current
        probs = predict_probabilities(model_r, to_features(pool.x, scaler_r))
        chosen = select(probs, args.pseudo_threshold, args.pseudo_class_cap, args.seed, round_index)
        record = dict(round=round_index, considered=chosen["considered"], accepted=chosen["accepted"],
                      below_threshold=chosen["below_threshold"], class_cap_removed=chosen["class_cap_removed"],
                      accepted_per_class=chosen["accepted_per_class"], min_confidence=chosen["min_confidence"],
                      mean_confidence=chosen["mean_confidence"], teacher_round=round_index - 1,
                      removed_by_cap_per_class=chosen["removed_by_cap_per_class"], accepted_ids=pool.ids(chosen["index"]))
        if chosen["accepted"] == 0:
            record["status"] = "no_pseudo_labels"
            summary["rounds"].append(record)
            if round_index == 1:
                summary["status"] = "no_pseudo_labels"
            break
        student_args = copy.copy(args)
        student_args.checkpoint_dir = Path(args.out.with_name(f"{args.out.name}.semi{round_index}.checkpoints"))
        student_args._pseudo = dict(x=pool.x[chosen["index"]], y=chosen["labels"], weight=args.pseudo_weight)
        print(f"semi-supervised round {round_index}/{args.pseudo_rounds}: {chosen['accepted']} of {chosen['considered']} "
              f"unlabeled windows accepted {chosen['accepted_per_class']}")
        current = train_model(student_args)
        final, last_args = current, student_args
        record["status"] = "ok"
        record["student_validation"] = _subset(current[2])
        out = student_args._validation_outputs
        record["comparable_validation_loss"] = comparable_validation_loss(out["probs"], out["y"], out["y_labeled_train"])
        record["delta_vs_supervised_only"] = _delta(record["student_validation"], teacher_validation)
        summary["rounds"].append(record)
        summary["accepted_ids"] = pool.ids(chosen["index"])
    args._checkpoint_session = last_args._checkpoint_session
    args._collect_validation = False
    args.__dict__.pop("_validation_outputs", None)
    args._pseudo_summary = summary
    return final


def clean(value):
    """JSON-bound copy without non-finite floats and numpy scalar types (omitted, never replaced by 0)."""
    if isinstance(value, dict):
        out = {}
        for key, item in value.items():
            cleaned = clean(item)
            if cleaned is not _OMIT:
                out[str(key)] = cleaned
        return out
    if isinstance(value, (list, tuple)):
        return [clean(item) for item in value]
    if isinstance(value, (np.floating, float)):
        return float(value) if math.isfinite(float(value)) else _OMIT
    if isinstance(value, np.integer):
        return int(value)
    if isinstance(value, np.bool_):
        return bool(value)
    return value


_OMIT = object()


def write_sidecar(model_path: Path, summary: dict) -> Path:
    import checkpoints

    path = Path(model_path).with_name(Path(model_path).name + SIDECAR_SUFFIX)
    checkpoints.atomic_json(path, clean(summary))
    return path


# --- Self-check -----------------------------------------------------------------------------------------------------------------


def _write_series(directory: Path, names, rows: int, seed: int, start: datetime.date) -> None:
    import polars as pl

    directory.mkdir(parents=True, exist_ok=True)
    for k, name in enumerate(names):
        g = np.random.default_rng(seed + k)
        close = 100.0 * np.exp(np.cumsum(g.normal(0.0, 0.01, rows)))
        open_ = np.concatenate([[100.0], close[:-1]])
        pl.DataFrame({"date": [start + datetime.timedelta(days=i) for i in range(rows)], "open": open_,
                      "high": np.maximum(open_, close) * 1.002, "low": np.minimum(open_, close) * 0.998, "close": close,
                      "volume": g.integers(1000, 5000, rows).astype(np.float64)}).write_parquet(directory / f"{name}.parquet")


def _run_selfcheck() -> None:
    import contextlib
    import io
    import tempfile

    import torch
    from torch import nn

    import train_pytorch as tp

    def parse(*argv: str):
        return tp._parse_args(list(argv))

    def refused(*argv: str) -> bool:
        with contextlib.redirect_stderr(io.StringIO()):
            try:
                parse(*argv)
            except SystemExit:
                return True
        return False

    # group 1: argument rules
    ok = parse("--semi-supervised", "--unlabeled-symbols", "U1, U2")
    assert (ok.unlabeled_symbols, ok.pseudo_threshold, ok.pseudo_rounds, ok.pseudo_weight, ok.pseudo_class_cap) == (
        "U1,U2", DEFAULT_THRESHOLD, DEFAULT_ROUNDS, DEFAULT_WEIGHT, DEFAULT_CLASS_CAP)
    plain = parse()
    assert plain.semi_supervised is False and plain.unlabeled_symbols is None and plain.pseudo_threshold is None
    base = ["--semi-supervised", "--unlabeled-symbols", "U1"]
    for bad in (["--semi-supervised"], ["--unlabeled-symbols", "U1"], ["--pseudo-rounds", "2"], base + ["--target-type", "regression"],
                base + ["--cpcv-groups", "6", "--cpcv-test-groups", "2", "--cpcv-combination", "0"],
                base + ["--bag-index", "0", "--bag-count", "3", "--bag-seed", "1"], base + ["--calibration", "--outer-fold-index", "0"],
                base + ["--initialization-mode", "fine_tune"], base + ["--pseudo-threshold", "0.3"],
                base + ["--pseudo-threshold", "1.5"], base + ["--pseudo-threshold", "nan"], base + ["--pseudo-rounds", "0"],
                base + ["--pseudo-rounds", str(MAX_ROUNDS + 1)], base + ["--pseudo-weight", "0"], base + ["--pseudo-weight", "1.1"],
                base + ["--pseudo-class-cap", "0"], base + ["--pseudo-class-cap", str(MAX_CLASS_CAP + 1)],
                ["--semi-supervised", "--unlabeled-symbols", "U1,u1"]):
        assert refused(*bad), f"{bad} must be refused"
    print("  group 1 argument rules OK")

    # group 2: selection (threshold, tie, class cap, determinism, empty)
    probs = np.array([[0.95, 0.03, 0.02], [0.92, 0.05, 0.03], [0.91, 0.05, 0.04], [0.93, 0.04, 0.03],
                      [0.02, 0.96, 0.02], [0.60, 0.30, 0.10], [1 / 3, 1 / 3, 1 / 3], [0.02, 0.03, 0.95]])
    s = select(probs, 0.90, 3, 42, 1)
    assert s["considered"] == 8 and s["below_threshold"] == 2 and s["class_cap_removed"] == 1 and s["accepted"] == 5, s
    assert s["accepted_per_class"] == {"Up": 3, "Down": 1, "Neutral": 1} and s["labels"].tolist() == [0 if i < 4 else 1 if i == 4 else 2 for i in s["index"]]
    assert np.array_equal(s["index"], select(probs, 0.90, 3, 42, 1)["index"]), "seeded thinning must repeat"
    assert select(probs, 0.90, 4, 42, 1)["class_cap_removed"] == 0

    def rows(counts):                                              # confident rows: counts[c] windows of class c at probability 0.95
        out = []
        for c, n in enumerate(counts):
            out += [[0.95 if k == c else 0.025 for k in range(NUM_CLASSES)]] * n
        return np.array(out)

    # N03 B: the cap follows the MEDIAN of the non-empty classes, so one near-empty class no longer thins the others
    for counts, cap_expected in (([3, 20, 60], 60), ([1, 40, 60], 120), ([0, 4, 40], 66), ([0, 0, 50], 150), ([10, 10, 10], 30), ([2, 9, 9], 27)):
        sel = select(rows(counts), 0.90, 3, 7, 1)
        kept = [min(n, cap_expected) for n in counts]
        assert [sel["accepted_per_class"][name] for name in ds.CLASS_LABELS] == kept, (counts, sel["accepted_per_class"])
        assert sel["class_cap_removed"] == sum(counts) - sum(kept) and sel["accepted"] == sum(kept)
    heavy = select(rows([100, 5, 5]), 0.90, 3, 7, 1)                 # a heavy class is still cut to 3 x median (= 3 x 5)
    assert [heavy["accepted_per_class"][name] for name in ds.CLASS_LABELS] == [15, 5, 5] and heavy["class_cap_removed"] == 85
    assert select(rows([1, 1, 90]), 0.90, 1, 7, 1)["accepted_per_class"]["Neutral"] == 1   # cap never below 1
    assert select(probs, 0.90, 1, 42, 1)["accepted_per_class"] == {"Up": 1, "Down": 1, "Neutral": 1}
    assert select(probs, 1.0, 3, 42, 1)["accepted"] == 0 and select(probs, 1.0, 3, 42, 1)["min_confidence"] is None
    tie = select(np.array([[0.5, 0.5, 0.0]]), 0.5, 3, 1, 1)
    assert tie["labels"].tolist() == [0], "an exact tie goes to the lowest class index"
    try:
        select(np.array([[0.5, np.nan, 0.5]]), 0.5, 3, 1, 1)
    except ValueError:
        pass
    else:
        raise AssertionError("non-finite probabilities must be refused")
    print("  group 2 selection OK")

    # group 3: weighted loss reproduces CrossEntropyLoss(mean) for unit weights and the manual formula otherwise
    torch.manual_seed(0)
    logits, target = torch.randn(7, 3), torch.tensor([0, 1, 2, 0, 1, 2, 2])
    cw = torch.tensor([0.5, 1.0, 2.0])
    mean_ce = nn.CrossEntropyLoss(weight=cw)(logits, target)
    each = nn.CrossEntropyLoss(weight=cw, reduction="none")(logits, target)
    assert torch.allclose(weighted_cross_entropy(each, cw, target, torch.ones(7)), mean_ce, atol=1e-6)
    w = torch.tensor([1.0, 1.0, 0.5, 0.5, 1.0, 0.25, 1.0])
    assert abs(float(weighted_cross_entropy(each, cw, target, w)) - float((each * w).sum() / (w * cw[target]).sum())) < 1e-7
    print("  group 3 weighted loss OK")

    with tempfile.TemporaryDirectory(prefix="sa_semi_selfcheck_") as tmp_name:
        tmp = Path(tmp_name)
        data = tmp / "data"
        start = datetime.date(2020, 1, 1)
        _write_series(data, ("LAB1", "LAB2", "LAB3"), 520, 1, start)
        _write_series(data, ("UNL1", "UNL2"), 520, 50, start)
        common = ["--data-dir", str(data), "--window", "10", "--horizon", "3", "--hidden", "8", "--layers", "1", "--epochs", "2",
                  "--batch", "64", "--no-verify", "--seed", "7"]

        # group 4: the time border and the identity of the unlabeled windows
        probe = parse(*common, "--out", str(tmp / "probe.onnx"), "--semi-supervised", "--unlabeled-symbols", "UNL1,UNL2")
        validation_start = "2021-02-01"
        pool = build_pool(probe, validation_start)
        gap = ds.resolve_purge_gap(10, 3, None)
        _, d = ds.load_parquet_dir(data, return_dates=True, symbols=["UNL1"])
        border = border_row(d["UNL1"], validation_start)
        mine = pool.symbol == "UNL1"
        assert mine.any() and np.all(pool.anchor_row[mine] + 3 + gap < border) and pool.anchor_row[mine].max() + 3 + gap == border - 1
        assert pool.counts["candidate_windows"] == pool.counts["kept"] + pool.counts["excluded_after_border"]
        assert str(pool.anchor_date.max()) < validation_start
        assert pool.ids(np.array([0]))[0] == dict(symbol=str(pool.symbol[0]), anchor_date=str(pool.anchor_date[0]), anchor_row=int(pool.anchor_row[0]))
        # rows after the border (labels and anything later) cannot change an accepted window
        import polars as pl
        altered = tmp / "data_altered"
        _write_series(altered, ("UNL1", "UNL2"), 520, 50, start)
        frame = pl.read_parquet(altered / "UNL1.parquet")
        scaled = frame.with_columns([(pl.when(pl.int_range(pl.len()) >= border).then(pl.col(c) * 5.0).otherwise(pl.col(c))).alias(c)
                                     for c in ("open", "high", "low", "close")])
        scaled.write_parquet(altered / "UNL1.parquet")
        probe_altered = parse(*common, "--out", str(tmp / "probe2.onnx"), "--semi-supervised", "--unlabeled-symbols", "UNL1,UNL2")
        probe_altered.data_dir = altered
        pool2 = build_pool(probe_altered, validation_start)
        assert np.array_equal(pool.x, pool2.x), "windows before the border must not depend on later rows"
        print(f"  group 4 time border OK ({pool.counts['kept']} windows kept, {pool.counts['excluded_after_border']} after the border)")

        # group 5: end to end through train_pytorch.main (non-fixed and fixed z-score)
        for label, extra in (("plain", []), ("fixed z-score", ["--fixed-zscore"])):
            out = tmp / f"{label.split()[0]}.onnx"
            with contextlib.redirect_stdout(io.StringIO()):
                code = tp.main([*common, "--out", str(out), "--semi-supervised", "--unlabeled-symbols", "UNL1,UNL2",
                                "--pseudo-threshold", "0.34", "--pseudo-rounds", "2", *extra])
            assert code == 0 and out.is_file()
            record = json.loads(Path(str(out) + SIDECAR_SUFFIX).read_text(encoding="utf-8"))
            assert record["status"] == "ok" and len(record["rounds"]) == 2 and all(r["status"] == "ok" for r in record["rounds"]), record["rounds"]
            assert record["isolation"]["unlabeled_symbols_in_labeled_set"] is False
            vs = datetime.date.fromisoformat(record["pool"]["validation_start"])
            ids = record["accepted_ids"]
            assert ids and {i["symbol"] for i in ids} <= {"UNL1", "UNL2"}
            _, ud = ds.load_parquet_dir(data, return_dates=True, symbols=["UNL1", "UNL2"])
            for i in ids:  # label end of every accepted window is before the border, with the purge gap in rows
                label_end = str(ud[i["symbol"]][i["anchor_row"] + 3])
                assert datetime.date.fromisoformat(label_end) < vs, (i, label_end, vs)
                assert i["anchor_row"] + 3 + gap < border_row(ud[i["symbol"]], str(vs))
            assert sum(record["rounds"][-1]["accepted_per_class"].values()) == len(ids)
            assert "delta_vs_supervised_only" in record["rounds"][0] and record["teacher"]["validation"]["n_samples"] > 0
            # I2 diagnostics: reliability of the teacher, comparable losses per round, per-round ids and per-class cap removal
            rel = record["teacher"]["reliability"]
            assert rel["n"] == record["teacher"]["validation"]["n_samples"] == sum(b["n"] for b in rel["bins"]) and len(rel["bins"]) == RELIABILITY_BINS
            assert 0.0 <= rel["ece"] <= 1.0 and rel["at_or_above_threshold"]["threshold"] == 0.34
            loss0 = record["teacher"]["comparable_validation_loss"]
            assert math.isfinite(loss0["labeled_weights"]) and math.isfinite(loss0["unweighted"]) and loss0["n"] == rel["n"]
            for r in record["rounds"]:
                assert len(r["accepted_ids"]) == r["accepted"] and sum(r["removed_by_cap_per_class"].values()) == r["class_cap_removed"]
                assert r["comparable_validation_loss"]["class_weights"] == loss0["class_weights"], "the labeled windows (and so the weights) are the same in every round"
                assert math.isfinite(r["comparable_validation_loss"]["labeled_weights"]) and r["comparable_validation_loss"]["n"] == rel["n"]
            assert record["rounds"][-1]["accepted_ids"] == record["accepted_ids"]
            text = Path(str(out) + SIDECAR_SUFFIX).read_text(encoding="utf-8")
            assert "NaN" not in text and "Infinity" not in text
            assert (tmp / f"{out.name}.semi1.checkpoints").is_dir() and (tmp / f"{out.name}.semi2.checkpoints").is_dir()
            print(f"    {label}: accepted {len(ids)} windows in the last round, validation accuracy "
                  f"{record['teacher']['validation']['accuracy']:.3f} -> {record['rounds'][-1]['student_validation']['accuracy']:.3f}")
        print("  group 5 end to end OK")

        # group 6: nothing accepted -> the supervised model is exported unchanged and the record says so
        zero_out = tmp / "zero.onnx"
        with contextlib.redirect_stdout(io.StringIO()):
            assert tp.main([*common, "--out", str(zero_out), "--semi-supervised", "--unlabeled-symbols", "UNL1,UNL2",
                            "--pseudo-threshold", "1.0"]) == 0
        record0 = json.loads(Path(str(zero_out) + SIDECAR_SUFFIX).read_text(encoding="utf-8"))
        assert record0["status"] == "no_pseudo_labels" and record0["accepted_ids"] == [] and record0["rounds"][0]["accepted"] == 0
        assert not (tmp / f"{zero_out.name}.semi1.checkpoints").exists(), "no student may be trained without pseudo-labels"
        print("  group 6 zero accepted OK")

        # group 7: the labeled set excludes the unlabeled symbols (they never reach split, scaler or validation)
        symbols, dates = ds.load_parquet_dir(data, return_dates=True)
        kept, _ = exclude_unlabeled(symbols, dates, probe)
        assert sorted(kept) == ["LAB1", "LAB2", "LAB3"]
        probe.unlabeled_symbols = "NOPE"
        try:
            exclude_unlabeled(symbols, dates, probe)
        except SystemExit:
            pass
        else:
            raise AssertionError("an unknown unlabeled symbol must be refused")
        print("  group 7 labeled/unlabeled separation OK")

    # group 8 (I2): reliability table, comparable losses, per-class cap removal -- hand-computed, model independent
    rel = reliability_table(np.array([[0.95, 0.03, 0.02], [0.85, 0.10, 0.05], [0.45, 0.30, 0.25], [0.35, 0.33, 0.32]]), np.array([0, 1, 0, 1]), 0.9)
    assert [b["n"] for b in rel["bins"]] == [0, 0, 0, 1, 1, 0, 0, 0, 1, 1] and rel["n"] == 4
    assert [b["accuracy"] for b in rel["bins"] if b["n"]] == [0.0, 1.0, 0.0, 1.0] and rel["bins"][9]["mean_confidence"] == 0.95
    assert abs(rel["ece"] - 0.25 * (0.05 + 0.85 + 0.55 + 0.35)) < 1e-12 and rel["at_or_above_threshold"] == dict(threshold=0.9, n=1, accuracy=1.0)
    full = reliability_table(np.array([[1.0, 0.0, 0.0]]), np.array([0]), 0.9)                         # confidence 1.0 belongs to the last bin
    assert full["bins"][9]["n"] == 1 and full["ece"] == 0.0 and reliability_table(np.array([[0.4, 0.3, 0.3]]), np.array([0]), 0.9)["at_or_above_threshold"]["accuracy"] is None
    y_lab = np.array([0, 0, 1, 2, 2, 2])                                                                # counts 2 / 1 / 3 -> weights 6 / (3 n) = 1, 2, 2/3
    p_val, y_val = np.array([[0.5, 0.3, 0.2], [0.4, 0.25, 0.35], [0.1, 0.1, 0.8]]), np.array([0, 1, 2])
    nll = -np.log(np.array([0.5, 0.25, 0.8]))
    loss = comparable_validation_loss(p_val, y_val, y_lab)
    assert abs(loss["labeled_weights"] - float((np.array([1.0, 2.0, 2.0 / 3.0]) * nll).sum() / (1.0 + 2.0 + 2.0 / 3.0))) < 1e-6
    assert abs(loss["unweighted"] - float(nll.mean())) < 1e-12 and loss["class_weights"] == {n: w for n, w in zip(ds.CLASS_LABELS, loss["class_weights"].values())}
    import train_pytorch as tp_
    assert np.allclose(list(loss["class_weights"].values()), tp_._class_weights(y_lab).numpy()), "one owner of the class weights"
    assert abs(comparable_validation_loss(p_val, y_val, np.array([0, 0, 0, 1, 1]))["class_weights"][ds.CLASS_LABELS[2]] - 2.0) < 1e-6  # an empty class counts as 1 and the total follows (trainer rule: 6 / (3 * 1))
    capped = select(rows([100, 5, 5]), 0.90, 3, 7, 1)
    assert capped["removed_by_cap_per_class"] == {ds.CLASS_LABELS[0]: 85, ds.CLASS_LABELS[1]: 0, ds.CLASS_LABELS[2]: 0} and capped["class_cap_removed"] == 85
    assert select(rows([3, 3, 3]), 0.90, 3, 7, 1)["removed_by_cap_per_class"] == {n: 0 for n in ds.CLASS_LABELS}
    print("  group 8 I2 diagnostics OK (reliability / ECE, comparable losses, per-class cap removal, class weights from one owner)")

    print("semi_supervised.py selfcheck: OK (8 groups passed)")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Pseudo-label training helper; use train_pytorch.py --semi-supervised.")
    parser.add_argument("--selfcheck", action="store_true", help="Run the assertions and exit.")
    if parser.parse_args().selfcheck:
        _run_selfcheck()
