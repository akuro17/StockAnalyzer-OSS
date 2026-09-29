namespace StockAnalyzer.Core.Services;

/// <summary>
/// Thrown when the circuit breaker is in Open state and the Python IPC service is unavailable.
/// Callers should catch this to implement Graceful Degradation (e.g., returning IndicatorResult.Failure).
/// </summary>
public sealed class PythonUnavailableException : Exception
{
    public PythonUnavailableException()
        : base("Python service is temporarily unavailable (circuit breaker open).")
    {
    }

    public PythonUnavailableException(string message)
        : base(message)
    {
    }

    public PythonUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
