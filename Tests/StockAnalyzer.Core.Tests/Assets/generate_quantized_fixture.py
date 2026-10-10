"""Regenerate the dynamically quantized ONNX fixtures used by QuantizedModelLoadTests (T17).

    trend_predictor_goodmeta_quantized.onnx        trend_predictor_goodmeta.onnx after verify_model.quantize_model
                                                   (QInt8, per-tensor, ops MatMul/Gemm/LSTM) with the same metadata_props
    trend_predictor_lstm.onnx                      a small torch-exported LSTM classifier (the graph family the LSTM trainer
                                                   exports) with the same metadata contract; the float reference of the LSTM pair
    trend_predictor_lstm_quantized.onnx            that model after verify_model.quantize_model: the LSTM becomes the
                                                   com.microsoft contrib op DynamicQuantizeLSTM (plus MatMulInteger for the head)
    trend_predictor_lstm_sharp.onnx                the same seeded LSTM with three knobs turned (K1 / FX1 A, DF27 A: a SEPARATE pair, the one
                                                   above is unchanged): the LSTM input weights scaled, the head bias centered on the
                                                   measurement windows (so the top class varies), the head scaled (so it is peaked). An
                                                   argmax comparison is only meaningful when the top class is clearly ahead AND not the
                                                   same for every window; the first combination of the table that meets the measured
                                                   criteria below is used, or the pair is not written
    trend_predictor_lstm_sharp_quantized.onnx      its dynamically quantized pair; the report also holds the Python-side measurement
    trend_predictor_lstm_sharp.candles.json        the first CSHARP_SERIES closed-form candle series: the C# test reads them, so the formulas have one owner
    *.quantization.json                            source file/hash, the compute nodes the converter produced and the
                                                   tolerance the C# test applies (verify_model's default, one owner)

The production code under test is StockAnalyzer.Core.Services.PredictionService; this script only produces the input it
loads. The MatMul pair uses standard-domain ops (DynamicQuantizeLinear, MatMulInteger); the LSTM pair additionally needs the
contrib op, which only the C# runtime (Microsoft.ML.OnnxRuntime 1.24.x) can prove it runs. Re-run only when the contract or the
quantization defaults change:

    StockAnalyzer.Python/.venv/Scripts/python.exe Tests/StockAnalyzer.Core.Tests/Assets/generate_quantized_fixture.py

With --sharp-only only the search for the sharp pair runs (the committed pairs stay as they are). K1 result (2026-10-10): scaling the head alone
never met the criteria (agreement stayed 0.615). FX (2026-10-10, FX1 A / FX2 A): the combined table below does; the measured table of every
combination is stored in the sharp report.
"""

import itertools
import json
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path

import numpy as np
import onnx
import onnxruntime as ort

TRAINING = Path(__file__).resolve().parents[3] / "StockAnalyzer.Python" / "training"
sys.path.insert(0, str(TRAINING))
import checkpoints  # noqa: E402
import dataset as ds  # noqa: E402
import onnx_meta  # noqa: E402
import verify_model as vm  # noqa: E402

HERE = Path(__file__).resolve().parent
IR_VERSION = 9  # compatible with Microsoft.ML.OnnxRuntime 1.24.x (same as generate_onnx_fixtures.py)
OPSET = 17
FIXTURE_WINDOW = 10  # = generate_onnx_fixtures.FIXTURE_WINDOW (PredictionServiceTests pass predictionWindowSize: 10)
CHANNELS = 5
CLASSES = 3
LSTM_HIDDEN = 8
LSTM_SEED = 20261010
# K1 / DF27 A / DF28 A / FX1 A: the sharp pair. The first (input scale, head scale) combination of this table, ascending, whose MEASURED figures
# (batch 1, the closed-form windows below, which the C# test rebuilds from the same formulas) meet all criteria is used; none meeting them ->
# no sharp pair is written. The head bias is always centered (mean logit of the measurement windows removed before the head scale): with the
# bias as initialised, or zero, every window fell into one class in the FX probe (agreement 1.0 meant nothing). The table values are planning
# values without an empirical basis; the measured result of every combination is recorded in the report.
SHARP_INPUT_SCALES = (2.0, 4.0, 8.0, 16.0, 32.0, 64.0)     # multiplies the LSTM input weights (weight_ih)
SHARP_HEAD_SCALES = (4.0, 8.0, 16.0, 24.0)                  # multiplies the head weight and (centered) bias
MEASURE_SERIES = 200            # windows measured in Python
CSHARP_SERIES = 40              # windows the C# test compares (the first ones of the same sequence)
MIN_MEDIAN_MAX_PROB = 0.6       # DF28 A: "peaked"
MIN_PYTHON_AGREEMENT = 0.98     # DF28 A: float vs quantized argmax agreement measured in Python
MIN_CLASS_SHARE = 0.2           # FX2 A: a class counts as "used" when it is the top class of at least this share of the windows (planning value)
MIN_CLASSES_AT_SHARE = 2        # FX2 A: at least this many of the 3 classes must be used, otherwise an agreement of 1.0 proves nothing
CSHARP_MIN_AGREEMENT = 0.95     # DF28 A: the bound the C# test applies to the first CSHARP_SERIES windows (read from the report there)
SERIES_BARS = 20                # candles per series; the model reads the last FIXTURE_WINDOW of them (PredictionService)


@dataclass(frozen=True)
class Pair:
    source: Path
    target: Path
    report: Path
    expect_op: str | None = None  # a compute node the quantized graph must contain


CANDLES = HERE / "trend_predictor_lstm_sharp.candles.json"       # the C# test's input series (see write_csharp_candles)
SHARP = Pair(HERE / "trend_predictor_lstm_sharp.onnx", HERE / "trend_predictor_lstm_sharp_quantized.onnx",
             HERE / "trend_predictor_lstm_sharp_quantized.quantization.json", expect_op="DynamicQuantizeLSTM")
MATMUL = Pair(HERE / "trend_predictor_goodmeta.onnx", HERE / "trend_predictor_goodmeta_quantized.onnx",
              HERE / "trend_predictor_goodmeta_quantized.quantization.json")
LSTM = Pair(HERE / "trend_predictor_lstm.onnx", HERE / "trend_predictor_lstm_quantized.onnx",
            HERE / "trend_predictor_lstm_quantized.quantization.json", expect_op="DynamicQuantizeLSTM")


def fixture_candles(k: int) -> np.ndarray:
    """Series *k* as (SERIES_BARS, 5) O, H, L, C, V from closed-form sin / cos (the C# test uses the identical formulas, no random generator)."""
    i = np.arange(SERIES_BARS, dtype=np.float64)
    close = 100.0 + 8.0 * np.sin(0.37 * (k + 1) * (i + 1)) + 3.0 * np.cos(0.91 * (i + 1) + k)
    open_ = close + 2.0 * np.sin(1.3 * (i + k))
    high = np.maximum(open_, close) + 1.0 + np.abs(np.sin(0.5 * (i + 2 * k)))
    low = np.minimum(open_, close) - 1.0 - np.abs(np.cos(0.6 * (i + k)))
    volume = np.round(1000.0 + 400.0 * np.sin(0.2 * (i + 3 * k) + 1.0))
    return np.stack([open_, high, low, close, volume], axis=1)


def write_text_atomic(path: Path, text: str) -> None:
    """LF text, staged beside the target and moved with ``os.replace`` semantics (never a half-written file, never CRLF)."""
    staged = path.with_name(path.name + ".incoming")
    staged.write_bytes(text.encode("utf-8"))
    checkpoints.replace_with_retry(staged, path)


def write_csharp_candles(path: Path = CANDLES) -> None:
    """The first CSHARP_SERIES series of ``fixture_candles`` as JSON (full float64 precision): the single owner of the input the C# test feeds to
    both models, so the closed-form formulas exist only here."""
    series = [fixture_candles(k).tolist() for k in range(CSHARP_SERIES)]
    write_text_atomic(path, json.dumps(dict(series_bars=SERIES_BARS, columns=["open", "high", "low", "close", "volume"], series=series)) + "\n")
    print(f"wrote {path.name} ({CSHARP_SERIES} series of {SERIES_BARS} bars)")


def fixture_windows(n: int) -> np.ndarray:
    """The model input of series 0..n-1: the last FIXTURE_WINDOW bars min-max normalized exactly like MLDataProcessor (dataset mirror)."""
    return np.stack([ds.normalize_ohlcv_minmax(fixture_candles(k)[-FIXTURE_WINDOW:]) for k in range(n)]).astype(np.float32)


def run_batch1(path: Path, windows: np.ndarray) -> np.ndarray:
    """Probabilities one window at a time (dynamic quantization is batch dependent; the C# runtime runs batch 1)."""
    session = ort.InferenceSession(str(path), providers=["CPUExecutionProvider"])
    return np.concatenate([session.run(["output"], {"input": w[None]})[0] for w in windows]).astype(np.float64)


def measure(source: Path, target: Path) -> dict:
    windows = fixture_windows(MEASURE_SERIES)
    float_probs, quant_probs = run_batch1(source, windows), run_batch1(target, windows)
    agree = float_probs.argmax(axis=1) == quant_probs.argmax(axis=1)
    counts = np.bincount(float_probs.argmax(axis=1), minlength=CLASSES)
    return dict(windows=MEASURE_SERIES, median_max_prob=float(np.median(float_probs.max(axis=1))), argmax_agreement=float(agree.mean()),
                argmax_agreement_first_csharp_windows=float(agree[:CSHARP_SERIES].mean()),
                max_prob_delta=float(np.abs(float_probs - quant_probs).max()),
                top_class_counts=counts.tolist(), classes_at_share=int(np.count_nonzero(counts / counts.sum() >= MIN_CLASS_SHARE)))


def meets_criteria(figures: dict) -> bool:
    """DF28 A (peaked, quantization keeps the argmax) plus FX2 A (the top class is not the same for every window)."""
    return (figures["median_max_prob"] >= MIN_MEDIAN_MAX_PROB and figures["argmax_agreement"] >= MIN_PYTHON_AGREEMENT
            and figures["classes_at_share"] >= MIN_CLASSES_AT_SHARE)


def build_lstm_source(path: Path, head_scale: float = 1.0, input_scale: float = 1.0, center_bias: bool = False) -> None:
    """Seeded torch export: input [batch, 10, 5] -> LSTM(8) -> last step -> Linear(3) -> Softmax, with the full metadata contract.

    The defaults leave the model exactly as it was. *input_scale* multiplies the LSTM input weights (the hidden state separates the inputs
    more); *center_bias* removes the mean logit of the measurement windows from the head bias (the classes then share the top place);
    *head_scale* multiplies the weight and bias of the final Linear, which only sharpens the softmax. Order: input scale, centering, head scale."""
    import torch
    from torch import nn

    class Net(nn.Module):
        def __init__(self) -> None:
            super().__init__()
            self.lstm, self.head = nn.LSTM(CHANNELS, LSTM_HIDDEN, batch_first=True), nn.Linear(LSTM_HIDDEN, CLASSES)

        def forward(self, x):
            return torch.softmax(self.head(self.lstm(x)[0][:, -1]), dim=-1)

    torch.manual_seed(LSTM_SEED)
    net = Net().eval()
    with torch.no_grad():
        if input_scale != 1.0:
            for name, parameter in net.lstm.named_parameters():
                if name.startswith("weight_ih"):
                    parameter.mul_(input_scale)
        if center_bias:
            logits = net.head(net.lstm(torch.from_numpy(fixture_windows(MEASURE_SERIES)))[0][:, -1])
            net.head.bias.sub_(logits.mean(dim=0))
        if head_scale != 1.0:
            net.head.weight.mul_(head_scale)
            net.head.bias.mul_(head_scale)
    with tempfile.TemporaryDirectory(prefix="sa_lstm_fixture_", dir=str(ds.DATA_ROOT)) as tmp:
        raw = Path(tmp) / "lstm.onnx"
        torch.onnx.export(net, torch.randn(1, FIXTURE_WINDOW, CHANNELS), str(raw), input_names=["input"], output_names=["output"],
                          dynamic_axes={"input": {0: "batch"}, "output": {0: "batch"}}, opset_version=OPSET, dynamo=False)
        model = onnx.load(str(raw), load_external_data=True)
    model.ir_version = min(model.ir_version, IR_VERSION)
    onnx_meta.apply(model, onnx_meta.build_contract(
        feature_mode="ohlcv_minmax", window_size=FIXTURE_WINDOW, channels=CHANNELS, horizon=5, threshold=0.005, wf_splits=5, seed=42,
        producer="generate_quantized_fixture.py"))
    onnx.checker.check_model(model)
    onnx.save(model, str(path))


def quantize_pair(pair: Pair, extra: dict | None = None) -> dict:
    metadata = {entry.key: entry.value for entry in onnx.load(str(pair.source)).metadata_props}
    expected, removed = vm.derived_metadata(metadata)
    ds.DATA_ROOT.mkdir(parents=True, exist_ok=True)
    # quantize_dynamic stages the model through the temp dir; keep it on a normal disk (see verify_model._temp_dir).
    with tempfile.TemporaryDirectory(prefix="sa_quant_fixture_", dir=str(ds.DATA_ROOT)) as tmp:
        scratch = Path(tmp) / "scratch"
        out = Path(tmp) / pair.target.name
        vm.quantize_model(pair.source, out, ops=vm.QUANT_DEFAULT_OPS, weight_type=vm.QUANT_DEFAULT_WEIGHT_TYPE,
                          per_channel=False, scratch=scratch)
        model = onnx.load(str(out))
        model.ir_version = min(model.ir_version, IR_VERSION)
        onnx_meta.apply(model, expected)
        onnx.checker.check_model(model)
        onnx.save(model, str(pair.target))
    counts = vm.count_quantized_nodes(pair.target)
    assert counts, "the fixture must contain quantized compute nodes"
    assert pair.expect_op is None or counts.get(pair.expect_op, 0) >= 1, (pair.expect_op, counts)
    problems = vm.compare_contracts(vm.contract_snapshot(pair.source), vm.contract_snapshot(pair.target), expected)
    assert not problems, problems
    write_text_atomic(pair.report, json.dumps(dict(
        source=pair.source.name, source_sha256=checkpoints.hash_file(pair.source), quantized_file=pair.target.name,
        quantized_compute_nodes=counts, removed_ref_keys=removed, weight_type=vm.QUANT_DEFAULT_WEIGHT_TYPE,
        ops=list(vm.QUANT_DEFAULT_OPS), max_prob_delta=vm.QUANT_DEFAULT_MAX_PROB_DELTA, **(extra or {}),
    ), indent=2, sort_keys=True) + "\n")
    print(f"wrote {pair.target.name} ({counts}) and {pair.report.name}")
    return counts


def build_sharp_pair() -> None:
    """Try the (input scale, head scale) table in ascending order; the first combination whose measurement meets all criteria becomes the pair
    (DF28 A, FX2 A). None -> nothing is written."""
    tried = []
    for input_scale, head_scale in itertools.product(SHARP_INPUT_SCALES, SHARP_HEAD_SCALES):
        build_lstm_source(SHARP.source, head_scale=head_scale, input_scale=input_scale, center_bias=True)
        quantize_pair(SHARP)
        figures = measure(SHARP.source, SHARP.target)
        met = meets_criteria(figures)
        tried.append(dict(input_scale=input_scale, head_scale=head_scale, **figures, criteria_met=met))
        print(f"  input_scale {input_scale:g} head_scale {head_scale:g}: median max prob {figures['median_max_prob']:.3f}, argmax agreement "
              f"{figures['argmax_agreement']:.3f} (first {CSHARP_SERIES}: {figures['argmax_agreement_first_csharp_windows']:.3f}), top classes "
              f"{figures['top_class_counts']}, max |dp| {figures['max_prob_delta']:.2e} -> {'MET' if met else 'not met'}")
        if met:
            quantize_pair(SHARP, extra=dict(
                input_scale=input_scale, head_scale=head_scale, head_bias="centered_on_the_measurement_windows",
                criteria=dict(min_median_max_prob=MIN_MEDIAN_MAX_PROB, min_python_argmax_agreement=MIN_PYTHON_AGREEMENT,
                              min_class_share=MIN_CLASS_SHARE, min_classes_at_share=MIN_CLASSES_AT_SHARE,
                              min_csharp_argmax_agreement=CSHARP_MIN_AGREEMENT),
                measured_python_batch1=figures, combinations_tried=tried, series_bars=SERIES_BARS, window=FIXTURE_WINDOW,
                csharp_series=CSHARP_SERIES, candles_file=CANDLES.name,
                inputs="closed-form sin / cos candles, series k = 0.." + str(MEASURE_SERIES - 1) + "; see fixture_candles()"))
            write_csharp_candles()
            return
    for path in (SHARP.source, SHARP.target, SHARP.report, CANDLES):
        path.unlink(missing_ok=True)
    print("NO sharp pair written: no head scale met the criteria; scales tried:", json.dumps(tried))


def main() -> None:
    if "--candles-only" in sys.argv[1:]:  # rewrites only the C# input series next to an existing sharp pair (models and reports untouched)
        write_csharp_candles()
        return
    if "--sharp-only" in sys.argv[1:]:  # regenerating the committed pairs is not byte-reproducible across environments (K1 observed it)
        build_sharp_pair()
        return
    build_lstm_source(LSTM.source)
    print(f"wrote {LSTM.source.name}")
    for pair in (MATMUL, LSTM):
        quantize_pair(pair)
    build_sharp_pair()


if __name__ == "__main__":
    main()
