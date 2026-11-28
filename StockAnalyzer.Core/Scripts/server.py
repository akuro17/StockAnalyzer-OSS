import sys
import json
import argparse
import time
import math
import os
import re
import threading
import pyarrow as pa

# Dynamic import for Windows-specific modules
IS_WINDOWS = sys.platform == 'win32'
if not IS_WINDOWS:
    import socket

# Bytes requested per read() on the Windows byte-stream pipe.
PIPE_READ_CHUNK_SIZE = 65536
MAX_LINE_BYTES = 16 * 1024 * 1024       # 16 MiB
MAX_TRANSFER_BYTES = 256 * 1024 * 1024  # 256 MiB
MAX_TRANSFER_ROWS = 100000
MAX_TRANSFER_COLUMNS = 32

MIN_WINDOW_SIZE = 4
MAX_WINDOW_SIZE = 4096
MIN_TOP_K = 1
MAX_TOP_K = 100
MIN_FUTURE_STEPS = 1
MAX_FUTURE_STEPS = 500
MIN_QUERY_LENGTH = 2
MAX_QUERY_LENGTH = 1000
MIN_LOOKBACK = 0
MAX_LOOKBACK = 50000
MIN_WARPING_RADIUS = -1
MAX_WARPING_RADIUS = 500
MIN_THRESHOLD = 0.0
MAX_THRESHOLD = 1.0

_PIPE_NAME_PATTERN = re.compile(r"^[A-Za-z0-9_.-]{1,64}$")


def write_all(pipe, data):
    """Write every byte of data to an unbuffered binary stream, repeating on partial writes."""
    view = memoryview(data)
    offset = 0
    while offset < len(view):
        written = pipe.write(view[offset:])
        if not written:
            raise IOError("Pipe write made no progress (connection closed).")
        offset += written


def read_line(pipe, cursor):
    """Read one LF-terminated frame from a byte-stream pipe.

    cursor is a per-connection bytearray holding bytes already read past the previous frame.
    Returns the frame without its LF (and a preceding CR), or None on clean EOF with no pending bytes.
    Raises IOError if EOF arrives in the middle of a frame.
    """
    while True:
        newline_index = cursor.find(b'\n')
        if newline_index >= 0:
            line = bytes(cursor[:newline_index])
            del cursor[:newline_index + 1]
            if line.endswith(b'\r'):
                line = line[:-1]
            return line
        if len(cursor) >= MAX_LINE_BYTES:
            raise IOError(f"Frame exceeded maximum line limit ({MAX_LINE_BYTES} bytes)")
        chunk = pipe.read(PIPE_READ_CHUNK_SIZE)
        if not chunk:
            if cursor:
                raise IOError("Incomplete frame received before end of stream.")
            return None
        cursor.extend(chunk)


def read_some(pipe, cursor, size):
    """Return up to size bytes, consuming bytes already held in cursor before reading the pipe."""
    if cursor:
        data = bytes(cursor[:size])
        del cursor[:size]
        return data
    data = pipe.read(size)
    if not data:
        raise IOError("Connection closed by peer.")
    return data


def read_line_uds(conn, cursor):
    """Read one LF-terminated frame from a Unix domain socket using chunk buffering."""
    while True:
        newline_index = cursor.find(b'\n')
        if newline_index >= 0:
            line = bytes(cursor[:newline_index])
            del cursor[:newline_index + 1]
            if line.endswith(b'\r'):
                line = line[:-1]
            return line
        if len(cursor) >= MAX_LINE_BYTES:
            raise IOError(f"Frame exceeded maximum line limit ({MAX_LINE_BYTES} bytes)")
        try:
            chunk = conn.recv(PIPE_READ_CHUNK_SIZE)
        except Exception as e:
            if cursor:
                raise IOError(f"Socket read error with incomplete frame: {e}")
            return None
        if not chunk:
            if cursor:
                raise IOError("Incomplete frame received before end of stream.")
            return None
        cursor.extend(chunk)


def read_some_uds(conn, cursor, size):
    """Return up to size bytes, consuming bytes already held in cursor before reading socket."""
    if cursor:
        data = bytes(cursor[:size])
        del cursor[:size]
        return data
    data = conn.recv(size)
    if not data:
        raise IOError("Connection closed by peer.")
    return data


def validate_pipe_name(pipe_name):
    """Validate pipe name to prevent directory traversal and arbitrary file unlinking."""
    if not isinstance(pipe_name, str) or not _PIPE_NAME_PATTERN.fullmatch(pipe_name):
        raise ValueError(f"Invalid pipe name: {pipe_name!r}")
    if os.path.basename(pipe_name) != pipe_name or ".." in pipe_name or "/" in pipe_name or "\\" in pipe_name:
        raise ValueError(f"Pipe name contains path traversal segments: {pipe_name!r}")
    return pipe_name


def get_uds_socket_path(pipe_name):
    """Resolve Unix domain socket path matching .NET NamedPipe discovery."""
    validate_pipe_name(pipe_name)
    dotnet_pipe_env = os.environ.get('DOTNET_PIPE_PATH')
    if dotnet_pipe_env and os.path.exists(dotnet_pipe_env):
        return os.path.join(dotnet_pipe_env, pipe_name)

    net8_default_dir = "/tmp/.dotnet/pipes"
    if os.path.isdir(net8_default_dir) or os.path.isdir("/tmp"):
        try:
            os.makedirs(net8_default_dir, exist_ok=True)
            return os.path.join(net8_default_dir, pipe_name)
        except Exception:
            pass

    return f"/tmp/CoreFX-NamedPipe-{pipe_name}"


# Sakoe-Chiba radius meaning "unconstrained"; must match C# DtwMath.UnconstrainedRadius.
DTW_UNCONSTRAINED_RADIUS = -1


def _dtw_distance_1d(x, y, sakoe_chiba_radius=DTW_UNCONSTRAINED_RADIUS):
    """Pure-Python DTW distance between two 1-D sequences.

    Returns sqrt(D(m, n)) where D is the accumulated squared point distance along the optimal
    warping path (same definition as the C# DtwMath.Calculate). sakoe_chiba_radius == -1 means
    unconstrained, 0 means diagonal only; otherwise cells farther than the radius from the (scaled)
    diagonal are excluded. The shorter sequence is used as the column dimension (as in C#).
    x and y may be shaped (m,) or (m, 1).
    """
    import numpy as np

    xs = np.asarray(x, dtype=float).ravel().tolist()
    ys = np.asarray(y, dtype=float).ravel().tolist()
    if len(xs) == 0 or len(ys) == 0:
        raise ValueError("DTW input sequences must not be empty.")
    if sakoe_chiba_radius < DTW_UNCONSTRAINED_RADIUS:
        raise ValueError("sakoe_chiba_radius must be >= %d." % DTW_UNCONSTRAINED_RADIUS)
    if len(xs) < len(ys):
        xs, ys = ys, xs
    m, n = len(xs), len(ys)

    inf = float('inf')
    banded = sakoe_chiba_radius != DTW_UNCONSTRAINED_RADIUS

    prev = [inf] * n
    prev[0] = (xs[0] - ys[0]) ** 2
    for j in range(1, n):
        if banded and j > sakoe_chiba_radius:
            break
        prev[j] = prev[j - 1] + (xs[0] - ys[j]) ** 2

    for i in range(1, m):
        if banded:
            center = (i * (n - 1)) // max(m - 1, 1)
            j_start = max(0, center - sakoe_chiba_radius)
            j_end = min(n - 1, center + sakoe_chiba_radius)
        else:
            j_start = 0
            j_end = n - 1

        curr = [inf] * n
        xi = xs[i]
        for j in range(j_start, j_end + 1):
            best = prev[j]
            if j > 0:
                left = curr[j - 1]
                diag = prev[j - 1]
                if left < best:
                    best = left
                if diag < best:
                    best = diag
            curr[j] = (xi - ys[j]) ** 2 + best
        prev = curr

    return float(prev[n - 1] ** 0.5)


# Background thread to monitor parent process to prevent zombie processes
def parent_monitor_loop(parent_pid, socket_path=None):
    if parent_pid <= 0:
        return
    while True:
        time.sleep(1.0)
        try:
            # Under Unix, if the parent process dies, the process is re-parented to init (PID 1)
            if not IS_WINDOWS:
                if os.getppid() == 1:
                    print("[Python Server] Parent process died. Exiting self-healing loop.")
                    if socket_path and os.path.exists(socket_path):
                        try:
                            os.unlink(socket_path)
                        except Exception:
                            pass
                    os._exit(0)
            else:
                # Windows fallback check
                import ctypes
                kernel32 = ctypes.windll.kernel32
                SYNCHRONIZE = 0x00100000
                ERROR_INVALID_PARAMETER = 87
                process_handle = kernel32.OpenProcess(SYNCHRONIZE, False, parent_pid)
                if process_handle == 0:
                    if kernel32.GetLastError() == ERROR_INVALID_PARAMETER:
                        print("[Python Server] Parent process (Windows) died. Exiting.")
                        os._exit(0)
                else:
                    status = kernel32.WaitForSingleObject(process_handle, 0)
                    kernel32.CloseHandle(process_handle)
                    if status == 0: # WAIT_OBJECT_0 (Signaled / terminated)
                        print("[Python Server] Parent process (Windows) died. Exiting.")
                        os._exit(0)
        except Exception:
            pass

def _require_int_in_range(args, key, default, min_val, max_val):
    value = args.get(key, default)
    if not isinstance(value, int) or isinstance(value, bool):
        raise ValueError(f"Parameter '{key}' must be an integer.")
    if value < min_val or value > max_val:
        raise ValueError(f"Parameter '{key}' must be between {min_val} and {max_val}.")
    return value


def _require_finite_float_in_range(args, key, default, min_val, max_val):
    value = args.get(key, default)
    if not isinstance(value, (int, float)) or isinstance(value, bool):
        raise ValueError(f"Parameter '{key}' must be a finite number.")
    result = float(value)
    if not math.isfinite(result) or result < min_val or result > max_val:
        raise ValueError(f"Parameter '{key}' must be finite and between {min_val} and {max_val}.")
    return result


def _read_transfer_buffer(read_pipe, size):
    """Read an exact byte payload directly into one preallocated bytearray without chunk concatenation copies."""
    buf = bytearray(size)
    mv = memoryview(buf)
    received = 0
    try:
        while received < size:
            to_read = min(PIPE_READ_CHUNK_SIZE, size - received)
            data_chunk = read_pipe(to_read)
            chunk_len = len(data_chunk)
            if chunk_len <= 0:
                raise IOError("Connection closed while reading Arrow transfer payload.")
            if chunk_len > size - received:
                raise IOError("Received more bytes than declared transfer size.")
            mv[received:received + chunk_len] = data_chunk
            received += chunk_len
    finally:
        mv.release()
    return buf


def _load_arrow_dataframe(buf):
    """Decode an Arrow IPC stream with strict column/row limits and zero-extra-copy buffer view."""
    reader = pa.ipc.open_stream(pa.py_buffer(buf))
    if len(reader.schema) > MAX_TRANSFER_COLUMNS:
        raise ValueError(f"Arrow table exceeds maximum column limit ({MAX_TRANSFER_COLUMNS}).")
    batches = []
    total_rows = 0
    for batch in reader:
        total_rows += batch.num_rows
        if total_rows > MAX_TRANSFER_ROWS:
            raise ValueError(f"Arrow table exceeds maximum row limit ({MAX_TRANSFER_ROWS}).")
        batches.append(batch)
    table = pa.Table.from_batches(batches, schema=reader.schema)
    del batches
    df = table.to_pandas()
    del table
    if 'Close' not in df.columns:
        raise ValueError("DataFrame must contain 'Close' column")
    return df


def _dispatch_command(method, args, global_df):
    """Validate and execute non-transfer JSON RPC methods cleanly without silent clamping."""
    response = {"status": "ok", "result": None}
    args_dict = {} if args is None else args
    if not isinstance(args_dict, dict):
        raise ValueError("Command 'args' must be a JSON object.")

    if method == "ping":
        response["result"] = "pong"
        return response

    if method == "calculate_fft_trend_filter":
        window_size = _require_int_in_range(args_dict, 'windowSize', 64, MIN_WINDOW_SIZE, MAX_WINDOW_SIZE)
        max_harmonics = window_size // 2 + 1
        num_harmonics = _require_int_in_range(args_dict, 'numHarmonics', 4, 1, max_harmonics)
        if global_df is None:
            response["status"] = "error"
            response["error"] = "No data available. Send data first."
            return response

        import numpy as np

        if 'High' in global_df.columns and 'Low' in global_df.columns:
            prices = (global_df['High'] + global_df['Low']) / 2.0
        else:
            prices = global_df['Close']
        prices = prices.values

        n = len(prices)
        trend = np.full(n, None, dtype=object)

        for i in range(window_size - 1, n):
            segment = prices[i - window_size + 1:i + 1]

            # No windowing, no detrend: unlike the FFT Cycle indicator, this
            # filter must retain the DC/trend component, and a Hanning
            # taper would suppress the very last sample we extract below.
            spectrum = np.fft.rfft(segment)
            filtered_spectrum = np.zeros_like(spectrum)
            filtered_spectrum[:num_harmonics] = spectrum[:num_harmonics]
            reconstructed = np.fft.irfft(filtered_spectrum, n=window_size)

            # Causal: only the last sample (this bar) is emitted, so no
            # future data leaks in and no repaint occurs as new bars
            # arrive -- the value at bar i depends only on prices up to i.
            trend[i] = float(reconstructed[-1])

        response["result"] = {
            "trend": [float(x) if x is not None else None for x in trend]
        }
        return response

    if method == "calculate_backtest_stats":
        import pandas as pd
        import numpy as np

        trades = args_dict.get('trades', [])
        if not trades:
            response["status"] = "error"
            response["error"] = "No trades provided."
            return response

        df_trades = pd.DataFrame(trades)
        if 'ProfitLoss' not in df_trades.columns:
            raise Exception("Trades must contain 'ProfitLoss' column")

        pnl = df_trades['ProfitLoss'].values
        cumulative_pnl = np.cumsum(pnl)
        peak = np.maximum.accumulate(cumulative_pnl)
        drawdown = peak - cumulative_pnl
        max_drawdown = float(np.max(drawdown)) if len(drawdown) > 0 else 0.0

        wins = df_trades[df_trades['ProfitLoss'] > 0]
        win_rate = float(len(wins) / len(df_trades)) if len(df_trades) > 0 else 0.0
        avg_profit = float(wins['ProfitLoss'].mean()) if len(wins) > 0 else 0.0
        losses = df_trades[df_trades['ProfitLoss'] <= 0]
        avg_loss = float(losses['ProfitLoss'].mean()) if len(losses) > 0 else 0.0
        profit_factor = float(abs(wins['ProfitLoss'].sum() / losses['ProfitLoss'].sum())) if len(losses) > 0 and losses['ProfitLoss'].sum() != 0 else float('inf')

        mean_pnl = np.mean(pnl)
        std_pnl = np.std(pnl)
        trade_sharpe = float(mean_pnl / std_pnl) if std_pnl != 0 else 0.0

        downside_pnl = pnl[pnl < 0]
        downside_std = np.std(downside_pnl) if len(downside_pnl) > 0 else 0.0
        trade_sortino = float(mean_pnl / downside_std) if downside_std != 0 else 0.0

        def safe_float(v):
            if pd.isna(v) or np.isinf(v):
                return None
            return float(v)

        response["result"] = {
            "TotalTrades": len(df_trades),
            "WinRate": safe_float(win_rate),
            "MaxDrawdown": safe_float(max_drawdown),
            "AverageProfit": safe_float(avg_profit),
            "AverageLoss": safe_float(avg_loss),
            "ProfitFactor": safe_float(profit_factor),
            "TradeSharpeRatio": safe_float(trade_sharpe),
            "TradeSortinoRatio": safe_float(trade_sortino),
            "TotalProfit": float(np.sum(pnl))
        }
        return response

    if method == "search_similar_patterns":
        lookback = _require_int_in_range(args_dict, 'lookback', 0, MIN_LOOKBACK, MAX_LOOKBACK)
        top_k = _require_int_in_range(args_dict, 'topK', 5, MIN_TOP_K, MAX_TOP_K)
        future_steps = _require_int_in_range(args_dict, 'futureSteps', 20, MIN_FUTURE_STEPS, MAX_FUTURE_STEPS)
        threshold = _require_finite_float_in_range(args_dict, 'threshold', 0.3, MIN_THRESHOLD, MAX_THRESHOLD)
        query_start_index = _require_int_in_range(args_dict, 'queryStartIndex', -1, -1, MAX_TRANSFER_ROWS)
        query_length = _require_int_in_range(args_dict, 'queryLength', 30, MIN_QUERY_LENGTH, MAX_QUERY_LENGTH)
        use_structural = args_dict.get('useStructural', False)
        if not isinstance(use_structural, bool):
            raise ValueError("Parameter 'useStructural' must be a boolean.")
        warping_radius = _require_int_in_range(
            args_dict,
            'warpingRadius',
            DTW_UNCONSTRAINED_RADIUS,
            MIN_WARPING_RADIUS,
            MAX_WARPING_RADIUS,
        )

        if global_df is None:
            response["status"] = "error"
            response["error"] = "No data available. Send data first."
            return response

        import numpy as np

        close = global_df['Close'].values.astype(float)
        n = len(close)

        if n < query_length + future_steps:
            response["status"] = "error"
            response["error"] = f"Insufficient data: need >= {query_length + future_steps} candles, got {n}"
            return response

        actual_q_start = query_start_index if query_start_index >= 0 else (n - query_length)
        actual_q_end = actual_q_start + query_length
        actual_q_start = max(0, min(actual_q_start, n - 1))
        actual_q_end = max(actual_q_start + 1, min(actual_q_end, n))
        query_length = actual_q_end - actual_q_start

        query = close[actual_q_start:actual_q_end]
        q_std = np.std(query)
        if q_std < 1e-10:
            q_std = 1.0
        query_z = ((query - np.mean(query)) / q_std).reshape(-1, 1)

        vol_full = None
        query_vol = 0.0
        if use_structural:
            supplied_vol = args_dict.get('volatility')
            if supplied_vol is not None and len(supplied_vol) == n:
                vol_full = np.asarray(supplied_vol, dtype=float)
                if np.all(np.isfinite(vol_full)):
                    query_vol = float(np.mean(vol_full[actual_q_start:actual_q_end]))
                else:
                    vol_full = None
            if vol_full is None:
                use_structural = False

        search_start = 0
        if lookback > 0:
            search_start = max(0, actual_q_start - lookback)
        search_end = actual_q_start - query_length

        if search_end <= search_start:
            response["result"] = {
                "patterns": [],
                "queryLength": query_length,
                "debug_info": f"q_s={actual_q_start} n={n} s={search_start} e={search_end} (No historical data before selection)",
            }
            return response

        candidates = []
        step = max(1, query_length // 4)
        for start in range(search_start, search_end, step):
            end = start + query_length
            segment = close[start:end]
            s_std = np.std(segment)
            if s_std < 1e-10:
                continue
            segment_z = ((segment - np.mean(segment)) / s_std).reshape(-1, 1)

            if warping_radius >= 0:
                dist = float(_dtw_distance_1d(query_z, segment_z, sakoe_chiba_radius=warping_radius))
            else:
                dist = float(_dtw_distance_1d(query_z, segment_z))
            rmse = dist / np.sqrt(query_length)

            if use_structural and vol_full is not None:
                seg_vol = float(np.mean(vol_full[start:end]))
                vol_ratio = max(query_vol, seg_vol) / max(min(query_vol, seg_vol), 1e-10)
                vol_penalty = np.log1p(vol_ratio - 1)
                rmse += vol_penalty * 0.1

            prob = float(np.exp(-rmse))
            if prob >= threshold or len(candidates) < top_k * 2:
                future_end = min(end + future_steps, n)
                future_raw = close[end:future_end].tolist()
                base_price = float(close[end - 1])
                future_pct = [round((p / base_price - 1.0) * 100, 4) for p in future_raw]

                candidates.append({
                    "distance": round(dist, 4),
                    "probability": round(prob, 4),
                    "startIndex": int(start),
                    "endIndex": int(end - 1),
                    "matchedPrices": [round(float(v), 4) for v in close[start:end]],
                    "futureRawPrices": [round(float(v), 4) for v in future_raw],
                    "futurePercentChange": future_pct
                })

        candidates.sort(key=lambda x: x["distance"])
        top_matches = candidates[:top_k]
        response["result"] = {
            "queryLength": query_length,
            "patterns": top_matches,
            "debug_info": f"scanned {len(candidates)} candidates. top matches: {len(top_matches)}"
        }
        return response

    response["status"] = "error"
    response["error"] = f"Unknown method: {method}"
    return response


def run_server(pipe_name):
    # Start parent monitor thread
    parent_pid = os.getppid()
    socket_path = None
    if not IS_WINDOWS:
        socket_path = get_uds_socket_path(pipe_name)
    monitor_thread = threading.Thread(target=parent_monitor_loop, args=(parent_pid, socket_path), daemon=True)
    monitor_thread.start()

    print(f"Connecting to pipe: {pipe_name}")
    
    # Global state to store the latest data
    global_df = None

    if IS_WINDOWS:
        full_pipe_name = f"\\\\.\\pipe\\{pipe_name}"
        try:
            # The .NET parent hosts a byte-mode NamedPipeServerStream; we connect as a plain file client.
            # Byte mode has no message boundaries: requests are LF-framed and binary payloads are size-prefixed
            # by the prepare_data_transfer request, so surplus bytes read past a frame stay in read_cursor.
            print(f"Connecting to {full_pipe_name}...")
            pipe_io = open(full_pipe_name, "r+b", buffering=0)
            read_cursor = bytearray()
            print("Connected!")

            def read_pipe(size):
                return read_some(pipe_io, read_cursor, size)

            def write_pipe(data):
                write_all(pipe_io, data)

            def close_pipe():
                pipe_io.close()
        except Exception as e:
            print(f"Pipe error: {e}")
            return
    else:
        # Unix Named Pipe mapping via Unix Domain Socket
        socket_path = get_uds_socket_path(pipe_name)
        
        # Clean up any stale socket file
        if os.path.exists(socket_path):
            try:
                os.unlink(socket_path)
            except Exception:
                pass
                
        print(f"Binding Unix domain socket at {socket_path}...")
        server_socket = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        try:
            old_umask = os.umask(0o077) if hasattr(os, 'umask') else None
            try:
                server_socket.bind(socket_path)
            finally:
                if old_umask is not None:
                    os.umask(old_umask)
            server_socket.listen(1)
            conn, addr = server_socket.accept()
            read_cursor = bytearray()
            print("Connected via UDS!")
            
            def read_pipe(size):
                return read_some_uds(conn, read_cursor, size)
                
            def write_pipe(data):
                conn.sendall(data)
                
            def close_pipe():
                conn.close()
                server_socket.close()
                try:
                    os.unlink(socket_path)
                except Exception:
                    pass
        except Exception as e:
            print(f"Socket error: {e}")
            try:
                os.unlink(socket_path)
            except Exception:
                pass
            return

    try:
        while True:
            # Read request (readline equivalent for UDS / ReadFile for Windows NamedPipe)
            if IS_WINDOWS:
                try:
                    line = read_line(pipe_io, read_cursor)
                except Exception as e:
                    print(f"Exception reading pipe: {e}")
                    break
                if line is None:
                    print("Connection closed (EOF).")
                    break
                decoded_msg = line.decode('utf-8').strip()
            else:
                try:
                    line = read_line_uds(conn, read_cursor)
                except Exception as e:
                    print(f"Exception reading socket: {e}")
                    break
                if line is None:
                    print("Connection closed (EOF).")
                    break
                decoded_msg = line.decode('utf-8').strip()

            if not decoded_msg:
                continue

            try:
                request = json.loads(decoded_msg)
                method = request.get('method')
                args = request.get('args')

                if method == "prepare_data_transfer":
                    size = args.get('size', 0) if isinstance(args, dict) else 0
                    if not isinstance(size, int) or isinstance(size, bool) or size <= 0 or size > MAX_TRANSFER_BYTES:
                        err_resp = {
                            "status": "error",
                            "error": f"Invalid transfer size. Must be an integer between 1 and {MAX_TRANSFER_BYTES} bytes."
                        }
                        write_pipe((json.dumps(err_resp) + "\n").encode('utf-8'))
                    else:
                        write_pipe((json.dumps({"status": "ready", "result": None}) + "\n").encode('utf-8'))
                        try:
                            buf = _read_transfer_buffer(read_pipe, size)
                            global_df = _load_arrow_dataframe(buf)
                            del buf
                            final_response = {"status": "transfer_complete", "rows": len(global_df)}
                            write_pipe((json.dumps(final_response) + "\n").encode('utf-8'))
                        except Exception as e:
                            err_response = {"status": "error", "error": str(e)}
                            write_pipe((json.dumps(err_response) + "\n").encode('utf-8'))
                    continue

                try:
                    response = _dispatch_command(method, args, global_df)
                except ImportError as ie:
                    response = {"status": "error", "error": f"Required package not installed: {ie}"}
                except Exception as e:
                    response = {"status": "error", "error": str(e)}

                response_json = json.dumps(response) + "\n"
                write_pipe(response_json.encode('utf-8'))
                
            except json.JSONDecodeError as e:
                snippet = decoded_msg[:200] if decoded_msg else "empty"
                err_resp = json.dumps({"status": "error", "error": f"Invalid JSON: {e}. Snip: {snippet}"}) + "\n"
                write_pipe(err_resp.encode('utf-8'))
                
    except Exception as e:
        print(f"Server error: {e}")
    finally:
        try:
            close_pipe()
        except:
            pass

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--pipe", required=True, help="Named pipe name")
    args = parser.parse_args()
    
    run_server(args.pipe)
