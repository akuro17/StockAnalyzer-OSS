using System;
using StockAnalyzer.Core.Models;

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

    // ---- DSL v6 Domain 6b: ANOVA, Chi-Square GOF, Mann-Whitney U, OLS Diagnostics ----

    /// <summary>Maximum number of distinct groups supported by stack-only <see cref="Anova1Way"/>.</summary>
    public const int AnovaMaxGroups = 64;

    /// <summary>Minimum allowed value of an integer group ID in <see cref="Anova1Way"/>.</summary>
    public const int AnovaMinGroupId = -1_000_000;

    /// <summary>Maximum allowed value of an integer group ID in <see cref="Anova1Way"/>.</summary>
    public const int AnovaMaxGroupId = 1_000_000;

    private const double PerfectFitResidualRatio = 1e-24;
    private const double DegenerateVarianceEpsilon = 1e-12;

    /// <summary>
    /// One-way ANOVA writing <c>[F, pValue, dfBetween, dfWithin]</c> into <paramref name="result"/> (length 4)
    /// using stack-only group tables (<see cref="AnovaMaxGroups"/> max groups, zero heap allocation).
    /// Enforces both overall <see cref="FlatWindowRule.Series"/> flatness and within-group residual flatness.
    /// </summary>
    public static MathErrorCode Anova1Way(
        scoped ReadOnlySpan<double> values,
        scoped ReadOnlySpan<int> groupIds,
        scoped Span<double> result)
    {
        if (result.Length < 4 || values.Length != groupIds.Length)
        {
            return MathErrorCode.DimensionMismatch;
        }

        int n = values.Length;
        if (n < 3)
        {
            return MathErrorCode.DomainError;
        }

        if (!WindowMoments.TryCompute(values, FlatWindowRule.Series, out WindowMoments allMoments))
        {
            return MathErrorCode.NonFiniteResult;
        }

        if (allMoments.IsFlat)
        {
            return MathErrorCode.DivisionByZero;
        }

        Span<int> uniqueIds = stackalloc int[AnovaMaxGroups];
        Span<int> groupCounts = stackalloc int[AnovaMaxGroups];
        Span<double> groupSums = stackalloc double[AnovaMaxGroups];
        Span<int> groupOf = stackalloc int[n];
        int k = 0;

        for (int i = 0; i < n; i++)
        {
            int id = groupIds[i];
            int idx = -1;
            for (int g = 0; g < k; g++)
            {
                if (uniqueIds[g] == id)
                {
                    idx = g;
                    break;
                }
            }

            if (idx < 0)
            {
                if (k >= AnovaMaxGroups)
                {
                    return MathErrorCode.DomainError;
                }

                idx = k++;
                uniqueIds[idx] = id;
                groupCounts[idx] = 0;
                groupSums[idx] = 0.0;
            }

            groupOf[i] = idx;
            groupCounts[idx]++;
            groupSums[idx] += values[i];
        }

        if (k < 2 || n <= k)
        {
            return MathErrorCode.DomainError;
        }

        Span<double> groupMeans = stackalloc double[k];
        double ssBetween = 0.0;
        for (int g = 0; g < k; g++)
        {
            double mean = groupSums[g] / groupCounts[g];
            groupMeans[g] = mean;
            double diff = mean - allMoments.Mean;
            ssBetween += groupCounts[g] * diff * diff;
        }

        double ssWithin = 0.0;
        for (int i = 0; i < n; i++)
        {
            double resid = values[i] - groupMeans[groupOf[i]];
            ssWithin += resid * resid;
        }

        double residualRms = Math.Sqrt(ssWithin / n);
        double residualFloor = Math.Max(
            ComparisonSeriesDefaults.FlatWindowDeviationEpsilon,
            WindowMoments.SeriesRelativeFlatEpsilon * Math.Abs(allMoments.Mean));
        if (ssWithin <= 0.0 || !(residualRms >= residualFloor))
        {
            return MathErrorCode.DivisionByZero;
        }

        double df1 = k - 1;
        double df2 = n - k;
        double fStat = (ssBetween / df1) / (ssWithin / df2);
        double betaArg = df2 / (df2 + (df1 * fStat));
        double pVal = DslSpecialFunctionsMath.IncBetaDouble(betaArg, 0.5 * df2, 0.5 * df1);
        if (!double.IsFinite(fStat) || !double.IsFinite(pVal))
        {
            return MathErrorCode.NonFiniteResult;
        }

        result[0] = fStat == 0.0 ? 0.0 : fStat;
        result[1] = Math.Clamp(pVal, 0.0, 1.0);
        result[2] = df1;
        result[3] = df2;
        return MathErrorCode.None;
    }

    /// <summary>
    /// Pearson chi-square goodness-of-fit test writing <c>[chi2, pValue, df]</c> into <paramref name="result"/> (length 3).
    /// </summary>
    public static MathErrorCode Chi2Gof(
        scoped ReadOnlySpan<double> observed,
        scoped ReadOnlySpan<double> expected,
        scoped Span<double> result)
    {
        if (result.Length < 3 || observed.Length != expected.Length)
        {
            return MathErrorCode.DimensionMismatch;
        }

        int k = observed.Length;
        if (k < 2)
        {
            return MathErrorCode.DomainError;
        }

        double chi2 = 0.0;
        for (int i = 0; i < k; i++)
        {
            double o = observed[i];
            double e = expected[i];
            if (!double.IsFinite(o) || !double.IsFinite(e) || o < 0.0 || e <= 0.0)
            {
                return MathErrorCode.DomainError;
            }

            double diff = o - e;
            chi2 += (diff * diff) / e;
        }

        double df = k - 1;
        double pVal = DslSpecialFunctionsMath.RegularizedGammaQDouble(0.5 * df, 0.5 * chi2);
        if (!double.IsFinite(chi2) || !double.IsFinite(pVal))
        {
            return MathErrorCode.NonFiniteResult;
        }

        result[0] = chi2 == 0.0 ? 0.0 : chi2;
        result[1] = Math.Clamp(pVal, 0.0, 1.0);
        result[2] = df;
        return MathErrorCode.None;
    }

    /// <summary>
    /// Two-sided Mann-Whitney U test with tie correction and continuity correction, writing <c>[U_min, pValue]</c>
    /// into <paramref name="result"/> (length 2).
    /// </summary>
    public static MathErrorCode MannWhitneyU(
        scoped ReadOnlySpan<double> x,
        scoped ReadOnlySpan<double> y,
        scoped Span<double> result)
    {
        if (result.Length < 2)
        {
            return MathErrorCode.DimensionMismatch;
        }

        int n1 = x.Length;
        int n2 = y.Length;
        int n = n1 + n2;
        if (n1 < 2 || n2 < 2 || n > 512)
        {
            return MathErrorCode.DomainError;
        }

        Span<double> combined = stackalloc double[n];
        for (int i = 0; i < n1; i++)
        {
            if (!double.IsFinite(x[i]))
            {
                return MathErrorCode.NonFiniteResult;
            }

            combined[i] = x[i];
        }

        for (int j = 0; j < n2; j++)
        {
            if (!double.IsFinite(y[j]))
            {
                return MathErrorCode.NonFiniteResult;
            }

            combined[n1 + j] = y[j];
        }

        Span<double> ranks = stackalloc double[n];
        Span<int> indexScratch = stackalloc int[n];
        ComputeAverageRanks(combined, ranks, indexScratch);

        double rankSum1 = 0.0;
        for (int i = 0; i < n1; i++)
        {
            rankSum1 += ranks[i];
        }

        double u1 = rankSum1 - (0.5 * n1 * (n1 + 1));
        double u2 = ((double)n1 * n2) - u1;
        double uMin = Math.Min(u1, u2);
        if (uMin < 0.0 && uMin > -1e-12)
        {
            uMin = 0.0;
        }

        double tieSum = 0.0;
        bool hasTie = false;
        int idx = 0;
        while (idx < n)
        {
            int end = idx;
            double v = combined[indexScratch[idx]];
            while (end + 1 < n && combined[indexScratch[end + 1]] == v)
            {
                end++;
            }

            int run = end - idx + 1;
            if (run == n)
            {
                return MathErrorCode.DivisionByZero;
            }

            if (run > 1)
            {
                hasTie = true;
                tieSum += ((double)run * run * run) - run;
            }

            idx = end + 1;
        }

        double pVal;
        if (!hasTie && n <= 20)
        {
            int uMinInt = (int)Math.Round(uMin);
            pVal = ComputeMannWhitneyExactPValue(n1, n2, uMinInt);
        }
        else
        {
            double sigmaU2 = ((double)n1 * n2 / 12.0) * ((n + 1.0) - (tieSum / ((double)n * (n - 1))));
            if (!double.IsFinite(sigmaU2) || sigmaU2 <= DegenerateVarianceEpsilon)
            {
                return MathErrorCode.DivisionByZero;
            }

            double muU = 0.5 * n1 * n2;
            double z = Math.Max(0.0, Math.Abs(uMin - muU) - 0.5) / Math.Sqrt(sigmaU2);
            pVal = Math.Clamp(SpecialFunctionsMath.Erfc(z / Math.Sqrt(2.0)), 0.0, 1.0);
        }

        if (!double.IsFinite(uMin) || !double.IsFinite(pVal))
        {
            return MathErrorCode.NonFiniteResult;
        }

        result[0] = uMin == 0.0 ? 0.0 : uMin;
        result[1] = pVal;
        return MathErrorCode.None;
    }

    private static double ComputeMannWhitneyExactPValue(int n1, int n2, int uMin)
    {
        int totalPairs = n1 * n2;
        int uTarget = Math.Min(Math.Max(0, uMin), totalPairs - Math.Max(0, uMin));
        int width = uTarget + 1;
        int n = n1 + n2;

        // Exact integer DP over combinations of size c <= n1 chosen from {0..n-1} slots with inversion sum u <= uTarget.
        Span<long> dp = stackalloc long[(n1 + 1) * width];
        dp.Clear();
        dp[0] = 1L;

        for (int pos = 0; pos < n; pos++)
        {
            int maxC = Math.Min(pos, n1 - 1);
            for (int c = maxC; c >= 0; c--)
            {
                int addedU = pos - c;
                if (addedU > uTarget)
                {
                    continue;
                }

                int srcRow = c * width;
                int dstRow = (c + 1) * width;
                for (int u = 0; u <= uTarget - addedU; u++)
                {
                    dp[dstRow + u + addedU] += dp[srcRow + u];
                }
            }
        }

        long tailCount = 0L;
        int finalRow = n1 * width;
        for (int u = 0; u <= uTarget; u++)
        {
            tailCount += dp[finalRow + u];
        }

        long totalCombinations = BinomialInt64(n, n1);
        return Math.Min(1.0, (2.0 * tailCount) / totalCombinations);
    }

    private static long BinomialInt64(int n, int k)
    {
        int r = Math.Min(k, n - k);
        long result = 1L;
        for (int i = 1; i <= r; i++)
        {
            result = (result * (n - r + i)) / i;
        }

        return result;
    }

    /// <summary>
    /// Computes OLS regression diagnostics <c>[r2, adjR2, fStat, fPval, rmse]</c> wrapping <see cref="DslRegressionMath.OlsFit"/>
    /// (intercept + <paramref name="p"/> features). Requires <paramref name="n"/> &gt;= <paramref name="p"/> + 2,
    /// non-flat target <paramref name="y"/>, non-collinear design matrix, and non-zero residual sum of squares.
    /// </summary>
    public static MathErrorCode OlsStats(
        scoped ReadOnlySpan<double> xMatrix,
        int n,
        int p,
        scoped ReadOnlySpan<double> y,
        scoped Span<double> result,
        ref DslExecutionBudget budget)
    {
        if (result.Length < 5 || n < 1 || p < 1 || p > DslRegressionMath.MaxFeatures || y.Length != n || xMatrix.Length != n * p)
        {
            return MathErrorCode.DimensionMismatch;
        }

        if (n < p + 2)
        {
            return MathErrorCode.DomainError;
        }

        if (!WindowMoments.TryCompute(y, FlatWindowRule.Series, out WindowMoments yMoments))
        {
            return MathErrorCode.NonFiniteResult;
        }

        if (yMoments.IsFlat)
        {
            return MathErrorCode.DivisionByZero;
        }

        Span<double> beta = stackalloc double[p + 1];
        MathErrorCode fitErr = DslRegressionMath.OlsFit(xMatrix, n, p, y, beta, ref budget);
        if (fitErr != MathErrorCode.None)
        {
            return fitErr;
        }

        double ssRes = 0.0;
        for (int i = 0; i < n; i++)
        {
            double yHat = beta[0];
            int rowBase = i * p;
            for (int j = 0; j < p; j++)
            {
                yHat += beta[j + 1] * xMatrix[rowBase + j];
            }

            double resid = y[i] - yHat;
            ssRes += resid * resid;
        }

        double ssTot = yMoments.M2;
        if (!double.IsFinite(ssRes) || !double.IsFinite(ssTot))
        {
            return MathErrorCode.NonFiniteResult;
        }

        if (ssRes <= PerfectFitResidualRatio * ssTot)
        {
            return MathErrorCode.DivisionByZero;
        }

        double r2 = 1.0 - (ssRes / ssTot);
        double ssReg = Math.Max(0.0, ssTot - ssRes);
        double df1 = p;
        double df2 = n - p - 1;
        double adjR2 = 1.0 - ((ssRes / df2) / (ssTot / (n - 1)));
        double fStat = (ssReg / df1) / (ssRes / df2);
        double fPval = DslSpecialFunctionsMath.IncBetaDouble(df2 / (df2 + (df1 * fStat)), 0.5 * df2, 0.5 * df1);
        double rmse = Math.Sqrt(ssRes / n);
        if (!double.IsFinite(r2) || !double.IsFinite(adjR2) || !double.IsFinite(fStat) || !double.IsFinite(fPval) || !double.IsFinite(rmse))
        {
            return MathErrorCode.NonFiniteResult;
        }

        result[0] = r2 == 0.0 ? 0.0 : r2;
        result[1] = adjR2 == 0.0 ? 0.0 : adjR2;
        result[2] = fStat == 0.0 ? 0.0 : fStat;
        result[3] = Math.Clamp(fPval, 0.0, 1.0);
        result[4] = rmse == 0.0 ? 0.0 : rmse;
        return MathErrorCode.None;
    }
}
