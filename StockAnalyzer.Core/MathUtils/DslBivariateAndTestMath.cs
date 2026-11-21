using System;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Zero-allocation bivariate statistics, correlations and hypothesis tests of the DSL v5 (covariance, Pearson/Spearman/Kendall, cross-correlation,
/// one- and two-sample t-tests, Jarque-Bera, Ljung-Box, two-sample Kolmogorov-Smirnov). Inputs are DSL 1D arrays (<c>&lt;= 512</c> elements) already
/// converted to <see cref="double"/>; working buffers are stack-allocated. Existing kernels (<see cref="NormalityAndTailMath"/>, <see cref="SerialDependenceMath"/>,
/// <see cref="DslSpecialFunctionsMath"/>) are reused unchanged.
/// </summary>
public static class DslBivariateAndTestMath
{
    /// <summary>A variance not above this value is treated as zero (<see cref="MathErrorCode.DivisionByZero"/>).</summary>
    public const double VarianceFloor = 1e-12;

    /// <summary>Welch's standard-error variance <c>s_x^2/n_x + s_y^2/n_y</c> not above this value is treated as zero.</summary>
    public const double WelchVarianceFloor = 1e-24;

    /// <summary>Number of terms of the asymptotic Kolmogorov series.</summary>
    public const int KsSeriesTerms = 100;

    private const double KsSmallSampleOffset = 0.12;
    private const double KsSmallSampleSlope = 0.11;
    private const int TTestResultLength = 3;

    // ---- Covariance & Correlation ----

    /// <summary>Two-pass centered covariance: sample (<c>/ (n - 1)</c>, <c>n &gt;= 2</c>) or population (<c>/ n</c>, <c>n &gt;= 1</c>).</summary>
    public static MathResult<double> Covariance(scoped ReadOnlySpan<double> x, scoped ReadOnlySpan<double> y, bool sample)
    {
        if (x.Length != y.Length)
        {
            return MathResult<double>.Fail(MathErrorCode.DimensionMismatch);
        }

        int n = x.Length;
        if (n < (sample ? 2 : 1))
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        double meanX = Mean(x);
        double meanY = Mean(y);
        double sxy = 0.0;
        for (int i = 0; i < n; i++)
        {
            sxy += (x[i] - meanX) * (y[i] - meanY);
        }

        return Finite(sxy / (sample ? n - 1 : n));
    }

    /// <summary>
    /// Pearson correlation clamped to <c>[-1, 1]</c>. <c>n &lt; 2</c> is a domain error; a population variance <c>S / n</c> of either series not above
    /// <see cref="VarianceFloor"/> is a division by zero (this also covers the sample variance, which is never smaller).
    /// </summary>
    public static MathResult<double> CorrPearson(scoped ReadOnlySpan<double> x, scoped ReadOnlySpan<double> y)
    {
        if (x.Length != y.Length)
        {
            return MathResult<double>.Fail(MathErrorCode.DimensionMismatch);
        }

        int n = x.Length;
        if (n < 2)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        double meanX = Mean(x);
        double meanY = Mean(y);
        double sxx = 0.0;
        double syy = 0.0;
        double sxy = 0.0;
        for (int i = 0; i < n; i++)
        {
            double dx = x[i] - meanX;
            double dy = y[i] - meanY;
            sxx += dx * dx;
            syy += dy * dy;
            sxy += dx * dy;
        }

        if (sxx / n <= VarianceFloor || syy / n <= VarianceFloor)
        {
            return MathResult<double>.Fail(MathErrorCode.DivisionByZero);
        }

        return Finite(Math.Clamp(sxy / Math.Sqrt(sxx * syy), -1.0, 1.0));
    }

    /// <summary>Spearman correlation: Pearson correlation of the 1-based ascending average ranks.</summary>
    public static MathResult<double> CorrSpearman(scoped ReadOnlySpan<double> x, scoped ReadOnlySpan<double> y)
    {
        if (x.Length != y.Length)
        {
            return MathResult<double>.Fail(MathErrorCode.DimensionMismatch);
        }

        int n = x.Length;
        if (n < 2)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        Span<double> rankX = stackalloc double[n];
        Span<double> rankY = stackalloc double[n];
        Span<int> scratch = stackalloc int[n];
        ComputeAverageRanks(x, rankX, scratch);
        ComputeAverageRanks(y, rankY, scratch);
        return CorrPearson(rankX, rankY);
    }

    /// <summary>
    /// Kendall's tau-b <c>(n_c - n_d) / sqrt((n_0 - n_1)(n_0 - n_2))</c>. Counting all pairs <c>i &lt; j</c> with equal values yields <c>sum t (t - 1) / 2</c> directly,
    /// so no tie grouping pass is needed. Consumes <c>max(1, n (n - 1) / 32)</c> budget units; <c>n_0 == n_1</c> or <c>n_0 == n_2</c> is a division by zero.
    /// </summary>
    public static MathResult<double> CorrKendall(scoped ReadOnlySpan<double> x, scoped ReadOnlySpan<double> y, ref DslExecutionBudget budget)
    {
        if (x.Length != y.Length)
        {
            return MathResult<double>.Fail(MathErrorCode.DimensionMismatch);
        }

        int n = x.Length;
        if (n < 2)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        long pairs = (long)n * (n - 1) / 2;
        if (!budget.TryConsume((int)Math.Max(1L, (long)n * (n - 1) / 32)))
        {
            return MathResult<double>.Fail(MathErrorCode.BudgetExceeded);
        }

        long tiesX = 0;
        long tiesY = 0;
        long concordant = 0;
        long discordant = 0;
        for (int i = 0; i < n - 1; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                bool tieX = x[i] == x[j];
                bool tieY = y[i] == y[j];
                if (tieX)
                {
                    tiesX++;
                }

                if (tieY)
                {
                    tiesY++;
                }

                if (tieX || tieY)
                {
                    continue;
                }

                if ((x[i] - x[j]) * (y[i] - y[j]) > 0.0)
                {
                    concordant++;
                }
                else
                {
                    discordant++;
                }
            }
        }

        if (pairs == tiesX || pairs == tiesY)
        {
            return MathResult<double>.Fail(MathErrorCode.DivisionByZero);
        }

        double denominator = Math.Sqrt((double)(pairs - tiesX) * (pairs - tiesY));
        return Finite(Math.Clamp((concordant - discordant) / denominator, -1.0, 1.0));
    }

    /// <summary>
    /// Cross-correlation at an integer lag in <c>[-(n - 1), n - 1]</c> with full-series means and the full-series denominator:
    /// <c>lag &gt;= 0</c> pairs <c>x_t</c> with <c>y_(t+lag)</c>, <c>lag &lt; 0</c> pairs <c>x_(t+|lag|)</c> with <c>y_t</c>.
    /// </summary>
    public static MathResult<double> CrossCorrelation(scoped ReadOnlySpan<double> x, scoped ReadOnlySpan<double> y, int lag)
    {
        if (x.Length != y.Length)
        {
            return MathResult<double>.Fail(MathErrorCode.DimensionMismatch);
        }

        int n = x.Length;
        if (n < 2 || Math.Abs((long)lag) >= n)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        double meanX = Mean(x);
        double meanY = Mean(y);
        double sxx = 0.0;
        double syy = 0.0;
        for (int i = 0; i < n; i++)
        {
            sxx += (x[i] - meanX) * (x[i] - meanX);
            syy += (y[i] - meanY) * (y[i] - meanY);
        }

        if (sxx / n <= VarianceFloor || syy / n <= VarianceFloor)
        {
            return MathResult<double>.Fail(MathErrorCode.DivisionByZero);
        }

        int shift = Math.Abs(lag);
        double cross = 0.0;
        for (int t = 0; t < n - shift; t++)
        {
            cross += lag >= 0
                ? (x[t] - meanX) * (y[t + shift] - meanY)
                : (x[t + shift] - meanX) * (y[t] - meanY);
        }

        return Finite(Math.Clamp(cross / Math.Sqrt(sxx * syy), -1.0, 1.0));
    }

    // ---- Student t-tests ----

    /// <summary>One-sample t-test of <c>mean(x) = mu0</c>: writes <c>[t, two-sided p, df]</c> into <paramref name="result"/> (length 3).</summary>
    public static MathErrorCode TTest1Samp(scoped ReadOnlySpan<double> x, double mu0, scoped Span<double> result)
    {
        if (result.Length < TTestResultLength)
        {
            return MathErrorCode.DimensionMismatch;
        }

        int n = x.Length;
        if (n < 2)
        {
            return MathErrorCode.DomainError;
        }

        double mean = Mean(x);
        double sum = 0.0;
        for (int i = 0; i < n; i++)
        {
            sum += (x[i] - mean) * (x[i] - mean);
        }

        double s = Math.Sqrt(sum / (n - 1));
        if (s <= VarianceFloor)
        {
            return MathErrorCode.DivisionByZero;
        }

        double df = n - 1;
        double t = (mean - mu0) / (s / Math.Sqrt(n));
        return StoreTTest(t, df, result);
    }

    /// <summary>Welch's two-sample t-test: writes <c>[t, two-sided p, Welch-Satterthwaite df]</c> into <paramref name="result"/> (length 3).</summary>
    public static MathErrorCode TTest2SampWelch(scoped ReadOnlySpan<double> x, scoped ReadOnlySpan<double> y, scoped Span<double> result)
    {
        if (result.Length < TTestResultLength)
        {
            return MathErrorCode.DimensionMismatch;
        }

        int nx = x.Length;
        int ny = y.Length;
        if (nx < 2 || ny < 2)
        {
            return MathErrorCode.DomainError;
        }

        double meanX = Mean(x);
        double meanY = Mean(y);
        double sx = 0.0;
        double sy = 0.0;
        for (int i = 0; i < nx; i++)
        {
            sx += (x[i] - meanX) * (x[i] - meanX);
        }

        for (int i = 0; i < ny; i++)
        {
            sy += (y[i] - meanY) * (y[i] - meanY);
        }

        double vx = sx / (nx - 1) / nx;
        double vy = sy / (ny - 1) / ny;
        double sum = vx + vy;
        if (sum <= WelchVarianceFloor)
        {
            return MathErrorCode.DivisionByZero;
        }

        double t = (meanX - meanY) / Math.Sqrt(sum);
        double df = sum * sum / ((vx * vx / (nx - 1)) + (vy * vy / (ny - 1)));
        return StoreTTest(t, df, result);
    }

    private static MathErrorCode StoreTTest(double t, double df, Span<double> result)
    {
        double p = DslSpecialFunctionsMath.IncBetaDouble(df / (df + (t * t)), 0.5 * df, 0.5);
        if (double.IsNaN(p))
        {
            return MathErrorCode.ConvergenceFailure;
        }

        if (!double.IsFinite(t) || !double.IsFinite(df))
        {
            return MathErrorCode.NonFiniteResult;
        }

        result[0] = t;
        result[1] = Math.Clamp(p, 0.0, 1.0);
        result[2] = df;
        return MathErrorCode.None;
    }

    // ---- Normality & Serial Dependence (delegating to the existing kernels) ----

    /// <summary>Jarque-Bera test. <c>n &lt; 8</c> is a domain error and a flat series a division by zero; the statistic comes from <see cref="NormalityAndTailMath.TryJarqueBera"/>.</summary>
    public static MathErrorCode JarqueBera(scoped ReadOnlySpan<double> x, out double statistic, out double pValue)
    {
        statistic = double.NaN;
        pValue = double.NaN;
        if (x.Length < NormalityAndTailMath.MinJarqueBeraCount)
        {
            return MathErrorCode.DomainError;
        }

        if (!WindowMoments.TryCompute(x, FlatWindowRule.Series, out WindowMoments moments))
        {
            return MathErrorCode.NonFiniteResult;
        }

        if (moments.IsFlat || moments.PopulationVariance <= VarianceFloor)
        {
            return MathErrorCode.DivisionByZero;
        }

        return NormalityAndTailMath.TryJarqueBera(in moments, out statistic, out pValue)
            ? MathErrorCode.None
            : MathErrorCode.NonFiniteResult;
    }

    /// <summary>
    /// Ljung-Box test. <c>n &lt; 2</c>, <c>lags &lt; 1</c> or <c>lags &gt;= n</c> is a domain error and a population variance not above
    /// <see cref="VarianceFloor"/> a division by zero; the statistic comes from <see cref="SerialDependenceMath.TryLjungBox"/>.
    /// </summary>
    public static MathErrorCode LjungBox(scoped ReadOnlySpan<double> x, int lags, out double statistic, out double pValue)
    {
        statistic = double.NaN;
        pValue = double.NaN;
        int n = x.Length;
        if (n < 2 || lags < 1 || lags >= n)
        {
            return MathErrorCode.DomainError;
        }

        double mean = Mean(x);
        double gamma0 = 0.0;
        for (int i = 0; i < n; i++)
        {
            gamma0 += (x[i] - mean) * (x[i] - mean);
        }

        if (gamma0 / n <= VarianceFloor)
        {
            return MathErrorCode.DivisionByZero;
        }

        return SerialDependenceMath.TryLjungBox(x, lags, out statistic, out pValue)
            ? MathErrorCode.None
            : MathErrorCode.NonFiniteResult;
    }

    // ---- Two-sample Kolmogorov-Smirnov ----

    /// <summary>
    /// Two-sample KS test: exact <c>D = sup |F_x - F_y|</c> by a merge scan over the sorted samples (ties across the samples are consumed together) and the
    /// asymptotic p-value <c>2 sum_{j=1..100} (-1)^(j-1) exp(-2 j^2 lambda^2)</c>, <c>lambda = (sqrt(ne) + 0.12 + 0.11 / sqrt(ne)) D</c>, clamped to <c>[0, 1]</c>.
    /// </summary>
    public static MathErrorCode Ks2Samp(scoped ReadOnlySpan<double> x, scoped ReadOnlySpan<double> y, out double dStatistic, out double pValue)
    {
        dStatistic = double.NaN;
        pValue = double.NaN;
        int nx = x.Length;
        int ny = y.Length;
        if (nx < 1 || ny < 1)
        {
            return MathErrorCode.DomainError;
        }

        Span<double> a = stackalloc double[nx];
        Span<double> b = stackalloc double[ny];
        x.CopyTo(a);
        y.CopyTo(b);
        a.Sort();
        b.Sort();

        int i = 0;
        int j = 0;
        double d = 0.0;
        while (i < nx || j < ny)
        {
            double v = i < nx ? (j < ny ? Math.Min(a[i], b[j]) : a[i]) : b[j];
            while (i < nx && a[i] == v)
            {
                i++;
            }

            while (j < ny && b[j] == v)
            {
                j++;
            }

            d = Math.Max(d, Math.Abs(((double)i / nx) - ((double)j / ny)));
        }

        double ne = (double)nx * ny / (nx + ny);
        double sqrtNe = Math.Sqrt(ne);
        double lambda = (sqrtNe + KsSmallSampleOffset + (KsSmallSampleSlope / sqrtNe)) * d;
        double p = 1.0;
        if (lambda > 0.0)
        {
            double sum = 0.0;
            double sign = 1.0;
            for (int term = 1; term <= KsSeriesTerms; term++)
            {
                sum += sign * Math.Exp(-2.0 * term * term * lambda * lambda);
                sign = -sign;
            }

            p = Math.Clamp(2.0 * sum, 0.0, 1.0);
        }

        if (!double.IsFinite(p))
        {
            return MathErrorCode.NonFiniteResult;
        }

        dStatistic = d;
        pValue = p;
        return MathErrorCode.None;
    }

    // ---- Shared helpers ----

    /// <summary>1-based ascending average ranks (ties share the mean of the ranks they span). <paramref name="indexScratch"/> needs <c>values.Length</c> entries.</summary>
    internal static void ComputeAverageRanks(scoped ReadOnlySpan<double> values, scoped Span<double> ranks, scoped Span<int> indexScratch)
    {
        int n = values.Length;
        for (int i = 0; i < n; i++)
        {
            indexScratch[i] = i;
        }

        for (int i = 1; i < n; i++)
        {
            int key = indexScratch[i];
            double keyValue = values[key];
            int j = i - 1;
            while (j >= 0 && values[indexScratch[j]] > keyValue)
            {
                indexScratch[j + 1] = indexScratch[j];
                j--;
            }

            indexScratch[j + 1] = key;
        }

        int start = 0;
        while (start < n)
        {
            int end = start;
            while (end + 1 < n && values[indexScratch[end + 1]] == values[indexScratch[start]])
            {
                end++;
            }

            double rank = 1.0 + (0.5 * (start + end));
            for (int k = start; k <= end; k++)
            {
                ranks[indexScratch[k]] = rank;
            }

            start = end + 1;
        }
    }

    private static double Mean(scoped ReadOnlySpan<double> values)
    {
        double sum = 0.0;
        for (int i = 0; i < values.Length; i++)
        {
            sum += values[i];
        }

        return sum / values.Length;
    }

    private static MathResult<double> Finite(double value)
        => double.IsFinite(value)
            ? MathResult<double>.Ok(value == 0.0 ? 0.0 : value)
            : MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
}
