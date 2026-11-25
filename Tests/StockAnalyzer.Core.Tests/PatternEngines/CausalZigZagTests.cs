using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.MarketStructure;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;
using Xunit;

namespace StockAnalyzer.Core.Tests.PatternEngines;

/// <summary>Plan ChartPatterns56 V2, run V2-1: the causal ZigZag of 00_COMMON 5.2 (probe PV-1: with the extra rules switched off it equals the legacy ExtractConfirmedPivots).</summary>
public class CausalZigZagTests
{
    private static readonly CausalZigZagOptions Plain = new(3m, MinAtrMultiplier: 0m, FractalBars: 0);
    private static readonly CausalZigZagOptions Full = new(3m);

    private static IReadOnlyList<ConfirmedPivot> Run(IReadOnlyList<CandleData> bars, CausalZigZagOptions options)
        => CausalZigZag.Extract(PatternEngineTestBars.Context(bars), options);

    // ---- PV-1: equality with the legacy function when the new rules are off --------------------------------------

    [Theory]
    [InlineData(1.0)]
    [InlineData(3.0)]
    [InlineData(5.0)]
    public void WithAtrAndFractalOff_EqualsTheLegacyExtractConfirmedPivots(double threshold)
    {
        for (int seed = 1; seed <= 12; seed++)
        {
            CandleData[] bars = PatternEngineTestBars.Walk(seed, 1000);
            IReadOnlyList<ConfirmedPivot> mine = Run(bars, new CausalZigZagOptions((decimal)threshold, 0m, 0));
            IReadOnlyList<PivotPoint> legacy = MarketStructureDetector.ExtractConfirmedPivots(bars, (decimal)threshold);

            Assert.Equal(legacy.Count, mine.Count);
            for (int k = 0; k < legacy.Count; k++)
            {
                Assert.Equal(legacy[k].Index, mine[k].Index);
                Assert.Equal(legacy[k].Price, mine[k].Price);
                Assert.Equal(legacy[k].IsHigh, mine[k].IsHigh);
                Assert.Equal(legacy[k].ConfirmedAtIndex, mine[k].ConfirmedAtIndex);
            }
        }
    }

    // ---- structure ----------------------------------------------------------------------------------------------

    [Fact]
    public void Pivots_AlternateAndAreInChronologicalOrder()
    {
        for (int seed = 1; seed <= 6; seed++)
        {
            IReadOnlyList<ConfirmedPivot> pivots = Run(PatternEngineTestBars.Walk(seed, 1000), Full);
            Assert.NotEmpty(pivots);
            for (int k = 1; k < pivots.Count; k++)
            {
                Assert.NotEqual(pivots[k - 1].IsHigh, pivots[k].IsHigh);
                Assert.True(pivots[k - 1].Index < pivots[k].Index);
                Assert.True(pivots[k - 1].ConfirmedAtIndex < pivots[k].ConfirmedAtIndex);
            }
        }
    }

    [Fact]
    public void Fractal_ConfirmationIsNeverEarlierThanThePivotPlusTheFractalBars()
    {
        foreach (int fractal in new[] { 0, 1, 3, 5 })
        {
            IReadOnlyList<ConfirmedPivot> pivots = Run(PatternEngineTestBars.Walk(2, 1000), new CausalZigZagOptions(3m, 0m, fractal));
            Assert.All(pivots, p => Assert.True(p.ConfirmedAtIndex >= p.Index + Math.Max(1, fractal)));
        }
    }

    [Fact]
    public void Fractal_OnlyDelaysTheConfirmation_TheSamePivotsAsWithoutIt()
    {
        CandleData[] bars = PatternEngineTestBars.Walk(3, 1000);
        IReadOnlyList<ConfirmedPivot> none = Run(bars, new CausalZigZagOptions(3m, 0m, 0));
        IReadOnlyList<ConfirmedPivot> delayed = Run(bars, new CausalZigZagOptions(3m, 0m, 5));

        Assert.Equal(none.Select(p => (p.Index, p.Price, p.IsHigh)), delayed.Select(p => (p.Index, p.Price, p.IsHigh)));
        Assert.True(delayed.Zip(none, (d, n) => d.ConfirmedAtIndex >= n.ConfirmedAtIndex).All(x => x));
        Assert.Contains(delayed.Zip(none, (d, n) => d.ConfirmedAtIndex > n.ConfirmedAtIndex), x => x); // the delay really happens on this series
    }

    // ---- causality ----------------------------------------------------------------------------------------------

    [Fact]
    public void PrefixInvariance_PivotsConfirmedByBarTAreTheSameWhenTheSeriesEndsAtT()
    {
        foreach (CausalZigZagOptions options in new[] { Plain, Full })
        {
            CandleData[] bars = PatternEngineTestBars.Walk(4, 600);
            IReadOnlyList<ConfirmedPivot> whole = Run(bars, options);

            for (int t = 40; t < bars.Length; t += 37)
            {
                var prefix = Run(bars.Take(t + 1).ToArray(), options).Where(p => p.ConfirmedAtIndex <= t).ToArray();
                var expected = whole.Where(p => p.ConfirmedAtIndex <= t).ToArray();
                Assert.Equal(expected, prefix);
            }
        }
    }

    // ---- amplitude and collision rules --------------------------------------------------------------------------

    [Fact]
    public void SameBarCollision_ABarThatSetsANewExtremeNeverConfirmsTheReversal()
    {
        CandleData[] bars =
        {
            PatternEngineTestBars.Bar(0, 100, 100, 100, 100),
            PatternEngineTestBars.Bar(1, 100, 110, 100, 110),
            PatternEngineTestBars.Bar(2, 110, 120, 100, 105),   // new high AND a low 16 percent below it: no reversal on this bar
            PatternEngineTestBars.Bar(3, 105, 119, 100, 104),   // the next bar confirms the high of bar 2
        };

        IReadOnlyList<ConfirmedPivot> pivots = Run(bars, Plain);

        Assert.Equal(2, pivots.Count);
        Assert.Equal(new ConfirmedPivot(0, 100m, false, 1), pivots[0]);
        Assert.Equal(new ConfirmedPivot(2, 120m, true, 3), pivots[1]);
    }

    // Every bar spans 105..95 around 100: each bar reverses by 10 percent, but the context ATR is 10.
    private static CandleData[] WideFlatBars(int count) => Enumerable.Range(0, count).Select(i => PatternEngineTestBars.Bar(i, 100, 105, 95, 100)).ToArray();

    [Fact]
    public void AtrAmplitude_RejectsSwingsSmallerThanTheMultipleOfTheContextAtr()
    {
        CandleData[] bars = WideFlatBars(60);

        Assert.NotEmpty(Run(bars, new CausalZigZagOptions(3m, 0m, 0)));   // percent only: pivots on almost every bar
        Assert.Empty(Run(bars, new CausalZigZagOptions(3m, 1.5m, 0)));    // 10 < 1.5 * 10
    }

    [Fact]
    public void AtrAmplitude_NoReversalIsConfirmedBeforeTheContextAtrExists()
    {
        IReadOnlyList<ConfirmedPivot> pivots = Run(WideFlatBars(60), new CausalZigZagOptions(3m, 0.5m, 0)); // 10 >= 0.5 * 10 once the ATR exists (bar 15)

        Assert.NotEmpty(pivots);
        Assert.All(pivots, p => Assert.True(p.ConfirmedAtIndex >= PatternSpecConstants.AtrPeriod + 1));
    }

    // ---- invalid bars -------------------------------------------------------------------------------------------

    [Fact]
    public void InvalidBar_IsIgnored_TheResultEqualsTheSeriesWithoutThatBar()
    {
        const int bad = 150;
        CandleData[] bars = PatternEngineTestBars.Walk(5, 400);
        CandleData[] corrupted = PatternEngineTestBars.Corrupt(bars, bad);
        CandleData[] removed = bars.Where((_, i) => i != bad).ToArray();

        IReadOnlyList<ConfirmedPivot> withBad = Run(corrupted, Plain);
        IReadOnlyList<ConfirmedPivot> without = Run(removed, Plain);

        Assert.Equal(without.Select(p => (p.Price, p.IsHigh)), withBad.Select(p => (p.Price, p.IsHigh)));
        Assert.DoesNotContain(withBad, p => p.Index == bad);
    }

    // ---- arguments ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0.0, 0.0, 0, 14)]
    [InlineData(-1.0, 0.0, 0, 14)]
    [InlineData(3.0, -0.1, 0, 14)]
    [InlineData(3.0, 0.0, -1, 14)]
    [InlineData(3.0, 0.0, 0, 0)]
    public void BadOptions_Throw(double threshold, double atr, int fractal, int period)
        => Assert.Throws<ArgumentOutOfRangeException>(() => Run(PatternEngineTestBars.Walk(1, 50), new CausalZigZagOptions((decimal)threshold, (decimal)atr, fractal, period)));

    [Fact]
    public void EmptySeries_GivesNoPivots() => Assert.Empty(Run(Array.Empty<CandleData>(), Full));
}
