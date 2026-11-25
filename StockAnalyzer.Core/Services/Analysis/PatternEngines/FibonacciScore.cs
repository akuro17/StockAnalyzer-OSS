using System;

namespace StockAnalyzer.Core.Services.Analysis.PatternEngines;

/// <summary>
/// The Fibonacci confidence score of the specification (<c>00_COMMON_MATHEMATICAL_DEFINITIONS.md</c> 6) used by the harmonic and Elliott engines: a trapezoid membership of one measured ratio and
/// the weighted sum of several. All arithmetic is <see cref="decimal"/>.
/// </summary>
public static class FibonacciScore
{
    /// <summary>
    /// S(r): 1 inside the ideal interval, a straight line down to 0 at the edges of the allowed interval, 0 outside it. A single ideal point has <paramref name="idealMin"/> equal to <paramref name="idealMax"/>.
    /// Requires <c>allowedMin &lt;= idealMin &lt;= idealMax &lt;= allowedMax</c>. When an edge has no slope (the allowed edge equals the ideal edge) the value just outside the ideal interval is 0.
    /// </summary>
    public static decimal Trapezoid(decimal ratio, decimal idealMin, decimal idealMax, decimal allowedMin, decimal allowedMax)
    {
        if (!(allowedMin <= idealMin && idealMin <= idealMax && idealMax <= allowedMax))
        {
            throw new ArgumentException("Intervals must satisfy allowedMin <= idealMin <= idealMax <= allowedMax.");
        }

        if (ratio >= idealMin && ratio <= idealMax)
        {
            return 1m;
        }

        if (ratio > allowedMin && ratio < idealMin && idealMin > allowedMin)
        {
            return (ratio - allowedMin) / (idealMin - allowedMin);
        }

        if (ratio > idealMax && ratio < allowedMax && allowedMax > idealMax)
        {
            return (allowedMax - ratio) / (allowedMax - idealMax);
        }

        return 0m;
    }

    /// <summary>The weighted sum of the per-ratio scores; the weights must sum to 1 (checked with the tolerance of one thousandth).</summary>
    public static decimal Weighted(ReadOnlySpan<decimal> scores, ReadOnlySpan<decimal> weights)
    {
        if (scores.Length != weights.Length || scores.Length == 0)
        {
            throw new ArgumentException("Scores and weights must have the same non-zero length.");
        }

        decimal sum = 0m;
        decimal weightSum = 0m;
        for (int i = 0; i < scores.Length; i++)
        {
            sum += scores[i] * weights[i];
            weightSum += weights[i];
        }

        if (Math.Abs(weightSum - 1m) > WeightSumTolerance)
        {
            throw new ArgumentException("Weights must sum to 1.");
        }

        return sum;
    }

    private const decimal WeightSumTolerance = 0.001m;
}
