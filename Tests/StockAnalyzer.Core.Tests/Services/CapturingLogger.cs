#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>Minimal <see cref="ILogger"/> that records each entry's level and rendered message.</summary>
internal class CapturingLogger : ILogger
{
    public readonly List<(LogLevel Level, string Message)> Entries = new();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception)));

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}

/// <summary>The same recorder for a constructor that asks for <see cref="ILogger{TCategoryName}"/>.</summary>
internal sealed class CapturingLogger<T> : CapturingLogger, ILogger<T>;
