using System;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.MathUtils;

/// <summary>
/// RangeRiskMath is called on every recalculation of a Date/Price Range, so a corrupt price must never make it throw:
/// a statistic that cannot be represented is null instead.
/// </summary>
public sealed class RangeRiskMathTests
{
    private static readonly DateTime Start = new(2026, 1, 5, 9, 0, 0);

    private static CoreCandleData Bar(int day, decimal high, decimal low, decimal close)
        => new(Start.AddDays(day), close, high, low, close, 1_000L);

    [Fact]
    public void Compute_DecimalMaxAndMinValueCloses_DoesNotThrow_AndLeavesTheOverflowingStatisticsNull()
    {
        var candles = new[]
        {
            Bar(0, decimal.MaxValue, decimal.MinValue, decimal.MaxValue),
            Bar(1, decimal.MaxValue, decimal.MinValue, decimal.MinValue),
            Bar(2, decimal.MaxValue, decimal.MinValue, decimal.MaxValue)
        };

        RangeRiskStats? stats = null;
        var thrown = Record.Exception(() => stats = RangeRiskMath.Compute(candles, 0, 2));

        Assert.Null(thrown);
        Assert.NotNull(stats);
        Assert.Equal(decimal.MaxValue, stats!.Value.HighestHigh);
        Assert.Equal(decimal.MinValue, stats.Value.LowestLow);
        Assert.Null(stats.Value.MaxDrawdownPercent); // peak - close does not fit a decimal
        Assert.Null(stats.Value.VolatilityPercent); // a close of 0 or less
    }

    [Fact]
    public void Compute_ExtremePositiveCloses_DoesNotThrow()
    {
        // A fall from the largest to a tiny positive close: the ratio (peak - close) / peak * 100 is fine,
        // the log return ratio of the tiny close to the largest one underflows to 0 (not finite after ln).
        var candles = new[]
        {
            Bar(0, decimal.MaxValue, 0.0000000000000000000000000001m, decimal.MaxValue),
            Bar(1, decimal.MaxValue, 0.0000000000000000000000000001m, 0.0000000000000000000000000001m),
            Bar(2, decimal.MaxValue, 0.0000000000000000000000000001m, decimal.MaxValue)
        };

        RangeRiskStats? stats = null;
        var thrown = Record.Exception(() => stats = RangeRiskMath.Compute(candles, 0, 2));

        Assert.Null(thrown);
        Assert.NotNull(stats);
        Assert.NotNull(stats!.Value.MaxDrawdownPercent);
        Assert.Null(stats.Value.VolatilityPercent);
    }

    [Fact]
    public void Calculate_WithExtremeDecimalCloses_DoesNotThrow_AndKeepsTheOtherValues()
    {
        var candles = new[]
        {
            Bar(0, decimal.MaxValue, decimal.MinValue, decimal.MaxValue),
            Bar(1, decimal.MaxValue, decimal.MinValue, decimal.MinValue)
        };
        var p1 = new DrawingAnchor(Start, 100m);
        var p2 = new DrawingAnchor(Start.AddDays(1), 100m);

        DrawingCalculatedValue[]? values = null;
        var thrown = Record.Exception(() => values = DatePriceRangeValues.Calculate(p1, p2, candles, IndicatorColor.Gray));

        Assert.Null(thrown);
        Assert.NotNull(values);
        Assert.Equal(DatePriceRangeValues.ValueCount, values!.Length);
        Assert.Equal(2m, values[2].NumericValue); // BarCount is unchanged
        Assert.Null(values[DatePriceRangeValues.MaxDrawdownIndex].NumericValue);
        Assert.Equal("-", values[DatePriceRangeValues.MaxDrawdownIndex].FormattedText);
        Assert.Equal(decimal.MaxValue, values[DatePriceRangeValues.HighestHighIndex].NumericValue);
    }
}
