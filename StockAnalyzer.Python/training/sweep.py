"""Compare trend-predictor models across window sizes and feature modes.

Drives one trainer (``train_pytorch`` / ``train_lightgbm`` / ``train_tensorflow``)
over the cartesian product of ``--windows`` x ``--feature-modes``, then tabulates
each run's ``<out>.onnx.metrics.json`` (written by :mod:`metrics`) so window and
feature representation can be judged on equal footing rather than by anecdote
(WebAI review #2 / #4).

Every metric is reported next to ``majority_baseline_accuracy`` -- a model that
only beats accuracy by predicting the majority class is not an improvement.

Examples
--------
    python sweep.py --trainer pytorch --windows 10,20,40,60 --feature-modes ohlcv_minmax,zscore
    python sweep.py --trainer lightgbm --windows 20,40 --feature-modes zscore_joint,log_return_ohlc --max-symbols 40
    python sweep.py --selfcheck
"""

from __future__ import annotations

import argparse
import csv
import json
import math
import shlex
import subprocess
import sys
from pathlib import Path
from typing import Dict, List, Sequence

sys.path.insert(0, str(Path(__file__).resolve().parent))
import dataset as ds  # noqa: E402  (sibling module; also the FEATURE_MODES SSoT)

_TRAINING_DIR: Path = Path(__file__).resolve().parent
TRAINERS: Dict[str, str] = {
    "pytorch": "train_pytorch.py",
    "lightgbm": "train_lightgbm.py",
    "tensorflow": "train_tensorflow.py",
}
# trainers that accept --arch / --epochs (lightgbm uses --n-estimators instead).
_ARCH_EPOCH_TRAINERS = ("pytorch", "tensorflow")
DEFAULT_WINDOWS: str = "10,20,40,60"
DEFAULT_OUT_DIR: Path = _TRAINING_DIR / "artifacts" / "sweep"

# columns pulled from each run's metrics JSON, in report order.
_METRIC_COLUMNS: Sequence[str] = (
    "accuracy",
    "majority_baseline_accuracy",
    "accuracy_over_baseline",
    "macro_f1",
    "multi_logloss",
    "auc_ovr",
    "brier",
)


def _parse_int_list(text: str) -> List[int]:
    return [int(part) for part in text.split(",") if part.strip()]


def _parse_str_list(text: str) -> List[str]:
    return [part.strip() for part in text.split(",") if part.strip()]


def build_command(
    args: argparse.Namespace, feature_mode: str, window: int, out_path: Path
) -> List[str]:
    """Assemble the trainer subprocess command for one sweep cell."""
    cmd = [
        args.python, str(_TRAINING_DIR / TRAINERS[args.trainer]),
        "--feature-mode", feature_mode,
        "--window", str(window),
        "--out", str(out_path),
        "--no-verify",
    ]
    if args.data_dir is not None:
        cmd += ["--data-dir", str(args.data_dir)]
    if args.max_symbols is not None:
        cmd += ["--max-symbols", str(args.max_symbols)]
    if args.trainer in _ARCH_EPOCH_TRAINERS:
        if args.arch is not None:
            cmd += ["--arch", args.arch]
        if args.epochs is not None:
            cmd += ["--epochs", str(args.epochs)]
    if args.extra:
        cmd += shlex.split(args.extra)
    return cmd


def _metric_sort_key(row: Dict[str, object]) -> float:
    value = row.get("macro_f1")
    return float(value) if isinstance(value, (int, float)) and math.isfinite(value) else -math.inf


def format_table(rows: Sequence[Dict[str, object]]) -> str:
    """Aligned text table, best ``macro_f1`` first (non-finite last)."""
    headers = ["feature_mode", "window", *_METRIC_COLUMNS]
    ordered = sorted(rows, key=_metric_sort_key, reverse=True)

    def cell(row: Dict[str, object], key: str) -> str:
        value = row.get(key)
        if value is None or (isinstance(value, float) and not math.isfinite(value)):
            return "-"
        if key == "window":
            return str(value)
        if key == "feature_mode":
            return str(value)
        return f"{float(value):.4f}"

    table = [headers] + [[cell(r, h) for h in headers] for r in ordered]
    widths = [max(len(line[i]) for line in table) for i in range(len(headers))]
    return "\n".join(
        "  ".join(field.ljust(widths[i]) for i, field in enumerate(line))
        for line in table
    )


def run_sweep(args: argparse.Namespace) -> List[Dict[str, object]]:
    windows = _parse_int_list(args.windows)
    modes = _parse_str_list(args.feature_modes)
    unknown = [m for m in modes if m not in ds.FEATURE_MODES]
    if unknown:
        raise SystemExit(f"unknown feature mode(s) {unknown}; known: {list(ds.FEATURE_MODES)}")

    out_dir = Path(args.out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)

    rows: List[Dict[str, object]] = []
    for mode in modes:
        for window in windows:
            out_path = out_dir / f"{args.trainer}_{mode}_w{window}.onnx"
            cmd = build_command(args, mode, window, out_path)
            print(f"\n=== {mode} window={window} ===\n{' '.join(cmd)}")
            if args.dry_run:
                continue
            subprocess.run(cmd, check=True)

            metrics_path = out_path.with_name(out_path.name + ".metrics.json")
            report = json.loads(metrics_path.read_text(encoding="utf-8"))
            row: Dict[str, object] = {"feature_mode": mode, "window": window}
            for key in _METRIC_COLUMNS:
                row[key] = report.get(key)
            rows.append(row)

    if rows:
        (out_dir / "summary.json").write_text(json.dumps(rows, indent=2), encoding="utf-8")
        with (out_dir / "summary.csv").open("w", newline="", encoding="utf-8") as handle:
            writer = csv.DictWriter(handle, fieldnames=["feature_mode", "window", *_METRIC_COLUMNS])
            writer.writeheader()
            writer.writerows(rows)
        print("\n" + format_table(rows))
        print(f"\nwrote {out_dir / 'summary.json'} and {out_dir / 'summary.csv'}")
    return rows


def _run_selfcheck() -> None:
    rows = [
        {"feature_mode": "ohlcv_minmax", "window": 20, "accuracy": 0.55,
         "majority_baseline_accuracy": 0.50, "accuracy_over_baseline": 0.05,
         "macro_f1": 0.48, "multi_logloss": 1.02, "auc_ovr": 0.58, "brier": 0.63},
        {"feature_mode": "zscore", "window": 40, "accuracy": 0.61,
         "majority_baseline_accuracy": 0.50, "accuracy_over_baseline": 0.11,
         "macro_f1": 0.59, "multi_logloss": 0.95, "auc_ovr": 0.64, "brier": 0.58},
        {"feature_mode": "log_return", "window": 10, "accuracy": 0.50,
         "majority_baseline_accuracy": 0.50, "accuracy_over_baseline": 0.0,
         "macro_f1": float("nan"), "multi_logloss": None, "auc_ovr": float("nan"), "brier": 0.66},
    ]
    table = format_table(rows)
    lines = table.splitlines()
    assert lines[0].split()[0] == "feature_mode", lines[0]
    assert lines[1].startswith("zscore"), lines[1]          # best macro_f1 first
    assert lines[-1].startswith("log_return"), lines[-1]    # non-finite macro_f1 last
    assert "-" in lines[-1].split(), lines[-1]              # nan / None cells render as "-"

    ns = _parse_args([
        "--trainer", "lightgbm", "--windows", "10,20", "--feature-modes", "ohlcv_minmax",
        "--max-symbols", "4", "--dry-run",
    ])
    cmd = build_command(ns, "ohlcv_minmax", 20, Path("x.onnx"))
    assert "--arch" not in cmd and "--epochs" not in cmd, cmd  # lightgbm gets neither
    assert cmd[:2] == [ns.python, str(_TRAINING_DIR / "train_lightgbm.py")], cmd
    assert "--no-verify" in cmd and "--max-symbols" in cmd

    ns_pt = _parse_args([
        "--trainer", "pytorch", "--windows", "10", "--feature-modes", "zscore",
        "--arch", "cnn", "--epochs", "3", "--dry-run",
    ])
    cmd_pt = build_command(ns_pt, "zscore", 10, Path("y.onnx"))
    assert "--arch" in cmd_pt and "cnn" in cmd_pt and "--epochs" in cmd_pt, cmd_pt

    print("sweep.py selfcheck: OK")


def _parse_args(argv: Sequence[str]) -> argparse.Namespace:
    p = argparse.ArgumentParser(description="Window / feature-mode comparison sweep for the trend predictor.")
    p.add_argument("--trainer", choices=sorted(TRAINERS), default="pytorch")
    p.add_argument("--windows", default=DEFAULT_WINDOWS, help="Comma-separated window sizes.")
    p.add_argument("--feature-modes", default="ohlcv_minmax,zscore",
                   help=f"Comma-separated; any of {list(ds.FEATURE_MODES)}.")
    p.add_argument("--arch", default=None, help="pytorch/tensorflow only.")
    p.add_argument("--epochs", type=int, default=None, help="pytorch/tensorflow only.")
    p.add_argument("--max-symbols", type=int, default=None)
    p.add_argument("--data-dir", type=Path, default=None)
    p.add_argument("--out-dir", type=Path, default=DEFAULT_OUT_DIR)
    p.add_argument("--python", default=sys.executable, help="Interpreter for the trainer subprocess.")
    p.add_argument("--extra", default="", help="Extra args appended verbatim to every trainer command.")
    p.add_argument("--dry-run", action="store_true", help="Print the trainer commands without running them.")
    p.add_argument("--selfcheck", action="store_true", help="Run assertions and exit.")
    return p.parse_args(list(argv))


def main(argv: Sequence[str]) -> int:
    args = _parse_args(argv)
    if args.selfcheck:
        _run_selfcheck()
        return 0
    run_sweep(args)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
