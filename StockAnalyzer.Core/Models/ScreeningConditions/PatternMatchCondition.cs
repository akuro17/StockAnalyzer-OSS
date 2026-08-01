using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Models.ScreeningConditions;

/// <summary>
/// Screening condition that checks if a specific chart pattern is detected
/// in the candle data using ML-based pattern recognition.
/// </summary>
public class PatternMatchCondition : IScreeningCondition
{
    private readonly PatternRecognitionService _service;
    private readonly string _targetPattern;
    private readonly double _minimumProbability;
    private readonly int _minWindow;
    private readonly int _maxWindow;

    /// <summary>
    /// Creates a new pattern match condition.
    /// </summary>
    /// <param name="service">The pattern recognition service to use.</param>
    /// <param name="targetPattern">
    /// The pattern name to match (e.g., "HeadAndShoulders", "DoubleBottom").
    /// If empty or null, any detected pattern above the threshold is a match.
    /// </param>
    /// <param name="minimumProbability">Minimum probability threshold (0.0 to 1.0).</param>
    /// <param name="minWindow">Minimum window size for detection.</param>
    /// <param name="maxWindow">Maximum window size for detection.</param>
    public PatternMatchCondition(
        PatternRecognitionService service,
        string? targetPattern = null,
        double minimumProbability = 0.5,
        int minWindow = 20,
        int maxWindow = 60)
    {
        _service = service;
        _targetPattern = targetPattern ?? string.Empty;
        _minimumProbability = minimumProbability;
        _minWindow = minWindow;
        _maxWindow = maxWindow;
    }

    public override string ToString()
    {
        if (string.IsNullOrEmpty(_targetPattern))
            return $"Pattern Match (any, prob >= {_minimumProbability:P0})";
        return $"Pattern Match ({_targetPattern}, prob >= {_minimumProbability:P0})";
    }

    /// <summary>
    /// Checks if the specified pattern is detected in the candle data synchronously.
    /// Blocks the calling thread. For high-concurrency scenarios, use IsMetAsync instead.
    /// </summary>
    public bool IsMet(IReadOnlyList<CandleData> candles)
    {
        return IsMetAsync(candles).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Checks if the specified pattern is detected in the candle data asynchronously
    /// without blocking a thread pool thread during Python ML inference.
    /// </summary>
    public async System.Threading.Tasks.ValueTask<bool> IsMetAsync(IReadOnlyList<CandleData> candles)
    {
        if (candles == null || candles.Count < _minWindow) return false;

        try
        {
            var result = await _service.DetectAsync(
                candles, _minWindow, _maxWindow, windowStep: 5, _minimumProbability);

            if (!result.IsSuccessful || result.Patterns.Count == 0) return false;

            // If no specific pattern is targeted, any match above threshold qualifies
            if (string.IsNullOrEmpty(_targetPattern))
            {
                return result.Patterns.Any(p => p.Probability >= _minimumProbability);
            }

            // Check for the specific pattern
            return result.Patterns.Any(p =>
                p.Name == _targetPattern && p.Probability >= _minimumProbability);
        }
        catch
        {
            // If Python service is unavailable, the condition is not met (fail-safe)
            return false;
        }
    }
}
