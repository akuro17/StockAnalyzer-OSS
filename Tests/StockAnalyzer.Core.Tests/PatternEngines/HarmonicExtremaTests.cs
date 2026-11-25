using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;
using Xunit;

namespace StockAnalyzer.Core.Tests.PatternEngines;

/// <summary>
/// Causal local-extrema extractor and R-26 alternation tests (<c>HarmonicExtrema</c>; ledger rows <c>HE_*</c>).
/// </summary>
public class HarmonicExtremaTests
{
    private static CandleData[] BarsFromHighsLows(decimal[] highs, decimal[] lows)
    {
        var bars = new CandleData[highs.Length];
        for (int i = 0; i < highs.Length; i++)
        {
            decimal mid = (highs[i] + lows[i]) / 2m;
            bars[i] = PatternEngineTestBars.Bar(i, mid, highs[i], lows[i], mid);
        }

        return bars;
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    public void HE_Windows_And_EqualPlateauKeepsLastBar(int w)
    {
        // A plateau of two equal highs at indices w+3 and w+4: left <= and right < selects ONLY index w+4, confirmed at w+4+w.
        int count = w * 2 + 12;
        var highs = Enumerable.Repeat(100m, count).ToArray();
        var lows = Enumerable.Repeat(90m, count).ToArray();
        highs[w + 3] = 120m;
        highs[w + 4] = 120m;

        // Also a plateau of two equal lows at w+8 and w+9 (far enough from the high plateau? keep w small/clear):
        // Put the low plateau at a separate series or verify high first.
        IReadOnlyList<ConfirmedPivot> pivots = HarmonicExtrema.ExtractWindow(PatternEngineTestBars.Context(BarsFromHighsLows(highs, lows)), w);
        ConfirmedPivot high = Assert.Single(pivots, p => p.IsHigh);
        Assert.Equal(w + 4, high.Index);
        Assert.Equal(120m, high.Price);
        Assert.Equal(w + 4 + w, high.ConfirmedAtIndex);

        // Mirror plateau for lows:
        var highs2 = Enumerable.Repeat(120m, count).ToArray();
        var lows2 = Enumerable.Repeat(100m, count).ToArray();
        lows2[w + 3] = 80m;
        lows2[w + 4] = 80m;
        ConfirmedPivot low = Assert.Single(HarmonicExtrema.ExtractWindow(PatternEngineTestBars.Context(BarsFromHighsLows(highs2, lows2)), w), p => !p.IsHigh);
        Assert.Equal(w + 4, low.Index);
        Assert.Equal(80m, low.Price);
        Assert.Equal(w + 4 + w, low.ConfirmedAtIndex);
    }

    [Fact]
    public void HE_Alternation_StrictExtreme_EqualKeepsEarlier()
    {
        // R-26: same-side neighbour replaces kept pivot ONLY when strictly more extreme; equal price keeps EARLIER pivot.
        var input = new List<ConfirmedPivot>
        {
            new(10, 120m, IsHigh: true, 12),
            new(15, 120m, IsHigh: true, 17),   // equal high -> keep bar 10
            new(20, 125m, IsHigh: true, 22),   // strictly higher -> replace with bar 20
            new(25, 122m, IsHigh: true, 27),   // lower high -> keep bar 20
            new(30, 100m, IsHigh: false, 32),  // low
            new(35, 100m, IsHigh: false, 37),  // equal low -> keep bar 30
            new(40, 95m, IsHigh: false, 42),   // strictly lower -> replace with bar 40
            new(45, 98m, IsHigh: false, 47),   // higher low -> keep bar 40
        };

        IReadOnlyList<ConfirmedPivot> alt = HarmonicExtrema.Alternate(input);
        Assert.Equal(2, alt.Count);
        Assert.Equal((20, 125m, true), (alt[0].Index, alt[0].Price, alt[0].IsHigh));
        Assert.Equal((40, 95m, false), (alt[1].Index, alt[1].Price, alt[1].IsHigh));
    }

    [Fact]
    public void HE_PrefixInvariance()
    {
        // R-26 causality: truncating a random walk at any cut t preserves every causal candidate confirmed at or before t.
        CandleData[] walk = PatternEngineTestBars.Walk(7, 300);
        foreach (int w in PatternSpecConstants.HarmonicWindows)
        {
            IReadOnlyList<HarmonicCandidatePivots> full = HarmonicExtrema.ExtractCausalCandidates(PatternEngineTestBars.Context(walk), w);
            foreach (int cut in new[] { 40, 95, 160, 230 })
            {
                IReadOnlyList<HarmonicCandidatePivots> prefix = HarmonicExtrema.ExtractCausalCandidates(PatternEngineTestBars.Context(walk.Take(cut).ToArray()), w);
                Assert.Equal(full.Where(c => c.ConfirmedAtIndex < cut).ToArray(), prefix.ToArray());
            }
        }
    }

    [Fact]
    public void HE_InvalidOhlcBar_IgnoresInvalidWindow_AndNeverBecomesPivot()
    {
        // R-18 & R-16: corrupt bar inside [i - w, i + w] yields no pivot; corrupt bar outside [i - w, i + w] leaves pivot intact.
        int count = 40;
        var highs = Enumerable.Repeat(100m, count).ToArray();
        var lows = Enumerable.Repeat(95m, count).ToArray();
        highs[15] = 130m;
        CandleData[] clean = BarsFromHighsLows(highs, lows);

        Assert.Contains(HarmonicExtrema.ExtractWindow(PatternEngineTestBars.Context(clean), 2), p => p.IsHigh && p.Index == 15);

        // Corrupt bar 17 (inside [13, 17]): bar 15 is rejected, and corrupt bar 17 (with High*1000) never becomes a pivot.
        CandleData[] corruptInside = PatternEngineTestBars.Corrupt(clean, 17);
        IReadOnlyList<ConfirmedPivot> pivotsInside = HarmonicExtrema.ExtractWindow(PatternEngineTestBars.Context(corruptInside), 2);
        Assert.DoesNotContain(pivotsInside, p => p.Index == 15 || p.Index == 17);

        // Corrupt bar 18 (outside [13, 17]): bar 15 stays confirmed.
        CandleData[] corruptOutside = PatternEngineTestBars.Corrupt(clean, 18);
        IReadOnlyList<ConfirmedPivot> pivotsOutside = HarmonicExtrema.ExtractWindow(PatternEngineTestBars.Context(corruptOutside), 2);
        Assert.Contains(pivotsOutside, p => p.IsHigh && p.Index == 15);
        Assert.DoesNotContain(pivotsOutside, p => p.Index == 18);
    }
}
