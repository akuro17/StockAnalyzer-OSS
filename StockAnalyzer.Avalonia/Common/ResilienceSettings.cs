namespace StockAnalyzer.Avalonia.Common;

public class CircuitBreakerSettings
{
    public int MinimumThroughput { get; set; } = 3;
    public double FailureRatio { get; set; } = 0.5;
    public int BreakDurationMs { get; set; } = 30000;
    public int SamplingDurationMs { get; set; } = 60000;

    public void Validate()
    {
        if (MinimumThroughput <= 0) throw new System.InvalidOperationException("CircuitBreakerSettings: MinimumThroughput must be positive.");
        if (FailureRatio <= 0 || FailureRatio > 1) throw new System.InvalidOperationException("CircuitBreakerSettings: FailureRatio must be between 0 and 1.");
        if (BreakDurationMs <= 0) throw new System.InvalidOperationException("CircuitBreakerSettings: BreakDurationMs must be positive.");
        if (SamplingDurationMs <= 0) throw new System.InvalidOperationException("CircuitBreakerSettings: SamplingDurationMs must be positive.");
    }
}

public class ResilienceSettings
{
    public CircuitBreakerSettings CircuitBreaker { get; set; } = new();

    public void Validate()
    {
        if (CircuitBreaker == null) throw new System.InvalidOperationException("ResilienceSettings: CircuitBreaker cannot be null.");
        CircuitBreaker.Validate();
    }
}
