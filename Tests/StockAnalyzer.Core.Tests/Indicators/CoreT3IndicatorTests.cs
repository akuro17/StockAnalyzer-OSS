using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

/// <summary>WebAiExpansion plan 03 T2: T3 = c1 x e6 + c2 x e5 + c3 x e4 + c4 x e3 over six cascaded EMAs (Tillson), v = VolumeFactor.</summary>
public class CoreT3IndicatorTests
{
    private const decimal Tolerance = 0.000000001m;

    private static List<CoreCandleData> Candles(IEnumerable<decimal> closes) =>
        closes.Select((c, i) => new CoreCandleData(new DateTime(2024, 1, 1).AddDays(i), c, c + 1m, c - 1m, c, 1000)).ToList();

    private static List<decimal> WavyCloses(int count) =>
        Enumerable.Range(0, count).Select(i => 100m + i * 0.4m + (i * 7 % 11) - 5m).ToList();

    // Independent EMA written out here (SMA seed of the first `period` values, multiplier 2 / (period + 1)); inputs have no gaps.
    private static decimal?[] Ema(IReadOnlyList<decimal?> input, int period)
    {
        var output = new decimal?[input.Count];
        decimal multiplier = 2m / (period + 1);
        decimal? previous = null;
        for (int i = 0; i < input.Count; i++)
        {
            if (input[i] is null) continue;
            if (previous is null)
            {
                int start = i - period + 1;
                if (start < 0 || input.Skip(start).Take(period).Any(v => v is null)) continue;
                previous = input.Skip(start).Take(period).Sum(v => v!.Value) / period;
            }
            else
            {
                previous = previous + (input[i]!.Value - previous.Value) * multiplier;
            }

            output[i] = previous;
        }

        return output;
    }

    private static decimal?[] ExpectedT3(IReadOnlyList<decimal> prices, int period, decimal v)
    {
        decimal?[] e = prices.Select(p => (decimal?)p).ToArray();
        decimal?[][] stage = new decimal?[7][];
        stage[0] = e;
        for (int k = 1; k <= 6; k++) stage[k] = Ema(stage[k - 1], period);

        decimal c1 = -v * v * v;
        decimal c2 = 3 * v * v + 3 * v * v * v;
        decimal c3 = -6 * v * v - 3 * v - 3 * v * v * v;
        decimal c4 = 1 + 3 * v + v * v * v + 3 * v * v;
        var result = new decimal?[prices.Count];
        for (int i = 0; i < prices.Count; i++)
        {
            if (stage[6][i] is null) continue;
            result[i] = c1 * stage[6][i]!.Value + c2 * stage[5][i]!.Value + c3 * stage[4][i]!.Value + c4 * stage[3][i]!.Value;
        }

        return result;
    }

    [Fact]
    public void NoCandles_ReturnsASuccessfulEmptyResult()
    {
        var result = new CoreT3Indicator().Calculate(new List<CoreCandleData>());

        Assert.True(result.IsSuccessful);
        Assert.Empty(result.MainValues);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(10, 5)]
    [InlineData(24, 5)]
    [InlineData(30, 6)]
    public void FewerBarsThanNeeded_AreAllNullAndKeepTheLength(int bars, int period)
    {
        // The first value is at index 6 x (Period - 1): Period 5 needs 25 bars, Period 6 needs 31.
        var indicator = new CoreT3Indicator { Period = period };

        var result = indicator.Calculate(Candles(WavyCloses(bars)));

        Assert.True(result.IsSuccessful);
        Assert.Equal(bars, result.MainValues.Count);
        Assert.All(result.MainValues, v => Assert.Null(v));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(3)]
    [InlineData(2)]
    public void FirstValueIsAtSixTimesPeriodMinusOne(int period)
    {
        var result = new CoreT3Indicator { Period = period, VolumeFactor = 0.7m }.Calculate(Candles(WavyCloses(60)));

        int first = result.MainValues.ToList().FindIndex(v => v.HasValue);
        Assert.Equal(6 * (period - 1), first);
        Assert.All(result.MainValues.Skip(first), v => Assert.NotNull(v));
    }

    [Fact]
    public void PeriodOne_ReturnsThePriceItself()
    {
        var closes = WavyCloses(20);

        var result = new CoreT3Indicator { Period = 1, VolumeFactor = 0.7m }.Calculate(Candles(closes));

        for (int i = 0; i < closes.Count; i++) Assert.Equal(closes[i], result.MainValues[i]);
    }

    [Fact]
    public void VolumeFactorZero_IsTheTripleExponentialAverage()
    {
        var closes = WavyCloses(60);

        var result = new CoreT3Indicator { Period = 4, VolumeFactor = 0m }.Calculate(Candles(closes));

        var e3 = Ema(Ema(Ema(closes.Select(c => (decimal?)c).ToArray(), 4), 4), 4);
        int firstValue = 6 * (4 - 1);
        for (int i = 0; i < closes.Count; i++)
        {
            if (i < firstValue)
            {
                Assert.Null(result.MainValues[i]);
                continue;
            }

            Assert.Equal((double)e3[i]!.Value, (double)result.MainValues[i]!.Value, 9);
        }
    }

    [Fact]
    public void ConstantSeries_GivesTheConstant()
    {
        var result = new CoreT3Indicator { Period = 3, VolumeFactor = 0.7m }.Calculate(Candles(Enumerable.Repeat(250m, 40)));

        foreach (decimal? v in result.MainValues.Skip(6 * 2))
        {
            Assert.NotNull(v);
            Assert.True(Math.Abs(v!.Value - 250m) < Tolerance, $"expected 250 but was {v}");
        }
    }

    [Theory]
    [InlineData(5, 0.7)]
    [InlineData(3, 1.0)]
    [InlineData(4, 0.35)]
    public void MatchesTheIndependentSixStageComputation(int period, double volumeFactor)
    {
        var closes = WavyCloses(80);
        decimal v = (decimal)volumeFactor;

        var result = new CoreT3Indicator { Period = period, VolumeFactor = v }.Calculate(Candles(closes));
        var expected = ExpectedT3(closes, period, v);

        Assert.Equal(closes.Count, result.MainValues.Count);
        for (int i = 0; i < closes.Count; i++)
        {
            if (expected[i] is null) { Assert.Null(result.MainValues[i]); continue; }
            Assert.NotNull(result.MainValues[i]);
            Assert.True(Math.Abs(expected[i]!.Value - result.MainValues[i]!.Value) < Tolerance, $"bar {i}: expected {expected[i]} but was {result.MainValues[i]}");
        }
    }

    [Fact]
    public void CoefficientsSumToOneForEveryVolumeFactor()
    {
        foreach (decimal v in new[] { 0m, 0.1m, 0.5m, 0.7m, 0.9m, 1m })
        {
            decimal c1 = -v * v * v;
            decimal c2 = 3 * v * v + 3 * v * v * v;
            decimal c3 = -6 * v * v - 3 * v - 3 * v * v * v;
            decimal c4 = 1 + 3 * v + v * v * v + 3 * v * v;
            Assert.Equal(1m, c1 + c2 + c3 + c4);
        }
    }

    [Fact]
    public void SeriesInput_WithLeadingNulls_StartsAfterTheFirstValue()
    {
        // CalculateSeries is how another indicator's output feeds the T3; the nulls delay the first value and are kept in place.
        var series = new List<decimal?> { null, null, null };
        series.AddRange(WavyCloses(20).Select(c => (decimal?)c));
        var indicator = new CoreT3Indicator { Period = 2, VolumeFactor = 0.7m };

        var result = indicator.CalculateSeries(series);

        Assert.True(result.IsSuccessful);
        Assert.Equal(series.Count, result.MainValues.Count);
        Assert.Equal(3 + 6, result.MainValues.ToList().FindIndex(v => v.HasValue));
    }

    [Fact]
    public void ValuesAtBarTDoNotDependOnLaterBars()
    {
        var closes = WavyCloses(60);
        var full = new CoreT3Indicator { Period = 3 }.Calculate(Candles(closes));
        var cut = new CoreT3Indicator { Period = 3 }.Calculate(Candles(closes.Take(35)));

        for (int i = 0; i < 35; i++) Assert.Equal(full.MainValues[i], cut.MainValues[i]);
    }

    [Fact]
    public void IsRegisteredWithItsParameterAndAnOverlayTrendDefault()
    {
        Assert.True(IndicatorFactory.Default.IsRegistered(IndicatorType.T3));
        Assert.IsType<CoreT3Indicator>(IndicatorFactory.Default.Create(IndicatorType.T3));

        var settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == IndicatorType.T3);
        Assert.IsType<CoreT3Parameter>(settings.ParameterObject);
        Assert.True(settings.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Trend, settings.Category);
        Assert.Equal(IndicatorDefaultConstants.SmaColor, settings.Color);
    }

    [Fact]
    public void ConfigureAppliesTheParameterObject()
    {
        var indicator = new CoreT3Indicator();

        indicator.Configure(new CoreT3Parameter { Period = 8, VolumeFactor = 0.4m });

        Assert.Equal(8, indicator.Period);
        Assert.Equal(0.4m, indicator.VolumeFactor);
    }

    [Fact]
    public void DefaultParameterValidates_AndOutOfRangeValuesThrow()
    {
        var defaults = new CoreT3Parameter();
        defaults.Validate();
        Assert.Equal(IndicatorDefaultConstants.T3DefaultPeriod, defaults.Period);
        Assert.Equal(IndicatorDefaultConstants.T3DefaultVolumeFactor, defaults.VolumeFactor);

        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreT3Parameter { Period = IndicatorDefaultConstants.DefaultPeriodMin - 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreT3Parameter { Period = IndicatorDefaultConstants.DefaultPeriodMax + 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreT3Parameter { VolumeFactor = -0.01m }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreT3Parameter { VolumeFactor = 1.01m }.Validate());
    }
}
