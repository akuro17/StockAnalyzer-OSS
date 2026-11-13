using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Trend;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

/// <summary>WebAiExpansion plan 03 T1: Chandelier Exit = two plain lines, HH - k x ATR (Main) and LL + k x ATR (ShortStop); no latch, no ratchet.</summary>
public class CoreChandelierExitIndicatorTests
{
    private static CoreCandleData Bar(int day, decimal high, decimal low, decimal close) =>
        new(new DateTime(2024, 1, 1).AddDays(day), close, high, low, close, 1000);

    // Hand-computed case (Period = 2, Multiplier = 1):
    //   bar:  0       1       2       3
    //   H:    10      12      13      15
    //   L:    8       9       10      11
    //   C:    9       11      12      14
    //   TR1 = max(12-9, |12-9|, |9-9|) = 3;  TR2 = max(13-10, |13-11|, |10-11|) = 3;  TR3 = max(15-11, |15-12|, |11-12|) = 4
    //   Wilder ATR(2): ATR2 = (TR1 + TR2) / 2 = 3;  ATR3 = (ATR2 x 1 + TR3) / 2 = 3.5
    //   HH(2): bar2 = max(12, 13) = 13, bar3 = max(13, 15) = 15;  LL(2): bar2 = min(9, 10) = 9, bar3 = min(10, 11) = 10
    //   Main = HH - ATR: bar2 = 13 - 3 = 10, bar3 = 15 - 3.5 = 11.5;  ShortStop = LL + ATR: bar2 = 9 + 3 = 12, bar3 = 10 + 3.5 = 13.5
    private static List<CoreCandleData> HandCandles() => new()
    {
        Bar(0, 10m, 8m, 9m),
        Bar(1, 12m, 9m, 11m),
        Bar(2, 13m, 10m, 12m),
        Bar(3, 15m, 11m, 14m),
    };

    [Fact]
    public void NoCandles_ReturnsASuccessfulEmptyResult()
    {
        var indicator = new CoreChandelierExitIndicator();

        var result = indicator.Calculate(new List<CoreCandleData>());

        Assert.True(result.IsSuccessful);
        Assert.Empty(result.MainValues);
        Assert.Empty(indicator.ShortStop);
    }

    [Theory]
    [InlineData(1, 22)]
    [InlineData(2, 22)]
    [InlineData(5, 5)]
    [InlineData(21, 22)]
    public void FewerBarsThanNeeded_AreAllNullAndKeepTheLength(int bars, int period)
    {
        // The first value is at bar index Period, so Period bars (indices 0..Period-1) give no value at all.
        var candles = Enumerable.Range(0, bars).Select(i => Bar(i, 100m + i + 2m, 100m + i - 2m, 100m + i)).ToList();
        var indicator = new CoreChandelierExitIndicator { Period = period };

        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(bars, result.MainValues.Count);
        Assert.Equal(bars, result.GetSeries(nameof(CoreChandelierExitIndicator.ShortStop)).Count);
        Assert.All(result.MainValues, v => Assert.Null(v));
        Assert.All(result.GetSeries(nameof(CoreChandelierExitIndicator.ShortStop)), v => Assert.Null(v));
    }

    [Fact]
    public void HandComputedValues_MatchTheFormula()
    {
        var indicator = new CoreChandelierExitIndicator { Period = 2, Multiplier = 1m };

        var result = indicator.Calculate(HandCandles());
        var main = result.MainValues;
        var shortStop = result.GetSeries(nameof(CoreChandelierExitIndicator.ShortStop));

        Assert.Null(main[0]);
        Assert.Null(main[1]);
        Assert.Equal(13m - 3m, main[2]);
        Assert.Equal(15m - 3.5m, main[3]);
        Assert.Null(shortStop[0]);
        Assert.Null(shortStop[1]);
        Assert.Equal(9m + 3m, shortStop[2]);
        Assert.Equal(10m + 3.5m, shortStop[3]);
    }

    [Fact]
    public void MultiplierScalesTheDistanceFromTheExtremesOnly()
    {
        var one = new CoreChandelierExitIndicator { Period = 2, Multiplier = 1m }.Calculate(HandCandles());
        var two = new CoreChandelierExitIndicator { Period = 2, Multiplier = 2m }.Calculate(HandCandles());

        // bar 3: HH = 15, LL = 10, ATR = 3.5
        Assert.Equal(15m - 2m * 3.5m, two.MainValues[3]);
        Assert.Equal(10m + 2m * 3.5m, two.GetSeries(nameof(CoreChandelierExitIndicator.ShortStop))[3]);
        Assert.NotEqual(one.MainValues[3], two.MainValues[3]);
    }

    [Fact]
    public void ThereIsNoLatchOrRatchet_TheLongStopFallsWhenTheHighestHighFalls()
    {
        // A price spike followed by a collapse: a ratcheting stop would never move down, the plain line does.
        var candles = new List<CoreCandleData>
        {
            Bar(0, 100m, 98m, 99m),
            Bar(1, 101m, 99m, 100m),
            Bar(2, 130m, 120m, 125m),
            Bar(3, 102m, 90m, 95m),
            Bar(4, 95m, 85m, 88m),
            Bar(5, 90m, 80m, 82m),
            Bar(6, 85m, 75m, 78m),
        };
        var result = new CoreChandelierExitIndicator { Period = 2, Multiplier = 1m }.Calculate(candles);

        var main = result.MainValues;
        Assert.True(main[6]!.Value < main[4]!.Value, "the long stop must be allowed to fall once the highest high falls");
    }

    [Fact]
    public void ValuesAtBarTDoNotDependOnLaterBars()
    {
        var candles = Enumerable.Range(0, 40).Select(i => Bar(i, 100m + i % 7 + 3m, 100m + i % 5 - 3m, 100m + i % 6)).ToList();
        var full = new CoreChandelierExitIndicator { Period = 5, Multiplier = 2m }.Calculate(candles);
        var cut = new CoreChandelierExitIndicator { Period = 5, Multiplier = 2m }.Calculate(candles.Take(25).ToList());

        for (int i = 0; i < 25; i++)
        {
            Assert.Equal(full.MainValues[i], cut.MainValues[i]);
            Assert.Equal(full.GetSeries(nameof(CoreChandelierExitIndicator.ShortStop))[i], cut.GetSeries(nameof(CoreChandelierExitIndicator.ShortStop))[i]);
        }
    }

    [Fact]
    public void IsRegisteredWithItsParameterAndAnOverlayTrendDefault()
    {
        Assert.True(IndicatorFactory.Default.IsRegistered(IndicatorType.ChandelierExit));
        Assert.IsType<CoreChandelierExitIndicator>(IndicatorFactory.Default.Create(IndicatorType.ChandelierExit));

        var settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == IndicatorType.ChandelierExit);
        Assert.IsType<CoreChandelierExitParameter>(settings.ParameterObject);
        Assert.True(settings.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Trend, settings.Category);
        Assert.Equal(IndicatorDefaultConstants.SmaColor, settings.Color);
        var shortStopColor = settings.SeriesColors.Single(c => c.Name == nameof(CoreChandelierExitIndicator.ShortStop));
        Assert.Equal(IndicatorDefaultConstants.StochDColor, shortStopColor.Color);
    }

    [Fact]
    public void BothStopsAreSelectableOutputs_AndTheResultHasExactlyTwoSeries()
    {
        var names = StockAnalyzer.Core.Services.IndicatorOutputSeriesResolver.GetDisplayNames(IndicatorType.ChandelierExit);
        Assert.Contains("LongStop", names);
        Assert.Contains("ShortStop", names);
        Assert.Equal("Main", StockAnalyzer.Core.Services.IndicatorOutputSeriesResolver.Normalize(IndicatorType.ChandelierExit, "LongStop"));
        Assert.Equal("ShortStop", StockAnalyzer.Core.Services.IndicatorOutputSeriesResolver.Normalize(IndicatorType.ChandelierExit, "ShortStop"));

        var result = new CoreChandelierExitIndicator { Period = 2, Multiplier = 1m }.Calculate(HandCandles());
        Assert.Equal(new[] { "Main", "ShortStop" }, result.SeriesNamesList.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void ConfigureAppliesTheParameterObject()
    {
        var indicator = new CoreChandelierExitIndicator();

        indicator.Configure(new CoreChandelierExitParameter { Period = 7, Multiplier = 2.5m });

        Assert.Equal(7, indicator.Period);
        Assert.Equal(2.5m, indicator.Multiplier);
    }

    [Fact]
    public void DefaultParameterValidates_AndOutOfRangeValuesThrow()
    {
        var defaults = new CoreChandelierExitParameter();
        defaults.Validate();
        Assert.Equal(IndicatorDefaultConstants.ChandelierExitDefaultPeriod, defaults.Period);
        Assert.Equal(IndicatorDefaultConstants.ChandelierExitDefaultMultiplier, defaults.Multiplier);

        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreChandelierExitParameter { Period = IndicatorDefaultConstants.DefaultPeriodMin - 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreChandelierExitParameter { Period = IndicatorDefaultConstants.DefaultPeriodMax + 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreChandelierExitParameter { Multiplier = (decimal)IndicatorDefaultConstants.ChandelierExitMinMultiplier - 0.01m }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreChandelierExitParameter { Multiplier = (decimal)IndicatorDefaultConstants.ChandelierExitMaxMultiplier + 0.01m }.Validate());
    }
}
