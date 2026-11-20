using System;
using System.Collections.Generic;
using StockAnalyzer.Core;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Utilities;
using Xunit;

namespace StockAnalyzer.Core.Tests.BlockChart;

/// <summary>
/// T0 of the block-chart overlay plan (finding F-24): <see cref="AutoBoxSizeCalculator.Calculate(ChartSizingMode, System.ReadOnlySpan{CoreCandleData}, decimal, int, decimal, ChartRoundingMode, AutoFallbackMode, decimal, decimal)"/>
/// never returns a non-positive size (it substitutes <see cref="ChartConstants.DefaultBoxSize"/>), so a size that the rounding post-processing floors to zero is invisible to its callers.
/// <see cref="AutoBoxSizeCalculator.TryCalculateStrict"/> reports it. Boundary between the two zeros (owner supplement 2): an ATR of exactly 0 is NOT a failure (the existing fallback gives a
/// positive size); only a size that the rounding post-processing crushes to 0 is.
/// </summary>
public class AutoBoxSizeStrictTests
{
    // Price 20000 -> EstimateTickSize = 10, so any size below 5 is quantized to 0 by Floor / Round / TickSize.
    private const decimal HighPrice = 20000m;
    private const decimal TinyFixedSize = 0.4m;

    private static CoreCandleData Bar(int day, decimal close, decimal range)
        => new(new DateTime(2024, 1, 1).AddDays(day), close, close + range, close - range, close, 1000);

    private static CoreCandleData[] Series(int count, decimal start, decimal step, decimal range)
    {
        var list = new CoreCandleData[count];
        for (int i = 0; i < count; i++) list[i] = Bar(i, start + i * step, range);
        return list;
    }

    [Theory]
    [InlineData(ChartRoundingMode.Floor)]
    [InlineData(ChartRoundingMode.Round)]
    [InlineData(ChartRoundingMode.TickSize)]
    public void Calculate_ReplacesAZeroSizeWithTheDefaultBoxSize_AndStrictReportsIt(ChartRoundingMode rounding)
    {
        var candles = Series(20, HighPrice, 1m, 1m);

        decimal viaCalculate = AutoBoxSizeCalculator.Calculate(
            ChartSizingMode.Fixed, candles.AsSpan(), TinyFixedSize, 14, 1m, rounding, AutoFallbackMode.Fixed, HighPrice, 1m);
        bool ok = AutoBoxSizeCalculator.TryCalculateStrict(
            ChartSizingMode.Fixed, candles.AsSpan(), TinyFixedSize, 14, 1m, rounding, AutoFallbackMode.Fixed, HighPrice, 1m, out decimal strict);

        Assert.Equal(ChartConstants.DefaultBoxSize, viaCalculate); // the silent substitution (existing behaviour, unchanged)
        Assert.False(ok);
        Assert.Equal(0m, strict);
    }

    [Fact]
    public void Strict_NeverSucceedsForAFixedSizeThatEveryBarRoundsToZero_SoTheCallerCanFinishQuietly()
    {
        // Contract of owner supplement 1: a setting such as "fixed 0.4 + floor" fails on EVERY prefix; the caller scans once and ends with "never anchored".
        var candles = Series(300, HighPrice, 3m, 1m);

        for (int k = 0; k < candles.Length; k++)
        {
            bool ok = AutoBoxSizeCalculator.TryCalculateStrict(
                ChartSizingMode.Fixed, candles.AsSpan(0, k + 1), TinyFixedSize, 14, 1m, ChartRoundingMode.Floor, AutoFallbackMode.Fixed, candles[k].Close, 1m, out decimal size);
            Assert.False(ok);
            Assert.Equal(0m, size);
        }
    }

    [Fact]
    public void Strict_SucceedsOnceThePriceFallsIntoAFinerTickBand()
    {
        // Fixed 0.4 + Floor: at price 20000 the tick is 10 (size -> 0, fails); at price 90 the tick is 0.05 (size 0.4 survives).
        var high = Series(20, HighPrice, 0m, 1m);
        var low = Series(20, 90m, 0m, 1m);

        Assert.False(AutoBoxSizeCalculator.TryCalculateStrict(ChartSizingMode.Fixed, high.AsSpan(), TinyFixedSize, 14, 1m, ChartRoundingMode.Floor, AutoFallbackMode.Fixed, 20000m, 1m, out _));
        Assert.True(AutoBoxSizeCalculator.TryCalculateStrict(ChartSizingMode.Fixed, low.AsSpan(), TinyFixedSize, 14, 1m, ChartRoundingMode.Floor, AutoFallbackMode.Fixed, 90m, 1m, out decimal size));
        Assert.Equal(0.4m, size);
    }

    [Fact]
    public void Strict_AtrOfExactlyZero_IsNotAFailure_TheExistingFallbackGivesAPositiveSize()
    {
        // Flat series: every true range is 0, so ATR == 0. ResolveAtrSize falls back (Percentage: price * DefaultFallbackPercentage; Fixed: the fixed size), both > 0.
        var flat = Series(30, 100m, 0m, 0m);

        bool okPercentageFallback = AutoBoxSizeCalculator.TryCalculateStrict(
            ChartSizingMode.AutoAtr, flat.AsSpan(), 1m, 14, 1m, ChartRoundingMode.None, AutoFallbackMode.Percentage, 100m, 1m, out decimal viaPercentage);
        bool okFixedFallback = AutoBoxSizeCalculator.TryCalculateStrict(
            ChartSizingMode.AutoAtr, flat.AsSpan(), 2.5m, 14, 1m, ChartRoundingMode.None, AutoFallbackMode.Fixed, 100m, 1m, out decimal viaFixed);

        Assert.True(okPercentageFallback);
        Assert.Equal(100m * ChartConstants.DefaultFallbackPercentage, viaPercentage);
        Assert.True(okFixedFallback);
        Assert.Equal(2.5m, viaFixed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Strict_ReturnsFalse_ForANonPositivePrice(int price)
    {
        var candles = Series(20, 100m, 1m, 1m);

        bool ok = AutoBoxSizeCalculator.TryCalculateStrict(
            ChartSizingMode.Fixed, candles.AsSpan(), 5m, 14, 1m, ChartRoundingMode.None, AutoFallbackMode.Fixed, price, 1m, out decimal size);

        Assert.False(ok);
        Assert.Equal(0m, size);
    }

    [Fact]
    public void Strict_EqualsCalculate_WheneverItSucceeds_AcrossAllModesRoundingsAndPrices()
    {
        var modes = new[] { ChartSizingMode.Fixed, ChartSizingMode.Percentage, ChartSizingMode.AutoAtr };
        var roundings = (ChartRoundingMode[])Enum.GetValues(typeof(ChartRoundingMode));
        var fallbacks = new[] { AutoFallbackMode.Percentage, AutoFallbackMode.Fixed };
        var prices = new[] { 0.5m, 5m, 50m, 99.9m, 100m, 500m, 1000m, 3000m, 10000m, 20000m };
        var fixedSizes = new[] { 0.004m, 0.4m, 1m, 7.3m, 250m };
        var percentages = new[] { 0.01m, 1m, 25m };
        int compared = 0, rejected = 0;

        foreach (var price in prices)
        foreach (var mode in modes)
        foreach (var rounding in roundings)
        foreach (var fallback in fallbacks)
        foreach (var fixedSize in fixedSizes)
        foreach (var percentage in percentages)
        {
            // 40 bars with a real range: enough for the 14-period ATR; the last close is the price under test.
            var candles = Series(40, price, 0m, price / 50m);
            decimal expected = AutoBoxSizeCalculator.Calculate(mode, candles.AsSpan(), fixedSize, 14, 1.5m, rounding, fallback, price, percentage);
            bool ok = AutoBoxSizeCalculator.TryCalculateStrict(mode, candles.AsSpan(), fixedSize, 14, 1.5m, rounding, fallback, price, percentage, out decimal strict);

            if (ok)
            {
                Assert.Equal(expected, strict);
                Assert.True(strict > 0m);
                compared++;
            }
            else
            {
                // The only reason for a failure is the substitution: Calculate answered with the default although the real size was not positive.
                Assert.Equal(0m, strict);
                Assert.Equal(ChartConstants.DefaultBoxSize, expected);
                rejected++;
            }
        }

        Assert.True(compared > 1000, "the grid must exercise many successes");
        Assert.True(rejected > 0, "the grid must exercise at least one rejected size");
    }
}
