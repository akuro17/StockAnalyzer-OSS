using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.MarketStructure;
using StockAnalyzer.Core.Tests.Backtest.Verification;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class MarketStructureConfirmedAndCausalTests
{
    private static CandleData Candle(decimal open, decimal high, decimal low, decimal close, int dayOffset = 0)
        => new(DateTime.Today.AddDays(dayOffset), open, high, low, close, 1000);

    #region Probe & Invariant Tests (Phases C1 & C2)

    [Fact]
    public void FourBarProbe_ConfirmedPivots_EliminatesFalseTrailingHigh()
    {
        // MP-C §4 Phase C2 probe: (H100,L90) (H100,L96) (H99,L95) (H104,L100) with threshold 3%
        var bars = new List<CandleData>
        {
            Candle(95m, 100m, 90m, 95m, 0),
            Candle(98m, 100m, 96m, 97m, 1),
            Candle(97m, 99m, 95m, 96m, 2),
            Candle(100m, 104m, 100m, 103m, 3),
        };

        const decimal threshold = 3.0m;

        // Legacy ExtractPivots adds the unconfirmed trailing extreme (H104 at index 3)
        var legacyPivots = MarketStructureDetector.ExtractPivots(bars, threshold);
        Assert.Equal(3, legacyPivots.Count);
        Assert.Equal(0, legacyPivots[0].Index);
        Assert.True(legacyPivots[0].IsHigh);
        Assert.Equal(100m, legacyPivots[0].Price);
        Assert.Equal(2, legacyPivots[1].Index);
        Assert.False(legacyPivots[1].IsHigh);
        Assert.Equal(95m, legacyPivots[1].Price);
        Assert.Equal(3, legacyPivots[2].Index);
        Assert.True(legacyPivots[2].IsHigh);
        Assert.Equal(104m, legacyPivots[2].Price);

        // Legacy IsDoubleTop matches 100 and 104 within 5% tolerance -> false positive!
        bool legacyIsDoubleTop = MarketStructureDetector.IsDoubleTop(bars, threshold);
        Assert.True(legacyIsDoubleTop, "Legacy IsDoubleTop yields false positive due to unconfirmed trailing high.");

        // Confirmed pivots only contain confirmed pivots: [H100@0 confirmed@1, L95@2 confirmed@3]
        var confirmedPivots = MarketStructureDetector.ExtractConfirmedPivots(bars, threshold);
        Assert.Equal(2, confirmedPivots.Count);
        Assert.Equal(0, confirmedPivots[0].Index);
        Assert.True(confirmedPivots[0].IsHigh);
        Assert.Equal(100m, confirmedPivots[0].Price);
        Assert.Equal(1, confirmedPivots[0].ConfirmedAtIndex);

        Assert.Equal(2, confirmedPivots[1].Index);
        Assert.False(confirmedPivots[1].IsHigh);
        Assert.Equal(95m, confirmedPivots[1].Price);
        Assert.Equal(3, confirmedPivots[1].ConfirmedAtIndex);

        // Confirmed IsDoubleTopConfirmed correctly returns false
        bool confirmedIsDoubleTop = MarketStructureDetector.IsDoubleTopConfirmed(bars, threshold);
        Assert.False(confirmedIsDoubleTop, "IsDoubleTopConfirmed must reject pattern with only 1 confirmed high.");
    }

    [Theory]
    [InlineData(1, 3.0)]
    [InlineData(2, 5.0)]
    [InlineData(3, 2.0)]
    [InlineData(4, 4.0)]
    public void ConfirmedAtIndex_IsStrictlyGreaterThan_Index(int seed, decimal threshold)
    {
        var bars = SyntheticBars.SeededRandomWalk(seed, 500, 100m);
        var pivots = MarketStructureDetector.ExtractConfirmedPivots(bars, threshold);

        Assert.NotEmpty(pivots);
        foreach (var p in pivots)
        {
            Assert.NotNull(p.ConfirmedAtIndex);
            Assert.True(p.ConfirmedAtIndex.Value > p.Index,
                $"Pivot at index {p.Index} must be confirmed at strictly greater index (was {p.ConfirmedAtIndex.Value})");
        }
    }

    [Fact]
    public void ExtractConfirmedPivots_HasPrefixProperty_AcrossCuts()
    {
        const int seed = 42;
        var fullBars = SyntheticBars.SeededRandomWalk(seed, 500, 100m);
        decimal threshold = 3.0m;

        var fullPivots = MarketStructureDetector.ExtractConfirmedPivots(fullBars, threshold);

        for (int t = 30; t < 500; t += 15)
        {
            var prefixBars = fullBars.Take(t + 1).ToList();
            var prefixPivots = MarketStructureDetector.ExtractConfirmedPivots(prefixBars, threshold);

            // Any pivot confirmed at or before bar t in the full series must exactly match the prefix series
            var expectedPivots = fullPivots.Where(p => p.ConfirmedAtIndex.HasValue && p.ConfirmedAtIndex.Value <= t).ToList();

            Assert.Equal(expectedPivots.Count, prefixPivots.Count);
            for (int i = 0; i < expectedPivots.Count; i++)
            {
                Assert.Equal(expectedPivots[i].Index, prefixPivots[i].Index);
                Assert.Equal(expectedPivots[i].IsHigh, prefixPivots[i].IsHigh);
                Assert.Equal(expectedPivots[i].Price, prefixPivots[i].Price);
                Assert.Equal(expectedPivots[i].ConfirmedAtIndex, prefixPivots[i].ConfirmedAtIndex);
            }
        }
    }

    [Fact]
    public void ExtractConfirmedPivots_BoundaryConditions()
    {
        Assert.Empty(MarketStructureDetector.ExtractConfirmedPivots(null!, 3.0m));
        Assert.Empty(MarketStructureDetector.ExtractConfirmedPivots(new List<CandleData>(), 3.0m));
        Assert.Empty(MarketStructureDetector.ExtractConfirmedPivots(new List<CandleData> { Candle(100, 100, 100, 100, 0) }, 3.0m));
        Assert.Empty(MarketStructureDetector.ExtractConfirmedPivots(new List<CandleData>
        {
            Candle(100, 100, 100, 100, 0),
            Candle(100, 100, 100, 100, 1),
        }, 3.0m));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MarketStructureDetector.ExtractConfirmedPivots(SyntheticBars.Flat(10, 100m), 0m));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MarketStructureDetector.ExtractConfirmedPivots(SyntheticBars.Flat(10, 100m), -1m));

        // Flat series produces no pivots
        Assert.Empty(MarketStructureDetector.ExtractConfirmedPivots(SyntheticBars.Flat(10, 100m), 3.0m));
    }

    #endregion

    #region Confirmed Pattern Predicates Tests (Phase C3)

    [Fact]
    public void IsDoubleTopConfirmed_RequiresConfirmedSecondPeak()
    {
        // Peak 1 @ 0 (100) -> Valley @ 2 (90) -> Peak 2 @ 4 (101)
        // If series ends at index 4, peak 2 is unconfirmed.
        var unconfirmedBars = new List<CandleData>
        {
            Candle(95, 100, 95, 96, 0), // Peak 1
            Candle(96, 97, 94, 94, 1),  // Reversal 1 confirms peak 1 (100 -> 94 is 6% > 3%)
            Candle(94, 94, 90, 91, 2),  // Valley
            Candle(91, 96, 91, 95, 3),  // Reversal confirms valley (90 -> 96 is 6.7% > 3%)
            Candle(95, 101, 95, 100, 4), // Peak 2 (unconfirmed trailing bar)
        };

        // Legacy returns true because peak 2 is appended as trailing pivot
        Assert.True(MarketStructureDetector.IsDoubleTop(unconfirmedBars, 3.0m));
        // Confirmed returns false because peak 2 has not been confirmed by subsequent reversal
        Assert.False(MarketStructureDetector.IsDoubleTopConfirmed(unconfirmedBars, 3.0m));

        // When confirmed by bar 5 dipping > 3%:
        var confirmedBars = new List<CandleData>(unconfirmedBars)
        {
            Candle(100, 100, 97, 97, 5), // Drop to 97 (101 -> 97 is 3.96% > 3%) confirms Peak 2!
        };

        Assert.True(MarketStructureDetector.IsDoubleTop(confirmedBars, 3.0m));
        Assert.True(MarketStructureDetector.IsDoubleTopConfirmed(confirmedBars, 3.0m));
    }

    [Fact]
    public void IsDoubleBottomConfirmed_RequiresConfirmedSecondValley()
    {
        // Valley 1 @ 0 (90) -> Peak @ 2 (100) -> Valley 2 @ 4 (91)
        var unconfirmedBars = new List<CandleData>
        {
            Candle(95, 95, 90, 94, 0), // Valley 1
            Candle(94, 96, 93, 95, 1),  // Reversal confirms valley 1 (90 -> 96 > 3%)
            Candle(95, 100, 95, 99, 2), // Peak
            Candle(99, 99, 94, 94, 3),  // Reversal confirms peak (100 -> 94 > 3%)
            Candle(94, 94, 91, 92, 4),  // Valley 2 (unconfirmed trailing bar)
        };

        Assert.True(MarketStructureDetector.IsDoubleBottom(unconfirmedBars, 3.0m));
        Assert.False(MarketStructureDetector.IsDoubleBottomConfirmed(unconfirmedBars, 3.0m));

        var confirmedBars = new List<CandleData>(unconfirmedBars)
        {
            Candle(92, 95, 92, 95, 5), // Rise to 95 (91 -> 95 is 4.39% > 3%) confirms Valley 2!
        };

        Assert.True(MarketStructureDetector.IsDoubleBottom(confirmedBars, 3.0m));
        Assert.True(MarketStructureDetector.IsDoubleBottomConfirmed(confirmedBars, 3.0m));
    }

    [Fact]
    public void IsHeadAndShouldersTopConfirmed_RequiresConfirmedRightShoulder()
    {
        // Unconfirmed sequence: ends at Right Shoulder (index 7, high 110)
        var unconfirmedBars = new List<CandleData>
        {
            Candle(90m, 90m, 90m, 90m, 0),
            Candle(108m, 110m, 108m, 110m, 1), // Left shoulder: 110
            Candle(100m, 100m, 95m, 95m, 2),   // Trough 1: 95
            Candle(105m, 108m, 105m, 108m, 3), // Reversal up
            Candle(128m, 130m, 128m, 130m, 4), // Head: 130
            Candle(100m, 100m, 95m, 95m, 5),   // Trough 2: 95
            Candle(105m, 108m, 105m, 108m, 6), // Reversal up
            Candle(108m, 110m, 108m, 110m, 7), // Right shoulder: 110 (unconfirmed trailing)
        };

        Assert.True(MarketStructureDetector.IsHeadAndShouldersTop(unconfirmedBars, 3.0m));
        Assert.False(MarketStructureDetector.IsHeadAndShouldersTopConfirmed(unconfirmedBars, 3.0m));

        // When confirmed by bar 8 reversing down (110 -> 98 is 10.9% > 3%):
        var confirmedBars = new List<CandleData>(unconfirmedBars)
        {
            Candle(100m, 100m, 98m, 98m, 8),   // Reversal down confirms RS!
        };

        Assert.True(MarketStructureDetector.IsHeadAndShouldersTop(confirmedBars, 3.0m));
        Assert.True(MarketStructureDetector.IsHeadAndShouldersTopConfirmed(confirmedBars, 3.0m));
    }

    [Fact]
    public void IsHeadAndShouldersBottomConfirmed_RequiresConfirmedRightShoulder()
    {
        // Unconfirmed sequence: ends at Right Shoulder (index 7, low 90)
        var unconfirmedBars = new List<CandleData>
        {
            Candle(110m, 110m, 110m, 110m, 0),
            Candle(92m, 92m, 90m, 90m, 1),     // Left shoulder: 90
            Candle(100m, 105m, 100m, 105m, 2), // Peak 1: 105
            Candle(95m, 95m, 92m, 92m, 3),     // Reversal down
            Candle(72m, 72m, 70m, 70m, 4),     // Head: 70
            Candle(100m, 105m, 100m, 105m, 5), // Peak 2: 105
            Candle(95m, 95m, 92m, 92m, 6),     // Reversal down
            Candle(92m, 92m, 90m, 90m, 7),     // Right shoulder: 90 (unconfirmed trailing)
        };

        Assert.True(MarketStructureDetector.IsHeadAndShouldersBottom(unconfirmedBars, 3.0m));
        Assert.False(MarketStructureDetector.IsHeadAndShouldersBottomConfirmed(unconfirmedBars, 3.0m));

        // When confirmed by bar 8 reversing up (90 -> 102 is 13.3% > 3%):
        var confirmedBars = new List<CandleData>(unconfirmedBars)
        {
            Candle(100m, 102m, 100m, 102m, 8), // Reversal up confirms RS!
        };

        Assert.True(MarketStructureDetector.IsHeadAndShouldersBottom(confirmedBars, 3.0m));
        Assert.True(MarketStructureDetector.IsHeadAndShouldersBottomConfirmed(confirmedBars, 3.0m));
    }

    #endregion

    #region Causal BOS / CHoCH State Machine & Transition Tests (Phase C4)

    [Fact]
    public void DetectCausal_Row1_TrendZero_UpBreak_EstablishesBullishTrend_NoEvent()
    {
        // Trend = 0. Price confirms High 100. Then Close > 100.
        // Transition: trend becomes +1, H* consumed, NO event emitted.
        var bars = new List<CandleData>
        {
            Candle(95, 100, 95, 96, 0),  // High 100 @ 0
            Candle(96, 97, 95, 95, 1),   // Reversal drop (100 -> 95 is 5%) confirms High 100 at index 1
            Candle(95, 96, 94, 95, 2),   // Hold
            Candle(95, 102, 95, 102, 3), // Bar 3: Close 102 > High 100. ConfirmedAtIndex=1 < 3.
        };

        var shifts = MarketStructureDetector.DetectCausal(bars, 5.0m);
        // Trend was 0, so initial break only sets trend to +1 without emitting BOS/CHoCH
        Assert.Empty(shifts);
    }

    [Fact]
    public void DetectCausal_Row2_TrendZero_DownBreak_EstablishesBearishTrend_NoEvent()
    {
        // Trend = 0. Price confirms Low 100. Then Close < 100.
        // Transition: trend becomes -1, L* consumed, NO event emitted.
        var bars = new List<CandleData>
        {
            Candle(105, 105, 100, 104, 0), // Low 100 @ 0
            Candle(104, 106, 104, 105, 1), // Reversal rise (100 -> 106 is 6%) confirms Low 100 at index 1
            Candle(105, 105, 104, 105, 2), // Hold
            Candle(105, 105, 98, 98, 3),   // Bar 3: Close 98 < Low 100. ConfirmedAtIndex=1 < 3.
        };

        var shifts = MarketStructureDetector.DetectCausal(bars, 5.0m);
        // Trend was 0, so initial break only sets trend to -1 without emitting BOS/CHoCH
        Assert.Empty(shifts);
    }

    [Fact]
    public void DetectCausal_Row3_TrendBullish_UpBreak_EmitsBullishBOS()
    {
        // 1. Establish trend +1: High 110 formed at 1, confirmed at 2, broken at 3
        // 2. New High 112 formed at 3, confirmed at 4, broken at 5 -> BullishBOS!
        var bars = new List<CandleData>
        {
            Candle(100, 100, 100, 100, 0),
            Candle(110, 110, 110, 110, 1), // High 110 @ 1
            Candle(100, 100, 100, 100, 2), // Confirms High 110 @ 2 (110 -> 100 is 9.09% >= 5%)
            Candle(112, 112, 112, 112, 3), // Close 112 > 110 -> trend = +1, High 110 consumed
            Candle(105, 105, 105, 105, 4), // Confirms High 112 @ 4 (112 -> 105 is 6.25% >= 5%)
            Candle(105, 115, 105, 115, 5), // Close 115 > 112 -> BullishBOS at bar 5!
        };

        var shifts = MarketStructureDetector.DetectCausal(bars, 5.0m);
        Assert.Single(shifts);
        Assert.Equal(MarketStructureType.BullishBOS, shifts[0].Type);
        Assert.Equal(5, shifts[0].Index);
        Assert.Equal(115m, shifts[0].Price);
        Assert.Equal(112m, shifts[0].PreviousPivotHigh);
        Assert.Equal(3, shifts[0].PreviousPivotHighIndex);
    }

    [Fact]
    public void DetectCausal_Row4_TrendBullish_DownBreak_EmitsBearishCHoCH()
    {
        // Trend +1 established, then price breaks below latest confirmed Low -> BearishCHoCH, trend becomes -1
        var bars = new List<CandleData>
        {
            // Initial trend establishment
            Candle(95, 100, 95, 96, 0),  // High 100 @ 0
            Candle(96, 97, 95, 95, 1),   // Confirms High 100 @ 1
            Candle(95, 102, 95, 102, 2), // Close 102 > 100 -> establishes trend = +1, High 100 consumed

            // Form confirmed Low 95 @ 3
            Candle(102, 102, 95, 96, 3), // Low 95 @ 3
            Candle(96, 102, 96, 101, 4), // Rise (95 -> 102 is 7.37% > 5%) confirms Low 95 @ 4
            Candle(101, 102, 100, 101, 5), // Hold
            Candle(101, 101, 90, 91, 6),  // Bar 6: Close 91 < Low 95 -> BearishCHoCH!
        };

        var shifts = MarketStructureDetector.DetectCausal(bars, 5.0m);
        Assert.Single(shifts);
        Assert.Equal(MarketStructureType.BearishCHoCH, shifts[0].Type);
        Assert.Equal(6, shifts[0].Index);
        Assert.Equal(91m, shifts[0].Price);
        Assert.Equal(95m, shifts[0].PreviousPivotLow);
        Assert.Equal(3, shifts[0].PreviousPivotLowIndex);
    }

    [Fact]
    public void DetectCausal_Row5_TrendBearish_DownBreak_EmitsBearishBOS()
    {
        // 1. Establish trend -1: Low 90 formed at 1, confirmed at 2, broken at 3
        // 2. New Low 88 formed at 3, confirmed at 4, broken at 5 -> BearishBOS!
        var bars = new List<CandleData>
        {
            Candle(100, 100, 100, 100, 0),
            Candle(90, 90, 90, 90, 1),   // Low 90 @ 1
            Candle(100, 100, 100, 100, 2), // Confirms Low 90 @ 2 (90 -> 100 is 11.1% >= 5%)
            Candle(88, 88, 88, 88, 3),   // Close 88 < 90 -> trend = -1, Low 90 consumed
            Candle(95, 95, 95, 95, 4),   // Confirms Low 88 @ 4 (88 -> 95 is 7.95% >= 5%)
            Candle(85, 85, 80, 80, 5),   // Close 80 < 88 -> BearishBOS at bar 5!
        };

        var shifts = MarketStructureDetector.DetectCausal(bars, 5.0m);
        Assert.Single(shifts);
        Assert.Equal(MarketStructureType.BearishBOS, shifts[0].Type);
        Assert.Equal(5, shifts[0].Index);
        Assert.Equal(80m, shifts[0].Price);
        Assert.Equal(88m, shifts[0].PreviousPivotLow);
        Assert.Equal(3, shifts[0].PreviousPivotLowIndex);
    }

    [Fact]
    public void DetectCausal_Row6_TrendBearish_UpBreak_EmitsBullishCHoCH()
    {
        // Trend -1 established, then price breaks above latest confirmed High -> BullishCHoCH, trend becomes +1
        var bars = new List<CandleData>
        {
            // Trend -1 establishment
            Candle(105, 105, 100, 104, 0), // Low 100 @ 0
            Candle(104, 107, 104, 106, 1), // Confirms Low 100 @ 1
            Candle(106, 106, 96, 96, 2),   // Close 96 < 100 -> establishes trend = -1, Low 100 consumed

            // Form confirmed High 105 @ 3
            Candle(96, 105, 96, 104, 3),  // High 105 @ 3
            Candle(104, 104, 98, 98, 4),  // Drop (105 -> 98 is 6.67% > 5%) confirms High 105 @ 4
            Candle(98, 99, 97, 98, 5),    // Hold
            Candle(98, 110, 98, 109, 6),  // Bar 6: Close 109 > High 105 -> BullishCHoCH!
        };

        var shifts = MarketStructureDetector.DetectCausal(bars, 5.0m);
        Assert.Single(shifts);
        Assert.Equal(MarketStructureType.BullishCHoCH, shifts[0].Type);
        Assert.Equal(6, shifts[0].Index);
        Assert.Equal(109m, shifts[0].Price);
        Assert.Equal(105m, shifts[0].PreviousPivotHigh);
        Assert.Equal(3, shifts[0].PreviousPivotHighIndex);
    }

    [Fact]
    public void DetectCausal_ConsumedPivot_DoesNotDoubleEmitOnSubsequentBars()
    {
        // 1. Establish trend +1: High 110 formed at 1, confirmed at 2, broken at 3
        // 2. New High 112 formed at 3, confirmed at 4, broken at 5 -> BullishBOS at bar 5
        // 3. Bar 6 closes above 112, but High 112 is already consumed -> NO second BOS
        var bars = new List<CandleData>
        {
            Candle(100, 100, 100, 100, 0),
            Candle(110, 110, 110, 110, 1),
            Candle(100, 100, 100, 100, 2),
            Candle(112, 112, 112, 112, 3), // trend = +1
            Candle(105, 105, 105, 105, 4), // confirms High 112
            Candle(105, 115, 105, 115, 5), // BullishBOS at bar 5
            Candle(115, 118, 115, 117, 6), // Close 117 > 112, but High 112 is consumed -> no event
        };

        var shifts = MarketStructureDetector.DetectCausal(bars, 5.0m);
        Assert.Single(shifts);
        Assert.Equal(5, shifts[0].Index);
    }

    [Theory]
    [InlineData(1, 3.0)]
    [InlineData(2, 5.0)]
    [InlineData(3, 3.0)]
    [InlineData(4, 5.0)]
    public void DetectCausal_StrictCausality_InvariantAcrossPrefixes(int seed, decimal threshold)
    {
        // MP-C §4 Phase C4 test (a): for a seeded 500-bar walk, every t in 50..499,
        // events of bars[0..t] equal events of the full series with Index <= t.
        var fullBars = SyntheticBars.SeededRandomWalk(seed, 500, 100m);
        var fullShifts = MarketStructureDetector.DetectCausal(fullBars, threshold);

        for (int t = 50; t < 500; t += 25)
        {
            var prefixBars = fullBars.Take(t + 1).ToList();
            var prefixShifts = MarketStructureDetector.DetectCausal(prefixBars, threshold);

            var expectedShifts = fullShifts.Where(s => s.Index <= t).ToList();

            Assert.Equal(expectedShifts.Count, prefixShifts.Count);
            for (int i = 0; i < expectedShifts.Count; i++)
            {
                Assert.Equal(expectedShifts[i].Type, prefixShifts[i].Type);
                Assert.Equal(expectedShifts[i].Index, prefixShifts[i].Index);
                Assert.Equal(expectedShifts[i].Price, prefixShifts[i].Price);
                Assert.Equal(expectedShifts[i].PreviousPivotHigh, prefixShifts[i].PreviousPivotHigh);
                Assert.Equal(expectedShifts[i].PreviousPivotLow, prefixShifts[i].PreviousPivotLow);
            }
        }
    }

    [Fact]
    public void DetectCausal_BoundaryConditions()
    {
        Assert.Empty(MarketStructureDetector.DetectCausal(null!, 5.0m));
        Assert.Empty(MarketStructureDetector.DetectCausal(new List<CandleData>(), 5.0m));
        Assert.Empty(MarketStructureDetector.DetectCausal(SyntheticBars.Flat(4, 100m), 5.0m));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MarketStructureDetector.DetectCausal(SyntheticBars.Flat(10, 100m), 0m));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MarketStructureDetector.DetectCausal(SyntheticBars.Flat(10, 100m), -2.0m));

        // Flat series produces no shifts
        Assert.Empty(MarketStructureDetector.DetectCausal(SyntheticBars.Flat(10, 100m), 5.0m));
    }

    #endregion
}
