using System;
using System.Threading;
using System.Threading.Tasks;
using Python.Runtime;
using Python.Included;
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
        private bool _hasPromptedUpdate = false;

        private string PythonExecutableName => System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? "python.exe" : "python3";

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

        public PythonService(IStockAnalyzerSettings settings, ILogger<PythonService>? logger = null)
        {
            _settings = settings;
            _logger = logger ?? NullLogger<PythonService>.Instance;
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
                        .Handle<System.IO.IOException>()
                        .Handle<System.InvalidOperationException>()
                        .Handle<System.TimeoutException>(),
                    OnRetry = args =>
                    {
                        LogSyncActivity($"[PythonService] Retry attempt {args.AttemptNumber + 1}/{_settings.PythonMaxRetries}. Exception: {args.Outcome.Exception?.Message}");
                        return ValueTask.CompletedTask;
                    }
                })
                .Build();
        }

        public async Task<T> ExecuteTransactionAsync<T>(Func<Task<T>> action)
        {
            await _transactionLock.WaitAsync();
            try
            {
                return await action();
            }
            finally
            {
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
                    var pythonHome = Installer.InstallPath;
                    var pythonExeName = PythonExecutableName;
                    var pythonExe = System.IO.Path.Combine(pythonHome, pythonExeName);
                    bool embeddedExists = System.IO.File.Exists(pythonExe);
                    if (!embeddedExists && System.IO.Directory.Exists(pythonHome))
                    {
                        var subdirs = System.IO.Directory.GetDirectories(pythonHome, "python-*-embed-*");
                        foreach (var dir in subdirs)
                        {
                            if (System.IO.File.Exists(System.IO.Path.Combine(dir, pythonExeName)))
                            {
                                embeddedExists = true;
                                break;
                            }
                        }
                    }

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
                            await Installer.SetupPython();
                            if (!PythonEngine.IsInitialized)
                            {
                                LogSyncActivity("PythonEngine.Initialize starting...");
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
                    await Installer.SetupPython();
                    LogSyncActivity("SetupPython complete.");
                    ct.ThrowIfCancellationRequested();

                    var packages = _settings.PythonEssentialPackages;
                    
                    for (int i = 0; i < packages.Count; i++)
                    {
                        var pkg = packages[i];
                        ((IProgress<string>)initProgress).Report($"Installing library: {pkg} ({i + 1}/{packages.Count})...");
                        await InstallPackage(pkg, ct);
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
             
             if (_processManager == null)
             {
                 _processManager = new PythonProcessManager(_settings);
                 await _processManager.StartAsync();
             }
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

        public async Task<string> CalculateEgarchAsync(int p = 1, int q = 1)
        {
            if (_processManager == null) throw new InvalidOperationException("External process not initialized.");
            
            // Invoke the command on the server which uses the last sent DataFrame
            return await _processManager.SendCommandAsync("calculate_egarch", new { p = p, q = q });
        }

        public async Task<string> CalculateMesaAsync(decimal fastLimit = 0.5m, decimal slowLimit = 0.05m)
        {
            if (_processManager == null) throw new InvalidOperationException("External process not initialized.");
            
            return await _processManager.SendCommandAsync("calculate_mesa", new { fastLimit = fastLimit, slowLimit = slowLimit });
        }

        public async Task<string> CalculateBacktestStatsAsync(System.Collections.Generic.IEnumerable<StockAnalyzer.Core.Models.Backtest.Trade> trades)
        {
            if (_processManager == null) throw new InvalidOperationException("External process not initialized.");
            
            return await _processManager.SendCommandAsync("calculate_backtest_stats", new { trades = trades });
        }

        public async Task<string> DetectPatternsAsync(int minWindow = 20, int maxWindow = 60, int windowStep = 5, double threshold = 0.5, int warpingRadius = ChartConstants.DtwDefaultWarpingRadius, double shortSpanPenaltyAlpha = ChartConstants.DtwShortSpanPenaltyAlpha)
        {
            if (_processManager == null) throw new InvalidOperationException("External process not initialized.");
            
            return await _processManager.SendCommandAsync("detect_patterns", new
            {
                minWindow = minWindow,
                maxWindow = maxWindow,
                windowStep = windowStep,
                threshold = threshold,
                warpingRadius = warpingRadius,
                shortSpanPenaltyAlpha = shortSpanPenaltyAlpha
            });
        }

        public async Task<string> CalculateStructuralDtwAsync(int topK = 5, double threshold = 0.3, int futureSteps = 20, int warpingRadius = ChartConstants.DtwDefaultWarpingRadius)
        {
            if (_processManager == null) throw new InvalidOperationException("External process not initialized.");
            
            return await _processManager.SendCommandAsync("calculate_structural_dtw", new
            {
                topK = topK,
                threshold = threshold,
                futureSteps = futureSteps,
                warpingRadius = warpingRadius
            });
        }

        public async Task<string> SearchSimilarPatternsAsync(int lookback = 0, int topK = 5, int futureSteps = 20, double threshold = 0.3, int queryLength = 30, int queryStartIndex = -1, bool useStructural = false, int warpingRadius = ChartConstants.DtwDefaultWarpingRadius)
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
                warpingRadius = warpingRadius
            });
        }

        public async Task<string> CalculateStructuralDtwOscillatorAsync(int period = 14, int lag = 14, int warpingRadius = ChartConstants.DtwDefaultWarpingRadius)
        {
            if (_processManager == null) throw new InvalidOperationException("External process not initialized.");
            
            return await _processManager.SendCommandAsync("calculate_structural_dtw_oscillator", new
            {
                period = period,
                lag = lag,
                warpingRadius = warpingRadius
            });
        }

        public async Task RunUpdatePipelineAsync(string? symbol = null, IProgress<int>? progress = null, bool forceMetadata = false, CancellationToken ct = default)
        {
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
                    var pythonHome = Installer.InstallPath;
                    var pythonExeName = PythonExecutableName;
                    var pythonExe = System.IO.Path.Combine(pythonHome, pythonExeName);

                    if (!System.IO.File.Exists(pythonExe))
                    {
                        var subdirs = System.IO.Directory.GetDirectories(pythonHome, "python-*-embed-*");
                        foreach (var dir in subdirs)
                        {
                            var candidate = System.IO.Path.Combine(dir, pythonExeName);
                            if (System.IO.File.Exists(candidate))
                            {
                                pythonExe = candidate;
                                break;
                            }
                        }
                    }

                    if (!System.IO.File.Exists(pythonExe))
                        throw new System.IO.FileNotFoundException($"Python executable not found at {pythonExe}");

                    var scriptPath = "StockAnalyzer.Python/update_pipeline.py";
                    var resolvedScriptPath = Common.PathDiscovery.ResolveDataPath(null, scriptPath); 
                    
                    if (!System.IO.File.Exists(resolvedScriptPath))
                    {
                        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                        var current = baseDir;
                        while (!string.IsNullOrEmpty(current))
                        {
                            var candidate = System.IO.Path.Combine(current, "StockAnalyzer.Python", "update_pipeline.py");
                            if (System.IO.File.Exists(candidate)) { resolvedScriptPath = candidate; break; }
                            var parent = System.IO.Directory.GetParent(current);
                            if (parent == null) break;
                            current = parent.FullName;
                        }
                    }

                    if (!System.IO.File.Exists(resolvedScriptPath))
                        throw new System.IO.FileNotFoundException($"Python script not found: {resolvedScriptPath}");

                    // 3. Process Setup
                    var arguments = $"-u \"{resolvedScriptPath}\"";
                    if (!string.IsNullOrEmpty(symbol)) arguments += $" --ticker {symbol}";
                    if (forceMetadata) arguments += " --force-metadata";

                    var startInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = pythonExe,
                        Arguments = arguments,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        WorkingDirectory = System.IO.Path.GetDirectoryName(resolvedScriptPath)
                    };

                    LogSyncActivity($"Starting process: {pythonExe} {arguments}");
                    
                    var combinedOutput = new System.Text.StringBuilder();
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

                    if (process.ExitCode != 0)
                    {
                        var errorLogs = combinedOutput.ToString();
                        throw new Exception($"Update pipeline failed with exit code {process.ExitCode}.{Environment.NewLine}Logs:{Environment.NewLine}{errorLogs}");
                    }

                    LogSyncActivity("Pipeline task completed Successfully.");
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
                    var pythonHome = Installer.InstallPath;
                    var pythonExeName = PythonExecutableName;
                    var pythonExe = System.IO.Path.Combine(pythonHome, pythonExeName);

                    if (!System.IO.File.Exists(pythonExe))
                    {
                        var subdirs = System.IO.Directory.GetDirectories(pythonHome, "python-*-embed-*");
                        foreach (var dir in subdirs)
                        {
                            var candidate = System.IO.Path.Combine(dir, pythonExeName);
                            if (System.IO.File.Exists(candidate))
                            {
                                pythonExe = candidate;
                                break;
                            }
                        }
                    }

                    if (!System.IO.File.Exists(pythonExe))
                        throw new System.IO.FileNotFoundException($"Python executable not found at {pythonExe}");

                    var scriptPath = "StockAnalyzer.Python/update_pipeline.py";
                    var resolvedScriptPath = Common.PathDiscovery.ResolveDataPath(null, scriptPath); 
                    
                    if (!System.IO.File.Exists(resolvedScriptPath))
                    {
                        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                        var current = baseDir;
                        while (!string.IsNullOrEmpty(current))
                        {
                            var candidate = System.IO.Path.Combine(current, "StockAnalyzer.Python", "update_pipeline.py");
                            if (System.IO.File.Exists(candidate)) { resolvedScriptPath = candidate; break; }
                            var parent = System.IO.Directory.GetParent(current);
                            if (parent == null) break;
                            current = parent.FullName;
                        }
                    }

                    if (!System.IO.File.Exists(resolvedScriptPath))
                        throw new System.IO.FileNotFoundException($"Python script not found: {resolvedScriptPath}");

                    // 3. Process Setup
                    var arguments = $"-u \"{resolvedScriptPath}\"";
                    if (!string.IsNullOrEmpty(symbol)) arguments += $" --ticker {symbol}";
                    if (!config.IsTimeSeriesSyncEnabled) arguments += " --skip-daily";
                    if (config.IsMetadataSyncEnabled) arguments += " --force-metadata";
                    
                    // Delay is fully managed on the C# Ticker-Loop side. We force Python process delay to 0
                    // to prevent processes from hanging/sleeping, saving resources.
                    arguments += " --delay 0";
                    
                    if (config.IsFullHistoryEnabled)
                    {
                        arguments += " --full-history";
                    }
                    else
                    {
                        arguments += $" --start-period {config.StartSyncPeriodYears}";
                    }

                    var startInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = pythonExe,
                        Arguments = arguments,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        WorkingDirectory = System.IO.Path.GetDirectoryName(resolvedScriptPath)
                    };

                    LogSyncActivity($"Starting process (Config): {pythonExe} {arguments}");
                    
                    var combinedOutput = new System.Text.StringBuilder();
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

                    if (process.ExitCode != 0)
                    {
                        var errorLogs = combinedOutput.ToString();
                        throw new Exception($"Update pipeline failed with exit code {process.ExitCode}.{Environment.NewLine}Logs:{Environment.NewLine}{errorLogs}");
                    }

                    LogSyncActivity("Pipeline task completed Successfully.");
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

        private async Task InstallPackage(string packageName, CancellationToken ct)
        {
            LogSyncActivity($"InstallPackage START: {packageName}");
            
            // Map pip package names to import names for those that differ
            string importName = packageName switch
            {
                "pandas-ta" => "pandas_ta",
                "scikit-learn" => "sklearn",
                "pywin32" => "win32api",
                _ => packageName
            };

            if (await IsModuleInstalled(importName))
            {
                LogSyncActivity($"Module {packageName} is already installed. Skipping pip.");
                return;
            }

            LogSyncActivity($"Module {packageName} not found. Running pip install...");
            await Installer.TryInstallPip();
            
            // Bypass build isolation because the embedded environment often fails to create
            // a clean isolated environment with all necessary build tools (like setuptools).
            await RunPipCommandAsync($"-m pip install --no-build-isolation {packageName}", ct);
            LogSyncActivity($"InstallPackage FINISH: {packageName}");
        }

        private async Task RunPipCommandAsync(string arguments, CancellationToken ct)
        {
            var pythonHome = Installer.InstallPath;
            var pythonExeName = PythonExecutableName;
            var pythonExe = System.IO.Path.Combine(pythonHome, pythonExeName);

            // Discovery
            if (!System.IO.File.Exists(pythonExe))
            {
                var subdirs = System.IO.Directory.GetDirectories(pythonHome, "python-*-embed-*");
                foreach (var dir in subdirs)
                {
                    var candidate = System.IO.Path.Combine(dir, pythonExeName);
                    if (System.IO.File.Exists(candidate)) { pythonExe = candidate; break; }
                }
            }

            if (!System.IO.File.Exists(pythonExe))
                throw new Exception("Python executable not found for pip install.");

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
                throw new Exception($"Pip command failed with exit code {process.ExitCode}");
        }

        private async Task<bool> IsModuleInstalled(string importName)
        {
            try
            {
                var pythonHome = Installer.InstallPath;
                var pythonExeName = PythonExecutableName;
                var pythonExe = System.IO.Path.Combine(pythonHome, pythonExeName);

                // Basic discovery
                if (!System.IO.File.Exists(pythonExe))
                {
                    var subdirs = System.IO.Directory.GetDirectories(pythonHome, "python-*-embed-*");
                    foreach (var dir in subdirs)
                    {
                        var candidate = System.IO.Path.Combine(dir, pythonExeName);
                        if (System.IO.File.Exists(candidate)) { pythonExe = candidate; break; }
                    }
                }

                if (!System.IO.File.Exists(pythonExe)) return false;

                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = pythonExe,
                    Arguments = $"-c \"import {importName}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = System.Diagnostics.Process.Start(startInfo);
                if (process == null) return false;
                
                // Set a short timeout for this check
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(cts.Token);
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
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
