namespace StockAnalyzer.Core.Services;

/// <summary>
/// Provides read-only access to the resilience pipeline state (Circuit Breaker).
/// Allows UI/ViewModel layers to react to service degradation without coupling to Polly internals.
/// </summary>
public interface IResilienceStateProvider
{
    /// <summary>
    /// Current circuit breaker state.
    /// </summary>
    CircuitState CurrentState { get; }

    /// <summary>
    /// Raised when the circuit breaker state transitions (e.g., Closed -> Open).
    /// </summary>
    event EventHandler<CircuitStateChangedEventArgs>? StateChanged;
}

/// <summary>
/// Represents the circuit breaker states aligned with Polly v8 CircuitBreakerStateProvider terminology.
/// </summary>
public enum CircuitState
{
    /// <summary>Requests flow normally.</summary>
    Closed,

    /// <summary>Requests are blocked; the service is considered unavailable.</summary>
    Open,

    /// <summary>A single probe request is allowed through to test recovery.</summary>
    HalfOpen,

    /// <summary>Manually isolated; all requests are blocked.</summary>
    Isolated
}

/// <summary>
/// Event args for circuit breaker state transitions.
/// </summary>
public sealed class CircuitStateChangedEventArgs : EventArgs
{
    public CircuitState OldState { get; }
    public CircuitState NewState { get; }
    public Exception? TriggeringException { get; }

    public CircuitStateChangedEventArgs(CircuitState oldState, CircuitState newState, Exception? triggeringException = null)
    {
        OldState = oldState;
        NewState = newState;
        TriggeringException = triggeringException;
    }
}
