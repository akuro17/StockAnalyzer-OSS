using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Locates, launches, and monitors the StockAnalyzer.McpServer process.
/// Resolves executable path adjacent to host application per release deployment specification.
/// </summary>
public class McpServerProcessService : IMcpServerProcessService
{
    private const string ExeBaseName = "StockAnalyzer.McpServer";
    private readonly IMcpServerSettingsManager _settingsManager;
    private readonly ILogger<McpServerProcessService> _logger;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _startLock = new(1, 1);

    private Process? _currentProcess;
    private WindowsJobObjectHandle? _jobHandle;
    private bool _isDisposed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _currentProcess is not null && !_currentProcess.HasExited;
            }
        }
    }

    public int? ProcessId
    {
        get
        {
            lock (_lock)
            {
                return IsRunning ? _currentProcess?.Id : null;
            }
        }
    }

    public McpServerProcessService(
        IMcpServerSettingsManager settingsManager,
        ILogger<McpServerProcessService>? logger = null)
    {
        _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
        _logger = logger ?? NullLogger<McpServerProcessService>.Instance;

        AppDomain.CurrentDomain.ProcessExit += OnCurrentDomainProcessExit;
    }

    public virtual string? ResolveExecutablePath()
    {
        string exeName = OperatingSystem.IsWindows() ? $"{ExeBaseName}.exe" : ExeBaseName;

        // 1. Release path convention: adjacent to host application (StockAnalyzer.Avalonia.exe)
        string releasePath = Path.Combine(AppContext.BaseDirectory, exeName);
        if (File.Exists(releasePath))
        {
            return releasePath;
        }

        // 2. Development / Debug fallbacks (when running from IDE without full solution output collation)
        string[] devCandidates =
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "StockAnalyzer.McpServer", "bin", "Debug", "net8.0", exeName)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "StockAnalyzer.McpServer", "bin", "Release", "net8.0", exeName)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "StockAnalyzer.McpServer", exeName))
        };

        foreach (string candidate in devCandidates)
        {
            if (File.Exists(candidate))
            {
                _logger.LogDebug("Resolved McpServer executable via dev fallback: {Path}", candidate);
                return candidate;
            }
        }

        return releasePath; // Return standard release path as canonical target
    }

    public async Task<McpServerLaunchResult> StartServerAsync(CancellationToken cancellationToken = default)
    {
        await _startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_lock)
            {
                if (IsRunning)
                {
                    _logger.LogInformation("McpServer is already running (PID: {Pid}).", _currentProcess!.Id);
                    return new McpServerLaunchResult(
                        Success: true,
                        ProcessId: _currentProcess.Id,
                        Message: $"McpServer is already running (PID: {_currentProcess.Id}).",
                        ExecutablePath: ResolveExecutablePath() ?? string.Empty,
                        Arguments: string.Empty);
                }
            }

            string? exePath = ResolveExecutablePath();
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                string notFoundMsg = $"StockAnalyzer.McpServer executable not found at: {exePath ?? "(null)"}";
                _logger.LogError("{Message}", notFoundMsg);
                return new McpServerLaunchResult(
                    Success: false,
                    ProcessId: null,
                    Message: notFoundMsg,
                    ExecutablePath: exePath ?? string.Empty,
                    Arguments: string.Empty);
            }

            // Ensure settings are fresh
            var settings = await _settingsManager.LoadAsync(cancellationToken).ConfigureAwait(false);
            string arguments = settings.EnableDiscussion ? "--enable-discussion" : string.Empty;

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory,
                UseShellExecute = true // Launch with visible console window so user/agents can monitor stdout/stderr
            };

            try
            {
                Process? process;
                try
                {
                    process = Process.Start(startInfo);
                }
                catch (Exception ex) when (startInfo.UseShellExecute)
                {
                    // Fallback to UseShellExecute = false if ShellExecute fails on target platform
                    _logger.LogWarning(ex, "Failed to start with UseShellExecute=true; trying UseShellExecute=false.");
                    startInfo.UseShellExecute = false;
                    startInfo.CreateNoWindow = false;
                    process = Process.Start(startInfo);
                }

                if (process is null)
                {
                    string failMsg = "Process.Start returned null for McpServer.";
                    _logger.LogError("{Message}", failMsg);
                    return new McpServerLaunchResult(false, null, failMsg, exePath, arguments);
                }

                lock (_lock)
                {
                    _currentProcess = process;
                    if (OperatingSystem.IsWindows())
                    {
                        try
                        {
                            _jobHandle = WindowsJobObject.TryAssign(process);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to assign McpServer process to Windows Job Object.");
                        }
                    }
                    process.EnableRaisingEvents = true;
                    process.Exited += OnProcessExited;
                }

                DispatchOnUI(() =>
                {
                    OnPropertyChanged(nameof(IsRunning));
                    OnPropertyChanged(nameof(ProcessId));
                });

                _logger.LogInformation("StockAnalyzer.McpServer started successfully (PID: {Pid}, Arguments: '{Args}').", process.Id, arguments);
                return new McpServerLaunchResult(
                    Success: true,
                    ProcessId: process.Id,
                    Message: $"StockAnalyzer.McpServer started (PID: {process.Id}, Args: '{arguments}').",
                    ExecutablePath: exePath,
                    Arguments: arguments);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start StockAnalyzer.McpServer from {Path}.", exePath);
                return new McpServerLaunchResult(false, null, ex.Message, exePath, arguments);
            }
        }
        finally
        {
            _startLock.Release();
        }
    }

    public void StopServer()
    {
        lock (_lock)
        {
            if (_currentProcess is not null && !_currentProcess.HasExited)
            {
                try
                {
                    _logger.LogInformation("Stopping McpServer (PID: {Pid})...", _currentProcess.Id);
                    _currentProcess.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error while stopping McpServer process.");
                }
                finally
                {
                    _currentProcess.Dispose();
                    _currentProcess = null;
                }
            }

            try
            {
                _jobHandle?.Dispose();
                _jobHandle = null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error while disposing Job Object handle.");
            }
        }

        DispatchOnUI(() =>
        {
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(ProcessId));
        });
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            if (_currentProcess is not null)
            {
                _currentProcess.Exited -= OnProcessExited;
                _currentProcess.Dispose();
                _currentProcess = null;
            }

            try
            {
                _jobHandle?.Dispose();
                _jobHandle = null;
            }
            catch { }
        }

        DispatchOnUI(() =>
        {
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(ProcessId));
        });
        _logger.LogInformation("StockAnalyzer.McpServer process has exited.");
    }

    private static void DispatchOnUI(Action action)
    {
        if (global::Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            global::Avalonia.Threading.Dispatcher.UIThread.Post(action);
        }
    }

    private void OnCurrentDomainProcessExit(object? sender, EventArgs e)
    {
        StopServer();
    }

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        AppDomain.CurrentDomain.ProcessExit -= OnCurrentDomainProcessExit;
        StopServer();
        _startLock.Dispose();
    }
}
