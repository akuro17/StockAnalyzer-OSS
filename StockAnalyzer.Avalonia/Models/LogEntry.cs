using System;

namespace StockAnalyzer.Avalonia.Models;

public enum LogLevel
{
    Verbose,
    Debug,
    Information,
    Warning,
    Error,
    Fatal
}

public record LogEntry
{
    public DateTime Timestamp { get; init; }
    public LogLevel Level { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? Exception { get; init; }
}
