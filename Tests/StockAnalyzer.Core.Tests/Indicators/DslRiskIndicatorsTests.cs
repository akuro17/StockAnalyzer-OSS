using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Screener;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

/// <summary>
/// DSL indicator and drawing tool expansion, run I-1: <see cref="CoreKpssStationarityIndicator"/>, <see cref="CoreRollingSharpeIndicator"/> and <see cref="CoreRollingMaxDrawdownIndicator"/>.
/// Values are pinned to an independent transcription of each formula (not to the kernel call alone), to the kernels on the same window, and to the registration contract.
/// </summary>
public class DslRiskIndicatorsTests
{
    private static List<CoreCandleData> Candles(int count, int seed = 5)
    {
        var rng = new Random(seed);
        var candles = new List<CoreCandleData>(count);
        double close = 100.0;
        for (int i = 0; i < count; i++)
        {
            double nextClose = close * (1.0 + (0.02 * (rng.NextDouble() - 0.5)) + (i % 13 == 0 ? 0.02 : 0.0));
            decimal c = (decimal)Math.Round(nextClose, 4);
            candles.Add(new CoreCandleData(new DateTime(2023, 1, 1).AddDays(i), c, c, c, c, 1000L + i));
            close = nextClose;
        }

        return candles;
    }

    private static List<CoreCandleData> FromCloses(IEnumerable<decimal> closes) =>
        closes.Select((c, i) => new CoreCandleData(new DateTime(2023, 1, 1).AddDays(i), c, c, c, c, 1000L + i)).ToList();

    // ---------------------------------------------------------------- KpssStationarity

    [Fact]
    public void Kpss_OnAShortHandSolvedSeries_GivesTheIndependentlyComputedStatistic()
    {
        // y = 1 3 2 5 4 6 8 7 9 10: mean 5.5, gamma0 = 8.25, lag = floor(4 (10/100)^0.25) = 2, long-run variance 16.91666..., eta = 0.4582758620689655 (independent transcription).
        var result = new CoreKpssStationarityIndicator { Period = 10 }.Calculate(FromCloses(new decimal[] { 1, 3, 2, 5, 4, 6, 8, 7, 9, 10 }));

        Assert.True(result.IsSuccessful);
        Assert.Equal(0.4582758620689655, (double)result.MainValues[9]!.Value, 9);
        Assert.Null(result.MainValues[8]);
    }

    [Fact]
    public void Kpss_EqualsTheKernelOnEveryWindow_ForPricesAndForLogReturns()
    {
        var candles = Candles(150);
        const int period = 40;

        var prices = new CoreKpssStationarityIndicator { Period = period }.Calculate(candles);
        var returns = new CoreKpssStationarityIndicator { Period = period, UseReturns = true }.Calculate(candles);
        double[] closes = candles.Select(c => (double)c.Close).ToArray();
        double[] logReturns = new double[closes.Length];
        for (int i = 1; i < closes.Length; i++)
        {
            logReturns[i] = Math.Log(closes[i] / closes[i - 1]);
        }

        for (int i = 0; i < candles.Count; i++)
        {
            if (i < period - 1)
            {
                Assert.Null(prices.MainValues[i]);
            }
            else
            {
                Assert.Equal(IndicatorCalculationHelper.ToDecimalOrNull(ArimaMath.ComputeKpssStatistic(closes.AsSpan(i - period + 1, period))), prices.MainValues[i]);
            }

            if (i < period)
            {
                Assert.Null(returns.MainValues[i]);
            }
            else
            {
                // The shared log-return converter stores each return as a decimal (about 15 significant digits), so the statistic agrees with a double transcription to 1e-9, not to the last digit.
                Assert.Equal(ArimaMath.ComputeKpssStatistic(logReturns.AsSpan(i - period + 1, period)), (double)returns.MainValues[i]!.Value, 9);
            }
        }

        Assert.NotNull(prices.MainValues[^1]);
        Assert.NotNull(returns.MainValues[^1]);
    }

    [Fact]
    public void Kpss_GivesNoValueForAFlatWindow_AndAZeroKernelAnswerIsNeverShown()
    {
        // The kernel answers 0.0 for a flat window; the indicator guards the window and gives null instead.
        var flat = new CoreKpssStationarityIndicator { Period = 10 }.Calculate(FromCloses(Enumerable.Repeat(50m, 30)));
        Assert.All(flat.MainValues, v => Assert.Null(v));
        Assert.All(flat.GetSeries(CoreKpssStationarityIndicator.CriticalValue5PctSeriesName), v => Assert.Null(v));

        // Constant log returns (a geometric series) are flat in the returns domain.
        var geometric = FromCloses(Enumerable.Range(0, 40).Select(i => 100m * (decimal)Math.Pow(1.01, i)));
        var flatReturns = new CoreKpssStationarityIndicator { Period = 12, UseReturns = true }.Calculate(geometric);
        Assert.All(flatReturns.MainValues, v => Assert.Null(v));
    }

    [Fact]
    public void Kpss_ReferenceLinesAreConstantsPresentExactlyWhereMainHasAValue()
    {
        var result = new CoreKpssStationarityIndicator { Period = 20 }.Calculate(Candles(80));
        var five = result.GetSeries(CoreKpssStationarityIndicator.CriticalValue5PctSeriesName);
        var one = result.GetSeries(CoreKpssStationarityIndicator.CriticalValue1PctSeriesName);

        for (int i = 0; i < result.MainValues.Count; i++)
        {
            Assert.Equal(result.MainValues[i].HasValue ? 0.463m : null, five[i]);
            Assert.Equal(result.MainValues[i].HasValue ? 0.739m : null, one[i]);
        }

        Assert.Contains(result.MainValues, v => v.HasValue);
    }

    [Fact]
    public void Kpss_FivePercentLevelEqualsTheKernelsOwnConstant_AndTheOnePercentLevelIsAbove()
    {
        FieldInfo? field = typeof(ArimaMath).GetField("KpssCriticalValue5Percent", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        Assert.Equal((decimal)(double)field!.GetRawConstantValue()!, IndicatorDefaultConstants.KpssCriticalValue5Percent);
        Assert.True(IndicatorDefaultConstants.KpssCriticalValue1Percent > IndicatorDefaultConstants.KpssCriticalValue5Percent);

        FieldInfo? minimum = typeof(ArimaMath).GetField("KpssMinSampleLength", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.Equal((int)minimum!.GetRawConstantValue()!, IndicatorDefaultConstants.KpssStationarityMinPeriod);
    }

    [Fact]
    public void Kpss_AWindowWithAnUnusablePriceGivesNoValue_AndEveryWindowAfterItRecovers()
    {
        var candles = Candles(60);
        // A null price cannot be built from CoreCandleData, so the gap is a non-positive price in the returns domain (log return invalid) and the series domain keeps it as a number.
        candles[30] = new CoreCandleData(candles[30].Timestamp, 0m, 0m, 0m, 0m, 1000);
        const int period = 10;

        var returns = new CoreKpssStationarityIndicator { Period = period, UseReturns = true }.Calculate(candles);

        // The log returns of bars 30 and 31 are invalid, so every window containing bar 30 or 31 (bars 30 .. 40) has no value; bar 41 is the first clean window again.
        for (int i = 30; i <= 40; i++)
        {
            Assert.Null(returns.MainValues[i]);
        }

        Assert.NotNull(returns.MainValues[41]);
    }

    [Fact]
    public void Kpss_ParameterRangeIsEnforced()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreKpssStationarityParameter { Period = 7 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreKpssStationarityParameter { Period = 501 }.Validate());
        new CoreKpssStationarityParameter { Period = 8 }.Validate();
        new CoreKpssStationarityParameter { Period = 500 }.Validate();
        Assert.False(new CoreKpssStationarityIndicator { Period = 7 }.Calculate(Candles(60)).IsSuccessful);
    }

    // ---------------------------------------------------------------- RollingSharpe

    private static double SharpeOfWindow(IReadOnlyList<CoreCandleData> candles, int last, int period, bool log, double rfPerBar, double ppy)
    {
        var r = new List<double>();
        for (int k = last - period + 1; k <= last; k++)
        {
            double previous = (double)candles[k - 1].Close;
            double current = (double)candles[k].Close;
            r.Add(log ? Math.Log(current / previous) : (current - previous) / previous);
        }

        double mean = r.Average();
        double sd = Math.Sqrt(r.Sum(x => (x - mean) * (x - mean)) / (period - 1));
        return ((mean - rfPerBar) / sd) * Math.Sqrt(ppy);
    }

    [Fact]
    public void Sharpe_PerBar_EqualsTheIndependentFormulaOnEveryWindow_ForSimpleAndLogReturns()
    {
        var candles = Candles(120);
        const int period = 20;

        foreach (bool log in new[] { false, true })
        {
            var result = new CoreRollingSharpeIndicator { Period = period, UseLogReturn = log }.Calculate(candles);

            for (int i = 0; i < candles.Count; i++)
            {
                if (i < period)
                {
                    Assert.Null(result.MainValues[i]);
                    continue;
                }

                Assert.True(result.MainValues[i].HasValue);
                Assert.Equal(SharpeOfWindow(candles, i, period, log, 0.0, 1.0), (double)result.MainValues[i]!.Value, 9);
            }
        }
    }

    [Fact]
    public void Sharpe_Annualised_ScalesBySqrtOfPeriodsPerYear_AndTheRiskFreeRateIsPerBar()
    {
        var candles = Candles(80);
        const int period = 25;
        const int ppy = 52;
        decimal annualRate = 0.052m; // 0.001 per bar at 52 bars per year

        var perBar = new CoreRollingSharpeIndicator { Period = period }.Calculate(candles);
        var annual = new CoreRollingSharpeIndicator { Period = period, Annualize = true, PeriodsPerYear = ppy }.Calculate(candles);
        var annualWithRate = new CoreRollingSharpeIndicator { Period = period, Annualize = true, PeriodsPerYear = ppy, AnnualRiskFreeRate = annualRate }.Calculate(candles);
        var perBarWithRate = new CoreRollingSharpeIndicator { Period = period, PeriodsPerYear = ppy, AnnualRiskFreeRate = annualRate }.Calculate(candles);

        int last = candles.Count - 1;
        Assert.Equal((double)perBar.MainValues[last]!.Value * Math.Sqrt(ppy), (double)annual.MainValues[last]!.Value, 9);
        Assert.Equal(SharpeOfWindow(candles, last, period, false, 0.001, ppy), (double)annualWithRate.MainValues[last]!.Value, 9);
        // Annualize off: the risk-free rate is still converted to a per-bar rate with PeriodsPerYear.
        Assert.Equal(SharpeOfWindow(candles, last, period, false, 0.001, 1.0), (double)perBarWithRate.MainValues[last]!.Value, 9);
    }

    [Fact]
    public void Sharpe_AFlatWindowOrAnUnusablePriceGivesNoValue()
    {
        var flat = new CoreRollingSharpeIndicator { Period = 5 }.Calculate(FromCloses(Enumerable.Repeat(10m, 20)));
        Assert.All(flat.MainValues, v => Assert.Null(v));

        var candles = Candles(40);
        candles[20] = new CoreCandleData(candles[20].Timestamp, 0m, 0m, 0m, 0m, 1000);
        var gap = new CoreRollingSharpeIndicator { Period = 5 }.Calculate(candles);
        // The shared simple-return converter accepts a price of 0 as the current price (a return of -100 %) and refuses it as the previous price: the return at bar 21 is invalid, so
        // the windows of bars 21 .. 25 have no value; the window of bar 20 holds only valid returns.
        Assert.NotNull(gap.MainValues[20]);
        for (int i = 21; i <= 25; i++)
        {
            Assert.Null(gap.MainValues[i]);
        }

        Assert.NotNull(gap.MainValues[26]);
        Assert.NotNull(gap.MainValues[19]);
    }

    [Fact]
    public void Sharpe_ParametersAreValidated_AndDefaultsDoNotAnnualize()
    {
        var parameter = new CoreRollingSharpeParameter();
        Assert.False(parameter.Annualize);
        Assert.Equal(252, parameter.PeriodsPerYear);
        Assert.Equal(0m, parameter.AnnualRiskFreeRate);
        Assert.Equal(20, parameter.Period);

        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreRollingSharpeParameter { Period = 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreRollingSharpeParameter { Period = 501 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreRollingSharpeParameter { PeriodsPerYear = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreRollingSharpeParameter { PeriodsPerYear = 367 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreRollingSharpeParameter { AnnualRiskFreeRate = -0.51m }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreRollingSharpeParameter { AnnualRiskFreeRate = 1.01m }.Validate());
        new CoreRollingSharpeParameter { Period = 2, PeriodsPerYear = 1, AnnualRiskFreeRate = -0.5m }.Validate();
        new CoreRollingSharpeParameter { Period = 500, PeriodsPerYear = 366, AnnualRiskFreeRate = 1.0m }.Validate();
        Assert.False(new CoreRollingSharpeIndicator { Period = 1 }.Calculate(Candles(30)).IsSuccessful);
    }

    // ---------------------------------------------------------------- RollingMaxDrawdown

    [Fact]
    public void MaxDrawdown_OnTheDocumentedExample_IsThePeakToTroughFall()
    {
        // 100 120 90 150 80 160: peaks 100, 120, 120, 150, 150, 160; drawdowns 0, 0, 0.25, 0, 70/150, 0 -> 0.46666... (the reference vector of the DSL max_drawdown).
        var closes = new decimal[] { 100, 120, 90, 150, 80, 160 };
        var ratio = new CoreRollingMaxDrawdownIndicator { Period = 6, AsPercent = false }.Calculate(FromCloses(closes));
        var percent = new CoreRollingMaxDrawdownIndicator { Period = 6 }.Calculate(FromCloses(closes));

        decimal expected = 70m / 150m;
        Assert.Equal(expected, ratio.MainValues[5]);
        Assert.Equal(expected * 100m, percent.MainValues[5]);
        Assert.All(ratio.MainValues.Take(5), v => Assert.Null(v));
    }

    [Fact]
    public void MaxDrawdown_WindowSlides_AndAMonotoneRiseIsZero()
    {
        var closes = new decimal[] { 100, 50, 60, 70, 80, 90 };
        var result = new CoreRollingMaxDrawdownIndicator { Period = 3, AsPercent = false }.Calculate(FromCloses(closes));

        Assert.Equal(0.5m, result.MainValues[2]);   // 100 50 60: the fall from 100 to 50
        Assert.Equal(0m, result.MainValues[3]);     // 50 60 70: a rise
        Assert.Equal(0m, result.MainValues[5]);
        Assert.Null(result.MainValues[1]);
    }

    [Fact]
    public void MaxDrawdown_EqualsTheKernelOnEveryWindow_AndANonPositivePriceGivesNoValue()
    {
        var candles = Candles(100);
        const int period = 30;
        var result = new CoreRollingMaxDrawdownIndicator { Period = period }.Calculate(candles);
        decimal[] closes = candles.Select(c => c.Close).ToArray();
        for (int i = period - 1; i < candles.Count; i++)
        {
            MathResult<decimal> kernel = DslQuantAndInfoMath.MaxDrawdown(closes.AsSpan(i - period + 1, period));
            Assert.True(kernel.IsOk);
            Assert.Equal(kernel.Value * 100m, result.MainValues[i]);
        }

        candles[50] = new CoreCandleData(candles[50].Timestamp, 0m, 0m, 0m, 0m, 1000);
        var withZero = new CoreRollingMaxDrawdownIndicator { Period = period }.Calculate(candles);
        for (int i = 50; i < 50 + period; i++)
        {
            Assert.Null(withZero.MainValues[i]);
        }

        Assert.NotNull(withZero.MainValues[50 + period]);
        Assert.NotNull(withZero.MainValues[49]);
    }

    [Fact]
    public void MaxDrawdown_ParametersAreValidated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreRollingMaxDrawdownParameter { Period = 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreRollingMaxDrawdownParameter { Period = 501 }.Validate());
        new CoreRollingMaxDrawdownParameter { Period = 2 }.Validate();
        new CoreRollingMaxDrawdownParameter { Period = 500 }.Validate();
        Assert.True(new CoreRollingMaxDrawdownParameter().AsPercent);
        Assert.False(new CoreRollingMaxDrawdownIndicator { Period = 1 }.Calculate(Candles(30)).IsSuccessful);
    }

    // ---------------------------------------------------------------- contract shared by the three types

    public static IEnumerable<object[]> TheTypes() => new[]
    {
        new object[] { IndicatorType.KpssStationarity, "Math", ChannelNormalization.WindowZScore, false },
        new object[] { IndicatorType.RollingSharpe, "Math", ChannelNormalization.WindowZScore, true },
        new object[] { IndicatorType.RollingMaxDrawdown, "Volatility", ChannelNormalization.None, true },
    };

    [Theory]
    [MemberData(nameof(TheTypes))]
    public void TheIndicatorIsRegistered_WithItsParameterObject_AndIsDisabledByDefault(IndicatorType type, string group, ChannelNormalization normalization, bool zeroBaseline)
    {
        Assert.True(IndicatorFactory.Default.IsRegistered(type));
        Assert.False(((CoreIndicatorBase)IndicatorFactory.Default.Create(type)!).IsOverlay);

        CoreIndicatorSettings settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == type);
        Assert.False(settings.IsEnabled);
        Assert.False(settings.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Oscillator, settings.Category);
        Assert.Equal(zeroBaseline, settings.IncludeZeroBaseline);
        Assert.NotNull(settings.ParameterObject);
        settings.ParameterObject!.Validate();
        Assert.True(settings.ParameterObject!.GetRequiredWarmupBars() > 0);
        Assert.Null(settings.MinValue);
        Assert.Null(settings.MaxValue);

        var groups = ScreenerIndicatorGroup.GetDefaultGroups()
            .Where(g => g.CategoryType == "Indicator" && g.IndicatorTypes.Contains(type))
            .Select(g => g.Name)
            .ToList();
        Assert.Equal(new[] { group }, groups);
        Assert.Equal(normalization, FeatureChannelNormalizationDefaults.ForIndicator(type));
    }

    [Theory]
    [MemberData(nameof(TheTypes))]
    public void EveryDefaultSeriesIsEmitted_AndMainIsMappedExplicitly(IndicatorType type, string group, ChannelNormalization normalization, bool zeroBaseline)
    {
        _ = (group, normalization, zeroBaseline);
        var factory = IndicatorFactory.Default;
        var result = factory.Create(type)!.Calculate(Candles(200));
        Assert.True(result.IsSuccessful);
        Assert.Contains(result.MainValues, v => v.HasValue);

        foreach (string display in IndicatorOutputSeriesResolver.GetDisplayNames(type, factory))
        {
            string name = IndicatorOutputSeriesResolver.Normalize(type, display, indicatorFactory: factory);
            Assert.True(result.HasSeries(name), $"{type}: '{name}' is not emitted");
        }

        CoreIndicatorSettings settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == type);
        Assert.Contains(settings.SeriesColors, sc => sc.Name == IndicatorResult.MainSeriesName && sc.TargetSeries.Contains(IndicatorResult.MainSeriesName));
        foreach (SeriesColorConfig colour in settings.SeriesColors)
        {
            foreach (string target in colour.TargetSeries)
            {
                Assert.False(IndicatorChartSuppression.IsSuppressed(type, target), $"{type}: the suppressed series '{target}' must not have a colour entry");
            }
        }
    }

    [Theory]
    [MemberData(nameof(TheTypes))]
    public void TheIndicatorIsCausal_AllowedForBacktestAndScreener_AndSupportsAnIndicatorTimeframe(IndicatorType type, string group, ChannelNormalization normalization, bool zeroBaseline)
    {
        _ = (group, normalization, zeroBaseline);
        Assert.DoesNotContain(type, BacktestIndicatorEligibility.NonCausalIndicatorTypes);
        Assert.DoesNotContain(type, BacktestIndicatorEligibility.SynchronousCalculationUnsupportedTypes);
        Assert.False(ScreenerIndicatorEligibility.IsTwoTickerType(type));
        Assert.False(ScreenerIndicatorEligibility.IsUniverseBreadthType(type));
        Assert.True(IndicatorTimeframePolicy.SupportsIndicatorTimeframe(type));
    }

    [Theory]
    [MemberData(nameof(TheTypes))]
    public void FutureBarsDoNotChangeThePast(IndicatorType type, string group, ChannelNormalization normalization, bool zeroBaseline)
    {
        _ = (group, normalization, zeroBaseline);
        var original = Candles(220);
        var poisoned = original.ToList();
        for (int i = 150; i < poisoned.Count; i++)
        {
            decimal c = original[i].Close * 7m + 13m;
            poisoned[i] = new CoreCandleData(original[i].Timestamp, c, c, c, c, 1);
        }

        var factory = IndicatorFactory.Default;
        var before = factory.Create(type)!.Calculate(original).MainValues;
        var after = factory.Create(type)!.Calculate(poisoned).MainValues;
        for (int i = 0; i < 150; i++)
        {
            Assert.Equal(before[i], after[i]);
        }
    }

    [Theory]
    [MemberData(nameof(TheTypes))]
    public void EmptyAndTinyInputsGiveNoValueAndNeverThrow(IndicatorType type, string group, ChannelNormalization normalization, bool zeroBaseline)
    {
        _ = (group, normalization, zeroBaseline);
        var factory = IndicatorFactory.Default;
        Assert.Empty(factory.Create(type)!.Calculate(new List<CoreCandleData>()).MainValues);
        var one = factory.Create(type)!.Calculate(Candles(1));
        Assert.All(one.MainValues, v => Assert.Null(v));
    }
}
