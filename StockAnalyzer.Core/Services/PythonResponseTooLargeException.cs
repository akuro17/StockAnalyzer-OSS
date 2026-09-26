namespace StockAnalyzer.Core.Services;

/// <summary>
/// Thrown when one response line from Python exceeds the configured maximum. Deliberately not an <see cref="System.IO.IOException"/>
/// (nor an <see cref="System.InvalidOperationException"/>): those are retried, but the same request would produce the same oversized
/// response, so re-running the transaction cannot succeed. The connection is still torn down by the caller because the stream is desynchronized.
/// </summary>
public sealed class PythonResponseTooLargeException : Exception
{
    public PythonResponseTooLargeException(int maxLineBytes)
        : base($"Python response line exceeds the maximum of {maxLineBytes} bytes.")
    {
        MaxLineBytes = maxLineBytes;
    }

    public int MaxLineBytes { get; }
}
