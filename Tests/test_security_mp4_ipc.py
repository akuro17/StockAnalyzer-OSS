import io
import sys
import unittest
from pathlib import Path

import pyarrow as pa

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "StockAnalyzer.Core" / "Scripts"))
import server  # noqa: E402


def _arrow_ipc_bytes(table):
    sink = io.BytesIO()
    with pa.ipc.new_stream(sink, table.schema) as writer:
        writer.write_table(table)
    return sink.getvalue()


class ServerIpcBoundsAndArrowTests(unittest.TestCase):
    def test_echo_method_is_removed(self):
        resp = server._dispatch_command("echo", {"hello": "world"}, None)
        self.assertEqual(resp["status"], "error")
        self.assertIn("Unknown method", resp["error"])

    def test_fft_trend_filter_rejects_bool_float_and_out_of_range_without_clamping(self):
        for bad_args in (
            {"windowSize": 3, "numHarmonics": 1},
            {"windowSize": 4097, "numHarmonics": 1},
            {"windowSize": True, "numHarmonics": 1},
            {"windowSize": 64.5, "numHarmonics": 1},
            {"windowSize": 16, "numHarmonics": 0},
            {"windowSize": 16, "numHarmonics": 10},
        ):
            with self.assertRaises(ValueError):
                server._dispatch_command("calculate_fft_trend_filter", bad_args, None)

    def test_search_similar_patterns_rejects_out_of_range_bool_and_non_finite(self):
        for bad_args in (
            {"lookback": -1},
            {"lookback": server.MAX_LOOKBACK + 1},
            {"topK": 0},
            {"topK": server.MAX_TOP_K + 1},
            {"futureSteps": 0},
            {"futureSteps": server.MAX_FUTURE_STEPS + 1},
            {"queryLength": 1},
            {"queryLength": server.MAX_QUERY_LENGTH + 1},
            {"warpingRadius": -2},
            {"warpingRadius": server.MAX_WARPING_RADIUS + 1},
            {"threshold": -0.01},
            {"threshold": 1.01},
            {"threshold": float("nan")},
            {"threshold": float("inf")},
            {"threshold": True},
            {"queryLength": False},
        ):
            with self.assertRaises(ValueError):
                server._dispatch_command("search_similar_patterns", bad_args, None)

    def test_read_transfer_buffer_and_load_arrow_dataframe_enforces_column_and_row_limits(self):
        good_table = pa.table({"Close": [10.0, 11.0, 12.0]})
        payload = _arrow_ipc_bytes(good_table)
        pos = 0

        def fake_read_pipe(n):
            nonlocal pos
            chunk = payload[pos:pos + min(n, 17)]
            pos += len(chunk)
            return chunk

        buf = server._read_transfer_buffer(fake_read_pipe, len(payload))
        df = server._load_arrow_dataframe(buf)
        self.assertEqual(len(df), 3)
        self.assertEqual(list(df["Close"]), [10.0, 11.0, 12.0])

        too_many_cols = pa.table(
            {"Close": [1.0]} | {f"C{i}": [1.0] for i in range(server.MAX_TRANSFER_COLUMNS)}
        )
        with self.assertRaises(ValueError):
            server._load_arrow_dataframe(bytearray(_arrow_ipc_bytes(too_many_cols)))

        too_many_rows = pa.table({"Close": [1.0] * (server.MAX_TRANSFER_ROWS + 1)})
        with self.assertRaises(ValueError):
            server._load_arrow_dataframe(bytearray(_arrow_ipc_bytes(too_many_rows)))


if __name__ == "__main__":
    unittest.main()
