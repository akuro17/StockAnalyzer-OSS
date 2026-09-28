using System;

namespace StockAnalyzer.Avalonia.Common;

/// <summary>
/// Strongly-typed configuration for the Python process manager.
/// Bound from the "Python" section of appsettings.json via IOptions.
/// Default values match the original hardcoded constants in PythonProcessManager.
/// </summary>
public class PythonSettings
{
    public string ScriptDirectory { get; set; } = "Scripts";
    public string? PythonPath { get; set; }
    public string ServerScriptName { get; set; } = "server.py";
    public int MaxRetries { get; set; } = 3;
    public int BackoffMs { get; set; } = 1000;
    public int HealthCheckIntervalMs { get; set; } = 5000;
    public int PipeConnectPollIntervalMs { get; set; } = 100;
    public int DisposeWaitMs { get; set; } = 1000;
    public int SyncTimeoutMinutes { get; set; } = 2;

    public string[] EssentialPackages { get; set; } = { "setuptools", "wheel", "polars", "pandas", "yfinance", "pyarrow" };

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ScriptDirectory)) throw new System.InvalidOperationException("PythonSettings: ScriptDirectory cannot be empty.");
        if (string.IsNullOrWhiteSpace(ServerScriptName)) throw new System.InvalidOperationException("PythonSettings: ServerScriptName cannot be empty.");
        if (MaxRetries < 0) throw new System.InvalidOperationException("PythonSettings: MaxRetries cannot be negative.");
        if (BackoffMs <= 0) throw new System.InvalidOperationException("PythonSettings: BackoffMs must be positive.");
        if (HealthCheckIntervalMs <= 0) throw new System.InvalidOperationException("PythonSettings: HealthCheckIntervalMs must be positive.");
        if (PipeConnectPollIntervalMs <= 0) throw new System.InvalidOperationException("PythonSettings: PipeConnectPollIntervalMs must be positive.");
        if (SyncTimeoutMinutes <= 0) throw new System.InvalidOperationException("PythonSettings: SyncTimeoutMinutes must be positive.");
    }
}
