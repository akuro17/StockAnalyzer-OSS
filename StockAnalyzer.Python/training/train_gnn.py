"""T25 graph neural network over a fixed set of symbols: one correlation graph per date, one prediction per node.

Approved decisions (2026-10-11, ``Y:\\Temp\\sa_decision_options_onnx_meta_attention_gnn_t23_t25.md`` star rows C1..C5, G1..G11, S1..S3, S5..S8, S19..S23):

* G1 A dense-matrix GCN written with torch only (no new dependency). G2 A the node set is the symbol list sorted ascending (``node_order``); inference refuses another
  identifier set. G3/S19/S20 the edge weight is the absolute Pearson correlation of the nodes' own one-bar log returns over the last ``corr_window`` calendar dates
  ending at the anchor date t: a return is built from a node's own two consecutive rows and is dated by the later row; a date the node does not have is a missing value
  (nothing is carried forward); ONE missing value in the window, or a constant series, means no edge; ``|rho| >= edge_threshold`` is an edge. Only data up to t is read.
* G4 a sample is a calendar date with at least ``min_nodes`` valid nodes; a node without a window at t has ``node_mask`` 0, zero features and no edge, and is left out of
  the loss. G5 the encoder is ``LstmClassifier.encode_sequence`` (last hidden state, shared by every node), then two GCN layers (ReLU, dropout), then a per-node Linear(3),
  softmax at export. The symmetric normalization ``D^-1/2 (A + I) D^-1/2`` lives inside the model (the consumer sends the raw adjacency). G6 labels are
  ``dataset.make_label`` per node (a node without a label is left out of the loss). G7 chronological last fold of ``dataset.walk_forward_split_indexed`` with a purge gap.
* G8 ONNX: ``node_features[B,N,W,C] adjacency[B,N,N] node_mask[B,N] -> output[B,N,3]`` (float32), metadata namespace ``com.stockanalyzer.graph.*``; it is not a model of
  the single-symbol prediction contract and the registry/``PredictionService`` refuse it. G11 / S2 / S3 hyperparameters are job options with named defaults that have NO
  empirical basis (``thresholds_basis``); safety bounds are the ``Limits`` class (mirrored by the C# ``GraphLimits``); only the run duration is read from the
  environment (``SA_GN_MAX_SECONDS``).
* C3 / S8 the out-of-sample tail (``--oos-tail-days`` >= 1) is never read, synthetic data and non-daily timeframes are refused (exit 3). S5 / S6 the run is a derived
  artifact under ``<DataRoot>/TrainingArtifacts/Graph/<runId>`` (``graph.onnx`` + ``graph.json``), built as ``<runId>.incoming`` and renamed once; it is not registered
  anywhere. Exit codes: 0 ok, 1 run failure (timeout, non-finite value), 2 invalid argument, 3 prerequisite. S7 shuffling uses ``SeedSequence(seed, spawn_key=...)``.

Decision time is the close of the anchor date: the return dated t uses Close[t] and Close[t-1]; the label uses Close[t+horizon].

The result is a descriptive validation report, not a statement about real-market usefulness and not advice.

Usage::

    python train_gnn.py train --symbols AAA,BBB,CCC --oos-tail-days 60
    python train_gnn.py --selfcheck
"""

from __future__ import annotations

import argparse
import copy
import datetime
import json
import math
import sys
import tempfile
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, List, Sequence, Tuple

import numpy as np
import torch
from torch import nn

sys.path.insert(0, str(Path(__file__).resolve().parent))
import checkpoints  # noqa: E402
import dataset as ds  # noqa: E402
import metrics  # noqa: E402
import onnx_meta  # noqa: E402
import train_pytorch as tp  # noqa: E402  (LstmClassifier)
from train_meta import PrerequisiteError, publish_run  # noqa: E402  (derived-artifact publication shared with T23; no edit of that module)

SCHEMA_VERSION: int = 1
TOOL: str = "train_gnn.py"
TOOL_VERSION: str = "1"
KIND: str = "Graph"
GRAPH_ONNX_NAME: str = "graph.onnx"
GRAPH_DOC_NAME: str = "graph.json"
RNG_POLICY: str = "per_epoch_seed_sequence"
THRESHOLDS_BASIS_DEFAULT: str = "planning_default_uncalibrated"
THRESHOLDS_BASIS_USER: str = "user_supplied"
SUPPORTED_TIMEFRAME: str = onnx_meta.DEFAULT_TIMEFRAME
NUM_CLASSES: int = len(ds.CLASS_LABELS)
ELAPSED_DIGITS: int = 3
OPSET_DEFAULT: int = tp.DEFAULT_OPSET
ONNX_ATOL: float = tp.ONNX_ATOL
EXIT_OK, EXIT_FAILED, EXIT_PREREQUISITE = 0, 1, 3
STREAM_SHUFFLE: int = 0                       # SeedSequence spawn_key[0]

# --- Contract (mirrored by C# ``GraphContract``; ``Tests/GraphParity.json`` keeps both sides identical) ------------------------------------
CONTRACT_VERSION: int = 1
META_PREFIX: str = "com.stockanalyzer.graph."
KEY_CONTRACT_VERSION: str = META_PREFIX + "contract_version"
KEY_NODE_ORDER: str = META_PREFIX + "node_order"
KEY_WINDOW: str = META_PREFIX + "window"
KEY_HORIZON: str = META_PREFIX + "horizon"
KEY_CORR_WINDOW: str = META_PREFIX + "corr_window"
KEY_EDGE_THRESHOLD: str = META_PREFIX + "edge_threshold"
KEY_MIN_NODES: str = META_PREFIX + "min_nodes"
KEY_FEATURE_MODE: str = META_PREFIX + "feature_mode"
KEY_CHANNELS: str = META_PREFIX + "channels"
KEY_CLASS_ORDER: str = META_PREFIX + "class_order"
KEY_TIMEFRAME: str = META_PREFIX + "timeframe"
CONTRACT_KEYS: Tuple[str, ...] = (
    KEY_CONTRACT_VERSION, KEY_NODE_ORDER, KEY_WINDOW, KEY_HORIZON, KEY_CORR_WINDOW, KEY_EDGE_THRESHOLD, KEY_MIN_NODES, KEY_FEATURE_MODE, KEY_CHANNELS,
    KEY_CLASS_ORDER, KEY_TIMEFRAME)
INPUT_NODE_FEATURES: str = "node_features"
INPUT_ADJACENCY: str = "adjacency"
INPUT_NODE_MASK: str = "node_mask"
OUTPUT_NAME: str = "output"
MIN_CORR_WINDOW: int = 2                      # a Pearson correlation needs two observations (``CalculateRollingPearsonCorrelation`` requires period >= 2)

# Layer 1 (job hyperparameters, planning values without an empirical basis; overridable on the command line).
DEFAULT_CORR_WINDOW: int = 60
DEFAULT_EDGE_THRESHOLD: float = 0.5
DEFAULT_MIN_NODES: int = 2
DEFAULT_HIDDEN: int = 32
DEFAULT_EPOCHS: int = 30
DEFAULT_BATCH: int = 16
DEFAULT_LR: float = 1e-3
DEFAULT_WEIGHT_DECAY: float = 1e-4
DEFAULT_DROPOUT: float = 0.1
DEFAULT_SEED: int = tp.DEFAULT_SEED
DEFAULT_ENCODER_LAYERS: int = tp.DEFAULT_LAYERS
DEFAULT_FEATURE_MODE: str = "ohlcv_minmax"


class Limits:
    """Layer 3: safety bounds no option can widen. ``MAX_NODES`` and ``MAX_TENSOR_BYTES`` mirror ``GraphLimits`` on the C# side."""

    MAX_NODES: int = 64
    MAX_WINDOW: int = 1024                  # bounds the feature window and the correlation window
    MAX_TENSOR_BYTES: int = 512 * 1024 * 1024
    MAX_DATES: int = 100_000
    MAX_EPOCHS: int = 10_000
    MAX_BATCH: int = 4096
    MAX_HIDDEN: int = 1024


# Layer 4: run duration (hard / standard band as in checkpoints.resolve_band; same form as SA_MT_MAX_SECONDS).
MAX_SECONDS_ENV: str = "SA_GN_MAX_SECONDS"
MAX_SECONDS: int = checkpoints.resolve_band(MAX_SECONDS_ENV, 3600, (1, 86_400), (60, 7_200))

NOTICES: Tuple[str, ...] = (
    "descriptive_validation_report_not_advice", "no_empirical_basis_for_default_hyperparameters", "real_world_validation_unverified",
    "purge_gap_covers_the_correlation_lookback", "symbols_are_contemporaneous_cross_symbol_time_overlap_is_not_purged")

# Epoch selection (DEC-3 A, 2026-10-11): ``last`` keeps the weights of the final epoch, ``val_loss`` keeps the weights of the epoch with the smallest validation loss.
# The validation metrics of a run that selected on them are optimistic: the run says so with this notice (added to the document of that run only).
SELECT_LAST: str = "last"
SELECT_VAL_LOSS: str = "val_loss"
SELECT_CHOICES: Tuple[str, ...] = (SELECT_LAST, SELECT_VAL_LOSS)
SELECTION_METRIC_LAST: str = "last_epoch"
SELECTION_FALLBACK_NO_LOSS: str = "no_validation_loss"
NOTICE_VALIDATION_USED_FOR_SELECTION: str = "validation_metrics_used_for_selection"


# --- Small helpers ------------------------------------------------------------------------------------------------------------------


def _generator(seed: int, *key: int) -> np.random.Generator:
    return np.random.default_rng(np.random.SeedSequence(int(seed), spawn_key=tuple(int(k) for k in key)))


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


def _write_json(path: Path, value: dict) -> None:
    path.write_text(json.dumps(value, sort_keys=True, indent=2, allow_nan=False), encoding="utf-8")


def _check_bytes(elements: int, what: str) -> None:
    needed = int(elements) * 4
    if needed > Limits.MAX_TENSOR_BYTES:
        raise PrerequisiteError(f"{what} needs {needed} bytes of float32 values, above the bound of {Limits.MAX_TENSOR_BYTES}")


# --- Graph definition (G2 / G3 / G4 / G6; S19 / S20) ----------------------------------------------------------------------------------


@dataclass(frozen=True)
class GraphSpec:
    """The inputs of one graph sample. ``feature_mode`` must be a per-window mode of ``dataset.FEATURE_MODES``."""

    feature_mode: str
    window: int
    horizon: int
    threshold: float          # neutral threshold of ``dataset.make_label``
    corr_window: int
    edge_threshold: float
    min_nodes: int

    @property
    def channels(self) -> int:
        return ds.feature_channels(self.feature_mode)


@dataclass(frozen=True)
class NodeSeries:
    """One node's chronological rows: ``ohlcv`` (rows, 5) float64 and the row-aligned ``dates`` (datetime64[D], strictly increasing)."""

    symbol: str
    ohlcv: np.ndarray
    dates: np.ndarray


def build_calendar(nodes: Sequence[NodeSeries]) -> np.ndarray:
    """Sorted union of every node's dates. A date is never invented: each calendar date is a date at least one node has."""
    parts = [np.asarray(node.dates).astype("datetime64[D]") for node in nodes if node.dates.shape[0]]
    return np.unique(np.concatenate(parts)) if parts else np.empty(0, dtype="datetime64[D]")


def node_row_index(nodes: Sequence[NodeSeries], calendar: np.ndarray) -> np.ndarray:
    """(dates, nodes) int64: the node's own row at each calendar date, ``-1`` where the node has no row on that date."""
    out = np.full((calendar.shape[0], len(nodes)), -1, dtype=np.int64)
    for i, node in enumerate(nodes):
        node_dates = np.asarray(node.dates).astype("datetime64[D]")
        if node_dates.shape[0] == 0:
            continue
        found = np.searchsorted(node_dates, calendar)
        clipped = np.minimum(found, node_dates.shape[0] - 1)
        hit = (found < node_dates.shape[0]) & (node_dates[clipped] == calendar)
        out[hit, i] = found[hit]
    return out


def log_return_matrix(nodes: Sequence[NodeSeries], row_index: np.ndarray) -> np.ndarray:
    """(dates, nodes) float64, NaN = missing. The return of a node is ``ln(close[row] / close[row - 1])`` of its OWN two consecutive rows, dated by the later row;
    a date without a row, a first row, or a non-positive / non-finite close gives NaN. Nothing is carried forward (S20)."""
    out = np.full(row_index.shape, np.nan, dtype=np.float64)
    for i, node in enumerate(nodes):
        close = np.asarray(node.ohlcv, dtype=np.float64)[:, ds.CLOSE] if node.ohlcv.shape[0] else np.empty(0)
        if close.shape[0] < 2:
            continue
        returns = np.full(close.shape[0], np.nan, dtype=np.float64)
        usable = np.isfinite(close[1:]) & np.isfinite(close[:-1]) & (close[1:] > 0.0) & (close[:-1] > 0.0)
        returns[1:][usable] = np.log(close[1:][usable] / close[:-1][usable])
        rows = row_index[:, i]
        has_row = rows >= 0
        out[has_row, i] = returns[rows[has_row]]
    return out


def adjacency_at(returns: np.ndarray, position: int, corr_window: int, edge_threshold: float) -> np.ndarray:
    """(nodes, nodes) float32 raw adjacency at calendar ``position`` (zero diagonal). The weight of an edge is ``|rho|`` of the two nodes' return columns over the last
    ``corr_window`` calendar positions ending at ``position`` and is kept when ``|rho| >= edge_threshold``. A window that starts before the calendar, any missing
    value in either column, or a constant column means no edge (the semantics of ``IndicatorCalculationHelper.CalculateRollingPearsonCorrelation``)."""
    n = returns.shape[1]
    adjacency = np.zeros((n, n), dtype=np.float32)
    start = position - corr_window + 1
    if start < 0:
        return adjacency
    window = returns[start:position + 1]
    usable = ~np.isnan(window).any(axis=0)
    usable[usable] &= np.ptp(window[:, usable], axis=0) > 0.0
    index = np.nonzero(usable)[0]
    if index.size < 2:
        return adjacency
    columns = window[:, index]
    centered = columns - columns.mean(axis=0)
    covariance = centered.T @ centered
    scale = np.sqrt(np.diag(covariance))
    rho = np.clip(covariance / np.outer(scale, scale), -1.0, 1.0)
    weight = np.abs(rho)
    weight[weight < edge_threshold] = 0.0
    np.fill_diagonal(weight, 0.0)
    adjacency[np.ix_(index, index)] = weight.astype(np.float32)
    return adjacency


def node_window_valid(ohlcv: np.ndarray, row: int, window: int) -> bool:
    """A node has a feature window at ``row`` when ``window`` finite rows end there and the anchor close is positive (the rule of ``dataset.build_dataset``)."""
    if row < window - 1:
        return False
    block = ohlcv[row - window + 1:row + 1]
    return bool(np.all(np.isfinite(block)) and block[-1, ds.CLOSE] > 0.0)


def node_label(ohlcv: np.ndarray, row: int, horizon: int, threshold: float) -> int:
    """``dataset.make_label`` of the anchor row, or ``-1`` when the future close does not exist / is not finite."""
    future_row = row + horizon
    if future_row >= ohlcv.shape[0]:
        return -1
    anchor, future = float(ohlcv[row, ds.CLOSE]), float(ohlcv[future_row, ds.CLOSE])
    if anchor <= 0.0 or not math.isfinite(future):
        return -1
    return int(ds.make_label(anchor, future, threshold))


class GraphBuilder:
    """All graph inputs of a set of nodes. ``sample(position)`` reads rows and returns at or before the calendar position only."""

    def __init__(self, nodes: Sequence[NodeSeries], spec: GraphSpec, *, with_labels: bool):
        if len(nodes) > Limits.MAX_NODES:
            raise PrerequisiteError(f"{len(nodes)} nodes exceed the bound of {Limits.MAX_NODES}")
        self.nodes = list(nodes)
        self.spec = spec
        self.calendar = build_calendar(self.nodes)
        if self.calendar.shape[0] > Limits.MAX_DATES:
            raise PrerequisiteError(f"{self.calendar.shape[0]} calendar dates exceed the bound of {Limits.MAX_DATES}")
        self.rows = node_row_index(self.nodes, self.calendar)
        self.returns = log_return_matrix(self.nodes, self.rows)
        channels = spec.channels
        _check_bytes(sum(node.ohlcv.shape[0] for node in self.nodes) * spec.window * channels, "the node windows")
        transform = ds.get_transform(spec.feature_mode)
        self._features: List[np.ndarray] = []
        self._valid: List[np.ndarray] = []
        for node in self.nodes:
            data = np.asarray(node.ohlcv, dtype=np.float64).reshape(-1, 5)
            valid = np.zeros(data.shape[0], dtype=bool)
            features = np.zeros((data.shape[0], spec.window, channels), dtype=np.float32)
            for row in range(data.shape[0]):
                if node_window_valid(data, row, spec.window):
                    features[row] = transform(data[row - spec.window + 1:row + 1])
                    valid[row] = True
            self._features.append(features)
            self._valid.append(valid)
        self.labels = np.full(self.rows.shape, -1, dtype=np.int64)
        self.node_valid = np.zeros(self.rows.shape, dtype=bool)
        for i, node in enumerate(self.nodes):
            data = np.asarray(node.ohlcv, dtype=np.float64).reshape(-1, 5)
            for position in np.nonzero(self.rows[:, i] >= 0)[0]:
                row = int(self.rows[position, i])
                if self._valid[i][row]:
                    self.node_valid[position, i] = True
                    if with_labels:
                        self.labels[position, i] = node_label(data, row, spec.horizon, spec.threshold)

    @property
    def node_count(self) -> int:
        return len(self.nodes)

    def usable_positions(self, *, require_label: bool) -> np.ndarray:
        """Calendar positions with at least ``min_nodes`` valid nodes (and, for training, at least one labeled node)."""
        enough = self.node_valid.sum(axis=1) >= self.spec.min_nodes
        if require_label:
            enough &= (self.labels >= 0).any(axis=1)
        return np.nonzero(enough)[0].astype(np.int64)

    def sample(self, position: int) -> Tuple[np.ndarray, np.ndarray, np.ndarray]:
        """``(node_features [N,W,C] float32, adjacency [N,N] float32, node_mask [N] float32)`` for the calendar position."""
        n, spec = self.node_count, self.spec
        x = np.zeros((n, spec.window, spec.channels), dtype=np.float32)
        mask = np.zeros(n, dtype=np.float32)
        for i in range(n):
            if self.node_valid[position, i]:
                x[i] = self._features[i][int(self.rows[position, i])]
                mask[i] = 1.0
        adjacency = adjacency_at(self.returns, position, spec.corr_window, spec.edge_threshold)
        adjacency[mask == 0.0, :] = 0.0
        adjacency[:, mask == 0.0] = 0.0
        return x, adjacency, mask

    def batch(self, positions: Sequence[int]) -> Tuple[torch.Tensor, torch.Tensor, torch.Tensor, torch.Tensor]:
        parts = [self.sample(int(p)) for p in positions]
        x = torch.from_numpy(np.stack([p[0] for p in parts]))
        adjacency = torch.from_numpy(np.stack([p[1] for p in parts]))
        mask = torch.from_numpy(np.stack([p[2] for p in parts]))
        y = torch.from_numpy(self.labels[np.asarray(positions, dtype=np.int64)])
        return x, adjacency, mask, y


def inference_sample(histories: Dict[str, NodeSeries], node_order: Sequence[str], spec: GraphSpec, as_of: np.datetime64) -> Tuple[np.ndarray, np.ndarray, np.ndarray]:
    """The Python reference of the C# consumer: rows after ``as_of`` are dropped first, a node without history is an empty node (masked)."""
    cutoff = np.datetime64(as_of, "D")
    nodes: List[NodeSeries] = []
    for symbol in node_order:
        source = histories.get(symbol)
        if source is None or source.dates.shape[0] == 0:
            nodes.append(NodeSeries(symbol, np.empty((0, 5)), np.empty(0, dtype="datetime64[D]")))
            continue
        keep = np.asarray(source.dates).astype("datetime64[D]") <= cutoff
        nodes.append(NodeSeries(symbol, np.asarray(source.ohlcv)[keep], np.asarray(source.dates).astype("datetime64[D]")[keep]))
    builder = GraphBuilder(nodes, spec, with_labels=False)
    position = int(np.searchsorted(builder.calendar, cutoff))
    if position >= builder.calendar.shape[0] or builder.calendar[position] != cutoff:
        raise ValueError("as_of is not a date of any node")
    return builder.sample(position)


# --- Model (G5) ---------------------------------------------------------------------------------------------------------------------


def normalized_adjacency(adjacency: torch.Tensor, node_mask: torch.Tensor) -> torch.Tensor:
    """``D^-1/2 (A + I) D^-1/2`` for the valid nodes: the raw adjacency is masked to valid pairs, its diagonal is replaced by the self loop (weight 1 for a valid
    node, 0 otherwise). A masked node has an all-zero row, so it contributes nothing to a neighbour and receives nothing."""
    n = adjacency.shape[-1]
    eye = torch.eye(n, dtype=adjacency.dtype, device=adjacency.device)
    valid_pair = node_mask.unsqueeze(-1) * node_mask.unsqueeze(-2)
    a = adjacency * valid_pair * (1.0 - eye) + eye * node_mask.unsqueeze(-1)
    degree = a.sum(dim=-1).clamp(min=1.0)                 # a valid node has degree >= 1; a masked row is zero and stays zero
    scale = torch.rsqrt(degree)
    return scale.unsqueeze(-1) * a * scale.unsqueeze(-2)


class GraphNet(nn.Module):
    """Shared LSTM encoder per node, two dense GCN layers, a per-node Linear head. Returns logits ``[B, N, 3]``."""

    def __init__(self, channels: int, hidden: int, encoder_layers: int, dropout: float):
        super().__init__()
        self.encoder = tp.LstmClassifier(channels, hidden, encoder_layers, dropout, NUM_CLASSES)   # its own head is not used; only encode_sequence is
        self.gcn1 = nn.Linear(hidden, hidden)
        self.gcn2 = nn.Linear(hidden, hidden)
        self.drop = nn.Dropout(dropout)
        self.head = nn.Linear(hidden, NUM_CLASSES)

    def forward(self, node_features: torch.Tensor, adjacency: torch.Tensor, node_mask: torch.Tensor) -> torch.Tensor:
        batch, nodes, window, channels = node_features.shape[0], node_features.shape[1], node_features.shape[2], node_features.shape[3]
        encoded = self.encoder.encode_sequence(node_features.reshape(-1, window, channels))[:, -1, :].reshape(batch, nodes, -1)
        h = encoded * node_mask.unsqueeze(-1)
        a_hat = normalized_adjacency(adjacency, node_mask)
        h = self.drop(torch.relu(self.gcn1(torch.matmul(a_hat, h))))
        h = self.drop(torch.relu(self.gcn2(torch.matmul(a_hat, h))))
        return self.head(h)


class GraphExport(nn.Module):
    """Adds the softmax so the exported ONNX emits probabilities (the single-symbol exporter does the same)."""

    def __init__(self, base: GraphNet):
        super().__init__()
        self.base = base

    def forward(self, node_features: torch.Tensor, adjacency: torch.Tensor, node_mask: torch.Tensor) -> torch.Tensor:
        return torch.softmax(self.base(node_features, adjacency, node_mask), dim=-1)


def masked_cross_entropy(logits: torch.Tensor, labels: torch.Tensor) -> torch.Tensor:
    """Mean cross entropy over the positions with a label (``label >= 0``); positions without one contribute nothing. No labeled position is an error."""
    keep = labels >= 0
    if not bool(keep.any()):
        raise ValueError("a batch without any labeled node")
    return nn.functional.cross_entropy(logits[keep], labels[keep])


# --- Training / evaluation (G7 / G11) --------------------------------------------------------------------------------------------


@dataclass(frozen=True)
class TrainSpec:
    epochs: int
    batch: int
    lr: float
    weight_decay: float
    seed: int


def _check_deadline(deadline: float | None) -> None:
    if deadline is not None and time.monotonic() > deadline:
        raise TimeoutError("the run exceeded its run-duration bound")


def evaluate(model: GraphNet, builder: GraphBuilder, positions: np.ndarray, batch: int) -> dict:
    """Loss and per-class metrics over every labeled node of the given dates (model in eval mode, no gradient)."""
    model.eval()
    truth: List[np.ndarray] = []
    probs: List[np.ndarray] = []
    total_loss, count = 0.0, 0
    with torch.no_grad():
        for start in range(0, positions.shape[0], batch):
            x, adjacency, mask, y = builder.batch(positions[start:start + batch])
            logits = model(x, adjacency, mask)
            keep = y >= 0
            if not bool(keep.any()):
                continue
            total_loss += float(nn.functional.cross_entropy(logits[keep], y[keep], reduction="sum"))
            count += int(keep.sum())
            truth.append(y[keep].numpy())
            probs.append(torch.softmax(logits[keep].double(), dim=-1).numpy())
    if count == 0:
        return dict(status="insufficient_data", labeled_nodes=0)
    y_true, p = np.concatenate(truth), np.concatenate(probs)
    report = metrics.classification_report_dict(y_true, p.argmax(axis=1), p)
    majority = float(np.bincount(y_true, minlength=NUM_CLASSES).max()) / count
    return dict(status="available", labeled_nodes=count, loss=total_loss / count, majority_class_accuracy=majority, report=report)


def select_best_epoch(validation_losses: Sequence[float | None]) -> int | None:
    """The 1-based epoch of the strictly smallest finite validation loss (a tie keeps the earlier epoch); ``None`` and non-finite entries are skipped;
    ``None`` when no entry qualifies."""
    best_epoch, best_loss = None, None
    for epoch, loss in enumerate(validation_losses, start=1):
        if loss is None or not math.isfinite(loss):
            continue
        if best_loss is None or loss < best_loss:
            best_epoch, best_loss = epoch, loss
    return best_epoch


def train_graph(model: GraphNet, builder: GraphBuilder, train_positions: np.ndarray, val_positions: np.ndarray, spec: TrainSpec,
                *, deadline: float | None = None, select_best: str = SELECT_LAST) -> dict:
    """``spec.epochs`` epochs of AdamW over shuffled date batches. ``select_best=last`` (the default) keeps the weights of the LAST epoch;
    ``select_best=val_loss`` loads the weights of the epoch with the smallest validation loss (and falls back to the last epoch, saying so, when no epoch
    has a finite validation loss). The returned dict records which epoch was selected and whether validation was used for the selection."""
    if select_best not in SELECT_CHOICES:
        raise ValueError(f"select_best must be one of {SELECT_CHOICES}")
    optimizer = torch.optim.AdamW(model.parameters(), lr=spec.lr, weight_decay=spec.weight_decay)
    curve: List[dict] = []
    best_loss: float | None = None
    best_state: Dict[str, torch.Tensor] | None = None
    for epoch in range(spec.epochs):
        _check_deadline(deadline)
        model.train()
        order = _generator(spec.seed, STREAM_SHUFFLE, epoch).permutation(train_positions)
        epoch_loss, batches = 0.0, 0
        for start in range(0, order.shape[0], spec.batch):
            _check_deadline(deadline)
            x, adjacency, mask, y = builder.batch(order[start:start + spec.batch])
            if not bool((y >= 0).any()):
                continue
            loss = masked_cross_entropy(model(x, adjacency, mask), y)
            if not torch.isfinite(loss):
                raise ArithmeticError(f"non-finite training loss at epoch {epoch + 1}")
            optimizer.zero_grad()
            loss.backward()
            optimizer.step()
            epoch_loss += float(loss.detach())
            batches += 1
        validation = evaluate(model, builder, val_positions, spec.batch)
        validation_loss = validation.get("loss")
        curve.append(dict(epoch=epoch + 1, train_loss=epoch_loss / max(batches, 1), validation_loss=validation_loss))
        # the same rule as select_best_epoch: strictly smaller and finite, so a tie keeps the earlier epoch
        if select_best == SELECT_VAL_LOSS and validation_loss is not None and math.isfinite(validation_loss) and (best_loss is None or validation_loss < best_loss):
            best_loss, best_state = validation_loss, copy.deepcopy(model.state_dict())
    used = best_state is not None
    if used:
        model.load_state_dict(best_state)
    result = dict(epochs=spec.epochs, curve=curve, selection_metric=SELECT_VAL_LOSS if used else SELECTION_METRIC_LAST,
                  best_epoch=select_best_epoch([row["validation_loss"] for row in curve]), final_epoch=spec.epochs, validation_used_for_selection=used)
    if select_best == SELECT_VAL_LOSS and not used:
        result["selection_fallback"] = SELECTION_FALLBACK_NO_LOSS
    return result


def split_positions(positions: np.ndarray, gap: int) -> Tuple[np.ndarray, np.ndarray]:
    """Last fold of ``dataset.walk_forward_split_indexed`` over the sample dates, then the purge gap enforced in calendar positions: every validation date lies more
    than ``gap`` calendar positions after the last training date. Raises ``PrerequisiteError`` when either side is empty."""
    folds = ds.walk_forward_split_indexed(int(positions.shape[0]), ds.DEFAULT_WF_SPLITS, gap)
    if not folds:
        raise PrerequisiteError(f"{positions.shape[0]} sample dates are too few for {ds.DEFAULT_WF_SPLITS} chronological folds with a purge gap of {gap}")
    _, train_index, val_index = folds[-1]
    train, val = positions[train_index], positions[val_index]
    val = val[val > train[-1] + gap]
    if train.shape[0] == 0 or val.shape[0] == 0:
        raise PrerequisiteError("the chronological split leaves no training or no validation date")
    return train, val


# --- Export (G8) --------------------------------------------------------------------------------------------------------------------


def contract_metadata(node_order: Sequence[str], spec: GraphSpec) -> Dict[str, str]:
    """The ONNX metadata (and the ``contract`` block of ``graph.json``): every value is a string."""
    return {
        KEY_CONTRACT_VERSION: str(CONTRACT_VERSION),
        KEY_NODE_ORDER: json.dumps(list(node_order), separators=(",", ":"), ensure_ascii=False),
        KEY_WINDOW: str(spec.window), KEY_HORIZON: str(spec.horizon), KEY_CORR_WINDOW: str(spec.corr_window),
        KEY_EDGE_THRESHOLD: repr(float(spec.edge_threshold)), KEY_MIN_NODES: str(spec.min_nodes),
        KEY_FEATURE_MODE: spec.feature_mode, KEY_CHANNELS: str(spec.channels),
        KEY_CLASS_ORDER: ",".join(ds.CLASS_LABELS), KEY_TIMEFRAME: SUPPORTED_TIMEFRAME,
    }


def export_graph_onnx(model: GraphNet, node_count: int, spec: GraphSpec, out_path: Path, metadata: Dict[str, str], opset: int = OPSET_DEFAULT) -> None:
    model.eval()
    wrapper = GraphExport(model).eval()
    dummy = (torch.randn(1, node_count, spec.window, spec.channels, dtype=torch.float32), torch.zeros(1, node_count, node_count, dtype=torch.float32),
             torch.ones(1, node_count, dtype=torch.float32))
    axes = {name: {0: "batch"} for name in (INPUT_NODE_FEATURES, INPUT_ADJACENCY, INPUT_NODE_MASK, OUTPUT_NAME)}
    torch.onnx.export(wrapper, dummy, str(out_path), input_names=[INPUT_NODE_FEATURES, INPUT_ADJACENCY, INPUT_NODE_MASK], output_names=[OUTPUT_NAME],
                      dynamic_axes=axes, opset_version=opset, dynamo=False)   # the stable TorchScript exporter, as the single-symbol exporter (C# ORT 1.24.3)
    onnx_meta.load_apply_save(out_path, metadata)


def verify_graph_onnx(path: Path, model: GraphNet, builder: GraphBuilder | None, node_count: int, spec: GraphSpec) -> float:
    """Checker + torch-vs-onnxruntime agreement on random inputs (batch 1 and 3) and, when a builder is given, on a real sample. Returns the largest delta."""
    import onnx
    import onnxruntime as ort

    onnx.checker.check_model(onnx.load(str(path)))
    session = ort.InferenceSession(str(path), providers=["CPUExecutionProvider"])
    assert [i.name for i in session.get_inputs()] == [INPUT_NODE_FEATURES, INPUT_ADJACENCY, INPUT_NODE_MASK], [i.name for i in session.get_inputs()]
    assert [o.name for o in session.get_outputs()] == [OUTPUT_NAME]
    assert [len(i.shape) for i in session.get_inputs()] == [4, 3, 2] and len(session.get_outputs()[0].shape) == 3
    rng = np.random.default_rng(0)
    delta = 0.0
    model.eval()
    cases = []
    for batch in (1, 3):
        x = rng.standard_normal((batch, node_count, spec.window, spec.channels)).astype(np.float32)
        a = (rng.random((batch, node_count, node_count)) > 0.5).astype(np.float32) * rng.random((batch, node_count, node_count)).astype(np.float32)
        a = np.maximum(a, a.transpose(0, 2, 1))
        m = np.ones((batch, node_count), dtype=np.float32)
        m[:, -1] = 0.0 if node_count > 2 else 1.0
        cases.append((x, a, m))
    if builder is not None:
        usable = builder.usable_positions(require_label=False)
        if usable.shape[0]:
            s = builder.sample(int(usable[-1]))
            cases.append(tuple(v[None] for v in s))
    for x, a, m in cases:
        (probs,) = session.run([OUTPUT_NAME], {INPUT_NODE_FEATURES: x, INPUT_ADJACENCY: a, INPUT_NODE_MASK: m})
        with torch.no_grad():
            ref = torch.softmax(model(torch.from_numpy(x), torch.from_numpy(a), torch.from_numpy(m)), dim=-1).numpy()
        assert probs.shape == (x.shape[0], node_count, NUM_CLASSES), probs.shape
        assert np.isfinite(probs).all() and np.allclose(probs.sum(axis=-1), 1.0, atol=ONNX_ATOL)
        delta = max(delta, float(np.max(np.abs(ref - probs))))
    assert delta < ONNX_ATOL, f"torch vs onnxruntime delta {delta:.2e} >= {ONNX_ATOL:.0e}"
    return delta


# --- Data (C3) -----------------------------------------------------------------------------------------------------------------------


def load_nodes(args, wanted: Sequence[str]) -> Tuple[List[NodeSeries], datetime.date, Dict[str, np.ndarray], Dict[str, np.ndarray]]:
    """The MAIN block of every wanted symbol (rows after the out-of-sample cutoff are removed before anything else is built), node order = ascending names."""
    try:
        symbols, dates = ds.load_parquet_dir(args.data_dir, return_dates=True, symbols=list(wanted), start=args.data_start, end=args.data_end)
    except (FileNotFoundError, ValueError) as exc:
        raise PrerequisiteError(str(exc)) from exc
    by_lower = {name.lower(): name for name in symbols}
    missing = [s for s in wanted if s.lower() not in by_lower]
    if missing:
        raise PrerequisiteError(f"no usable parquet data for: {', '.join(missing)}")
    common_end = ds.common_end_date({name: dates[name] for name in symbols})
    nodes: List[NodeSeries] = []
    for name in sorted({by_lower[s.lower()] for s in wanted}):
        main_ohlcv, main_dates, _, _ = ds.oos_split(symbols[name], dates[name], args.oos_tail_days, common_end=common_end)
        nodes.append(NodeSeries(name, np.asarray(main_ohlcv, dtype=np.float64), np.asarray(main_dates).astype("datetime64[D]")))
    return nodes, common_end, symbols, dates


def _tunables(args) -> dict:
    return dict(corr_window=args.corr_window, edge_threshold=args.edge_threshold, min_nodes=args.min_nodes, hidden=args.hidden,
                encoder_layers=args.encoder_layers, epochs=args.epochs, batch=args.batch, lr=args.lr, weight_decay=args.weight_decay, dropout=args.dropout)


def _thresholds_basis(args) -> str:
    defaults = dict(corr_window=DEFAULT_CORR_WINDOW, edge_threshold=DEFAULT_EDGE_THRESHOLD, min_nodes=DEFAULT_MIN_NODES, hidden=DEFAULT_HIDDEN,
                    encoder_layers=DEFAULT_ENCODER_LAYERS, epochs=DEFAULT_EPOCHS, batch=DEFAULT_BATCH, lr=DEFAULT_LR, weight_decay=DEFAULT_WEIGHT_DECAY,
                    dropout=DEFAULT_DROPOUT)
    tunables = _tunables(args)
    return THRESHOLDS_BASIS_USER if any(tunables[k] != v for k, v in defaults.items()) else THRESHOLDS_BASIS_DEFAULT


# --- train ---------------------------------------------------------------------------------------------------------------------------


def run_train(args) -> Path:
    started = time.monotonic()
    deadline = started + args.max_seconds
    if args.timeframe != SUPPORTED_TIMEFRAME:
        raise PrerequisiteError(f"timeframe_not_supported: {args.timeframe!r}; the window and horizon are bars of the daily series")
    if len(args.symbols) > Limits.MAX_NODES:
        raise PrerequisiteError(f"{len(args.symbols)} symbols exceed the bound of {Limits.MAX_NODES}")
    spec = GraphSpec(args.feature_mode, args.window, args.horizon, args.threshold, args.corr_window, args.edge_threshold, args.min_nodes)
    if spec.min_nodes > len(args.symbols):
        raise PrerequisiteError(f"--min-nodes {spec.min_nodes} is above the number of symbols {len(args.symbols)}")
    nodes, common_end, symbols, dates = load_nodes(args, args.symbols)
    node_order = [node.symbol for node in nodes]
    builder = GraphBuilder(nodes, spec, with_labels=True)
    positions = builder.usable_positions(require_label=True)
    _check_bytes(int(positions.shape[0]) * len(nodes) * len(nodes), "the adjacency matrices")
    information_length = max(spec.window, spec.corr_window + 1)   # a sample reads this many bars back (features) or prices back (correlation)
    gap = ds.resolve_purge_gap(information_length, spec.horizon, args.gap)
    train_positions, val_positions = split_positions(positions, gap)
    torch.manual_seed(args.seed)
    model = GraphNet(spec.channels, args.hidden, args.encoder_layers, args.dropout)
    summary = train_graph(model, builder, train_positions, val_positions,
                          TrainSpec(epochs=args.epochs, batch=args.batch, lr=args.lr, weight_decay=args.weight_decay, seed=args.seed), deadline=deadline,
                          select_best=args.select_best)
    validation = evaluate(model, builder, val_positions, args.batch)
    data_revision = checkpoints.source_revision({s: symbols[s] for s in symbols}, {s: dates[s] for s in symbols})
    metadata = contract_metadata(node_order, spec)
    config = dict(node_order=node_order, feature_mode=spec.feature_mode, window=spec.window, horizon=spec.horizon, threshold=spec.threshold, gap=gap,
                  seed=args.seed, oos_tail_days=args.oos_tail_days, common_end=str(common_end), data_revision=data_revision, select_best=args.select_best,
                  **_tunables(args))
    config_sha256 = _sha_of(config)
    runtime = {"torch": str(torch.__version__), "numpy": np.__version__, "device": "cpu"}

    def write(folder: Path) -> None:
        onnx_path = folder / GRAPH_ONNX_NAME
        export_graph_onnx(model, len(nodes), spec, onnx_path, metadata)
        delta = verify_graph_onnx(onnx_path, model, builder, len(nodes), spec)
        document = dict(
            schema_version=SCHEMA_VERSION, tool=TOOL, tool_version=TOOL_VERSION, created_utc=_utc_now().isoformat(),
            elapsed_seconds=round(time.monotonic() - started, ELAPSED_DIGITS), status="available", kind=KIND,
            config=config, config_sha256=config_sha256, thresholds_basis=_thresholds_basis(args), rng_policy=RNG_POLICY, runtime=runtime,
            contract=metadata, node_order=node_order,
            model=dict(file=GRAPH_ONNX_NAME, sha256=checkpoints.hash_file(onnx_path), parameter_count=int(sum(p.numel() for p in model.parameters()))),
            data=dict(data_revision=data_revision, oos_tail_days=args.oos_tail_days, oos_consumed=False, common_end=str(common_end),
                      timeframe=args.timeframe, symbols=node_order),
            samples=dict(sample_dates=int(positions.shape[0]), train_dates=int(train_positions.shape[0]), validation_dates=int(val_positions.shape[0]),
                         gap=gap, gap_basis=f"dataset.resolve_purge_gap(max(window, corr_window + 1) = {information_length}, horizon)" if args.gap is None
                         else "user_supplied"),
            training=summary, validation=validation,
            notices=list(NOTICES) + ([NOTICE_VALIDATION_USED_FOR_SELECTION] if summary["validation_used_for_selection"] else []),
            verification=dict(onnx_torch_max_delta=delta, real_world_validation="unverified"))
        _write_json(folder / GRAPH_DOC_NAME, document)

    final = publish_run(args.out_root, KIND, config_sha256, write)
    last = summary["curve"][-1]
    print(f"wrote {final} (nodes={len(nodes)} dates train={train_positions.shape[0]} validation={val_positions.shape[0]} "
          f"epochs={summary['epochs']} selection={summary['selection_metric']} best_epoch={summary['best_epoch']} "
          f"train_loss={last['train_loss']:.4f} validation_loss={last['validation_loss']})")
    return final


# --- CLI -----------------------------------------------------------------------------------------------------------------------------


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="T25 graph neural network over a fixed symbol set (correlation graph per date).")
    sub = parser.add_subparsers(dest="command", required=True)
    train = sub.add_parser("train", help="Train and publish a graph model (graph.onnx + graph.json).")
    train.add_argument("--symbols", type=_csv, required=True, help="The node set (comma separated); the node order is the ascending sort of the names.")
    train.add_argument("--data-dir", type=Path, default=ds.DEFAULT_DATA_DIR)
    train.add_argument("--data-start", default=None, help="Inclusive first calendar date (YYYY-MM-DD).")
    train.add_argument("--data-end", default=None, help="Inclusive last calendar date (YYYY-MM-DD).")
    train.add_argument("--timeframe", choices=tuple(ds.TIMEFRAME_DIRS), default=SUPPORTED_TIMEFRAME)
    train.add_argument("--out-root", type=Path, default=ds.TRAINING_ARTIFACTS_ROOT)
    train.add_argument("--max-seconds", type=_positive_int, default=MAX_SECONDS, help=f"Run-duration bound (default {MAX_SECONDS}, env {MAX_SECONDS_ENV}).")
    train.add_argument("--oos-tail-days", type=_positive_int, required=True, help="Calendar days of the out-of-sample tail that is never read (>= 1).")
    train.add_argument("--feature-mode", choices=ds.FEATURE_MODES, default=DEFAULT_FEATURE_MODE)
    train.add_argument("--window", type=_positive_int, default=ds.DEFAULT_WINDOW)
    train.add_argument("--horizon", type=_positive_int, default=ds.DEFAULT_HORIZON)
    train.add_argument("--threshold", type=_finite_float, default=ds.DEFAULT_THRESHOLD)
    train.add_argument("--gap", type=int, default=None, help="Purge gap in calendar dates (default: dataset.resolve_purge_gap over the longest lookback).")
    train.add_argument("--corr-window", type=_positive_int, default=DEFAULT_CORR_WINDOW, help="Calendar dates of the correlation window.")
    train.add_argument("--edge-threshold", type=_finite_float, default=DEFAULT_EDGE_THRESHOLD, help="An edge exists when |rho| >= this value.")
    train.add_argument("--min-nodes", type=_positive_int, default=DEFAULT_MIN_NODES, help="Valid nodes a date needs to be a sample.")
    train.add_argument("--hidden", type=_positive_int, default=DEFAULT_HIDDEN)
    train.add_argument("--encoder-layers", type=_positive_int, default=DEFAULT_ENCODER_LAYERS)
    train.add_argument("--dropout", type=_finite_float, default=DEFAULT_DROPOUT)
    train.add_argument("--epochs", type=_positive_int, default=DEFAULT_EPOCHS)
    train.add_argument("--batch", type=_positive_int, default=DEFAULT_BATCH, help="Dates per minibatch.")
    train.add_argument("--lr", type=_finite_float, default=DEFAULT_LR)
    train.add_argument("--weight-decay", type=_finite_float, default=DEFAULT_WEIGHT_DECAY)
    train.add_argument("--seed", type=int, default=DEFAULT_SEED)
    train.add_argument("--select-best", choices=SELECT_CHOICES, default=SELECT_VAL_LOSS,
                       help="Which epoch's weights to keep: the epoch with the smallest validation loss (default; the validation metrics are then optimistic) or the last epoch.")
    return parser


def parse_args(argv: Sequence[str]) -> argparse.Namespace:
    parser = build_parser()
    args = parser.parse_args(list(argv))
    if args.max_seconds > 86_400:
        parser.error("--max-seconds is above the hard bound of 86400")
    if not args.symbols:
        parser.error("--symbols must name at least one symbol")
    if len({s.lower() for s in args.symbols}) != len(args.symbols):
        parser.error("a symbol is listed twice")
    if args.corr_window < MIN_CORR_WINDOW:
        parser.error(f"--corr-window must be at least {MIN_CORR_WINDOW}")
    if not 0.0 < args.edge_threshold <= 1.0:
        parser.error("--edge-threshold must be in (0, 1]")
    if not 0.0 <= args.dropout < 1.0:
        parser.error("--dropout must be in [0, 1)")
    if args.lr <= 0 or args.weight_decay < 0:
        parser.error("--lr must be positive and --weight-decay non-negative")
    if args.threshold < 0:
        parser.error("--threshold must be non-negative")
    if args.gap is not None and args.gap < 0:
        parser.error("--gap must be non-negative")
    for name, value, limit in (("--symbols", len(args.symbols), Limits.MAX_NODES), ("--window", args.window, Limits.MAX_WINDOW),
                               ("--corr-window", args.corr_window, Limits.MAX_WINDOW), ("--epochs", args.epochs, Limits.MAX_EPOCHS),
                               ("--batch", args.batch, Limits.MAX_BATCH), ("--hidden", args.hidden, Limits.MAX_HIDDEN)):
        if value > limit:
            parser.error(f"{name} {value} is above the safety bound of {limit}")
    return args


def main(argv: Sequence[str]) -> int:
    args = parse_args(argv)
    try:
        run_train(args)
        return EXIT_OK
    except PrerequisiteError as exc:
        print(f"prerequisite: {exc}", file=sys.stderr)
        return EXIT_PREREQUISITE
    except (TimeoutError, ArithmeticError) as exc:
        print(f"failed: {exc}", file=sys.stderr)
        return EXIT_FAILED


# --- self-verification ---------------------------------------------------------------------------------------------------------------


def _synthetic_nodes(count: int, rows: int, seed: int, *, start: str = "2022-01-03") -> List[NodeSeries]:
    """Random-walk nodes that share a common factor (so some pairs correlate) on a shared daily calendar."""
    rng = np.random.default_rng(seed)
    common = rng.normal(0.0, 0.01, rows)
    dates = (np.datetime64(start) + np.arange(rows)).astype("datetime64[D]")
    out = []
    for index in range(count):
        mix = 0.9 if index % 2 == 0 else 0.1
        step = mix * common + (1.0 - mix) * rng.normal(0.0, 0.01, rows)
        close = 100.0 * np.exp(np.cumsum(step))
        open_ = close * np.exp(rng.normal(0.0, 0.003, rows))
        high = np.maximum(open_, close) * (1.0 + np.abs(rng.normal(0.0, 0.003, rows)))
        low = np.minimum(open_, close) * (1.0 - np.abs(rng.normal(0.0, 0.003, rows)))
        volume = rng.integers(1_000, 50_000, rows).astype(np.float64)
        out.append(NodeSeries(f"N{index}", np.stack([open_, high, low, close, volume], axis=1), dates.copy()))
    return out


def _write_parquet(folder: Path, nodes: Sequence[NodeSeries]) -> None:
    import polars as pl
    folder.mkdir(parents=True, exist_ok=True)
    for node in nodes:
        frame = pl.DataFrame({"date": [d.astype(datetime.date) for d in node.dates], "open": node.ohlcv[:, 0], "high": node.ohlcv[:, 1], "low": node.ohlcv[:, 2],
                              "close": node.ohlcv[:, 3], "volume": node.ohlcv[:, 4]})
        frame.write_parquet(folder / f"{node.symbol}.parquet")


def _expect_exit(argv: Sequence[str], code: int) -> None:
    try:
        parse_args(argv)
    except SystemExit as exc:
        assert exc.code == code, f"{argv} exited {exc.code}, expected {code}"
        return
    raise AssertionError(f"{list(argv)} must be rejected")


def _tiny_spec(**override) -> GraphSpec:
    base = dict(feature_mode="ohlcv_minmax", window=6, horizon=2, threshold=0.005, corr_window=10, edge_threshold=0.5, min_nodes=2)
    base.update(override)
    return GraphSpec(**base)


def _run_selfcheck() -> None:
    torch.manual_seed(0)
    base = ["train", "--symbols", "A,B", "--oos-tail-days", "30"]
    ok = parse_args(base)
    assert ok.corr_window == DEFAULT_CORR_WINDOW and ok.edge_threshold == DEFAULT_EDGE_THRESHOLD and ok.min_nodes == DEFAULT_MIN_NODES

    # (a) argument validation: type, range, bounds, duplicate symbols
    for bad in (base + ["--corr-window", "1"], base + ["--corr-window", "0"], base + ["--corr-window", str(Limits.MAX_WINDOW + 1)],
                base + ["--window", str(Limits.MAX_WINDOW + 1)], base + ["--edge-threshold", "0"], base + ["--edge-threshold", "1.5"],
                base + ["--edge-threshold", "nan"], base + ["--dropout", "1"], base + ["--lr", "-1"], base + ["--epochs", "0"], base + ["--epochs", "2.5"],
                base + ["--epochs", str(Limits.MAX_EPOCHS + 1)], base + ["--batch", str(Limits.MAX_BATCH + 1)], base + ["--hidden", str(Limits.MAX_HIDDEN + 1)],
                base + ["--max-seconds", "86401"], base + ["--gap", "-1"], base + ["--feature-mode", "composed_features"],
                ["train", "--symbols", "A,a", "--oos-tail-days", "30"], ["train", "--symbols", "A,B"], ["train", "--symbols", "A,B", "--oos-tail-days", "0"],
                ["train", "--symbols", ",".join(f"S{i}" for i in range(Limits.MAX_NODES + 1)), "--oos-tail-days", "30"]):
        _expect_exit(bad, 2)

    # (b) calendar and returns: a node's own consecutive rows, dated by the later row; no carry-forward, no invention
    day = lambda n: np.datetime64("2022-01-03") + np.timedelta64(n, "D")  # noqa: E731
    close_a = np.array([100.0, 110.0, 121.0, 133.1])
    a = NodeSeries("A", np.tile(close_a[:, None], (1, 5)), np.array([day(0), day(1), day(2), day(3)]))
    b = NodeSeries("B", np.tile(np.array([50.0, 55.0, 0.0, 66.0])[:, None], (1, 5)), np.array([day(0), day(2), day(3), day(5)]))   # B misses day 1 and 4
    calendar = build_calendar([a, b])
    assert [int((d - day(0)).astype(int)) for d in calendar] == [0, 1, 2, 3, 5], "the calendar is the union of the node dates"
    rows = node_row_index([a, b], calendar)
    assert rows[:, 0].tolist() == [0, 1, 2, 3, -1] and rows[:, 1].tolist() == [0, -1, 1, 2, 3]
    ret = log_return_matrix([a, b], rows)
    assert np.isnan(ret[0]).all(), "the first row of a node has no return"
    assert abs(ret[1, 0] - math.log(1.1)) < 1e-12 and abs(ret[3, 0] - math.log(133.1 / 121.0)) < 1e-12
    assert np.isnan(ret[1, 1]), "B has no row on day 1: nothing is carried forward"
    assert abs(ret[2, 1] - math.log(55.0 / 50.0)) < 1e-12, "B's return on day 2 spans its OWN consecutive rows (day 0 -> day 2)"
    assert np.isnan(ret[3, 1]) and np.isnan(ret[4, 1]), "a zero close gives no return on that row or on the next"
    assert np.isnan(ret[4, 0]), "A has no row on day 5"

    # (c) correlation / adjacency: hand values, missing value, constant column, short history, threshold
    t = np.arange(12, dtype=np.float64)
    series = np.stack([np.sin(t), 2.0 * np.sin(t) + 1.0, -np.sin(t), np.cos(t * 1.7), np.full(12, 0.01)], axis=1)
    adj = adjacency_at(series, 11, 10, 0.5)
    assert adj.dtype == np.float32 and np.allclose(adj, adj.T) and np.all(np.diag(adj) == 0.0)
    assert abs(adj[0, 1] - 1.0) < 1e-6 and abs(adj[0, 2] - 1.0) < 1e-6, "|rho| of perfectly (anti)correlated columns is 1"
    assert np.all(adj[4] == 0.0) and np.all(adj[:, 4] == 0.0), "a constant column has no edge"
    expected = abs(np.corrcoef(series[2:12, 0], series[2:12, 3])[0, 1])
    assert (adj[0, 3] > 0.0) == (expected >= 0.5) and (adj[0, 3] == 0.0 or abs(adj[0, 3] - expected) < 1e-6)
    assert np.all(adjacency_at(series, 11, 10, 1.0 + 1e-9) == 0.0), "no edge reaches a threshold above 1"
    holed = series.copy()
    holed[6, 1] = np.nan
    adj_hole = adjacency_at(holed, 11, 10, 0.5)
    assert np.all(adj_hole[1] == 0.0) and np.all(adj_hole[:, 1] == 0.0), "ONE missing value in the window removes the node's edges"
    assert abs(adj_hole[0, 2] - 1.0) < 1e-6, "other pairs are untouched"
    assert np.all(adjacency_at(series, 8, 10, 0.5) == 0.0), "a window that starts before the calendar has no edge"
    assert adjacency_at(holed, 11, 4, 0.5)[0, 1] > 0.99, "a window that no longer contains the missing value has the edge again"

    # (d) no future reference: every price after t (and the labels) may change without changing the sample at t
    nodes = _synthetic_nodes(4, 140, 5)
    spec = _tiny_spec()
    builder = GraphBuilder(nodes, spec, with_labels=True)
    cut = 90
    altered = []
    rng = np.random.default_rng(1)
    for node in nodes:
        changed = node.ohlcv.copy()
        changed[cut + 1:] = changed[cut + 1:] * rng.uniform(0.5, 2.0, size=changed[cut + 1:].shape)
        altered.append(NodeSeries(node.symbol, changed, node.dates))
    builder_altered = GraphBuilder(altered, spec, with_labels=True)
    for position in (60, 75, cut):
        for left, right in zip(builder.sample(position), builder_altered.sample(position)):
            assert np.array_equal(left, right), f"the sample at {position} read data after it"
    assert not np.array_equal(builder.sample(cut + 5)[1], builder_altered.sample(cut + 5)[1]) or not np.array_equal(builder.sample(cut + 5)[0], builder_altered.sample(cut + 5)[0])

    # (e) features and labels are the single-symbol ones (dataset.build_dataset), per feature mode
    for mode in ds.FEATURE_MODES:
        mode_spec = _tiny_spec(feature_mode=mode)
        mode_builder = GraphBuilder(nodes[:2], mode_spec, with_labels=True)
        for i, node in enumerate(nodes[:2]):
            x, y, anchors = ds.build_dataset(node.ohlcv, mode, mode_spec.window, mode_spec.horizon, mode_spec.threshold, return_anchor_rows=True)
            assert anchors.shape[0] > 0
            for sample_index in (0, anchors.shape[0] // 2, anchors.shape[0] - 1):
                position = int(np.nonzero(mode_builder.rows[:, i] == anchors[sample_index])[0][0])
                assert np.array_equal(mode_builder.sample(position)[0][i], x[sample_index]), f"{mode}: feature window differs from build_dataset"
                assert mode_builder.labels[position, i] == y[sample_index], f"{mode}: label differs from build_dataset"
    last_row = nodes[0].ohlcv.shape[0] - 1
    assert builder.labels[int(np.nonzero(builder.rows[:, 0] == last_row)[0][0]), 0] == -1, "no future close -> no label"

    # (f) masks: a node without a window at t is masked, has zero features and no edge; too few valid nodes -> not a sample
    sparse = [nodes[0], nodes[1], NodeSeries("N2", nodes[2].ohlcv[:50], nodes[2].dates[:50]), NodeSeries("N3", nodes[3].ohlcv[:5], nodes[3].dates[:5])]
    sparse_builder = GraphBuilder(sparse, spec, with_labels=True)
    x, adj, mask = sparse_builder.sample(100)
    assert mask.tolist() == [1.0, 1.0, 0.0, 0.0] and not x[2:].any() and not adj[2:].any() and not adj[:, 2:].any()
    assert sparse_builder.node_valid[2, 3] is np.False_ or not sparse_builder.node_valid[2, 3], "a node needs a full window"
    only_one = GraphBuilder([nodes[0], NodeSeries("N1", nodes[1].ohlcv[:3], nodes[1].dates[:3])], spec, with_labels=True)
    assert 100 not in only_one.usable_positions(require_label=False).tolist() and 100 in GraphBuilder(nodes[:2], spec, with_labels=True).usable_positions(require_label=False)

    # (g) model: normalization by hand, masked nodes do not influence the others, rows are probabilities
    adjacency_small = torch.tensor([[[0.0, 0.5, 0.0], [0.5, 0.0, 0.25], [0.0, 0.25, 0.0]]])
    mask_small = torch.tensor([[1.0, 1.0, 1.0]])
    degree = torch.tensor([1.5, 1.75, 1.25])
    reference = torch.tensor([[[1.0, 0.5, 0.0], [0.5, 1.0, 0.25], [0.0, 0.25, 1.0]]]) / torch.sqrt(degree.unsqueeze(-1) * degree.unsqueeze(0))
    assert torch.allclose(normalized_adjacency(adjacency_small, mask_small), reference, atol=1e-7)
    masked = normalized_adjacency(adjacency_small, torch.tensor([[1.0, 1.0, 0.0]]))
    assert masked[0, 2].abs().sum() == 0 and masked[0, :, 2].abs().sum() == 0, "a masked node has no row and no column"
    assert torch.allclose(masked[0, :2, :2], torch.tensor([[1.0, 0.5], [0.5, 1.0]]) / torch.tensor([[1.5, 1.5], [1.5, 1.5]]), atol=1e-7)
    net = GraphNet(spec.channels, 4, 1, 0.0).eval()
    x0 = torch.randn(2, 3, spec.window, spec.channels)
    a0 = torch.rand(2, 3, 3)
    a0 = (a0 + a0.transpose(1, 2)) / 2
    m0 = torch.tensor([[1.0, 1.0, 0.0], [1.0, 1.0, 1.0]])
    with torch.no_grad():
        out0 = net(x0, a0, m0)
        x1 = x0.clone()
        x1[0, 2] = 99.0
        a1 = a0.clone()
        a1[0, 2, :] = 0.7
        a1[0, :, 2] = 0.7
        out1 = net(x1, a1, m0)
    assert out0.shape == (2, 3, NUM_CLASSES)
    assert torch.allclose(out0[0, :2], out1[0, :2], atol=1e-6), "a masked node must not change the valid nodes"
    assert torch.allclose(torch.softmax(out0, dim=-1).sum(-1), torch.ones(2, 3), atol=1e-6)

    # (h) loss mask: positions without a label contribute nothing; no labeled position is an error
    logits = torch.randn(1, 3, NUM_CLASSES, requires_grad=True)
    labels = torch.tensor([[1, -1, -1]])
    masked_cross_entropy(logits, labels).backward()
    assert logits.grad[0, 0].abs().sum() > 0 and logits.grad[0, 1:].abs().sum() == 0
    try:
        masked_cross_entropy(logits, torch.full((1, 3), -1))
    except ValueError:
        pass
    else:
        raise AssertionError("a batch without a label must fail")

    # (i) split: chronological, the purge gap is enforced in calendar positions, nothing overlaps
    positions = np.arange(10, 250, dtype=np.int64)
    train_p, val_p = split_positions(positions, 12)
    assert train_p.max() < val_p.min() and val_p.min() - train_p.max() > 12 and not set(train_p.tolist()) & set(val_p.tolist())
    try:
        split_positions(np.arange(5, dtype=np.int64), 3)
    except PrerequisiteError:
        pass
    else:
        raise AssertionError("too few dates must be refused")

    # (j) determinism: the same seed gives identical weights, another seed does not
    small_nodes = _synthetic_nodes(3, 120, 9)
    small_builder = GraphBuilder(small_nodes, spec, with_labels=True)
    small_positions = small_builder.usable_positions(require_label=True)
    train_s, val_s = split_positions(small_positions, 15)

    def fit(seed: int) -> Dict[str, torch.Tensor]:
        torch.manual_seed(seed)
        m = GraphNet(spec.channels, 4, 1, 0.1)
        train_graph(m, small_builder, train_s, val_s, TrainSpec(epochs=2, batch=8, lr=0.01, weight_decay=0.0, seed=seed))
        return {k: v.detach().clone() for k, v in m.state_dict().items()}

    first, second, other = fit(3), fit(3), fit(4)
    assert all(torch.equal(first[k], second[k]) for k in first), "same seed must reproduce the weights exactly"
    assert any(not torch.equal(first[k], other[k]) for k in first)
    try:
        train_graph(GraphNet(spec.channels, 4, 1, 0.0), small_builder, train_s, val_s, TrainSpec(1, 8, 0.01, 0.0, 1), deadline=time.monotonic() - 1)
    except TimeoutError:
        pass
    else:
        raise AssertionError("an exhausted time budget must stop the run")

    # (j2) best-validation epoch: the pure rule, then the selection inside train_graph (weights, record, fallback)
    assert select_best_epoch([0.5, 0.3, 0.28, 0.9]) == 3 and select_best_epoch([0.3, 0.3]) == 1
    assert select_best_epoch([None, None]) is None and select_best_epoch([None, 0.4, float("nan"), 0.4]) == 2 and select_best_epoch([]) is None
    four = TrainSpec(epochs=4, batch=8, lr=0.01, weight_decay=0.0, seed=5)

    def selected(mode: str) -> Tuple[dict, float]:
        torch.manual_seed(5)
        m = GraphNet(spec.channels, 4, 1, 0.1)
        record = train_graph(m, small_builder, train_s, val_s, four, select_best=mode)
        return record, evaluate(m, small_builder, val_s, 8)["loss"]

    best_record, best_loss_after = selected(SELECT_VAL_LOSS)
    losses = [row["validation_loss"] for row in best_record["curve"]]
    argmin = min(range(len(losses)), key=lambda i: (losses[i], i)) + 1          # 1-based, ties -> earliest
    assert best_record["best_epoch"] == argmin and best_record["final_epoch"] == 4 and len(losses) == 4
    assert best_record["validation_used_for_selection"] is True and best_record["selection_metric"] == SELECT_VAL_LOSS and "selection_fallback" not in best_record
    assert best_loss_after == losses[argmin - 1], "the loaded weights must be those of the selected epoch"
    last_record, last_loss_after = selected(SELECT_LAST)
    assert last_record["validation_used_for_selection"] is False and last_record["selection_metric"] == SELECTION_METRIC_LAST and "selection_fallback" not in last_record
    assert last_loss_after == last_record["curve"][-1]["validation_loss"], "the default keeps the weights of the last epoch"
    assert last_record["best_epoch"] == argmin and last_record["final_epoch"] == 4
    real_evaluate = evaluate
    try:
        globals()["evaluate"] = lambda *a, **k: dict(status="insufficient_data", labeled_nodes=0)
        torch.manual_seed(5)
        fallback = train_graph(GraphNet(spec.channels, 4, 1, 0.1), small_builder, train_s, val_s, four, select_best=SELECT_VAL_LOSS)
    finally:
        globals()["evaluate"] = real_evaluate
    assert fallback["validation_used_for_selection"] is False and fallback["selection_fallback"] == SELECTION_FALLBACK_NO_LOSS
    assert fallback["best_epoch"] is None and fallback["selection_metric"] == SELECTION_METRIC_LAST
    try:
        train_graph(GraphNet(spec.channels, 4, 1, 0.1), small_builder, train_s, val_s, four, select_best="best")
    except ValueError:
        pass
    else:
        raise AssertionError("an unknown selection mode must be refused")
    assert parse_args(base).select_best == SELECT_VAL_LOSS
    _expect_exit(base + ["--select-best", "best"], 2)

    # (k) ONNX: names, ranks, dynamic batch, torch vs onnxruntime, metadata is the graph namespace only
    import onnx
    with tempfile.TemporaryDirectory() as tmp:
        path = Path(tmp) / "g.onnx"
        trained = GraphNet(spec.channels, 4, 1, 0.0)
        metadata = contract_metadata([n.symbol for n in small_nodes], spec)
        export_graph_onnx(trained, len(small_nodes), spec, path, metadata)
        delta = verify_graph_onnx(path, trained, small_builder, len(small_nodes), spec)
        assert delta < ONNX_ATOL
        stored = {p.key: p.value for p in onnx.load(str(path)).metadata_props}
        assert stored == metadata and set(stored) == set(CONTRACT_KEYS) and all(k.startswith(META_PREFIX) for k in stored)
        assert json.loads(stored[KEY_NODE_ORDER]) == ["N0", "N1", "N2"] and stored[KEY_CLASS_ORDER] == "Up,Down,Neutral"
        assert not set(stored) & set(onnx_meta.CONTRACT_KEYS), "the graph model carries no single-symbol contract key"

    # (l) end to end on parquet: publish, exit codes, atomicity, out-of-sample tail, synthetic and missing data
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        data = root / "data"
        _write_parquet(data, _synthetic_nodes(4, 330, 21))
        common = ["--data-dir", str(data), "--out-root", str(root / "out"), "--oos-tail-days", "30", "--window", "6", "--horizon", "2", "--corr-window", "10",
                  "--epochs", "2", "--batch", "8", "--hidden", "4", "--encoder-layers", "1"]
        assert main(["train", "--symbols", "N0,N1,N2,N3"] + common) == EXIT_OK
        run = next((root / "out" / KIND).iterdir())
        assert not run.name.endswith(".incoming") and sorted(p.name for p in run.iterdir()) == [GRAPH_DOC_NAME, GRAPH_ONNX_NAME]
        document = json.loads((run / GRAPH_DOC_NAME).read_text(encoding="utf-8"))
        assert document["schema_version"] == SCHEMA_VERSION and document["node_order"] == ["N0", "N1", "N2", "N3"]
        assert document["model"]["sha256"] == checkpoints.hash_file(run / GRAPH_ONNX_NAME) and document["contract"] == contract_metadata(document["node_order"], _tiny_spec())
        assert document["data"]["oos_consumed"] is False and document["thresholds_basis"] == THRESHOLDS_BASIS_USER
        training = document["training"]
        assert document["config"]["select_best"] == SELECT_VAL_LOSS and training["validation_used_for_selection"] is True
        assert training["selection_metric"] == SELECT_VAL_LOSS and training["final_epoch"] == 2 and training["best_epoch"] in (1, 2)
        assert NOTICE_VALIDATION_USED_FOR_SELECTION in document["notices"] and NOTICE_VALIDATION_USED_FOR_SELECTION not in NOTICES
        assert document["verification"]["real_world_validation"] == "unverified" and document["validation"]["status"] == "available"
        end = np.datetime64(document["data"]["common_end"]) - np.timedelta64(30, "D")
        main_nodes, _, _, _ = load_nodes(parse_args(["train", "--symbols", "N0,N1", "--data-dir", str(data), "--oos-tail-days", "30"]), ["N0", "N1"])
        assert all(node.dates.max() <= end for node in main_nodes), "rows after the out-of-sample cutoff must never be loaded"
        before = sorted(p.name for p in (root / "out" / KIND).iterdir())
        assert main(["train", "--symbols", "N0,N1,N2,N3", "--select-best", SELECT_LAST] + common) == EXIT_OK         # another selection -> another run id
        last_run = next(r for r in (root / "out" / KIND).iterdir() if r.name not in before)
        last_document = json.loads((last_run / GRAPH_DOC_NAME).read_text(encoding="utf-8"))
        assert last_document["training"]["validation_used_for_selection"] is False and NOTICE_VALIDATION_USED_FOR_SELECTION not in last_document["notices"]
        before = sorted(p.name for p in (root / "out" / KIND).iterdir())
        assert main(["train", "--symbols", "N0,N1,N2,N3", "--seed", "7"] + common) == EXIT_OK            # another seed -> another run id, nothing overwritten
        assert len(sorted(p.name for p in (root / "out" / KIND).iterdir())) == len(before) + 1
        assert main(["train", "--symbols", "N0,Missing"] + common) == EXIT_PREREQUISITE
        assert main(["train", "--symbols", "N0,N1", "--timeframe", "weekly"] + common) == EXIT_PREREQUISITE
        assert main(["train", "--symbols", "N0,N1", "--min-nodes", "3"] + common) == EXIT_PREREQUISITE
        assert main(["train", "--symbols", "N0"] + common) == EXIT_PREREQUISITE, "one node cannot reach the default min_nodes of 2"
        published = sorted(p.name for p in (root / "out" / KIND).iterdir())
        # synthetic data is refused (exit 3) and nothing is published
        synthetic = root / "synthetic"
        _write_parquet(synthetic, _synthetic_nodes(2, 220, 3))
        (synthetic / ds.SYNTHETIC_MANIFEST).write_text(json.dumps({"data_origin": ds.SYNTHETIC_ORIGIN}), encoding="utf-8")
        refused = [a for a in common]
        refused[refused.index("--data-dir") + 1] = str(synthetic)
        assert main(["train", "--symbols", "N0,N1"] + refused) == EXIT_PREREQUISITE
        # an exhausted run-duration bound fails with exit 1 and leaves no half-written run
        slow = [a for a in common]
        slow[slow.index("--epochs") + 1] = str(Limits.MAX_EPOCHS)
        assert main(["train", "--symbols", "N0,N1", "--max-seconds", "1"] + slow) == EXIT_FAILED
        assert not any(p.name.endswith(".incoming") for p in (root / "out" / KIND).iterdir()), "no half-written run may remain"
        assert set(published) <= {p.name for p in (root / "out" / KIND).iterdir()}, "an earlier run is never removed or replaced"
    print("train_gnn.py selfcheck: OK")


if __name__ == "__main__":
    if "--selfcheck" in sys.argv[1:]:
        _run_selfcheck()
    else:
        raise SystemExit(main(sys.argv[1:]))
