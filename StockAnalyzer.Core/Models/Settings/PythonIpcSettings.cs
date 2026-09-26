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
