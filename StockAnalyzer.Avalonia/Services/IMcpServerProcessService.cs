using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Result of an McpServer process launch attempt.
/// </summary>
public readonly record struct McpServerLaunchResult(
    bool Success,
    int? ProcessId,
    string? Message,
    string ExecutablePath,
    string Arguments);

/// <summary>
/// Service contract for resolving, starting, and monitoring the external StockAnalyzer.McpServer process.
/// </summary>
public interface IMcpServerProcessService : INotifyPropertyChanged, IDisposable
{
    /// <summary>
    /// Gets whether an McpServer process instance started by this service is currently running.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Gets the process ID of the running McpServer instance, or null if not running.
    /// </summary>
    int? ProcessId { get; }

    /// <summary>
    /// Resolves the executable path to StockAnalyzer.McpServer.
    /// </summary>
    string? ResolveExecutablePath();

    /// <summary>
    /// Starts the StockAnalyzer.McpServer process using current user settings (e.g. --enable-discussion).
    /// </summary>
    Task<McpServerLaunchResult> StartServerAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the running McpServer process if it was started by this service.
    /// </summary>
    void StopServer();
}
