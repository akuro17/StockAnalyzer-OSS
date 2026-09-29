import sys
import json
import argparse
import time
import io
import os
import threading
import pyarrow as pa

# Dynamic import for Windows-specific modules
IS_WINDOWS = sys.platform == 'win32'
if not IS_WINDOWS:
    import socket

# Bytes requested per read() on the Windows byte-stream pipe.
PIPE_READ_CHUNK_SIZE = 65536


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

def run_server(pipe_name):
    # Start parent monitor thread
    parent_pid = os.getppid()
    socket_path = None
    if not IS_WINDOWS:
        socket_path = f"/tmp/CoreFX-NamedPipe-{pipe_name}"
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
        # .NET NamedPipeServer/Client on Unix maps to a Unix Domain Socket at /tmp/CoreFX-NamedPipe-{pipe_name}
        socket_path = f"/tmp/CoreFX-NamedPipe-{pipe_name}"
        
        # Clean up any stale socket file
        if os.path.exists(socket_path):
            try:
                os.unlink(socket_path)
            except Exception:
                pass
                
        print(f"Binding Unix domain socket at {socket_path}...")
        server_socket = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        try:
            server_socket.bind(socket_path)
            server_socket.listen(1)
            conn, addr = server_socket.accept()
            print("Connected via UDS!")
            
            def read_pipe(size):
                data = conn.recv(size)
                if not data:
                    raise IOError("Connection closed by peer.")
                return data
                
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
                # Read line from socket
                buffer = bytearray()
                while True:
                    try:
                        char = conn.recv(1)
                    except Exception as e:
                        print(f"Socket read error: {e}")
                        char = b""
                    if not char:
                        break
                    if char == b'\n':
                        break
                    buffer.extend(char)
                if len(buffer) == 0:
                    print("Connection closed (EOF).")
                    break
                decoded_msg = buffer.decode('utf-8').strip()

            if not decoded_msg:
                continue

            try:
                request = json.loads(decoded_msg)
                method = request.get('method')
                args = request.get('args')
                
                response = { "status": "ok", "result": None }

                if method == "ping":
                    response["result"] = "pong"
                elif method == "echo":
                     response["result"] = args
                elif method == "prepare_data_transfer":
                    # Args should contain 'size'
                    size = args.get('size', 0)
                    if size > 0:
                        # Send "ready" first
                        response["status"] = "ready"
                        write_pipe((json.dumps(response) + "\n").encode('utf-8'))
                        
                        # Now read binary data
                        received = 0
                        chunks = []
                        while received < size:
                            to_read = min(65536, size - received)
                            data_chunk = read_pipe(to_read)
                            chunks.append(data_chunk)
                            received += len(data_chunk)
                        
                        full_data = b"".join(chunks)
                        
                        # Process Arrow Data
                        try:
                            reader = pa.ipc.open_stream(io.BytesIO(full_data))
                            table = reader.read_all()
                            
                            global_df = table.to_pandas()
                            # Ensure 'Close' column exists and is numeric
                            if 'Close' not in global_df.columns:
                                raise Exception("DataFrame must contain 'Close' column")
                            
                            # Send completion response
                            final_response = { "status": "transfer_complete", "rows": len(global_df) }
                            write_pipe((json.dumps(final_response) + "\n").encode('utf-8'))
                            continue 
                            
                        except Exception as e:
                             err_response = { "status": "error", "error": str(e) }
                             write_pipe((json.dumps(err_response) + "\n").encode('utf-8'))
                             continue

                    else:
                        response["status"] = "error"
                        response["error"] = "Invalid size"
                
                elif method == "calculate_fft_trend_filter":
                    if global_df is None:
                        response["status"] = "error"
                        response["error"] = "No data available. Send data first."
                    else:
                        try:
                            import numpy as np

                            window_size = int(args.get('windowSize', 64))
                            if window_size < 4:
                                window_size = 4

                            max_harmonics = window_size // 2 + 1
                            num_harmonics = int(args.get('numHarmonics', 4))
                            num_harmonics = max(1, min(num_harmonics, max_harmonics))

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
                        except Exception as e:
                            response["status"] = "error"
                            response["error"] = str(e)

                elif method == "calculate_backtest_stats":
                    try:
                        import pandas as pd
                        import numpy as np

                        trades = args.get('trades', [])
                        if not trades:
                            response["status"] = "error"
                            response["error"] = "No trades provided."
                        else:
                            # trades is a list of dicts: {"EntryTime": "...", "ExitTime": "...", "EntryPrice": ..., "ExitPrice": ..., "Quantity": ..., "ProfitLoss": ...}
                            df_trades = pd.DataFrame(trades)
                            
                            if 'ProfitLoss' not in df_trades.columns:
                                raise Exception("Trades must contain 'ProfitLoss' column")
                                
                            # Basic calculations based on ProfitLoss
                            # Assume Risk-Free Rate is 0 for simplicity, and we calculate per trade (or per period if times are given)
                            # To calculate standard Sharpe, we need returns over time. If we only have trade PnL, we can calculate Trade Sharpe.
                            # For a proper Sharpe, we'd need Equity Curve (daily returns).
                            # If we only get a list of trades, we will calculate based on Trade Returns if available, or just aggregate PnL.
                            
                            # Let's assume we can compute an equity curve from ProfitLoss, or at least metrics based on trade outcomes
                            pnl = df_trades['ProfitLoss'].values
                            cumulative_pnl = np.cumsum(pnl)
                            
                            # Max Drawdown
                            peak = np.maximum.accumulate(cumulative_pnl)
                            drawdown = peak - cumulative_pnl
                            max_drawdown = float(np.max(drawdown)) if len(drawdown) > 0 else 0.0
                            
                            # Win Rate
                            wins = df_trades[df_trades['ProfitLoss'] > 0]
                            win_rate = float(len(wins) / len(df_trades)) if len(df_trades) > 0 else 0.0
                            
                            # Average Profit/Loss
                            avg_profit = float(wins['ProfitLoss'].mean()) if len(wins) > 0 else 0.0
                            losses = df_trades[df_trades['ProfitLoss'] <= 0]
                            avg_loss = float(losses['ProfitLoss'].mean()) if len(losses) > 0 else 0.0
                            
                            profit_factor = float(abs(wins['ProfitLoss'].sum() / losses['ProfitLoss'].sum())) if len(losses) > 0 and losses['ProfitLoss'].sum() != 0 else float('inf')
                            
                            # Simplified Sharpe/Sortino based on trade series (not time series, which is usually standard, but this is a start)
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
                    except Exception as e:
                        response["status"] = "error"
                        response["error"] = str(e)

                elif method == "search_similar_patterns":
                    if global_df is None:
                        response["status"] = "error"
                        response["error"] = "No data available. Send data first."
                    else:
                        try:
                            import numpy as np

                            lookback = args.get('lookback', 0) if args else 0  # 0 = use all history
                            top_k = args.get('topK', 5) if args else 5
                            future_steps = args.get('futureSteps', 20) if args else 20
                            threshold = args.get('threshold', 0.3) if args else 0.3
                            query_start_index = args.get('queryStartIndex', -1) if args else -1
                            query_length = args.get('queryLength', 30) if args else 30
                            use_structural = args.get('useStructural', False) if args else False
                            warping_radius = args.get('warpingRadius', DTW_UNCONSTRAINED_RADIUS) if args else DTW_UNCONSTRAINED_RADIUS

                            close = global_df['Close'].values.astype(float)
                            n = len(close)

                            if n < query_length + future_steps:
                                response["status"] = "error"
                                response["error"] = f"Insufficient data: need >= {query_length + future_steps} candles, got {n}"
                            else:
                                # Determine actual query segment
                                actual_q_start = query_start_index if query_start_index >= 0 else (n - query_length)
                                actual_q_end = actual_q_start + query_length
                                
                                # Safety clamp
                                actual_q_start = max(0, min(actual_q_start, n - 1))
                                actual_q_end = max(actual_q_start + 1, min(actual_q_end, n))
                                query_length = actual_q_end - actual_q_start
                                
                                query = close[actual_q_start:actual_q_end]
                                q_std = np.std(query)
                                if q_std < 1e-10:
                                    q_std = 1.0
                                query_z = ((query - np.mean(query)) / q_std).reshape(-1, 1)

                                # Optional structural filtering: the volatility series is estimated by the .NET side (one value per candle).
                                # Without a usable series (missing, wrong length, non-finite) the search runs without the filter.
                                vol_full = None
                                query_vol = 0.0
                                if use_structural:
                                    supplied_vol = args.get('volatility') if args else None
                                    if supplied_vol is not None and len(supplied_vol) == n:
                                        vol_full = np.asarray(supplied_vol, dtype=float)
                                        if np.all(np.isfinite(vol_full)):
                                            query_vol = float(np.mean(vol_full[actual_q_start:actual_q_end]))
                                        else:
                                            vol_full = None
                                    if vol_full is None:
                                        use_structural = False  # Fallback to non-structural

                                # Determine search range (search for patterns ending BEFORE the query starts)
                                search_start = 0
                                if lookback > 0:
                                    search_start = max(0, actual_q_start - lookback)

                                # Search must end such that its future projection doesn't overlap the exact match,
                                # meaning the window ends before `actual_q_start`. The max start for the candidate is `actual_q_start - query_length`
                                search_end = actual_q_start - query_length

                                if search_end <= search_start:
                                    response["result"] = {"patterns": [], "queryLength": query_length, "debug_info": f"q_s={actual_q_start} n={n} s={search_start} e={search_end} (No historical data before selection)"}
                                else:
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

                                        # Optional volatility penalty
                                        if use_structural and vol_full is not None:
                                            seg_vol = float(np.mean(vol_full[start:end]))
                                            vol_ratio = max(query_vol, seg_vol) / max(min(query_vol, seg_vol), 1e-10)
                                            vol_penalty = np.log1p(vol_ratio - 1)
                                            rmse += vol_penalty * 0.1

                                        prob = float(np.exp(-rmse))

                                        # Temporarily relaxing threshold requirement for debugging
                                        if prob >= threshold or len(candidates) < top_k * 2:
                                            # Extract future path as raw prices
                                            future_end = min(end + future_steps, n)
                                            future_raw = close[end:future_end].tolist()
                                            # Also provide % change from match endpoint
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

                        except ImportError as ie:
                            response["status"] = "error"
                            response["error"] = f"Required package not installed: {ie}"
                        except Exception as e:
                            response["status"] = "error"
                            response["error"] = str(e)

                else:
                    response["status"] = "error"
                    response["error"] = f"Unknown method: {method}"

                # Send response
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
