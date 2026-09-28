using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Models.ScreeningConditions;

/// <summary>
/// Screening condition that checks if a specific chart pattern is detected
/// in the candle data using DTW-based pattern recognition.
/// </summary>
public class PatternMatchCondition : IScreeningCondition
{
    private readonly IPatternRecognitionService _service;
    private readonly string _targetPattern;
    private readonly double _minimumProbability;
    private readonly int _minWindow;
    private readonly int _maxWindow;
    private readonly int _windowStep;

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
    /// <param name="windowStep">Sliding-window step (bars) for detection.</param>
    public PatternMatchCondition(
        IPatternRecognitionService service,
        string? targetPattern = null,
        double minimumProbability = ChartConstants.PatternRecognitionDefaultThreshold,
        int minWindow = ChartConstants.PatternRecognitionDefaultMinWindow,
        int maxWindow = ChartConstants.PatternRecognitionDefaultMaxWindow,
        int windowStep = ChartConstants.PatternRecognitionDefaultWindowStep)
    {
        _service = service;
        _targetPattern = targetPattern ?? string.Empty;
        _minimumProbability = minimumProbability;
        _minWindow = minWindow;
        _maxWindow = maxWindow;
        _windowStep = windowStep;
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
    /// without blocking the calling thread while the DTW scan runs on a worker thread.
    /// </summary>
    public async System.Threading.Tasks.ValueTask<bool> IsMetAsync(IReadOnlyList<CandleData> candles)
    {
        if (candles == null || candles.Count < _minWindow) return false;

        try
        {
            var result = await _service.DetectAsync(
                candles, _minWindow, _maxWindow, _windowStep, _minimumProbability);

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
            // If the detection fails, the condition is not met (fail-safe)
            return false;
        }
    }
}
