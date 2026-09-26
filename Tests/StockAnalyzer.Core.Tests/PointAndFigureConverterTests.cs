using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests;

/// <summary>
/// Unit tests for PointAndFigureConverter.
/// Each P&F column is represented as a CoreCandleData where:
///   - Up column (X): Open = column bottom, Close = column top, IsBullish = true
///   - Down column (O): Open = column top, Close = column bottom, IsBullish = false
///   - High/Low mirror Open/Close for consistency
/// </summary>
public class PointAndFigureConverterTests
{
    private static DateTime BaseDate => new(2025, 1, 1);

    private static CoreCandleData MakeCandle(int dayOffset, decimal open, decimal high, decimal low, decimal close)
        => new(BaseDate.AddDays(dayOffset), open, high, low, close, 1000);

    // ── Empty / Minimal input ────────────────────────────

    [Fact]
    public void Convert_NullInput_ReturnsEmpty()
    {
        var result = PointAndFigureConverter.Convert(null!, 1m, 3);
        Assert.Empty(result);
    }

    [Fact]
    public void Convert_EmptyInput_ReturnsEmpty()
    {
        var result = PointAndFigureConverter.Convert(Array.Empty<CoreCandleData>(), 1m, 3);
        Assert.Empty(result);
    }

    [Fact]
    public void Convert_SingleCandle_ReturnsEmpty()
    {
        var candles = new[] { MakeCandle(0, 100, 105, 95, 100) };
        var result = PointAndFigureConverter.Convert(candles, 1m, 3);
        // A single candle cannot form a column direction
        Assert.Empty(result);
    }

    // ── Initial direction detection ─────────────────────

    [Fact]
    public void Convert_FirstMoveUp_CreatesUpColumn()
    {
        // Box=1, Reversal=3. First candle sets baseline.
        // Second candle high triggers up move (>= baseline + boxSize).
        var candles = new[]
        {
            MakeCandle(0, 100, 100, 100, 100),
            MakeCandle(1, 100, 103, 99, 102),
        };
        var result = PointAndFigureConverter.Convert(candles, 1m, 3);
        Assert.NotEmpty(result);
        // First column should be up (bullish)
        Assert.True(result[0].IsBullish, "First column should be Up (X)");
    }

    [Fact]
    public void Convert_FirstMoveDown_CreatesDownColumn()
    {
        var candles = new[]
        {
            MakeCandle(0, 100, 100, 100, 100),
            MakeCandle(1, 98, 99, 97, 98),
        };
        var result = PointAndFigureConverter.Convert(candles, 1m, 3);
        Assert.NotEmpty(result);
        // First column should be down (bearish)
        Assert.False(result[0].IsBullish, "First column should be Down (O)");
    }

    // ── Up column extension ─────────────────────────────

    [Fact]
    public void Convert_UpColumnExtends_WhenHighExceedsBoxSize()
    {
        // Steady uptrend: each bar pushes high by more than boxSize
        var candles = new[]
        {
            MakeCandle(0, 100, 100, 100, 100),
            MakeCandle(1, 101, 102, 100, 101),
            MakeCandle(2, 102, 104, 101, 103),
            MakeCandle(3, 103, 106, 102, 105),
        };
        var result = PointAndFigureConverter.Convert(candles, 1m, 3);
        // Should still be a single up column that keeps extending
        Assert.Single(result);
        Assert.True(result[0].IsBullish);
        // Column top should reflect the highest quantized price
        Assert.True(result[0].Close >= 105m, "Column top should reach at least 105");
    }

    // ── Reversal from Up to Down ────────────────────────

    [Fact]
    public void Convert_ReversalFromUp_CreatesNewDownColumn()
    {
        // Box=1, Reversal=3. Up column reaches ~105, then drops by 3+ boxes.
        var candles = new[]
        {
            MakeCandle(0, 100, 100, 100, 100),
            MakeCandle(1, 101, 105, 100, 104),  // Up column to 105
            MakeCandle(2, 103, 103, 101, 101),   // Drop but not enough (only 4, need reversal from close)
            MakeCandle(3, 100, 100, 97, 98),     // Big drop — should reverse
        };
        var result = PointAndFigureConverter.Convert(candles, 1m, 3);
        Assert.True(result.Count >= 2, $"Expected at least 2 columns (up+down), got {result.Count}");
        Assert.True(result[0].IsBullish, "First column = Up");
        Assert.False(result[1].IsBullish, "Second column = Down (reversal)");
    }

    // ── Reversal from Down to Up ────────────────────────

    [Fact]
    public void Convert_ReversalFromDown_CreatesNewUpColumn()
    {
        // First move is down, then reverse up.
        var candles = new[]
        {
            MakeCandle(0, 100, 100, 100, 100),
            MakeCandle(1, 98, 99, 95, 96),   // Down column to ~95
            MakeCandle(2, 96, 96, 94, 95),   // Extend down a bit
            MakeCandle(3, 97, 100, 96, 99),  // Up reversal (>= bottom + 3)
        };
        var result = PointAndFigureConverter.Convert(candles, 1m, 3);
        Assert.True(result.Count >= 2, $"Expected at least 2 columns, got {result.Count}");
        Assert.False(result[0].IsBullish, "First column = Down");
        Assert.True(result[1].IsBullish, "Second column = Up (reversal)");
    }

    // ── Multi-column scenario ───────────────────────────

    [Fact]
    public void Convert_MultipleReversals_CreatesCorrectColumnCount()
    {
        // Box=2, Reversal=3. Create up → down → up pattern.
        // Reversal threshold = 2 * 3 = 6
        var candles = new[]
        {
            MakeCandle(0,  100, 100, 100, 100),
            MakeCandle(1,  102, 110, 100, 108),  // Up column: top ~110
            MakeCandle(2,  105, 106, 103, 104),  // No reversal yet (drop from 110 is 7, needs >= 6 from column close)
            MakeCandle(3,  102, 102, 98,  99),   // Reversal down: drop >= 6 from column top
            MakeCandle(4,  99,  99,  90,  92),   // Extend down column
            MakeCandle(5,  94,  100, 93,  99),   // Up reversal: rise >= 6 from column bottom
            MakeCandle(6,  100, 112, 99,  110),  // Extend up
        };
        var result = PointAndFigureConverter.Convert(candles, 2m, 3);
        // Expect at least 3 columns: Up → Down → Up
        Assert.True(result.Count >= 3, $"Expected at least 3 columns, got {result.Count}");
        Assert.True(result[0].IsBullish, "Col 0 = Up");
        Assert.False(result[1].IsBullish, "Col 1 = Down");
        Assert.True(result[2].IsBullish, "Col 2 = Up");
    }

    // ── Column non-overlap rule ─────────────────────────

    [Fact]
    public void Convert_NewColumn_DoesNotOverlapPrevious()
    {
        // Standard P&F rule: new column starts 1 box away from previous column tip
        var candles = new[]
        {
            MakeCandle(0, 100, 100, 100, 100),
            MakeCandle(1, 101, 108, 100, 107),  // Up to 108
            MakeCandle(2, 103, 103, 100, 101),  // Reversal down
            MakeCandle(3, 99,  99,  95,  96),
        };
        var result = PointAndFigureConverter.Convert(candles, 1m, 3);
        if (result.Count >= 2)
        {
            var upCol = result[0];
            var downCol = result[1];

            // Down column's top (Open for bearish) should be below up column's top (Close for bullish)
            decimal upTop = upCol.Close;
            decimal downTop = downCol.Open;
            Assert.True(downTop < upTop,
                $"Down column top ({downTop}) should be below up column top ({upTop})");
        }
    }

    // ── Box quantization ────────────────────────────────

    [Fact]
    public void Convert_QuantizesToBoxBoundaries()
    {
        // With box=5, prices should snap to multiples of 5
        var candles = new[]
        {
            MakeCandle(0, 102, 102, 102, 102),
            MakeCandle(1, 105, 117, 103, 115),
        };
        var result = PointAndFigureConverter.Convert(candles, 5m, 3);
        Assert.NotEmpty(result);
        var col = result[0];
        // Close (top) should be quantized to box boundary
        Assert.Equal(0m, col.Close % 5m);
    }
}
