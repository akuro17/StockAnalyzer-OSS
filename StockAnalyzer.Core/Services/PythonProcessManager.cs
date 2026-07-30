using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Python.Included;

namespace StockAnalyzer.Core.Services
{
    public class PythonProcessManager : IResilienceStateProvider, IAsyncDisposable
    {
        private Process? _pythonProcess;
        private readonly string _pythonScriptPath;
        private readonly string _pipeName;
        private NamedPipeClientStream? _pipeClient;
        private StreamReader? _pipeReader;
        private StreamWriter? _pipeWriter;
        private bool _isConnected;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly IStockAnalyzerSettings _settings;
        private CancellationTokenSource? _healthCheckCts;
        private Task? _healthCheckTask;
        private readonly ResiliencePipeline _resiliencePipeline;
        private CircuitState _currentCircuitState = CircuitState.Closed;

        /// <inheritdoc />
        public CircuitState CurrentState => _currentCircuitState;

        /// <inheritdoc />
        public event EventHandler<CircuitStateChangedEventArgs>? StateChanged;

        public PythonProcessManager(IStockAnalyzerSettings settings)
        {
            _settings = settings;
            _pipeName = $"{settings.PipeName}_{Guid.NewGuid():N}";
            
            // Script directory from settings (or default)
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var scriptDir = Path.Combine(baseDir, _settings.PythonScriptDirectory);
            _pythonScriptPath = Path.Combine(scriptDir, _settings.PythonServerScriptName);

            _resiliencePipeline = BuildResiliencePipeline();
        }

        private ResiliencePipeline BuildResiliencePipeline()
        {
            return new ResiliencePipelineBuilder()
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = _settings.PythonMaxRetries,
                    BackoffType = DelayBackoffType.Exponential,
                    Delay = TimeSpan.FromMilliseconds(_settings.PythonBackoffMs),
                    ShouldHandle = new PredicateBuilder()
                        .Handle<IOException>()
                        .Handle<InvalidOperationException>()
                        .Handle<ObjectDisposedException>()
                        .Handle<UnauthorizedAccessException>(),
                    OnRetry = args =>
                    {
                        Debug.WriteLine($"[PythonProcessManager] Retry attempt {args.AttemptNumber + 1}/{_settings.PythonMaxRetries}. Exception: {args.Outcome.Exception?.Message}");
                        _isConnected = false;
                        return ValueTask.CompletedTask;
                    }
                })
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    FailureRatio = _settings.CircuitBreakerFailureRatio,
                    MinimumThroughput = _settings.CircuitBreakerMinimumThroughput,
                    BreakDuration = TimeSpan.FromMilliseconds(_settings.CircuitBreakerBreakDurationMs),
                    SamplingDuration = TimeSpan.FromMilliseconds(_settings.CircuitBreakerSamplingDurationMs),
                    ShouldHandle = new PredicateBuilder()
                        .Handle<IOException>()
                        .Handle<InvalidOperationException>()
                        .Handle<ObjectDisposedException>()
                        .Handle<UnauthorizedAccessException>(),
                    OnOpened = args =>
                    {
                        TransitionCircuitState(CircuitState.Open, args.Outcome.Exception);
                        return ValueTask.CompletedTask;
                    },
                    OnClosed = args =>
                    {
                        TransitionCircuitState(CircuitState.Closed);
                        return ValueTask.CompletedTask;
                    },
                    OnHalfOpened = args =>
                    {
                        TransitionCircuitState(CircuitState.HalfOpen);
                        return ValueTask.CompletedTask;
                    }
                })
                .Build();
        }

        private void TransitionCircuitState(CircuitState newState, Exception? triggeringException = null)
        {
            var oldState = _currentCircuitState;
            if (oldState == newState) return;

            _currentCircuitState = newState;
            Debug.WriteLine($"[PythonProcessManager] Circuit breaker state: {oldState} -> {newState}");
            StateChanged?.Invoke(this, new CircuitStateChangedEventArgs(oldState, newState, triggeringException));
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_isConnected && !IsProcessDead()) return;

            await _lock.WaitAsync(cancellationToken);
            try
            {
                if (_isConnected && !IsProcessDead()) return;

                CleanupConnection();

                string pythonExe;
                if (!string.IsNullOrEmpty(_settings.PythonPath))
                {
                    pythonExe = _settings.PythonPath;
                    if (!File.Exists(pythonExe))
                    {
                        throw new FileNotFoundException($"Custom Python executable not found at specified path: {pythonExe}");
                    }
                }
                else
                {
                    // Ensure Python is set up
                    await Installer.SetupPython();

                    // Locate python.exe / python
                    // Python.Included installs to %LOCALAPPDATA%/python-3.11.3/... or similar
                    // We can use Installer.InstallPath
                    var pythonHome = Installer.InstallPath;
                    var pythonExeName = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? "python.exe" : "python";
                    pythonExe = Path.Combine(pythonHome, pythonExeName);

                    if (!File.Exists(pythonExe))
                    {
                        // Fallback: Check for versioned subdirectory (e.g. python-3.13.0-embed-amd64) inside InstallPath
                        // This seems to happen with newer Python.Included versions or specific environments
                        var subdirs = Directory.GetDirectories(pythonHome, "python-*-embed-*");
                        foreach (var dir in subdirs)
                        {
                            var candidate = Path.Combine(dir, pythonExeName);
                            if (File.Exists(candidate))
                            {
                                pythonExe = candidate;
                                break;
                            }
                        }
                    }

                    if (!File.Exists(pythonExe))
                    {
                        throw new FileNotFoundException($"Python executable not found at {pythonExe} or subdirectories of {pythonHome}");
                    }
                }

                if (!File.Exists(_pythonScriptPath))
                {
                    // If script doesn't exist yet, we can't run it. Caller should ensure it exists.
                    // For now, checks are strict.
                     throw new FileNotFoundException($"Python server script not found at {_pythonScriptPath}");
                }

                // Start Python Process
                var startInfo = new ProcessStartInfo
                {
                    FileName = pythonExe,
                    Arguments = $"-u \"{_pythonScriptPath}\" --pipe \"{_pipeName}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(_pythonScriptPath)
                };
                startInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

                _pythonProcess = new Process { StartInfo = startInfo };
                _pythonProcess.OutputDataReceived += (s, e) => { 
                    if (!string.IsNullOrEmpty(e.Data)) 
                    {
                        Debug.WriteLine($"[Python OUT]: {e.Data}");
                    }
                };
                _pythonProcess.ErrorDataReceived += (s, e) => { 
                    if (!string.IsNullOrEmpty(e.Data)) 
                    {
                        Debug.WriteLine($"[Python ERR]: {e.Data}");
                    }
                };
                _pythonProcess.Start();

                _pythonProcess.BeginOutputReadLine();
                _pythonProcess.BeginErrorReadLine();

                // Connect to Pipe
                _pipeClient = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                
                // Wait for connection with timeout
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(_settings.PipeConnectionTimeoutMs);

                try 
                {
                    while (!_pipeClient.IsConnected)
                    {
                        if (_pythonProcess.HasExited)
                        {
                            var exitCode = _pythonProcess.ExitCode;
                            throw new Exception($"Python process exited immediately with code {exitCode}. Check python logs.");
                        }

                        if (cts.Token.IsCancellationRequested)
                        {
                            throw new TimeoutException($"Pipe connection timed out after {_settings.PipeConnectionTimeoutMs}ms.");
                        }

                        try { await _pipeClient.ConnectAsync(_settings.PipeConnectPollIntervalMs, cts.Token); }
                        catch (TimeoutException) { /* retry in loop */ }
                    }
                }
                catch (OperationCanceledException)
                {
                    if (_pythonProcess.HasExited) throw new Exception($"Python process exited immediately with code {_pythonProcess.ExitCode}.");
                    throw new TimeoutException($"Pipe connection timed out after {_settings.PipeConnectionTimeoutMs}ms.");
                }

                _pipeReader = new StreamReader(_pipeClient, Encoding.UTF8, leaveOpen: true);
                _pipeWriter = new StreamWriter(_pipeClient, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

                _isConnected = true;

                if (_healthCheckCts == null)
                {
                    _healthCheckCts = new CancellationTokenSource();
                    _healthCheckTask = Task.Run(() => HealthMonitorLoopAsync(_healthCheckCts.Token));
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        private readonly struct CommandRequest
        {
            public string method { get; }
            public object? args { get; }
            public CommandRequest(string method, object? args)
            {
                this.method = method;
                this.args = args;
            }
        }

        public Task<string> SendCommandAsync(string method, object? args = null, CancellationToken cancellationToken = default)
        {
            return ExecuteWithResilienceAsync(
                static async (context, state) =>
                {
                    var manager = state.manager;
                    var request = new CommandRequest(state.method, state.args);

                    var jsonRequest = JsonSerializer.Serialize(request);
                    await manager._pipeWriter!.WriteAsync((jsonRequest + "\n").AsMemory(), context.CancellationToken);
                    await manager._pipeWriter.FlushAsync(); // Ensure JSON is sent

                    var responseJson = await manager._pipeReader!.ReadLineAsync(context.CancellationToken);
                    
                    if (string.IsNullOrEmpty(responseJson))
                    {
                        throw new IOException("Received empty response from Python process.");
                    }

                    return responseJson;
                },
                (manager: this, method: method, args: args),
                cancellationToken);
        }

        public Task<string> WaitForResponseAsync(CancellationToken cancellationToken = default)
        {
             return ExecuteWithResilienceAsync(
                 static async (context, manager) =>
                 {
                     var responseJson = await manager._pipeReader!.ReadLineAsync(context.CancellationToken);
                     if (string.IsNullOrEmpty(responseJson)) throw new IOException("Received empty response.");
                     return responseJson;
                 },
                 this,
                 cancellationToken);
        }

        public Task SendBinaryDataAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            return ExecuteWithResilienceAsync(
                static async (context, state) =>
                {
                    var manager = state.manager;
                    await manager._pipeWriter!.FlushAsync(); // Ensure any pending text is gone
                    await manager._pipeClient!.WriteAsync(state.data, context.CancellationToken);
                    await manager._pipeClient.FlushAsync();
                    return true;
                },
                (manager: this, data: data),
                cancellationToken);
        }

        private async Task<T> ExecuteWithResilienceAsync<TState, T>(
            Func<ResilienceContext, TState, ValueTask<T>> callback,
            TState state,
            CancellationToken cancellationToken)
        {
            var context = Polly.ResilienceContextPool.Shared.Get(cancellationToken);
            try
            {
                return await _resiliencePipeline.ExecuteAsync<T, TState>(callback, context, state).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                CleanupConnection();
                if (ex is BrokenCircuitException bce)
                {
                    Debug.WriteLine($"[PythonProcessManager] Circuit breaker is open. Fast-failing. {ex.Message}");
                    throw new PythonUnavailableException(
                        "Python service is temporarily unavailable (circuit breaker open). Feature will auto-recover when service is restored.",
                        bce);
                }
                throw;
            }
            finally
            {
                Polly.ResilienceContextPool.Shared.Return(context);
            }
        }

        private async Task HealthMonitorLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_settings.PythonHealthCheckIntervalMs, token);

                    if (_isConnected && IsProcessDead())
                    {
                        Debug.WriteLine("[PythonProcessManager] Health check failed. Process is dead. Attempting Self-Healing via resilience pipeline...");

                        try
                        {
                            var context = Polly.ResilienceContextPool.Shared.Get(token);
                            try
                            {
                                await _resiliencePipeline.ExecuteAsync<bool, PythonProcessManager>(
                                    static async (ctx, manager) => { await manager.StartAsync(ctx.CancellationToken); return true; },
                                    context,
                                    this).ConfigureAwait(false);
                            }
                            finally
                            {
                                Polly.ResilienceContextPool.Shared.Return(context);
                            }
                            Debug.WriteLine("[PythonProcessManager] Self-Healing successful.");
                        }
                        catch (BrokenCircuitException ex)
                        {
                            Debug.WriteLine($"[PythonProcessManager] Self-Healing blocked by circuit breaker: {ex.Message}");
                            _isConnected = false;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            Debug.WriteLine($"[PythonProcessManager] Self-Healing failed after all retries: {ex.Message}");
                            _isConnected = false;
                        }
                    }
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PythonProcessManager] Health check loop exception: {ex.Message}");
                }
            }
        }

        private void CleanupConnection()
        {
            _isConnected = false;
            
            try { _pipeReader?.Dispose(); } catch { }
            _pipeReader = null;

            try { _pipeWriter?.Dispose(); } catch { }
            _pipeWriter = null;

            try { _pipeClient?.Dispose(); } catch { }
            _pipeClient = null;
            
            if (_pythonProcess != null)
            {
                if (!IsProcessDead())
                {
                    try { _pythonProcess.Kill(); } catch { }
                }
                try { _pythonProcess.Dispose(); } catch { }
                _pythonProcess = null;
            }
        }

        private bool IsProcessDead()
        {
            if (_pythonProcess == null) return true;
            try
            {
                return _pythonProcess.HasExited;
            }
            catch
            {
                return true;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _healthCheckCts?.Cancel();
            try 
            { 
                if (_healthCheckTask != null)
                {
                    var timeoutTask = Task.Delay(_settings.DisposeWaitMs);
                    var completedTask = await Task.WhenAny(_healthCheckTask, timeoutTask).ConfigureAwait(false);
                    if (completedTask != _healthCheckTask)
                    {
                        Debug.WriteLine("[PythonProcessManager] Health check task did not complete nicely within timeout, forcing cleanup.");
                    }
                }
            } 
            catch { }

            CleanupConnection();
            _lock.Dispose();
            _healthCheckCts?.Dispose();
        }
    }
}
