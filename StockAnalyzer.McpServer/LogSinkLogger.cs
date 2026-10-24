using Microsoft.Extensions.Logging;

namespace StockAnalyzer.McpServer;

/// <summary>
/// Lets a Core component that logs through <see cref="ILogger{TCategoryName}"/> write to the server's <see cref="LogSink"/> (standard error). Only the
/// formatted message is written, plus the type of an attached exception: an exception's own text can carry a path, and the log of the execution
/// gateway holds job ids and states only.
/// </summary>
internal sealed class LogSinkLogger<T> : ILogger<T>
{
    private readonly LogSink _sink;

    public LogSinkLogger(LogSink sink)
    {
        _sink = sink;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel is >= LogLevel.Information and < LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        string message = formatter(state, null);
        if (exception is not null)
        {
            message += $" ({exception.GetType().Name})";
        }

        if (logLevel >= LogLevel.Error)
        {
            _sink.Error(message);
        }
        else if (logLevel == LogLevel.Warning)
        {
            _sink.Info($"Warning: {message}");
        }
        else
        {
            _sink.Info(message);
        }
    }
}
