using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Utilities;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class PriceDataHelperFillPricesTests
{
    private static List<CoreCandleData> Candles(int count)
    {
        var candles = new List<CoreCandleData>(count);
        for (int i = 0; i < count; i++)
        {
            decimal open = 100m + (i % 7) * 1.5m - (i % 3) * 0.75m;
            decimal close = open + ((i % 5) - 2) * 1.25m;
            decimal high = Math.Max(open, close) + 1m + (i % 4) * 0.5m;
            decimal low = Math.Min(open, close) - 1m - (i % 3) * 0.25m;
            candles.Add(new CoreCandleData(DateTime.Today.AddDays(i), open, high, low, close, 1000 + i));
        }

        return candles;
    }

    private static decimal HaField(CoreCandleData ha, PriceType type) => type switch
    {
        PriceType.HeikinAshiOpen => ha.Open,
        PriceType.HeikinAshiHigh => ha.High,
        PriceType.HeikinAshiLow => ha.Low,
        _ => ha.Close
    };

    public static IEnumerable<object[]> HeikinAshiTypes() => new[]
    {
        new object[] { PriceType.HeikinAshiOpen },
        new object[] { PriceType.HeikinAshiHigh },
        new object[] { PriceType.HeikinAshiLow },
        new object[] { PriceType.HeikinAshiClose },
    };

    [Theory]
    [MemberData(nameof(HeikinAshiTypes))]
    public void FillPrices_HeikinAshi_EqualsConverterForEveryStartAndCount(PriceType type)
    {
        var candles = Candles(40);
        var converted = HeikinAshiConverter.Convert(candles);

        foreach (var (start, count) in new[] { (0, 40), (0, 1), (1, 5), (7, 20), (39, 1), (12, 28) })
        {
            var buffer = new decimal[count];
            PriceDataHelper.FillPrices(candles, start, buffer, type);

            for (int k = 0; k < count; k++)
            {
                Assert.Equal(HaField(converted[start + k], type), buffer[k]);
            }
        }
    }

    [Fact]
    public void FillPrices_HeikinAshiOpen_MatchesHandComputedRecursion()
    {
        // Same candles as HeikinAshiRecursionTests: HA open = 102.5, 102.5, 105.625, 108.6875, 107.46875.
        var candles = new List<CoreCandleData>
        {
            new(DateTime.Today.AddDays(0), 100m, 110m, 95m, 105m, 1000),
            new(DateTime.Today.AddDays(1), 106m, 115m, 102m, 112m, 1200),
            new(DateTime.Today.AddDays(2), 111m, 118m, 108m, 110m, 1100),
            new(DateTime.Today.AddDays(3), 109m, 114m, 100m, 102m, 1500),
            new(DateTime.Today.AddDays(4), 103m, 108m, 98m, 107m, 1300),
        };

        var all = new decimal[5];
        PriceDataHelper.FillPrices(candles, 0, all, PriceType.HeikinAshiOpen);
        Assert.Equal(new[] { 102.5m, 102.5m, 105.625m, 108.6875m, 107.46875m }, all);

        var tail = new decimal[3];
        PriceDataHelper.FillPrices(candles, 2, tail, PriceType.HeikinAshiOpen);
        Assert.Equal(new[] { 105.625m, 108.6875m, 107.46875m }, tail);
    }

    public static IEnumerable<object[]> NonHeikinAshiTypes()
    {
        foreach (PriceType type in Enum.GetValues<PriceType>())
        {
            if (!PriceDataHelper.IsHeikinAshi(type))
            {
                yield return new object[] { type };
            }
        }
    }

    // Every non-Heikin-Ashi type equals the series value (True High / True Low use the previous close, also for the first bar of a
    // window that does not start the series).
    [Theory]
    [MemberData(nameof(NonHeikinAshiTypes))]
    public void FillPrices_NonHeikinAshi_EqualsExtractPriceSeries(PriceType type)
    {
        var candles = Candles(25);
        var series = PriceDataHelper.ExtractPriceSeries(candles, type);

        foreach (int start in new[] { 0, 3 })
        {
            var buffer = new decimal[20];
            PriceDataHelper.FillPrices(candles, start, buffer, type);

            for (int k = 0; k < buffer.Length; k++)
            {
                Assert.Equal(series[start + k], buffer[k]);
            }
        }
    }

    [Theory]
    [InlineData(PriceType.TrueHigh)]
    [InlineData(PriceType.TrueLow)]
    public void FillPrices_TrueHighLow_UsePreviousCloseIncludingBeforeTheWindow(PriceType type)
    {
        var candles = new List<CoreCandleData>
        {
            new(DateTime.Today.AddDays(0), 100m, 110m, 95m, 120m, 1000),
            new(DateTime.Today.AddDays(1), 100m, 105m, 98m, 101m, 1000),
            new(DateTime.Today.AddDays(2), 100m, 105m, 98m, 90m, 1000),
        };

        var buffer = new decimal[2];
        PriceDataHelper.FillPrices(candles, 1, buffer, type);

        // Bar 1 sees the close of bar 0 (120): True High = max(105, 120) = 120, True Low = min(98, 120) = 98.
        // Bar 2 sees the close of bar 1 (101): True High = 105, True Low = min(98, 101) = 98.
        decimal[] expected = type == PriceType.TrueHigh ? new[] { 120m, 105m } : new[] { 98m, 98m };
        Assert.Equal(expected, buffer);
    }

    [Fact]
    public void FillPrices_EmptyDestination_DoesNothing()
    {
        PriceDataHelper.FillPrices(Candles(3), 3, Span<decimal>.Empty, PriceType.HeikinAshiOpen);
    }

    [Theory]
    [InlineData(-1, 2)]
    [InlineData(4, 2)]
    [InlineData(0, 6)]
    [InlineData(int.MaxValue, 2)]
    public void FillPrices_InvalidRange_Throws(int startIndex, int length)
    {
        var candles = Candles(5);
        var buffer = new decimal[length];

        Assert.Throws<ArgumentOutOfRangeException>(() => PriceDataHelper.FillPrices(candles, startIndex, buffer, PriceType.Close));
    }

    [Fact]
    public void IsHeikinAshi_IsTrueOnlyForTheFourHeikinAshiTypes()
    {
        foreach (PriceType type in Enum.GetValues<PriceType>())
        {
            bool expected = type is PriceType.HeikinAshiOpen or PriceType.HeikinAshiHigh or PriceType.HeikinAshiLow or PriceType.HeikinAshiClose;
            Assert.Equal(expected, PriceDataHelper.IsHeikinAshi(type));
        }
    }
}
