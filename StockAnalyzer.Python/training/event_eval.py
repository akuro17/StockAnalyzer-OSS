"""T22 event-window evaluation: does a trained classifier behave differently on validation windows near events the user lists?

Approved decisions (2026-10-10, ``Y:\\Temp\\sa_decision_options_onnx_research_t17_t22.md`` section 7 and
``Y:\\Temp\\sa_decision_options_onnx_research_remaining.md`` section 2): R1 = hold lifted, V01 = A, R1b = A, V02..V19 = star.

* V01 A the ONLY event source is a user-supplied, versioned CSV. There is no network access here; where the dates come from, and the right to
  use them, is the user's responsibility (``source_note``). The tool cannot verify that an event date is correct.
* R1b A the importer, the evaluator and ``--selfcheck`` on SYNTHETIC events are what exists. No real event set has been evaluated; every report
  says ``real_event_evaluation: unverified``.
* V03 CSV columns ``event_id, event_type, symbol_scope, timezone, announced_at, effective_at, revision, source_note`` (``effective_at`` may be
  empty = ``announced_at``; timestamps are ISO-8601 WITH offset; ``timezone`` is an IANA name; a duplicate ``event_id`` is refused).
* V04 event bar = the first bar of the symbol whose date is >= the local date of ``effective_at`` in ``timezone``. V10 a non-trading event day maps
  to the next bar (``mapped_to_next_bar``); no bar is invented. V09 events reach symbols only through ``symbol_scope``.
* V05/V06 the window is ``event bar - N_pre .. event bar + N_post`` in BARS of the symbol's own rows (defaults 2 / 5); an anchor inside it is a window
  sample. V08 an anchor in several windows is counted once, for the earliest event bar (ties: smaller ``event_id``); ``overlap_dropped`` is recorded.
* V07 A only the validation population of the model (C11 A); fit rows and the out-of-sample tail are never read.
* V11/V12/V13 a set is immutable (a revision is a new ``set_id``): ``<DataRoot>/Events/<set_id>/events.csv`` + ``manifest.json`` (SHA-256, counts);
  an existing set is never overwritten; evaluation re-hashes the CSV and refuses a mismatch; the report echoes both hashes.
* V14 A ``metrics.classification_report_dict`` for the in-window samples and for the complement; V02 A the in-window samples are also tabulated per
  ``event_type``. V15 A a group smaller than ``--min-window-samples`` (30) is ``insufficient_data`` without metric keys. V16 A no usable event ->
  ``no_events``; no in-window sample -> ``no_samples``; nothing is filled with zeros. V17 A no confidence interval. V18 A classification models only.
* V19 A output ``<model>.onnx.events.json`` beside the model, header from ``explain.make_header``.

The result is a descriptive comparison of model behaviour on two groups of validation windows, not a causal statement about events and not advice.
Nothing here changes the model, the registry or any prediction path.

Usage::

    python event_eval.py import --csv fomc_2024.csv --set-id fomc_2024_v1
    python event_eval.py evaluate --model m.onnx --event-set fomc_2024_v1 --symbols AAA,BBB --data-start 2021-01-04 --wf-splits 5
    python event_eval.py --selfcheck
"""

from __future__ import annotations

import argparse
import csv
import datetime
import functools
import gc
import hashlib
import io
import json
import math
import re
import shutil
import sys
import tempfile
import time
import warnings
import zoneinfo
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, List, Sequence, Tuple

import numpy as np
import onnxruntime as ort

sys.path.insert(0, str(Path(__file__).resolve().parent))
import checkpoints  # noqa: E402
import dataset as ds  # noqa: E402
import explain  # noqa: E402  (make_header)
import metrics  # noqa: E402  (classification_report_dict)
import model_analysis  # noqa: E402  (channel_names)
import onnx_meta  # noqa: E402
import verify_model as vm  # noqa: E402  (validation population, validation_folds, PrerequisiteError)

SCHEMA_VERSION: int = 1
TOOL: str = "event_eval.py"
TOOL_VERSION: str = "1"
SIDECAR_SUFFIX: str = ".events.json"
EVENTS_DIRNAME: str = "Events"
CSV_NAME: str = "events.csv"
MANIFEST_NAME: str = "manifest.json"
INCOMING_SUFFIX: str = ".incoming"
CSV_COLUMNS: Tuple[str, ...] = ("event_id", "event_type", "symbol_scope", "timezone", "announced_at", "effective_at", "revision", "source_note")
OPTIONAL_CELLS: Tuple[str, ...] = ("effective_at",)
MAX_SET_ID_CHARS: int = 64                       # planning value: a folder name, long enough for a descriptive id
SET_ID_PATTERN = re.compile(rf"[A-Za-z0-9][A-Za-z0-9._-]{{0,{MAX_SET_ID_CHARS - 1}}}")
CONTROL_CHAR_LIMIT: int = 32                     # code points below this (space) are control characters, refused in every CSV cell
ELAPSED_DIGITS: int = 3                          # decimals of the elapsed seconds recorded in the report
HASH_PREFIX_CHARS: int = 16                     # length of the SHA-256 prefix printed by `import` (the full hash is in the manifest)
INTEGER_PATTERN = re.compile(r"[0-9]{1,9}")
CALENDAR_QUALITY: str = "user_supplied_not_verified_by_tool"
REAL_EVALUATION: str = "unverified"          # R1b A: the tool never reports a real-event evaluation as verified
WINDOW_BASIS: str = "trading_bars_of_each_symbols_own_rows"
SUPPORTED_TIMEFRAME: str = onnx_meta.DEFAULT_TIMEFRAME     # daily: a weekly / monthly bar has no defined relation to an event day (refused, exit 3)
NOTICES: Tuple[str, ...] = (
    "calendar_quality_is_the_users_responsibility", "descriptive_comparison_not_a_causal_claim", "validation_population_only",
    "windows_are_defined_on_each_symbols_own_bars", "not_advice")
STATUS_AVAILABLE, STATUS_NO_EVENTS, STATUS_NO_SAMPLES, STATUS_INSUFFICIENT = "available", "no_events", "no_samples", "insufficient_data"
EVENT_OK, EVENT_MAPPED, EVENT_SKIPPED = "ok", "mapped_to_next_bar", "skipped"
SKIP_NO_SYMBOL, SKIP_BEFORE, SKIP_AFTER, SKIP_OUTSIDE = "no_population_symbol", "before_data_start", "after_data_end", "outside_data_range"
EXIT_OK, EXIT_FAILED, EXIT_PREREQUISITE = 0, 1, 3      # C17

# Planning values without an empirical basis (V06 / V15 star values and the safety bounds): named here, validated at start.
DEFAULT_N_PRE: int = 2
DEFAULT_N_POST: int = 5
MAX_WINDOW_BARS: int = 250
DEFAULT_MIN_WINDOW_SAMPLES: int = 30
MIN_WINDOW_SAMPLES_BAND: Tuple[int, int] = (1, 10_000_000)
MAX_CSV_BYTES: int = 16 * 1024 * 1024
MAX_CELL_CHARS: int = 2000
MAX_REPORTED_PROBLEMS: int = 20
PREDICT_CHUNK: int = 512
MAX_EVENTS_ENV: str = "SA_EV_MAX_EVENTS"
MAX_EVENTS: int = checkpoints.resolve_band(MAX_EVENTS_ENV, 100_000, (1, 10_000_000), (1, 1_000_000))
MAX_SECONDS_ENV: str = "SA_EV_MAX_SECONDS"
MAX_SECONDS: int = checkpoints.resolve_band(MAX_SECONDS_ENV, 3600, (1, 86_400), (60, 7_200))

PrerequisiteError = vm.PrerequisiteError


class EventSetError(PrerequisiteError):
    """The event CSV or a stored event set is unusable (exit code 3). Nothing is written."""


# --- The event CSV (V03) -----------------------------------------------------------------------------------------------------------


@dataclass(frozen=True)
class Event:
    event_id: str
    event_type: str
    all_symbols: bool
    scope: Tuple[str, ...]
    timezone: str
    announced_at: datetime.datetime
    effective_at: datetime.datetime
    revision: int
    source_note: str
    local_date: datetime.date          # the date of ``effective_at`` in ``timezone`` (V04)


@functools.lru_cache(maxsize=None)
def _zone(name: str) -> zoneinfo.ZoneInfo:
    return zoneinfo.ZoneInfo(name)


def _aware(text: str, column: str) -> datetime.datetime:
    try:
        value = datetime.datetime.fromisoformat(text)
    except ValueError as exc:
        raise ValueError(f"{column} {text!r} is not an ISO-8601 timestamp") from exc
    if value.tzinfo is None or value.utcoffset() is None:
        raise ValueError(f"{column} {text!r} has no UTC offset (a date or a local time alone is refused)")
    return value


def _parse_scope(cell: str) -> Tuple[bool, Tuple[str, ...]]:
    text = cell.strip()
    if text == "*":
        return True, ()
    tokens = [t.strip() for t in text.split(",")]
    if any(not t for t in tokens):
        raise ValueError("symbol_scope has an empty symbol")
    if "*" in tokens:
        raise ValueError("symbol_scope '*' cannot be combined with symbols")
    if len({t.lower() for t in tokens}) != len(tokens):
        raise ValueError("symbol_scope lists a symbol twice")
    return False, tuple(tokens)


def _parse_row(cells: Dict[str, str]) -> Event:
    for name, cell in cells.items():
        if len(cell) > MAX_CELL_CHARS:
            raise ValueError(f"{name} is longer than {MAX_CELL_CHARS} characters")
        if any(ord(ch) < CONTROL_CHAR_LIMIT for ch in cell):
            raise ValueError(f"{name} contains a control character")
        if name != "symbol_scope" and cell != cell.strip():
            raise ValueError(f"{name} has leading or trailing whitespace")
        if not cell and name not in OPTIONAL_CELLS:
            raise ValueError(f"{name} is empty")
    all_symbols, scope = _parse_scope(cells["symbol_scope"])
    try:
        zone = _zone(cells["timezone"])
    except (zoneinfo.ZoneInfoNotFoundError, ValueError, OSError) as exc:
        raise ValueError(f"timezone {cells['timezone']!r} is not an IANA name known to this machine "
                         "(on Windows the tzdata package provides the database)") from exc
    announced = _aware(cells["announced_at"], "announced_at")
    effective = _aware(cells["effective_at"], "effective_at") if cells["effective_at"] else announced
    if INTEGER_PATTERN.fullmatch(cells["revision"]) is None or int(cells["revision"]) < 1:
        raise ValueError(f"revision {cells['revision']!r} must be an integer >= 1")
    return Event(event_id=cells["event_id"], event_type=cells["event_type"], all_symbols=all_symbols, scope=scope, timezone=cells["timezone"],
                 announced_at=announced, effective_at=effective, revision=int(cells["revision"]), source_note=cells["source_note"],
                 local_date=effective.astimezone(zone).date())


def parse_events_csv(raw: bytes) -> List[Event]:
    """Every event of the CSV *raw*, or an ``EventSetError`` that lists the first problems (nothing is repaired or guessed)."""
    if len(raw) > MAX_CSV_BYTES:
        raise EventSetError(f"the CSV is {len(raw)} bytes; the bound is {MAX_CSV_BYTES}")
    try:
        text = raw.decode("utf-8-sig")
    except UnicodeDecodeError as exc:
        raise EventSetError(f"the CSV is not UTF-8 text ({exc})") from exc
    reader = csv.reader(io.StringIO(text, newline=""))
    try:
        header = next(reader)
    except StopIteration:
        raise EventSetError("the CSV is empty (no header line)") from None
    except csv.Error as exc:
        raise EventSetError(f"the CSV header cannot be read ({exc})") from exc
    if len(set(header)) != len(header):
        raise EventSetError(f"the CSV header repeats a column: {header}")
    missing, extra = [c for c in CSV_COLUMNS if c not in header], [c for c in header if c not in CSV_COLUMNS]
    if missing or extra:
        raise EventSetError(f"the CSV header must be exactly {list(CSV_COLUMNS)}; missing {missing}, unexpected {extra}")
    events: List[Event] = []
    problems: List[str] = []
    seen: Dict[str, int] = {}
    try:
        for row in reader:
            if not row:
                continue
            line = reader.line_num
            if len(row) != len(header):
                problems.append(f"line {line}: {len(row)} cells, the header has {len(header)}")
                continue
            try:
                event = _parse_row(dict(zip(header, row)))
            except ValueError as exc:
                problems.append(f"line {line}: {exc}")
                continue
            if event.event_id in seen:
                problems.append(f"line {line}: event_id {event.event_id!r} already used on line {seen[event.event_id]}")
                continue
            seen[event.event_id] = line
            events.append(event)
            if len(events) > MAX_EVENTS:
                raise EventSetError(f"more than {MAX_EVENTS} events (env {MAX_EVENTS_ENV})")
    except csv.Error as exc:
        raise EventSetError(f"the CSV cannot be read ({exc})") from exc
    if problems:
        shown = problems[:MAX_REPORTED_PROBLEMS]
        more = f" (+{len(problems) - len(shown)} more)" if len(problems) > len(shown) else ""
        raise EventSetError("the CSV is refused: " + "; ".join(shown) + more)
    return events


# --- The event set store (V11 / V12 / V13) -------------------------------------------------------------------------------------------


def default_events_root() -> Path:
    return ds.DATA_ROOT / EVENTS_DIRNAME


def check_set_id(set_id: str) -> str:
    if not isinstance(set_id, str) or SET_ID_PATTERN.fullmatch(set_id) is None or set_id.endswith(INCOMING_SUFFIX):
        raise ValueError(f"--set-id must match {SET_ID_PATTERN.pattern} and must not end with {INCOMING_SUFFIX}")
    return set_id


def import_event_set(csv_path: Path, set_id: str, root: Path) -> dict:
    """Validate *csv_path* and store it as the immutable set *set_id* under *root*; returns the manifest."""
    check_set_id(set_id)
    csv_path = Path(csv_path)
    if not csv_path.is_file():
        raise EventSetError(f"event CSV not found: {csv_path}")
    if csv_path.stat().st_size > MAX_CSV_BYTES:
        raise EventSetError(f"the CSV is larger than {MAX_CSV_BYTES} bytes")
    raw = csv_path.read_bytes()
    events = parse_events_csv(raw)
    final, work = Path(root) / set_id, Path(root) / (set_id + INCOMING_SUFFIX)
    if final.exists() or work.exists():
        raise EventSetError(f"event set {set_id!r} already exists under {root}; a revision is a new --set-id (V11)")
    types: Dict[str, int] = {}
    for event in events:
        types[event.event_type] = types.get(event.event_type, 0) + 1
    manifest = dict(
        schema_version=SCHEMA_VERSION, tool=TOOL, tool_version=TOOL_VERSION, set_id=set_id, csv_name=CSV_NAME,
        csv_sha256=hashlib.sha256(raw).hexdigest(), csv_bytes=len(raw), columns=list(CSV_COLUMNS), events=len(events),
        revision_min=min((e.revision for e in events), default=None), revision_max=max((e.revision for e in events), default=None),
        event_types=types, timezones=sorted({e.timezone for e in events}), wildcard_events=sum(e.all_symbols for e in events),
        imported_utc=datetime.datetime.now(datetime.timezone.utc).isoformat(), calendar_quality=CALENDAR_QUALITY)
    Path(root).mkdir(parents=True, exist_ok=True)
    work.mkdir()
    try:
        (work / CSV_NAME).write_bytes(raw)
        checkpoints.atomic_json(work / MANIFEST_NAME, manifest)
        work.rename(final)                                        # never replaces an existing folder
    except BaseException:
        shutil.rmtree(work, ignore_errors=True)
        raise
    return manifest


def load_event_set(root: Path, set_id: str) -> Tuple[List[Event], dict, str]:
    """(events, manifest, manifest SHA-256) of a stored set; any difference from what was imported is refused (V12)."""
    check_set_id(set_id)
    folder = Path(root) / set_id
    try:
        manifest_bytes = (folder / MANIFEST_NAME).read_bytes()
        manifest = json.loads(manifest_bytes.decode("utf-8"))
        raw = (folder / CSV_NAME).read_bytes()
    except (OSError, ValueError) as exc:
        raise EventSetError(f"event set {set_id!r} cannot be read under {root} ({exc})") from exc
    if not isinstance(manifest, dict) or manifest.get("schema_version") != SCHEMA_VERSION or manifest.get("set_id") != set_id:
        raise EventSetError(f"the manifest of event set {set_id!r} is not a schema {SCHEMA_VERSION} manifest of that set")
    if hashlib.sha256(raw).hexdigest() != manifest.get("csv_sha256"):
        raise EventSetError(f"event set {set_id!r}: events.csv no longer matches the SHA-256 recorded at import; the set was changed "
                            "(hindsight is refused, import the changed file as a new --set-id)")
    events = parse_events_csv(raw)
    if len(events) != manifest.get("events"):
        raise EventSetError(f"event set {set_id!r}: the manifest records {manifest.get('events')} events, the CSV holds {len(events)}")
    return events, manifest, hashlib.sha256(manifest_bytes).hexdigest()


# --- Event bars, windows and attribution (model independent) -------------------------------------------------------------------------


def event_bar(day: np.ndarray, local_date: datetime.date) -> Tuple[int | None, str]:
    """(row index, status) of the first bar of *day* (ascending datetime64[D]) with date >= *local_date* (V04 / V10)."""
    target = np.datetime64(local_date, "D")
    if day.size == 0 or target < day[0]:
        return None, SKIP_BEFORE
    index = int(np.searchsorted(day, target, side="left"))
    if index >= day.size:
        return None, SKIP_AFTER
    return index, (EVENT_OK if day[index] == target else EVENT_MAPPED)


def map_events(events: Sequence[Event], symbol_days: Dict[str, np.ndarray], n_pre: int, n_post: int) -> List[dict]:
    """Per event the per-symbol event bar and window rows (``window_first_row`` .. ``window_last_row``, inclusive), on each symbol's own rows."""
    lower = {name.lower(): name for name in symbol_days}
    out = []
    for event in events:
        names = list(symbol_days) if event.all_symbols else [lower[t.lower()] for t in event.scope if t.lower() in lower]
        absent = [] if event.all_symbols else [t for t in event.scope if t.lower() not in lower]
        rows = []
        for name in names:
            day = symbol_days[name]
            index, status = event_bar(day, event.local_date)
            row: dict = dict(symbol=name, status=status)
            if index is not None:
                row.update(event_bar=index, event_bar_date=str(day[index]), window_first_row=max(0, index - n_pre),
                           window_last_row=min(day.size - 1, index + n_post), window_clipped_start=index - n_pre < 0,
                           window_clipped_end=index + n_post > day.size - 1)
            rows.append(row)
        usable = [r for r in rows if "event_bar" in r]
        entry = dict(event=event, symbols=rows, scope_symbols_not_in_population=absent)
        if usable:
            entry["status"] = EVENT_MAPPED if any(r["status"] == EVENT_MAPPED for r in usable) else EVENT_OK
        else:
            reasons = {r["status"] for r in rows}
            entry["status"] = EVENT_SKIPPED
            entry["skip_reason"] = SKIP_NO_SYMBOL if not rows else (reasons.pop() if len(reasons) == 1 else SKIP_OUTSIDE)
        out.append(entry)
    return out


def attribute(mapped: Sequence[dict], anchors: Dict[str, np.ndarray]) -> Tuple[Dict[str, np.ndarray], Dict[Tuple[int, str], Tuple[int, int]]]:
    """Per symbol the index of the event each anchor row is attributed to (-1 = outside every window), and ``(event, symbol) -> (in window, attributed)``.

    V08 A: an anchor inside several windows belongs to the earliest event bar of that symbol (ties: the smaller ``event_id``)."""
    entries: Dict[str, list] = {name: [] for name in anchors}
    for e, item in enumerate(mapped):
        for row in item["symbols"]:
            if "event_bar" in row:
                entries[row["symbol"]].append((row["event_bar"], item["event"].event_id, e, row["window_first_row"], row["window_last_row"]))
    owner: Dict[str, np.ndarray] = {}
    counts: Dict[Tuple[int, str], Tuple[int, int]] = {}
    for name, rows in anchors.items():
        who = np.full(rows.shape[0], -1, dtype=np.int64)
        for _bar, _id, e, first, last in sorted(entries[name]):
            inside = (rows >= first) & (rows <= last)
            claim = inside & (who < 0)
            who[claim] = e
            counts[(e, name)] = (int(inside.sum()), int(claim.sum()))
        owner[name] = who
    return owner, counts


# --- The evaluation (model independent) -----------------------------------------------------------------------------------------------


def _clean(value):
    """JSON-safe copy: numpy scalars / arrays become Python values and a non-finite float becomes null (a metric that is undefined)."""
    if isinstance(value, dict):
        return {str(k): _clean(v) for k, v in value.items()}
    if isinstance(value, (list, tuple)):
        return [_clean(v) for v in value]
    if isinstance(value, np.ndarray):
        return _clean(value.tolist())
    if isinstance(value, np.generic):
        return _clean(value.item())
    if isinstance(value, float) and not math.isfinite(value):
        return None
    return value


def metric_group(y: np.ndarray, pred: np.ndarray, probs: np.ndarray, mask: np.ndarray, minimum: int) -> dict:
    """V15 / V16: ``no_samples`` for an empty group, ``insufficient_data`` (no metric keys) below *minimum*, otherwise the metric set."""
    n = int(mask.sum())
    if n == 0:
        return dict(status=STATUS_NO_SAMPLES, n_samples=0)
    if n < minimum:
        return dict(status=STATUS_INSUFFICIENT, n_samples=n, minimum=minimum)
    with warnings.catch_warnings():
        # float32 softmax rows sum to 1 +- 1e-7; scikit-learn's float64 sum-to-one check warns about that rounding. The numbers stay exactly
        # those the trainers report (same function, same inputs); only this one message is silenced.
        warnings.filterwarnings("ignore", message="The y_prob values do not sum to one")
        report = metrics.classification_report_dict(y[mask], pred[mask], probs[mask], num_classes=probs.shape[1], class_labels=list(ds.CLASS_LABELS))
    return _clean(dict(report, status="ok"))


def evaluate_population(events: Sequence[Event], *, symbol_days: Dict[str, np.ndarray], anchors: Dict[str, np.ndarray], y: np.ndarray,
                        probs: np.ndarray, n_pre: int, n_post: int, min_samples: int) -> dict:
    """Window / outside / per-type tables for the pooled validation samples (ordered symbol by symbol as *anchors*)."""
    total = sum(rows.shape[0] for rows in anchors.values())
    if y.shape[0] != total or probs.shape[0] != total:
        raise ValueError("labels / probabilities do not match the anchors")
    mapped = map_events(events, symbol_days, n_pre, n_post)
    owner, counts = attribute(mapped, anchors)
    who = np.concatenate([owner[name] for name in anchors]) if anchors else np.zeros(0, dtype=np.int64)
    pred = np.argmax(probs, axis=1)
    in_window = who >= 0
    event_rows, skipped = [], {}
    for e, item in enumerate(mapped):
        event = item["event"]
        details = []
        for row in item["symbols"]:
            inside, claimed = counts.get((e, row["symbol"]), (0, 0))
            details.append(dict(row, anchors_in_window=inside, anchors_attributed=claimed, overlap_dropped=inside - claimed))
        in_n = sum(d["anchors_in_window"] for d in details)
        attributed = sum(d["anchors_attributed"] for d in details)
        record = dict(event_id=event.event_id, event_type=event.event_type, revision=event.revision, timezone=event.timezone,
                      effective_local_date=event.local_date.isoformat(), scope="*" if event.all_symbols else list(event.scope),
                      scope_symbols_not_in_population=item["scope_symbols_not_in_population"], status=item["status"],
                      anchors_in_window=in_n, anchors_attributed=attributed, overlap_dropped=in_n - attributed, symbols=details)
        if item["status"] == EVENT_SKIPPED:
            record["skip_reason"] = item["skip_reason"]
            skipped[item["skip_reason"]] = skipped.get(item["skip_reason"], 0) + 1
        event_rows.append(record)
    usable = [r for r in event_rows if r["status"] != EVENT_SKIPPED]
    types = sorted({r["event_type"] for r in usable})
    type_of = np.array([m["event"].event_type for m in mapped], dtype=object)
    by_type = {}
    for t in types:
        mask = in_window & np.array([bool(type_of[w] == t) if w >= 0 else False for w in who], dtype=bool)
        by_type[t] = dict(events=sum(r["event_type"] == t for r in usable), **metric_group(y, pred, probs, mask, min_samples))
    window = metric_group(y, pred, probs, in_window, min_samples)
    if not usable:
        status = STATUS_NO_EVENTS
    elif not in_window.any():
        status = STATUS_NO_SAMPLES
    elif window["status"] == STATUS_INSUFFICIENT:
        status = STATUS_INSUFFICIENT
    else:
        status = STATUS_AVAILABLE
    memberships = sum(r["anchors_in_window"] for r in event_rows)
    return dict(
        status=status,
        counts=dict(events_total=len(event_rows), events_usable=len(usable), events_skipped=skipped,
                    events_mapped_to_next_bar=sum(r["status"] == EVENT_MAPPED for r in event_rows),
                    anchors_total=int(total), anchors_in_window=int(in_window.sum()), anchors_outside_window=int((~in_window).sum()),
                    window_memberships=memberships, overlap_dropped_total=memberships - int(in_window.sum())),
        events=event_rows,
        metrics=dict(in_window=window, outside_window=metric_group(y, pred, probs, ~in_window, min_samples), by_event_type=by_type))


# --- Run ----------------------------------------------------------------------------------------------------------------------------


def validate_args(args: argparse.Namespace) -> None:
    check_set_id(args.set_id if args.command == "import" else args.event_set)
    if args.command == "import":
        return
    for name in ("n_pre", "n_post"):
        value = getattr(args, name)
        if type(value) is not int or not 0 <= value <= MAX_WINDOW_BARS:
            raise ValueError(f"--{name.replace('_', '-')} must be an integer in 0..{MAX_WINDOW_BARS}")
    for name, band in (("min_window_samples", MIN_WINDOW_SAMPLES_BAND), ("max_seconds", (1, MAX_SECONDS))):
        value = getattr(args, name)
        if type(value) is not int or not band[0] <= value <= band[1]:
            raise ValueError(f"--{name.replace('_', '-')} must be an integer in {band[0]}..{band[1]}")
    vm.validate_population_arguments(args)


def run(args: argparse.Namespace) -> int:
    started = time.monotonic()
    try:
        validate_args(args)
        return _run_import(args) if args.command == "import" else _run_evaluate(args, started)
    except PrerequisiteError as exc:
        print(f"event_eval: PREREQUISITE NOT MET: {exc}", file=sys.stderr)
        return EXIT_PREREQUISITE
    except (TimeoutError, ArithmeticError) as exc:
        print(f"event_eval: FAILED: {exc}", file=sys.stderr)
        return EXIT_FAILED


def _run_import(args: argparse.Namespace) -> int:
    root = Path(args.events_root) if args.events_root else default_events_root()
    manifest = import_event_set(args.csv, args.set_id, root)
    print(f"event_eval: IMPORTED {manifest['set_id']}  {manifest['events']} event(s)  sha256 {manifest['csv_sha256'][:HASH_PREFIX_CHARS]}...  -> {root / args.set_id}")
    print("  NOTE: the tool cannot verify that the dates are correct; the source and its terms of use are the user's responsibility.")
    return EXIT_OK


def predict(session, x: np.ndarray, deadline: float) -> np.ndarray:
    parts = []
    for start in range(0, x.shape[0], PREDICT_CHUNK):
        if time.monotonic() > deadline:
            raise TimeoutError("the event evaluation exceeded --max-seconds")
        (probs,) = session.run(["output"], {"input": np.asarray(x[start:start + PREDICT_CHUNK], dtype=np.float32)})
        parts.append(np.asarray(probs, dtype=np.float32))                  # the model's own dtype: float64 sums of float32 softmax rows trip scikit-learn's sum-to-one check
    probs = np.concatenate(parts) if parts else np.zeros((0, len(ds.CLASS_LABELS)), dtype=np.float32)
    if not np.isfinite(probs).all():
        raise ArithmeticError("the model output for the validation population is not finite")
    return probs


def validation_index(population: dict) -> Tuple[Dict[str, np.ndarray], Dict[str, np.ndarray]]:
    """(anchor rows, all dates) per symbol in the order of the pooled validation population, checked against ``population["x"]``."""
    context = population["context"]
    folds = vm.validation_folds(population)          # also checks that the per-window rebuild equals the pooled population
    anchors = {name: np.asarray(fold_anchors[fold_val], dtype=np.int64) for name, (_x, _y, fold_anchors, _train, fold_val) in folds.items()}
    days = {name: np.asarray(context["dates"][name]).astype("datetime64[D]").reshape(-1) for name in folds}
    return anchors, days


def _refuse_existing_output(out_path: Path, overwrite: bool) -> None:
    if out_path.exists() and not overwrite:
        raise PrerequisiteError(f"{out_path} already exists; pass --overwrite to replace it or --out for another file")


def _run_evaluate(args: argparse.Namespace, started: float) -> int:
    deadline = started + args.max_seconds
    model_path = Path(args.model)
    out_path = Path(args.out) if args.out else model_path.with_name(model_path.name + SIDECAR_SUFFIX)
    _refuse_existing_output(out_path, bool(args.overwrite))
    if not model_path.is_file():
        raise PrerequisiteError(f"model not found: {model_path}")
    root = Path(args.events_root) if args.events_root else default_events_root()
    events, manifest, manifest_sha = load_event_set(root, args.event_set)
    sha_before = checkpoints.hash_file(model_path)
    session = ort.InferenceSession(str(model_path), providers=["CPUExecutionProvider"])
    metadata = dict(session.get_modelmeta().custom_metadata_map)
    if metadata.get("target_type", onnx_meta.TARGET_TYPE) != "classification":
        raise PrerequisiteError("regression_not_adopted: the event evaluation covers classification models only (V18)")
    timeframe = metadata.get(onnx_meta.OUTPUT_TIMEFRAME_KEY, onnx_meta.DEFAULT_TIMEFRAME)
    if timeframe != SUPPORTED_TIMEFRAME:
        raise PrerequisiteError(f"timeframe_not_supported: the event bar rule (V04) maps an event to the first bar on or after its local date, "
                                f"which is only defined for {SUPPORTED_TIMEFRAME!r} bars; the model timeframe is {timeframe!r}")
    classes = list(ds.CLASS_LABELS)
    if metadata.get("class_order", "").split(",") != classes:
        raise PrerequisiteError("the model class order disagrees with the dataset class labels")
    try:
        base_channels, expanded = model_analysis.channel_names(metadata)
        int(metadata["window_size"])
    except (KeyError, ValueError) as exc:
        raise PrerequisiteError(f"model metadata cannot define the channels: {exc}") from exc
    population = vm.reconstruct_validation_population(metadata, model_path, args)
    anchors, days = validation_index(population)
    probs = predict(session, population["x"], deadline)
    body = evaluate_population(events, symbol_days=days, anchors=anchors, y=np.asarray(population["y"]).astype(np.int64).ravel(), probs=probs,
                               n_pre=args.n_pre, n_post=args.n_post, min_samples=args.min_window_samples)
    del session
    gc.collect()
    if checkpoints.hash_file(model_path) != sha_before:
        raise ArithmeticError("the model file changed while it was being evaluated")
    config = dict(n_pre=args.n_pre, n_post=args.n_post, min_window_samples=args.min_window_samples, wf_splits=args.wf_splits,
                  outer_fold_index=args.outer_fold_index, calibration_segment=bool(args.calibration_segment))
    header = explain.make_header(
        model_sha256=sha_before, data_revision=population["data_revision"], metadata=metadata,
        timeframe=timeframe, window_size=int(metadata["window_size"]),
        base_channels=base_channels, ordered_channels=expanded)
    document = dict(
        header, tool=TOOL, tool_version=TOOL_VERSION, created_utc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
        elapsed_seconds=round(time.monotonic() - started, ELAPSED_DIGITS), status=body["status"],
        config=config, config_sha256=hashlib.sha256(checkpoints.canonical(config).encode()).hexdigest(),
        event_set=dict(set_id=args.event_set, csv_sha256=manifest["csv_sha256"], manifest_sha256=manifest_sha, events=manifest["events"],
                       revision_min=manifest["revision_min"], revision_max=manifest["revision_max"], imported_utc=manifest["imported_utc"],
                       calendar_quality=manifest["calendar_quality"]),
        window=dict(basis=WINDOW_BASIS, n_pre=args.n_pre, n_post=args.n_post, event_bar_rule="first_bar_on_or_after_local_effective_date",
                    overlap_rule="earliest_event_bar_then_smaller_event_id", population_rule="validation_anchors_only"),
        population=dict(source="validation", oos_consumed=population["oos_consumed"], validation_total=int(population["x"].shape[0]),
                        validation_span=population["validation_span"], symbols=population["symbols"]),
        counts=body["counts"], events=body["events"], metrics=body["metrics"],
        verification=dict(real_event_evaluation=REAL_EVALUATION, calendar_quality=CALENDAR_QUALITY),
        notices=list(NOTICES))
    document = json.loads(checkpoints.canonical(_clean(document)))
    _refuse_existing_output(out_path, bool(args.overwrite))      # again right before the replacement: narrows the window for a writer that appeared meanwhile (no lock)
    checkpoints.atomic_json(out_path, document)
    counts = body["counts"]
    print(f"event_eval: {body['status'].upper()}  set {args.event_set}  events {counts['events_usable']}/{counts['events_total']} usable  "
          f"anchors in window {counts['anchors_in_window']}/{counts['anchors_total']}  overlap dropped {counts['overlap_dropped_total']}  -> {out_path}")
    for label, key in (("in window ", "in_window"), ("outside   ", "outside_window")):
        group = body["metrics"][key]
        print(f"  {label}: n={group['n_samples']}  " + (f"accuracy {group['accuracy']:.4f}  macro_f1 {group['macro_f1']:.4f}" if group["status"] == "ok"
                                                         else group["status"]))
    print("  NOTE: a descriptive comparison, not a causal statement; real-event evaluation is unverified and the calendar quality is the user's.")
    return EXIT_OK


# --- Self-check ----------------------------------------------------------------------------------------------------------------------


def _weekday_dates(rows: int) -> List[datetime.date]:
    out, day = [], datetime.date(2020, 1, 1)
    while len(out) < rows:
        if day.weekday() < 5:
            out.append(day)
        day += datetime.timedelta(days=1)
    return out


def _csv_text(rows: Sequence[Dict[str, str]], header: Sequence[str] = CSV_COLUMNS) -> bytes:
    stream = io.StringIO(newline="")
    writer = csv.writer(stream, lineterminator="\r\n")
    writer.writerow(header)
    for row in rows:
        writer.writerow([row.get(c, "") for c in header])
    return stream.getvalue().encode("utf-8")


def _event_row(event_id: str, effective: str, *, scope: str = "*", tz: str = "UTC", event_type: str = "T1", revision: str = "1",
               announced: str | None = None, note: str = "synthetic selfcheck event") -> Dict[str, str]:
    return dict(event_id=event_id, event_type=event_type, symbol_scope=scope, timezone=tz, announced_at=announced or effective,
                effective_at=effective, revision=revision, source_note=note)


def _expect_refused(raw: bytes, needle: str) -> None:
    try:
        parse_events_csv(raw)
    except EventSetError as exc:
        assert needle in str(exc), (needle, str(exc))
        return
    raise AssertionError(f"the CSV should be refused ({needle})")


def _run_selfcheck() -> None:
    import contextlib
    import onnx
    from unittest import mock

    # 1. CSV validation (V03): every rejection names its line; accepted shapes
    base = _event_row("A1", "2024-03-20T14:00:00-04:00", tz="America/New_York")
    assert len(parse_events_csv(_csv_text([base]))) == 1
    assert len(parse_events_csv(b"\xef\xbb\xbf" + _csv_text([base]))) == 1                                     # a BOM is tolerated
    z = parse_events_csv(_csv_text([dict(base, event_id="A2", announced_at="2024-03-20T18:00:00Z", effective_at="")]))[0]
    assert z.effective_at == z.announced_at and z.local_date == datetime.date(2024, 3, 20)                     # empty effective_at -> announced_at
    spaced = parse_events_csv(_csv_text([dict(base, event_id="A3", symbol_scope="AAA, BBB")]))[0]
    assert spaced.scope == ("AAA", "BBB") and not spaced.all_symbols
    shifted = parse_events_csv(_csv_text([_event_row("B1", "2024-03-20T02:00:00+00:00", tz="America/New_York"),
                                          _event_row("B2", "2024-03-20T20:00:00+00:00", tz="Asia/Tokyo")]))
    assert shifted[0].local_date == datetime.date(2024, 3, 19) and shifted[1].local_date == datetime.date(2024, 3, 21)     # the declared timezone decides the day
    header_swapped = tuple(reversed(CSV_COLUMNS))
    assert len(parse_events_csv(_csv_text([base], header_swapped))) == 1                                       # column order is free
    _expect_refused(b"", "empty")
    _expect_refused(_csv_text([base], CSV_COLUMNS[:-1]), "missing ['source_note']")
    _expect_refused(_csv_text([base], CSV_COLUMNS + ("extra",)), "unexpected ['extra']")
    _expect_refused(_csv_text([base], CSV_COLUMNS + ("event_id",)), "repeats a column")
    _expect_refused(_csv_text([base, dict(base)]), "already used on line 2")
    _expect_refused(_csv_text([dict(base, timezone="Mars/Base")]), "IANA")
    _expect_refused(_csv_text([dict(base, announced_at="2024-03-20T14:00:00", effective_at="")]), "no UTC offset")
    _expect_refused(_csv_text([dict(base, announced_at="2024-03-20", effective_at="")]), "no UTC offset")
    _expect_refused(_csv_text([dict(base, announced_at="yesterday", effective_at="")]), "ISO-8601")
    for bad_revision in ("0", "1.5", "x", "-1", "", "1234567890"):
        _expect_refused(_csv_text([dict(base, revision=bad_revision)]), "revision" if bad_revision else "revision is empty")
    _expect_refused(_csv_text([dict(base, event_type="")]), "event_type is empty")
    _expect_refused(_csv_text([dict(base, source_note="")]), "source_note is empty")
    _expect_refused(_csv_text([dict(base, symbol_scope="*,AAA")]), "cannot be combined")
    _expect_refused(_csv_text([dict(base, symbol_scope="AAA,,BBB")]), "empty symbol")
    _expect_refused(_csv_text([dict(base, symbol_scope="AAA,aaa")]), "twice")
    _expect_refused(_csv_text([dict(base, event_id=" A1")]), "whitespace")
    _expect_refused(_csv_text([dict(base, source_note="bad\x01note")]), "control character")
    _expect_refused(_csv_text([base]).replace(b"America/New_York", b"America/New_York,extra"), "cells")
    _expect_refused(b"\xff\xfe\x00bad", "UTF-8")
    with mock.patch.object(sys.modules[__name__], "MAX_EVENTS", 1):
        _expect_refused(_csv_text([base, dict(base, event_id="A9")]), "more than 1 events")
    _expect_refused(_csv_text([dict(base, event_id="A1", revision="0"), dict(base, event_id="A2", effective_at="bad")]), "line 2")
    print("  group 1 CSV validation OK")

    # 2. the store (V11-V13): immutable, hashed, refuses a changed set
    with tempfile.TemporaryDirectory(prefix="sa_event_eval_selfcheck_") as tmp_name:
        tmp = Path(tmp_name)
        raw = b"\xef\xbb\xbf" + _csv_text([base, _event_row("A2", "2024-04-01T00:00:00+00:00", revision="3", scope="AAA")])
        source = tmp / "events_source.csv"
        source.write_bytes(raw)
        root = tmp / "Events"
        manifest = import_event_set(source, "set_v1", root)
        assert (root / "set_v1" / CSV_NAME).read_bytes() == raw and manifest["csv_sha256"] == hashlib.sha256(raw).hexdigest()   # the original bytes are stored
        assert manifest["events"] == 2 and manifest["revision_min"] == 1 and manifest["revision_max"] == 3 and manifest["calendar_quality"] == CALENDAR_QUALITY
        assert not list(root.glob(f"*{INCOMING_SUFFIX}"))
        loaded, loaded_manifest, manifest_sha = load_event_set(root, "set_v1")
        assert [e.event_id for e in loaded] == ["A1", "A2"] and loaded_manifest == manifest and len(manifest_sha) == 64
        try:
            import_event_set(source, "set_v1", root)
            raise AssertionError("an existing set must not be replaced")
        except EventSetError as exc:
            assert "already exists" in str(exc)
        assert (root / "set_v1" / CSV_NAME).read_bytes() == raw
        for bad_id in ("", "../x", "a b", "x" * 65, ".hidden", "set_v1.incoming"):
            try:
                check_set_id(bad_id)
                raise AssertionError(f"set id {bad_id!r} should be refused")
            except ValueError:
                pass
        bad_source = tmp / "bad.csv"
        bad_source.write_bytes(_csv_text([dict(base, revision="0")]))
        try:
            import_event_set(bad_source, "set_bad", root)
            raise AssertionError("an invalid CSV must not be imported")
        except EventSetError:
            pass
        assert not (root / "set_bad").exists() and not list(root.glob(f"*{INCOMING_SUFFIX}")), "a refused import leaves nothing behind"
        try:
            import_event_set(tmp / "no_such.csv", "set_none", root)
            raise AssertionError("a missing file is a prerequisite")
        except EventSetError:
            pass
        stored = root / "set_v1" / CSV_NAME
        stored.write_bytes(raw + b"A9,T1,*,UTC,2024-05-01T00:00:00+00:00,,1,added after import\r\n")                      # an event appended after import
        try:
            load_event_set(root, "set_v1")
            raise AssertionError("a changed CSV must be refused")
        except EventSetError as exc:
            assert "no longer matches" in str(exc)
        stored.write_bytes(raw)
        assert load_event_set(root, "set_v1")[1] == manifest                                                  # restored: accepted again
        edited = dict(manifest, events=5)
        (root / "set_v1" / MANIFEST_NAME).write_text(checkpoints.canonical(edited), encoding="utf-8")
        try:
            load_event_set(root, "set_v1")
            raise AssertionError("an edited manifest must be refused")
        except EventSetError as exc:
            assert "records 5 events" in str(exc)
        try:
            load_event_set(root, "set_missing")
            raise AssertionError("a missing set must be refused")
        except EventSetError:
            pass
        print("  group 2 immutable store OK")

        # 3. event bars on a weekday calendar (V04 / V10)
        weekdays = _weekday_dates(60)
        day = np.array(weekdays, dtype="datetime64[D]")
        wed = next(d for d in weekdays if d.weekday() == 2 and d > weekdays[10])
        sat, sun = wed + datetime.timedelta(days=3), wed + datetime.timedelta(days=4)
        bar, status = event_bar(day, wed)
        assert status == EVENT_OK and day[bar] == np.datetime64(wed)
        for weekend in (sat, sun):
            bar, status = event_bar(day, weekend)
            assert status == EVENT_MAPPED and day[bar] == np.datetime64(wed + datetime.timedelta(days=5))     # the next Monday; no Saturday bar exists
        assert event_bar(day, weekdays[0] - datetime.timedelta(days=1)) == (None, SKIP_BEFORE)
        assert event_bar(day, weekdays[0]) == (0, EVENT_OK)
        assert event_bar(day, weekdays[-1]) == (59, EVENT_OK) and event_bar(day, weekdays[-1] + datetime.timedelta(days=1)) == (None, SKIP_AFTER)
        assert event_bar(day[:0], wed) == (None, SKIP_BEFORE)
        print("  group 3 event bars OK")

        # 4. windows, clipping, overlap attribution (V05 / V06 / V08) on explicit rows
        def ev(event_id: str, local: datetime.date, scope: str = "*") -> Event:
            all_symbols, tokens = _parse_scope(scope)
            stamp = datetime.datetime(local.year, local.month, local.day, 12, tzinfo=datetime.timezone.utc)
            return Event(event_id, "T", all_symbols, tokens, "UTC", stamp, stamp, 1, "n", local)

        days = {"AAA": day, "BBB": day[:30]}
        e_first, e_second, e_late, e_other = (ev("e1", weekdays[10]), ev("e2", weekdays[12]), ev("e3", weekdays[29], "AAA,bbb"), ev("e4", weekdays[40], "BBB"))
        mapped = map_events([e_first, e_second, e_late, e_other], days, 2, 5)
        assert mapped[0]["symbols"][0]["window_first_row"] == 8 and mapped[0]["symbols"][0]["window_last_row"] == 15
        assert mapped[2]["symbols"][1]["symbol"] == "BBB" and mapped[2]["symbols"][1]["window_last_row"] == 29 and mapped[2]["symbols"][1]["window_clipped_end"] is True
        assert mapped[3]["status"] == EVENT_SKIPPED and mapped[3]["skip_reason"] == SKIP_AFTER                  # BBB ends before that date
        assert map_events([ev("x", weekdays[3], "ZZZ")], days, 2, 5)[0]["skip_reason"] == SKIP_NO_SYMBOL
        assert map_events([ev("x", weekdays[3], "ZZZ")], days, 2, 5)[0]["scope_symbols_not_in_population"] == ["ZZZ"]
        early = map_events([ev("x", weekdays[0])], days, 2, 5)[0]["symbols"][0]
        assert early["window_first_row"] == 0 and early["window_clipped_start"] is True
        rows = {"AAA": np.arange(0, 40, dtype=np.int64), "BBB": np.arange(0, 30, dtype=np.int64)}
        owner, counts = attribute(mapped, rows)
        assert (owner["AAA"][8:12] == 0).all()                       # rows 8..11 sit in e1's window and (10..) in e2's: e1 (the earlier bar) keeps them
        assert (owner["AAA"][12:15] == 0).all() and (owner["AAA"][15] == 0) and (owner["AAA"][16:18] == 1).all() and owner["AAA"][18] == -1
        assert counts[(0, "AAA")] == (8, 8) and counts[(1, "AAA")] == (8, 2)        # e2: rows 10..17, 6 of them already belong to e1
        assert owner["AAA"][27:30].tolist() == [2, 2, 2] and owner["AAA"][26] == -1
        assert counts[(2, "AAA")] == (8, 8) and counts[(2, "BBB")] == (3, 3)
        # a tie in the event bar goes to the smaller event_id regardless of the file order
        twin_a, twin_b = ev("a_first", weekdays[20]), ev("b_second", weekdays[20])
        for order in ([twin_a, twin_b], [twin_b, twin_a]):
            tie_owner, _ = attribute(map_events(order, {"AAA": day}, 1, 1), {"AAA": np.arange(60, dtype=np.int64)})
            assert set(tie_owner["AAA"][19:22].tolist()) == {order.index(twin_a)}
        zero_window = attribute(map_events([ev("z", weekdays[20])], {"AAA": day}, 0, 0), {"AAA": np.arange(60, dtype=np.int64)})[0]["AAA"]
        assert np.flatnonzero(zero_window >= 0).tolist() == [20]                                                # N_pre = N_post = 0: the event bar only
        print("  group 4 windows and overlap OK")

        # 5. end to end on an ONNX fixture and parquet data with weekends (a holiday gap exists in the rows)
        w, c, horizon, splits = 10, ds.FEATURES_OHLCV, 5, ds.DEFAULT_WF_SPLITS
        names = ("AAA", "BBB", "CCC")
        series = {n: (_weekday_dates(330), arr) for n, (_d, arr) in vm._fixture_series(330, names, 7).items()}
        data_dir = tmp / "data"
        vm._write_fixture_parquet(data_dir, series, 300)
        symbols, dates = ds.load_parquet_dir(data_dir, return_dates=True)
        ranges = ds.train_val_date_ranges(symbols, dates, feature_mode="ohlcv_minmax", window=w, horizon=horizon, threshold=ds.DEFAULT_THRESHOLD, n_splits=splits)
        meta = onnx_meta.build_contract(feature_mode="ohlcv_minmax", window_size=w, channels=c, horizon=horizon, threshold=ds.DEFAULT_THRESHOLD,
                                        wf_splits=splits, seed=42, producer="event_eval.py selfcheck", date_ranges=ranges)
        model = tmp / "ev.onnx"
        vm.build_fixture_model(model, window=w, channels=c, classes=3, seed=5, metadata=meta, weight_scale=1.5)
        common = ["--model", str(model), "--data-dir", str(data_dir), "--events-root", str(root), "--min-window-samples", "3", "--max-seconds", "300"]
        meta_dict = {p.key: p.value for p in onnx.load(str(model)).metadata_props}
        probe = _parse_args(["evaluate", "--event-set", "set_v1", *common])
        pop = vm.reconstruct_validation_population(meta_dict, model, probe)
        anchors, symbol_days = validation_index(pop)
        aaa_days = symbol_days["AAA"][anchors["AAA"]]
        assert aaa_days.size >= 12
        mid = aaa_days.size // 2
        d1, d2, d3, d4 = (aaa_days[mid].astype(object), aaa_days[mid + 2].astype(object), aaa_days[mid // 2].astype(object), aaa_days[mid + 4].astype(object))
        saturday = d1 + datetime.timedelta(days=(5 - d1.weekday()) % 7 or 7)
        rows_csv = [
            _event_row("E1", f"{d1}T12:00:00+00:00", event_type="central_bank"),
            _event_row("E2", f"{d2}T12:00:00+00:00", event_type="central_bank"),                       # overlaps E1
            _event_row("E3", f"{saturday}T12:00:00+00:00", scope="AAA", event_type="macro"),            # a weekend: mapped to the next bar
            _event_row("E4", f"{d3}T02:00:00+00:00", tz="America/New_York", scope="aaa,CCC", event_type="macro"),        # the local day is the day before
            _event_row("E5", "2019-01-02T12:00:00+00:00", event_type="macro"),                          # before the data
            _event_row("E6", "2031-01-02T12:00:00+00:00", event_type="macro"),                          # after the data
            _event_row("E7", f"{d4}T12:00:00+00:00", scope="ZZZ", event_type="earnings"),               # no such symbol
            _event_row("E8", f"{weekdays[15]}T12:00:00+00:00", event_type="early"),                     # inside the data, far from every validation anchor
        ]
        events_file = tmp / "synthetic_events.csv"
        events_file.write_bytes(_csv_text(rows_csv))
        import_event_set(events_file, "synthetic_a", root)
        out_a = tmp / "a.events.json"
        sha_model = checkpoints.hash_file(model)
        with contextlib.redirect_stdout(io.StringIO()):
            assert run(_parse_args(["evaluate", "--event-set", "synthetic_a", *common, "--out", str(out_a)])) == EXIT_OK
        doc = json.loads(out_a.read_text(encoding="utf-8"))
        assert doc["status"] == STATUS_AVAILABLE and doc["verification"]["real_event_evaluation"] == REAL_EVALUATION
        assert doc["event_set"]["csv_sha256"] == hashlib.sha256(events_file.read_bytes()).hexdigest() and doc["population"]["oos_consumed"] is False
        assert doc["notices"] == list(NOTICES) and doc["model_sha256"] == sha_model == checkpoints.hash_file(model)
        # independent recount with plain loops over the same population
        folds = vm.validation_folds(pop)
        recount_session = ort.InferenceSession(str(model), providers=["CPUExecutionProvider"])
        probs = predict(recount_session, pop["x"], time.monotonic() + 300)
        del recount_session
        gc.collect()                                                  # Windows: an open session blocks the removal of the temp folder
        y = np.asarray(pop["y"]).astype(np.int64).ravel()
        parsed = {e.event_id: e for e in parse_events_csv(events_file.read_bytes())}
        expected_owner: Dict[Tuple[str, int], str] = {}
        expected_in: Dict[Tuple[str, str], int] = {}
        for name, fold in folds.items():
            day_n = np.asarray(pop["context"]["dates"][name]).astype("datetime64[D]").reshape(-1)
            candidates = []
            for event_id, event in parsed.items():
                if not (event.all_symbols or name.lower() in [t.lower() for t in event.scope]):
                    continue
                bar = next((i for i in range(day_n.size) if day_n[i] >= np.datetime64(event.local_date)), None)
                if bar is None or np.datetime64(event.local_date) < day_n[0]:
                    continue
                candidates.append((bar, event_id, max(0, bar - 2), min(day_n.size - 1, bar + 5)))
            for row in fold[2][fold[4]]:
                inside = sorted((bar, eid) for bar, eid, first, last in candidates if first <= row <= last)
                for _bar, eid in inside:
                    expected_in[(eid, name)] = expected_in.get((eid, name), 0) + 1
                if inside:
                    expected_owner[(name, int(row))] = inside[0][1]
        by_id = {e["event_id"]: e for e in doc["events"]}
        for event_id, record in by_id.items():
            got = {d["symbol"]: d["anchors_in_window"] for d in record["symbols"]}
            want = {n: expected_in.get((event_id, n), 0) for n in got}
            assert got == want, (event_id, got, want)
            attributed = sum(1 for owner_id in expected_owner.values() if owner_id == event_id)
            assert record["anchors_attributed"] == attributed and record["overlap_dropped"] == record["anchors_in_window"] - attributed, event_id
        assert by_id["E1"]["overlap_dropped"] == 0 and by_id["E2"]["overlap_dropped"] > 0, "E2 overlaps the earlier E1"
        assert by_id["E3"]["status"] == EVENT_MAPPED and by_id["E3"]["symbols"][0]["event_bar_date"] > saturday.isoformat()
        assert by_id["E4"]["effective_local_date"] == (d3 - datetime.timedelta(days=1)).isoformat()
        assert by_id["E5"]["skip_reason"] == SKIP_BEFORE and by_id["E6"]["skip_reason"] == SKIP_AFTER and by_id["E7"]["skip_reason"] == SKIP_NO_SYMBOL
        assert by_id["E8"]["status"] == EVENT_OK and by_id["E8"]["anchors_in_window"] == 0
        counts = doc["counts"]
        assert counts["anchors_in_window"] == len(expected_owner) and counts["anchors_total"] == y.shape[0]
        assert counts["anchors_in_window"] + counts["anchors_outside_window"] == counts["anchors_total"]
        assert counts["overlap_dropped_total"] == counts["window_memberships"] - counts["anchors_in_window"] > 0
        assert counts["events_total"] == 8 and counts["events_usable"] == 5 and counts["events_skipped"] == {SKIP_BEFORE: 1, SKIP_AFTER: 1, SKIP_NO_SYMBOL: 1}
        # the metric tables equal classification_report_dict on the independently selected samples
        pooled_keys = [(name, int(r)) for name, f in folds.items() for r in f[2][f[4]]]
        member = np.array([k in expected_owner for k in pooled_keys])
        pred = probs.argmax(axis=1)
        with warnings.catch_warnings():
            warnings.filterwarnings("ignore", message="The y_prob values do not sum to one")
            want_window = _clean(metrics.classification_report_dict(y[member], pred[member], probs[member], num_classes=3, class_labels=list(ds.CLASS_LABELS)))
            want_outside = _clean(metrics.classification_report_dict(y[~member], pred[~member], probs[~member], num_classes=3, class_labels=list(ds.CLASS_LABELS)))
        for key, want in (("in_window", want_window), ("outside_window", want_outside)):
            got = {k: v for k, v in doc["metrics"][key].items() if k != "status"}
            assert got == json.loads(json.dumps(want)), key
        assert doc["metrics"]["in_window"]["n_samples"] + doc["metrics"]["outside_window"]["n_samples"] == y.shape[0]
        by_type = doc["metrics"]["by_event_type"]
        assert set(by_type) == {"central_bank", "macro", "early"} and by_type["early"]["status"] == STATUS_NO_SAMPLES           # "earnings" was skipped: no table
        assert sum(t["n_samples"] for t in by_type.values()) == counts["anchors_in_window"]
        print("  group 5 end to end (independent recount, metrics, overlap, mapping) OK")

        # 6. statuses: no usable event, usable events without samples, a group below the minimum (nothing is zero-filled)
        no_events = tmp / "no_events.csv"
        no_events.write_bytes(_csv_text(rows_csv[4:5] + rows_csv[5:7]))
        import_event_set(no_events, "synthetic_none", root)
        quiet = tmp / "quiet.csv"
        quiet.write_bytes(_csv_text([rows_csv[7]]))
        import_event_set(quiet, "synthetic_quiet", root)
        empty = tmp / "empty.csv"
        empty.write_bytes(_csv_text([]))
        import_event_set(empty, "synthetic_empty", root)
        for set_id, want_status in (("synthetic_none", STATUS_NO_EVENTS), ("synthetic_quiet", STATUS_NO_SAMPLES), ("synthetic_empty", STATUS_NO_EVENTS)):
            target = tmp / f"{set_id}.json"
            with contextlib.redirect_stdout(io.StringIO()):
                assert run(_parse_args(["evaluate", "--event-set", set_id, *common, "--out", str(target)])) == EXIT_OK
            result = json.loads(target.read_text(encoding="utf-8"))
            assert result["status"] == want_status, (set_id, result["status"])
            assert result["metrics"]["in_window"] == dict(status=STATUS_NO_SAMPLES, n_samples=0)
            assert "accuracy" not in json.dumps(result["metrics"]["in_window"]), "no metric is invented for an empty group"
        big_min = tmp / "big_min.json"
        with contextlib.redirect_stdout(io.StringIO()):
            assert run(_parse_args(["evaluate", "--event-set", "synthetic_a", *common[:-4], "--min-window-samples", "100000", "--max-seconds", "300",
                                    "--out", str(big_min)])) == EXIT_OK
        small = json.loads(big_min.read_text(encoding="utf-8"))
        assert small["status"] == STATUS_INSUFFICIENT and small["metrics"]["in_window"] == dict(status=STATUS_INSUFFICIENT, n_samples=counts["anchors_in_window"], minimum=100000)
        assert small["metrics"]["outside_window"]["status"] == STATUS_INSUFFICIENT and small["config_sha256"] != doc["config_sha256"]
        print("  group 6 statuses OK")

        # 7. refusals and exit codes: a changed set, an existing report, a missing model, synthetic data; nothing is written, the model is untouched
        stored = root / "synthetic_a" / CSV_NAME
        original = stored.read_bytes()
        stored.write_bytes(original + b"\r\n")
        refused = tmp / "refused.json"
        with contextlib.redirect_stderr(io.StringIO()) as err:
            assert run(_parse_args(["evaluate", "--event-set", "synthetic_a", *common, "--out", str(refused)])) == EXIT_PREREQUISITE
        assert "no longer matches" in err.getvalue() and not refused.exists()
        stored.write_bytes(original)
        with contextlib.redirect_stderr(io.StringIO()) as err:
            assert run(_parse_args(["evaluate", "--event-set", "synthetic_a", *common, "--out", str(out_a)])) == EXIT_PREREQUISITE
        assert "already exists" in err.getvalue()
        kept = out_a.read_bytes()
        with contextlib.redirect_stdout(io.StringIO()):
            assert run(_parse_args(["evaluate", "--event-set", "synthetic_a", *common, "--out", str(out_a), "--overwrite"])) == EXIT_OK
        assert json.loads(out_a.read_text(encoding="utf-8"))["metrics"] == doc["metrics"] and kept                    # replaced on request, same content
        with contextlib.redirect_stderr(io.StringIO()) as err:
            assert run(_parse_args(["evaluate", "--event-set", "synthetic_a", *common[2:], "--model", str(tmp / "nope.onnx"), "--out", str(refused)])) == EXIT_PREREQUISITE
            assert run(_parse_args(["evaluate", "--event-set", "missing_set", *common, "--out", str(refused)])) == EXIT_PREREQUISITE
            assert run(_parse_args(["evaluate", "--event-set", "synthetic_a", *common, "--symbols", "NOPE", "--out", str(refused)])) == EXIT_PREREQUISITE
        assert not refused.exists()
        import polars as pl

        synth_dir = tmp / "synthetic_data"
        synth_dir.mkdir()
        for parquet in data_dir.glob("*.parquet"):
            pl.read_parquet(parquet).write_parquet(synth_dir / parquet.name, metadata={ds.SYNTHETIC_ORIGIN_KEY: ds.SYNTHETIC_ORIGIN})
        with contextlib.redirect_stderr(io.StringIO()) as err:
            assert run(_parse_args(["evaluate", "--event-set", "synthetic_a", *[str(synth_dir) if a == str(data_dir) else a for a in common],
                                    "--out", str(refused)])) == EXIT_PREREQUISITE
        assert "PREREQUISITE NOT MET" in err.getvalue() and not refused.exists()
        with contextlib.redirect_stderr(io.StringIO()) as err:        # a timeout is a failure (exit 1) and writes nothing
            with mock.patch.object(sys.modules[__name__], "predict", side_effect=TimeoutError("the event evaluation exceeded --max-seconds")):
                assert run(_parse_args(["evaluate", "--event-set", "synthetic_a", *common, "--out", str(refused)])) == EXIT_FAILED
        assert not refused.exists() and checkpoints.hash_file(model) == sha_model
        # CLI import command end to end (a refused file leaves no set)
        with contextlib.redirect_stdout(io.StringIO()):
            assert run(_parse_args(["import", "--csv", str(events_file), "--set-id", "via_cli", "--events-root", str(root)])) == EXIT_OK
        with contextlib.redirect_stderr(io.StringIO()):
            assert run(_parse_args(["import", "--csv", str(events_file), "--set-id", "via_cli", "--events-root", str(root)])) == EXIT_PREREQUISITE
            assert run(_parse_args(["import", "--csv", str(bad_source), "--set-id", "via_cli_bad", "--events-root", str(root)])) == EXIT_PREREQUISITE
        assert not (root / "via_cli_bad").exists() and not list(root.glob(f"*{INCOMING_SUFFIX}"))
        # Phase 3: a report that appears between the first check and the replacement is not overwritten; the named literals keep their old values
        assert SET_ID_PATTERN.pattern == "[A-Za-z0-9][A-Za-z0-9._-]{0,63}" and CONTROL_CHAR_LIMIT == 32 and HASH_PREFIX_CHARS == 16
        real_evaluate = evaluate_population
        racing_out, stolen_report = tmp / "racing.events.json", json.dumps({"other": "writer"})

        def other_writer(*args_, **kwargs_):
            body_ = real_evaluate(*args_, **kwargs_)
            racing_out.write_text(stolen_report, encoding="utf-8")                  # another process finishes first
            return body_

        with mock.patch.object(sys.modules[__name__], "evaluate_population", other_writer), contextlib.redirect_stderr(io.StringIO()) as err:
            assert run(_parse_args(["evaluate", "--event-set", "synthetic_a", *common, "--out", str(racing_out)])) == EXIT_PREREQUISITE
        assert "already exists" in err.getvalue() and racing_out.read_text(encoding="utf-8") == stolen_report, "the other writer's file must stay as it is"
        assert not list(tmp.glob(".incoming_*"))
        # M3: a weekly model is refused before any data is read (the event bar rule is defined for daily bars only); nothing is written
        weekly_meta = onnx_meta.build_contract(feature_mode="ohlcv_minmax", window_size=w, channels=c, horizon=horizon, threshold=ds.DEFAULT_THRESHOLD,
                                               wf_splits=splits, seed=42, producer="event_eval.py selfcheck", date_ranges=ranges, timeframe="weekly")
        weekly_model = tmp / "weekly.onnx"
        vm.build_fixture_model(weekly_model, window=w, channels=c, classes=3, seed=5, metadata=weekly_meta, weight_scale=1.5)
        weekly_out = tmp / "weekly.events.json"
        with contextlib.redirect_stderr(io.StringIO()) as err:
            assert run(_parse_args(["evaluate", "--event-set", "synthetic_a", *common[2:], "--model", str(weekly_model), "--out", str(weekly_out)])) == EXIT_PREREQUISITE
        assert "timeframe_not_supported" in err.getvalue() and "'weekly'" in err.getvalue() and not weekly_out.exists()
        print("  group 7 refusals and exit codes OK")

        # 8. arguments
        evaluate_base = ["evaluate", "--model", "m.onnx", "--event-set", "s1"]
        validate_args(_parse_args(evaluate_base))
        for bad in (["--n-pre", "-1"], ["--n-post", str(MAX_WINDOW_BARS + 1)], ["--min-window-samples", "0"], ["--max-seconds", "0"], ["--wf-splits", "0"],
                    ["--outer-fold-index", "-1"], ["--max-symbols", "0"]):
            try:
                validate_args(_parse_args(evaluate_base + bad))
            except ValueError:
                continue
            raise AssertionError(f"argument {bad} should be rejected")
        try:
            validate_args(_parse_args(["evaluate", "--model", "m.onnx", "--event-set", "../up"]))
            raise AssertionError("a path-like set id should be rejected")
        except ValueError:
            pass
        print("  group 8 arguments OK")
    print("event_eval.py selfcheck: OK (8 groups passed)")


def _parse_args(argv: List[str]) -> argparse.Namespace:
    p = argparse.ArgumentParser(description="Event-window evaluation of a trained classifier on user-supplied events (T22).")
    p.add_argument("--selfcheck", action="store_true", help="Run the assertions and exit.")
    sub = p.add_subparsers(dest="command")
    imp = sub.add_parser("import", help="Validate an event CSV and store it as an immutable event set.")
    imp.add_argument("--csv", type=Path, required=True)
    imp.add_argument("--set-id", required=True, help="Name of the new set; a revision of a calendar is a new set id.")
    imp.add_argument("--events-root", type=Path, default=None, help=f"Default: <DataRoot>/{EVENTS_DIRNAME}")
    ev = sub.add_parser("evaluate", help="Compare model behaviour inside and outside the event windows on the validation population.")
    ev.add_argument("--model", type=Path, required=True)
    ev.add_argument("--event-set", required=True)
    ev.add_argument("--events-root", type=Path, default=None, help=f"Default: <DataRoot>/{EVENTS_DIRNAME}")
    ev.add_argument("--n-pre", type=int, default=DEFAULT_N_PRE, help="Bars before the event bar (V06).")
    ev.add_argument("--n-post", type=int, default=DEFAULT_N_POST, help="Bars after the event bar (V06).")
    ev.add_argument("--min-window-samples", type=int, default=DEFAULT_MIN_WINDOW_SAMPLES, help="A group below this is insufficient_data (V15).")
    vm.add_population_arguments(ev)
    ev.add_argument("--max-seconds", type=int, default=MAX_SECONDS, help=f"Run bound; env {MAX_SECONDS_ENV}.")
    ev.add_argument("--out", type=Path, default=None, help="Default: <model>.onnx.events.json")
    ev.add_argument("--overwrite", action="store_true", help="Replace an existing report file.")
    return p.parse_args(argv)


if __name__ == "__main__":
    if "--selfcheck" in sys.argv[1:]:
        _run_selfcheck()
    else:
        _args = _parse_args(sys.argv[1:])
        if _args.command is None:
            print("event_eval: invalid argument: choose a command (import | evaluate) or --selfcheck", file=sys.stderr)
            raise SystemExit(2)
        try:
            validate_args(_args)
        except ValueError as _exc:
            print(f"event_eval: invalid argument: {_exc}", file=sys.stderr)
            raise SystemExit(2)
        raise SystemExit(run(_args))
