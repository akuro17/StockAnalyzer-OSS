"""T21 offline counterfactual sensitivity of one validation window: the smallest one-channel input shift that changes the predicted class.

Approved decisions (2026-10-10, ``Y:\\Temp\\sa_decision_options_onnx_research_t17_t22.md``): C01..C19 = star (C01 A optional offline artifact,
C11 A validation population only, C13 A metadata + the split arguments, C14 A standalone CLI, C15 A no C# change), F01..F19 = star:

* F01 perturbation space: a constant offset added in the ONNX INPUT space (after the fixed scaler), the contract of ``explain``'s partial
  dependence. It does not keep OHLC geometry consistent (``explain.PDP_SEMANTICS``); it is NOT a price scenario.
* F02 variable = one base channel (every bar of the window and all of its lag copies, ``explain.groups``). F03 the same offset on every bar.
* F04 objective: another class becomes the argmax. Ties go to the lowest class index (numpy ``argmax``); agreement with the C# tie rule is
  not verified. F05 the shift is reported in model input units (standard deviations when the model has a fixed z-score scaler).
* F06 grid scan from 0 outward (``--grid-points``), then bisection of the first flipping interval. F07 |shift| <= ``--max-shift`` AND every shifted
  cell inside the [min, max] of the fit population of that channel group (the support limit, stated per direction). W01 B (2026-10-10): with ``--allow-out-of-support`` a direction that found nothing inside the
  support is searched again up to ``--max-shift`` and the result is stored apart (``extrapolated``, flagged ``out_of_support``); it never
  enters the in-support figures. F08 ``--tolerance``,
  ``--max-iter``; not reaching the tolerance is ``failed(max_iterations)``.
* F09 per group ``found`` / ``not_found`` / ``failed(reason)``; the overall figure is "the smallest shift found among the searched single-channel
  shifts" -- never called a global minimum. F10 the first bracket only; the number of sign changes on the grid is recorded
  (``monotonic_assumed: false``). F11 positive and negative directions are reported separately.
* F12 one window per run by ``--symbol`` / ``--anchor-date`` (must belong to the validation population); W02 B (2026-10-10): the document
  holds a list of windows keyed by (symbol, anchor date); a run adds its window to a compatible existing document and never drops another
  one. F13 the ONNX probability output.
* F14 classification models only. F15 the fixed scaler sidecar is hash-verified and applied exactly as ``explain`` does.
* F16/F17 standalone CLI; ``<model>.onnx.counterfactual.json`` beside the model, header from ``explain.make_header``. F19 ``--selfcheck``.

Not implemented (it was conditional in F05): the price-unit conversion of a shift. The result is a sensitivity of the model to its input, not
advice and not a statement about the market. The tool never changes the model and nothing here is read by prediction or rendering.

Usage::

    python counterfactual.py --model m.onnx --symbols AAA,BBB --data-start 2021-01-04 --wf-splits 5 --symbol AAA --anchor-date 2026-08-27
    python counterfactual.py --selfcheck
"""

from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import math
import sys
import tempfile
import time
from pathlib import Path
from typing import Callable, Dict, List, Tuple

import numpy as np
import onnxruntime as ort

sys.path.insert(0, str(Path(__file__).resolve().parent))
import checkpoints  # noqa: E402
import dataset as ds  # noqa: E402
import explain  # noqa: E402  (groups, make_header, PDP_SEMANTICS, MAX_MODEL_CALLS)
import model_analysis  # noqa: E402  (channel_names)
import onnx_meta  # noqa: E402
import verify_model as vm  # noqa: E402  (validation-population reconstruction, PrerequisiteError)

SCHEMA_VERSION: int = 1
TOOL: str = "counterfactual.py"
TOOL_VERSION: str = "1"
SIDECAR_SUFFIX: str = ".counterfactual.json"
SPACE: str = "onnx_input_after_scaler"
TIE_RULE: str = "lowest_class_index"
NOTICE_GRID: str = "interval_narrower_than_grid_spacing_is_local"   # J2: a class change between two grid points can be missed; see grid_spacing
NOTICES: Tuple[str, ...] = ("input_space_shift_not_a_price_scenario", "local_boundary_only", "not_advice", NOTICE_GRID)
NOTICE_EXTRAPOLATION: str = "out_of_support_results_are_extrapolation"
COMPATIBLE_KEYS: Tuple[str, ...] = (       # a window joins an existing document only if all of these are identical (W02 B)
    "schema_version", "model_sha256", "data_revision", "feature_contract_hash", "target_contract_hash", "timeframe", "window_size",
    "base_channels", "ordered_channels", "config_sha256", "config", "perturbation", "objective", "search", "population")
MINIMALITY: str = ("smallest |shift| of ONE channel group along its own axis, first flipping interval of a grid scan refined by bisection; "
                   "it says nothing about changes of several channels together or about other regions of the input space")
STATUS_FOUND, STATUS_NOT_FOUND, STATUS_FAILED = "found", "not_found", "failed"
REASON_NON_FINITE, REASON_MAX_ITERATIONS, REASON_NO_FLIP, REASON_NO_RANGE = "non_finite", "max_iterations", "no_flip_in_range", "no_feasible_range"
LIMIT_MAX_SHIFT, LIMIT_SUPPORT = "max_shift", "support"
DIRECTIONS: Tuple[Tuple[str, float], ...] = (("positive", 1.0), ("negative", -1.0))
EXIT_OK, EXIT_FAILED, EXIT_PREREQUISITE = 0, 1, 3     # C17

DEFAULT_MAX_SHIFT: float = 3.0              # F07 candidates 2.0 / 3.0 / 5.0
DEFAULT_GRID_POINTS: int = 61               # F06 candidates 31 / 61 / 121
DEFAULT_TOLERANCE: float = 1e-3             # F08 candidates 1e-2 / 1e-3 / 1e-4
DEFAULT_MAX_ITER: int = 60                  # F08 candidates 40 / 60 / 100
MAX_SHIFT_BAND: Tuple[float, float] = (1e-6, 100.0)
GRID_POINTS_BAND: Tuple[int, int] = (3, 1001)
TOLERANCE_BAND: Tuple[float, float] = (1e-9, 1.0)
MAX_ITER_BAND: Tuple[int, int] = (1, 1000)
MAX_SECONDS_ENV: str = "SA_CF_MAX_SECONDS"
MAX_SECONDS: int = checkpoints.resolve_band(MAX_SECONDS_ENV, 3600, (1, 86_400), (60, 7_200))

PrerequisiteError = vm.PrerequisiteError
Forward = Callable[[np.ndarray], np.ndarray]


# --- The search (model independent) ------------------------------------------------------------------------------------------------


def _checked(forward: Forward, rows: np.ndarray, deadline: float) -> np.ndarray | None:
    """Probabilities of *rows*, or ``None`` when any value is not finite. The run bound is tested on every call."""
    if time.monotonic() > deadline:
        raise TimeoutError("the counterfactual search exceeded --max-seconds")
    probs = np.asarray(forward(rows), dtype=np.float64)
    return probs if np.isfinite(probs).all() else None


def _shifted(x0: np.ndarray, group: np.ndarray, offsets: np.ndarray) -> np.ndarray:
    """Copies of *x0* (1, W, C) with ``offsets[i]`` added to every cell of *group* (float32, the dtype the model reads)."""
    batch = np.repeat(np.asarray(x0, dtype=np.float32), offsets.shape[0], axis=0)
    batch[:, :, group] += offsets.astype(np.float32)[:, None, None]
    return batch


def search_direction(forward: Forward, x0: np.ndarray, group: np.ndarray, sign: float, limit: float, limit_reason: str, *, base_class: int,
                     grid_points: int, tolerance: float, max_iter: int, deadline: float) -> dict:
    """Scan ``0 .. limit`` on a grid for the first class change, then bisect that interval down to *tolerance*."""
    if not limit > 0.0:
        return dict(status=STATUS_NOT_FOUND, reason=REASON_NO_RANGE, search_limit=0.0, limit_reason=limit_reason)
    offsets = np.linspace(0.0, limit, grid_points)
    spacing = float(limit) / (grid_points - 1)                 # J2: the scan cannot see a class change that appears and disappears inside one step
    probs = _checked(forward, _shifted(x0, group, sign * offsets), deadline)
    if probs is None:
        return dict(status=STATUS_FAILED, reason=REASON_NON_FINITE, search_limit=float(limit), limit_reason=limit_reason, grid_spacing=spacing)
    flipped = np.argmax(probs, axis=1) != base_class          # numpy argmax: the lowest class index wins an exact tie (F04)
    flipped[0] = False                                         # offset 0 is the unshifted window: the base class by definition (batch noise cannot flip it)
    changes = int(np.count_nonzero(flipped[1:] != flipped[:-1]))
    common = dict(search_limit=float(limit), limit_reason=limit_reason, grid_spacing=spacing, grid_sign_changes=changes, monotonic_assumed=False)
    if not flipped.any():
        return dict(status=STATUS_NOT_FOUND, reason=REASON_NO_FLIP, **common)
    first = int(np.argmax(flipped))
    lo, hi = float(offsets[first - 1]), float(offsets[first])
    p_lo, p_hi = probs[first - 1], probs[first]
    iterations = 0
    while hi - lo > tolerance:
        if iterations >= max_iter:
            return dict(status=STATUS_FAILED, reason=REASON_MAX_ITERATIONS, iterations=iterations, bracket=[lo, hi], **common)
        mid = 0.5 * (lo + hi)
        p_mid = _checked(forward, _shifted(x0, group, np.array([sign * mid])), deadline)
        if p_mid is None:
            return dict(status=STATUS_FAILED, reason=REASON_NON_FINITE, iterations=iterations, bracket=[lo, hi], **common)
        iterations += 1
        if int(np.argmax(p_mid[0])) != base_class:
            hi, p_hi = mid, p_mid[0]
        else:
            lo, p_lo = mid, p_mid[0]
    return dict(status=STATUS_FOUND, abs_shift=hi, shift=sign * hi, bracket=[lo, hi], iterations=iterations,
                probabilities_below=[float(v) for v in p_lo], probabilities_at=[float(v) for v in p_hi],
                class_at=int(np.argmax(p_hi)), **common)


def support_limits(cells: np.ndarray, low: float, high: float, max_shift: float) -> Tuple[Tuple[float, str], Tuple[float, str]]:
    """((positive limit, reason), (negative limit, reason)): ``min(max_shift, distance to the fit population's range)`` per direction (F07)."""
    up, down = float(high - cells.max()), float(cells.min() - low)
    return (((max_shift, LIMIT_MAX_SHIFT) if max_shift <= up else (max(up, 0.0), LIMIT_SUPPORT)),
            ((max_shift, LIMIT_MAX_SHIFT) if max_shift <= down else (max(down, 0.0), LIMIT_SUPPORT)))


def counterfactual_arrays(forward: Forward, x0: np.ndarray, *, fit_low: np.ndarray, fit_high: np.ndarray, groups: List[np.ndarray],
                          base_channels: List[str], classes: List[str], max_shift: float, grid_points: int, tolerance: float,
                          max_iter: int, deadline: float, allow_out_of_support: bool = False) -> dict:
    """Base class, the per-group / per-direction results and the summary for one window *x0* (1, W, C).

    With *allow_out_of_support*, a direction that is ``not_found`` inside the support is searched again over ``0 .. max_shift`` and the
    result is stored under ``extrapolated`` (with ``out_of_support`` and the share of shifted cells outside the fit range at the
    boundary). Those results are never used for ``status``, ``smallest_abs_shift`` or ``smallest_found_shift``."""
    p0 = _checked(forward, np.asarray(x0, dtype=np.float32), deadline)
    if p0 is None:
        return dict(status="failed", reason=REASON_NON_FINITE)
    base_class = int(np.argmax(p0[0]))
    rows = []
    for g, group in enumerate(groups):
        cells = np.asarray(x0)[:, :, group]
        outside = float(np.mean((cells < fit_low[g]) | (cells > fit_high[g])))
        limits = support_limits(cells, float(fit_low[g]), float(fit_high[g]), max_shift)
        directions = {}
        for (name, sign), (limit, reason) in zip(DIRECTIONS, limits):
            result = search_direction(forward, x0, group, sign, limit, reason, base_class=base_class, grid_points=grid_points,
                                      tolerance=tolerance, max_iter=max_iter, deadline=deadline)
            if allow_out_of_support and result["status"] == STATUS_NOT_FOUND:
                extra = search_direction(forward, x0, group, sign, max_shift, LIMIT_MAX_SHIFT, base_class=base_class,
                                         grid_points=grid_points, tolerance=tolerance, max_iter=max_iter, deadline=deadline)
                if extra["status"] == STATUS_FOUND:
                    moved = cells + sign * extra["abs_shift"]
                    rate = float(np.mean((moved < fit_low[g]) | (moved > fit_high[g])))
                    extra.update(out_of_support_rate_at_boundary=rate, out_of_support=rate > 0.0)
                result["extrapolated"] = extra
            directions[name] = result
        found = [(d["abs_shift"], n) for n, d in directions.items() if d["status"] == STATUS_FOUND]
        failed = [d["reason"] for d in directions.values() if d["status"] == STATUS_FAILED]
        row = dict(channel=base_channels[g], window_out_of_support_rate=outside, directions=directions,
                   status=STATUS_FOUND if found else (STATUS_FAILED if failed else STATUS_NOT_FOUND))
        if found:
            best = min(found)
            row.update(smallest_abs_shift=best[0], smallest_direction=best[1])
        elif failed:
            row["reason"] = failed[0]
        rows.append(row)
    found_rows = [(r["smallest_abs_shift"], i) for i, r in enumerate(rows) if r["status"] == STATUS_FOUND]
    extrapolated = [(d["extrapolated"]["abs_shift"], r["channel"], n) for r in rows for n, d in r["directions"].items()
                    if d.get("extrapolated", {}).get("status") == STATUS_FOUND]
    summary = dict(found=sum(r["status"] == STATUS_FOUND for r in rows), not_found=sum(r["status"] == STATUS_NOT_FOUND for r in rows),
                   failed=sum(r["status"] == STATUS_FAILED for r in rows))
    if allow_out_of_support:
        summary["extrapolated_directions_found"] = len(extrapolated)
    body = dict(status="available", base_class_index=base_class, base_class=classes[base_class],
                probabilities={name: float(p0[0][k]) for k, name in enumerate(classes)}, groups=rows, summary=summary)
    if found_rows:
        shift, index = min(found_rows)
        body["smallest_found_shift"] = dict(channel=rows[index]["channel"], direction=rows[index]["smallest_direction"], abs_shift=shift)
    if extrapolated:
        shift, channel, direction = min(extrapolated)
        body["smallest_extrapolated_shift"] = dict(channel=channel, direction=direction, abs_shift=shift, out_of_support_search=True)
    return body


def model_calls(features: int, grid_points: int, max_iter: int, allow_out_of_support: bool = False) -> int:
    """Upper bound of the model rows one window evaluates: base + per group and direction a grid and at most ``max_iter`` bisection rows
    (twice that with the out-of-support search)."""
    return 1 + features * len(DIRECTIONS) * (grid_points + max_iter) * (2 if allow_out_of_support else 1)


class ConcurrentUpdate(RuntimeError):
    """The output document changed between reading it and replacing it (DF26 A): nothing is written."""


def _fingerprint(path: Path) -> str | None:
    """SHA-256 of the file's bytes, or ``None`` when it does not exist."""
    return hashlib.sha256(path.read_bytes()).hexdigest() if path.is_file() else None


def window_key(window: dict) -> Tuple[str, str]:
    return window["sample"]["symbol"], window["sample"]["anchor_date"]


def merge_document(existing: dict | None, static: dict, window: dict, overwrite: bool) -> dict:
    """The document to publish: *window* added to a compatible *existing* one (W02 B). Another window is never dropped; the same window is
    replaced only with *overwrite*; a document made under a different model / data / setting is refused, not rewritten."""
    if existing is None:
        return dict(static, windows=[window])
    if not isinstance(existing, dict) or existing.get("tool") != TOOL or not isinstance(existing.get("windows"), list):
        raise PrerequisiteError("the existing file is not a counterfactual document; use --out for a separate file")
    differs = [key for key in COMPATIBLE_KEYS if existing.get(key) != static[key]]
    if differs:
        raise PrerequisiteError(f"the existing document was made under a different model, data or setting ({', '.join(differs)}); "
                                f"use --out for a separate document")
    kept = []
    for other in existing["windows"]:
        if window_key(other) == window_key(window):
            if not overwrite:
                raise PrerequisiteError(f"the window {window_key(window)} is already in the document; --overwrite replaces only that window")
            continue
        kept.append(other)
    kept.append(window)
    kept.sort(key=window_key)
    return dict(existing, updated_utc=static["updated_utc"], windows=kept)


# --- Population and sample -------------------------------------------------------------------------------------------------------------


def _fit_support(folds: Dict[str, tuple], transform, groups: List[np.ndarray]) -> Tuple[np.ndarray, np.ndarray]:
    low = np.full(len(groups), np.inf)
    high = np.full(len(groups), -np.inf)
    for x, _y, _anchors, train, _val in folds.values():
        fit = transform(x[train])
        for g, group in enumerate(groups):
            if fit.shape[0]:
                low[g], high[g] = min(low[g], float(fit[:, :, group].min())), max(high[g], float(fit[:, :, group].max()))
    if not (np.isfinite(low).all() and np.isfinite(high).all()):
        raise PrerequisiteError("the fit population is empty; the support of the channels cannot be stated")
    return low, high


def locate_sample(population: dict, symbol: str, anchor_date: datetime.date, groups: List[np.ndarray]) -> dict:
    """The validation window of (*symbol*, *anchor_date*) and the fit support, from the inputs ``verify_model`` rebuilt."""
    context = population["context"]
    dates = context["dates"]
    folds = vm.validation_folds(population)                      # also checks that the per-window rebuild equals the pooled population
    transform = vm.population_transform(population)
    by_name = {name.lower(): name for name in folds}
    if symbol.lower() not in by_name:
        raise PrerequisiteError(f"--symbol {symbol!r} has no validation window; validation symbols: {sorted(folds)}")
    name = by_name[symbol.lower()]
    x, _y, anchors, _train, val = folds[name]
    day = np.asarray(dates[name]).astype("datetime64[D]").reshape(-1)
    anchor_days = day[anchors[val]]
    match = np.flatnonzero(anchor_days == np.datetime64(anchor_date, "D"))
    if match.size == 0:
        raise PrerequisiteError(f"--anchor-date {anchor_date} is not a validation anchor of {name}; they span "
                                f"{anchor_days.min()} .. {anchor_days.max()} ({anchor_days.size} windows)")
    index = int(val[match[0]])
    low, high = _fit_support(folds, transform, groups)
    return dict(symbol=name, anchor_date=anchor_date.isoformat(), anchor_row=int(anchors[index]), x0=transform(x[index:index + 1]),
                fit_low=low, fit_high=high)


# --- Run -------------------------------------------------------------------------------------------------------------------------------


def validate_args(args: argparse.Namespace) -> None:
    if not (isinstance(args.max_shift, (int, float)) and math.isfinite(args.max_shift) and MAX_SHIFT_BAND[0] <= args.max_shift <= MAX_SHIFT_BAND[1]):
        raise ValueError(f"--max-shift must be in {MAX_SHIFT_BAND}")
    if not (isinstance(args.tolerance, (int, float)) and math.isfinite(args.tolerance) and TOLERANCE_BAND[0] <= args.tolerance <= TOLERANCE_BAND[1]):
        raise ValueError(f"--tolerance must be in {TOLERANCE_BAND}")
    for name, band in (("grid_points", GRID_POINTS_BAND), ("max_iter", MAX_ITER_BAND), ("max_seconds", (1, MAX_SECONDS))):
        value = getattr(args, name)
        if type(value) is not int or not band[0] <= value <= band[1]:
            raise ValueError(f"--{name.replace('_', '-')} must be an integer in {band[0]}..{band[1]}")
    vm.validate_population_arguments(args)
    if not args.symbol or not isinstance(args.symbol, str):
        raise ValueError("--symbol is required")
    if not isinstance(args.anchor_date, datetime.date):
        raise ValueError("--anchor-date (YYYY-MM-DD) is required")


def run(args: argparse.Namespace) -> int:
    started = time.monotonic()
    try:
        return _run(args, started)
    except PrerequisiteError as exc:
        print(f"counterfactual: PREREQUISITE NOT MET: {exc}", file=sys.stderr)
        return EXIT_PREREQUISITE
    except ConcurrentUpdate as exc:
        print(f"counterfactual: FAILED: concurrent_update: {exc}", file=sys.stderr)
        return EXIT_FAILED
    except (TimeoutError, ArithmeticError) as exc:
        print(f"counterfactual: FAILED: {exc}", file=sys.stderr)
        return EXIT_FAILED


def _run(args: argparse.Namespace, started: float) -> int:
    validate_args(args)
    deadline = started + args.max_seconds
    model_path = Path(args.model)
    out_path = Path(args.out) if args.out else model_path.with_name(model_path.name + SIDECAR_SUFFIX)
    existing, read_sha = None, None
    if out_path.exists():
        try:
            raw = out_path.read_bytes()
            read_sha = hashlib.sha256(raw).hexdigest()            # J2: what was read is what the replacement must still find
            existing = json.loads(raw.decode("utf-8"))
        except (OSError, ValueError) as exc:
            raise PrerequisiteError(f"{out_path} cannot be read as a document ({exc}); it is left untouched, use --out") from exc
    if not model_path.is_file():
        raise PrerequisiteError(f"model not found: {model_path}")
    sha_before = checkpoints.hash_file(model_path)
    session = ort.InferenceSession(str(model_path), providers=["CPUExecutionProvider"])
    metadata = dict(session.get_modelmeta().custom_metadata_map)
    if metadata.get("target_type", onnx_meta.TARGET_TYPE) != "classification":
        raise PrerequisiteError("regression_not_adopted: counterfactual explanations cover classification models only (F14)")
    classes = list(ds.CLASS_LABELS)
    if metadata.get("class_order", "").split(",") != classes:
        raise PrerequisiteError("the model class order disagrees with the dataset class labels")
    try:
        base_channels, expanded = model_analysis.channel_names(metadata)
        groups = explain.groups(len(base_channels), int(metadata["channels"]))
    except (KeyError, ValueError) as exc:
        raise PrerequisiteError(f"model metadata cannot define the channel groups: {exc}") from exc
    calls = model_calls(len(groups), args.grid_points, args.max_iter, args.allow_out_of_support)
    if calls > explain.MAX_MODEL_CALLS:
        raise PrerequisiteError(f"{calls} model rows exceed the bound {explain.MAX_MODEL_CALLS}; reduce --grid-points or --max-iter")
    population = vm.reconstruct_validation_population(metadata, model_path, args)
    sample = locate_sample(population, args.symbol, args.anchor_date, groups)

    def forward(tensor: np.ndarray) -> np.ndarray:
        (probs,) = session.run(["output"], {"input": np.asarray(tensor, dtype=np.float32)})
        return np.asarray(probs, dtype=np.float64)

    config = dict(max_shift=float(args.max_shift), grid_points=args.grid_points, tolerance=float(args.tolerance), max_iter=args.max_iter,
                  allow_out_of_support=bool(args.allow_out_of_support), wf_splits=args.wf_splits,
                  outer_fold_index=args.outer_fold_index, calibration_segment=bool(args.calibration_segment))
    body = counterfactual_arrays(forward, sample["x0"], fit_low=sample["fit_low"], fit_high=sample["fit_high"], groups=groups,
                                 base_channels=base_channels, classes=classes, max_shift=float(args.max_shift), grid_points=args.grid_points,
                                 tolerance=float(args.tolerance), max_iter=args.max_iter, deadline=deadline,
                                 allow_out_of_support=bool(args.allow_out_of_support))
    if body["status"] != "available":
        raise ArithmeticError(f"the model output for the window is not finite ({body.get('reason')})")
    header = explain.make_header(
        model_sha256=sha_before, data_revision=population["data_revision"], metadata=metadata,
        timeframe=metadata.get(onnx_meta.OUTPUT_TIMEFRAME_KEY, onnx_meta.DEFAULT_TIMEFRAME), window_size=int(metadata["window_size"]),
        base_channels=base_channels, ordered_channels=expanded)
    now = datetime.datetime.now(datetime.timezone.utc).isoformat()
    static = dict(
        header, tool=TOOL, tool_version=TOOL_VERSION, created_utc=now, updated_utc=now, status="available",
        config_sha256=hashlib.sha256(checkpoints.canonical(config).encode()).hexdigest(), config=config,
        perturbation=dict(space=SPACE, shape="constant_offset_on_every_bar_of_the_channel_group_and_its_lag_copies",
                          unit="fixed_scaler_standard_deviations" if population["context"]["scaler"] is not None else "model_input_units",
                          semantics=explain.PDP_SEMANTICS, ohlc_geometry_preserved=False, price_unit_conversion="not_provided"),
        objective=dict(kind="argmax_class_change", tie_rule=TIE_RULE, directions="positive_and_negative_reported_separately",
                       minimality_claim=MINIMALITY),
        search=dict(method="grid_scan_then_bisection", deterministic=True, support="fit_population_min_max_of_the_channel_group",
                    max_shift=float(args.max_shift), grid_points=args.grid_points, tolerance=float(args.tolerance), max_iter=args.max_iter,
                    allow_out_of_support=bool(args.allow_out_of_support)),
        population=dict(source="validation", oos_consumed=population["oos_consumed"], validation_total=int(population["x"].shape[0]),
                        validation_span=population["validation_span"], symbols=population["symbols"]),
        notices=list(NOTICES) + ([NOTICE_EXTRAPOLATION] if args.allow_out_of_support else []))
    static = json.loads(checkpoints.canonical(static))                    # the JSON form is what an existing document is compared with
    window = json.loads(checkpoints.canonical(dict(
        sample=dict(symbol=sample["symbol"], anchor_date=sample["anchor_date"], anchor_row=sample["anchor_row"]), computed_utc=now,
        elapsed_seconds=round(time.monotonic() - started, 3), **{k: v for k, v in body.items() if k != "status"})))
    document = merge_document(existing, static, window, bool(args.overwrite))
    if checkpoints.hash_file(model_path) != sha_before:
        raise ArithmeticError("the model file changed while it was being explained")
    if _fingerprint(out_path) != read_sha:                        # J2 / DF26 A: narrows the race window, does not close it (no lock)
        raise ConcurrentUpdate(f"{out_path} changed (or appeared) after it was read; it is left as the other writer made it, run again")
    checkpoints.atomic_json(out_path, document)
    best = window.get("smallest_found_shift")
    print(f"counterfactual: AVAILABLE  {sample['symbol']} {sample['anchor_date']}  base class {window['base_class']}  "
          f"-> {out_path} ({len(document['windows'])} window(s))")
    print(f"  channels: found {window['summary']['found']}, not_found {window['summary']['not_found']}, failed {window['summary']['failed']}")
    if best:
        print(f"  smallest found shift among the searched single-channel shifts: {best['abs_shift']:.4g} ({best['direction']}, {best['channel']})")
    if "smallest_extrapolated_shift" in window:
        extra = window["smallest_extrapolated_shift"]
        print(f"  OUT OF SUPPORT (extrapolation, kept apart): {extra['abs_shift']:.4g} ({extra['direction']}, {extra['channel']})")
    print("  NOTE: an input-space sensitivity of the model, not a price scenario and not advice.")
    return EXIT_OK


# --- Self-check ------------------------------------------------------------------------------------------------------------------------


def _softmax(z: np.ndarray) -> np.ndarray:
    e = np.exp(z - z.max(axis=1, keepdims=True))
    return e / e.sum(axis=1, keepdims=True)


def _linear_forward(weight: np.ndarray, bias: np.ndarray) -> Forward:
    """Flatten -> MatMul -> Add -> Softmax in numpy (the fixture family); exact analytic boundaries."""
    return lambda x: _softmax(np.asarray(x, dtype=np.float64).reshape(x.shape[0], -1) @ weight + bias)


def _analytic_boundary(weight, bias, x0, group, sign, base_class, window, channels) -> float | None:
    """Smallest delta > 0 where another class overtakes *base_class* along ``x0 + sign*delta`` on *group* (logits are linear in delta)."""
    mask = np.zeros((window, channels))
    mask[:, group] = 1.0
    flat = x0.reshape(-1)
    logits = flat @ weight + bias
    slope = sign * mask.reshape(-1) @ weight
    best = None
    for k in range(weight.shape[1]):
        if k == base_class:
            continue
        rate = slope[k] - slope[base_class]
        gap = logits[base_class] - logits[k]          # >= 0 for the base class (a tie at 0 is resolved by the lowest index)
        if k < base_class and gap == 0:
            return 0.0
        if rate > 0:
            at = gap / rate
            best = at if best is None else min(best, at)
    return best


def _run_selfcheck() -> None:
    import contextlib
    import io

    deadline = time.monotonic() + 600
    window, channels = 4, 5
    rng = np.random.default_rng(3)
    groups = explain.groups(channels, channels)
    classes = list(ds.CLASS_LABELS)
    names = [f"ch{i}" for i in range(channels)]
    common = dict(base_class=0, grid_points=61, tolerance=1e-4, max_iter=60, deadline=deadline)

    # 1. analytic boundary of a linear-logit model, both directions, every channel group
    weight = rng.normal(0.0, 1.5, size=(window * channels, 3))
    bias = rng.normal(0.0, 0.3, size=3)
    forward = _linear_forward(weight, bias)
    x0 = rng.uniform(0.2, 0.8, size=(1, window, channels))
    base = int(np.argmax(forward(x0)[0]))
    low, high = np.full(channels, -100.0), np.full(channels, 100.0)
    body = counterfactual_arrays(forward, x0, fit_low=low, fit_high=high, groups=groups, base_channels=names, classes=classes,
                                 max_shift=3.0, grid_points=61, tolerance=1e-4, max_iter=60, deadline=deadline)
    assert body["status"] == "available" and body["base_class_index"] == base
    checked = 0
    for g, row in enumerate(body["groups"]):
        for name, sign in DIRECTIONS:
            exact = _analytic_boundary(weight, bias, x0[0], groups[g], sign, base, window, channels)
            got = row["directions"][name]
            if exact is not None and exact <= 3.0:
                assert got["status"] == STATUS_FOUND, (g, name, exact, got)
                assert got["bracket"][0] <= exact + 1e-9 <= got["bracket"][1] + 1e-6, (g, name, exact, got["bracket"])
                assert got["bracket"][1] - got["bracket"][0] <= 1e-4 + 1e-12 and got["grid_sign_changes"] == 1
                assert int(np.argmax(got["probabilities_below"])) == base and int(np.argmax(got["probabilities_at"])) != base
                assert got["monotonic_assumed"] is False and got["shift"] == sign * got["abs_shift"]
                checked += 1
            else:
                assert got["status"] == STATUS_NOT_FOUND and got["reason"] == REASON_NO_FLIP, (g, name, exact, got)
    assert checked >= 4, checked
    found = [r for r in body["groups"] if r["status"] == STATUS_FOUND]
    assert body["summary"]["found"] == len(found) and body["smallest_found_shift"]["abs_shift"] == min(r["smallest_abs_shift"] for r in found)
    assert "global" not in json.dumps(body)
    print(f"  group 1 analytic boundaries OK ({checked} directions)")

    # 2. no flip inside the range -> not_found; a support limit and an empty range are stated
    flat = _linear_forward(np.zeros((window * channels, 3)), np.array([1.0, 0.0, 0.0]))
    nf = search_direction(flat, x0, groups[0], 1.0, 3.0, LIMIT_MAX_SHIFT, **common)
    assert nf["status"] == STATUS_NOT_FOUND and nf["reason"] == REASON_NO_FLIP and nf["grid_sign_changes"] == 0
    empty = search_direction(flat, x0, groups[0], 1.0, 0.0, LIMIT_SUPPORT, **common)
    assert empty == dict(status=STATUS_NOT_FOUND, reason=REASON_NO_RANGE, search_limit=0.0, limit_reason=LIMIT_SUPPORT)
    cells = x0[:, :, groups[0]]
    (up, up_reason), (down, down_reason) = support_limits(cells, 0.0, 1.0, 3.0)
    assert up_reason == down_reason == LIMIT_SUPPORT and abs(up - (1.0 - cells.max())) < 1e-12 and abs(down - cells.min()) < 1e-12
    assert support_limits(cells, -10.0, 10.0, 3.0) == ((3.0, LIMIT_MAX_SHIFT), (3.0, LIMIT_MAX_SHIFT))
    assert support_limits(cells, 0.5, 0.6, 3.0)[0][0] == 0.0                  # a window already outside the support has no feasible range
    print("  group 2 not_found / support limit OK")

    # 3. non-finite output -> failed(non_finite), in the grid and inside the bisection
    nan_grid = search_direction(lambda x: np.full((x.shape[0], 3), np.nan), x0, groups[0], 1.0, 3.0, LIMIT_MAX_SHIFT, **common)
    assert nan_grid["status"] == STATUS_FAILED and nan_grid["reason"] == REASON_NON_FINITE
    step = lambda x: np.where((x[:, 0, 0] > x0[0, 0, 0] + 1.0)[:, None], [[0.1, 0.8, 0.1]], [[0.8, 0.1, 0.1]])
    calls = {"n": 0}

    def bad_after_grid(x: np.ndarray) -> np.ndarray:
        calls["n"] += 1
        out = step(x)
        return np.full_like(out, np.nan) if calls["n"] > 1 else out

    nan_mid = search_direction(bad_after_grid, x0, groups[0], 1.0, 3.0, LIMIT_MAX_SHIFT, **common)
    assert nan_mid["status"] == STATUS_FAILED and nan_mid["reason"] == REASON_NON_FINITE and "iterations" in nan_mid and "bracket" in nan_mid
    print("  group 3 non-finite OK")

    # 4. the iteration cap -> failed(max_iterations); enough iterations reach the tolerance
    capped = search_direction(step, x0, groups[0], 1.0, 3.0, LIMIT_MAX_SHIFT, **dict(common, max_iter=2, tolerance=1e-9))
    assert capped["status"] == STATUS_FAILED and capped["reason"] == REASON_MAX_ITERATIONS and capped["iterations"] == 2
    ok = search_direction(step, x0, groups[0], 1.0, 3.0, LIMIT_MAX_SHIFT, **dict(common, tolerance=1e-6))
    assert ok["status"] == STATUS_FOUND and abs(ok["abs_shift"] - 1.0) <= 1e-6 + 1e-6 and ok["iterations"] > 0
    print("  group 4 iteration cap OK")

    # 5. a non-monotone response: the first interval is used, the sign changes are counted, nothing says "monotone"
    def band(x: np.ndarray) -> np.ndarray:
        d = x[:, 0, 0].astype(np.float64) - x0[0, 0, 0]
        flip = (d > 0.5) & (d < 1.5) | (d > 2.2) & (d < 2.6)
        return np.where(flip[:, None], [[0.1, 0.8, 0.1]], [[0.8, 0.1, 0.1]])

    nm = search_direction(band, x0, groups[0], 1.0, 3.0, LIMIT_MAX_SHIFT, **dict(common, grid_points=121))
    assert nm["status"] == STATUS_FOUND and abs(nm["abs_shift"] - 0.5) < 2e-4 and nm["grid_sign_changes"] == 4 and nm["monotonic_assumed"] is False
    print("  group 5 non-monotone OK")

    # 6. the tie rule: the lowest class index wins an exact tie
    after = lambda row, before: (lambda x: np.where((x[:, 0, 0] > x0[0, 0, 0] + 1.0)[:, None], [row], [before]))
    to_zero = search_direction(after([0.4, 0.4, 0.2], [0.2, 0.6, 0.2]), x0, groups[0], 1.0, 3.0, LIMIT_MAX_SHIFT, **dict(common, base_class=1))
    assert to_zero["status"] == STATUS_FOUND and to_zero["class_at"] == 0                 # a 0/1 tie: class 0 (the lowest index) is the argmax
    stays = search_direction(after([0.4, 0.4, 0.2], [0.6, 0.2, 0.2]), x0, groups[0], 1.0, 3.0, LIMIT_MAX_SHIFT, **dict(common, base_class=0))
    assert stays["status"] == STATUS_NOT_FOUND                                            # the same tie keeps class 0: no flip
    noisy = search_direction(after([0.1, 0.8, 0.1], [0.2, 0.6, 0.2]), x0, groups[0], 1.0, 3.0, LIMIT_MAX_SHIFT, **dict(common, base_class=0))
    assert noisy["status"] == STATUS_FOUND and noisy["bracket"][0] == 0.0 and noisy["abs_shift"] <= 3.0 / 60 + 1e-9   # offset 0 is never a flip; the first grid point is
    print("  group 6 tie rule OK")

    # 7. arguments
    base_args = _parse_args(["--model", "m.onnx", "--symbol", "AAA", "--anchor-date", "2026-01-02"])
    validate_args(base_args)
    for bad in (["--max-shift", "0"], ["--max-shift", "nan"], ["--grid-points", "2"], ["--tolerance", "0"], ["--max-iter", "0"],
                ["--max-seconds", "0"], ["--wf-splits", "0"], ["--outer-fold-index", "-1"]):
        try:
            validate_args(_parse_args(["--model", "m.onnx", "--symbol", "AAA", "--anchor-date", "2026-01-02", *bad]))
        except ValueError:
            continue
        raise AssertionError(f"argument {bad} should be rejected")
    assert model_calls(5, 61, 60) == 1 + 5 * 2 * 121
    print("  group 7 arguments OK")

    # 8. out-of-support search (W01 B), model independent: stored apart, flagged, never mixed into the in-support figures
    flip_half = lambda x: np.where((x[:, 0, 0] > x0[0, 0, 0] + 0.5)[:, None], [[0.1, 0.8, 0.1]], [[0.8, 0.1, 0.1]])
    cells0 = x0[:, :, groups[0]]
    narrow_high = np.full(channels, 100.0)
    narrow_high[0] = float(cells0.max()) + 0.1                            # the positive support limit of channel 0 is 0.1
    kwargs = dict(fit_low=np.full(channels, -100.0), fit_high=narrow_high, groups=groups, base_channels=names, classes=classes,
                  max_shift=3.0, grid_points=61, tolerance=1e-4, max_iter=60, deadline=deadline)
    plain = counterfactual_arrays(flip_half, x0, **kwargs)
    assert plain["groups"][0]["status"] == STATUS_NOT_FOUND and "extrapolated" not in json.dumps(plain) and "smallest_extrapolated_shift" not in plain
    wide = counterfactual_arrays(flip_half, x0, allow_out_of_support=True, **kwargs)
    pos = wide["groups"][0]["directions"]["positive"]
    assert pos["status"] == STATUS_NOT_FOUND and pos["reason"] == REASON_NO_FLIP and pos["search_limit"] < 0.2        # in-support: nothing
    assert pos["extrapolated"]["status"] == STATUS_FOUND and abs(pos["extrapolated"]["abs_shift"] - 0.5) < 2e-4
    assert pos["extrapolated"]["out_of_support"] is True and pos["extrapolated"]["out_of_support_rate_at_boundary"] > 0.0
    assert wide["groups"][0]["status"] == STATUS_NOT_FOUND and "smallest_found_shift" not in wide
    assert wide["smallest_extrapolated_shift"]["channel"] == names[0] and wide["smallest_extrapolated_shift"]["out_of_support_search"] is True
    assert wide["summary"]["found"] == 0 and wide["summary"]["extrapolated_directions_found"] == 1
    assert wide["groups"][1]["directions"]["positive"]["extrapolated"]["status"] == STATUS_NOT_FOUND     # an independent channel flips nowhere
    inside = counterfactual_arrays(flip_half, x0, allow_out_of_support=True, **dict(kwargs, fit_high=np.full(channels, 100.0)))
    assert inside["groups"][0]["status"] == STATUS_FOUND and "extrapolated" not in inside["groups"][0]["directions"]["positive"]
    assert model_calls(5, 61, 60, True) == 1 + 5 * 2 * 121 * 2 and model_calls(5, 61, 60) == 1 + 5 * 2 * 121
    print("  group 8 out-of-support search (model independent) OK")

    # 9. end to end on an ONNX fixture whose boundary is analytic; population rebuilt from the metadata like T17
    import onnx
    from onnx import numpy_helper

    w, c, horizon, splits = 10, ds.FEATURES_OHLCV, 5, ds.DEFAULT_WF_SPLITS
    fixture_names = ("AAA", "BBB", "CCC")
    with tempfile.TemporaryDirectory(prefix="sa_cf_selfcheck_") as tmp_name:
        tmp = Path(tmp_name)
        series = vm._fixture_series(330, fixture_names, 7)
        data_dir = tmp / "data"
        vm._write_fixture_parquet(data_dir, series, 300)
        symbols, dates = ds.load_parquet_dir(data_dir, return_dates=True)
        ranges = ds.train_val_date_ranges(symbols, dates, feature_mode="ohlcv_minmax", window=w, horizon=horizon,
                                          threshold=ds.DEFAULT_THRESHOLD, n_splits=splits)
        meta = onnx_meta.build_contract(feature_mode="ohlcv_minmax", window_size=w, channels=c, horizon=horizon, threshold=ds.DEFAULT_THRESHOLD,
                                        wf_splits=splits, seed=42, producer="counterfactual.py selfcheck", date_ranges=ranges)
        model = tmp / "cf.onnx"
        vm.build_fixture_model(model, window=w, channels=c, classes=3, seed=5, metadata=meta, weight_scale=3.0)
        initializers = {i.name: numpy_helper.to_array(i) for i in onnx.load(str(model)).graph.initializer}
        mw, mb = initializers["W"].astype(np.float64), initializers["B"].astype(np.float64)
        # the least confident validation window of BBB: its class boundary lies closest, i.e. inside the (narrow) min-max support
        probe = _parse_args(["--model", str(model), "--data-dir", str(data_dir), "--symbol", "BBB", "--anchor-date", ranges["validation_end"]])
        meta_dict = {p.key: p.value for p in onnx.load(str(model)).metadata_props}
        probe_pop = vm.reconstruct_validation_population(meta_dict, model, probe)
        first_day = datetime.date.fromisoformat(ranges["validation_start"])
        last_day = datetime.date.fromisoformat(ranges["validation_end"])
        margins = []
        for offset in range((last_day - first_day).days + 1):
            day = first_day + datetime.timedelta(days=offset)
            try:
                found_sample = locate_sample(probe_pop, "BBB", day, explain.groups(c, c))
            except PrerequisiteError:
                continue
            top2 = np.sort(_linear_forward(mw, mb)(found_sample["x0"])[0])[-2:]
            margins.append((float(top2[1] - top2[0]), day))
        anchor_day = min(margins)[1]
        base_argv = ["--model", str(model), "--data-dir", str(data_dir), "--symbol", "BBB", "--anchor-date", anchor_day.isoformat(),
                     "--tolerance", "1e-4", "--max-seconds", "300"]
        args = _parse_args(base_argv)
        with contextlib.redirect_stdout(io.StringIO()):
            assert run(args) == EXIT_OK
        out = model.with_name(model.name + SIDECAR_SUFFIX)
        doc = json.loads(out.read_text(encoding="utf-8"))
        assert doc["status"] == "available" and len(doc["windows"]) == 1 and doc["population"]["oos_consumed"] is False
        win = doc["windows"][0]
        assert win["sample"]["symbol"] == "BBB" and doc["search"]["allow_out_of_support"] is False and NOTICE_EXTRAPOLATION not in doc["notices"]
        assert doc["model_sha256"] == checkpoints.hash_file(model) and doc["perturbation"]["ohlc_geometry_preserved"] is False
        assert doc["objective"]["tie_rule"] == TIE_RULE and "global" not in json.dumps(doc) and win["summary"]["failed"] == 0
        assert "extrapolated" not in json.dumps(doc) and "smallest_extrapolated_shift" not in win
        # the analytic check: rebuild the same window and support independently with the logits of the stored weights
        pop = probe_pop
        sample = locate_sample(pop, "BBB", anchor_day, explain.groups(c, c))
        base = int(np.argmax(_linear_forward(mw, mb)(sample["x0"])[0]))
        assert win["base_class_index"] == base and sample["anchor_row"] == win["sample"]["anchor_row"]
        verified = 0
        for g, row in enumerate(win["groups"]):
            cells = sample["x0"][:, :, g:g + 1]
            limits = support_limits(cells, float(sample["fit_low"][g]), float(sample["fit_high"][g]), DEFAULT_MAX_SHIFT)
            for (name, sign), (limit, _reason) in zip(DIRECTIONS, limits):
                exact = _analytic_boundary(mw, mb, sample["x0"][0].astype(np.float64), [g], sign, base, w, c)
                got = row["directions"][name]
                if exact is not None and 0.0 < exact <= limit - 1e-3:
                    assert got["status"] == STATUS_FOUND and got["bracket"][0] - 1e-5 <= exact <= got["bracket"][1] + 1e-5, (g, name, exact, got)
                    verified += 1
                elif exact is None or exact > limit + 1e-3:
                    assert got["status"] == STATUS_NOT_FOUND, (g, name, exact, limit, got)
        assert verified >= 1, "the fixture must flip at least once inside the support"
        # W02 B: a second window joins the document; the first stays exactly as it was
        second_day = sorted(margins)[1][1]
        argv_second = [a if a != anchor_day.isoformat() else second_day.isoformat() for a in base_argv]
        before_bytes = out.read_bytes()
        with contextlib.redirect_stdout(io.StringIO()):
            assert run(_parse_args(argv_second)) == EXIT_OK
        merged = json.loads(out.read_text(encoding="utf-8"))
        assert [w["sample"]["anchor_date"] for w in merged["windows"]] == sorted([anchor_day.isoformat(), second_day.isoformat()])
        assert win in merged["windows"] and merged["created_utc"] == doc["created_utc"] and merged["updated_utc"] >= merged["created_utc"]
        assert {k: merged[k] for k in COMPATIBLE_KEYS} == {k: doc[k] for k in COMPATIBLE_KEYS}
        # the same window again is refused without --overwrite (the file is untouched); with it only that entry is replaced
        merged_bytes = out.read_bytes()
        with contextlib.redirect_stderr(io.StringIO()) as err:
            assert run(_parse_args(base_argv)) == EXIT_PREREQUISITE
        assert "already in the document" in err.getvalue() and out.read_bytes() == merged_bytes
        with contextlib.redirect_stdout(io.StringIO()):
            assert run(_parse_args(base_argv + ["--overwrite"])) == EXIT_OK
        replaced = json.loads(out.read_text(encoding="utf-8"))
        assert len(replaced["windows"]) == 2 and [w for w in replaced["windows"] if w["sample"]["anchor_date"] == second_day.isoformat()] == \
            [w for w in merged["windows"] if w["sample"]["anchor_date"] == second_day.isoformat()]
        # a document made under another setting is refused and kept; so is a file that is not a document
        replaced_bytes = out.read_bytes()
        with contextlib.redirect_stderr(io.StringIO()) as err:
            assert run(_parse_args(base_argv + ["--max-shift", "2.5", "--overwrite"])) == EXIT_PREREQUISITE
        assert "different model, data or setting" in err.getvalue() and out.read_bytes() == replaced_bytes
        junk = tmp / "junk.json"
        junk.write_text("not json", encoding="utf-8")
        with contextlib.redirect_stderr(io.StringIO()) as err:
            assert run(_parse_args(base_argv + ["--out", str(junk), "--overwrite"])) == EXIT_PREREQUISITE
        assert junk.read_text(encoding="utf-8") == "not json" and "cannot be read as a document" in err.getvalue()
        other = tmp / "other.json"
        other.write_text(json.dumps({"tool": "else", "windows": []}), encoding="utf-8")
        with contextlib.redirect_stderr(io.StringIO()) as err:
            assert run(_parse_args(base_argv + ["--out", str(other), "--overwrite"])) == EXIT_PREREQUISITE
        assert "not a counterfactual document" in err.getvalue()
        with contextlib.redirect_stderr(io.StringIO()) as err:        # a date that is not a validation anchor is a prerequisite failure
            assert run(_parse_args(base_argv[:6] + ["--anchor-date", "1999-01-04"] + base_argv[8:] + ["--overwrite"])) == EXIT_PREREQUISITE
            assert run(_parse_args([a if a != "BBB" else "NOPE" for a in base_argv] + ["--overwrite"])) == EXIT_PREREQUISITE
        assert "is not a validation anchor" in err.getvalue() and "has no validation window" in err.getvalue()
        sha = checkpoints.hash_file(model)
        assert out.read_bytes() == replaced_bytes and before_bytes != merged_bytes
        assert checkpoints.hash_file(model) == sha                     # the model file is never changed

        # D2: a missing or synthetic --data-dir is a prerequisite (exit 3), not a traceback; no document is written
        import polars as pl

        synth_dir = tmp / "synthetic_data"
        synth_dir.mkdir()
        for parquet in data_dir.glob("*.parquet"):
            pl.read_parquet(parquet).write_parquet(synth_dir / parquet.name, metadata={ds.SYNTHETIC_ORIGIN_KEY: ds.SYNTHETIC_ORIGIN})
        for bad_dir in (tmp / "no_such_data_dir", synth_dir):
            target = tmp / f"never_written_{bad_dir.name}.json"
            bad_argv = [str(bad_dir) if a == str(data_dir) else a for a in base_argv] + ["--out", str(target)]
            with contextlib.redirect_stderr(io.StringIO()) as err:
                assert run(_parse_args(bad_argv)) == EXIT_PREREQUISITE, bad_dir
            assert "PREREQUISITE NOT MET" in err.getvalue() and not target.exists(), (bad_dir, err.getvalue())

        # J2 / DF26 A: a document that changed (or appeared) between reading and replacing it is not overwritten: exit 1, concurrent_update
        from unittest import mock

        real_arrays = counterfactual_arrays

        def other_writer(path: Path, payload: str):
            def wrapped(*args_, **kwargs_):
                body_ = real_arrays(*args_, **kwargs_)
                path.write_text(payload, encoding="utf-8")                    # another process finishes first
                return body_
            return wrapped

        racing, stolen = tmp / "racing.json", json.dumps({"other": "writer"})
        with contextlib.redirect_stdout(io.StringIO()):
            assert run(_parse_args(base_argv + ["--out", str(racing)])) == EXIT_OK            # an ordinary document first
        with mock.patch.object(sys.modules[__name__], "counterfactual_arrays", other_writer(racing, stolen)),                 contextlib.redirect_stderr(io.StringIO()) as err:
            assert run(_parse_args(argv_second + ["--out", str(racing)])) == EXIT_FAILED
        assert "concurrent_update" in err.getvalue() and racing.read_text(encoding="utf-8") == stolen, "the other writer's file must stay as it is"
        appearing = tmp / "appearing.json"                                                    # absent when read, present before the replacement
        with mock.patch.object(sys.modules[__name__], "counterfactual_arrays", other_writer(appearing, stolen)),                 contextlib.redirect_stderr(io.StringIO()) as err:
            assert run(_parse_args(base_argv + ["--out", str(appearing)])) == EXIT_FAILED
        assert "concurrent_update" in err.getvalue() and appearing.read_text(encoding="utf-8") == stolen
        assert not list(tmp.glob(".incoming_*")), "no temporary file is left behind"
        calm = tmp / "calm.json"
        with contextlib.redirect_stdout(io.StringIO()):
            assert run(_parse_args(base_argv + ["--out", str(calm)])) == EXIT_OK and run(_parse_args(argv_second + ["--out", str(calm)])) == EXIT_OK
        assert len(json.loads(calm.read_text(encoding="utf-8"))["windows"]) == 2, "without a concurrent writer the merge works as before"

        # 10. W01 B end to end on the same fixture: in-support results are identical, the extrapolation is stored apart and matches the analytic boundary
        wide = tmp / "wide.json"
        with contextlib.redirect_stdout(io.StringIO()):
            assert run(_parse_args(base_argv + ["--allow-out-of-support", "--out", str(wide)])) == EXIT_OK
        wdoc = json.loads(wide.read_text(encoding="utf-8"))
        assert wdoc["search"]["allow_out_of_support"] is True and NOTICE_EXTRAPOLATION in wdoc["notices"]
        assert wdoc["config_sha256"] != doc["config_sha256"]
        wwin = wdoc["windows"][0]
        checked_extra = 0
        for g, (row, plain) in enumerate(zip(wwin["groups"], win["groups"])):
            cells = sample["x0"][:, :, g:g + 1]
            limits = support_limits(cells, float(sample["fit_low"][g]), float(sample["fit_high"][g]), DEFAULT_MAX_SHIFT)
            for (name, sign), (limit, _reason) in zip(DIRECTIONS, limits):
                got = {k: v for k, v in row["directions"][name].items() if k != "extrapolated"}
                assert got == plain["directions"][name], (g, name)         # the in-support figures do not change
                extra = row["directions"][name].get("extrapolated")
                if plain["directions"][name]["status"] != STATUS_NOT_FOUND:
                    assert extra is None
                    continue
                assert extra is not None
                exact = _analytic_boundary(mw, mb, sample["x0"][0].astype(np.float64), [g], sign, base, w, c)
                if exact is not None and limit + 1e-3 < exact <= DEFAULT_MAX_SHIFT - 1e-3:
                    assert extra["status"] == STATUS_FOUND and extra["out_of_support"] is True and extra["out_of_support_rate_at_boundary"] > 0.0
                    assert extra["bracket"][0] - 1e-5 <= exact <= extra["bracket"][1] + 1e-5, (g, name, exact, extra)
                    checked_extra += 1
                elif exact is None or exact > DEFAULT_MAX_SHIFT + 1e-3:
                    assert extra["status"] == STATUS_NOT_FOUND
        assert checked_extra >= 1, "the fixture must flip outside the support at least once"
        assert wwin["smallest_extrapolated_shift"]["out_of_support_search"] is True and wwin["summary"]["extrapolated_directions_found"] >= 1
        assert wwin["summary"]["found"] == win["summary"]["found"] and wwin.get("smallest_found_shift") == win.get("smallest_found_shift")
    print(f"  group 9 end to end OK ({verified} analytic directions, merge / overwrite / refusals)")
    print(f"  group 10 out-of-support search end to end OK ({checked_extra} analytic extrapolated directions)")

    # 11. D5 was NOT reproduced: the model reads float32, but the search runs on the float64 offset and the class is a step function of it,
    # so the bisection reaches --tolerance 1e-9 (the band floor) at any cell magnitude. The result is only as fine as the float32 cell
    # spacing (stated below), which is a statement about accuracy, not a convergence failure. No tolerance floor is applied.
    w5, c5, boundary = 4, 2, 0.123456789
    for cell in (0.0, 1.0, 1e3):
        x5 = np.full((1, w5, c5), cell, dtype=np.float32)
        weight5, bias5 = np.zeros((w5 * c5, 3)), np.zeros(3)
        weight5[[b * c5 for b in range(w5)], 1] = 0.25
        bias5[1] = -0.25 * w5 * cell - 0.25 * w5 * boundary
        forward5 = _linear_forward(weight5, bias5)
        got = search_direction(forward5, x5, np.array([0]), 1.0, 3.0, LIMIT_MAX_SHIFT, base_class=int(np.argmax(forward5(x5)[0])),
                               grid_points=61, tolerance=TOLERANCE_BAND[0], max_iter=60, deadline=deadline)
        spacing = float(np.spacing(np.float32(abs(cell) + 3.0)))
        assert got["status"] == STATUS_FOUND and got["iterations"] < 60 and got["bracket"][1] - got["bracket"][0] <= TOLERANCE_BAND[0], (cell, got)
        assert abs(got["abs_shift"] - boundary) <= spacing + 1e-6, (cell, got["abs_shift"], spacing)
    print("  group 11 float32 tolerance (D5 not reproduced) OK: found at 1e-9 for cells 0, 1, 1e3")

    # 12. J2: every scanned direction states its grid spacing (limit / (grid_points - 1)); a direction without a range scans nothing and has none
    spaced = search_direction(step, x0, groups[0], 1.0, 3.0, LIMIT_MAX_SHIFT, **common)
    assert spaced["status"] == STATUS_FOUND and spaced["grid_spacing"] == 3.0 / 60 and spaced["bracket"][1] - spaced["bracket"][0] <= 1e-4 < spaced["grid_spacing"]
    assert search_direction(flat, x0, groups[0], 1.0, 3.0, LIMIT_MAX_SHIFT, **common)["grid_spacing"] == 3.0 / 60       # not_found keeps it too
    assert search_direction(lambda x: np.full((x.shape[0], 3), np.nan), x0, groups[0], 1.0, 3.0, LIMIT_MAX_SHIFT, **common)["grid_spacing"] == 3.0 / 60
    assert "grid_spacing" not in search_direction(flat, x0, groups[0], 1.0, 0.0, LIMIT_SUPPORT, **common)
    assert search_direction(step, x0, groups[0], 1.0, 1.5, LIMIT_SUPPORT, **dict(common, grid_points=31))["grid_spacing"] == 1.5 / 30
    whole = counterfactual_arrays(step, x0, fit_low=np.full(channels, -100.0), fit_high=np.full(channels, 100.0), groups=groups, base_channels=names,
                                  classes=classes, max_shift=3.0, grid_points=61, tolerance=1e-4, max_iter=60, deadline=deadline)
    assert all(d["grid_spacing"] == 3.0 / 60 for row in whole["groups"] for d in row["directions"].values())
    assert NOTICE_GRID in NOTICES
    print("  group 12 J2 grid spacing OK")
    print("counterfactual.py selfcheck: OK (12 groups passed)")


def _iso_date(text: str) -> datetime.date:
    try:
        return datetime.date.fromisoformat(text)
    except ValueError as exc:
        raise argparse.ArgumentTypeError(f"expected YYYY-MM-DD, got {text!r}") from exc


def _parse_args(argv: List[str]) -> argparse.Namespace:
    p = argparse.ArgumentParser(description="Smallest one-channel input shift that changes the predicted class of one validation window.")
    p.add_argument("--selfcheck", action="store_true", help="Run the assertions and exit.")
    p.add_argument("--model", type=Path, required=True)
    p.add_argument("--symbol", required=True, help="Symbol of the validation window.")
    p.add_argument("--anchor-date", type=_iso_date, required=True, help="Anchor date (YYYY-MM-DD) of the validation window.")
    vm.add_population_arguments(p)
    p.add_argument("--max-shift", type=float, default=DEFAULT_MAX_SHIFT)
    p.add_argument("--grid-points", type=int, default=DEFAULT_GRID_POINTS)
    p.add_argument("--tolerance", type=float, default=DEFAULT_TOLERANCE)
    p.add_argument("--max-iter", type=int, default=DEFAULT_MAX_ITER)
    p.add_argument("--allow-out-of-support", action="store_true",
                   help="Also search beyond the fit range where nothing was found inside it; reported apart and flagged (W01 B).")
    p.add_argument("--max-seconds", type=int, default=MAX_SECONDS, help=f"Run bound; env {MAX_SECONDS_ENV}.")
    p.add_argument("--out", type=Path, default=None, help="Default: <model>.onnx.counterfactual.json")
    p.add_argument("--overwrite", action="store_true",
                   help="Replace the entry of the same (symbol, anchor date) window; other windows of the document are kept.")
    return p.parse_args(argv)


if __name__ == "__main__":
    if "--selfcheck" in sys.argv[1:]:
        _run_selfcheck()
    else:
        _args = _parse_args(sys.argv[1:])
        try:
            validate_args(_args)
        except ValueError as _exc:
            print(f"counterfactual: invalid argument: {_exc}", file=sys.stderr)
            raise SystemExit(2)
        raise SystemExit(run(_args))
