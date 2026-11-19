using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Utilities;
using Xunit;

namespace StockAnalyzer.Core.Tests;

/// <summary>
/// The Reversal / Update price of a Point and Figure column and the Reversal price of a Kagi column (the Data tab rows). The expected values are the
/// definitions of the implementation plan, computed by hand from the reversal condition of the calculators.
/// </summary>
public class SpecialChartLevelsTests
{
    private static DateTime Day(int offset) => new DateTime(2026, 1, 1).AddDays(offset);

    // A Point and Figure chart is given column by column as (bottom, top, isUp): an X column has Open = bottom and Close = top, an O column Open = top and Close = bottom.
    private static (decimal[] Opens, decimal[] Highs, decimal[] Lows, decimal[] Closes) Columns(params (decimal Bottom, decimal Top, bool IsUp)[] columns)
    {
        var opens = new decimal[columns.Length];
        var highs = new decimal[columns.Length];
        var lows = new decimal[columns.Length];
        var closes = new decimal[columns.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            opens[i] = columns[i].IsUp ? columns[i].Bottom : columns[i].Top;
            closes[i] = columns[i].IsUp ? columns[i].Top : columns[i].Bottom;
            highs[i] = columns[i].Top;
            lows[i] = columns[i].Bottom;
        }

        return (opens, highs, lows, closes);
    }

    private static bool Describe(
        (decimal[] Opens, decimal[] Highs, decimal[] Lows, decimal[] Closes) c, int index, decimal box, int reversal, out decimal reversalPrice, out decimal updatePrice)
        => PointAndFigureLevels.TryDescribe(c.Opens, c.Highs, c.Lows, c.Closes, index, box, reversal, out reversalPrice, out updatePrice);

    // ---- Point and Figure ----

    [Fact]
    public void PointAndFigure_XColumn_ReversalIsTopMinusReversalBoxes_UpdateIsTopPlusOneBox()
    {
        var c = Columns((100m, 108m, true));

        Assert.True(Describe(c, 0, 2m, 3, out var reversal, out var update));
        Assert.Equal(102m, reversal);   // 108 - 3 x 2
        Assert.Equal(110m, update);     // 108 + 2
    }

    [Fact]
    public void PointAndFigure_OColumn_ReversalIsBottomPlusReversalBoxes_UpdateIsBottomMinusOneBox()
    {
        var c = Columns((100m, 108m, false));

        Assert.True(Describe(c, 0, 2m, 3, out var reversal, out var update));
        Assert.Equal(106m, reversal);   // 100 + 3 x 2
        Assert.Equal(98m, update);      // 100 - 2
    }

    [Theory]
    [InlineData(0, 102, 110)]   // X: 100..108
    [InlineData(1, 100, 92)]   // O: 94..106 (alternating after the X column): 94 + 3 x 2, 94 - 2
    public void PointAndFigure_AlternatingColumns_UseEachColumnOwnExtremes(int index, int expectedReversal, int expectedUpdate)
    {
        var c = Columns((100m, 108m, true), (94m, 106m, false));

        Assert.True(Describe(c, index, 2m, 3, out var reversal, out var update));
        Assert.Equal((decimal)expectedReversal, reversal);
        Assert.Equal((decimal)expectedUpdate, update);
    }

    [Fact]
    public void PointAndFigure_FlatColumnAfterAnXColumn_IsAnOColumn()
    {
        // Reversal of one box: a column of one box has Open == Close. The column after an X column is an O column.
        var c = Columns((100m, 110m, true), (108m, 108m, false));

        Assert.True(Describe(c, 1, 2m, 1, out var reversal, out var update));
        Assert.Equal(110m, reversal);   // O: bottom 108 + 1 x 2
        Assert.Equal(106m, update);     // O: bottom 108 - 2
    }

    [Fact]
    public void PointAndFigure_FlatColumnAfterAnOColumn_IsAnXColumn()
    {
        var c = Columns((100m, 110m, false), (102m, 102m, true));

        Assert.True(Describe(c, 1, 2m, 1, out var reversal, out var update));
        Assert.Equal(100m, reversal);   // X: top 102 - 1 x 2
        Assert.Equal(104m, update);     // X: top 102 + 2
    }

    [Fact]
    public void PointAndFigure_RunOfFlatColumns_FollowsTheAlternationOfTheNearestColumnWithADirection()
    {
        // X (100..104), then three flat columns: O, X, O.
        var c = Columns((100m, 104m, true), (102m, 102m, false), (104m, 104m, true), (102m, 102m, false));

        Assert.True(Describe(c, 1, 2m, 1, out var r1, out var u1));
        Assert.Equal((104m, 100m), (r1, u1));   // O at 102: 102 + 2, 102 - 2
        Assert.True(Describe(c, 2, 2m, 1, out var r2, out var u2));
        Assert.Equal((102m, 106m), (r2, u2));   // X at 104: 104 - 2, 104 + 2
        Assert.True(Describe(c, 3, 2m, 1, out var r3, out var u3));
        Assert.Equal((104m, 100m), (r3, u3));   // O at 102
    }

    [Fact]
    public void PointAndFigure_FlatFirstColumn_WithoutAnyDirection_IsAnXColumn()
    {
        var c = Columns((100m, 100m, true));

        Assert.True(Describe(c, 0, 2m, 1, out var reversal, out var update));
        Assert.Equal(98m, reversal);
        Assert.Equal(102m, update);
    }

    [Fact]
    public void PointAndFigure_ResultBelowZero_IsNotClamped()
    {
        var c = Columns((1m, 3m, true));

        Assert.True(Describe(c, 0, 5m, 3, out var reversal, out var update));
        Assert.Equal(-12m, reversal);   // 3 - 3 x 5
        Assert.Equal(8m, update);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void PointAndFigure_IndexOutsideTheColumns_IsNotDescribed(int index)
    {
        var c = Columns((100m, 108m, true));

        Assert.False(Describe(c, index, 2m, 3, out var reversal, out var update));
        Assert.Equal(0m, reversal);
        Assert.Equal(0m, update);
    }

    [Theory]
    [InlineData(0, 3)]       // no box size
    [InlineData(-2, 3)]      // negative box size
    [InlineData(2, 0)]       // fewer boxes than one
    [InlineData(2, -1)]
    public void PointAndFigure_BoxSizeOrReversalOutOfRange_IsNotDescribed(int box, int reversalAmount)
    {
        var c = Columns((100m, 108m, true));

        Assert.False(Describe(c, 0, box, reversalAmount, out _, out _));
    }

    [Fact]
    public void PointAndFigure_EmptyColumns_AreNotDescribed()
    {
        Assert.False(PointAndFigureLevels.TryDescribe(ReadOnlySpan<decimal>.Empty, ReadOnlySpan<decimal>.Empty, ReadOnlySpan<decimal>.Empty, ReadOnlySpan<decimal>.Empty, 0, 2m, 3, out _, out _));
    }

    [Fact]
    public void PointAndFigure_SpansOfDifferentLength_AreNotDescribed()
    {
        var c = Columns((100m, 108m, true), (94m, 106m, false));

        Assert.False(PointAndFigureLevels.TryDescribe(c.Opens, c.Highs, c.Lows, new decimal[] { 108m }, 0, 2m, 3, out _, out _));
    }

    [Fact]
    public void PointAndFigure_OnTheColumnsOfTheCalculator_XAndOColumnsAlternate_AndAFlatColumnIsDescribedLikeAnyOther()
    {
        // Box 1, reversal of one box: every column after the first has one box. Columns: X 100..101, O 100, X 101, O 100.
        var source = new[]
        {
            new CoreCandleData(Day(0), 100m, 100m, 100m, 100m, 1000),
            new CoreCandleData(Day(1), 100m, 101.2m, 100.1m, 101.1m, 1000),
            new CoreCandleData(Day(2), 100.5m, 100.5m, 99.5m, 100m, 1000),
            new CoreCandleData(Day(3), 100m, 101.2m, 100.3m, 101.1m, 1000),
            new CoreCandleData(Day(4), 100.5m, 100.9m, 99.7m, 100m, 1000),
        };
        var columns = PointAndFigureConverter.Convert(source, 1m, 1, ChartRoundingMode.None);

        Assert.Equal(4, columns.Count);
        var opens = new decimal[columns.Count];
        var highs = new decimal[columns.Count];
        var lows = new decimal[columns.Count];
        var closes = new decimal[columns.Count];
        for (int i = 0; i < columns.Count; i++)
        {
            opens[i] = columns[i].Open;
            highs[i] = columns[i].High;
            lows[i] = columns[i].Low;
            closes[i] = columns[i].Close;
        }

        Assert.Equal(opens[1], closes[1]);   // the column after the first has one box (the case that Close >= Open cannot classify)

        var expected = new[] { (100m, 102m), (101m, 99m), (100m, 102m), (101m, 99m) };   // (reversal, update): X, O, X, O
        for (int i = 0; i < columns.Count; i++)
        {
            Assert.True(PointAndFigureLevels.TryDescribe(opens, highs, lows, closes, i, 1m, 1, out var reversal, out var update));
            Assert.Equal(expected[i], (reversal, update));
        }
    }

    [Fact]
    public void PointAndFigure_ReversalPrice_IsTheBoundaryOfTheCalculatorReversalCondition()
    {
        // The X column 100..108 (box 2, three boxes). A low of exactly the Reversal price (102) reverses it; a low one tick above does not.
        decimal box = 2m;
        int reversalBoxes = 3;
        var c = Columns((100m, 108m, true));
        Assert.True(Describe(c, 0, box, reversalBoxes, out var reversalPrice, out _));

        int ReversedColumns(decimal low)
        {
            var source = new[]
            {
                new CoreCandleData(Day(0), 100m, 100m, 100m, 100m, 1000),
                new CoreCandleData(Day(1), 100m, 108m, 100m, 108m, 1000),
                new CoreCandleData(Day(2), 108m, 108m, low, low, 1000),
            };
            return PointAndFigureConverter.Convert(source, box, reversalBoxes, ChartRoundingMode.None).Count;
        }

        Assert.Equal(2, ReversedColumns(reversalPrice));            // reversed: an O column follows
        Assert.Equal(1, ReversedColumns(reversalPrice + 0.001m));   // not reversed
    }

    // ---- Kagi ----

    // Segments: up 100->110, up 110->120 (one column), down 120->105, down 105->95 (one column), up 95->130.
    private static List<CoreCandleData> KagiSegments() => new()
    {
        new(Day(0), 100m, 110m, 100m, 110m, 0), new(Day(1), 110m, 120m, 110m, 120m, 1),
        new(Day(2), 120m, 120m, 105m, 105m, 0), new(Day(3), 105m, 105m, 95m, 95m, 0),
        new(Day(4), 95m, 130m, 95m, 130m, 1),
    };

    [Theory]
    [InlineData(0, 110)]    // first column, first segment: high 120 - 10
    [InlineData(1, 110)]    // the same column, last segment
    [InlineData(2, 105)]    // second column: low 95 + 10
    [InlineData(3, 105)]
    [InlineData(4, 120)]    // newest column: high 130 - 10
    public void Kagi_ReversalPrice_IsTheColumnExtremeMovedBackByTheThreshold(int segment, int expected)
    {
        Assert.True(KagiLevels.TryGetReversalPrice(KagiSegments(), segment, 10m, out var reversal));
        Assert.Equal((decimal)expected, reversal);
    }

    [Fact]
    public void Kagi_FlatAnchorColumn_HasNoReversalPrice()
    {
        var segments = new List<CoreCandleData>
        {
            new(Day(0), 100m, 100m, 100m, 100m, 0),   // the anchor at the starting close
            new(Day(1), 100m, 112m, 100m, 112m, 1),
        };

        Assert.False(KagiLevels.TryGetReversalPrice(segments, 0, 10m, out var anchor));
        Assert.Equal(0m, anchor);
        Assert.True(KagiLevels.TryGetReversalPrice(segments, 1, 10m, out var first));
        Assert.Equal(102m, first);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Kagi_ThresholdNotPositive_HasNoReversalPrice(int threshold)
    {
        Assert.False(KagiLevels.TryGetReversalPrice(KagiSegments(), 0, threshold, out _));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public void Kagi_IndexOutsideTheSegments_HasNoReversalPrice(int index)
    {
        Assert.False(KagiLevels.TryGetReversalPrice(KagiSegments(), index, 10m, out _));
    }

    [Fact]
    public void Kagi_NoSegments_HaveNoReversalPrice()
    {
        Assert.False(KagiLevels.TryGetReversalPrice(null, 0, 10m, out _));
        Assert.False(KagiLevels.TryGetReversalPrice(new List<CoreCandleData>(), 0, 10m, out _));
    }

    [Fact]
    public void Kagi_ReversalPrice_IsTheBoundaryOfTheCalculatorReversalCondition()
    {
        // Closes 100, 120 (a column up to 120), then a close of exactly 120 - 10 reverses it, a close one tick above does not (the condition is inclusive).
        decimal threshold = 10m;

        int CountColumns(decimal lastClose)
        {
            var source = new[]
            {
                new CoreCandleData(Day(0), 100m, 100m, 100m, 100m, 1000),
                new CoreCandleData(Day(1), 100m, 120m, 100m, 120m, 1000),
                new CoreCandleData(Day(2), 120m, 120m, lastClose, lastClose, 1000),
            };
            var segments = KagiConverter.Convert(source, threshold);
            var drawn = new List<CoreCandleData>();
            foreach (var s in segments) drawn.Add(new CoreCandleData(s.Timestamp, s.Open, s.High, s.Low, s.Close, 0));
            var (_, end) = KagiColumns.ColumnBounds(drawn, drawn.Count - 1);
            int columns = 0;
            for (int i = 0; i < drawn.Count; i++) if (i == 0 || KagiColumns.StartsNewColumn(drawn, i)) columns++;
            Assert.Equal(drawn.Count - 1, end);
            return columns;
        }

        var upColumn = new List<CoreCandleData> { new(Day(0), 100m, 120m, 100m, 120m, 0) };
        Assert.True(KagiLevels.TryGetReversalPrice(upColumn, 0, threshold, out var reversalPrice));
        Assert.Equal(110m, reversalPrice);

        int reversed = CountColumns(reversalPrice);
        int notReversed = CountColumns(reversalPrice + 0.001m);
        Assert.Equal(notReversed + 1, reversed);
    }
}
