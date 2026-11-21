using System;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Zero-allocation bivariate statistics, correlations and hypothesis tests of the DSL v5 (covariance, Pearson/Spearman/Kendall, cross-correlation,
/// one- and two-sample t-tests, Jarque-Bera, Ljung-Box, two-sample Kolmogorov-Smirnov). Inputs are DSL 1D arrays (<c>&lt;= 512</c> elements) already
/// converted to <see cref="double"/>; working buffers are stack-allocated. Existing kernels (<see cref="NormalityAndTailMath"/>, <see cref="SerialDependenceMath"/>,
/// <see cref="DslSpecialFunctionsMath"/>) are reused unchanged.
/// A "flat" series (one whose deviation is undefined) is decided by one shared rule for every function: <see cref="FlatWindowRule.Series"/> of <see cref="WindowMoments"/>
/// (population deviation below <c>max(1e-10, 1e-12 * |mean|)</c>) -> <see cref="MathErrorCode.DivisionByZero"/>.
/// </summary>
public static class DslBivariateAndTestMath
{
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
    /// Pearson correlation clamped to <c>[-1, 1]</c>. <c>n &lt; 2</c> is a domain error; either series being flat (shared rule, see the class remarks) is a division by zero.
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

        MathErrorCode flat = TryComputeSeriesMoments(x, y, out WindowMoments momentsX, out WindowMoments momentsY);
        if (flat != MathErrorCode.None)
        {
            return MathResult<double>.Fail(flat);
        }

        double sxy = 0.0;
        for (int i = 0; i < n; i++)
        {
            sxy += (x[i] - momentsX.Mean) * (y[i] - momentsY.Mean);
        }

        return Finite(Math.Clamp(sxy / Math.Sqrt(momentsX.M2 * momentsY.M2), -1.0, 1.0));
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
    /// so no tie grouping pass is needed. Consumes <c>max(1, n (n - 1) / DslExecutionBudget.CheapOperationsPerUnit)</c> budget units; <c>n_0 == n_1</c> or <c>n_0 == n_2</c> is a division by zero.
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
        if (!budget.TryConsume((int)Math.Max(1L, (long)n * (n - 1) / DslExecutionBudget.CheapOperationsPerUnit)))
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

        MathErrorCode flat = TryComputeSeriesMoments(x, y, out WindowMoments momentsX, out WindowMoments momentsY);
        if (flat != MathErrorCode.None)
        {
            return MathResult<double>.Fail(flat);
        }

        double meanX = momentsX.Mean;
        double meanY = momentsY.Mean;
        int shift = Math.Abs(lag);
        double cross = 0.0;
        for (int t = 0; t < n - shift; t++)
        {
            cross += lag >= 0
                ? (x[t] - meanX) * (y[t + shift] - meanY)
                : (x[t + shift] - meanX) * (y[t] - meanY);
        }

        return Finite(Math.Clamp(cross / Math.Sqrt(momentsX.M2 * momentsY.M2), -1.0, 1.0));
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

        if (!WindowMoments.TryCompute(x, FlatWindowRule.Series, out WindowMoments moments))
        {
            return MathErrorCode.NonFiniteResult;
        }

        if (moments.IsFlat)
        {
            return MathErrorCode.DivisionByZero;
        }

        double df = n - 1;
        double t = (moments.Mean - mu0) / (moments.SampleStandardDeviation / Math.Sqrt(n));
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

        if (!WindowMoments.TryCompute(x, FlatWindowRule.Series, out WindowMoments momentsX) ||
            !WindowMoments.TryCompute(y, FlatWindowRule.Series, out WindowMoments momentsY))
        {
            return MathErrorCode.NonFiniteResult;
        }

        // Only two flat samples are a division by zero. A flat side contributes exactly 0.0 (never its rounding noise): the standard error is then the
        // other side's, and the Welch-Satterthwaite df collapses to that side's n - 1.
        if (momentsX.IsFlat && momentsY.IsFlat)
        {
            return MathErrorCode.DivisionByZero;
        }

        double vx = momentsX.IsFlat ? 0.0 : momentsX.M2 / (nx - 1) / nx;
        double vy = momentsY.IsFlat ? 0.0 : momentsY.M2 / (ny - 1) / ny;
        double sum = vx + vy;
        double t = (momentsX.Mean - momentsY.Mean) / Math.Sqrt(sum);
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

        if (moments.IsFlat)
        {
            return MathErrorCode.DivisionByZero;
        }

        return NormalityAndTailMath.TryJarqueBera(in moments, out statistic, out pValue)
            ? MathErrorCode.None
            : MathErrorCode.NonFiniteResult;
    }

    /// <summary>
    /// Ljung-Box test. <c>n &lt; 2</c>, <c>lags &lt; 1</c> or <c>lags &gt;= n</c> is a domain error; a flat series (shared rule) is a division by zero. The delegated kernel
    /// <see cref="SerialDependenceMath.TryLjungBox"/> (unchanged) additionally rejects a population variance not above <see cref="ReturnStatistics.VarianceEpsilon"/>; that
    /// effective floor is checked here, by reference to the constant, so it is a division by zero too and not an unexplained non-finite result.
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

        if (!WindowMoments.TryCompute(x, FlatWindowRule.Series, out WindowMoments moments))
        {
            return MathErrorCode.NonFiniteResult;
        }

        if (moments.IsFlat || !(moments.PopulationVariance > ReturnStatistics.VarianceEpsilon))
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

    /// <summary>
    /// The moments of two equally long series under the shared flat rule: <see cref="MathErrorCode.NonFiniteResult"/> when either cannot be computed,
    /// <see cref="MathErrorCode.DivisionByZero"/> when either one is flat (a two-series statistic divides by both deviations), otherwise <see cref="MathErrorCode.None"/>.
    /// </summary>
    private static MathErrorCode TryComputeSeriesMoments(scoped ReadOnlySpan<double> x, scoped ReadOnlySpan<double> y, out WindowMoments momentsX, out WindowMoments momentsY)
    {
        momentsY = default;
        if (!WindowMoments.TryCompute(x, FlatWindowRule.Series, out momentsX) ||
            !WindowMoments.TryCompute(y, FlatWindowRule.Series, out momentsY))
        {
            return MathErrorCode.NonFiniteResult;
        }

        return momentsX.IsFlat || momentsY.IsFlat ? MathErrorCode.DivisionByZero : MathErrorCode.None;
    }

    /// <summary>1-based ascending average ranks (ties share the mean of the ranks they span). <paramref name="indexScratch"/> needs <c>values.Length</c> entries.</summary>
    internal static void ComputeAverageRanks(scoped ReadOnlySpan<double> values, scoped Span<double> ranks, scoped Span<int> indexScratch)
    {
        int n = values.Length;
        for (int i = 0; i < n; i++)
        {
            indexScratch[i] = i;
        }

        // O(n log n), allocation free. The ranks buffer holds the keys while sorting (it is overwritten below); the order of equal values is irrelevant
        // because a group of ties receives one shared (mean) rank.
        values.CopyTo(ranks);
        ranks.Slice(0, n).Sort(indexScratch.Slice(0, n));

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
