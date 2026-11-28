using System;

namespace StockAnalyzer.Core.Models.Settings;

/// <summary>
/// Python IPC safety limits bound from the "PythonIpc" section of appsettings.json (via <c>IStockAnalyzerSettings</c>).
/// The defaults live here only; the interface defaults and the configuration file both follow them.
/// </summary>
public class PythonIpcSettings
{
    /// <summary>Longest wait (ms) for one request/response exchange with the Python process before the connection is treated as hung.</summary>
    public int RequestTimeoutMs { get; set; } = 60000;

    /// <summary>Largest accepted single response line (bytes) from Python; a longer line aborts the connection instead of exhausting memory.</summary>
    public int MaxResponseBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>
    /// Most times one transaction may be re-run after a request timeout. A timeout can mean a hung process (a re-run recovers it) or a
    /// healthy but slow computation (a re-run repeats the whole cost), so timeouts get a tighter budget than connection errors,
    /// which keep using <c>PythonMaxRetries</c>. 0 disables re-running after a timeout.
    /// </summary>
    public int MaxTimeoutRetries { get; set; } = 1;

    public void Validate()
    {
        if (RequestTimeoutMs <= 0) throw new InvalidOperationException("PythonIpcSettings: RequestTimeoutMs must be positive.");
        if (MaxResponseBytes <= 0) throw new InvalidOperationException("PythonIpcSettings: MaxResponseBytes must be positive.");
        if (MaxTimeoutRetries < 0) throw new InvalidOperationException("PythonIpcSettings: MaxTimeoutRetries must not be negative.");
    }
}

/// <summary>
/// Hard SSoT ceilings mirrored by <c>StockAnalyzer.Core/Scripts/server.py</c> for Python IPC command arguments and Arrow transfers.
/// </summary>
public static class PythonIpcLimits
{
    public const int MinWindowSize = 4;
    public const int MaxWindowSize = 4096;
    public const int MinTopK = 1;
    public const int MaxTopK = 100;
    public const int MinFutureSteps = 1;
    public const int MaxFutureSteps = 500;
    public const int MinQueryLength = 2;
    public const int MaxQueryLength = 1000;
    public const int MinLookback = 0;
    public const int MaxLookback = 50000;
    public const int MinWarpingRadius = -1;
    public const int MaxWarpingRadius = 500;
    public const int MinQueryStartIndex = -1;
    public const double MinThreshold = 0.0;
    public const double MaxThreshold = 1.0;
    public const int MaxTransferRows = 100000;
    public const int MaxTransferColumns = 32;
}
