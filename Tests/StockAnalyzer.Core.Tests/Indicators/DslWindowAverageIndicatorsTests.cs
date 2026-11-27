using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Calculator;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Screener;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

/// <summary>
/// DSL indicator and drawing tool expansion, run I-3: the trimmed, winsorized, Huber, biweight, geometric and harmonic moving averages and the decimal robust-location kernel behind the first two.
/// </summary>
public class DslWindowAverageIndicatorsTests
{
    private static List<CoreCandleData> Candles(int count, int seed = 5)
    {
        var rng = new Random(seed);
        var candles = new List<CoreCandleData>(count);
        double close = 100.0;
        for (int i = 0; i < count; i++)
        {
            double nextClose = close * (1.0 + (0.02 * (rng.NextDouble() - 0.5)) + (i % 11 == 0 ? 0.03 : 0.0));
            decimal c = (decimal)Math.Round(nextClose, 4);
            candles.Add(new CoreCandleData(new DateTime(2023, 1, 1).AddDays(i), c, c, c, c, 1000L + i));
            close = nextClose;
        }

        return candles;
    }

    private static List<CoreCandleData> FromCloses(IEnumerable<decimal> closes) =>
        closes.Select((c, i) => new CoreCandleData(new DateTime(2023, 1, 1).AddDays(i), c, c, c, c, 1000L + i)).ToList();

    // ---------------------------------------------------------------- the decimal kernel

    [Fact]
    public void DecimalKernel_OnAHandSolvedWindow_CutsAndClampsTheExtremes()
    {
        // sorted 1 2 3 10 11 12 40 50, alpha 0.25 -> trim 2: trimmed = (3 + 10 + 11 + 12) / 4 = 9, winsorized = (2 x (3 + 12) + 36) / 8 = 8.25.
        decimal[] window = { 40m, 1m, 12m, 3m, 50m, 10m, 2m, 11m };
        var scratch = new decimal[window.Length];

        Assert.Equal(9m, DecimalRobustLocationMath.TrimmedMean(window, 0.25m, scratch).Value);
        Assert.Equal(8.25m, DecimalRobustLocationMath.WinsorMean(window, 0.25m, scratch).Value);
        Assert.Equal(window.Average(), DecimalRobustLocationMath.TrimmedMean(window, 0m, scratch).Value);
        Assert.Equal(window.Average(), DecimalRobustLocationMath.WinsorMean(window, 0m, scratch).Value);
    }

    [Fact]
    public void DecimalKernel_RefusesWhatTheDslKernelsRefuse()
    {
        decimal[] window = { 1m, 2m, 3m };
        var scratch = new decimal[3];

        Assert.False(DecimalRobustLocationMath.TrimmedMean(window, 0.5m, scratch).IsOk);       // above 0.49
        Assert.False(DecimalRobustLocationMath.TrimmedMean(window, -0.01m, scratch).IsOk);
        Assert.False(DecimalRobustLocationMath.TrimmedMean(ReadOnlySpan<decimal>.Empty, 0.1m, scratch).IsOk);
        Assert.False(DecimalRobustLocationMath.TrimmedMean(window, 0.1m, new decimal[2]).IsOk);  // scratch too short
        Assert.True(DecimalRobustLocationMath.TrimmedMean(window, 0.49m, scratch).IsOk);          // floor(1.47) = 1 value per end, one remains
    }

    [Fact]
    public void DecimalKernel_EqualsTheDslKernels_WhereAlphaTimesNIsNotAtADoubleBoundary()
    {
        var rng = new Random(21);
        foreach (int n in new[] { 8, 20, 33, 50 })
        {
            foreach (decimal alpha in new[] { 0m, 0.1m, 0.25m, 0.4m })
            {
                decimal[] window = Enumerable.Range(0, n).Select(_ => Math.Round(100m + (decimal)(rng.NextDouble() * 20 - 10), 4)).ToArray();
                double[] asDouble = window.Select(v => (double)v).ToArray();
                var scratch = new decimal[n];

                MathResult<double> dslTrimmed = DslRobustStatisticsMath.TrimmedMean(asDouble, (double)alpha);
                MathResult<double> dslWinsor = DslRobustStatisticsMath.WinsorMean(asDouble, (double)alpha);

                Assert.Equal(dslTrimmed.Value, (double)DecimalRobustLocationMath.TrimmedMean(window, alpha, scratch).Value, 9);
                Assert.Equal(dslWinsor.Value, (double)DecimalRobustLocationMath.WinsorMean(window, alpha, scratch).Value, 9);
            }
        }
    }

    [Fact]
    public void DecimalKernel_TheKnownDifferenceToTheDslKernelIsPinned_TheDecimalCutIsTheExactOne()
    {
        // 0.29 x 100 is 28.999999999999996 in double: the DSL kernel cuts 28 values per end, the exact value is 29.
        Assert.Equal(28, (int)Math.Floor(0.29 * 100));
        Assert.Equal(29, DecimalRobustLocationMath.TrimCount(0.29m, 100));

        decimal[] window = Enumerable.Range(1, 100).Select(i => (decimal)i).ToArray();
        double dsl = DslRobustStatisticsMath.TrimmedMean(window.Select(v => (double)v).ToArray(), 0.29).Value;
        decimal exact = DecimalRobustLocationMath.TrimmedMean(window, 0.29m, new decimal[100]).Value;

        Assert.Equal(50.5, dsl, 12);   // symmetric window: the mean of the kept values is 50.5 for either cut ...
        Assert.Equal(50.5m, exact);    // ... so the two differ only for a skewed window
        decimal[] skewed = Enumerable.Range(1, 100).Select(i => (decimal)(i * i)).ToArray();
        Assert.NotEqual(DslRobustStatisticsMath.TrimmedMean(skewed.Select(v => (double)v).ToArray(), 0.29).Value, (double)DecimalRobustLocationMath.TrimmedMean(skewed, 0.29m, new decimal[100]).Value, 6);
    }

    [Fact]
    public void ParameterConstants_EqualTheDslKernelDefaults()
    {
        Assert.Equal((decimal)DslRobustStatisticsMath.DefaultHuberK, IndicatorDefaultConstants.HuberMaDefaultTuningK);
        Assert.Equal((decimal)DslRobustStatisticsMath.DefaultBiweightC, IndicatorDefaultConstants.BiweightMaDefaultTuningC);
        Assert.Equal(DslRobustStatisticsMath.MaxTrimAlpha, IndicatorDefaultConstants.TrimFractionMaxAlpha);
        Assert.Equal((decimal)DslRobustStatisticsMath.MaxTrimAlpha, DecimalRobustLocationMath.MaxAlpha);
    }

    // ---------------------------------------------------------------- the indicators

    [Fact]
    public void TrimmedAndWinsorized_EqualTheDecimalKernelOnEveryWindow_AndTheDefaultIsInsideTheRange()
    {
        var candles = Candles(90);
        const int period = 20;
        decimal[] closes = candles.Select(c => c.Close).ToArray();
        var trimmed = new CoreTrimmedMaIndicator { Period = period }.Calculate(candles);
        var winsor = new CoreWinsorizedMaIndicator { Period = period, Alpha = 0.2m }.Calculate(candles);

        var scratch = new decimal[period];
        for (int i = 0; i < candles.Count; i++)
        {
            if (i < period - 1)
            {
                Assert.Null(trimmed.MainValues[i]);
                Assert.Null(winsor.MainValues[i]);
                continue;
            }

            var window = closes.AsSpan(i - period + 1, period);
            Assert.Equal(DecimalRobustLocationMath.TrimmedMean(window, 0.10m, scratch).Value, trimmed.MainValues[i]);
            Assert.Equal(DecimalRobustLocationMath.WinsorMean(window, 0.2m, scratch).Value, winsor.MainValues[i]);
        }
    }

    [Fact]
    public void HuberAndBiweight_EqualTheDslKernelOnEveryWindow_AndNeverShareABudgetAcrossBars()
    {
        // 600 bars with a window of 80: a budget shared by all bars (10,000 operations) would be exhausted long before the last bar.
        var candles = Candles(600);
        const int period = 80;
        double[] closes = candles.Select(c => (double)c.Close).ToArray();
        var huber = new CoreHuberMaIndicator { Period = period }.Calculate(candles);
        var biweight = new CoreBiweightMaIndicator { Period = period, TuningC = 5.5m }.Calculate(candles);

        int compared = 0;
        for (int i = period - 1; i < candles.Count; i++)
        {
            var budgetH = DslExecutionBudget.Create(CalculatorLimits.Default.MaxExecutionOps);
            var budgetB = DslExecutionBudget.Create(CalculatorLimits.Default.MaxExecutionOps);
            MathResult<double> h = DslRobustStatisticsMath.HuberLocation(closes.AsSpan(i - period + 1, period), ref budgetH, 1.345);
            MathResult<double> b = DslRobustStatisticsMath.BiweightLocation(closes.AsSpan(i - period + 1, period), ref budgetB, 5.5);
            Assert.True(h.IsOk && b.IsOk);
            Assert.Equal(IndicatorCalculationHelper.ToDecimalOrNull(h.Value), huber.MainValues[i]);
            Assert.Equal(IndicatorCalculationHelper.ToDecimalOrNull(b.Value), biweight.MainValues[i]);
            compared++;
        }

        Assert.Equal(candles.Count - period + 1, compared);
        Assert.NotNull(huber.MainValues[^1]);
        Assert.NotNull(biweight.MainValues[^1]);
    }

    [Fact]
    public void Huber_WithAZeroMad_IsTheMedian_SoALoneOutlierDoesNotMoveIt()
    {
        var closes = Enumerable.Repeat(100m, 9).Concat(new[] { 1000m }).ToArray();
        var result = new CoreHuberMaIndicator { Period = 10 }.Calculate(FromCloses(closes));

        Assert.Equal(100m, result.MainValues[9]);
    }

    [Fact]
    public void GeometricAndHarmonic_EqualTheDecimalKernelAndTheClosedForm()
    {
        var one = FromCloses(new[] { 1m, 2m, 4m });
        var geometric = new CoreGeometricMaIndicator { Period = 3 }.Calculate(one);
        var harmonic = new CoreHarmonicMaIndicator { Period = 3 }.Calculate(one);

        Assert.Equal(2.0, (double)geometric.MainValues[2]!.Value, 12);            // (1 x 2 x 4)^(1/3)
        Assert.Equal(3.0 / 1.75, (double)harmonic.MainValues[2]!.Value, 12);      // 3 / (1 + 1/2 + 1/4)

        var candles = Candles(70);
        const int period = 15;
        decimal[] closes = candles.Select(c => c.Close).ToArray();
        var g = new CoreGeometricMaIndicator { Period = period }.Calculate(candles);
        var h = new CoreHarmonicMaIndicator { Period = period }.Calculate(candles);
        for (int i = period - 1; i < candles.Count; i++)
        {
            Assert.Equal(DslStatisticsMath.GeometricMean(closes.AsSpan(i - period + 1, period)).Value, g.MainValues[i]);
            Assert.Equal(DslStatisticsMath.HarmonicMean(closes.AsSpan(i - period + 1, period)).Value, h.MainValues[i]);
        }
    }

    [Fact]
    public void AZeroPriceGivesNoGeometricOrHarmonicValue_ButTheOrderStatisticAveragesStillHaveOne()
    {
        var candles = Candles(60);
        candles[30] = new CoreCandleData(candles[30].Timestamp, 0m, 0m, 0m, 0m, 1000);
        const int period = 5;

        var geometric = new CoreGeometricMaIndicator { Period = period }.Calculate(candles);
        var harmonic = new CoreHarmonicMaIndicator { Period = period }.Calculate(candles);
        var trimmed = new CoreTrimmedMaIndicator { Period = period }.Calculate(candles);

        for (int i = 30; i <= 34; i++)
        {
            Assert.Null(geometric.MainValues[i]);
            Assert.Null(harmonic.MainValues[i]);
            Assert.NotNull(trimmed.MainValues[i]);
        }

        Assert.NotNull(geometric.MainValues[35]);
        Assert.NotNull(geometric.MainValues[29]);
    }

    [Fact]
    public void ParametersAreValidated_AndTheDefaultsAreTheDocumentedOnes()
    {
        Assert.Equal(20, new CoreTrimmedMaParameter().Period);
        Assert.Equal(0.10m, new CoreTrimmedMaParameter().Alpha);
        Assert.Equal(0.10m, new CoreWinsorizedMaParameter().Alpha);
        Assert.Equal(1.345m, new CoreHuberMaParameter().TuningK);
        Assert.Equal(4.685m, new CoreBiweightMaParameter().TuningC);

        foreach (CoreWindowAverageParameterBase parameter in new CoreWindowAverageParameterBase[] { new CoreTrimmedMaParameter(), new CoreWinsorizedMaParameter(), new CoreHuberMaParameter(), new CoreBiweightMaParameter(), new CoreGeometricMaParameter(), new CoreHarmonicMaParameter() })
        {
            parameter.Period = 1;
            Assert.Throws<ArgumentOutOfRangeException>(() => parameter.Validate());
            parameter.Period = 501;
            Assert.Throws<ArgumentOutOfRangeException>(() => parameter.Validate());
            parameter.Period = 2;
            parameter.Validate();
            parameter.Period = 500;
            parameter.Validate();
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreTrimmedMaParameter { Alpha = 0.5m }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreWinsorizedMaParameter { Alpha = -0.1m }.Validate());
        new CoreTrimmedMaParameter { Alpha = 0.49m }.Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreHuberMaParameter { TuningK = 0.09m }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreHuberMaParameter { TuningK = 10.1m }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreBiweightMaParameter { TuningC = 0.4m }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreBiweightMaParameter { TuningC = 20.1m }.Validate());
        Assert.False(new CoreTrimmedMaIndicator { Alpha = 0.6m }.Calculate(Candles(40)).IsSuccessful);
        Assert.False(new CoreHuberMaIndicator { Period = 1 }.Calculate(Candles(40)).IsSuccessful);
    }

    // ---------------------------------------------------------------- contract shared by the six types

    public static IEnumerable<object[]> TheTypes() => new[]
    {
        new object[] { IndicatorType.TrimmedMA, "Trend" },
        new object[] { IndicatorType.WinsorizedMA, "Trend" },
        new object[] { IndicatorType.HuberMA, "Trend" },
        new object[] { IndicatorType.BiweightMA, "Trend" },
        new object[] { IndicatorType.GeometricMA, "Trend" },
        new object[] { IndicatorType.HarmonicMA, "Trend" },
    };

    [Theory]
    [MemberData(nameof(TheTypes))]
    public void TheIndicatorIsRegistered_AsAPriceOverlay_DisabledByDefault(IndicatorType type, string group)
    {
        Assert.True(IndicatorFactory.Default.IsRegistered(type));
        Assert.True(((CoreIndicatorBase)IndicatorFactory.Default.Create(type)!).IsOverlay);

        CoreIndicatorSettings settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == type);
        Assert.False(settings.IsEnabled);
        Assert.True(settings.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Trend, settings.Category);
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
        Assert.Equal(ChannelNormalization.WindowMinMax, FeatureChannelNormalizationDefaults.ForIndicator(type));
    }

    [Theory]
    [MemberData(nameof(TheTypes))]
    public void EveryDefaultSeriesIsEmitted_AndMainIsMappedExplicitly(IndicatorType type, string group)
    {
        _ = group;
        var factory = IndicatorFactory.Default;
        var result = factory.Create(type)!.Calculate(Candles(120));
        Assert.True(result.IsSuccessful);
        Assert.Contains(result.MainValues, v => v.HasValue);

        foreach (string display in IndicatorOutputSeriesResolver.GetDisplayNames(type, factory))
        {
            string name = IndicatorOutputSeriesResolver.Normalize(type, display, indicatorFactory: factory);
            Assert.True(result.HasSeries(name), $"{type}: '{name}' is not emitted");
        }

        CoreIndicatorSettings settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == type);
        Assert.Contains(settings.SeriesColors, sc => sc.Name == IndicatorResult.MainSeriesName && sc.TargetSeries.Contains(IndicatorResult.MainSeriesName));
    }

    [Theory]
    [MemberData(nameof(TheTypes))]
    public void TheIndicatorIsCausal_AllowedForBacktestAndScreener_AndSupportsAnIndicatorTimeframe(IndicatorType type, string group)
    {
        _ = group;
        Assert.DoesNotContain(type, BacktestIndicatorEligibility.NonCausalIndicatorTypes);
        Assert.DoesNotContain(type, BacktestIndicatorEligibility.SynchronousCalculationUnsupportedTypes);
        Assert.False(ScreenerIndicatorEligibility.IsTwoTickerType(type));
        Assert.True(IndicatorTimeframePolicy.SupportsIndicatorTimeframe(type));
    }

    [Theory]
    [MemberData(nameof(TheTypes))]
    public void FutureBarsDoNotChangeThePast_AndEmptyInputGivesNoValue(IndicatorType type, string group)
    {
        _ = group;
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

        Assert.Empty(factory.Create(type)!.Calculate(new List<CoreCandleData>()).MainValues);
        Assert.All(factory.Create(type)!.Calculate(Candles(1)).MainValues, v => Assert.Null(v));
    }
}
