using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

/// <summary>
/// Base of the two-sample tests of returns: the RECENT window holds the returns of the bars <c>[i - Period + 1, i]</c> and the BASELINE window the <see cref="BaselinePeriod"/> returns right before
/// it, <c>[i - Period - BaselinePeriod + 1, i - Period]</c> (contiguous, no overlap), so the first usable bar is <c>Period + BaselinePeriod</c> (return 0 does not exist). The returns are simple returns or,
/// with <see cref="UseLogReturn"/>, log returns (<see cref="ReturnSeriesWindow"/>). Null where either window holds an unusable return or the test refuses the windows. Causal: bar t uses bars &lt;= t only.
/// The test itself is the only part a derived indicator supplies (<see cref="Compute"/>); a derived indicator that has a second output (the p-value) stores it in <see cref="StoreSecondary"/>.
/// </summary>
public abstract class CoreTwoSampleReturnTestIndicatorBase : CoreIndicatorBase
{
    public override bool IsOverlay => false;

    public int Period { get; set; } = IndicatorDefaultConstants.TwoSampleTestDefaultPeriod;

    public int BaselinePeriod { get; set; } = IndicatorDefaultConstants.TwoSampleTestDefaultBaselinePeriod;

    public bool UseLogReturn { get; set; } = IndicatorDefaultConstants.TestDefaultUseLogReturn;

    /// <summary>A parameter object with the current settings; its <c>Validate()</c> decides whether the indicator calculates.</summary>
    protected abstract CoreIndicatorParameterBase CreateParameters();

    /// <summary>The statistic of the two windows and, in <paramref name="secondary"/>, the second output (the p-value) where the test has one; null where the test refuses the windows.</summary>
    protected abstract decimal? Compute(ReadOnlySpan<double> recent, ReadOnlySpan<double> baseline, out decimal? secondary);

    /// <summary>Clears the second output series (none by default).</summary>
    protected virtual void ClearSecondary()
    {
    }

    /// <summary>Appends the second output of one bar (nothing by default).</summary>
    protected virtual void StoreSecondary(decimal? secondary)
    {
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        ClearSecondary();

        try
        {
            CreateParameters().Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return IndicatorResult.Failure(ex.Message);
        }

        var returns = ReturnSeriesWindow.FromPrices(PriceDataHelper.ExtractPriceSeries(candles, PriceSource), UseLogReturn);
        for (int i = 0; i < returns.Count; i++)
        {
            decimal? statistic = null;
            decimal? secondary = null;
            int baselineFirst = i - Period - BaselinePeriod + 1;
            if (baselineFirst >= 1 && returns.IsValid(baselineFirst, i))
            {
                statistic = Compute(returns.Returns.AsSpan(i - Period + 1, Period), returns.Returns.AsSpan(baselineFirst, BaselinePeriod), out secondary);
            }

            _values.Add(statistic);
            StoreSecondary(statistic.HasValue ? secondary : null);
        }

        return CreateAutomaticResult();
    }
}

/// <summary>
/// Rolling one-sample Student t-test of the mean return of the last <see cref="Period"/> returns against <see cref="NullMean"/> (<see cref="DslBivariateAndTestMath.TTest1Samp"/>):
/// <c>Main</c> = <c>(mean - NullMean) / (s / sqrt(n))</c> with the sample standard deviation s (divisor n - 1), dimensionless; <c>PValue</c> = the two-sided p-value in [0, 1], chart-suppressed
/// (scale), see <see cref="IndicatorChartSuppression"/>. Null for bars before bar <c>Period</c>, for a window with an unusable price and for a window of equal returns. Causal.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.OneSampleTTest)]
public class CoreOneSampleTTestIndicator : CoreIndicatorBase
{
    public const string PValueSeriesName = nameof(PValue);

    public override bool IsOverlay => false;

    public int Period { get; set; } = IndicatorDefaultConstants.OneSampleTTestDefaultPeriod;

    public bool UseLogReturn { get; set; } = IndicatorDefaultConstants.TestDefaultUseLogReturn;

    public decimal NullMean { get; set; } = IndicatorDefaultConstants.OneSampleTTestDefaultNullMean;

    public List<decimal?> PValue { get; } = new();

    public override string Name => $"t-Test ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreOneSampleTTestParameter p)
        {
            p.Validate();
            Period = p.Period;
            UseLogReturn = p.UseLogReturn;
            NullMean = p.NullMean;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        PValue.Clear();

        var parameters = new CoreOneSampleTTestParameter { Period = Period, UseLogReturn = UseLogReturn, NullMean = NullMean };
        try
        {
            parameters.Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return IndicatorResult.Failure(ex.Message);
        }

        var returns = ReturnSeriesWindow.FromPrices(PriceDataHelper.ExtractPriceSeries(candles, PriceSource), UseLogReturn);
        double nullMean = (double)NullMean;
        Span<double> result = stackalloc double[3];
        for (int i = 0; i < returns.Count; i++)
        {
            decimal? t = null;
            decimal? p = null;
            int first = i - Period + 1;
            if (first >= 1 && returns.IsValid(first, i) && DslBivariateAndTestMath.TTest1Samp(returns.Returns.AsSpan(first, Period), nullMean, result) == MathErrorCode.None)
            {
                t = IndicatorCalculationHelper.ToDecimalOrNull(result[0]);
                p = t.HasValue ? IndicatorCalculationHelper.ToDecimalOrNull(result[1]) : null;
            }

            _values.Add(t);
            PValue.Add(p);
        }

        return CreateAutomaticResult();
    }
}

/// <summary>
/// Rolling Welch two-sample t-test of the recent returns against the baseline returns (<see cref="DslBivariateAndTestMath.TTest2SampWelch"/>): <c>Main</c> = the Welch t of
/// <c>mean(recent) - mean(baseline)</c> (positive when the recent returns are higher), <c>PValue</c> = the two-sided p-value in [0, 1] (chart-suppressed, scale). Windows as in the base class.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.TwoSampleTTest)]
public class CoreTwoSampleTTestIndicator : CoreTwoSampleReturnTestIndicatorBase
{
    public const string PValueSeriesName = nameof(PValue);

    public List<decimal?> PValue { get; } = new();

    public override string Name => $"Welch t-Test ({Period}, {BaselinePeriod})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreTwoSampleTTestParameter p)
        {
            p.Validate();
            Period = p.Period;
            BaselinePeriod = p.BaselinePeriod;
            UseLogReturn = p.UseLogReturn;
        }
    }

    protected override CoreIndicatorParameterBase CreateParameters() => new CoreTwoSampleTTestParameter { Period = Period, BaselinePeriod = BaselinePeriod, UseLogReturn = UseLogReturn };

    protected override void ClearSecondary() => PValue.Clear();

    protected override void StoreSecondary(decimal? secondary) => PValue.Add(secondary);

    protected override decimal? Compute(ReadOnlySpan<double> recent, ReadOnlySpan<double> baseline, out decimal? secondary)
    {
        secondary = null;
        Span<double> result = stackalloc double[3];
        if (DslBivariateAndTestMath.TTest2SampWelch(recent, baseline, result) != MathErrorCode.None)
        {
            return null;
        }

        secondary = IndicatorCalculationHelper.ToDecimalOrNull(result[1]);
        return IndicatorCalculationHelper.ToDecimalOrNull(result[0]);
    }
}

/// <summary>
/// Rolling two-sample Kolmogorov-Smirnov test of the recent returns against the baseline returns (<see cref="DslBivariateAndTestMath.Ks2Samp"/>): <c>Main</c> = the largest distance D between the two
/// empirical distribution functions, in [0, 1]; <c>PValue</c> = the asymptotic p-value in [0, 1] (chart-suppressed). Windows as in the base class.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.KsTwoSampleTest)]
public class CoreKsTwoSampleTestIndicator : CoreTwoSampleReturnTestIndicatorBase
{
    public const string PValueSeriesName = nameof(PValue);

    public List<decimal?> PValue { get; } = new();

    public override string Name => $"KS Test ({Period}, {BaselinePeriod})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreKsTwoSampleTestParameter p)
        {
            p.Validate();
            Period = p.Period;
            BaselinePeriod = p.BaselinePeriod;
            UseLogReturn = p.UseLogReturn;
        }
    }

    protected override CoreIndicatorParameterBase CreateParameters() => new CoreKsTwoSampleTestParameter { Period = Period, BaselinePeriod = BaselinePeriod, UseLogReturn = UseLogReturn };

    protected override void ClearSecondary() => PValue.Clear();

    protected override void StoreSecondary(decimal? secondary) => PValue.Add(secondary);

    protected override decimal? Compute(ReadOnlySpan<double> recent, ReadOnlySpan<double> baseline, out decimal? secondary)
    {
        secondary = null;
        if (DslBivariateAndTestMath.Ks2Samp(recent, baseline, out double d, out double p) != MathErrorCode.None)
        {
            return null;
        }

        secondary = IndicatorCalculationHelper.ToDecimalOrNull(p);
        return IndicatorCalculationHelper.ToDecimalOrNull(d);
    }
}

/// <summary>
/// Rolling Mann-Whitney U test of the recent returns against the baseline returns (<see cref="DslBivariateAndTestMath.MannWhitneyU"/>, tie and continuity corrected): <c>Main</c> = the smaller of the two
/// U statistics (a count of pairs, 0 .. Period x BaselinePeriod / 2), <c>PValue</c> = the two-sided p-value in [0, 1] (chart-suppressed). The kernel takes at most 512 returns in both windows together,
/// which the ranges of <see cref="CoreMannWhitneyUTestParameter"/> (200 + 300) guarantee. Windows as in the base class.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.MannWhitneyUTest)]
public class CoreMannWhitneyUTestIndicator : CoreTwoSampleReturnTestIndicatorBase
{
    public const string PValueSeriesName = nameof(PValue);

    public List<decimal?> PValue { get; } = new();

    public override string Name => $"Mann-Whitney U ({Period}, {BaselinePeriod})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreMannWhitneyUTestParameter p)
        {
            p.Validate();
            Period = p.Period;
            BaselinePeriod = p.BaselinePeriod;
            UseLogReturn = p.UseLogReturn;
        }
    }

    protected override CoreIndicatorParameterBase CreateParameters() => new CoreMannWhitneyUTestParameter { Period = Period, BaselinePeriod = BaselinePeriod, UseLogReturn = UseLogReturn };

    protected override void ClearSecondary() => PValue.Clear();

    protected override void StoreSecondary(decimal? secondary) => PValue.Add(secondary);

    protected override decimal? Compute(ReadOnlySpan<double> recent, ReadOnlySpan<double> baseline, out decimal? secondary)
    {
        secondary = null;
        Span<double> result = stackalloc double[2];
        if (DslBivariateAndTestMath.MannWhitneyU(recent, baseline, result) != MathErrorCode.None)
        {
            return null;
        }

        secondary = IndicatorCalculationHelper.ToDecimalOrNull(result[1]);
        return IndicatorCalculationHelper.ToDecimalOrNull(result[0]);
    }
}

/// <summary>
/// Rolling Kullback-Leibler divergence <c>D(P || Q)</c> of the distribution of the recent returns P from the distribution of the baseline returns Q (<see cref="DslQuantAndInfoMath.KlDivergence"/> on the
/// two Laplace-smoothed histograms of <see cref="SmoothedHistogramMath"/>, <see cref="BinCount"/> equal-width bins over the shared range). <c>Main</c> is in nats and not negative. Null where both windows
/// hold equal returns (an empty range) and where a window holds an unusable return. Windows as in the base class.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.KlDivergence)]
public class CoreKlDivergenceIndicator : CoreTwoSampleReturnTestIndicatorBase
{
    public CoreKlDivergenceIndicator()
    {
        Period = IndicatorDefaultConstants.KlDivergenceDefaultPeriod;
        BaselinePeriod = IndicatorDefaultConstants.KlDivergenceDefaultBaselinePeriod;
        UseLogReturn = IndicatorDefaultConstants.KlDivergenceDefaultUseLogReturn;
    }

    public int BinCount { get; set; } = IndicatorDefaultConstants.KlDivergenceDefaultBinCount;

    public override string Name => $"KL Divergence ({Period}, {BaselinePeriod}, {BinCount})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreKlDivergenceParameter p)
        {
            p.Validate();
            Period = p.Period;
            BaselinePeriod = p.BaselinePeriod;
            UseLogReturn = p.UseLogReturn;
            BinCount = p.BinCount;
        }
    }

    protected override CoreIndicatorParameterBase CreateParameters() => new CoreKlDivergenceParameter { Period = Period, BaselinePeriod = BaselinePeriod, UseLogReturn = UseLogReturn, BinCount = BinCount };

    protected override decimal? Compute(ReadOnlySpan<double> recent, ReadOnlySpan<double> baseline, out decimal? secondary)
    {
        secondary = null;
        Span<double> p = stackalloc double[BinCount];
        Span<double> q = stackalloc double[BinCount];
        if (!SmoothedHistogramMath.TryBuild(recent, baseline, BinCount, p, q))
        {
            return null;
        }

        MathResult<double> divergence = DslQuantAndInfoMath.KlDivergence(p, q);
        return divergence.IsOk ? IndicatorCalculationHelper.ToDecimalOrNull(divergence.Value) : null;
    }
}
