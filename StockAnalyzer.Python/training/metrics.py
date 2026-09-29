"""Evaluation metrics shared by the ONNX trend-predictor trainers.

One place for every "is this model actually better than always predicting the
majority class?" number, so ``train_pytorch`` / ``train_lightgbm`` /
``train_tensorflow`` all report the same set and write it next to the exported
``.onnx`` as ``<model>.metrics.json``.

Accuracy alone never selects a model (WebAI review #17): a 70/30 class split
scores 70% just by always predicting the majority class, so
``majority_baseline_accuracy`` is always reported next to ``accuracy``.

``scikit-learn`` is used when importable; otherwise every metric falls back to a
self-contained numpy implementation so the trainers never hard-depend on it.

Run ``python metrics.py --selfcheck`` to verify the numpy formulas.
"""

from __future__ import annotations

import argparse
import json
import sys
from typing import Dict, List, Sequence

import numpy as np

try:  # optional; numpy fallbacks below cover every metric if it is missing.
    import sklearn.metrics as _sk

    _HAVE_SKLEARN = True
except Exception:  # noqa: BLE001 - any import failure means "use the numpy path"
    _HAVE_SKLEARN = False

# mirrors dataset.CLASS_LABELS index order ["Up", "Down", "Neutral"].
CLASS_LABELS: Sequence[str] = ("Up", "Down", "Neutral")
# probability clip for log-loss so a confident wrong prediction stays finite.
LOGLOSS_EPS: float = 1e-15


# --- numpy fallbacks ---------------------------------------------------------


def _confusion(y_true: np.ndarray, y_pred: np.ndarray, k: int) -> np.ndarray:
    cm = np.zeros((k, k), dtype=np.int64)
    for t, p in zip(y_true, y_pred):
        cm[int(t), int(p)] += 1
    return cm


def _binary_auc(labels: np.ndarray, scores: np.ndarray) -> float:
    """One-vs-rest ROC AUC via the rank (Mann-Whitney U) identity, tie-safe."""
    labels = np.asarray(labels)
    scores = np.asarray(scores, dtype=np.float64)
    n_pos = int(np.sum(labels == 1))
    n_neg = int(np.sum(labels == 0))
    if n_pos == 0 or n_neg == 0:
        return float("nan")

    order = np.argsort(scores, kind="mergesort")
    s_sorted = scores[order]
    ranks_sorted = np.empty(s_sorted.shape[0], dtype=np.float64)
    i = 0
    while i < s_sorted.shape[0]:
        j = i
        while j < s_sorted.shape[0] and s_sorted[j] == s_sorted[i]:
            j += 1
        ranks_sorted[i:j] = (i + j - 1) / 2.0 + 1.0  # average rank for the tie block
        i = j
    ranks = np.empty(scores.shape[0], dtype=np.float64)
    ranks[order] = ranks_sorted

    sum_pos = float(np.sum(ranks[labels == 1]))
    return (sum_pos - n_pos * (n_pos + 1) / 2.0) / (n_pos * n_neg)


# --- public API ------------------------------------------------------------


def classification_report_dict(
    y_true: Sequence[int],
    y_pred: Sequence[int],
    probs: Sequence[Sequence[float]] | None,
    num_classes: int = len(CLASS_LABELS),
    class_labels: Sequence[str] = CLASS_LABELS,
) -> Dict[str, object]:
    """Return the full metric set as a JSON-serializable dict.

    ``probs`` shape ``(N, num_classes)`` enables ``multi_logloss``, ``auc_ovr`` and
    ``brier``; pass ``None`` to skip the probability-based metrics.
    """
    yt = np.asarray(y_true, dtype=np.int64).ravel()
    yp = np.asarray(y_pred, dtype=np.int64).ravel()
    if yt.shape != yp.shape:
        raise ValueError(f"y_true {yt.shape} and y_pred {yp.shape} shape mismatch")

    cm = _confusion(yt, yp, num_classes)
    support = cm.sum(axis=1)
    pred_count = cm.sum(axis=0)
    tp = np.diag(cm).astype(np.float64)

    with np.errstate(invalid="ignore", divide="ignore"):
        precision = np.where(pred_count > 0, tp / np.maximum(pred_count, 1), 0.0)
        recall = np.where(support > 0, tp / np.maximum(support, 1), 0.0)
        f1 = np.where(
            (precision + recall) > 0,
            2 * precision * recall / np.maximum(precision + recall, 1e-12),
            0.0,
        )

    per_class = {
        class_labels[c]: {
            "precision": float(precision[c]),
            "recall": float(recall[c]),
            "f1": float(f1[c]),
            "support": int(support[c]),
        }
        for c in range(num_classes)
    }

    total = int(yt.shape[0])
    accuracy = float(tp.sum() / total) if total else 0.0
    majority = float(support.max() / total) if total else 0.0

    report: Dict[str, object] = {
        "n_samples": total,
        "class_labels": list(class_labels),
        "confusion_matrix": cm.tolist(),
        "per_class": per_class,
        "macro_f1": float(f1.mean()),
        "accuracy": accuracy,
        "majority_baseline_accuracy": majority,
        "accuracy_over_baseline": accuracy - majority,
    }

    if probs is None:
        report["multi_logloss"] = None
        report["auc_ovr"] = None
        report["brier"] = None
        return report

    p = np.asarray(probs, dtype=np.float64)
    if p.ndim != 2 or p.shape[0] != total or p.shape[1] != num_classes:
        raise ValueError(f"probs shape {p.shape} != ({total}, {num_classes})")
    onehot = np.eye(num_classes, dtype=np.float64)[yt]

    if _HAVE_SKLEARN:
        multi_logloss = float(_sk.log_loss(yt, p, labels=list(range(num_classes))))
        try:
            auc_ovr = float(_sk.roc_auc_score(
                yt, p, average="macro", multi_class="ovr", labels=list(range(num_classes))
            ))
        except ValueError:
            auc_ovr = float("nan")
    else:
        clipped = np.clip(p, LOGLOSS_EPS, 1.0 - LOGLOSS_EPS)
        multi_logloss = float(-np.mean(np.log(clipped[np.arange(total), yt])))
        aucs = [_binary_auc(onehot[:, c], p[:, c]) for c in range(num_classes)]
        finite = [a for a in aucs if np.isfinite(a)]
        auc_ovr = float(np.mean(finite)) if finite else float("nan")

    brier = float(np.mean(np.sum((p - onehot) ** 2, axis=1)))

    report["multi_logloss"] = multi_logloss
    report["auc_ovr"] = auc_ovr
    report["brier"] = brier
    return report


def regression_metrics(
    y_true: Sequence[float], y_pred: Sequence[float]
) -> Dict[str, object]:
    """Return the continuous-target metric set as a JSON-serializable dict.

    For a forward-log-return target: ``rmse`` / ``mae`` measure size of error and
    ``directional_accuracy`` the fraction of predictions with the right sign. The
    always-predict-zero forecast is the regression analogue of
    ``majority_baseline_accuracy`` -- ``rmse_zero_baseline`` /
    ``mae_zero_baseline`` -- and ``rmse_over_baseline`` is negative when the model
    beats it.
    """
    yt = np.asarray(y_true, dtype=np.float64).ravel()
    yp = np.asarray(y_pred, dtype=np.float64).ravel()
    if yt.shape != yp.shape:
        raise ValueError(f"y_true {yt.shape} and y_pred {yp.shape} shape mismatch")

    total = int(yt.shape[0])
    resid = yp - yt
    rmse = float(np.sqrt(np.mean(resid ** 2))) if total else 0.0
    mae = float(np.mean(np.abs(resid))) if total else 0.0
    rmse_baseline = float(np.sqrt(np.mean(yt ** 2))) if total else 0.0
    mae_baseline = float(np.mean(np.abs(yt))) if total else 0.0
    directional = float(np.mean(np.sign(yp) == np.sign(yt))) if total else 0.0

    return {
        "n_samples": total,
        "rmse": rmse,
        "mae": mae,
        "directional_accuracy": directional,
        "rmse_zero_baseline": rmse_baseline,
        "mae_zero_baseline": mae_baseline,
        "rmse_over_baseline": rmse - rmse_baseline,
    }


def format_report(report: Dict[str, object]) -> str:
    """Compact human-readable table for stdout."""
    lines: List[str] = []
    lines.append(f"samples={report['n_samples']}  "
                 f"accuracy={report['accuracy']:.4f}  "
                 f"majority_baseline={report['majority_baseline_accuracy']:.4f}  "
                 f"(delta={report['accuracy_over_baseline']:+.4f})")
    ml, auc, brier = report.get("multi_logloss"), report.get("auc_ovr"), report.get("brier")
    if ml is not None:
        lines.append(f"macro_f1={report['macro_f1']:.4f}  multi_logloss={ml:.4f}  "
                     f"auc_ovr={auc:.4f}  brier={brier:.4f}")
    else:
        lines.append(f"macro_f1={report['macro_f1']:.4f}  (no probabilities supplied)")
    for label, m in report["per_class"].items():  # type: ignore[assignment]
        lines.append(f"  {label:<8} P={m['precision']:.3f} R={m['recall']:.3f} "
                     f"F1={m['f1']:.3f} n={m['support']}")
    return "\n".join(lines)


def write_report(report: Dict[str, object], onnx_out_path) -> str:
    """Write *report* as JSON next to the exported model; return the path written."""
    from pathlib import Path

    out = Path(onnx_out_path)
    metrics_path = out.with_name(out.name + ".metrics.json")
    metrics_path.write_text(json.dumps(report, indent=2), encoding="utf-8")
    return str(metrics_path)


# --- self-verification -----------------------------------------------------


def _run_selfcheck() -> None:
    y_true = [0, 0, 1, 1, 2, 2]
    y_pred = [0, 1, 1, 1, 2, 2]
    rep = classification_report_dict(y_true, y_pred, probs=None)

    assert rep["n_samples"] == 6
    assert abs(rep["accuracy"] - 5 / 6) < 1e-9, rep["accuracy"]
    assert abs(rep["majority_baseline_accuracy"] - 1 / 3) < 1e-9
    pc = rep["per_class"]
    assert abs(pc["Up"]["precision"] - 1.0) < 1e-9 and abs(pc["Up"]["recall"] - 0.5) < 1e-9
    assert abs(pc["Down"]["precision"] - 2 / 3) < 1e-9 and abs(pc["Down"]["recall"] - 1.0) < 1e-9
    assert abs(pc["Neutral"]["f1"] - 1.0) < 1e-9
    assert abs(rep["macro_f1"] - (2 * (1 * 0.5) / 1.5 + 0.8 + 1.0) / 3) < 1e-9

    # one-hot probabilities matching the truth: brier 0, perfect OvR AUC, tiny logloss
    onehot = np.eye(3)[np.asarray(y_true)]
    rep_p = classification_report_dict(y_true, y_true, probs=onehot)
    assert rep_p["brier"] == 0.0, rep_p["brier"]
    assert abs(rep_p["auc_ovr"] - 1.0) < 1e-9, rep_p["auc_ovr"]
    assert rep_p["multi_logloss"] < 1e-6, rep_p["multi_logloss"]

    # blurred probabilities stay finite and in range
    rng = np.random.default_rng(0)
    blur = rng.dirichlet(np.ones(3), size=6)
    rep_b = classification_report_dict(y_true, y_pred, probs=blur)
    assert np.isfinite(rep_b["multi_logloss"]) and 0.0 <= rep_b["brier"] <= 2.0
    assert 0.0 <= rep_b["auc_ovr"] <= 1.0 or np.isnan(rep_b["auc_ovr"])

    # numpy AUC identity vs a hand case: scores perfectly separate the positives
    assert abs(_binary_auc(np.array([0, 0, 1, 1]), np.array([0.1, 0.2, 0.8, 0.9])) - 1.0) < 1e-9
    assert abs(_binary_auc(np.array([0, 1, 0, 1]), np.array([0.5, 0.5, 0.5, 0.5])) - 0.5) < 1e-9

    _ = format_report(rep_p)

    # regression_metrics: exact error on a hand case, perfect fit -> zero error,
    # directional accuracy counts sign agreement, and the zero-forecast baseline.
    rm = regression_metrics([0.01, -0.02, 0.03, -0.04], [0.02, -0.01, 0.03, 0.01])
    assert rm["n_samples"] == 4
    assert abs(rm["rmse"] - np.sqrt(np.mean([0.01 ** 2, 0.01 ** 2, 0.0, 0.05 ** 2]))) < 1e-12
    assert abs(rm["mae"] - np.mean([0.01, 0.01, 0.0, 0.05])) < 1e-12
    assert abs(rm["directional_accuracy"] - 0.75) < 1e-12  # last sign disagrees
    assert abs(rm["rmse_zero_baseline"] - np.sqrt(np.mean(np.square([0.01, -0.02, 0.03, -0.04])))) < 1e-12
    perfect = regression_metrics([0.1, -0.2, 0.3], [0.1, -0.2, 0.3])
    assert perfect["rmse"] == 0.0 and perfect["mae"] == 0.0
    assert perfect["directional_accuracy"] == 1.0
    assert perfect["rmse_over_baseline"] <= 0.0
    try:
        regression_metrics([0.1, 0.2], [0.1])
    except ValueError:
        pass
    else:  # pragma: no cover
        raise AssertionError("expected ValueError on shape mismatch")

    print(f"metrics.py selfcheck: OK (sklearn={'yes' if _HAVE_SKLEARN else 'no, numpy fallback'})")


def _parse_args(argv: List[str]) -> argparse.Namespace:
    p = argparse.ArgumentParser(description="Trend-predictor evaluation metrics.")
    p.add_argument("--selfcheck", action="store_true", help="Run formula assertions and exit.")
    return p.parse_args(argv)


if __name__ == "__main__":
    ns = _parse_args(sys.argv[1:])
    if ns.selfcheck:
        _run_selfcheck()
    else:
        print("nothing to do; pass --selfcheck", file=sys.stderr)
        sys.exit(2)
