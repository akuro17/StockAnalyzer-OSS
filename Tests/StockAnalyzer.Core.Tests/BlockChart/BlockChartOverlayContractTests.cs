using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Chart;
using Xunit;
using static StockAnalyzer.Core.Tests.BlockChart.BlockChartTestData;

namespace StockAnalyzer.Core.Tests.BlockChart;

/// <summary>
/// The contract of the shared per-time-bar driver (<see cref="BlockChartOverlayMapper"/>), TC-01..TC-06 and TC-09 plus the owner's supplements 1 and 3 (anchor wait, invalid bars).
/// Each case runs a sized engine through the driver; T2 adds the Point and Figure and Kagi engines to <see cref="SizedEngines"/>, T3 adds Three Line Break (no sizing) in its own test class.
/// </summary>
public class BlockChartOverlayContractTests
{
    private sealed record EngineCase(string Name, bool HasUpdate, Func<IReadOnlyList<CoreCandleData>, BlockChartSizingSpec, BlockChartOverlaySeries> Run);

    private static readonly EngineCase[] SizedEngines =
    {
        new("Renko", true, (candles, sizing) => BlockChartOverlayMapper.Map(candles, sizing, size => new RenkoBarEngine(size, 2, sizing.Rounding), hasUpdate: true)),
        new("PointAndFigure", true, (candles, sizing) => BlockChartOverlayMapper.Map(candles, sizing, size => new PointAndFigureBarEngine(size, 3, sizing.Rounding), hasUpdate: true)),
        new("Kagi", false, (candles, sizing) => BlockChartOverlayMapper.Map(candles, sizing, size => new KagiBarEngine(size), hasUpdate: false)),
    };

    private static BlockChartSizingSpec Fixed(decimal size, ChartRoundingMode rounding = ChartRoundingMode.None)
        => new(ChartSizingMode.Fixed, size, 1m, 14, 1m, rounding, AutoFallbackMode.Percentage);

    private static BlockChartSizingSpec AutoAtr(int period = 14)
        => new(ChartSizingMode.AutoAtr, 1m, 1m, period, 1m, ChartRoundingMode.None, AutoFallbackMode.Percentage);

    private static BlockChartSizingSpec Percent(decimal percentage)
        => new(ChartSizingMode.Percentage, 1m, percentage, 14, 1m, ChartRoundingMode.None, AutoFallbackMode.Percentage);

    private static IEnumerable<BlockChartSizingSpec> AllSizings()
    {
        yield return Fixed(1m);
        yield return AutoAtr();
        yield return Percent(1m);
    }

    private static bool AllNullAt(BlockChartOverlaySeries s, int i)
        => s.Close[i] is null && s.Reversal[i] is null && s.IsUpTrend[i] is null && (s.Update is null || s.Update[i] is null);

    private static void AssertSameAt(BlockChartOverlaySeries a, int i, BlockChartOverlaySeries b, int j, string label)
    {
        Assert.True(a.Close[i] == b.Close[j], $"{label}: Close {i}/{j}");
        Assert.True(a.Reversal[i] == b.Reversal[j], $"{label}: Reversal {i}/{j}");
        Assert.True(a.IsUpTrend[i] == b.IsUpTrend[j], $"{label}: IsUpTrend {i}/{j}");
        if (a.Update is not null) Assert.True(a.Update[i] == b.Update![j], $"{label}: Update {i}/{j}");
    }

    // --- TC-01 -----------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Tc01_ZeroOrOneCandle_NeverThrows_AndReturnsSeriesOfTheInputLength()
    {
        foreach (var engine in SizedEngines)
        foreach (var sizing in AllSizings())
        {
            var none = engine.Run(new List<CoreCandleData>(), sizing);
            Assert.Empty(none.Close);
            Assert.Empty(none.Reversal);
            Assert.Empty(none.IsUpTrend);
            if (engine.HasUpdate) Assert.Empty(none.Update!); else Assert.Null(none.Update);

            var one = engine.Run(FromCloses(100m), sizing);
            Assert.Single(one.Close);
            Assert.Single(one.Reversal);
            Assert.Single(one.IsUpTrend);
            if (engine.HasUpdate) Assert.Single(one.Update!); else Assert.Null(one.Update);
            Assert.True(AllNullAt(one, 0), engine.Name);
        }
    }

    // --- TC-02 -----------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Tc02_BeforeTheFirstBlock_EverySeriesIsNullNeverZero()
    {
        var candles = RandomWalk(41, 120, 100m, 2m);

        foreach (var engine in SizedEngines)
        foreach (var sizing in AllSizings())
        {
            var s = engine.Run(candles, sizing);
            int firstDefined = Enumerable.Range(0, candles.Count).First(i => s.Close[i] is not null);
            Assert.True(firstDefined > 0, "the first bar can never carry a block");

            for (int i = 0; i < firstDefined; i++) Assert.True(AllNullAt(s, i), $"{engine.Name} bar {i}");
            Assert.Empty(s.Close.Concat(s.Reversal).Concat(s.Update ?? new List<decimal?>()).Where(v => v == 0m)); // "no block yet" is null, never a plotted zero
        }
    }

    // --- TC-03 / supplements 1 and 2 -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Tc03_AutoAtrWithFewerThanPeriodPlusOneBars_IsAllNull()
    {
        foreach (var engine in SizedEngines)
        {
            var candles = RandomWalk(42, 14, 100m, 2m); // 14 bars: an ATR(14) needs 15
            var s = engine.Run(candles, AutoAtr(14));
            for (int i = 0; i < candles.Count; i++) Assert.True(AllNullAt(s, i), $"{engine.Name} bar {i}");
        }
    }

    [Fact]
    public void Tc03_AutoAtrAnchorsAtTheBarAfterThePeriod_AndEarlierBarsStayNull()
    {
        var candles = RandomWalk(43, 80, 100m, 3m);

        foreach (var engine in SizedEngines)
        {
            var s = engine.Run(candles, AutoAtr(14));
            for (int i = 0; i < 14; i++) Assert.True(AllNullAt(s, i), $"{engine.Name} bar {i}");
            Assert.Contains(s.Close.Skip(14), v => v.HasValue);
        }
    }

    [Fact]
    public void Tc03_FlatPrices_AtrIsExactlyZero_IsNotAFailure_NoBlockNoException()
    {
        // Owner supplement 2: an ATR of exactly 0 uses the existing fallback (a positive size); the series is null only because no block can form on a flat price.
        var flat = FromCloses(Enumerable.Repeat(100m, 60).ToArray());

        foreach (var engine in SizedEngines)
        {
            var s = engine.Run(flat, AutoAtr(14));
            for (int i = 0; i < flat.Count; i++) Assert.True(AllNullAt(s, i), $"{engine.Name} bar {i}");
        }
    }

    [Fact]
    public void Supplement1_ASizeThatEveryBarRoundsToZero_FinishesQuietlyWithAllNull()
    {
        // Fixed 0.4 + Floor at a price near 20000 (tick 10): every candidate anchor fails; the scan ends and every series is null.
        var candles = RandomWalk(44, 200, 20000m, 30m);

        foreach (var engine in SizedEngines)
        {
            var s = engine.Run(candles, Fixed(0.4m, ChartRoundingMode.Floor));
            Assert.Equal(candles.Count, s.Close.Count);
            for (int i = 0; i < candles.Count; i++) Assert.True(AllNullAt(s, i), $"{engine.Name} bar {i}");
        }
    }

    [Fact]
    public void Supplement1_TheAnchorWaitsForTheFirstBarWithAPositiveSize_NeverUsingTheDefaultBoxSize()
    {
        // Five bars at 20000 (Fixed 0.4 + Floor -> 0, no anchor), then the price is 90 (tick 0.05, the size 0.4 survives): the anchor is bar 5.
        var candles = FromCloses(20000m, 20000m, 20000m, 20000m, 20000m, 90m, 90m);

        foreach (var engine in SizedEngines)
        {
            var s = engine.Run(candles, Fixed(0.4m, ChartRoundingMode.Floor));
            for (int i = 0; i < 5; i++) Assert.True(AllNullAt(s, i), $"{engine.Name} bar {i}");

            // Replay with S = 0.4 (not 1.0): the seed cell is [20000, 20000.4]; 19910 / 0.4 = 49775 boxes down end exactly at 90.
            Assert.Equal(90m, s.Close[5]);
            Assert.Equal(0m, s.IsUpTrend[5]);
            Assert.Equal(90m, s.Close[6]);
        }
    }

    // --- TC-04 + supplements 3 and 4 (invalid bars) ----------------------------------------------------------------------------------------------

    private static CoreCandleData BreakClose(CoreCandleData c) => c with { Close = 0m };
    private static CoreCandleData BreakRange(CoreCandleData c) => c with { High = c.Low - 1m, Low = c.Low > 1m ? c.Low : 1m }; // High < Low

    [Theory]
    [InlineData(0, 1)]   // the very first bar
    [InlineData(5, 1)]   // before the ATR(14) anchor: the ATR window must ignore it as well
    [InlineData(40, 1)]  // in the middle
    [InlineData(79, 1)]  // the last bar
    [InlineData(40, 2)]  // two consecutive invalid bars
    public void Tc04_AnInvalidBar_IsNullInEverySeries_AndLeavesTheStateAndTheAnchorUntouched(int position, int runLength)
    {
        var clean = RandomWalk(45, 80, 100m, 2m);

        foreach (var engine in SizedEngines)
        foreach (var sizing in AllSizings())
        foreach (var breaker in new Func<CoreCandleData, CoreCandleData>[] { BreakClose, BreakRange })
        {
            var broken = clean.ToList();
            for (int k = 0; k < runLength; k++) broken[position + k] = breaker(broken[position + k]);
            var removed = clean.Where((_, i) => i < position || i >= position + runLength).ToList();

            var full = engine.Run(broken, sizing);
            var reduced = engine.Run(removed, sizing);

            Assert.Equal(broken.Count, full.Close.Count);
            int j = 0;
            for (int i = 0; i < broken.Count; i++)
            {
                if (i >= position && i < position + runLength)
                {
                    Assert.True(AllNullAt(full, i), $"{engine.Name} invalid bar {i} must be null in every series (IsUpTrend included)");
                    continue;
                }

                AssertSameAt(full, i, reduced, j, $"{engine.Name} {sizing.Mode} pos {position} run {runLength} bar {i}");
                j++;
            }
        }
    }

    // --- TC-05 -----------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Tc05_BarsWithoutAnyEvent_KeepTheStateHorizontally()
    {
        // Size 10: after the move to 120 the state holds for every close below. Renko: up brick [110,120], band (90,130); Point and Figure (R=3): column top 120, reversal at 90; Kagi: up line at 120, turning price 110.
        var candles = FromCloses(100m, 120m, 112m, 115m, 118m, 119m, 111m, 119m);

        foreach (var engine in SizedEngines)
        {
            var s = engine.Run(candles, Fixed(10m));
            for (int i = 2; i < candles.Count; i++) AssertSameAt(s, i, s, 1, $"{engine.Name} bar {i}");
            Assert.Equal(120m, s.Close[1]);
        }
    }

    // --- TC-09 (no repaint / no look-ahead) ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Tc09_AppendingBars_NeverChangesEarlierValues_EvenAViolentReversalOrAnAtrShock()
    {
        var baseSeries = RandomWalk(46, 100, 100m, 2m);
        var last = baseSeries[^1];
        var violentReversal = Bar(100, last.Close, last.Close * 1.5m, last.Close * 0.4m, last.Close * 0.45m);
        var atrShock = Bar(100, last.Close, last.Close * 3m, last.Close * 0.2m, last.Close);

        foreach (var engine in SizedEngines)
        foreach (var sizing in AllSizings())
        foreach (var appended in new[] { violentReversal, atrShock })
        {
            var extended = baseSeries.Append(appended).ToList();
            var before = engine.Run(baseSeries, sizing);
            var after = engine.Run(extended, sizing);

            for (int i = 0; i < baseSeries.Count; i++) AssertSameAt(before, i, after, i, $"{engine.Name} {sizing.Mode} bar {i}");
        }
    }

    [Fact]
    public void Tc09_EveryPrefixOfAnAutoAtrRun_IsTheStartOfTheFullRun()
    {
        var candles = RandomWalk(47, 150, 100m, 2m);

        foreach (var engine in SizedEngines)
        {
            var full = engine.Run(candles, AutoAtr(14));
            for (int n = 1; n <= candles.Count; n += 7)
            {
                var prefix = engine.Run(candles.Take(n).ToList(), AutoAtr(14));
                Assert.Equal(n, prefix.Close.Count);
                for (int i = 0; i < n; i++) AssertSameAt(prefix, i, full, i, $"{engine.Name} prefix {n} bar {i}");
            }
        }
    }

    // --- Contract of the types themselves ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void BlockChartOverlayTypes_IsExactlyTheFourRegisteredBlockCharts()
    {
        Assert.Equal(
            new[] { IndicatorType.Kagi, IndicatorType.PointAndFigure, IndicatorType.Renko, IndicatorType.ThreeLineBreak },
            BlockChartOverlayTypes.All.OrderBy(t => t.ToString(), StringComparer.Ordinal).ToArray());

        Assert.False(BlockChartOverlayTypes.Contains(IndicatorType.RenkoOverlay));
        Assert.False(BlockChartOverlayTypes.Contains(IndicatorType.ThreeLineBreakSignal));
    }

    [Fact]
    public void IsValidBar_AcceptsPositivePricesWithHighNotBelowLow_AndRejectsTheRest()
    {
        Assert.True(BlockChartOverlayMapper.IsValidBar(Flat(0, 100m)));
        Assert.True(BlockChartOverlayMapper.IsValidBar(Bar(0, 100m, 101m, 99m, 100m)));
        Assert.False(BlockChartOverlayMapper.IsValidBar(Flat(0, 0m)));
        Assert.False(BlockChartOverlayMapper.IsValidBar(Flat(0, -1m)));
        Assert.False(BlockChartOverlayMapper.IsValidBar(Bar(0, 100m, 99m, 101m, 100m)));      // High < Low
        Assert.False(BlockChartOverlayMapper.IsValidBar(Bar(0, 100m, 0m, 0m, 100m)));         // High and Low not positive
        Assert.False(BlockChartOverlayMapper.IsValidBar(Bar(0, 100m, 101m, 0m, 100m)));       // Low not positive
    }
}
