using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Chart;
using StockAnalyzer.Core.Utilities;
using StockAnalyzer.ZeroAllocation;
using Xunit;
using static StockAnalyzer.Core.Tests.BlockChart.BlockChartTestData;

namespace StockAnalyzer.Core.Tests.BlockChart;

/// <summary>
/// Renko overlay engine (T1). The oracle is the EXISTING <see cref="RenkoCalculator"/> run on every prefix of the series with the same effective size: the overlay value of bar j must equal the
/// state that calculator ends in after bars 0..j. That is the permanent proof that the incremental engine and the chart type cannot drift apart.
/// </summary>
public class RenkoOverlayEngineTests
{
    private readonly record struct RenkoState(decimal Close, decimal Reversal, decimal Update, bool IsUp);

    /// <summary>The state the chart type ends in for the given bars (null while it has no brick). The Reversal/Update formulas are written out independently of <see cref="RenkoLevels"/>.</summary>
    private static RenkoState? Oracle(IReadOnlyList<CoreCandleData> bars, decimal size, int reversalBricks, ChartRoundingMode rounding)
    {
        var source = bars.Select(c => new ZeroAllocCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, (long)c.Volume)).ToArray();
        var adapter = RenkoCalculator.Calculate(source, new RenkoParameters(size, reversalBricks, rounding));
        if (adapter.Count == 0) return null;

        decimal open = adapter.Opens.Span[adapter.Count - 1];
        decimal close = adapter.Closes.Span[adapter.Count - 1];
        bool isUp = close >= open;
        decimal top = Math.Max(open, close);
        decimal bottom = Math.Min(open, close);
        int r = Math.Max(reversalBricks, 1);

        return new RenkoState(
            close,
            isUp ? bottom - r * size : top + r * size,
            isUp ? top + size : bottom - size,
            isUp);
    }

    private static BlockChartOverlaySeries Run(IReadOnlyList<CoreCandleData> candles, BlockChartSizingSpec sizing, int reversalBricks)
        => BlockChartOverlayMapper.Map(candles, sizing, size => new RenkoBarEngine(size, reversalBricks, sizing.Rounding), hasUpdate: true);

    private static BlockChartSizingSpec Fixed(decimal size, ChartRoundingMode rounding = ChartRoundingMode.None)
        => new(ChartSizingMode.Fixed, size, 1m, 14, 1m, rounding, AutoFallbackMode.Percentage);

    private static BlockChartSizingSpec AutoAtr(int period = 14, decimal multiplier = 1m, ChartRoundingMode rounding = ChartRoundingMode.None)
        => new(ChartSizingMode.AutoAtr, 1m, 1m, period, multiplier, rounding, AutoFallbackMode.Percentage);

    private static BlockChartSizingSpec Percent(decimal percentage)
        => new(ChartSizingMode.Percentage, 1m, percentage, 14, 1m, ChartRoundingMode.None, AutoFallbackMode.Percentage);

    private static void AssertEqualsOracle(IReadOnlyList<CoreCandleData> candles, BlockChartOverlaySeries actual, decimal size, int anchorOrdinal, int reversalBricks, ChartRoundingMode rounding, string label)
    {
        BlockChartParity.AssertMatches(
            candles, actual, anchorOrdinal,
            prefix => Oracle(prefix, size, reversalBricks, rounding) is { } s ? new OracleState(s.Close, s.Reversal, s.Update, s.IsUp) : null,
            label,
            requireRichComparison: candles.Count >= 100);
    }

    // --- TC-10 (prefix parity): every prefix of every series equals the chart type's final state -------------------------------------------------

    [Theory]
    [InlineData(1, 1.0, 2, 0)]
    [InlineData(2, 0.5, 1, 0)]
    [InlineData(33, 2.5, 3, 0)]
    [InlineData(4, 1.0, 2, 3)] // Floor: the grid and the (price-dependent) size are quantized
    [InlineData(5, 0.7, 2, 5)] // Round
    public void FixedSize_EveryPrefixEqualsTheRenkoCalculator(int seed, double sizeValue, int reversalBricks, int roundingValue)
    {
        decimal size = (decimal)sizeValue;
        var rounding = (ChartRoundingMode)roundingValue;
        var candles = RandomWalk(seed, 150, 100m, 2m);
        var sizing = Fixed(size, rounding);

        // Fixed: the anchor is bar 0 unless the rounded size is not positive; resolve it exactly like the mapper does.
        Assert.True(sizing.TryResolve(candles.Take(1).ToArray(), out decimal resolved));

        AssertEqualsOracle(candles, Run(candles, sizing, reversalBricks), resolved, 0, reversalBricks, rounding, $"seed {seed}");
    }

    [Theory]
    [InlineData(11, 14, 1.0, 2)]
    [InlineData(12, 5, 0.5, 2)]
    [InlineData(13, 20, 2.0, 3)]
    public void AutoAtr_EveryPrefixFromTheAnchorEqualsTheRenkoCalculatorWithTheAnchoredSize(int seed, int period, double multiplier, int reversalBricks)
    {
        var candles = RandomWalk(seed, 150, 100m, 2m);
        var sizing = AutoAtr(period, (decimal)multiplier);

        Assert.True(sizing.TryResolve(candles.Take(period + 1).ToArray(), out decimal anchored));

        AssertEqualsOracle(candles, Run(candles, sizing, reversalBricks), anchored, period, reversalBricks, ChartRoundingMode.None, $"seed {seed}");
    }

    [Fact]
    public void Percentage_AnchorsAtTheFirstBarAndEveryPrefixEqualsTheRenkoCalculator()
    {
        var candles = RandomWalk(21, 150, 100m, 2m);
        var sizing = Percent(1.5m);
        Assert.True(sizing.TryResolve(candles.Take(1).ToArray(), out decimal anchored));
        Assert.Equal(candles[0].Close * 1.5m / 100m, anchored);

        AssertEqualsOracle(candles, Run(candles, sizing, 2), anchored, 0, 2, ChartRoundingMode.None, "percentage");
    }

    // --- TC-11 (Renko specific) -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Reversal_NeedsRBricksBelowTheBottomOfTheLastBrick_AndFlipsUpdateAndReversal()
    {
        // Size 10, R = 2. 100 seeds the grid cell [100,110]; 120 adds the up brick [110,120] (bottom 110): Reversal = 110 - 20 = 90, Update = 130.
        var candles = FromCloses(100m, 120m, 110m, 100m, 91m, 90m);
        var series = Run(candles, Fixed(10m), 2);

        Assert.Null(series.Close[0]);
        AssertState(series, 1, close: 120m, reversal: 90m, update: 130m, up: true);
        AssertState(series, 2, close: 120m, reversal: 90m, update: 130m, up: true);   // 110: 0 boxes below the bottom
        AssertState(series, 3, close: 120m, reversal: 90m, update: 130m, up: true);   // 100: 1 box below the bottom
        AssertState(series, 4, close: 120m, reversal: 90m, update: 130m, up: true);   // 91: 1.9 boxes below the bottom -> no reversal
        // 90: 2 boxes below the bottom -> reversal brick [100,110], and the same close continues down to [90,100].
        AssertState(series, 5, close: 90m, reversal: 120m, update: 80m, up: false);   // Reversal = top 100 + 2 x 10, Update = bottom 90 - 10
    }

    [Fact]
    public void Reversal_BoundariesAreInclusive_AndTheBrickAfterReversalMayContinueInTheSameBar()
    {
        // S = 0.1, R = 2, seed 100.00 -> cell [100.0, 100.1]; the first up brick needs close >= 100.2 (TC-08 for Renko).
        var below = Run(FromCloses(100.00m, 100.19m), Fixed(0.1m), 2);
        var exact = Run(FromCloses(100.00m, 100.20m), Fixed(0.1m), 2);
        var above = Run(FromCloses(100.00m, 100.21m), Fixed(0.1m), 2);

        Assert.Null(below.Close[1]);                                               // one hundredth short: no brick
        AssertState(exact, 1, close: 100.2m, reversal: 99.9m, update: 100.3m, up: true);
        AssertState(above, 1, close: 100.2m, reversal: 99.9m, update: 100.3m, up: true); // still one brick

        // Down side of the same grid: the first down brick needs close <= 99.9.
        Assert.Null(Run(FromCloses(100.00m, 99.91m), Fixed(0.1m), 2).Close[1]);
        AssertState(Run(FromCloses(100.00m, 99.90m), Fixed(0.1m), 2), 1, close: 99.9m, reversal: 100.2m, update: 99.8m, up: false);

        // A close exactly on the reversal boundary reverses (inclusive) AND continues in the same bar: up brick [100.1,100.2]; 100.1 - 2 x 0.1 = 99.9; the reversal brick is [100.0,100.1], then the close continues down to [99.9,100.0].
        var reversing = Run(FromCloses(100.00m, 100.20m, 99.90m), Fixed(0.1m), 2);
        AssertState(reversing, 2, close: 99.9m, reversal: 100.2m, update: 99.8m, up: false); // Reversal = top 100.0 + 2 x 0.1
        var notYet = Run(FromCloses(100.00m, 100.20m, 99.91m), Fixed(0.1m), 2);
        AssertState(notYet, 2, close: 100.2m, reversal: 99.9m, update: 100.3m, up: true);
    }

    // --- TC-06 (one bar, many boxes) -------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(500)]
    public void ManyBoxesInOneBar_EqualTheCalculatorsFinalState(int boxes)
    {
        var up = FromCloses(100m, 100m + boxes);
        var down = FromCloses(1_000_000m, 1_000_000m - boxes);

        AssertEqualsOracle(up, Run(up, Fixed(1m), 2), 1m, 0, 2, ChartRoundingMode.None, "up gap");
        AssertEqualsOracle(down, Run(down, Fixed(1m), 2), 1m, 0, 2, ChartRoundingMode.None, "down gap");
    }

    [Fact]
    public void AnAbsurdGap_IsResolvedInClosedForm_WithoutLoopingOverBricks()
    {
        // 100000 boxes in one bar. The calculator would have to build 100000 bricks; the engine computes the count. Seed 100.00, S = 1 -> cell [100,101]; the close 100100 makes 99999 bricks.
        var series = Run(FromCloses(100m, 100100m), Fixed(1m), 2);

        AssertState(series, 1, close: 100100m, reversal: 100097m, update: 100101m, up: true); // bottom 100099 - 2 x 1

        // The extreme: price collapses to almost nothing (positive) in one bar with a tiny size; must terminate and stay consistent.
        var collapse = Run(new List<CoreCandleData> { Flat(0, 1_000_000m), Flat(1, 0.01m) }, Fixed(0.0001m), 2);
        Assert.NotNull(collapse.Close[1]);
        Assert.False(collapse.IsUpTrend[1] == 1m);
    }

    private static void AssertState(BlockChartOverlaySeries series, int index, decimal close, decimal reversal, decimal update, bool up)
    {
        Assert.True(series.Close[index] == close, $"Close at {index}: {series.Close[index]} vs {close}");
        Assert.True(series.Reversal[index] == reversal, $"Reversal at {index}: {series.Reversal[index]} vs {reversal}");
        Assert.True(series.Update![index] == update, $"Update at {index}: {series.Update[index]} vs {update}");
        Assert.True(series.IsUpTrend[index] == (up ? 1m : 0m), $"IsUpTrend at {index}");
    }

    [Fact]
    public void RenkoLevels_MatchesTheFormulasOfTheDataTab()
    {
        Assert.True(RenkoLevels.TryDescribe(true, 120m, 110m, 10m, 2, out decimal upReversal, out decimal upUpdate));
        Assert.Equal(90m, upReversal);
        Assert.Equal(130m, upUpdate);

        Assert.True(RenkoLevels.TryDescribe(false, 110m, 100m, 10m, 2, out decimal downReversal, out decimal downUpdate));
        Assert.Equal(130m, downReversal);
        Assert.Equal(90m, downUpdate);

        Assert.False(RenkoLevels.TryDescribe(true, 120m, 110m, 0m, 2, out _, out _));
        Assert.False(RenkoLevels.TryDescribe(true, 120m, 110m, 10m, ChartConstants.MinRenkoReversalBricks - 1, out _, out _));
    }

    [Fact]
    public void Engine_RejectsANonPositiveSize_SoItCanNeverLoopOrDivideByZero()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RenkoBarEngine(0m, 2, ChartRoundingMode.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RenkoBarEngine(-1m, 2, ChartRoundingMode.None));
    }
}
