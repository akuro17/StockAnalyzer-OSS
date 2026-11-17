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

        Assert.Equal(10, values.Length);
        Assert.Equal(
            new[]
            {
                "PriceDelta", "PriceChangePercent", "BarCount", "CalendarDays", "TotalVolume", "AverageVolume",
                "HighestHigh", "LowestLow", "MaxDrawdown", "Volatility"
            },
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

        Assert.Equal(125m, values[6].NumericValue);
        Assert.Equal("125.000", values[6].FormattedText);
        Assert.Equal(95m, values[7].NumericValue);
        Assert.Equal("95.000", values[7].FormattedText);
        Assert.Equal(0m, values[8].NumericValue); // the closes never fall
        Assert.Equal("0.00%", values[8].FormattedText);
        AssertVolatility(4.451, values[9], "4.45%");

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

    // ---- HighestHigh / LowestLow / MaxDrawdown / Volatility (appended at indices 6..9) ----

    private const int HighestHighIndex = DatePriceRangeValues.HighestHighIndex;
    private const int LowestLowIndex = DatePriceRangeValues.LowestLowIndex;
    private const int MaxDrawdownIndex = DatePriceRangeValues.MaxDrawdownIndex;
    private const int VolatilityIndex = DatePriceRangeValues.VolatilityIndex;

    /// <summary>Tolerance of the hand-calculated volatility, in percent per bar.</summary>
    private const double VolatilityTolerance = 0.001;

    private static void AssertVolatility(double expectedPercent, DrawingCalculatedValue value, string expectedText)
    {
        Assert.Equal("Volatility", value.Key);
        Assert.NotNull(value.NumericValue);
        Assert.InRange((double)value.NumericValue!.Value, expectedPercent - VolatilityTolerance, expectedPercent + VolatilityTolerance);
        Assert.Equal(expectedText, value.FormattedText);
    }

    private static CoreCandleData[] CandlesFromCloses(params decimal[] closes)
    {
        var candles = new CoreCandleData[closes.Length];
        for (int i = 0; i < closes.Length; i++)
        {
            candles[i] = new CoreCandleData(MonWeek1.AddDays(i), closes[i], closes[i] + 5m, closes[i] - 5m, closes[i], 1_000L);
        }

        return candles;
    }

    private static DrawingCalculatedValue[] CalculateWholeSeries(CoreCandleData[] candles)
        => DatePriceRangeValues.Calculate(
            new DrawingAnchor(candles[0].Timestamp, 100m),
            new DrawingAnchor(candles[^1].Timestamp, 100m),
            candles,
            BlueColor);

    [Fact]
    public void Calculate_FirstSixValues_KeepKeyLabelAndOrder()
    {
        var values = DatePriceRangeValues.Calculate(
            new DrawingAnchor(MonWeek1, 100m),
            new DrawingAnchor(MonWeek1.AddDays(13), 125m),
            CreateTenBusinessDayCandles(),
            BlueColor);

        Assert.Equal(
            new[] { "Price Delta", "Price Change %", "Bar Count", "Calendar Days", "Total Volume", "Average Volume" },
            values.Take(6).Select(v => v.Label).ToArray());
        Assert.Equal(
            new[] { "Highest High", "Lowest Low", "Max Drawdown %", "Volatility %/bar" },
            values.Skip(6).Select(v => v.Label).ToArray());
    }

    [Fact]
    public void PublishedPositions_MatchTheKeysOfTheReturnedArray()
    {
        var values = CalculateWholeSeries(CandlesFromCloses(100m, 120m, 90m));

        Assert.Equal(DatePriceRangeValues.ValueCount, values.Length);
        Assert.Equal(6, DatePriceRangeValues.BaseValueCount);
        Assert.Equal("HighestHigh", values[DatePriceRangeValues.HighestHighIndex].Key);
        Assert.Equal("LowestLow", values[DatePriceRangeValues.LowestLowIndex].Key);
        Assert.Equal("MaxDrawdown", values[DatePriceRangeValues.MaxDrawdownIndex].Key);
        Assert.Equal("Volatility", values[DatePriceRangeValues.VolatilityIndex].Key);
        Assert.Equal("AverageVolume", values[DatePriceRangeValues.BaseValueCount - 1].Key);
    }

    [Fact]
    public void Calculate_MaxDrawdown_IsTheLargestFallFromTheRunningPeakClose()
    {
        var values = CalculateWholeSeries(CandlesFromCloses(100m, 120m, 90m, 110m, 80m, 100m));

        // Peak 120, lowest close after it 80: (120 - 80) / 120.
        Assert.NotNull(values[MaxDrawdownIndex].NumericValue);
        Assert.InRange(values[MaxDrawdownIndex].NumericValue!.Value, 33.3333333m, 33.3333334m);
        Assert.Equal("33.33%", values[MaxDrawdownIndex].FormattedText);
    }

    [Fact]
    public void Calculate_MaxDrawdown_UsesTheClosesNotTheLowsOrHighs()
    {
        // Bar 1 has a huge low and a huge high that must not matter.
        var candles = new[]
        {
            new CoreCandleData(MonWeek1, 100m, 100m, 100m, 100m, 1L),
            new CoreCandleData(MonWeek1.AddDays(1), 100m, 500m, 1m, 90m, 1L)
        };

        var values = CalculateWholeSeries(candles);

        Assert.Equal(10m, values[MaxDrawdownIndex].NumericValue);
        Assert.Equal("500.000", values[HighestHighIndex].FormattedText);
        Assert.Equal("1.000", values[LowestLowIndex].FormattedText);
    }

    [Fact]
    public void Calculate_Volatility_IsTheSampleStandardDeviationOfLogReturnsInPercentPerBar()
    {
        var values = CalculateWholeSeries(CandlesFromCloses(100m, 102m, 101m, 103m));

        AssertVolatility(1.7066, values[VolatilityIndex], "1.71%");
    }

    [Fact]
    public void Calculate_ConstantCloses_HaveZeroVolatilityAndZeroDrawdown()
    {
        var values = CalculateWholeSeries(CandlesFromCloses(50m, 50m, 50m, 50m));

        Assert.Equal(0m, values[VolatilityIndex].NumericValue);
        Assert.Equal("0.00%", values[VolatilityIndex].FormattedText);
        Assert.Equal(0m, values[MaxDrawdownIndex].NumericValue);
    }

    [Fact]
    public void Calculate_SingleBar_HasPricesAndZeroDrawdownButNoVolatility()
    {
        var candles = new[] { new CoreCandleData(MonWeek1, 100m, 110m, 100m, 105m, 1_000L) };

        var values = CalculateWholeSeries(candles);

        Assert.Equal(110m, values[HighestHighIndex].NumericValue);
        Assert.Equal(100m, values[LowestLowIndex].NumericValue);
        Assert.Equal(0m, values[MaxDrawdownIndex].NumericValue);
        Assert.Null(values[VolatilityIndex].NumericValue);
        Assert.Equal("-", values[VolatilityIndex].FormattedText);
    }

    [Fact]
    public void Calculate_TwoBars_HaveNoVolatilityButHaveDrawdown()
    {
        var values = CalculateWholeSeries(CandlesFromCloses(100m, 80m));

        Assert.Equal(20m, values[MaxDrawdownIndex].NumericValue);
        Assert.Null(values[VolatilityIndex].NumericValue);
        Assert.Equal("-", values[VolatilityIndex].FormattedText);
    }

    [Fact]
    public void Calculate_NoBarsInRange_LeavesAllFourNewValuesUndefined()
    {
        var candles = CreateTenBusinessDayCandles();
        var values = DatePriceRangeValues.Calculate(
            new DrawingAnchor(MonWeek1.AddDays(30), 100m),
            new DrawingAnchor(MonWeek1.AddDays(35), 110m),
            candles,
            IndicatorColor.Gray);

        Assert.Equal(10, values.Length);
        Assert.Equal(0m, values[2].NumericValue); // the existing values are unchanged
        for (int i = HighestHighIndex; i <= VolatilityIndex; i++)
        {
            Assert.Null(values[i].NumericValue);
            Assert.Equal("-", values[i].FormattedText);
        }

        var emptyValues = DatePriceRangeValues.Calculate(
            new DrawingAnchor(MonWeek1, 100m), new DrawingAnchor(MonWeek1.AddDays(1), 110m), Array.Empty<CoreCandleData>(), IndicatorColor.Gray);
        for (int i = HighestHighIndex; i <= VolatilityIndex; i++)
        {
            Assert.Null(emptyValues[i].NumericValue);
        }

        var nullValues = DatePriceRangeValues.Calculate(
            new DrawingAnchor(MonWeek1, 100m), new DrawingAnchor(MonWeek1.AddDays(1), 110m), null, IndicatorColor.Gray);
        Assert.Equal(10, nullValues.Length);
        Assert.Null(nullValues[VolatilityIndex].NumericValue);
    }

    [Fact]
    public void Calculate_ReverseDrag_GivesTheSameNewValues()
    {
        var candles = CandlesFromCloses(100m, 120m, 90m, 110m, 80m, 100m);
        var forward = DatePriceRangeValues.Calculate(
            new DrawingAnchor(candles[0].Timestamp, 100m), new DrawingAnchor(candles[^1].Timestamp, 130m), candles, BlueColor);
        var reverse = DatePriceRangeValues.Calculate(
            new DrawingAnchor(candles[^1].Timestamp, 130m), new DrawingAnchor(candles[0].Timestamp, 100m), candles, BlueColor);

        for (int i = HighestHighIndex; i <= VolatilityIndex; i++)
        {
            Assert.Equal(forward[i].NumericValue, reverse[i].NumericValue);
            Assert.Equal(forward[i].FormattedText, reverse[i].FormattedText);
        }
    }

    [Fact]
    public void Calculate_RangeEdges_AreInclusive_AndBarsOutsideTheRangeDoNotCount()
    {
        // Closes: 1000 (before) | 100, 110, 99, 105 (inside) | 1 (after).
        var candles = CandlesFromCloses(1000m, 100m, 110m, 99m, 105m, 1m);
        var inside = DatePriceRangeValues.Calculate(
            new DrawingAnchor(candles[1].Timestamp, 100m), new DrawingAnchor(candles[4].Timestamp, 100m), candles, BlueColor);
        var insideOnly = CalculateWholeSeries(CandlesFromCloses(100m, 110m, 99m, 105m));

        Assert.Equal(4m, inside[2].NumericValue);
        for (int i = HighestHighIndex; i <= VolatilityIndex; i++)
        {
            Assert.Equal(insideOnly[i].NumericValue, inside[i].NumericValue);
        }

        // The fall from the peak 110 to 99 is 10 %; the 1000 before the range and the 1 after it are not part of it.
        Assert.Equal(10m, inside[MaxDrawdownIndex].NumericValue);
        Assert.Equal(115m, inside[HighestHighIndex].NumericValue);
        Assert.Equal(94m, inside[LowestLowIndex].NumericValue);
    }

    [Fact]
    public void Calculate_NonPositiveClose_LeavesVolatilityUndefined_AndDrawdownOnlyWhenThePeakIsNotPositive()
    {
        // A close of 0 after a positive peak: the drawdown is 100 %, the log return is undefined.
        var zeroLater = CalculateWholeSeries(CandlesFromCloses(100m, 50m, 0m, 80m));
        Assert.Equal(100m, zeroLater[MaxDrawdownIndex].NumericValue);
        Assert.Null(zeroLater[VolatilityIndex].NumericValue);
        Assert.Equal("-", zeroLater[VolatilityIndex].FormattedText);

        // The peak (highest close so far) is 0 or less from the first bar: the drawdown is undefined.
        var negativeStart = CalculateWholeSeries(CandlesFromCloses(-10m, -20m, -5m));
        Assert.Null(negativeStart[MaxDrawdownIndex].NumericValue);
        Assert.Equal("-", negativeStart[MaxDrawdownIndex].FormattedText);
        Assert.Null(negativeStart[VolatilityIndex].NumericValue);
        Assert.Equal(0m, negativeStart[HighestHighIndex].NumericValue); // High = close + 5 in the helper, so the best bar is -5 + 5
    }

    [Fact]
    public void Calculate_LogReturnThatDoesNotFitADecimalRatio_LeavesVolatilityUndefinedWithoutThrowing()
    {
        var values = CalculateWholeSeries(CandlesFromCloses(0.0000000000000000000000000001m, 70_000_000_000_000_000_000_000_000_000m, 1m));

        Assert.Null(values[VolatilityIndex].NumericValue);
        Assert.Equal("-", values[VolatilityIndex].FormattedText);
    }

    [Fact]
    public void Calculate_TotalVolumeOverflow_StillCalculatesTheNewValues()
    {
        var candles = new[]
        {
            new CoreCandleData(MonWeek1, 100m, 105m, 95m, 100m, long.MaxValue),
            new CoreCandleData(MonWeek1.AddDays(1), 100m, 106m, 94m, 90m, 1L),
            new CoreCandleData(MonWeek1.AddDays(2), 90m, 99m, 88m, 95m, 1L)
        };

        var values = CalculateWholeSeries(candles);

        Assert.Null(values[4].NumericValue);
        Assert.Equal(106m, values[HighestHighIndex].NumericValue);
        Assert.Equal(88m, values[LowestLowIndex].NumericValue);
        Assert.Equal(10m, values[MaxDrawdownIndex].NumericValue);
        Assert.NotNull(values[VolatilityIndex].NumericValue);
    }

    [Fact]
    public void Calculate_NewValueTexts_UseFixedCultureRegardlessOfThreadCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            var values = CalculateWholeSeries(CandlesFromCloses(100m, 120m, 90m, 110m, 80m, 100m));

            Assert.Equal("125.000", values[HighestHighIndex].FormattedText);
            Assert.Equal("33.33%", values[MaxDrawdownIndex].FormattedText);
            Assert.Contains(".", values[VolatilityIndex].FormattedText);
            Assert.DoesNotContain(",", values[VolatilityIndex].FormattedText);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
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
