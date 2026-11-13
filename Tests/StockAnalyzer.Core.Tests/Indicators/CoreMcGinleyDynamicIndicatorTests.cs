using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

/// <summary>
/// WebAiExpansion plan 03 T3 (decision D-14=A+a'): MD starts as the mean of the first N positive prices; a non-positive price gives null and keeps the state;
/// MD = MD_prev + (P - MD_prev) / max(1, k x N x (P / MD_prev)^4); a price ratio above 1,000,000 holds MD.
/// </summary>
public class CoreMcGinleyDynamicIndicatorTests
{
    private static List<CoreCandleData> Candles(IEnumerable<decimal> closes) =>
        closes.Select((c, i) => new CoreCandleData(new DateTime(2024, 1, 1).AddDays(i), c, c, c, c, 1000)).ToList();

    private static IReadOnlyList<decimal?> Run(IEnumerable<decimal> closes, int period, decimal constant) =>
        new CoreMcGinleyDynamicIndicator { Period = period, Constant = constant }.Calculate(Candles(closes)).MainValues;

    [Fact]
    public void NoCandles_ReturnsASuccessfulEmptyResult()
    {
        var result = new CoreMcGinleyDynamicIndicator().Calculate(new List<CoreCandleData>());

        Assert.True(result.IsSuccessful);
        Assert.Empty(result.MainValues);
    }

    [Theory]
    [InlineData(1, 14)]
    [InlineData(13, 14)]
    [InlineData(4, 5)]
    public void FewerPositivePricesThanPeriod_AreAllNullAndKeepTheLength(int bars, int period)
    {
        var values = Run(Enumerable.Range(0, bars).Select(i => 100m + i), period, 0.6m);

        Assert.Equal(bars, values.Count);
        Assert.All(values, v => Assert.Null(v));
    }

    [Fact]
    public void FirstValueIsTheMeanOfTheFirstNPrices()
    {
        var values = Run(new[] { 10m, 20m, 60m, 30m }, 3, 0.6m);

        Assert.Null(values[0]);
        Assert.Null(values[1]);
        Assert.Equal((10m + 20m + 60m) / 3m, values[2]);
    }

    [Fact]
    public void OneStepHandComputedValue()
    {
        // N = 14, k = 0.6: 14 prices of 100 give MD = 100; then P = 110: r = 1.1, r^4 = 1.4641, den = 0.6 x 14 x 1.4641 = 12.29844, MD = 100 + 10 / 12.29844 = 100.8131...
        var closes = Enumerable.Repeat(100m, 14).Append(110m).ToList();

        var values = Run(closes, 14, 0.6m);

        Assert.Equal(100m, values[13]);
        decimal expected = 100m + 10m / (0.6m * 14m * 1.4641m);
        Assert.Equal((double)expected, (double)values[14]!.Value, 12);
        Assert.Equal(100.8131, (double)values[14]!.Value, 4);
    }

    [Fact]
    public void ConstantSeries_GivesTheConstant()
    {
        var values = Run(Enumerable.Repeat(42.5m, 40), 7, 0.6m);

        Assert.All(values.Skip(6), v => Assert.Equal(42.5m, v));
    }

    [Fact]
    public void ANonPositivePriceGivesNull_AndTheNextPositivePriceContinuesFromTheStateBeforeIt()
    {
        var withZero = Run(new[] { 100m, 100m, 110m, 0m, 120m }, 2, 0.6m);
        var withoutZero = Run(new[] { 100m, 100m, 110m, 120m }, 2, 0.6m);

        Assert.Null(withZero[3]);
        Assert.NotNull(withZero[2]);
        Assert.Equal(withoutZero[2], withZero[2]);
        Assert.Equal(withoutZero[3], withZero[4]);
    }

    [Fact]
    public void LeadingNonPositivePricesAreNotCounted()
    {
        var values = Run(new[] { 0m, -5m, 100m, 0m, 120m, 140m }, 3, 0.6m);

        // positive prices: idx2 (100), idx4 (120), idx5 (140) -> the third one is at idx5
        Assert.All(values.Take(5), v => Assert.Null(v));
        Assert.Equal((100m + 120m + 140m) / 3m, values[5]);
    }

    [Fact]
    public void ADivisorBelowOneIsRaisedToOne_SoAPriceCrashFollowsThePriceInOneStep()
    {
        // N = 1, k = 0.1, MD = 100, P = 50: r = 0.5, r^4 = 0.0625, den = 0.00625 -> raised to 1, MD = P.
        var values = Run(new[] { 100m, 50m }, 1, 0.1m);

        Assert.Equal(50m, values[1]);
    }

    [Fact]
    public void APriceRatioAboveOneMillionHoldsTheValue()
    {
        var values = Run(new[] { 1m, 2_000_000m, 1_000_000m }, 1, 0.6m);

        Assert.Equal(1m, values[0]);
        Assert.Equal(1m, values[1]);
        // 1,000,000 / 1 is exactly the limit, which is NOT above it: the normal step applies (den = 0.6 x 1 x 1e24).
        Assert.True(values[2]!.Value > 1m);
    }

    [Fact]
    public void ALongRally_FreezesTheLineWithTheDefaultConstant_AndALowerConstantFollowsIt()
    {
        // Documented property of the formula (owner decision 2026-11-14, option A: keep the standard definition): the divisor k x N x (P / MD)^4
        // grows so fast once the price is far above MD that MD stops following. 2.5 % per bar for 160 bars is a 50-fold rally.
        var rally = new List<decimal>();
        decimal price = 900m;
        for (int i = 0; i < 160; i++)
        {
            price *= 1.025m;
            rally.Add(price);
        }

        var slow = Run(rally, 14, 0.6m);
        var followed = Run(rally, 14, 0.3m);

        Assert.True(slow[^1]!.Value / rally[^1] < 0.1m, "with Constant 0.6 MD must be left far behind the price");
        Assert.True(followed[^1]!.Value / rally[^1] > 0.7m, "with Constant 0.3 MD must follow the price");
    }

    [Fact]
    public void TheConstantDescriptionExplainsTheRallyBehaviourAndTheRemedy()
    {
        var property = typeof(CoreMcGinleyDynamicParameter).GetProperty(nameof(CoreMcGinleyDynamicParameter.Constant))!;
        string description = ((System.ComponentModel.DescriptionAttribute)Attribute.GetCustomAttribute(property, typeof(System.ComponentModel.DescriptionAttribute))!).Description;

        Assert.Contains("rally", description);
        Assert.Contains("stops following", description);
        Assert.Contains("lower the Constant", description);
    }

    [Fact]
    public void EveryOutputIsFiniteAndPositive_ForAWildSeries()
    {
        var rng = new Random(20261114);
        var closes = new List<decimal>();
        decimal price = 100m;
        for (int i = 0; i < 400; i++)
        {
            price = Math.Max(0.0001m, price * (decimal)(0.5 + rng.NextDouble()));
            closes.Add(price);
        }

        var values = Run(closes, 14, 0.6m);

        Assert.Equal(closes.Count, values.Count);
        Assert.All(values.Skip(13), v => Assert.True(v.HasValue && v.Value > 0m));
    }

    [Fact]
    public void ValuesAtBarTDoNotDependOnLaterBars()
    {
        var closes = Enumerable.Range(0, 60).Select(i => 100m + (i * 7 % 13) - 6m).ToList();

        var full = Run(closes, 5, 0.6m);
        var cut = Run(closes.Take(30), 5, 0.6m);

        for (int i = 0; i < 30; i++) Assert.Equal(full[i], cut[i]);
    }

    [Fact]
    public void IsRegisteredWithItsParameterAndAnOverlayTrendDefault()
    {
        Assert.True(IndicatorFactory.Default.IsRegistered(IndicatorType.McGinleyDynamic));
        Assert.IsType<CoreMcGinleyDynamicIndicator>(IndicatorFactory.Default.Create(IndicatorType.McGinleyDynamic));

        var settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == IndicatorType.McGinleyDynamic);
        Assert.IsType<CoreMcGinleyDynamicParameter>(settings.ParameterObject);
        Assert.True(settings.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Trend, settings.Category);
        Assert.Equal(IndicatorDefaultConstants.SmaColor, settings.Color);
    }

    [Fact]
    public void ConfigureAppliesTheParameterObject()
    {
        var indicator = new CoreMcGinleyDynamicIndicator();

        indicator.Configure(new CoreMcGinleyDynamicParameter { Period = 9, Constant = 1.2m });

        Assert.Equal(9, indicator.Period);
        Assert.Equal(1.2m, indicator.Constant);
    }

    [Fact]
    public void DefaultParameterValidates_AndOutOfRangeValuesThrow()
    {
        var defaults = new CoreMcGinleyDynamicParameter();
        defaults.Validate();
        Assert.Equal(IndicatorDefaultConstants.McGinleyDefaultPeriod, defaults.Period);
        Assert.Equal(IndicatorDefaultConstants.McGinleyDefaultConstant, defaults.Constant);

        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreMcGinleyDynamicParameter { Period = IndicatorDefaultConstants.DefaultPeriodMin - 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreMcGinleyDynamicParameter { Period = IndicatorDefaultConstants.DefaultPeriodMax + 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreMcGinleyDynamicParameter { Constant = 0.09m }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreMcGinleyDynamicParameter { Constant = 2.01m }.Validate());
    }
}
