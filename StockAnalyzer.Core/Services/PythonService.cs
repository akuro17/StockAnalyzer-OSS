using System;
using System.Threading;
using System.Threading.Tasks;
using Python.Runtime;
using Polly;
using Polly.Retry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace StockAnalyzer.Core.Services
{
    public class PythonService : IPythonService, IAsyncDisposable
    {
        private bool _isInitialized = false;
        private bool _isInitializing = false;
        private readonly System.Threading.SemaphoreSlim _initLock = new(1, 1);
        private readonly SemaphoreSlim _transactionLock = new(1, 1);
        private readonly System.IO.MemoryStream _sharedIpcStream = new(); // Used under _transactionLock
        private PythonProcessManager? _processManager;
        private readonly IStockAnalyzerSettings _settings;
        private readonly ResiliencePipeline _resiliencePipeline;
        private readonly ILogger<PythonService> _logger;
        private readonly ITimeFrameParquetGenerator? _timeframeGenerator;
        private bool _hasPromptedUpdate = false;

        private string PythonExecutableName => System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? "python.exe" : "python3";

        private static readonly System.Text.RegularExpressions.Regex ValidPythonModuleNameRegex =
            new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$",
                System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        private void LogSyncActivity(string message, LogLevel level = LogLevel.Information)
        {
            _logger.Log(level, "{Message}", message);
            System.Diagnostics.Debug.WriteLine($"[SyncLog] {message}");
        }

        public bool IsInitializing => _isInitializing;
        public System.Func<Task<PythonSetupDecision>>? SetupDecisionProvider { get; set; }
        public System.Func<Task<PythonSetupDecision>>? UpdateDecisionProvider { get; set; }
        public bool IsUpdateSuppressed { get; set; }
        public System.Action<System.IProgress<string>>? SetupProgressStarted { get; set; }
        public System.Action? SetupProgressFinished { get; set; }

        public PythonService(
            IStockAnalyzerSettings settings,
            ILogger<PythonService>? logger = null,
            ITimeFrameParquetGenerator? timeframeGenerator = null)
        {
            _settings = settings;
            _logger = logger ?? NullLogger<PythonService>.Instance;
            _timeframeGenerator = timeframeGenerator;
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
                    ShouldHandle = args => ValueTask.FromResult(ShouldRetry(args.Outcome.Exception, args.Context)),
                    OnRetry = args =>
                    {
                        LogSyncActivity($"[PythonService] Retry attempt {args.AttemptNumber + 1}/{_settings.PythonMaxRetries}. Exception: {args.Outcome.Exception?.Message}");
                        return ValueTask.CompletedTask;
                    }
                })
                .Build();
        }

        /// <summary>Timeouts seen so far by the transaction that owns the <see cref="ResilienceContext"/> (absent for other pipelines).</summary>
        private static readonly ResiliencePropertyKey<int> TransactionTimeoutCountKey = new("StockAnalyzer.TransactionTimeoutCount");

        /// <summary>
        /// Connection errors are always worth a re-run. A timeout is re-run only while the transaction has timed out at most
        /// <c>PythonMaxTimeoutRetries</c> times, so a slow (not hung) computation is not repeated for every allowed attempt.
        /// Pipelines that do not track timeouts (setup, update) keep re-running on every timeout.
        /// </summary>
        private bool ShouldRetry(Exception? exception, ResilienceContext context)
        {
            if (exception is System.IO.IOException or System.InvalidOperationException)
            {
                return true;
            }

            if (exception is System.TimeoutException)
            {
                return !context.Properties.TryGetValue(TransactionTimeoutCountKey, out int timeouts)
                    || timeouts <= _settings.PythonMaxTimeoutRetries;
            }

            return false;
        }

        public async Task<T> ExecuteTransactionAsync<T>(Func<Task<T>> action)
        {
            await _transactionLock.WaitAsync();
            var context = ResilienceContextPool.Shared.Get();
            try
            {
                context.Properties.Set(TransactionTimeoutCountKey, 0);
                // The whole transaction (data upload + calculation) is the unit of retry: a lost connection also loses the
                // data Python holds, so a partial replay cannot succeed. Retries reconnect first because the failed attempt
                // tore the connection down.
                bool firstAttempt = true;
                return await _resiliencePipeline.ExecuteAsync(async ctx =>
                {
                    if (!firstAttempt)
                    {
                        await InitializeExternalProcessAsync();
                    }

                    firstAttempt = false;
                    try
                    {
                        return await action();
                    }
                    catch (TimeoutException)
                    {
                        ctx.Properties.TryGetValue(TransactionTimeoutCountKey, out int timeouts);
                        ctx.Properties.Set(TransactionTimeoutCountKey, timeouts + 1);
                        throw;
                    }
                }, context);
            }
            finally
            {
                ResilienceContextPool.Shared.Return(context);
                _transactionLock.Release();
            }
        }

        public async Task InitializeAsync(IProgress<string>? progress = null, CancellationToken ct = default)
        {
            if (_isInitialized) return;

            LogSyncActivity("Enter InitializeAsync");

            // Use a short timeout to check if we can enter initialization without blocking indefinitely
            if (!await _initLock.WaitAsync(0, ct))
            {
                LogSyncActivity("Waiting for _initLock...");
                await _initLock.WaitAsync(ct);
            }

            try
            {
                if (_isInitialized) 
                {
                    LogSyncActivity("Already initialized by another thread.");
                    return;
                }

                // Check if Python setup is needed on Windows
                var isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
                if (isWindows && string.IsNullOrEmpty(_settings.PythonPath))
                {
                    bool embeddedExists = PythonEmbeddedInstaller.IsInstalled();

                    bool bypassPackageInstall = false;
                    if (!embeddedExists)
                    {
                        if (SetupDecisionProvider != null)
                        {
                            LogSyncActivity("Python not found. Requesting user setup decision...");
                            var decision = await SetupDecisionProvider();
                            LogSyncActivity($"User setup decision: {decision}");
                            if (decision != PythonSetupDecision.Automatic)
                            {
                                throw new OperationCanceledException("Python automatic setup declined or canceled by user.");
                            }
                        }
                    }
                    else
                    {
                        if (IsUpdateSuppressed || _hasPromptedUpdate)
                        {
                            bypassPackageInstall = true;
                        }
                        else if (UpdateDecisionProvider != null)
                        {
                            _hasPromptedUpdate = true;
                            LogSyncActivity("Python found. Requesting user update decision...");
                            var decision = await UpdateDecisionProvider();
                            LogSyncActivity($"User update decision: {decision}");
                            if (decision == PythonSetupDecision.Cancel)
                            {
                                IsUpdateSuppressed = true;
                                bypassPackageInstall = true;
                            }
                            else if (decision == PythonSetupDecision.Manual)
                            {
                                bypassPackageInstall = true;
                            }
                        }
                    }

                    if (bypassPackageInstall)
                    {
                        _isInitializing = true;
                        try
                        {
                            await PythonEmbeddedInstaller.SetupAsync(ct: ct);
                            if (!PythonEngine.IsInitialized)
                            {
                                LogSyncActivity("PythonEngine.Initialize starting...");
                                PythonEmbeddedInstaller.ConfigureEngine();
                                PythonEngine.Initialize();
                                PythonEngine.BeginAllowThreads();
                                LogSyncActivity("PythonEngine.Initialize complete.");
                            }
                            _isInitialized = true;
                            _isInitializing = false;
                            LogSyncActivity("InitializeAsync success (bypassed package install).");
                            return;
                        }
                        catch (Exception)
                        {
                            _isInitializing = false;
                            throw;
                        }
                    }
                }

                _isInitializing = true;

                var initProgress = new Progress<string>(status => progress?.Report(status));
                SetupProgressStarted?.Invoke(initProgress);

                try
                {
                    LogSyncActivity("SetupPython starting...");
                    ((IProgress<string>)initProgress).Report("Extracting Python environment...");
                    await PythonEmbeddedInstaller.SetupAsync(initProgress, ct);
                    LogSyncActivity("SetupPython complete.");
                    ct.ThrowIfCancellationRequested();

                    // Before the package checks: InstallPackage returns early for up-to-date packages, so the pip upgrade must not depend on them.
                    await EnsurePipSecureAsync(ct);

                    var packages = _settings.PythonEssentialPackages;
                    
                    for (int i = 0; i < packages.Count; i++)
                    {
                        var pkg = packages[i];
                        ((IProgress<string>)initProgress).Report($"Installing library: {pkg} ({i + 1}/{packages.Count})...");
                        await InstallPackage(pkg, forceUpgrade: false, ct);
                        ct.ThrowIfCancellationRequested();
                    }
                }
                finally
                {
                    SetupProgressFinished?.Invoke();
                }
                
                // Initialize the Python engine (In-Process)
                if (!PythonEngine.IsInitialized)
                {
                    LogSyncActivity("PythonEngine.Initialize starting...");
                    PythonEmbeddedInstaller.ConfigureEngine();
                    PythonEngine.Initialize();
                    PythonEngine.BeginAllowThreads();
                    LogSyncActivity("PythonEngine.Initialize complete.");
                }

                _isInitialized = true;
                _isInitializing = false;
                LogSyncActivity("InitializeAsync success.");
            }
            catch (Exception ex)
            {
                _isInitializing = false;
                LogSyncActivity($"InitializeAsync FAILED: {ex.Message}");
                throw;
            }
            finally
            {
                _initLock.Release();
            }
        }

        public async Task InitializeExternalProcessAsync()
        {
             if (!_isInitialized) await InitializeAsync();

             // StartAsync() itself is a cheap no-op when the existing connection is still
             // healthy (it checks _isConnected && !IsProcessDead() before doing any work), so
             // it must be called every time rather than only once when _processManager is
             // null. Otherwise, once a prior Python call tears the pipe down on failure
             // (PythonProcessManager.CleanupConnection), _processManager stays non-null
             // forever and this method becomes a permanent no-op, leaving every subsequent
             // Python-backed indicator to dereference the now-null pipe fields.
             _processManager ??= new PythonProcessManager(_settings);
             await _processManager.StartAsync();
        }

        public async Task<string> PingExternalProcessAsync()
        {
            if (_processManager == null) throw new InvalidOperationException("External process not initialized.");
            return await _processManager.SendCommandAsync("ping");
        }

        public async Task<string> SendCandlesAsync(System.Collections.Generic.List<StockAnalyzer.Core.Models.CandleData> candles)
        {
            if (_processManager == null) throw new InvalidOperationException("External process not initialized.");

            // IMPORTANT: Do NOT acquire _transactionLock here.
            // Callers (e.g., CoreMesaIndicator.CalculateAsync) wrap this call inside
            // ExecuteTransactionAsync which already holds _transactionLock.
            // SemaphoreSlim(1,1) is NOT reentrant, so double-acquiring would deadlock.

            // 1. Convert to Arrow IPC bytes using shared buffer
            _sharedIpcStream.Position = 0;
            _sharedIpcStream.SetLength(0); // clear
            
            await ArrowConverter.WriteToArrowStreamAsync(candles, _sharedIpcStream);
            
            if (!_sharedIpcStream.TryGetBuffer(out ArraySegment<byte> buffer))
            {
                // Fallback just in case, though default MemoryStream allows TryGetBuffer
                buffer = new ArraySegment<byte>(_sharedIpcStream.ToArray());
            }

            ReadOnlyMemory<byte> arrowBytes = buffer;

            // 2. Notify Python to expect data
            var response = await _processManager.SendCommandAsync("prepare_data_transfer", new { size = arrowBytes.Length });
            
            // 3. Send raw bytes
            await _processManager.SendBinaryDataAsync(arrowBytes);

            // 4. Wait for confirmation
            return await _processManager.WaitForResponseAsync();
        }

        public async Task<string> CalculateFftTrendFilterAsync(int windowSize = IndicatorDefaultConstants.FftTrendFilterDefaultWindowSize, int numHarmonics = IndicatorDefaultConstants.FftTrendFilterDefaultNumHarmonics)
        {
            if (_processManager == null) throw new InvalidOperationException("External process not initialized.");

            return await _processManager.SendCommandAsync("calculate_fft_trend_filter", new { windowSize = windowSize, numHarmonics = numHarmonics });
        }

        public async Task<string> CalculateBacktestStatsAsync(System.Collections.Generic.IEnumerable<StockAnalyzer.Core.Models.Backtest.Trade> trades)
        {
            if (_processManager == null) throw new InvalidOperationException("External process not initialized.");
            
            return await _processManager.SendCommandAsync("calculate_backtest_stats", new { trades = trades });
        }

        public async Task<string> SearchSimilarPatternsAsync(int lookback = 0, int topK = 5, int futureSteps = 20, double threshold = 0.3, int queryLength = 30, int queryStartIndex = -1, bool useStructural = false, int warpingRadius = ChartConstants.DtwDefaultWarpingRadius, System.Collections.Generic.IReadOnlyList<double>? volatility = null)
        {
            if (_processManager == null) throw new InvalidOperationException("External process not initialized.");
            
            return await _processManager.SendCommandAsync("search_similar_patterns", new
            {
                lookback = lookback,
                topK = topK,
                futureSteps = futureSteps,
                threshold = threshold,
                queryLength = queryLength,
                queryStartIndex = queryStartIndex,
                useStructural = useStructural,
                warpingRadius = warpingRadius,
                volatility = volatility
            });
        }

        private string ResolvePythonExecutable()
        {
            if (!string.IsNullOrEmpty(_settings.PythonPath) && System.IO.File.Exists(_settings.PythonPath))
            {
                return _settings.PythonPath;
            }

            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                return PythonEmbeddedInstaller.ExecutablePath(PythonExecutableName);
            }

            return PythonInterpreterLocator.TryResolveExecutable(out var located) && System.IO.File.Exists(located)
                ? located
                : PythonInterpreterLocator.ExecutableName;
        }

        public async Task RunUpdatePipelineAsync(string? symbol = null, IProgress<int>? progress = null, bool forceMetadata = false, CancellationToken ct = default)
        {
            if (!string.IsNullOrEmpty(symbol) && (!AttestedFileNames.IsPlain(symbol) || symbol.Contains(' ') || symbol.StartsWith('-')))
                throw new ArgumentException($"Invalid ticker symbol '{symbol}'.", nameof(symbol));

            LogSyncActivity($"RunUpdatePipelineAsync START for {symbol}");

            // Set a safety timeout from settings (default 2 minutes) to prevent indefinite hangs.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var timeoutMinutes = _settings.SyncTimeoutMinutes > 0 ? _settings.SyncTimeoutMinutes : 2;
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));
            var linkedToken = timeoutCts.Token;

            var executionTask = Task.Run(async () =>
            {
                try
                {
                    LogSyncActivity("Pipeline task started in background...");
                    
                    // 1. Initialize
                    LogSyncActivity("Invoking InitializeAsync...");
                    await InitializeAsync(ct: linkedToken);

                    // 2. Resolve Paths
                    var pythonExe = ResolvePythonExecutable();

                    if (!System.IO.File.Exists(pythonExe))
                        throw new System.IO.FileNotFoundException($"Python executable not found at {pythonExe}");

                    var resolvedScriptPath = PythonScriptLocator.Resolve("update_pipeline.py");

                    // 3. Process Setup
                    var startInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = pythonExe,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        WorkingDirectory = System.IO.Path.GetDirectoryName(resolvedScriptPath)
                    };

                    startInfo.ArgumentList.Add("-u");
                    startInfo.ArgumentList.Add(resolvedScriptPath);
                    if (!string.IsNullOrEmpty(symbol))
                    {
                        startInfo.ArgumentList.Add("--ticker");
                        startInfo.ArgumentList.Add(symbol);
                    }
                    if (forceMetadata)
                    {
                        startInfo.ArgumentList.Add("--force-metadata");
                    }

                    LogSyncActivity($"Starting process: {pythonExe} {string.Join(" ", startInfo.ArgumentList)}");
                    
                    var combinedOutput = new RollingLogBuffer(500);
                    using var process = new System.Diagnostics.Process { StartInfo = startInfo };
                    
                    process.OutputDataReceived += (s, e) =>
                    {
                        if (string.IsNullOrEmpty(e.Data)) return;
                        combinedOutput.AppendLine(e.Data);
                        if (e.Data.StartsWith("PROGRESS:"))
                        {
                            if (int.TryParse(e.Data.Substring(9), out int p)) progress?.Report(p);
                        }
                    };
                    process.ErrorDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data)) 
                        {
                            combinedOutput.AppendLine("[STDERR] " + e.Data);
                            LogSyncActivity($"[Python ERR] {e.Data}", LogLevel.Debug);
                        }
                    };

                    using var reg = linkedToken.Register(() => 
                    {
                        try { if (!process.HasExited) process.Kill(true); } catch { }
                    });

                    await _resiliencePipeline.ExecuteAsync(async innerCt => 
                    {
                        process.Start();
                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();

                        await process.WaitForExitAsync(innerCt);
                    }, linkedToken);

                    LogSyncActivity($"Process exited with code {process.ExitCode}");

                    if (_timeframeGenerator != null)
                    {
                        try
                        {
                            LogSyncActivity($"Running TimeFrameParquetGenerator for {(string.IsNullOrEmpty(symbol) ? "all symbols" : symbol)}...");
                            progress?.Report(90);
                            int failed = await _timeframeGenerator.GenerateAsync(symbol, linkedToken).ConfigureAwait(false);
                            if (failed > 0)
                            {
                                LogSyncActivity($"TimeFrameParquetGenerator reported {failed} failure(s).", LogLevel.Warning);
                            }
                        }
                        catch (Exception tfEx)
                        {
                            LogSyncActivity($"TimeFrameParquetGenerator threw an exception: {tfEx.Message}", LogLevel.Error);
                        }
                    }

                    if (process.ExitCode != 0)
                    {
                        var errorLogs = combinedOutput.ToString();
                        throw new Exception($"Update pipeline failed with exit code {process.ExitCode}.{Environment.NewLine}Logs:{Environment.NewLine}{errorLogs}");
                    }

                    LogSyncActivity("Pipeline task completed Successfully.");
                    progress?.Report(100);
                }
                catch (Exception ex)
                {
                    LogSyncActivity($"Pipeline task FAILED: {ex.GetType().Name}: {ex.Message}");
                    throw;
                }
            }, linkedToken);

            var timeoutTask = Task.Delay(TimeSpan.FromMinutes(timeoutMinutes), linkedToken);

            var completedTask = await Task.WhenAny(executionTask, timeoutTask);
            
            if (completedTask == timeoutTask)
            {
                LogSyncActivity($"RunUpdatePipelineAsync TIMEOUT triggered ({timeoutMinutes} minutes)", LogLevel.Error);
                throw new TimeoutException($"The update pipeline timed out after {timeoutMinutes} minutes. Please check the application logs for details.");
            }

            // Await executionTask to propagate any exceptions
            await executionTask;
            LogSyncActivity("RunUpdatePipelineAsync FINISH (Success)");
        }

        public async Task<string> ResolvePythonExecutablePathAsync(CancellationToken ct = default)
        {
            if (!_isInitialized)
            {
                await InitializeAsync(ct: ct);
            }

            var pythonExe = ResolvePythonExecutable();
            if (!System.IO.File.Exists(pythonExe))
            {
                throw new System.IO.FileNotFoundException($"Python executable not found at {pythonExe}");
            }

            return pythonExe;
        }

        public async Task RunUpdatePipelineAsync(string? symbol, SyncSessionConfig config, IProgress<int>? progress = null, CancellationToken ct = default)
        {
            if (config.DelayMinSeconds < 1.0m || config.DelayMinSeconds > 60.0m)
                throw new ArgumentOutOfRangeException(nameof(config.DelayMinSeconds), "DelayMinSeconds must be in range [1.0, 60.0].");
            if (config.DelayMaxSeconds < 1.0m || config.DelayMaxSeconds > 60.0m)
                throw new ArgumentOutOfRangeException(nameof(config.DelayMaxSeconds), "DelayMaxSeconds must be in range [1.0, 60.0].");
            if (config.DelayMinSeconds > config.DelayMaxSeconds)
                throw new ArgumentOutOfRangeException(nameof(config.DelayMinSeconds), "DelayMinSeconds must be less than or equal to DelayMaxSeconds.");
            if (config.StartSyncPeriodYears < 1 || config.StartSyncPeriodYears > 50)
                throw new ArgumentOutOfRangeException(nameof(config.StartSyncPeriodYears), "StartSyncPeriodYears must be in range [1, 50].");

            if (!string.IsNullOrEmpty(symbol) && (!AttestedFileNames.IsPlain(symbol) || symbol.Contains(' ') || symbol.StartsWith('-')))
                throw new ArgumentException($"Invalid ticker symbol '{symbol}'.", nameof(symbol));

            LogSyncActivity($"RunUpdatePipelineAsync (Config) START for {symbol}");

            // Set a safety timeout from settings (default 2 minutes) to prevent indefinite hangs.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var timeoutMinutes = _settings.SyncTimeoutMinutes > 0 ? _settings.SyncTimeoutMinutes : 2;
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));
            var linkedToken = timeoutCts.Token;

            var executionTask = Task.Run(async () =>
            {
                try
                {
                    LogSyncActivity("Pipeline task (Config) started in background...");
                    
                    // 1. Initialize
                    LogSyncActivity("Invoking InitializeAsync...");
                    await InitializeAsync(ct: linkedToken);

                    // 2. Resolve Paths
                    var pythonExe = ResolvePythonExecutable();

                    if (!System.IO.File.Exists(pythonExe))
                        throw new System.IO.FileNotFoundException($"Python executable not found at {pythonExe}");

                    var resolvedScriptPath = PythonScriptLocator.Resolve("update_pipeline.py");

                    // 3. Process Setup
                    var startInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = pythonExe,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        WorkingDirectory = System.IO.Path.GetDirectoryName(resolvedScriptPath)
                    };

                    startInfo.ArgumentList.Add("-u");
                    startInfo.ArgumentList.Add(resolvedScriptPath);
                    if (!string.IsNullOrEmpty(symbol))
                    {
                        startInfo.ArgumentList.Add("--ticker");
                        startInfo.ArgumentList.Add(symbol);
                    }
                    if (!config.IsTimeSeriesSyncEnabled)
                    {
                        startInfo.ArgumentList.Add("--skip-daily");
                    }
                    if (config.IsMetadataSyncEnabled)
                    {
                        startInfo.ArgumentList.Add("--force-metadata");
                        if (config.IsImputeMissingMetadataEnabled)
                        {
                            startInfo.ArgumentList.Add("--impute-missing");
                        }
                    }
                    
                    // Delay is fully managed on the C# Ticker-Loop side. We force Python process delay to 0
                    // to prevent processes from hanging/sleeping, saving resources.
                    startInfo.ArgumentList.Add("--delay");
                    startInfo.ArgumentList.Add("0");
                    
                    if (config.IsFullHistoryEnabled)
                    {
                        startInfo.ArgumentList.Add("--full-history");
                    }
                    else
                    {
                        startInfo.ArgumentList.Add("--start-period");
                        startInfo.ArgumentList.Add(config.StartSyncPeriodYears.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }

                    if (config.IsForcePeriodDownloadEnabled)
                    {
                        startInfo.ArgumentList.Add("--force-period");
                    }

                    LogSyncActivity($"Starting process (Config): {pythonExe} {string.Join(" ", startInfo.ArgumentList)}");
                    
                    var combinedOutput = new RollingLogBuffer(500);
                    using var process = new System.Diagnostics.Process { StartInfo = startInfo };
                    
                    process.OutputDataReceived += (s, e) =>
                    {
                        if (string.IsNullOrEmpty(e.Data)) return;
                        combinedOutput.AppendLine(e.Data);
                        if (e.Data.StartsWith("PROGRESS:"))
                        {
                            if (int.TryParse(e.Data.Substring(9), out int p)) progress?.Report(p);
                        }
                    };
                    process.ErrorDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data)) 
                        {
                            combinedOutput.AppendLine("[STDERR] " + e.Data);
                            LogSyncActivity($"[Python ERR] {e.Data}", LogLevel.Debug);
                        }
                    };

                    await _resiliencePipeline.ExecuteAsync(async innerCt => 
                    {
                        process.Start();
                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();

                        var exitTask = process.WaitForExitAsync();
                        var cancelTask = Task.Delay(-1, innerCt);

                        var completedTask = await Task.WhenAny(exitTask, cancelTask);
                        if (completedTask == cancelTask)
                        {
                            LogSyncActivity("Cancellation triggered. Waiting up to 5 seconds for graceful exit...");
                            var delayTask = Task.Delay(5000);
                            var graceCompletedTask = await Task.WhenAny(exitTask, delayTask);
                            if (graceCompletedTask == delayTask)
                            {
                                LogSyncActivity("Grace period expired. Forcefully killing process tree...");
                                try { if (!process.HasExited) process.Kill(true); } catch { }
                                await exitTask;
                            }
                            throw new OperationCanceledException(innerCt);
                        }
                        else
                        {
                            await exitTask;
                        }
                    }, linkedToken);

                    LogSyncActivity($"Process exited with code {process.ExitCode}");

                    if (_timeframeGenerator != null)
                    {
                        try
                        {
                            LogSyncActivity($"Running TimeFrameParquetGenerator for {(string.IsNullOrEmpty(symbol) ? "all symbols" : symbol)}...");
                            progress?.Report(90);
                            int failed = await _timeframeGenerator.GenerateAsync(symbol, linkedToken).ConfigureAwait(false);
                            if (failed > 0)
                            {
                                LogSyncActivity($"TimeFrameParquetGenerator reported {failed} failure(s).", LogLevel.Warning);
                            }
                        }
                        catch (Exception tfEx)
                        {
                            LogSyncActivity($"TimeFrameParquetGenerator threw an exception: {tfEx.Message}", LogLevel.Error);
                        }
                    }

                    if (process.ExitCode != 0)
                    {
                        var errorLogs = combinedOutput.ToString();
                        throw new Exception($"Update pipeline failed with exit code {process.ExitCode}.{Environment.NewLine}Logs:{Environment.NewLine}{errorLogs}");
                    }

                    LogSyncActivity("Pipeline task completed Successfully.");
                    progress?.Report(100);
                }
                catch (Exception ex)
                {
                    LogSyncActivity($"Pipeline task FAILED: {ex.GetType().Name}: {ex.Message}");
                    throw;
                }
            }, linkedToken);

            var timeoutTask = Task.Delay(TimeSpan.FromMinutes(timeoutMinutes), linkedToken);

            var completedTask = await Task.WhenAny(executionTask, timeoutTask);
            
            if (completedTask == timeoutTask)
            {
                LogSyncActivity($"RunUpdatePipelineAsync TIMEOUT triggered ({timeoutMinutes} minutes)", LogLevel.Error);
                throw new TimeoutException($"The update pipeline timed out after {timeoutMinutes} minutes. Please check the application logs for details.");
            }

            // Await executionTask to propagate any exceptions
            await executionTask;
            LogSyncActivity("RunUpdatePipelineAsync FINISH (Success)");
        }

        /// <summary>
        /// Update probe run by <see cref="CheckForUpdateAsync"/>. Multi-line on purpose: a one-line
        /// form with compound statements is a syntax error in Python, so the probe never reported an update.
        /// </summary>
        internal const string UpdateCheckScript = """
            import importlib.metadata, json, sys, urllib.request

            def parse_v(v):
                return tuple(int(x) for x in v.split('.') if x.isdigit())

            def parse_release(v):
                parts = v.split('.')
                return tuple(int(x) for x in parts) if all(x.isdigit() for x in parts) else None

            def norm(t):
                t = list(t)
                while t and t[-1] == 0:
                    t.pop()
                return tuple(t)

            def is_yanked(files):
                return not files or all(f.get('yanked') for f in files)

            # Arguments: package name, request time limit in seconds, then optionally the lower bound and the exclusive upper major.
            name = sys.argv[1]
            request_timeout = float(sys.argv[2])
            try:
                installed = importlib.metadata.version(name)
                req = urllib.request.Request(f'https://pypi.org/pypi/{name}/json', headers={'User-Agent': 'StockAnalyzer'})
                with urllib.request.urlopen(req, timeout=request_timeout) as r:
                    doc = json.loads(r.read().decode())
                if len(sys.argv) >= 5:
                    # Pinned package: the latest release inside >=sys.argv[3],<sys.argv[4] (final, not yanked).
                    lower = norm(parse_v(sys.argv[3]))
                    upper = int(sys.argv[4])
                    inside = [t for v, files in doc['releases'].items() if not is_yanked(files)
                              for t in [parse_release(v)] if t is not None and norm(t) >= lower and t[0] < upper]
                    latest = max(inside, key=norm) if inside else ()
                else:
                    latest = parse_v(doc['info']['version'])
                print('UPDATE_AVAILABLE' if norm(latest) > norm(parse_v(installed)) else 'NO_UPDATE')
            except Exception as e:
                print('ERROR:', e)
            """;

        /// <summary>
        /// Whether a newer release exists. A pinned package is compared with the latest release inside its update range
        /// (<see cref="PackageVersionRange"/>), so a newer major never makes every start run pip; an unpinned package is compared with the global latest.
        /// A check that could not run is <see cref="UpdateCheckState.CheckFailed"/>, not "up to date". Cancellation through <paramref name="ct"/> is rethrown.
        /// </summary>
        private async Task<UpdateCheckResult> CheckForUpdateAsync(string packageName, string? pinnedVersion, CancellationToken ct)
        {
            var pythonExe = ResolvePythonExecutable();

            if (!System.IO.File.Exists(pythonExe))
            {
                return new UpdateCheckResult(UpdateCheckState.CheckFailed, "the Python executable was not found");
            }

            // "-" reads the script from stdin; the package name and the request time limit follow as sys.argv[1] and [2] (no quoting needed).
            var arguments = new System.Collections.Generic.List<string>
            {
                "-",
                packageName,
                _settings.PythonUpdateCheckRequestTimeout.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            if (PackageVersionRange.UpperMajor(pinnedVersion) is { } upperMajor)
            {
                arguments.Add(pinnedVersion!.Trim());
                arguments.Add(upperMajor.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            return await UpdateCheckProbe.RunAsync(pythonExe, arguments.ToArray(), UpdateCheckScript, _settings.PythonUpdateCheckTimeout, ct);
        }

        public async Task<bool> IsPackageInstalledAsync(string packageName, CancellationToken ct = default)
        {
            string importName = packageName switch
            {
                "pandas-ta" => "pandas_ta",
                "scikit-learn" => "sklearn",
                _ => packageName
            };
            return await ProbeModuleAsync(importName, ct) == ModuleInstallState.Installed;
        }

        private async Task InstallPackage(string packageName, bool forceUpgrade, CancellationToken ct)
        {
            LogSyncActivity($"InstallPackage START: {packageName} (forceUpgrade={forceUpgrade})");
            
            // Map pip package names to import names for those that differ
            string importName = packageName switch
            {
                "pandas-ta" => "pandas_ta",
                "scikit-learn" => "sklearn",
                _ => packageName
            };

            var state = await ProbeModuleAsync(importName, ct);
            if (!ModuleInstallProbe.IsConclusive(state))
            {
                LogSyncActivity($"Import check for {packageName} ended as {state}. The installed version is kept; pip is not run.", LogLevel.Warning);
                return;
            }

            var pinnedMap = GetPinnedPackageVersions(msg => LogSyncActivity(msg, LogLevel.Warning));
            string? pinnedVersion = null;
            if (pinnedMap.TryGetValue(packageName, out pinnedVersion))
            {
                LogSyncActivity($"Resolved pinned version for '{packageName}': {pinnedVersion}");
            }
            else if (packageName != PythonPackageSets.TorchPackage)
            {
                LogSyncActivity($"Package '{packageName}' not found in requirements.txt; proceeding without strict pin", LogLevel.Warning);
            }

            bool isInstalled = state == ModuleInstallState.Installed;
            bool shouldUpdate = forceUpgrade;

            if (isInstalled && !forceUpgrade)
            {
                var check = await CheckForUpdateAsync(packageName, pinnedVersion, ct);
                if (check.State == UpdateCheckState.CheckFailed)
                {
                    LogSyncActivity($"Update check for {packageName} failed: {check.Reason}. The installed version is kept; pip is not run.", LogLevel.Warning);
                    return;
                }

                shouldUpdate = check.State == UpdateCheckState.UpdateAvailable;
                if (!shouldUpdate)
                {
                    LogSyncActivity($"Module {packageName} is already installed and up-to-date. Skipping pip.");
                    return;
                }
                LogSyncActivity($"Module {packageName} is installed, but a newer version is available. Upgrading...");
            }
            else if (!isInstalled)
            {
                LogSyncActivity($"Module {packageName} not found. Running pip install...");
            }

            await EnsurePipSecureAsync(ct);

            await RunPipCommandAsync(BuildInstallArguments(packageName, pinnedVersion, shouldUpdate, forceUpgrade), ct);
            LogSyncActivity($"InstallPackage FINISH: {packageName}");
        }

        /// <summary>
        /// pip arguments of an install step. Fresh install of a pinned package: the exact pin. Automatic update of a pinned package: the latest
        /// release inside <c>&gt;=pin,&lt;next major</c> (D-01 B). A forced (user-initiated) upgrade and a package without a pin are not bounded
        /// (N-04 A). The command start (build isolation bypassed) is <see cref="PythonPackageSets.PipInstallPrefix"/>.
        /// </summary>
        internal static string BuildInstallArguments(string packageName, string? pinnedVersion, bool shouldUpdate, bool forceUpgrade)
        {
            const string installPrefix = PythonPackageSets.PipInstallPrefix;
            bool upgrade = shouldUpdate || forceUpgrade;

            if (packageName == PythonPackageSets.TorchPackage)
            {
                return upgrade
                    ? $"{installPrefix} --upgrade {packageName} --index-url {PythonPackageSets.TorchIndexUrl}"
                    : $"{installPrefix} {packageName} --index-url {PythonPackageSets.TorchIndexUrl}";
            }

            if (!string.IsNullOrEmpty(pinnedVersion) && !upgrade)
            {
                return $"{installPrefix} {packageName}=={pinnedVersion}";
            }

            if (upgrade && !forceUpgrade && PackageVersionRange.UpgradeSpecifier(packageName, pinnedVersion) is { } specifier)
            {
                return $"{installPrefix} --upgrade \"{specifier}\"";
            }

            return upgrade
                ? $"{installPrefix} --upgrade {packageName}"
                : $"{installPrefix} {packageName}";
        }

        private static System.Collections.Generic.Dictionary<string, string>? _pinnedPackageVersions;
        private static readonly object _pinnedVersionsLock = new();

        private static System.Collections.Generic.IReadOnlyDictionary<string, string> GetPinnedPackageVersions(Action<string>? warnLogger = null)
        {
            if (_pinnedPackageVersions != null) return _pinnedPackageVersions;

            lock (_pinnedVersionsLock)
            {
                if (_pinnedPackageVersions != null) return _pinnedPackageVersions;

                var dict = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var reqPath = PythonScriptLocator.Resolve("requirements.txt");
                    if (System.IO.File.Exists(reqPath))
                    {
                        var lines = System.IO.File.ReadAllLines(reqPath);
                        foreach (var rawLine in lines)
                        {
                            var line = rawLine.Trim();
                            if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;

                            var eqIndex = line.IndexOf("==");
                            if (eqIndex > 0)
                            {
                                var pkg = line.Substring(0, eqIndex).Trim();
                                var ver = line.Substring(eqIndex + 2).Trim();
                                dict[pkg] = ver;
                            }
                        }
                    }
                    else
                    {
                        warnLogger?.Invoke($"requirements.txt not found at resolved path: {reqPath}");
                    }
                }
                catch (Exception ex)
                {
                    warnLogger?.Invoke($"Failed to load requirements.txt: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"Failed to load requirements.txt: {ex.Message}");
                }

                _pinnedPackageVersions = dict;
                return _pinnedPackageVersions;
            }
        }

        /// <summary>Minimum pip version required in the embedded interpreter (security requirement).</summary>
        public const string MinimumPipVersion = "26.2.2";

        private readonly PipSecurityGate _pipSecurity = new();

        /// <summary>Interpreter arguments that enforce <see cref="MinimumPipVersion"/> and refresh the CA bundle.</summary>
        public const string PipSecurityUpgradeArguments = PythonPackageSets.PipInstallCommand + " --upgrade \"pip>=" + MinimumPipVersion + "\" certifi";


        private Task EnsurePipSecureAsync(CancellationToken ct)
            => _pipSecurity.EnsureAsync(
                ReadInstalledPipVersionAsync,
                token => RunPipCommandAsync(PipSecurityUpgradeArguments, token),
                ct);

        /// <summary>
        /// The pip version the embedded interpreter reports through its package metadata, or null when it cannot be read (including a read that
        /// passes <c>PythonImportProbeTimeout</c>: it is the time limit of a plain interpreter start).
        /// </summary>
        private async Task<string?> ReadInstalledPipVersionAsync(CancellationToken ct)
        {
            var pythonExe = ResolvePythonExecutable();
            if (!System.IO.File.Exists(pythonExe)) return null;

            return await PipVersionProbe.ReadAsync(
                pythonExe,
                "-c \"import importlib.metadata; print(importlib.metadata.version('pip'))\"",
                _settings.PythonImportProbeTimeout,
                ct);
        }

        private async Task EnsurePipReadyAsync(CancellationToken ct = default)
        {
            await PythonEmbeddedInstaller.SetupAsync(ct: ct);
        }

        public async Task InstallPackagesAsync(IEnumerable<string> packageNames, bool forceUpgrade = false, IProgress<string>? progress = null, CancellationToken ct = default)
        {
            await EnsurePipReadyAsync(ct);

            var packageList = packageNames.ToList();
            for (int i = 0; i < packageList.Count; i++)
            {
                var pkg = packageList[i];
                var action = forceUpgrade ? "Upgrading" : "Installing";
                progress?.Report($"{action} library: {pkg} ({i + 1}/{packageList.Count})...");
                await InstallPackage(pkg, forceUpgrade, ct);
                ct.ThrowIfCancellationRequested();
            }
        }

        public async Task RunPipCommandAsync(System.Collections.Generic.IReadOnlyList<string> arguments, CancellationToken ct = default)
        {
            var pythonExe = ResolvePythonExecutable();

            if (!System.IO.File.Exists(pythonExe))
                throw new System.IO.FileNotFoundException("Python executable not found for pip install.", pythonExe);

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = pythonExe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var arg in arguments)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = new System.Diagnostics.Process { StartInfo = startInfo };
            process.OutputDataReceived += (s, e) => { if (e.Data != null) LogSyncActivity($"[PIP OUT] {e.Data}", LogLevel.Debug); };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) LogSyncActivity($"[PIP ERR] {e.Data}", LogLevel.Debug); };

            using var reg = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });

            await _resiliencePipeline.ExecuteAsync(async innerCt => 
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                await process.WaitForExitAsync(innerCt);
            }, ct);

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Pip command failed with exit code {process.ExitCode}");
        }

        public async Task RunPipCommandAsync(string arguments, CancellationToken ct = default)
        {
            var pythonExe = ResolvePythonExecutable();

            if (!System.IO.File.Exists(pythonExe))
                throw new System.IO.FileNotFoundException("Python executable not found for pip install.", pythonExe);

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new System.Diagnostics.Process { StartInfo = startInfo };
            process.OutputDataReceived += (s, e) => { if (e.Data != null) LogSyncActivity($"[PIP OUT] {e.Data}", LogLevel.Debug); };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) LogSyncActivity($"[PIP ERR] {e.Data}", LogLevel.Debug); };

            using var reg = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });

            await _resiliencePipeline.ExecuteAsync(async innerCt => 
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                await process.WaitForExitAsync(innerCt);
            }, ct);

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Pip command failed with exit code {process.ExitCode}");
        }

        /// <summary>The import check of one package; its time limit is <c>PythonImportProbeTimeout</c> (heavy packages must not be reported as missing because the check timed out).</summary>
        private async Task<ModuleInstallState> ProbeModuleAsync(string importName, CancellationToken ct)
        {
            var pythonExe = ResolvePythonExecutable();

            if (!System.IO.File.Exists(pythonExe)) return ModuleInstallState.Missing;

            if (string.IsNullOrWhiteSpace(importName) || !ValidPythonModuleNameRegex.IsMatch(importName))
            {
                return ModuleInstallState.Failed;
            }

            return await ModuleInstallProbe.RunWithArgumentsAsync(pythonExe, ["-c", $"import {importName}"], _settings.PythonImportProbeTimeout, ct);
        }

        public void Run(Action<PyModule> action)
        {
            EnsureInitialized();
            using (Py.GIL())
            {
                using (var scope = Py.CreateScope())
                {
                    action(scope);
                }
            }
        }

        public T Run<T>(Func<PyModule, T> func)
        {
            EnsureInitialized();
            using (Py.GIL())
            {
                using (var scope = Py.CreateScope())
                {
                    return func(scope);
                }
            }
        }

        public async Task RunAsync(Action<PyModule> action, System.Threading.CancellationToken cancellationToken = default)
        {
            EnsureInitialized();
            await Task.Run(() => 
            {
                using (Py.GIL())
                {
                    using (var scope = Py.CreateScope())
                    {
                        action(scope);
                    }
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        public async Task<T> RunAsync<T>(Func<PyModule, T> func, System.Threading.CancellationToken cancellationToken = default)
        {
            EnsureInitialized();
            return await Task.Run(() =>
            {
                using (Py.GIL())
                {
                    using (var scope = Py.CreateScope())
                    {
                        return func(scope);
                    }
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        private void EnsureInitialized()
        {
            if (!_isInitialized)
            {
                throw new InvalidOperationException("PythonService is not initialized. Call InitializeAsync() first.");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_processManager != null)
            {
                await _processManager.DisposeAsync().ConfigureAwait(false);
            }
            _sharedIpcStream.Dispose();
        }
    }
}
