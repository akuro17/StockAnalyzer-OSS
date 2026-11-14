using System;
using System.Globalization;
using System.Linq;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Drawing;

public sealed class DatePriceRangeValuesTests
{
    private static readonly DateTime MonWeek1 = new(2026, 1, 5, 9, 0, 0);
    private static readonly IndicatorColor BlueColor = new(255, 33, 150, 243);
    private static readonly IndicatorColor RedColor = new(255, 244, 67, 54);
    private static readonly IndicatorColor GreenColor = new(255, 76, 175, 80);

    private static CoreCandleData[] CreateTenBusinessDayCandles()
    {
        var candles = new CoreCandleData[10];
        // Week 1: Mon Jan 5 .. Fri Jan 9 (5 bars), Week 2: Mon Jan 12 .. Fri Jan 16 (5 bars)
        for (int i = 0; i < 5; i++)
        {
            candles[i] = new CoreCandleData(MonWeek1.AddDays(i), 100m, 110m, 95m, 105m, 1_000L * (i + 1));
            candles[5 + i] = new CoreCandleData(MonWeek1.AddDays(7 + i), 105m, 125m, 100m, 120m, 1_000L * (6 + i));
        }

        return candles;
    }

    [Fact]
    public void Calculate_TenBusinessDaysOver13CalendarDays_ReturnsExpectedMetrics()
    {
        var candles = CreateTenBusinessDayCandles();
        var p1 = new DrawingAnchor(MonWeek1, 100m);
        var p2 = new DrawingAnchor(MonWeek1.AddDays(13), 125m); // Jan 5 -> Jan 18 (13 calendar days)

        var values = DatePriceRangeValues.Calculate(p1, p2, candles, BlueColor);

        Assert.Equal(6, values.Length);
        Assert.Equal(
            new[] { "PriceDelta", "PriceChangePercent", "BarCount", "CalendarDays", "TotalVolume", "AverageVolume" },
            values.Select(v => v.Key).ToArray());

        Assert.Equal(25m, values[0].NumericValue);
        Assert.Equal("+25.00", values[0].FormattedText);

        Assert.Equal(25m, values[1].NumericValue);
        Assert.Equal("+25.00%", values[1].FormattedText);

        Assert.Equal(10m, values[2].NumericValue);
        Assert.Equal("10", values[2].FormattedText);

        Assert.Equal(13m, values[3].NumericValue);
        Assert.Equal("13", values[3].FormattedText);

        Assert.Equal(55_000m, values[4].NumericValue);
        Assert.Equal("55,000", values[4].FormattedText);

        Assert.Equal(5_500m, values[5].NumericValue);
        Assert.Equal("5,500", values[5].FormattedText);

        // No raw anchor coordinate keys
        Assert.DoesNotContain(values, v => v.Key is "P1" or "P2" or "Start" or "End");
    }

    [Fact]
    public void Calculate_ReverseDrag_KeepsSameBarCountAndNegatesPriceDelta()
    {
        var candles = CreateTenBusinessDayCandles();
        var p1 = new DrawingAnchor(MonWeek1.AddDays(13), 125m);
        var p2 = new DrawingAnchor(MonWeek1, 100m);

        var values = DatePriceRangeValues.Calculate(p1, p2, candles, RedColor);

        Assert.Equal(-25m, values[0].NumericValue);
        Assert.Equal("-25.00", values[0].FormattedText);
        Assert.Equal(-20m, values[1].NumericValue);
        Assert.Equal("-20.00%", values[1].FormattedText);
        Assert.Equal(10m, values[2].NumericValue);
        Assert.Equal(13m, values[3].NumericValue);
        Assert.Equal(55_000m, values[4].NumericValue);
        Assert.Equal(5_500m, values[5].NumericValue);
    }

    [Fact]
    public void Calculate_NoBarsInRangeOrEmptyCandles_ReturnsZeroBarsAndNullAverageVolume()
    {
        var candles = CreateTenBusinessDayCandles();
        var outOfRangeP1 = new DrawingAnchor(MonWeek1.AddDays(30), 100m);
        var outOfRangeP2 = new DrawingAnchor(MonWeek1.AddDays(35), 110m);

        var outValues = DatePriceRangeValues.Calculate(outOfRangeP1, outOfRangeP2, candles, IndicatorColor.Gray);
        Assert.Equal(0m, outValues[2].NumericValue);
        Assert.Equal("0", outValues[2].FormattedText);
        Assert.Equal(0m, outValues[4].NumericValue);
        Assert.Null(outValues[5].NumericValue);
        Assert.Equal("-", outValues[5].FormattedText);

        var emptyValues = DatePriceRangeValues.Calculate(outOfRangeP1, outOfRangeP2, Array.Empty<CoreCandleData>(), IndicatorColor.Gray);
        Assert.Equal(0m, emptyValues[2].NumericValue);
        Assert.Null(emptyValues[5].NumericValue);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void Calculate_NonPositiveStartPrice_ReturnsNullPriceChangePercent(decimal startPrice)
    {
        var p1 = new DrawingAnchor(MonWeek1, startPrice);
        var p2 = new DrawingAnchor(MonWeek1.AddDays(1), 50m);

        var values = DatePriceRangeValues.Calculate(p1, p2, Array.Empty<CoreCandleData>(), GreenColor);

        Assert.Equal(50m - startPrice, values[0].NumericValue);
        Assert.Null(values[1].NumericValue);
        Assert.Equal("-", values[1].FormattedText);
    }

    [Fact]
    public void Calculate_ZeroDelta_FormatsWithoutSign()
    {
        var p1 = new DrawingAnchor(MonWeek1, 100m);
        var p2 = new DrawingAnchor(MonWeek1, 100m);

        var values = DatePriceRangeValues.Calculate(p1, p2, Array.Empty<CoreCandleData>(), IndicatorColor.Gray);

        Assert.Equal(0m, values[0].NumericValue);
        Assert.Equal("0.00", values[0].FormattedText);
        Assert.Equal(0m, values[1].NumericValue);
        Assert.Equal("0.00%", values[1].FormattedText);
    }

    [Fact]
    public void Calculate_TotalVolumeOverflow_ReturnsNullTotalAndAverageVolume()
    {
        var candles = new[]
        {
            new CoreCandleData(MonWeek1, 100m, 105m, 95m, 100m, long.MaxValue),
            new CoreCandleData(MonWeek1.AddDays(1), 100m, 105m, 95m, 100m, 1L)
        };
        var p1 = new DrawingAnchor(MonWeek1, 100m);
        var p2 = new DrawingAnchor(MonWeek1.AddDays(1), 100m);

        var values = DatePriceRangeValues.Calculate(p1, p2, candles, BlueColor);

        Assert.Equal(2m, values[2].NumericValue);
        Assert.Null(values[4].NumericValue);
        Assert.Equal("-", values[4].FormattedText);
        Assert.Null(values[5].NumericValue);
        Assert.Equal("-", values[5].FormattedText);
    }

    [Fact]
    public void Calculate_UsesFixedCultureRegardlessOfThreadCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var candles = new[]
            {
                new CoreCandleData(MonWeek1, 100m, 105m, 95m, 100m, 1_500L)
            };
            var p1 = new DrawingAnchor(MonWeek1, 100m);
            var p2 = new DrawingAnchor(MonWeek1.AddDays(1), 125m);

            var values = DatePriceRangeValues.Calculate(p1, p2, candles, BlueColor);

            Assert.Equal("+25.00", values[0].FormattedText);
            Assert.Equal("+25.00%", values[1].FormattedText);
            Assert.Equal("1,500", values[4].FormattedText);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
