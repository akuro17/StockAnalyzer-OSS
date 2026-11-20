using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Chart;
using StockAnalyzer.Core.Utilities;
using Xunit;
using static StockAnalyzer.Core.Tests.BlockChart.BlockChartTestData;

namespace StockAnalyzer.Core.Tests.BlockChart;

/// <summary>
/// Three Line Break overlay engine (T3). Oracle = the EXISTING <see cref="ThreeLineBreakConverter"/> on every prefix; its Reversal is the level the converter's own rule has to see broken
/// (lowest low / highest high of the last min(count, lineCount) blocks, written out by hand here, independent of the strategy class the engine calls).
/// </summary>
public class ThreeLineBreakOverlayEngineTests
{
    private const decimal Tolerance = ChartConstants.ThreeLineBreakTrendThreshold;

    private static OracleState? Oracle(IReadOnlyList<CoreCandleData> bars, int lineCount, decimal minMove)
    {
        var blocks = ThreeLineBreakConverter.Convert(bars, lineCount, Tolerance, minMove);
        if (blocks.IsEmpty) return null;

        var last = blocks[^1];
        return new OracleState(last.ClosePrice, NextReversalLevel(blocks, blocks.Length - 1, lineCount), null, last.IsUp);
    }

    /// <summary>The level to break for the NEXT reversal after block <paramref name="index"/>: the extreme of the last min(count, lineCount) blocks, the block itself included (the converter's rule).</summary>
    private static decimal NextReversalLevel(ImmutableArray<ThreeLineBreakBlock> blocks, int index, int lineCount)
    {
        int count = index + 1;
        int lookBack = Math.Min(count, lineCount);
        bool up = blocks[index].IsUp;
        decimal level = up ? decimal.MaxValue : decimal.MinValue;
        for (int i = 0; i < lookBack; i++)
        {
            var block = blocks[index - i];
            level = up ? Math.Min(level, block.Low) : Math.Max(level, block.High);
        }

        return level;
    }

    private static BlockChartOverlaySeries Run(IReadOnlyList<CoreCandleData> candles, int lineCount, decimal minMove = 0m)
        => BlockChartOverlayMapper.Map(candles, sizing: null, _ => new ThreeLineBreakBarEngine(lineCount, minMove), hasUpdate: false);

    // --- prefix parity (TC-10) --------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, 3, 0.0)]
    [InlineData(2, 1, 0.0)]
    [InlineData(3, 5, 0.0)]
    [InlineData(4, 3, 0.5)]
    [InlineData(5, 3, 2.0)]
    [InlineData(6, 2, 1.0)]
    public void EveryPrefixEqualsTheThreeLineBreakConverter(int seed, int lineCount, double minMove)
    {
        var candles = RandomWalk(seed, 200, 100m, 2m);

        BlockChartParity.AssertMatches(candles, Run(candles, lineCount, (decimal)minMove), 0, p => Oracle(p, lineCount, (decimal)minMove), $"seed {seed}", requireRichComparison: true);
    }

    // --- TC-14 ------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Tc14_FewerBlocksThanTheLineCount_NeverFailsAndUsesTheBlocksThatExist()
    {
        // lineCount 3 but only 1, then 2 blocks exist: min(count, lineCount) keeps every index inside the window.
        var candles = FromCloses(100m, 101m, 102m, 103m, 104m);
        var series = Run(candles, 3);

        Assert.Null(series.Close[0]);
        Assert.Equal(101m, series.Close[1]);
        Assert.Equal(100m, series.Reversal[1]);   // 1 block: its own low
        Assert.Equal(102m, series.Close[2]);
        Assert.Equal(100m, series.Reversal[2]);   // 2 blocks: lowest low of both
        Assert.Equal(103m, series.Close[3]);
        Assert.Equal(100m, series.Reversal[3]);   // 3 blocks
        Assert.Equal(104m, series.Close[4]);
        Assert.Equal(101m, series.Reversal[4]);   // the window is now the last 3 blocks: lows 101, 102, 103
        Assert.All(Enumerable.Range(1, 4), i => Assert.Equal(1m, series.IsUpTrend[i]));
        Assert.Null(series.Update);               // no Update series at all
        BlockChartParity.AssertMatches(candles, series, 0, p => Oracle(p, 3, 0m), "few blocks");
    }

    [Fact]
    public void Tc14_AfterThreeBlocksABreakBelowTheLowestLowReverses_AndNothingElseDoes()
    {
        // Blocks [100,101] [101,102] [102,103] [103,104]; the reversal level is the lowest low of the last 3 blocks = 101.
        var notBroken = Run(FromCloses(100m, 101m, 102m, 103m, 104m, 101.00m), 3);
        Assert.Equal(104m, notBroken.Close[5]);  // 101.00 is not below 101 - tolerance

        var broken = Run(FromCloses(100m, 101m, 102m, 103m, 104m, 100.80m), 3);
        Assert.Equal(100.80m, broken.Close[5]);
        Assert.Equal(0m, broken.IsUpTrend[5]);
        Assert.Equal(104m, broken.Reversal[5]);  // down trend: highest high of the last 3 blocks (103-104, 102-103... and the new one from 104)
    }

    // --- TC-08: the tolerance boundaries are strict ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("100.0001", false)] // exactly the tolerance: not more than it
    [InlineData("100.0002", true)]
    public void Tc08_TheFirstBlockNeedsMoreThanTheTolerance(string closeText, bool blockExists)
    {
        decimal close = decimal.Parse(closeText, System.Globalization.CultureInfo.InvariantCulture);
        var series = Run(FromCloses(100m, close), 3);

        Assert.Equal(blockExists, series.Close[1].HasValue);
        if (blockExists) Assert.Equal(close, series.Close[1]);
    }

    [Theory]
    [InlineData("101.0001", 101.0)]   // continuation needs MORE than last close + tolerance
    [InlineData("101.0002", 101.0002)]
    public void Tc08_ContinuationNeedsMoreThanTheTolerance(string closeText, double expectedClose)
    {
        decimal close = decimal.Parse(closeText, System.Globalization.CultureInfo.InvariantCulture);
        var series = Run(FromCloses(100m, 101m, close), 3);

        Assert.Equal((decimal)expectedClose, series.Close[2]);
    }

    [Theory]
    [InlineData("99.9999", 101.0)]    // reversal needs a close BELOW (lowest low - tolerance), strictly
    [InlineData("99.9998", 99.9998)]
    public void Tc08_ReversalNeedsAStrictBreakBeyondTheTolerance(string closeText, double expectedClose)
    {
        decimal close = decimal.Parse(closeText, System.Globalization.CultureInfo.InvariantCulture);
        var series = Run(FromCloses(100m, 101m, close), 3);

        Assert.Equal((decimal)expectedClose, series.Close[2]);
    }

    [Fact]
    public void MinimumMove_SkipsSmallMoves_AndAMoveOfExactlyTheMinimumPasses()
    {
        var series = Run(FromCloses(100m, 105m, 106m, 107m), lineCount: 3, minMove: 2m);

        Assert.Equal(105m, series.Close[1]); // the first block ignores the filter
        Assert.Equal(105m, series.Close[2]); // |106 - 105| = 1 < 2: ignored
        Assert.Equal(107m, series.Close[3]); // |107 - 105| = 2: not less than the minimum, processed
    }

    // --- engine contract and the Data tab discrepancy (finding F-5) -------------------------------------------------------------------------------

    [Fact]
    public void Contract_EmptyOneCandleAndInvalidBars()
    {
        var empty = Run(new List<CoreCandleData>(), 3);
        Assert.Empty(empty.Close);
        Assert.Null(empty.Update);

        var one = Run(FromCloses(100m), 3);
        Assert.Single(one.Close);
        Assert.Null(one.Close[0]);

        // An invalid bar in the middle is null everywhere and leaves the state untouched; later bars equal the run without it.
        var clean = RandomWalk(51, 80, 100m, 2m);
        var broken = clean.ToList();
        broken[40] = broken[40] with { Close = 0m };
        var removed = clean.Where((_, i) => i != 40).ToList();
        var full = Run(broken, 3);
        var reduced = Run(removed, 3);

        Assert.Null(full.Close[40]);
        Assert.Null(full.Reversal[40]);
        Assert.Null(full.IsUpTrend[40]);
        for (int i = 0, j = 0; i < broken.Count; i++)
        {
            if (i == 40) continue;
            Assert.True(full.Close[i] == reduced.Close[j] && full.Reversal[i] == reduced.Reversal[j] && full.IsUpTrend[i] == reduced.IsUpTrend[j], $"bar {i}");
            j++;
        }
    }

    [Fact]
    public void NoRepaint_AppendingABarNeverChangesEarlierValues()
    {
        var baseSeries = RandomWalk(52, 100, 100m, 2m);
        var violent = Bar(100, baseSeries[^1].Close, baseSeries[^1].Close * 1.5m, baseSeries[^1].Close * 0.4m, baseSeries[^1].Close * 0.45m);

        var before = Run(baseSeries, 3);
        var after = Run(baseSeries.Append(violent).ToList(), 3);

        for (int i = 0; i < baseSeries.Count; i++)
        {
            Assert.True(before.Close[i] == after.Close[i] && before.Reversal[i] == after.Reversal[i] && before.IsUpTrend[i] == after.IsUpTrend[i], $"bar {i}");
        }
    }

    [Fact]
    public void Engine_RejectsAZeroLineCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThreeLineBreakBarEngine(0, 0m));
    }

    /// <summary>
    /// CHARACTERIZATION of finding F-5 (not a fix): the Data tab row "Reversal" of the Three Line Break chart type (DataWindowViewModel, the loop over
    /// <c>blocks[target - lineCount .. target - 1]</c>) leaves the hovered block out, while the converter's reversal rule (<see cref="StandardThreeLineBreakStrategy.ShouldReverse"/>) includes the last block.
    /// The overlay follows the rule the converter applies (owner decision D-4 = A); the Data tab row is untouched. If the Data tab is ever corrected, this test must be revisited together with it.
    /// </summary>
    [Fact]
    public void Characterization_F5_DataTabLeavesTheHoveredBlockOut_TheConverterRuleIncludesIt()
    {
        const int lineCount = 3;
        var candles = FromCloses(10m, 11m, 12m, 13m, 14m); // four up blocks with lows 10, 11, 12, 13
        var blocks = ThreeLineBreakConverter.Convert(candles, lineCount, Tolerance, 0m);
        Assert.Equal(4, blocks.Length);
        int target = 3;

        // The Data tab formula, copied from DataWindowViewModel (targetBlockIndex >= lineCount, up trend): lowest low of blocks [target - lineCount, target - 1].
        decimal dataTab = decimal.MaxValue;
        for (int b = target - lineCount; b < target; b++) dataTab = Math.Min(dataTab, blocks[b].Low);

        // The rule the converter applies for the next reversal, through the strategy class itself.
        new StandardThreeLineBreakStrategy().ShouldReverse(blocks[target].ClosePrice, blocks.ToList(), blocks[target].IsUp, lineCount, Tolerance, out decimal converterRule);

        Assert.Equal(10m, dataTab);
        Assert.Equal(11m, converterRule);

        // The overlay reports the converter's rule.
        var series = Run(candles, lineCount);
        Assert.Equal(converterRule, series.Reversal[4]);
    }
}
