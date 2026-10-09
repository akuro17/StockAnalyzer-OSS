"""Regenerate the minimal ONNX fixtures of the portfolio actor boundary (RlPortfolioActorModel tests).

These are NOT trained models: hand-built linear graphs with a closed-form weight rule, so the C# test recomputes the logits independently.

    rl_portfolio_fixture_n3.onnx        input "observation" float32 [1, 970]  (N_max = 3: 3 * (64 * 5 + 2) + 4)
                                        output "logits" float32 [1, 4]        (cash + 3 asset slots)
                                        logits[j] = sum_i x[i] * W[i][j] + b[j],
                                        W[i][j] = ((i * 7 + j * 13) % 17 - 8) / 1000,  b[j] = (j - 1.5) / 10
    rl_portfolio_fixture_badshape.onnx  same input, output [1, 3] (wrong logit count)
    rl_portfolio_fixture_nan.onnx       same input, output [1, 4] that is always NaN
    rl_portfolio_fixture_v2a_n3.onnx    the same linear rule for the v2-a variant: input [1, 1354] (3 * (64 * 7 + 2) + 4), output [1, 4]
    rl_portfolio_fixture_n3.parity.json observations and the logits that onnxruntime (Python) computes for them

The files carry no metadata_props: the tests append the metadata they need. Run with the training venv:
    StockAnalyzer.Python/.venv/Scripts/python.exe Tests/StockAnalyzer.Core.Tests/Assets/generate_rl_portfolio_fixture.py
"""
import json
from pathlib import Path

import numpy as np
import onnx
import onnxruntime as ort
from onnx import TensorProto, helper, numpy_helper

SLOTS = 3
WINDOW, AUX, GLOBALS = 64, 2, 4
CHANNELS_V1, CHANNELS_V2A = 5, 7
LENGTH = SLOTS * (WINDOW * CHANNELS_V1 + AUX) + GLOBALS
LENGTH_V2A = SLOTS * (WINDOW * CHANNELS_V2A + AUX) + GLOBALS
LOGITS = SLOTS + 1
here = Path(__file__).resolve().parent


def weights(outputs, length):
    w = np.zeros((length, outputs), np.float32)
    for i in range(length):
        for j in range(outputs):
            w[i, j] = ((i * 7 + j * 13) % 17 - 8) / 1000
    b = np.asarray([(j - 1.5) / 10 for j in range(outputs)], np.float32)
    return w, b


def build(path, outputs, nan=False, length=LENGTH):
    w, b = weights(outputs, length)
    nodes = [helper.make_node("Gemm", ["observation", "W", "b"], ["raw" if nan else "logits"])]
    initializers = [numpy_helper.from_array(w, "W"), numpy_helper.from_array(b, "b")]
    if nan:
        initializers.append(numpy_helper.from_array(np.full((outputs,), np.nan, np.float32), "nan"))
        nodes.append(helper.make_node("Mul", ["raw", "nan"], ["logits"]))
    graph = helper.make_graph(nodes, "rl_portfolio_fixture",
        [helper.make_tensor_value_info("observation", TensorProto.FLOAT, [1, length])],
        [helper.make_tensor_value_info("logits", TensorProto.FLOAT, [1, outputs])], initializers)
    model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 13)])
    model.ir_version = 8
    onnx.checker.check_model(model)
    onnx.save(model, path)


build(here / "rl_portfolio_fixture_n3.onnx", LOGITS)
build(here / "rl_portfolio_fixture_badshape.onnx", LOGITS - 1)
build(here / "rl_portfolio_fixture_nan.onnx", LOGITS, nan=True)
build(here / "rl_portfolio_fixture_v2a_n3.onnx", LOGITS, length=LENGTH_V2A)

rows = [np.zeros(LENGTH, np.float32), np.full(LENGTH, 0.5, np.float32),
        np.asarray([((i * 31) % 23 - 11) / 10 for i in range(LENGTH)], np.float32),
        np.asarray([((i * 17) % 29 - 14) / 7 for i in range(LENGTH)], np.float32)]
session = ort.InferenceSession(str(here / "rl_portfolio_fixture_n3.onnx"), providers=["CPUExecutionProvider"])
logits = [session.run(["logits"], {"observation": row.reshape(1, -1)})[0][0].tolist() for row in rows]
(here / "rl_portfolio_fixture_n3.parity.json").write_text(json.dumps(
    dict(observations=[row.tolist() for row in rows], logits=logits), allow_nan=False, separators=(",", ":")), encoding="utf-8")
print("wrote fixtures; logits of the ones-half row:", logits[1])
