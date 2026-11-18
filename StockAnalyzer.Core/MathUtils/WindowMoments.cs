using System;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// How a window is judged "flat" (constant), so that a statistic that divides by its standard deviation is undefined (plan section 3.1).
/// </summary>
public enum FlatWindowRule
{
    /// <summary>Dimensionless returns: flat when the population variance is not above <see cref="ReturnStatistics.VarianceEpsilon"/> (1e-14).</summary>
    Returns,

    /// <summary>
    /// A series in its own unit: flat when the population deviation is below the larger of <see cref="ComparisonSeriesDefaults.FlatWindowDeviationEpsilon"/> (1e-10, absolute, the rule of the Z-Score)
    /// and <see cref="WindowMoments.SeriesRelativeFlatEpsilon"/> times the absolute mean. The relative part keeps rounding noise of a large constant (a value near 1e6 with a fraction
    /// has a two-pass deviation of about 5e-10) from passing as a real spread.
    /// </summary>
    Series,
}

/// <summary>
/// The centred power sums of one window, computed in two passes (mean first, then the sums of (x - mean)^k, k = 2, 3, 4), and the moment statistics derived from them.
/// A statistic that is undefined (window too short, flat window, non-finite result) is <see cref="double.NaN"/>; callers narrow with
/// <c>IndicatorCalculationHelper.ToDecimalOrNull</c>, which turns NaN into null.
/// Definitions (population, divisor n): <c>b1 = (M3 / n) / (M2 / n)^(3/2)</c>, <c>b2 = (M4 / n) / (M2 / n)^2 - 3</c>.
/// Sample-adjusted (Fisher): <c>G1 = sqrt(n (n - 1)) / (n - 2) * b1</c> (n &gt;= 3), <c>G2 = (n - 1) / ((n - 2)(n - 3)) * ((n + 1) b2 + 6)</c> (n &gt;= 4);
/// sample standard deviation <c>sqrt(M2 / (n - 1))</c> (n &gt;= 2).
/// </summary>
public readonly struct WindowMoments
{
    /// <summary>
    /// The relative part of the <see cref="FlatWindowRule.Series"/> threshold: a deviation below this fraction of the absolute mean is rounding noise, not a spread (double arithmetic
    /// resolves about 2e-16 of a value, so 1e-12 keeps four orders of magnitude of margin and no real series varies by less). Owner decision D-3 = A of the peer-review follow-up.
    /// </summary>
    public const double SeriesRelativeFlatEpsilon = 1e-12;

    private readonly FlatWindowRule _rule;

    private WindowMoments(int count, double mean, double m2, double m3, double m4, FlatWindowRule rule)
    {
        Count = count;
        Mean = mean;
        M2 = m2;
        M3 = m3;
        M4 = m4;
        _rule = rule;
    }

    /// <summary>Number of values n of the window.</summary>
    public int Count { get; }

    /// <summary>Arithmetic mean.</summary>
    public double Mean { get; }

    /// <summary>Sum of (x - mean)^2.</summary>
    public double M2 { get; }

    /// <summary>Sum of (x - mean)^3.</summary>
    public double M3 { get; }

    /// <summary>Sum of (x - mean)^4.</summary>
    public double M4 { get; }

    /// <summary>Population variance M2 / n.</summary>
    public double PopulationVariance => Count > 0 ? M2 / Count : double.NaN;

    /// <summary>True when the window is flat under the rule given to <see cref="TryCompute"/>; every statistic that divides by the deviation is then NaN.</summary>
    public bool IsFlat => _rule switch
    {
        // "!(x >= threshold)": a NaN deviation, mean or threshold lands on the flat side.
        FlatWindowRule.Series => !(Math.Sqrt(PopulationVariance) >= Math.Max(ComparisonSeriesDefaults.FlatWindowDeviationEpsilon, SeriesRelativeFlatEpsilon * Math.Abs(Mean))),
        _ => !(PopulationVariance > ReturnStatistics.VarianceEpsilon),
    };

    /// <summary>Sample standard deviation sqrt(M2 / (n - 1)); NaN below two values. Defined for a flat window too (a magnitude, no division).</summary>
    public double SampleStandardDeviation => Count >= 2 ? Finite(Math.Sqrt(M2 / (Count - 1))) : double.NaN;

    /// <summary>Population skewness b1; NaN for a flat window.</summary>
    public double PopulationSkewness => IsFlat ? double.NaN : Finite((M3 / Count) / Math.Pow(M2 / Count, 1.5));

    /// <summary>Population excess kurtosis b2 (a normal distribution is 0); NaN for a flat window.</summary>
    public double PopulationExcessKurtosis => IsFlat ? double.NaN : Finite(((M4 / Count) / ((M2 / Count) * (M2 / Count))) - 3.0);

    /// <summary>Sample-adjusted skewness G1; NaN below three values or for a flat window.</summary>
    public double SampleSkewness => Count >= 3 && !IsFlat ? Finite(Math.Sqrt((double)Count * (Count - 1)) / (Count - 2) * PopulationSkewness) : double.NaN;

    /// <summary>Sample-adjusted excess kurtosis G2; NaN below four values or for a flat window.</summary>
    public double SampleExcessKurtosis =>
        Count >= 4 && !IsFlat
            ? Finite((Count - 1.0) / ((Count - 2.0) * (Count - 3.0)) * (((Count + 1.0) * PopulationExcessKurtosis) + 6.0))
            : double.NaN;

    /// <summary>
    /// Computes the moments of <paramref name="window"/>. False (and a default value) for an empty window or when any element is NaN or infinite, or when a power sum overflows.
    /// </summary>
    public static bool TryCompute(ReadOnlySpan<double> window, FlatWindowRule rule, out WindowMoments moments)
    {
        moments = default;
        int n = window.Length;
        if (n == 0)
        {
            return false;
        }

        double sum = 0.0;
        for (int i = 0; i < n; i++)
        {
            double value = window[i];
            if (!double.IsFinite(value))
            {
                return false;
            }

            sum += value;
        }

        double mean = sum / n;
        double m2 = 0.0;
        double m3 = 0.0;
        double m4 = 0.0;
        for (int i = 0; i < n; i++)
        {
            double deviation = window[i] - mean;
            double square = deviation * deviation;
            m2 += square;
            m3 += square * deviation;
            m4 += square * square;
        }

        if (!double.IsFinite(mean) || !double.IsFinite(m2) || !double.IsFinite(m3) || !double.IsFinite(m4))
        {
            return false;
        }

        moments = new WindowMoments(n, mean, m2, m3, m4, rule);
        return true;
    }

    private static double Finite(double value) => double.IsFinite(value) ? value : double.NaN;
}
