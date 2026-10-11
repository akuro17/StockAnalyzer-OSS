"""Regenerate the graph-model fixtures the C# consumer is verified against.

* ``Tests/StockAnalyzer.Core.Tests/Assets/graph_fixture/{graph.onnx,graph.json}``: a small published run produced by the real ``train_gnn.py`` (3 nodes AAA/BBB/CCC).
* ``Tests/GraphParity.json``: the contract constants and, for a set of candle histories, the inputs the Python reference builds (``train_gnn.inference_sample``)
  and the model output (onnxruntime on the fixture model). ``GraphContract`` / ``GraphInputBuilder`` / ``GraphPredictionService`` must reproduce them.

Prices are rounded to 4 decimals so a decimal in C# and a float64 here are the same number. No edge weight may lie within ``BOUNDARY_MARGIN`` of the threshold
(the threshold comparison is the one place two float implementations could legitimately differ); the generator fails instead of writing such a case.

Run with the training interpreter:
    StockAnalyzer.Python/.venv/Scripts/python.exe Tests/StockAnalyzer.Core.Tests/Assets/generate_graph_fixture.py
"""

from __future__ import annotations

import json
import shutil
import sys
import tempfile
from pathlib import Path

import numpy as np
import torch

HERE = Path(__file__).resolve().parent
TRAINING = HERE.parents[2] / "StockAnalyzer.Python" / "training"
sys.path.insert(0, str(TRAINING))
import checkpoints  # noqa: E402
import dataset as ds  # noqa: E402
import train_gnn as gnn  # noqa: E402

NODE_ORDER = ["AAA", "BBB", "CCC"]
WINDOW, HORIZON, CORR_WINDOW, EDGE_THRESHOLD, MIN_NODES = 6, 2, 10, 0.5, 2
MODE = "ohlcv_minmax"
BASE_ROWS = 40
BOUNDARY_MARGIN = 1e-3
PRICE_DECIMALS = 4
FIXTURE_DIR = HERE / "graph_fixture"
PARITY_PATH = HERE.parents[1] / "GraphParity.json"
SEEDS = range(1, 200)
FIXTURE_MODEL_SEED = 5
WEIGHT_STD, BIAS_STD = 1.0, 0.5


def is_weekday(day: np.datetime64) -> bool:
    return day.astype("datetime64[D]").astype(object).weekday() < 5


def weekdays(count: int, start: str = "2022-01-03") -> np.ndarray:
    out, day = [], np.datetime64(start)
    while len(out) < count:
        if is_weekday(day):
            out.append(day)
        day = day + np.timedelta64(1, "D")
    return np.array(out, dtype="datetime64[D]")


def rounded(node: gnn.NodeSeries) -> gnn.NodeSeries:
    data = node.ohlcv.copy()
    data[:, :4] = np.round(data[:, :4], PRICE_DECIMALS)
    data[:, 4] = np.round(data[:, 4])
    return gnn.NodeSeries(node.symbol, data, node.dates)


COMMON_MIX = (0.8, 0.7, 0.75)       # share of the common factor per node: every pair of the base histories correlates strongly


def factor_nodes(rows: int, seed: int, mix: tuple, dates: np.ndarray) -> dict:
    """Random-walk OHLCV per node whose returns share one factor with weight ``mix[i]`` (0 = an independent node)."""
    rng = np.random.default_rng(seed)
    common = rng.normal(0.0, 0.01, rows)
    out = {}
    for name, weight in zip(NODE_ORDER, mix):
        step = weight * common + (1.0 - weight) * rng.normal(0.0, 0.01, rows)
        close = 100.0 * np.exp(np.cumsum(step))
        open_ = close * np.exp(rng.normal(0.0, 0.003, rows))
        high = np.maximum(open_, close) * (1.0 + np.abs(rng.normal(0.0, 0.003, rows)))
        low = np.minimum(open_, close) * (1.0 - np.abs(rng.normal(0.0, 0.003, rows)))
        volume = rng.integers(1_000, 50_000, rows).astype(np.float64)
        out[name] = rounded(gnn.NodeSeries(name, np.stack([open_, high, low, close, volume], axis=1), dates))
    return out


def base_histories(seed: int) -> dict:
    return factor_nodes(BASE_ROWS, seed, COMMON_MIX, weekdays(BASE_ROWS))


def rows_of(node: gnn.NodeSeries) -> list:
    return [[str(d), float(r[0]), float(r[1]), float(r[2]), float(r[3]), int(r[4])] for d, r in zip(node.dates, node.ohlcv)]


def without_row(node: gnn.NodeSeries, index: int) -> gnn.NodeSeries:
    keep = np.arange(node.dates.shape[0]) != index
    return gnn.NodeSeries(node.symbol, node.ohlcv[keep], node.dates[keep])


def make_cases(base: dict, seed: int) -> list:
    last = str(base["AAA"].dates[-1])
    mid = str(base["AAA"].dates[25])
    cases = []

    def add(name, histories, as_of, note):
        cases.append(dict(name=name, note=note, histories=histories, as_of=as_of))

    add("full_as_of_last", dict(base), last, "all nodes, newest date")
    add("as_of_mid_ignores_later_bars", dict(base), mid, "bars after the as-of date exist and must not matter")
    add("node_absent", {k: v for k, v in base.items() if k != "CCC"}, last, "CCC is not supplied: masked")
    add("node_short_history", {**base, "CCC": gnn.NodeSeries("CCC", base["CCC"].ohlcv[:4], base["CCC"].dates[:4])}, last, "CCC has fewer rows than the window")
    add("node_missing_date_in_window", {**base, "BBB": without_row(base["BBB"], BASE_ROWS - 4)}, last,
        "BBB lacks one date inside the correlation window: no edge of BBB, nothing carried forward")
    zero = base["AAA"].ohlcv.copy()
    zero[BASE_ROWS - 5, 3] = 0.0
    add("node_zero_close_in_window", {**base, "AAA": gnn.NodeSeries("AAA", zero, base["AAA"].dates)}, last, "a zero close removes two returns of AAA")
    flat = base["CCC"].ohlcv.copy()
    flat[BASE_ROWS - 12:] = flat[BASE_ROWS - 12]
    add("node_constant_window", {**base, "CCC": gnn.NodeSeries("CCC", flat, base["CCC"].dates)}, last, "CCC is constant over the window: zero variance, no edge")
    add("single_valid_node", {"AAA": base["AAA"]}, last, "one valid node is below min_nodes")
    add("short_calendar", {k: gnn.NodeSeries(k, v.ohlcv[:8], v.dates[:8]) for k, v in base.items()}, str(base["AAA"].dates[7]),
        "fewer calendar dates than corr_window: no edge, features still valid")
    add("node_without_row_on_as_of", {**base, "BBB": gnn.NodeSeries("BBB", base["BBB"].ohlcv[:-1], base["BBB"].dates[:-1])}, last,
        "BBB has history but no row on the as-of date: masked")
    saturday = str(base["AAA"].dates[BASE_ROWS - 1] + np.timedelta64(1, "D"))
    while is_weekday(np.datetime64(saturday)):
        saturday = str(np.datetime64(saturday) + np.timedelta64(1, "D"))
    add("as_of_not_in_data", dict(base), saturday, "no node has a candle on the as-of date")
    independent = factor_nodes(BASE_ROWS, seed + 1000, (0.8, 0.0, 0.75), base["AAA"].dates)
    add("node_independent_below_threshold", {**base, "BBB": independent["BBB"]}, last, "BBB is independent: its |rho| stays below the edge threshold")
    return cases


def spec_for(mode: str) -> gnn.GraphSpec:
    return gnn.GraphSpec(mode, WINDOW, HORIZON, 0.005, CORR_WINDOW, EDGE_THRESHOLD, MIN_NODES)


def reference(case: dict, spec: gnn.GraphSpec, session) -> dict:
    """Python reference of one case: status, mask, adjacency, features and (with a model) the output of the valid nodes."""
    histories = case["histories"]
    as_of = np.datetime64(case["as_of"])
    have_date = any(v.dates.shape[0] and (v.dates == as_of).any() for v in histories.values())
    if not have_date:
        return dict(status="unavailable", reason="as_of_not_in_data")
    x, adjacency, mask = gnn.inference_sample(histories, NODE_ORDER, spec, as_of)
    if int(mask.sum()) < spec.min_nodes:
        return dict(status="unavailable", reason="insufficient_nodes", mask=mask.tolist())
    out = dict(status="available", mask=mask.tolist(), adjacency=adjacency.reshape(-1).tolist(), node_features=x.reshape(-1).tolist())
    if session is not None:
        (probs,) = session.run([gnn.OUTPUT_NAME], {gnn.INPUT_NODE_FEATURES: x[None], gnn.INPUT_ADJACENCY: adjacency[None], gnn.INPUT_NODE_MASK: mask[None]})
        out["output"] = probs[0].reshape(-1).tolist()
    return out


def boundary_clear(case: dict, spec: gnn.GraphSpec) -> bool:
    """True when no pair weight of the case lies within the margin of the edge threshold."""
    as_of = np.datetime64(case["as_of"])
    nodes = []
    for name in NODE_ORDER:
        source = case["histories"].get(name)
        if source is None or source.dates.shape[0] == 0:
            nodes.append(gnn.NodeSeries(name, np.empty((0, 5)), np.empty(0, dtype="datetime64[D]")))
        else:
            keep = source.dates <= as_of
            nodes.append(gnn.NodeSeries(name, source.ohlcv[keep], source.dates[keep]))
    builder = gnn.GraphBuilder(nodes, spec, with_labels=False)
    position = int(np.searchsorted(builder.calendar, as_of))
    if position >= builder.calendar.shape[0] or builder.calendar[position] != as_of:
        return True
    weights = gnn.adjacency_at(builder.returns, position, spec.corr_window, 0.0)   # a threshold of 0 keeps every |rho|
    return not bool(np.any(np.abs(weights.astype(np.float64) - spec.edge_threshold) < BOUNDARY_MARGIN))


def choose_seed() -> tuple:
    spec = spec_for(MODE)
    for seed in SEEDS:
        base = base_histories(seed)
        cases = make_cases(base, seed)
        if all(boundary_clear(c, spec) for c in cases):
            counts = {c["name"]: int(np.count_nonzero(gnn.inference_sample(c["histories"], NODE_ORDER, spec, np.datetime64(c["as_of"]))[1]))
                      for c in cases if c["name"] not in ("as_of_not_in_data", "single_valid_node")}
            # every node-specific case must show its effect: the full case has all three edges, and each removal leaves exactly the edge(s) it should
            expected = {"full_as_of_last": 6, "as_of_mid_ignores_later_bars": 6, "node_missing_date_in_window": 2, "node_zero_close_in_window": 2,
                        "node_constant_window": 2, "node_without_row_on_as_of": 2, "node_independent_below_threshold": 2, "node_absent": 2,
                        "node_short_history": 2, "short_calendar": 0}
            if all(counts[name] == value for name, value in expected.items()):
                return seed, base, cases
    raise SystemExit("no seed gives boundary-clear cases with at least one edge")


def train_fixture_model(out_dir: Path) -> None:
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        nodes = [gnn.NodeSeries(name, node.ohlcv, node.dates) for name, node in zip(NODE_ORDER, gnn._synthetic_nodes(3, 330, 21))]
        gnn._write_parquet(root / "data", nodes)
        argv = ["train", "--symbols", ",".join(NODE_ORDER), "--data-dir", str(root / "data"), "--out-root", str(root / "out"), "--oos-tail-days", "30",
                "--window", str(WINDOW), "--horizon", str(HORIZON), "--corr-window", str(CORR_WINDOW), "--edge-threshold", str(EDGE_THRESHOLD),
                "--min-nodes", str(MIN_NODES), "--epochs", "8", "--batch", "8", "--hidden", "4", "--encoder-layers", "1", "--lr", "0.01", "--seed", "11"]
        assert gnn.main(argv) == gnn.EXIT_OK
        published = next((root / "out" / gnn.KIND).iterdir())
        if out_dir.exists():
            shutil.rmtree(out_dir)
        shutil.copytree(published, out_dir)
    # A model trained on random walks collapses to a constant (the inputs do not matter), which would make the parity check blind. The fixture therefore keeps the
    # published run format but carries a seeded random model with N(0, 1) layer weights, so every node, edge and mask changes the output.
    spec = spec_for(MODE)
    torch.manual_seed(FIXTURE_MODEL_SEED)
    model = gnn.GraphNet(spec.channels, 4, 1, 0.0)
    generator = torch.Generator().manual_seed(FIXTURE_MODEL_SEED + 4)
    with torch.no_grad():
        for layer in (model.gcn1, model.gcn2, model.head):   # the default init leaves every ReLU dead, so all nodes would share one output
            layer.weight.copy_(torch.randn(layer.weight.shape, generator=generator) * WEIGHT_STD)
            layer.bias.copy_(torch.randn(layer.bias.shape, generator=generator) * BIAS_STD)
    onnx_path = out_dir / gnn.GRAPH_ONNX_NAME
    gnn.export_graph_onnx(model, len(NODE_ORDER), spec, onnx_path, gnn.contract_metadata(NODE_ORDER, spec))
    gnn.verify_graph_onnx(onnx_path, model, None, len(NODE_ORDER), spec)
    document_path = out_dir / gnn.GRAPH_DOC_NAME
    document = json.loads(document_path.read_text(encoding="utf-8"))
    document["model"]["sha256"] = checkpoints.hash_file(onnx_path)
    document["fixture_note"] = "test fixture: the published run format with a seeded random model (not a trained one)"
    gnn._write_json(document_path, document)


def main() -> None:
    import onnxruntime as ort

    train_fixture_model(FIXTURE_DIR)
    session = ort.InferenceSession(str(FIXTURE_DIR / gnn.GRAPH_ONNX_NAME), providers=["CPUExecutionProvider"])
    seed, base, cases = choose_seed()
    spec = spec_for(MODE)
    out_cases = []
    for case in cases:
        out_cases.append(dict(name=case["name"], note=case["note"], as_of=case["as_of"],
                              histories={k: rows_of(v) for k, v in case["histories"].items()}, expected=reference(case, spec, session)))
    outputs = {c["name"]: np.array(c["expected"]["output"]).reshape(3, 3) for c in out_cases if "output" in c["expected"]}
    # A graph in which every pair is connected averages the nodes (that is what a GCN does); a node without an edge keeps its own signal.
    isolated = outputs["node_zero_close_in_window"]          # AAA has no edge here
    assert np.abs(isolated[0] - isolated[1]).max() > 1e-2, "an isolated node must differ from a connected one"
    for name in ("node_missing_date_in_window", "node_zero_close_in_window", "node_constant_window", "node_without_row_on_as_of", "node_independent_below_threshold"):
        assert np.abs(outputs[name] - outputs["full_as_of_last"]).max() > 1e-3, f"{name}: the output must react to the changed graph"
    full = cases[0]
    mode_cases = []
    for mode in ds.FEATURE_MODES:
        mode_spec = spec_for(mode)
        ref = reference(full, mode_spec, None)
        mode_cases.append(dict(feature_mode=mode, channels=mode_spec.channels, as_of=full["as_of"], mask=ref["mask"], adjacency=ref["adjacency"],
                               node_features=ref["node_features"]))
    keys = dict(contract_version=gnn.KEY_CONTRACT_VERSION, node_order=gnn.KEY_NODE_ORDER, window=gnn.KEY_WINDOW, horizon=gnn.KEY_HORIZON,
                corr_window=gnn.KEY_CORR_WINDOW, edge_threshold=gnn.KEY_EDGE_THRESHOLD, min_nodes=gnn.KEY_MIN_NODES, feature_mode=gnn.KEY_FEATURE_MODE,
                channels=gnn.KEY_CHANNELS, class_order=gnn.KEY_CLASS_ORDER, timeframe=gnn.KEY_TIMEFRAME)
    parity = dict(
        contract=dict(contract_version=gnn.CONTRACT_VERSION, metadata_prefix=gnn.META_PREFIX, keys=keys, input_names=[gnn.INPUT_NODE_FEATURES, gnn.INPUT_ADJACENCY, gnn.INPUT_NODE_MASK],
                      output_name=gnn.OUTPUT_NAME, class_count=gnn.NUM_CLASSES, class_order=",".join(ds.CLASS_LABELS), timeframe=gnn.SUPPORTED_TIMEFRAME,
                      min_corr_window=gnn.MIN_CORR_WINDOW, kind=gnn.KIND, model_file=gnn.GRAPH_ONNX_NAME, document_file=gnn.GRAPH_DOC_NAME,
                      document_schema_version=gnn.SCHEMA_VERSION,
                      channels={mode: ds.feature_channels(mode) for mode in ds.FEATURE_MODES},
                      limits=dict(max_nodes=gnn.Limits.MAX_NODES, max_window=gnn.Limits.MAX_WINDOW, max_tensor_bytes=gnn.Limits.MAX_TENSOR_BYTES)),
        node_order=NODE_ORDER,
        spec=dict(feature_mode=MODE, window=WINDOW, horizon=HORIZON, corr_window=CORR_WINDOW, edge_threshold=EDGE_THRESHOLD, min_nodes=MIN_NODES),
        history_seed=seed, boundary_margin=BOUNDARY_MARGIN, cases=out_cases, mode_cases=mode_cases)
    PARITY_PATH.write_bytes((json.dumps(parity, indent=1, allow_nan=False) + chr(10)).encode("utf-8"))
    print("wrote", PARITY_PATH.name, len(out_cases), "cases", len(mode_cases), "mode cases, seed", seed, "; fixture", FIXTURE_DIR.name)


if __name__ == "__main__":
    main()
