#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Utilities;

namespace StockAnalyzer.Core.Tests;

public class ThreeLineBreakConverterTests
{
    private static List<CoreCandleData> GenerateCandles(params decimal[] closes)
    {
        var result = new List<CoreCandleData>();
        var date = new DateTime(2020, 1, 1);
        foreach (var c in closes)
        {
            result.Add(new CoreCandleData(
                date,
                c, // Open (dummy, since standard ThreeLineBreak uses Close)
                c + 1, // High
                c - 1, // Low
                c, // Close
                1000 // Volume
            ));
            date = date.AddDays(1);
        }
        return result;
    }

    [Fact]
    public void Convert_LineCount3_ReversesCorrectly()
    {
        // LineCount 3
        // Sequence: 10, 11, 12, 13 (Up Trend 4 lines) -> High is 13, Lows are 10,11,12
        // To reverse down, needs to break lowest of last 3 lines (10)
        // 10.5 (No Block) -> 9.5 (Reverse Down)
        
        var candles = GenerateCandles(10, 11, 12, 13, 10.5m, 9.5m);
        var blocks = ThreeLineBreakConverter.Convert(candles, lineCount: 3);

        // Blocks:
        // 1: 10 -> 11 (Up)
        // 2: 11 -> 12 (Up)
        // 3: 12 -> 13 (Up)
        // 4: 13 -> 9.5 (Down)
        
        Assert.Equal(4, blocks.Length);
        Assert.True(blocks[0].IsUp);
        Assert.True(blocks[1].IsUp);
        Assert.True(blocks[2].IsUp);
        Assert.False(blocks[3].IsUp);
        
        Assert.Equal(13, blocks[2].ClosePrice);
        Assert.Equal(9.5m, blocks[3].ClosePrice);
    }

    [Fact]
    public void Convert_LineCount2_ReversesCorrectly()
    {
        // LineCount 2
        // Sequence: 10, 11, 12, 13 (Up Trend) -> High is 13, Lows of last 2 are 11, 12
        // To reverse down, needs to break lowest of last 2 lines (11)
        // 10.5 breaks 11, so it should reverse down immediately!
        
        var candles = GenerateCandles(10, 11, 12, 13, 10.5m, 9.5m);
        var blocks = ThreeLineBreakConverter.Convert(candles, lineCount: 2);

        // Blocks:
        // 1: 10 -> 11 (Up)
        // 2: 11 -> 12 (Up)
        // 3: 12 -> 13 (Up)
        // 4: 13 -> 10.5 (Down) - Because lineCount is 2! (Lowest of last 2 is 11)
        // 5: 10.5 -> 9.5 (Down)
        
        Assert.Equal(5, blocks.Length);
        Assert.True(blocks[2].IsUp);
        Assert.False(blocks[3].IsUp);
        Assert.Equal(10.5m, blocks[3].ClosePrice);
        Assert.False(blocks[4].IsUp);
        Assert.Equal(9.5m, blocks[4].ClosePrice);
    }

    [Fact]
    public void Convert_LineCount4_ReversesCorrectly()
    {
        // LineCount 4
        // Sequence: 10, 11, 12, 13, 14 (Up Trend)
        // To reverse down, break lowest of last 4 (which is 10)
        // 10.5 doesn't break, 9.5 breaks 10.
        
        var candles = GenerateCandles(10, 11, 12, 13, 14, 10.5m, 9.5m);
        var blocks = ThreeLineBreakConverter.Convert(candles, lineCount: 4);

        Assert.Equal(5, blocks.Length); // 4 up blocks + 1 down block
        Assert.False(blocks[4].IsUp);
        Assert.Equal(9.5m, blocks[4].ClosePrice);
    }
}
