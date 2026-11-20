using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Chart;
using StockAnalyzer.Core.Utilities;
using StockAnalyzer.ZeroAllocation;
using Xunit;
using static StockAnalyzer.Core.Tests.BlockChart.BlockChartTestData;

namespace StockAnalyzer.Core.Tests.BlockChart;

/// <summary>
/// Kagi overlay engine (T2). Oracle = the EXISTING <see cref="KagiCalculator"/> on every prefix with the same effective reversal amount, then the existing
/// <see cref="KagiLevels.TryGetReversalPrice"/> on its last segment (built into <see cref="CoreCandleData"/> the way ChartDataCoordinator does).
/// </summary>
public class KagiOverlayEngineTests
{
    private static OracleState? Oracle(IReadOnlyList<CoreCandleData> bars, decimal reversalAmount)
    {
        var source = bars.Select(c => new ZeroAllocCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, (long)c.Volume)).ToArray();
        var adapter = KagiCalculator.Calculate(source, new KagiParameters(reversalAmount, ChartRoundingMode.None));

        // Segment 0 is the flat anchor (no direction, no reversal price): the chart type has nothing confirmed until a second segment exists.
        if (adapter.Count < 2) return null;

        var segments = new List<CoreCandleData>(adapter.Count);
        for (int i = 0; i < adapter.Count; i++)
        {
            segments.Add(new CoreCandleData(
                adapter.Timestamps.Span[i], adapter.Opens.Span[i], adapter.Highs.Span[i], adapter.Lows.Span[i], adapter.Closes.Span[i], adapter.Volumes.Span[i]));
        }

        int last = segments.Count - 1;
        Assert.True(KagiLevels.TryGetReversalPrice(segments, last, reversalAmount, out decimal reversal));
        return new OracleState(segments[last].Close, reversal, null, segments[last].Close > segments[last].Open);
    }

    private static BlockChartOverlaySeries Run(IReadOnlyList<CoreCandleData> candles, BlockChartSizingSpec sizing)
        => BlockChartOverlayMapper.Map(candles, sizing, size => new KagiBarEngine(size), hasUpdate: false);

    private static BlockChartSizingSpec Fixed(decimal size)
        => new(ChartSizingMode.Fixed, size, 1m, 14, 1m, ChartRoundingMode.None, AutoFallbackMode.Percentage);

    // --- prefix parity (TC-10) --------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, 1.0)]
    [InlineData(2, 0.5)]
    [InlineData(3, 2.5)]
    [InlineData(4, 4.0)]
    public void FixedAmount_EveryPrefixEqualsTheKagiCalculator(int seed, double amount)
    {
        var candles = RandomWalk(seed, 200, 100m, 2m);

        BlockChartParity.AssertMatches(candles, Run(candles, Fixed((decimal)amount)), 0, p => Oracle(p, (decimal)amount), $"seed {seed}", requireRichComparison: true);
    }

    [Theory]
    [InlineData(11, 14, 1.0)]
    [InlineData(12, 5, 0.5)]
    [InlineData(13, 20, 2.0)]
    public void AutoAtr_EveryPrefixFromTheAnchorEqualsTheCalculatorWithTheAnchoredAmount(int seed, int period, double multiplier)
    {
        var candles = RandomWalk(seed, 200, 100m, 2m);
        var sizing = new BlockChartSizingSpec(ChartSizingMode.AutoAtr, 1m, 1m, period, (decimal)multiplier, ChartRoundingMode.None, AutoFallbackMode.Percentage);
        Assert.True(sizing.TryResolve(candles.Take(period + 1).ToArray(), out decimal anchored));

        BlockChartParity.AssertMatches(candles, Run(candles, sizing), period, p => Oracle(p, anchored), $"seed {seed}", requireRichComparison: true);
    }

    [Fact]
    public void Percentage_EveryPrefixEqualsTheKagiCalculator()
    {
        var candles = RandomWalk(21, 200, 100m, 2m);
        var sizing = new BlockChartSizingSpec(ChartSizingMode.Percentage, 1m, 1.2m, 14, 1m, ChartRoundingMode.None, AutoFallbackMode.Percentage);
        Assert.True(sizing.TryResolve(candles.Take(1).ToArray(), out decimal anchored));

        BlockChartParity.AssertMatches(candles, Run(candles, sizing), 0, p => Oracle(p, anchored), "percentage", requireRichComparison: true);
    }

    // --- TC-08 / TC-13 ----------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("100.09", false)]
    [InlineData("100.10", true)]   // |close - seed| >= S is inclusive
    [InlineData("100.11", true)]
    public void Tc08_TheFirstDirectionIsSetExactlyAtOneReversalAmountFromTheSeed(string closeText, bool directionSet)
    {
        decimal close = decimal.Parse(closeText, System.Globalization.CultureInfo.InvariantCulture);
        var candles = FromCloses(100.00m, close);
        var series = Run(candles, Fixed(0.1m));

        if (directionSet)
        {
            Assert.Equal(close, series.Close[1]);
            Assert.Equal(close - 0.1m, series.Reversal[1]);
            Assert.Equal(1m, series.IsUpTrend[1]);
        }
        else
        {
            Assert.Null(series.Close[1]); // the flat anchor is not a confirmed line
        }

        BlockChartParity.AssertMatches(candles, series, 0, p => Oracle(p, 0.1m), closeText);
    }

    [Fact]
    public void Tc13_ReversalIsTheTurningPrice_InclusiveAtExactlyOneAmount_AndThereIsNoUpdateSeries()
    {
        // S = 1. 100 -> 102 sets an up line at 102 (Reversal 101). 101.01 is above the turning price: nothing changes. 101.00 turns the line (inclusive): a down line at 101, Reversal 102.
        var held = Run(FromCloses(100m, 102m, 101.01m), Fixed(1m));
        Assert.Equal(102m, held.Close[2]);
        Assert.Equal(101m, held.Reversal[2]);
        Assert.Equal(1m, held.IsUpTrend[2]);

        var turned = Run(FromCloses(100m, 102m, 101m), Fixed(1m));
        Assert.Equal(101m, turned.Close[2]);
        Assert.Equal(102m, turned.Reversal[2]);
        Assert.Equal(0m, turned.IsUpTrend[2]);

        // A close beyond the line continues it before any reversal is considered.
        var continued = Run(FromCloses(100m, 102m, 103.5m), Fixed(1m));
        Assert.Equal(103.5m, continued.Close[2]);
        Assert.Equal(102.5m, continued.Reversal[2]);

        Assert.Null(held.Update);
        Assert.Null(turned.Update);
    }

    [Fact]
    public void ReversalIsNotTheYangYinShoulderOrWaist()
    {
        // Up to 110, down to 105, up through the old shoulder to 112: the shoulder (110) changes the Yang/Yin state in the chart type, but the Reversal stays "turning price".
        var candles = FromCloses(100m, 110m, 105m, 112m);
        var series = Run(candles, Fixed(5m));

        Assert.Equal(112m, series.Close[3]);
        Assert.Equal(107m, series.Reversal[3]); // 112 - 5, never 110 (the shoulder)
        BlockChartParity.AssertMatches(candles, series, 0, p => Oracle(p, 5m), "shoulder");
    }

    [Fact]
    public void Engine_RejectsANonPositiveReversalAmount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new KagiBarEngine(0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => new KagiBarEngine(-1m));
    }

    [Fact]
    public void Engine_HasNoUpdateByContract()
    {
        var engine = new KagiBarEngine(1m);
        engine.Feed(Flat(0, 100m));
        engine.Feed(Flat(1, 103m));

        Assert.True(engine.State.IsDefined);
        Assert.Null(engine.State.Update);
    }
}
