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
if IS_WINDOWS:
    import win32pipe
    import win32file
    import pywintypes
    import winerror
else:
    import socket

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
            h_pipe = win32pipe.CreateNamedPipe(
                full_pipe_name,
                win32pipe.PIPE_ACCESS_DUPLEX,
                win32pipe.PIPE_TYPE_MESSAGE | win32pipe.PIPE_READMODE_MESSAGE | win32pipe.PIPE_WAIT,
                1, 65536, 65536,
                0,
                None
            )
            print(f"Waiting for connection on {full_pipe_name}...")
            win32pipe.ConnectNamedPipe(h_pipe, None)
            print("Connected!")
            
            def read_pipe(size):
                hr, data = win32file.ReadFile(h_pipe, size)
                # ERROR_MORE_DATA is not a failure: on a PIPE_TYPE_MESSAGE pipe, a single
                # WriteFile call from the .NET side (e.g. the Arrow-serialized candle
                # payload) is one message, and if that message is larger than the
                # requested read size, ReadFile returns the partial chunk it read with
                # hr=ERROR_MORE_DATA to signal "call again for the rest of this same
                # message". Treating that as fatal here aborted large candle transfers
                # (~1300+ rows) partway through with "Received empty response.".
                if hr != 0 and hr != winerror.ERROR_MORE_DATA:
                    raise IOError(f"Read failed: {hr}")
                return data
                
            def write_pipe(data):
                win32file.WriteFile(h_pipe, data)
                
            def close_pipe():
                win32file.CloseHandle(h_pipe)
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
                    hr, data = win32file.ReadFile(h_pipe, 65536)
                    if hr != 0:
                        print(f"Error reading: {hr}")
                        break
                except Exception as e:
                    print(f"Exception reading pipe: {e}")
                    break
                decoded_msg = data.decode('utf-8').strip()
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
                
                elif method == "calculate_egarch":
                    if global_df is None:
                        response["status"] = "error"
                        response["error"] = "No data available. Send data first."
                    else:
                        try:
                            from arch import arch_model
                            import pandas as pd
                            import numpy as np
                            
                            # Extract returns (percentage change * 100 for better scaling)
                            # close_prices = global_df['Close']
                            # returns = 100 * close_prices.pct_change().dropna()
                            
                            # Simply use parameters from args if needed, default to p=1, q=1
                            p = args.get('p', 1)
                            q = args.get('q', 1)
                            
                            # EGARCH requires returns
                            # We assume the dataframe has 'Close'. 
                            # We might need to handle NaN at the beginning
                            returns = 100 * global_df['Close'].pct_change().dropna()
                            
                            model = arch_model(returns, vol='EGARCH', p=p, q=q, dist='Normal')
                            res = model.fit(disp='off')
                            
                            # Conditional Volatility
                            # We need to align it back to the original index
                            cond_vol = res.conditional_volatility
                            
                            # Create a full length array matching original DF
                            # First few are NaN due to pct_change
                            full_vol = np.full(len(global_df), None, dtype=object)
                            
                            # Map resulting volatility back to correct indices
                            # cond_vol index corresponds to returns index
                            # returns index is subset of global_df index
                            
                            # Convert to list for JSON serialization
                            # We can also return as Arrow but JSON is easier for 'result' field for now
                            # If result is large, we should use separate data channel, 
                            # but for volatility (1D array), standard JSON might be okay for <10k rows?
                            # 10k float array in text is ~100KB. Pipe max is large.
                            
                            # Aligning:
                            # If df has N rows, returns has N-1 rows (usually).
                            # arch_model alignments can be tricky.
                            # simplistically:
                            aligned_vol = [None] * len(global_df)
                            
                            # Use index mapping if possible, otherwise assumme last N-1
                            # returns.index should match global_df.index[1:]
                            
                            for idx, val in cond_vol.items():
                                # idx is the index label. If global_df has default integer index:
                                if isinstance(idx, int) and 0 <= idx < len(aligned_vol):
                                    aligned_vol[idx] = float(val)
                                else:
                                    # If index is timestamp or other, this simple array mapping might fail
                                    # For now assume RangeIndex or handle separately
                                    pass
                                    
                            # Fallback: just list of values, let C# handle alignment?
                            # Or better: send list of objects { time: ..., value: ... }? Too heavy.
                            # Just send the values as list.
                            
                            response["result"] = [float(v) if not pd.isna(v) else None for v in cond_vol.tolist()]
                            # Also send start index offset?
                            response["offset"] = len(global_df) - len(cond_vol)

                        except Exception as e:
                            response["status"] = "error"
                            response["error"] = str(e)
                            
                elif method == "calculate_mesa":
                    if global_df is None:
                        response["status"] = "error"
                        response["error"] = "No data available. Send data first."
                    else:
                        try:
                            import numpy as np
                            import scipy.signal
                            
                            fastLimit = float(args.get('fastLimit', 0.5))
                            slowLimit = float(args.get('slowLimit', 0.05))
                            
                            if 'High' in global_df.columns and 'Low' in global_df.columns:
                                prices = (global_df['High'] + global_df['Low']) / 2.0
                            else:
                                prices = global_df['Close']
                            prices = prices.values
                            
                            n = len(prices)
                            mama = np.full(n, None, dtype=object)
                            fama = np.full(n, None, dtype=object)
                            
                            window_size = 32
                            order = 10
                            nfft = 256
                            
                            if n < window_size:
                                for i in range(n):
                                    mama[i] = prices[i]
                                    fama[i] = prices[i]
                            else:
                                current_mama = prices[window_size-1]
                                current_fama = prices[window_size-1]
                                for i in range(window_size-1):
                                    mama[i] = prices[i]
                                    fama[i] = prices[i]
                                    
                                for i in range(window_size-1, n):
                                    window = prices[i-window_size+1:i+1]
                                    window = window - np.mean(window)
                                    
                                    # Burg's Method
                                    Nw = len(window)
                                    rho = np.zeros(order + 1)
                                    a = np.zeros((order + 1, order + 1))
                                    rho[0] = np.sum(window**2) / max(Nw, 1)
                                    ef = np.copy(window)
                                    eb = np.copy(window)
                                    
                                    for m in range(1, order + 1):
                                        num = -2.0 * np.sum(ef[m:] * eb[m-1:-1])
                                        den = np.sum(ef[m:]**2 + eb[m-1:-1]**2)
                                        am = num / den if den != 0 else 0
                                        a[m, m] = am
                                        rho[m] = rho[m-1] * (1.0 - am**2)
                                        for k in range(1, m):
                                            a[m, k] = a[m-1, k] + am * a[m-1, m-k]
                                        ef_new = ef[m:] + am * eb[m-1:-1]
                                        eb_new = eb[m-1:-1] + am * ef[m:]
                                        ef[m:] = ef_new
                                        eb[m-1:-1] = eb_new
                                        
                                    coeffs = a[order, 1:order+1]
                                    
                                    # Spectrum
                                    aa = np.concatenate(([1.0], coeffs))
                                    w, h = scipy.signal.freqz(1.0, aa, worN=nfft, whole=False)
                                    psd = np.abs(h)**2
                                    
                                    min_idx = max(int(nfft * 0.02), 1)
                                    peak_idx = min_idx + np.argmax(psd[min_idx:])
                                    peak_freq = w[peak_idx] / (2 * np.pi)
                                    period = 1.0 / peak_freq if peak_freq > 0 else 0
                                    
                                    alpha = 2.0 / (period + 1.0) if period > 0 else fastLimit
                                    alpha = max(slowLimit, min(fastLimit, alpha))
                                    
                                    current_mama = float(alpha * prices[i] + (1 - alpha) * current_mama)
                                    current_fama = float(0.5 * alpha * current_mama + (1 - 0.5 * alpha) * current_fama)
                                    
                                    mama[i] = current_mama
                                    fama[i] = current_fama
                                    
                            response["result"] = {
                                "mama": [float(x) if x is not None else None for x in mama],
                                "fama": [float(x) if x is not None else None for x in fama]
                            }
                        except Exception as e:
                            response["status"] = "error"
                            response["error"] = str(e)

                elif method == "calculate_fft_cycle":
                    if global_df is None:
                        response["status"] = "error"
                        response["error"] = "No data available. Send data first."
                    else:
                        try:
                            import numpy as np

                            def _nearest_power_of_two(n):
                                n = int(n)
                                if n < 1:
                                    return 1
                                lower = 1 << (n.bit_length() - 1)
                                upper = lower << 1
                                return lower if (n - lower) <= (upper - n) else upper

                            window_size = _nearest_power_of_two(args.get('windowSize', 64))

                            if 'High' in global_df.columns and 'Low' in global_df.columns:
                                prices = (global_df['High'] + global_df['Low']) / 2.0
                            else:
                                prices = global_df['Close']
                            prices = prices.values

                            n = len(prices)
                            cycle = np.full(n, None, dtype=object)
                            strength = np.full(n, None, dtype=object)
                            oscillator = np.full(n, None, dtype=object)

                            if n >= window_size and window_size >= 4:
                                hanning_window = np.hanning(window_size)

                                for i in range(window_size - 1, n):
                                    segment = prices[i - window_size + 1:i + 1]

                                    # Detrend: remove DC component (mean)
                                    detrended = segment - np.mean(segment)

                                    # Apply Hanning window to reduce edge noise
                                    windowed = detrended * hanning_window

                                    # Spectrum via real FFT
                                    spectrum = np.fft.rfft(windowed)
                                    magnitude = np.abs(spectrum)

                                    # Find the dominant frequency bin, excluding DC (k=0)
                                    if len(magnitude) > 1:
                                        k = int(np.argmax(magnitude[1:]) + 1)
                                        cycle[i] = float(window_size) / k if k > 0 else None

                                        # Cycle Strength: how much the dominant peak stands
                                        # out above the average of the rest of the spectrum
                                        # (excluding DC). >>1 means a genuine cycle; ~1 means
                                        # the peak is indistinguishable from noise floor.
                                        surrounding = np.mean(magnitude[1:])
                                        strength[i] = float(magnitude[k] / surrounding) if surrounding > 0 else None

                                        # Cycle Oscillator: instantaneous phase of the dominant
                                        # cycle's complex coefficient, projected onto a cosine
                                        # and scaled so a pure sinusoid of the window's amplitude
                                        # yields an oscillator amplitude near that same scale.
                                        phase = np.angle(spectrum[k])
                                        normalized_amplitude = magnitude[k] / (window_size / 2.0)
                                        oscillator[i] = float(normalized_amplitude * np.cos(phase))

                            response["result"] = {
                                "cycle": [float(x) if x is not None else None for x in cycle],
                                "strength": [float(x) if x is not None else None for x in strength],
                                "oscillator": [float(x) if x is not None else None for x in oscillator]
                            }
                        except Exception as e:
                            response["status"] = "error"
                            response["error"] = str(e)

                elif method == "calculate_fourier_transform":
                    if global_df is None:
                        response["status"] = "error"
                        response["error"] = "No data available. Send data first."
                    else:
                        try:
                            import numpy as np

                            target_period = int(args.get('targetPeriod', 20))
                            if target_period < 2:
                                target_period = 2

                            if 'High' in global_df.columns and 'Low' in global_df.columns:
                                prices = (global_df['High'] + global_df['Low']) / 2.0
                            else:
                                prices = global_df['Close']
                            prices = prices.values

                            n = len(prices)
                            amplitude = np.full(n, None, dtype=object)

                            # Goertzel single-bin DFT: k=1 over a window of size target_period
                            # corresponds to exactly one full cycle at the target period.
                            window_size = target_period
                            omega = 2.0 * np.pi / window_size
                            cw = np.cos(omega)
                            sw = np.sin(omega)
                            coeff = 2.0 * cw

                            if n >= window_size:
                                for i in range(window_size - 1, n):
                                    segment = prices[i - window_size + 1:i + 1]

                                    # Detrend: remove DC component to avoid leakage into the target bin
                                    detrended = segment - np.mean(segment)

                                    q1 = 0.0
                                    q2 = 0.0
                                    for sample in detrended:
                                        q0 = coeff * q1 - q2 + sample
                                        q2 = q1
                                        q1 = q0

                                    real = q1 - q2 * cw
                                    imag = q2 * sw
                                    magnitude = np.sqrt(real * real + imag * imag)

                                    # Normalize so a pure sinusoid of amplitude A yields ~A.
                                    amplitude[i] = float(2.0 * magnitude / window_size)

                            response["result"] = {
                                "amplitude": [float(x) if x is not None else None for x in amplitude]
                            }
                        except Exception as e:
                            response["status"] = "error"
                            response["error"] = str(e)

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

                                # No windowing, no detrend: unlike calculate_fft_cycle, this
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

                elif method == "detect_patterns":
                    if global_df is None:
                        response["status"] = "error"
                        response["error"] = "No data available. Send data first."
                    else:
                        try:
                            import numpy as np
                            from tslearn.metrics import dtw as tslearn_dtw

                            # --- Configuration ---
                            min_window = args.get('minWindow', 20) if args else 20
                            max_window = args.get('maxWindow', 60) if args else 60
                            window_step = args.get('windowStep', 5) if args else 5
                            threshold = args.get('threshold', 0.5) if args else 0.5
                            warping_radius = args.get('warpingRadius', 0) if args else 0
                            short_span_alpha = args.get('shortSpanPenaltyAlpha', 0.5) if args else 0.5

                            close = global_df['Close'].values.astype(float)
                            n = len(close)

                            # --- Canonical Pattern Templates (normalized shapes) ---
                            # Each template is a 1-D array representing the idealized shape.
                            # Values are abstract (Z-Score scale), stretched to window via interpolation.
                            templates = {
                                "HeadAndShoulders": np.array([0, 1, 0.3, 1.5, 0.3, 1, 0], dtype=float),
                                "InverseHeadAndShoulders": np.array([0, -1, -0.3, -1.5, -0.3, -1, 0], dtype=float),
                                "DoubleTop": np.array([0, 1, 0.3, 1, 0], dtype=float),
                                "DoubleBottom": np.array([0, -1, -0.3, -1, 0], dtype=float),
                                "TripleTop": np.array([0, 1, 0.3, 1, 0.3, 1, 0], dtype=float),
                                "TripleBottom": np.array([0, -1, -0.3, -1, -0.3, -1, 0], dtype=float),
                            }

                            detected = []

                            for pattern_name, template in templates.items():
                                best_prob = 0.0
                                best_start = 0
                                best_end = 0

                                for w in range(min_window, min(max_window + 1, n + 1), window_step):
                                    # Interpolate template to window size
                                    x_template = np.linspace(0, 1, len(template))
                                    x_window = np.linspace(0, 1, w)
                                    stretched = np.interp(x_window, x_template, template)
                                    
                                    # Z-Score normalize the stretched template to match segment scaling
                                    t_std = np.std(stretched)
                                    if t_std > 1e-10:
                                        stretched = (stretched - np.mean(stretched)) / t_std

                                    stretched_2d = stretched.reshape(-1, 1)

                                    for start in range(0, n - w + 1, window_step):
                                        segment = close[start:start + w]

                                        # Z-Score normalization
                                        std = np.std(segment)
                                        if std < 1e-10:
                                            continue
                                        z_segment = (segment - np.mean(segment)) / std
                                        z_segment_2d = z_segment.reshape(-1, 1)

                                        # DTW distance (tslearn returns sqrt of sum of squared distances)
                                        if warping_radius > 0:
                                            dist = tslearn_dtw(z_segment_2d, stretched_2d, global_constraint="sakoe_chiba", sakoe_chiba_radius=warping_radius)
                                        else:
                                            dist = tslearn_dtw(z_segment_2d, stretched_2d)

                                        # Convert distance to probability based on RMSE
                                        rmse = dist / np.sqrt(w)
                                        prob = float(np.exp(-rmse))

                                        # Short Span Penalty (exponentially penalize patterns formed over too few bars)
                                        if short_span_alpha > 0 and w < max_window:
                                            span_ratio = w / max_window
                                            prob *= (span_ratio ** short_span_alpha)

                                        if prob > best_prob:
                                            best_prob = prob
                                            best_start = start
                                            best_end = start + w - 1

                                if best_prob >= threshold:
                                    detected.append({
                                        "name": pattern_name,
                                        "probability": round(best_prob, 4),
                                        "startIndex": int(best_start),
                                        "endIndex": int(best_end)
                                    })

                            # Sort by probability descending
                            detected.sort(key=lambda x: x["probability"], reverse=True)
                            response["result"] = {"patterns": detected}

                        except ImportError as ie:
                            response["status"] = "error"
                            response["error"] = f"Required package not installed: {ie}. Run: pip install tslearn"
                        except Exception as e:
                            response["status"] = "error"
                            response["error"] = str(e)

                elif method == "calculate_structural_dtw":
                    if global_df is None:
                        response["status"] = "error"
                        response["error"] = "No data available. Send data first."
                    else:
                        try:
                            import numpy as np
                            from arch import arch_model
                            import scipy.signal
                            from tslearn.metrics import dtw as tslearn_dtw

                            top_k = args.get('topK', 5) if args else 5
                            threshold = args.get('threshold', 0.3) if args else 0.3
                            future_steps = args.get('futureSteps', 20) if args else 20
                            warping_radius = args.get('warpingRadius', 0) if args else 0

                            close = global_df['Close'].values.astype(float)
                            n = len(close)

                            if n < 60:
                                response["status"] = "error"
                                response["error"] = f"Insufficient data: need >= 60 candles, got {n}"
                            else:
                                # --- 1. MESA: Estimate dominant cycle period ---
                                mesa_window = min(32, n // 2)
                                mesa_order = 10
                                mesa_nfft = 256

                                if 'High' in global_df.columns and 'Low' in global_df.columns:
                                    mesa_prices = ((global_df['High'] + global_df['Low']) / 2.0).values.astype(float)
                                else:
                                    mesa_prices = close

                                # Use last mesa_window points for current cycle estimate
                                window_data = mesa_prices[-(mesa_window):]
                                window_data = window_data - np.mean(window_data)

                                # Burg's Method for spectral estimation
                                Nw = len(window_data)
                                rho = np.zeros(mesa_order + 1)
                                a = np.zeros((mesa_order + 1, mesa_order + 1))
                                rho[0] = np.sum(window_data**2) / max(Nw, 1)
                                ef = np.copy(window_data)
                                eb = np.copy(window_data)

                                for m in range(1, mesa_order + 1):
                                    num = -2.0 * np.sum(ef[m:] * eb[m-1:-1])
                                    den = np.sum(ef[m:]**2 + eb[m-1:-1]**2)
                                    am = num / den if den != 0 else 0
                                    a[m, m] = am
                                    rho[m] = rho[m-1] * (1.0 - am**2)
                                    for k in range(1, m):
                                        a[m, k] = a[m-1, k] + am * a[m-1, m-k]
                                    ef_new = ef[m:] + am * eb[m-1:-1]
                                    eb_new = eb[m-1:-1] + am * ef[m:]
                                    ef[m:] = ef_new
                                    eb[m-1:-1] = eb_new

                                coeffs = a[mesa_order, 1:mesa_order+1]
                                aa = np.concatenate(([1.0], coeffs))
                                w, h = scipy.signal.freqz(1.0, aa, worN=mesa_nfft, whole=False)
                                psd = np.abs(h)**2
                                min_idx = max(int(mesa_nfft * 0.02), 1)
                                peak_idx = min_idx + np.argmax(psd[min_idx:])
                                peak_freq = w[peak_idx] / (2 * np.pi)
                                dominant_period = int(1.0 / peak_freq) if peak_freq > 0 else 20
                                dominant_period = max(10, min(dominant_period, n // 3))

                                # --- 2. EGARCH: Estimate conditional volatility ---
                                returns = 100 * np.diff(np.log(close + 1e-10))
                                try:
                                    model = arch_model(returns, vol='EGARCH', p=1, q=1, dist='Normal')
                                    res = model.fit(disp='off')
                                    cond_vol = res.conditional_volatility.values
                                    # Pad to match close length (returns has n-1 elements)
                                    vol_full = np.concatenate(([cond_vol[0]], cond_vol))
                                except Exception:
                                    # Fallback: use rolling std as volatility proxy
                                    vol_window = min(20, n // 4)
                                    vol_full = np.array([
                                        np.std(returns[max(0, i-vol_window):i+1]) if i > 0 else 0.0
                                        for i in range(len(returns))
                                    ])
                                    vol_full = np.concatenate(([vol_full[0]], vol_full))

                                # --- 3. Structural DTW: Compare current segment with history ---
                                dtw_window = dominant_period
                                query_start = n - dtw_window
                                if query_start < 0:
                                    response["status"] = "error"
                                    response["error"] = "Query window exceeds data length"
                                else:
                                    query_segment = close[query_start:]
                                    q_std = np.std(query_segment)
                                    if q_std < 1e-10:
                                        q_std = 1.0
                                    query_z = ((query_segment - np.mean(query_segment)) / q_std).reshape(-1, 1)
                                    query_vol = np.mean(vol_full[query_start:])

                                    candidates = []
                                    step = max(1, dtw_window // 4)

                                    for start in range(0, query_start - dtw_window - future_steps, step):
                                        end = start + dtw_window
                                        segment = close[start:end]
                                        s_std = np.std(segment)
                                        if s_std < 1e-10:
                                            continue
                                        segment_z = ((segment - np.mean(segment)) / s_std).reshape(-1, 1)

                                        # DTW distance
                                        if warping_radius > 0:
                                            dist = tslearn_dtw(query_z, segment_z, global_constraint="sakoe_chiba", sakoe_chiba_radius=warping_radius)
                                        else:
                                            dist = tslearn_dtw(query_z, segment_z)
                                        
                                        # Ensure distance metric scales with sqrt(W) not W
                                        rmse = dist / np.sqrt(dtw_window)

                                        # Volatility penalty: penalize segments with very different volatility regime
                                        seg_vol = np.mean(vol_full[start:end])
                                        vol_ratio = max(query_vol, seg_vol) / max(min(query_vol, seg_vol), 1e-10)
                                        vol_penalty = np.log1p(vol_ratio - 1)  # 0 when identical, grows with divergence

                                        # Combined structural distance (RMSE scale)
                                        structural_rmse = rmse + vol_penalty * 0.1

                                        # Probability score (inverse exponential)
                                        prob = float(np.exp(-structural_rmse))

                                        if prob >= threshold:
                                            # Extract future path (price changes from end of match)
                                            future_end = min(end + future_steps, n)
                                            future_prices = close[end:future_end]
                                            if len(future_prices) > 0:
                                                # Normalize future path as percentage change from match end
                                                base_price = close[end - 1]
                                                future_pct = ((future_prices / base_price) - 1.0) * 100.0
                                                candidates.append({
                                                    "distance": round(float(structural_dist), 4),
                                                    "probability": round(prob, 4),
                                                    "startIndex": int(start),
                                                    "endIndex": int(end - 1),
                                                    "futurePath": [round(float(v), 4) for v in future_pct]
                                                })

                                    # Sort by distance (ascending) and take top-K
                                    candidates.sort(key=lambda x: x["distance"])
                                    top_matches = candidates[:top_k]

                                    response["result"] = {
                                        "dominantPeriod": dominant_period,
                                        "dtwWindow": dtw_window,
                                        "queryVolatility": round(float(query_vol), 4),
                                        "matches": top_matches
                                    }

                        except ImportError as ie:
                            response["status"] = "error"
                            response["error"] = f"Required package not installed: {ie}"
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
                            from tslearn.metrics import dtw as tslearn_dtw

                            lookback = args.get('lookback', 0) if args else 0  # 0 = use all history
                            top_k = args.get('topK', 5) if args else 5
                            future_steps = args.get('futureSteps', 20) if args else 20
                            threshold = args.get('threshold', 0.3) if args else 0.3
                            query_start_index = args.get('queryStartIndex', -1) if args else -1
                            query_length = args.get('queryLength', 30) if args else 30
                            use_structural = args.get('useStructural', False) if args else False
                            warping_radius = args.get('warpingRadius', 0) if args else 0

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

                                # Optional: compute volatility for structural filtering
                                vol_full = None
                                query_vol = 0.0
                                if use_structural:
                                    try:
                                        from arch import arch_model
                                        returns = 100 * np.diff(np.log(close + 1e-10))
                                        model = arch_model(returns, vol='EGARCH', p=1, q=1, dist='Normal')
                                        res = model.fit(disp='off')
                                        cond_vol = res.conditional_volatility.values
                                        vol_full = np.concatenate(([cond_vol[0]], cond_vol))
                                        query_vol = float(np.mean(vol_full[actual_q_start:actual_q_end]))
                                    except Exception:
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

                                        if warping_radius > 0:
                                            dist = float(tslearn_dtw(query_z, segment_z, global_constraint="sakoe_chiba", sakoe_chiba_radius=warping_radius))
                                        else:
                                            dist = float(tslearn_dtw(query_z, segment_z))
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

                elif method == "calculate_structural_dtw_oscillator":
                    if global_df is None:
                        response["status"] = "error"
                        response["error"] = "No data available. Send data first."
                    else:
                        try:
                            import numpy as np
                            from tslearn.metrics import dtw as tslearn_dtw

                            period = args.get('period', 14) if args else 14
                            lag = args.get('lag', 14) if args else 14
                            warping_radius = args.get('warpingRadius', 0) if args else 0

                            close = global_df['Close'].values.astype(float)
                            n = len(close)

                            if n < period + lag:
                                response["status"] = "error"
                                response["error"] = f"Insufficient data: need >= {period + lag} candles, got {n}"
                            else:
                                distances = np.full(n, None)

                                # Calculate DTW distance using a rolling window
                                for i in range(period + lag, n + 1):
                                    current_segment = close[i - period:i]
                                    lagged_segment = close[i - period - lag:i - lag]
                                    
                                    curr_std = np.std(current_segment)
                                    if curr_std < 1e-10: curr_std = 1.0
                                    curr_z = ((current_segment - np.mean(current_segment)) / curr_std).reshape(-1, 1)

                                    lag_std = np.std(lagged_segment)
                                    if lag_std < 1e-10: lag_std = 1.0
                                    lag_z = ((lagged_segment - np.mean(lagged_segment)) / lag_std).reshape(-1, 1)

                                    if warping_radius > 0:
                                        dist = tslearn_dtw(curr_z, lag_z, global_constraint="sakoe_chiba", sakoe_chiba_radius=warping_radius)
                                    else:
                                        dist = tslearn_dtw(curr_z, lag_z)
                                        
                                    distances[i - 1] = float(dist)

                                response["result"] = [round(float(d), 4) if d is not None else None for d in distances]
                                response["offset"] = period + lag - 1

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
                win32file.WriteFile(h_pipe, response_json.encode('utf-8'))
                
            except json.JSONDecodeError as e:
                snippet = decoded_msg[:200] if decoded_msg else "empty"
                err_resp = json.dumps({"status": "error", "error": f"Invalid JSON: {e}. Snip: {snippet}"}) + "\n"
                win32file.WriteFile(h_pipe, err_resp.encode('utf-8'))
                
    except Exception as e:
        print(f"Server error: {e}")
    finally:
        try:
            win32file.CloseHandle(h_pipe)
        except:
            pass

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--pipe", required=True, help="Named pipe name")
    args = parser.parse_args()
    
    run_server(args.pipe)
