using System;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Tests and tail statistics of a window of dimensionless returns: the Jarque-Bera normality test, the Cornish-Fisher Value at Risk and the Probabilistic Sharpe Ratio.
/// <c>double</c> functions of dimensionless arguments; an undefined result is <see cref="double.NaN"/> (never an exception) and the caller narrows with
/// <c>IndicatorCalculationHelper.ToDecimalOrNull</c>. The flat-window guard is the one of <see cref="WindowMoments"/> (rule <see cref="FlatWindowRule.Returns"/>).
/// Specification: docs/indicators_formulas.md, records indicators_formulas_0596 (Jarque-Bera) and indicators_formulas_0602 (Cornish-Fisher VaR and the Probabilistic Sharpe Ratio).
/// </summary>
public static class NormalityAndTailMath
{
    /// <summary>Fewest values of a Jarque-Bera test.</summary>
    public const int MinJarqueBeraCount = 8;

    /// <summary>Fewest values of a Cornish-Fisher VaR and of a Probabilistic Sharpe Ratio (the sample excess kurtosis G2 needs four).</summary>
    public const int MinTailStatisticCount = 4;

    /// <summary>Lowest confidence level of <see cref="TryCornishFisherValueAtRisk"/> (alpha = 0.5, z = 0).</summary>
    public const double MinConfidenceLevel = 0.50;

    /// <summary>Highest confidence level of <see cref="TryCornishFisherValueAtRisk"/>.</summary>
    public const double MaxConfidenceLevel = 0.999;

    /// <summary>
    /// Jarque-Bera statistic <c>JB = n / 6 (b1^2 + b2^2 / 4)</c> from the POPULATION skewness b1 and excess kurtosis b2, and its exact p-value under the chi-square law with
    /// two degrees of freedom, <c>exp(-JB / 2)</c>. False (both NaN) for fewer than <see cref="MinJarqueBeraCount"/> values or a flat window.
    /// </summary>
    public static bool TryJarqueBera(in WindowMoments moments, out double statistic, out double pValue)
    {
        statistic = double.NaN;
        pValue = double.NaN;
        if (moments.Count < MinJarqueBeraCount || moments.IsFlat)
        {
            return false;
        }

        double b1 = moments.PopulationSkewness;
        double b2 = moments.PopulationExcessKurtosis;
        double jb = moments.Count / 6.0 * ((b1 * b1) + (b2 * b2 / 4.0));
        if (!double.IsFinite(jb))
        {
            return false;
        }

        statistic = jb;
        pValue = Math.Exp(-jb / 2.0);
        return true;
    }

    /// <summary>Cornish-Fisher adjusted standard normal quantile <c>z + (z^2 - 1) G1 / 6 + (z^3 - 3z) G2 / 24 - (2 z^3 - 5z) G1^2 / 36</c>.</summary>
    public static double CornishFisherQuantile(double z, double skewness, double excessKurtosis) =>
        z
        + (((z * z) - 1.0) * skewness / 6.0)
        + (((z * z * z) - (3.0 * z)) * excessKurtosis / 24.0)
        - ((((2.0 * z * z * z) - (5.0 * z)) * skewness * skewness) / 36.0);

    /// <summary>
    /// Derivative of <see cref="CornishFisherQuantile"/> in z, <c>q(z) = 1 + z G1 / 3 + (3 z^2 - 3) G2 / 24 - (6 z^2 - 5) G1^2 / 36</c>. The expansion is a valid quantile function
    /// only where this is positive (owner decision U-7 = A); the value of a VaR depends on every point between the median and the evaluation point, so the guard of
    /// <see cref="TryCornishFisherValueAtRisk"/> is <see cref="IsCornishFisherMonotoneOnTail"/> (the whole interval, owner decision D-1 = A of the peer-review follow-up).
    /// </summary>
    public static double CornishFisherDerivative(double z, double skewness, double excessKurtosis) =>
        1.0
        + (z * skewness / 3.0)
        + (((3.0 * z * z) - 3.0) * excessKurtosis / 24.0)
        - ((((6.0 * z * z) - 5.0) * skewness * skewness) / 36.0);

    /// <summary>
    /// True when <see cref="CornishFisherDerivative"/> is positive at every point of the interval from <paramref name="z"/> (at most 0) to the median 0, that is when
    /// <see cref="CornishFisherQuantile"/> is increasing there and the adjusted quantile is a valid one. The derivative is the parabola <c>q(u) = a u^2 + b u + c</c> with
    /// <c>a = G2 / 8 - G1^2 / 6</c>, <c>b = G1 / 3</c>, <c>c = 1 - G2 / 8 + 5 G1^2 / 36 = q(0)</c>, so: (1) both ends must be positive, (2) only for a valley-shaped parabola
    /// (<c>a &gt; 0</c>) the vertex <c>-b / (2 a)</c> can be the lowest point of the interval: when it lies strictly between z and 0 its value must be positive too.
    /// For <c>a &lt;= 0</c> (hill-shaped or straight) the lowest point is an end, the division is never reached (a = 0 exactly at G1 = G2 = 0). A NaN argument gives false.
    /// </summary>
    internal static bool IsCornishFisherMonotoneOnTail(double z, double skewness, double excessKurtosis)
    {
        if (!(CornishFisherDerivative(z, skewness, excessKurtosis) > 0.0) || !(CornishFisherDerivative(0.0, skewness, excessKurtosis) > 0.0))
        {
            return false;
        }

        double a = (excessKurtosis / 8.0) - (skewness * skewness / 6.0);
        if (a > 0.0)
        {
            double vertex = -(skewness / 3.0) / (2.0 * a);
            if (vertex > z && vertex < 0.0)
            {
                return CornishFisherDerivative(vertex, skewness, excessKurtosis) > 0.0;
            }
        }

        return true;
    }

    /// <summary>
    /// One-bar Value at Risk of a window of returns as the lower-tail RETURN quantile (a loss is negative, the sign of <c>CoreVarIndicator</c>):
    /// <paramref name="gaussianVar"/> = <c>mean + z s</c> and <paramref name="cornishFisherVar"/> = <c>mean + z_CF s</c>, with z = Phi^-1(1 - confidence), the sample mean and the
    /// sample standard deviation s (divisor n - 1). False (both NaN) for fewer than <see cref="MinTailStatisticCount"/> values or a confidence level outside
    /// [<see cref="MinConfidenceLevel"/>, <see cref="MaxConfidenceLevel"/>]. For a flat window the Gaussian VaR is the mean (s = 0) and the Cornish-Fisher VaR is NaN
    /// (G1 and G2 are undefined); the Cornish-Fisher VaR is also NaN where the expansion is not monotone between the median and the quantile (<see cref="IsCornishFisherMonotoneOnTail"/> is false).
    /// </summary>
    public static bool TryCornishFisherValueAtRisk(in WindowMoments moments, double confidenceLevel, out double gaussianVar, out double cornishFisherVar)
    {
        gaussianVar = double.NaN;
        cornishFisherVar = double.NaN;
        if (moments.Count < MinTailStatisticCount || !(confidenceLevel >= MinConfidenceLevel && confidenceLevel <= MaxConfidenceLevel))
        {
            return false;
        }

        double z = SpecialFunctionsMath.NormalQuantile(1.0 - confidenceLevel);
        double s = moments.SampleStandardDeviation;
        if (!double.IsFinite(z) || !double.IsFinite(s))
        {
            return false;
        }

        gaussianVar = moments.Mean + (z * s);
        double g1 = moments.SampleSkewness;
        double g2 = moments.SampleExcessKurtosis;
        if (double.IsFinite(g1) && double.IsFinite(g2) && IsCornishFisherMonotoneOnTail(z, g1, g2))
        {
            double zCf = CornishFisherQuantile(z, g1, g2);
            double value = moments.Mean + (zCf * s);
            cornishFisherVar = double.IsFinite(value) ? value : double.NaN;
        }

        return true;
    }

    /// <summary>
    /// Probabilistic Sharpe Ratio against the benchmark 0: <c>Phi(SR sqrt(n - 1) / sqrt(1 - G1 SR + (G2 + 2) SR^2 / 4))</c> with the per-bar <c>SR = mean / s</c> (risk-free rate 0,
    /// not annualised). NaN for fewer than <see cref="MinTailStatisticCount"/> values, a flat window, or a radicand not above <see cref="ReturnStatistics.VarianceEpsilon"/>.
    /// </summary>
    public static double ProbabilisticSharpeRatio(in WindowMoments moments)
    {
        if (moments.Count < MinTailStatisticCount || moments.IsFlat)
        {
            return double.NaN;
        }

        return ProbabilisticSharpeRatio(moments.Mean / moments.SampleStandardDeviation, moments.SampleSkewness, moments.SampleExcessKurtosis, moments.Count);
    }

    /// <summary>The formula of <see cref="ProbabilisticSharpeRatio(in WindowMoments)"/> for given figures: sharpe ratio, sample skewness, sample excess kurtosis and count.</summary>
    public static double ProbabilisticSharpeRatio(double sharpeRatio, double skewness, double excessKurtosis, int count)
    {
        double radicand = 1.0 - (skewness * sharpeRatio) + ((excessKurtosis + 2.0) / 4.0 * sharpeRatio * sharpeRatio);
        if (!double.IsFinite(radicand) || !(radicand > ReturnStatistics.VarianceEpsilon) || count < 2)
        {
            return double.NaN;
        }

        double statistic = sharpeRatio * Math.Sqrt(count - 1.0) / Math.Sqrt(radicand);
        return double.IsFinite(statistic) ? SpecialFunctionsMath.NormalCdf(statistic) : double.NaN;
    }
}
