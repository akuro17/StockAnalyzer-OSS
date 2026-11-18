using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Screener;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

/// <summary>
/// Statistical functions plan, Run R6: <see cref="CoreVarianceRatioIndicator"/>. Its Main series is pinned bit for bit to the <c>VarianceRatio</c> series of the Hurst exponent
/// (one formula, <see cref="HurstMath.VarianceRatio"/>), the p-value to the two-sided normal tail of Z*, together with the registration contract of plan section 4.1.
/// </summary>
public class StatisticalVarianceRatioIndicatorTests
{
    private static List<CoreCandleData> Candles(int count, int seed = 11)
    {
        var rng = new Random(seed);
        var candles = new List<CoreCandleData>(count);
        double close = 100.0;
        for (int i = 0; i < count; i++)
        {
            double nextClose = close * (1.0 + (0.02 * (rng.NextDouble() - 0.5)) + (i % 17 == 0 ? 0.03 : 0.0));
            decimal c = (decimal)Math.Round(nextClose, 4);
            candles.Add(new CoreCandleData(new DateTime(2023, 1, 1).AddDays(i), c, c, c, c, 1000L + i));
            close = nextClose;
        }

        return candles;
    }

    private static List<CoreCandleData> FromCloses(IEnumerable<decimal> closes) =>
        closes.Select((c, i) => new CoreCandleData(new DateTime(2023, 1, 1).AddDays(i), c, c, c, c, 1000L + i)).ToList();

    [Theory]
    [InlineData(60, 5)]
    [InlineData(40, 2)]
    [InlineData(100, 12)]
    public void Main_IsBitIdenticalToTheHurstVarianceRatioSeries_AndWarmsUpAtBarPeriod(int period, int horizon)
    {
        var candles = Candles(220);
        var result = new CoreVarianceRatioIndicator { Period = period, AggregationHorizon = horizon }.Calculate(candles);
        var hurst = new CoreHurstExponentIndicator { Period = period, VarianceRatioLag = horizon }.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.True(hurst.IsSuccessful);
        IReadOnlyList<decimal?> hurstRatio = hurst.GetSeries(CoreHurstExponentIndicator.VarianceRatioSeriesName);
        IReadOnlyList<decimal?> hurstZ = hurst.GetSeries(CoreHurstExponentIndicator.VarianceRatioZScoreSeriesName);
        IReadOnlyList<decimal?> zStar = result.GetSeries(CoreVarianceRatioIndicator.ZStarStatisticSeriesName);
        IReadOnlyList<decimal?> pValue = result.GetSeries(CoreVarianceRatioIndicator.PValueSeriesName);
        IReadOnlyList<decimal?> line = result.GetSeries(CoreVarianceRatioIndicator.RandomWalkLineSeriesName);
        double[] returns = ReturnSeriesWindow.FromPrices(candles.Select(c => (decimal?)c.Close).ToList(), useLogReturn: true).Returns;

        Assert.Equal(hurstRatio, result.MainValues);
        Assert.Equal(hurstZ, zStar);
        for (int i = 0; i < candles.Count; i++)
        {
            if (i < period)
            {
                Assert.Null(result.MainValues[i]);
                Assert.Null(line[i]);
                Assert.Null(zStar[i]);
                Assert.Null(pValue[i]);
                continue;
            }

            Assert.NotNull(result.MainValues[i]);
            Assert.Equal(1m, line[i]);
            VarianceRatioTestResult expected = SerialDependenceMath.VarianceRatio(returns.AsSpan(i - period + 1, period), horizon);
            Assert.Equal(IndicatorCalculationHelper.ToDecimalOrNull(expected.PValue), pValue[i]);
            Assert.Equal(SpecialFunctionsMath.NormalTwoSidedPValue(expected.ZStar), expected.PValue);
            Assert.InRange(pValue[i]!.Value, 0m, 1m);
        }
    }

    [Fact]
    public void Defaults_AreTheDocumentedOnes()
    {
        var indicator = new CoreVarianceRatioIndicator();
        Assert.Equal(60, indicator.Period);
        Assert.Equal(5, indicator.AggregationHorizon);
        Assert.Equal("Variance Ratio (60, 5)", indicator.Name);
    }

    [Fact]
    public void AWindowShorterThanTwoHorizonsPlusTwo_GivesNullEverywhere_WithoutAValidationError()
    {
        // Period 20 with the largest horizon 30: 20 < 2 * 30 + 2. No cross-field validation: the bars are null.
        var result = new CoreVarianceRatioIndicator { Period = 20, AggregationHorizon = 30 }.Calculate(Candles(120));

        Assert.True(result.IsSuccessful);
        foreach (string name in new[] { CoreVarianceRatioIndicator.RandomWalkLineSeriesName, CoreVarianceRatioIndicator.ZStarStatisticSeriesName, CoreVarianceRatioIndicator.PValueSeriesName })
        {
            Assert.All(result.GetSeries(name), v => Assert.Null(v));
        }

        Assert.All(result.MainValues, v => Assert.Null(v));

        // W = 2 q + 2 is the first valid window.
        var edge = new CoreVarianceRatioIndicator { Period = 22, AggregationHorizon = 10 }.Calculate(Candles(120));
        Assert.Contains(edge.MainValues, v => v.HasValue);
    }

    [Fact]
    public void AConstantWindowGivesNullForEverySeries()
    {
        var result = new CoreVarianceRatioIndicator { Period = 30 }.Calculate(FromCloses(Enumerable.Repeat(50m, 80)));

        Assert.True(result.IsSuccessful);
        foreach (string name in new[] { CoreVarianceRatioIndicator.RandomWalkLineSeriesName, CoreVarianceRatioIndicator.ZStarStatisticSeriesName, CoreVarianceRatioIndicator.PValueSeriesName })
        {
            Assert.All(result.GetSeries(name), v => Assert.Null(v));
        }

        Assert.All(result.MainValues, v => Assert.Null(v));
    }

    [Fact]
    public void ANonPositivePrice_BlocksTheWindowsThatContainItsReturns()
    {
        var closes = Candles(120).Select(c => c.Close).ToList();
        closes[60] = 0m;
        var result = new CoreVarianceRatioIndicator { Period = 30 }.Calculate(FromCloses(closes));

        // The returns of bars 60 and 61 are invalid: windows ending at bars 60..90 contain one of them.
        for (int i = 60; i <= 90; i++)
        {
            Assert.Null(result.MainValues[i]);
            Assert.Null(result.GetSeries(CoreVarianceRatioIndicator.RandomWalkLineSeriesName)[i]);
        }

        Assert.NotNull(result.MainValues[59]);
        Assert.NotNull(result.MainValues[91]);
    }

    // ---------------------------------------------------------------- registration contract

    [Fact]
    public void TheIndicatorIsRegistered_WithItsParameterObject_AndIsDisabledInASubPanelByDefault()
    {
        const IndicatorType type = IndicatorType.VarianceRatio;
        Assert.True(IndicatorFactory.Default.IsRegistered(type));
        Assert.NotNull(IndicatorFactory.Default.Create(type));

        CoreIndicatorSettings settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == type);
        Assert.False(settings.IsEnabled);
        Assert.False(settings.IsOverlay);
        Assert.NotNull(settings.ParameterObject);
        settings.ParameterObject!.Validate();
        Assert.Equal(IndicatorDefaultConstants.VarianceRatioDefaultPeriod, settings.ParameterObject!.GetRequiredWarmupBars());
        Assert.Equal(CoreIndicatorCategory.Oscillator, settings.Category);
        Assert.Null(settings.MinValue);
        Assert.Null(settings.MaxValue);
    }

    [Fact]
    public void EveryDefaultSeriesIsEmitted_AndMainIsMappedExplicitly_AndSuppressedSeriesHaveNoColourEntry()
    {
        const IndicatorType type = IndicatorType.VarianceRatio;
        var factory = IndicatorFactory.Default;
        var result = factory.Create(type)!.Calculate(Candles(200));
        Assert.True(result.IsSuccessful);

        foreach (string display in IndicatorOutputSeriesResolver.GetDisplayNames(type, factory))
        {
            string name = IndicatorOutputSeriesResolver.Normalize(type, display, indicatorFactory: factory);
            Assert.True(result.HasSeries(name), $"{type}: '{name}' is not emitted");
        }

        CoreIndicatorSettings settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == type);
        Assert.Contains(settings.SeriesColors, sc => sc.Name == IndicatorResult.MainSeriesName && sc.TargetSeries.Contains(IndicatorResult.MainSeriesName));
        Assert.Contains(settings.SeriesColors, sc => sc.TargetSeries.Contains(CoreVarianceRatioIndicator.RandomWalkLineSeriesName));
        foreach (SeriesColorConfig colour in settings.SeriesColors)
        {
            foreach (string target in colour.TargetSeries)
            {
                Assert.False(IndicatorChartSuppression.IsSuppressed(type, target), $"{type}: the suppressed series '{target}' must not have a colour entry");
            }
        }

        Assert.True(IndicatorChartSuppression.IsSuppressed(type, CoreVarianceRatioIndicator.ZStarStatisticSeriesName));
        Assert.True(IndicatorChartSuppression.IsSuppressed(type, CoreVarianceRatioIndicator.PValueSeriesName));
        Assert.False(IndicatorChartSuppression.IsSuppressed(type, CoreVarianceRatioIndicator.RandomWalkLineSeriesName));
    }

    [Fact]
    public void TheClassification_IsMathGroup_WindowZScore_AndAutomaticAxis()
    {
        const IndicatorType type = IndicatorType.VarianceRatio;
        var groups = ScreenerIndicatorGroup.GetDefaultGroups()
            .Where(g => g.CategoryType == "Indicator" && g.IndicatorTypes.Contains(type))
            .Select(g => g.Name)
            .ToList();
        Assert.Equal(new[] { "Math" }, groups);
        Assert.Equal(ChannelNormalization.WindowZScore, FeatureChannelNormalizationDefaults.ForIndicator(type));
    }

    [Fact]
    public void TheIndicatorIsCausal_AllowedForBacktestAndScreener_AndSupportsAnIndicatorTimeframe()
    {
        const IndicatorType type = IndicatorType.VarianceRatio;
        Assert.DoesNotContain(type, BacktestIndicatorEligibility.NonCausalIndicatorTypes);
        Assert.False(ScreenerIndicatorEligibility.IsTwoTickerType(type));
        Assert.False(ScreenerIndicatorEligibility.IsUniverseBreadthType(type));
        Assert.True(IndicatorTimeframePolicy.SupportsIndicatorTimeframe(type));
    }

    [Fact]
    public void FutureBarsDoNotChangeThePast()
    {
        const IndicatorType type = IndicatorType.VarianceRatio;
        var original = Candles(220);
        var poisoned = original.ToList();
        for (int i = 150; i < poisoned.Count; i++)
        {
            decimal absurd = 1m + (i % 7) * 400m;
            poisoned[i] = new CoreCandleData(poisoned[i].Timestamp, absurd, absurd, absurd, absurd, 1L);
        }

        var a = IndicatorFactory.Default.Create(type)!.Calculate(original);
        var b = IndicatorFactory.Default.Create(type)!.Calculate(poisoned);
        foreach (string display in IndicatorOutputSeriesResolver.GetDisplayNames(type, IndicatorFactory.Default))
        {
            string name = IndicatorOutputSeriesResolver.Normalize(type, display, indicatorFactory: IndicatorFactory.Default);
            Assert.Equal(a.GetSeries(name).Take(150), b.GetSeries(name).Take(150));
            Assert.Contains(a.GetSeries(name).Take(150), v => v.HasValue);
        }
    }

    [Fact]
    public void ParameterRanges_AreChecked_AndBadValuesFailTheCalculation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreVarianceRatioParameter { Period = IndicatorDefaultConstants.VarianceRatioMinPeriod - 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreVarianceRatioParameter { Period = IndicatorDefaultConstants.VarianceRatioMaxPeriod + 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreVarianceRatioParameter { AggregationHorizon = 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreVarianceRatioParameter { AggregationHorizon = 31 }.Validate());
        new CoreVarianceRatioParameter { Period = IndicatorDefaultConstants.VarianceRatioMaxPeriod, AggregationHorizon = 30 }.Validate();

        Assert.False(new CoreVarianceRatioIndicator { AggregationHorizon = 1 }.Calculate(Candles(100)).IsSuccessful);
        Assert.False(new CoreVarianceRatioIndicator { Period = 5 }.Calculate(Candles(100)).IsSuccessful);
    }
}
