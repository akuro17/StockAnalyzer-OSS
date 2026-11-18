using System;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>Downside / upside risk of one window of simple returns. Units: ratio per bar (a return of 0.01 is 1 %); the Sortino ratio and the asymmetry are dimensionless.</summary>
/// <param name="DownsideDeviation">sigma-: square root of the mean of min(0, r - target)^2.</param>
/// <param name="UpsideDeviation">sigma+: square root of the mean of max(0, r - target)^2.</param>
/// <param name="RollingSortino">(mean(r) - target) / sigma-; null when sigma- is below <see cref="ComparisonSeriesDefaults.FlatWindowDeviationEpsilon"/>.</param>
/// <param name="SemivarianceAsymmetry">(sigma+^2 - sigma-^2) / (sigma+^2 + sigma-^2), in [-1, 1]; null when the denominator is not above <see cref="ReturnStatistics.DecimalVarianceEpsilon"/>.</param>
public readonly record struct DownsideStatistics(decimal DownsideDeviation, decimal UpsideDeviation, decimal? RollingSortino, decimal? SemivarianceAsymmetry);

/// <summary>Which part of the benchmark returns a conditional beta is estimated on.</summary>
public enum BenchmarkRegime
{
    /// <summary>Dates with a strictly negative benchmark return.</summary>
    Down,

    /// <summary>Dates with a strictly positive benchmark return.</summary>
    Up,
}

/// <summary>
/// Asymmetric risk statistics of simple returns, computed in <see cref="decimal"/> (the returns are ratios shown and compared as values, owner decision U-2 = A);
/// only the square root goes through <c>double</c> and back through <see cref="IndicatorCalculationHelper.ToDecimalOrNull"/>. Every undefined result is <c>null</c> (never 0, never an exception).
/// Specification: docs/indicators_formulas.md, records indicators_formulas_0600 (downside deviation) and indicators_formulas_0605 (conditional beta).
/// </summary>
public static class AsymmetricRiskMath
{
    /// <summary>Fewest dates of the benchmark regime a conditional beta needs.</summary>
    public const int MinConditionalBetaCount = 3;

    private static readonly decimal FlatDeviationEpsilon = (decimal)ComparisonSeriesDefaults.FlatWindowDeviationEpsilon;

    /// <summary>
    /// sigma-, sigma+, the Sortino ratio and the semivariance asymmetry of <paramref name="returns"/> against <paramref name="targetReturn"/> (the minimum acceptable return, 0 by default).
    /// The variances divide by the window length n (population definition, like the Z-Score). False for an empty window or a decimal overflow.
    /// A flat window still has defined deviations (0 when every return equals the target); only the two quotients can be null.
    /// </summary>
    public static bool TryComputeDownsideStatistics(ReadOnlySpan<decimal> returns, decimal targetReturn, out DownsideStatistics statistics)
    {
        statistics = default;
        int n = returns.Length;
        if (n == 0)
        {
            return false;
        }

        try
        {
            decimal sum = 0m;
            decimal downside = 0m;
            decimal upside = 0m;
            for (int i = 0; i < n; i++)
            {
                decimal r = returns[i];
                decimal excess = r - targetReturn;
                sum += r;
                if (excess < 0m)
                {
                    downside += excess * excess;
                }
                else
                {
                    upside += excess * excess;
                }
            }

            decimal downsideVariance = downside / n;
            decimal upsideVariance = upside / n;
            if (IndicatorCalculationHelper.ToDecimalOrNull(Math.Sqrt((double)downsideVariance)) is not { } sigmaMinus
                || IndicatorCalculationHelper.ToDecimalOrNull(Math.Sqrt((double)upsideVariance)) is not { } sigmaPlus)
            {
                return false;
            }

            decimal? sortino = sigmaMinus >= FlatDeviationEpsilon ? ((sum / n) - targetReturn) / sigmaMinus : null;
            decimal totalVariance = downsideVariance + upsideVariance;
            decimal? asymmetry = totalVariance > ReturnStatistics.DecimalVarianceEpsilon ? (upsideVariance - downsideVariance) / totalVariance : null;
            statistics = new DownsideStatistics(sigmaMinus, sigmaPlus, sortino, asymmetry);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>
    /// Conditional beta of <paramref name="asset"/> on <paramref name="benchmark"/> inside the subset of dates where the benchmark return is strictly negative
    /// (<see cref="BenchmarkRegime.Down"/>) or strictly positive (<see cref="BenchmarkRegime.Up"/>; a zero benchmark return belongs to neither):
    /// <c>sum (a - mean_a)(b - mean_b) / sum (b - mean_b)^2</c> with the means taken inside the subset (the OLS slope within the subset; NOT the lower-partial-moment downside beta).
    /// Null when the series differ in length, the subset has fewer than <see cref="MinConditionalBetaCount"/> dates, the benchmark's sample variance in the subset
    /// (divisor count - 1) is not above <see cref="ReturnStatistics.DecimalVarianceEpsilon"/>, or a decimal overflow occurs.
    /// </summary>
    public static decimal? ConditionalBeta(ReadOnlySpan<decimal> asset, ReadOnlySpan<decimal> benchmark, BenchmarkRegime regime)
    {
        if (asset.Length != benchmark.Length)
        {
            return null;
        }

        try
        {
            int count = 0;
            decimal sumAsset = 0m;
            decimal sumBenchmark = 0m;
            for (int i = 0; i < benchmark.Length; i++)
            {
                if (InRegime(benchmark[i], regime))
                {
                    count++;
                    sumAsset += asset[i];
                    sumBenchmark += benchmark[i];
                }
            }

            if (count < MinConditionalBetaCount)
            {
                return null;
            }

            decimal meanAsset = sumAsset / count;
            decimal meanBenchmark = sumBenchmark / count;
            decimal cross = 0m;
            decimal benchmarkSquares = 0m;
            for (int i = 0; i < benchmark.Length; i++)
            {
                if (InRegime(benchmark[i], regime))
                {
                    decimal b = benchmark[i] - meanBenchmark;
                    cross += (asset[i] - meanAsset) * b;
                    benchmarkSquares += b * b;
                }
            }

            return benchmarkSquares / (count - 1m) <= ReturnStatistics.DecimalVarianceEpsilon ? null : cross / benchmarkSquares;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static bool InRegime(decimal benchmarkReturn, BenchmarkRegime regime) =>
        regime == BenchmarkRegime.Down ? benchmarkReturn < 0m : benchmarkReturn > 0m;
}
