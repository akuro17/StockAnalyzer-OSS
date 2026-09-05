"""Model-contract metadata embedded into every exported trend-predictor ONNX.

Shape validation alone cannot tell a ``zscore``-trained model from an
``ohlcv_minmax`` one (both are ``[batch, window, 5]``). This module writes the
training-time contract into the model's ``metadata_props`` so the C# loader
(``StockAnalyzer.Core.Models.PredictionModelMetadata`` /
``PredictionService.EnsureModelLoaded``) can reject a model whose
``feature_mode`` / ``window_size`` / ``class_order`` does not match the running
configuration (WebAI review #16).

The ``feature_mode`` wire strings are exactly :data:`dataset.FEATURE_MODES`
(``ohlcv_minmax`` / ``log_return`` / ``zscore`` / ``zscore_joint`` /
``log_return_ohlc``) -- the single source of truth is shared with C# through
``docs/ja/API_SPECIFICATION.md``.

Run ``python onnx_meta.py --selfcheck`` to verify the key set and the
proto round-trip.
"""

from __future__ import annotations

import argparse
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Dict, Mapping, Sequence

sys.path.insert(0, str(Path(__file__).resolve().parent))
import dataset as ds  # noqa: E402  (sibling module, path inserted above)

# bump when the key set or the meaning of a value changes; the C# side logs it.
MODEL_CONTRACT_VERSION: str = "1"
# the dataset pipeline (dataset.load_parquet_dir) assumes split- and dividend-adjusted
# OHLCV; callers override only when they knowingly train on differently-adjusted data.
DEFAULT_PRICE_ADJUSTMENT: str = "adjusted"
# default learning objective: fixed key/value model outputs are class probabilities in
# this order. `regression` models instead emit one continuous forward-return value and
# carry no meaningful class order. Mirror of C# StockAnalyzer.Core.Models.Training.TargetType.
TARGET_TYPE: str = "classification"
TARGET_TYPES: tuple = ("classification", "regression")

# Every exported model MUST carry exactly these keys. C# validates a subset
# (feature_mode / window_size / class_order); the rest are informational.
CONTRACT_KEYS: tuple = (
    "feature_mode",
    "window_size",
    "channels",
    "channel_order",
    "class_order",
    "target_type",
    "prediction_horizon",
    "neutral_threshold",
    "normalization",
    "price_adjustment",
    "model_contract_version",
    "producer",
    "created_utc",
    "training_start",
    "training_end",
    "validation_start",
    "validation_end",
)

# SSoT: dataset.DATE_RANGE_KEYS. dataset.train_val_date_ranges fills these from the
# dataset's own `date` column; build_contract stringifies whatever it is handed.
_DATE_RANGE_KEYS: tuple = tuple(ds.DATE_RANGE_KEYS)

# per feature-mode channel names, for the informational metadata_props.channel_order key.
_CHANNEL_ORDER: Dict[str, str] = {
    "ohlcv_minmax": "open,high,low,close,volume",
    "zscore": "open,high,low,close,volume",
    "zscore_joint": "open,high,low,close,volume",
    "log_return": "log_return",
    "log_return_ohlc": "gap,high_open,low_open,close_open",
}


def build_contract(
    *,
    feature_mode: str,
    window_size: int,
    channels: int,
    horizon: int,
    threshold: float,
    wf_splits: int,
    seed: int,
    producer: str,
    price_adjustment: str = DEFAULT_PRICE_ADJUSTMENT,
    target_type: str = TARGET_TYPE,
    date_ranges: Mapping[str, str] | None = None,
    class_labels: Sequence[str] = ds.CLASS_LABELS,
) -> Dict[str, str]:
    """Assemble the ``metadata_props`` mapping (every value stringified).

    ``date_ranges`` may supply any of :data:`_DATE_RANGE_KEYS`; missing entries
    default to an empty string so the key set is always complete. ``wf_splits``
    and ``seed`` are folded into ``producer`` for provenance without adding
    non-contract keys. ``target_type`` selects the learning objective
    (:data:`TARGET_TYPES`); a ``regression`` model keeps ``class_order`` present
    for a stable key set but the C# loader ignores it.
    """
    if feature_mode not in ds.FEATURE_MODES:
        raise ValueError(f"unknown feature_mode {feature_mode!r}; expected one of {ds.FEATURE_MODES}")
    if target_type not in TARGET_TYPES:
        raise ValueError(f"unknown target_type {target_type!r}; expected one of {TARGET_TYPES}")
    expected_channels = ds.feature_channels(feature_mode)
    if int(channels) != expected_channels:
        raise ValueError(
            f"channels={channels} disagrees with feature_mode {feature_mode!r} "
            f"({expected_channels} channels per bar)"
        )

    ranges = dict(date_ranges or {})
    channel_order = _CHANNEL_ORDER[feature_mode]

    contract: Dict[str, str] = {
        "feature_mode": feature_mode,
        "window_size": str(int(window_size)),
        "channels": str(int(channels)),
        "channel_order": channel_order,
        "class_order": ",".join(class_labels),
        "target_type": target_type,
        "prediction_horizon": str(int(horizon)),
        "neutral_threshold": repr(float(threshold)),
        "normalization": feature_mode,
        "price_adjustment": price_adjustment,
        "model_contract_version": MODEL_CONTRACT_VERSION,
        "producer": f"{producer} (wf_splits={int(wf_splits)}, seed={int(seed)})",
        "created_utc": datetime.now(timezone.utc).isoformat(),
    }
    for key in _DATE_RANGE_KEYS:
        contract[key] = str(ranges.get(key, ""))

    missing = set(CONTRACT_KEYS) - set(contract)
    extra = set(contract) - set(CONTRACT_KEYS)
    if missing or extra:
        raise AssertionError(f"contract key mismatch: missing={missing} extra={extra}")
    return contract


def apply(model_proto, mapping: Mapping[str, str]) -> None:
    """Replace *model_proto*'s ``metadata_props`` with *mapping* (sorted keys)."""
    del model_proto.metadata_props[:]
    for key in sorted(mapping):
        entry = model_proto.metadata_props.add()
        entry.key = key
        entry.value = mapping[key]


def load_apply_save(path: Path | str, mapping: Mapping[str, str]) -> None:
    """Load the ONNX at *path*, replace its ``metadata_props``, save in place."""
    import onnx

    model = onnx.load(str(path))
    apply(model, mapping)
    onnx.save(model, str(path))


# --- self-verification -----------------------------------------------------


def _run_selfcheck() -> None:
    import onnx
    from onnx import TensorProto, helper

    c5 = build_contract(
        feature_mode="ohlcv_minmax", window_size=75, channels=5,
        horizon=5, threshold=0.005, wf_splits=5, seed=42, producer="selfcheck",
    )
    assert set(c5) == set(CONTRACT_KEYS), set(c5) ^ set(CONTRACT_KEYS)
    assert all(isinstance(v, str) for v in c5.values())
    assert c5["channel_order"] == "open,high,low,close,volume"
    assert c5["class_order"] == "Up,Down,Neutral"
    assert c5["neutral_threshold"] == repr(0.005)
    assert c5["model_contract_version"] == MODEL_CONTRACT_VERSION
    assert c5["price_adjustment"] == "adjusted"
    assert build_contract(
        feature_mode="zscore", window_size=10, channels=5, horizon=5, threshold=0.005,
        wf_splits=5, seed=1, producer="x", price_adjustment="unadjusted",
    )["price_adjustment"] == "unadjusted"

    c1 = build_contract(
        feature_mode="log_return", window_size=20, channels=1,
        horizon=3, threshold=0.01, wf_splits=4, seed=0, producer="selfcheck",
        date_ranges={"training_start": "2015-01-01", "training_end": "2022-12-31"},
    )
    assert c1["channel_order"] == "log_return"
    assert c1["training_start"] == "2015-01-01" and c1["validation_end"] == ""

    c4 = build_contract(
        feature_mode="log_return_ohlc", window_size=40, channels=4,
        horizon=5, threshold=0.005, wf_splits=5, seed=7, producer="selfcheck",
    )
    assert c4["channel_order"] == "gap,high_open,low_open,close_open"
    assert c4["channels"] == "4"

    assert c5["target_type"] == "classification"  # default objective unchanged
    creg = build_contract(
        feature_mode="ohlcv_minmax", window_size=75, channels=5, horizon=5, threshold=0.005,
        wf_splits=5, seed=42, producer="selfcheck", target_type="regression",
    )
    assert creg["target_type"] == "regression"
    assert set(creg) == set(CONTRACT_KEYS)  # regression keeps the same key set
    assert creg["model_contract_version"] == MODEL_CONTRACT_VERSION  # no version bump

    try:
        build_contract(feature_mode="bogus", window_size=10, channels=5,
                       horizon=5, threshold=0.005, wf_splits=5, seed=1, producer="x")
    except ValueError:
        pass
    else:  # pragma: no cover
        raise AssertionError("expected ValueError for unknown feature_mode")

    try:
        build_contract(feature_mode="ohlcv_minmax", window_size=10, channels=5,
                       horizon=5, threshold=0.005, wf_splits=5, seed=1, producer="x",
                       target_type="ranking")
    except ValueError:
        pass
    else:  # pragma: no cover
        raise AssertionError("expected ValueError for unknown target_type")

    try:
        build_contract(feature_mode="log_return_ohlc", window_size=10, channels=5,
                       horizon=5, threshold=0.005, wf_splits=5, seed=1, producer="x")
    except ValueError:
        pass
    else:  # pragma: no cover
        raise AssertionError("expected ValueError for channels/feature_mode mismatch")

    node = helper.make_node("Identity", ["x"], ["y"])
    graph = helper.make_graph(
        [node], "g",
        [helper.make_tensor_value_info("x", TensorProto.FLOAT, [1])],
        [helper.make_tensor_value_info("y", TensorProto.FLOAT, [1])],
    )
    model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 17)])
    apply(model, c5)
    assert {e.key: e.value for e in model.metadata_props} == c5
    apply(model, c1)  # replaces, never appends
    assert {e.key: e.value for e in model.metadata_props} == c1
    assert len(model.metadata_props) == len(CONTRACT_KEYS)

    print("onnx_meta.py selfcheck: OK")


def _parse_args(argv) -> argparse.Namespace:
    p = argparse.ArgumentParser(description="Trend-predictor ONNX model-contract metadata.")
    p.add_argument("--selfcheck", action="store_true", help="Run assertions and exit.")
    return p.parse_args(argv)


if __name__ == "__main__":
    ns = _parse_args(sys.argv[1:])
    if ns.selfcheck:
        _run_selfcheck()
    else:
        print("nothing to do; pass --selfcheck", file=sys.stderr)
        sys.exit(2)
