using System.Linq;
using StockAnalyzer.Core;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;
using Xunit;

namespace StockAnalyzer.Core.Tests.PatternEngines;

/// <summary>Plan ChartPatterns56 V2, run V2-1: the shared pure functions of the specification (00_COMMON 1.1, 2.2, 2.3, 4.1, 4.2, 4.3) and the read-set rule R-16.</summary>
public class PatternMathTests
{
    private static CandleData B(decimal o, decimal h, decimal l, decimal c) => PatternEngineTestBars.Bar(0, o, h, l, c);

    // ---- 1.1 OHLC integrity -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(10, 12, 9, 11, true)]
    [InlineData(10, 10, 10, 10, true)]   // flat bar is consistent (H >= L)
    [InlineData(10, 9.9, 9, 9.5, false)] // H < Open
    [InlineData(10, 10.4, 9, 11, false)] // H < Close
    [InlineData(10, 12, 10.1, 11, false)] // L > Open
    [InlineData(10, 12, 11.1, 11, false)] // L > Close and Low above Open
    [InlineData(0, 12, 9, 11, false)]
    [InlineData(10, 12, 0, 11, false)]
    [InlineData(10, 12, 9, 0, false)]
    [InlineData(10, -1, 9, 11, false)]
    public void OhlcConsistency_FollowsSpecSection11(double o, double h, double l, double c, bool expected)
    {
        CandleData bar = B((decimal)o, (decimal)h, (decimal)l, (decimal)c);
        Assert.Equal(expected, PatternMath.IsOhlcConsistent(in bar));
    }

    [Fact]
    public void OhlcConsistency_HighBelowLow_IsInconsistent()
    {
        CandleData bar = B(10, 8, 9, 9); // H < L (and H < O)
        Assert.False(PatternMath.IsOhlcConsistent(in bar));
    }

    [Fact]
    public void OhlcConsistency_NegativeVolume_IsInconsistent()
    {
        CandleData bar = PatternEngineTestBars.Bar(0, 10, 12, 9, 11, volume: -1);
        Assert.False(PatternMath.IsOhlcConsistent(in bar));

        CandleData zeroVol = PatternEngineTestBars.Bar(0, 10, 12, 9, 11, volume: 0);
        Assert.True(PatternMath.IsOhlcConsistent(in zeroVol));
    }

    // ---- 2.2 context ATR ----------------------------------------------------------------------------------------

    [Fact]
    public void AtrPeriodEqualsLegacyConstant() => Assert.Equal(ChartConstants.CandlePatternAtrPeriod, PatternSpecConstants.AtrPeriod);

    [Fact]
    public void AtrCtx_IsTheLegacyComputeContext_ForEveryComputableContextEnd()
    {
        for (int seed = 1; seed <= 6; seed++)
        {
            CandleData[] bars = PatternEngineTestBars.Walk(seed, 400);
            PatternSeriesContext ctx = PatternEngineTestBars.Context(bars);

            for (int e = PatternSpecConstants.AtrPeriod; e < bars.Length; e++)
            {
                Assert.True(PatternMath.TryAtrCtx(ctx, e, PatternSpecConstants.AtrPeriod, out decimal atr, out decimal avgBody), $"seed {seed} e {e}");
                (decimal expectedBody, decimal expectedAtr) = CandlePatternDetector.ComputeContext(bars, e - PatternSpecConstants.AtrPeriod + 1, e);
                Assert.Equal(expectedAtr, atr);
                Assert.Equal(expectedBody, avgBody);
            }
        }
    }

    [Theory]
    [InlineData(13)] // below the period: the first true range has no previous close
    [InlineData(-1)]
    [InlineData(100)] // beyond the series
    public void AtrCtx_NotComputable_OutsideTheSeriesOrBeforeThePeriod(int contextEnd)
    {
        PatternSeriesContext ctx = PatternEngineTestBars.Context(PatternEngineTestBars.Walk(1, 100));
        Assert.False(PatternMath.TryAtrCtx(ctx, contextEnd, PatternSpecConstants.AtrPeriod, out _, out _));
    }

    [Fact]
    public void AtrCtx_RejectsANonPositivePeriod()
    {
        PatternSeriesContext ctx = PatternEngineTestBars.Context(PatternEngineTestBars.Walk(1, 100));
        Assert.False(PatternMath.TryAtrCtx(ctx, 50, 0, out _, out _));
    }

    [Fact]
    public void AtrCtx_InvalidBar_IsNotComputableExactlyWhileItIsInTheReadSet()
    {
        const int bad = 50;
        const int p = PatternSpecConstants.AtrPeriod;
        PatternSeriesContext ctx = PatternEngineTestBars.Context(PatternEngineTestBars.Corrupt(PatternEngineTestBars.Walk(2, 120), bad));

        for (int e = 20; e < 120; e++)
        {
            bool inReadSet = e - p <= bad && bad <= e; // R-16: [e - P, e]
            Assert.Equal(!inReadSet, PatternMath.TryAtrCtx(ctx, e, p, out _, out _));
        }
    }

    [Fact]
    public void AtrCtx_InvalidBar_LeavesOtherContextEndsUnchanged()
    {
        CandleData[] clean = PatternEngineTestBars.Walk(3, 120);
        PatternSeriesContext cleanCtx = PatternEngineTestBars.Context(clean);
        PatternSeriesContext badCtx = PatternEngineTestBars.Context(PatternEngineTestBars.Corrupt(clean, 50));

        const int after = 50 + PatternSpecConstants.AtrPeriod + 1;
        for (int e = after; e < 120; e++)
        {
            Assert.True(PatternMath.TryAtrCtx(cleanCtx, e, PatternSpecConstants.AtrPeriod, out decimal a1, out decimal b1));
            Assert.True(PatternMath.TryAtrCtx(badCtx, e, PatternSpecConstants.AtrPeriod, out decimal a2, out decimal b2));
            Assert.Equal(a1, a2);
            Assert.Equal(b1, b2);
        }
    }

    // ---- 2.3 volume ---------------------------------------------------------------------------------------------

    private static PatternSeriesContext VolumeContext(long volume, int count = 40)
        => PatternEngineTestBars.Context(Enumerable.Range(0, count).Select(i => PatternEngineTestBars.Bar(i, 10, 11, 9, 10, volume)).ToArray());

    [Fact]
    public void Volume_OptionOff_AlwaysPasses() => Assert.True(PatternMath.VolumeConfirmed(VolumeContext(100), volume: 0, averageEnd: 30, requireVolume: false, multiplier: 5m));

    [Fact]
    public void Volume_InstrumentWithoutVolume_BypassesTheRule() => Assert.True(PatternMath.VolumeConfirmed(VolumeContext(0), volume: 0, averageEnd: 30, requireVolume: true, multiplier: 5m));

    [Fact]
    public void Volume_ZeroVolumeWithPositiveAverage_Fails() => Assert.False(PatternMath.VolumeConfirmed(VolumeContext(100), volume: 0, averageEnd: 30, requireVolume: true, multiplier: 1m));

    [Theory]
    [InlineData(120, true)]  // exactly 1.20 * 100
    [InlineData(119, false)]
    [InlineData(500, true)]
    public void Volume_NeedsTheMultipleOfTheAverage(long volume, bool expected)
        => Assert.Equal(expected, PatternMath.VolumeConfirmed(VolumeContext(100), volume, averageEnd: 30, requireVolume: true, multiplier: 1.20m));

    [Fact]
    public void Volume_AverageNotComputable_Fails()
        => Assert.False(PatternMath.VolumeConfirmed(VolumeContext(100), volume: 1000, averageEnd: 10, requireVolume: true, multiplier: 1m)); // [-9, 10] leaves the series

    [Fact]
    public void Volume_InvalidBarInTheAverageWindow_Fails()
    {
        PatternSeriesContext ctx = PatternEngineTestBars.Context(PatternEngineTestBars.Corrupt(Enumerable.Range(0, 40).Select(i => PatternEngineTestBars.Bar(i, 10, 11, 9, 10, 100)).ToArray(), 25));
        Assert.False(PatternMath.VolumeConfirmed(ctx, volume: 1000, averageEnd: 30, requireVolume: true, multiplier: 1m)); // window [11, 30] holds bar 25
        Assert.True(PatternMath.VolumeConfirmed(ctx, volume: 1000, averageEnd: 35, requireVolume: true, multiplier: 1m, averagingBars: 5)); // window [31, 35] does not
    }

    [Fact]
    public void Volume_NegativeConfirmingOrContextVolume_Fails_WhileZeroVolumeInstrumentBypassStillHolds()
    {
        // Confirming bar with negative sentinel volume fails even on zero-volume instruments (FX/Index).
        Assert.False(PatternMath.VolumeConfirmed(VolumeContext(0), volume: -1, averageEnd: 30, requireVolume: true, multiplier: 1m));

        // Negative sentinel volume cancelling positive bars to zero mean must NOT trigger the R-8 zero-volume bypass.
        CandleData[] mixedNegative = Enumerable.Range(0, 40)
            .Select(i => PatternEngineTestBars.Bar(i, 10, 11, 9, 10, i == 25 ? -1900L : 100L))
            .ToArray();
        PatternSeriesContext mixedCtx = PatternEngineTestBars.Context(mixedNegative);
        Assert.False(PatternMath.TryAverageVolume(mixedCtx, end: 30, length: 20, out _));
        Assert.False(PatternMath.VolumeConfirmed(mixedCtx, volume: 500, averageEnd: 30, requireVolume: true, multiplier: 1m));

        // Pure zero-volume instrument continues to bypass R-8 when confirming volume >= 0.
        Assert.True(PatternMath.VolumeConfirmed(VolumeContext(0), volume: 0, averageEnd: 30, requireVolume: true, multiplier: 1.5m));
    }

    // ---- 4.3 Pos ------------------------------------------------------------------------------------------------

    [Fact]
    public void Pos_IsThePositionInsideTheRange_AndClamped()
    {
        // 20 bars: lows 90..109 ascending by 1, highs 100..119 -> range [90, 119]
        PatternSeriesContext ctx = PatternEngineTestBars.Context(Enumerable.Range(0, 20).Select(i => PatternEngineTestBars.Bar(i, 95 + i, 100 + i, 90 + i, 96 + i)).ToArray());
        Assert.Equal(0m, PatternMath.Pos(ctx, 90m, 19, 20));
        Assert.Equal(1m, PatternMath.Pos(ctx, 119m, 19, 20));
        Assert.Equal(0m, PatternMath.Pos(ctx, 50m, 19, 20));
        Assert.Equal(1m, PatternMath.Pos(ctx, 500m, 19, 20));
        Assert.Equal((104.5m - 90m) / 29m, PatternMath.Pos(ctx, 104.5m, 19, 20));
    }

    [Fact]
    public void Pos_FlatRange_IsUndefined()
    {
        PatternSeriesContext ctx = PatternEngineTestBars.Context(Enumerable.Range(0, 20).Select(i => PatternEngineTestBars.Bar(i, 10, 10, 10, 10)).ToArray());
        Assert.Null(PatternMath.Pos(ctx, 10m, 19, 20));
    }

    [Fact]
    public void Pos_WindowOutsideTheSeriesOrWithAnInvalidBar_IsUndefined()
    {
        CandleData[] bars = PatternEngineTestBars.Walk(4, 60);
        Assert.Null(PatternMath.Pos(PatternEngineTestBars.Context(bars), 100m, 18, 20)); // start = -1
        Assert.NotNull(PatternMath.Pos(PatternEngineTestBars.Context(bars), 100m, 19, 20));
        Assert.Null(PatternMath.Pos(PatternEngineTestBars.Context(PatternEngineTestBars.Corrupt(bars, 30)), 100m, 40, 20)); // window [21, 40]
        Assert.Null(PatternMath.Pos(PatternEngineTestBars.Context(bars), 100m, 19, 0));
    }

    // ---- 4.1 short-term trend -----------------------------------------------------------------------------------

    // Rising 1.0 per bar with wicks 0.1: true range 1.2 for every bar (|H - prevClose| = 1.1), so the context ATR is exactly 1.2.
    private static PatternSeriesContext Rising() => PatternEngineTestBars.Context(PatternEngineTestBars.Trend(60, 100m, 1m, 0.1m));

    private static PatternSeriesContext Falling() => PatternEngineTestBars.Context(PatternEngineTestBars.Trend(60, 200m, -1m, 0.1m));

    [Fact]
    public void ShortTermTrend_AtrOfTheFixtureIsOnePointTwo()
    {
        Assert.True(PatternMath.TryAtrCtx(Rising(), 40, PatternSpecConstants.AtrPeriod, out decimal atr, out _));
        Assert.Equal(1.2m, atr);
    }

    [Fact]
    public void ShortTermTrend_RisingFallingAndFlat()
    {
        Assert.Equal(1, PatternMath.ShortTermTrend(Rising(), 40, PatternSpecConstants.ShortTrendLookbackBars, PatternSpecConstants.ShortTrendAtrMultiplier));
        Assert.Equal(-1, PatternMath.ShortTermTrend(Falling(), 40, PatternSpecConstants.ShortTrendLookbackBars, PatternSpecConstants.ShortTrendAtrMultiplier));

        PatternSeriesContext flat = PatternEngineTestBars.Context(PatternEngineTestBars.Trend(60, 100m, 0m, 0.5m));
        Assert.Equal(0, PatternMath.ShortTermTrend(flat, 40, PatternSpecConstants.ShortTrendLookbackBars, PatternSpecConstants.ShortTrendAtrMultiplier));
    }

    [Fact]
    public void ShortTermTrend_ChangeMustReachTheAtrMultipleExactly()
    {
        // lookback 6: change = 6.0; required = multiplier * 1.2
        Assert.Equal(1, PatternMath.ShortTermTrend(Rising(), 40, 6, 5.0m));   // 6.0 >= 6.0
        Assert.Equal(0, PatternMath.ShortTermTrend(Rising(), 40, 6, 5.01m));  // 6.0 < 6.012
        Assert.Equal(-1, PatternMath.ShortTermTrend(Falling(), 40, 6, 5.0m));
        Assert.Equal(0, PatternMath.ShortTermTrend(Falling(), 40, 6, 5.01m));
    }

    [Fact]
    public void ShortTermTrend_NotComputable_ReturnsNull()
    {
        Assert.Null(PatternMath.ShortTermTrend(Rising(), 10, 10, 1.5m));  // no context ATR before index 14
        Assert.Null(PatternMath.ShortTermTrend(Rising(), 100, 10, 1.5m)); // outside the series
        Assert.Null(PatternMath.ShortTermTrend(Rising(), 40, 0, 1.5m));
    }

    [Fact]
    public void ShortTermTrend_InvalidBarInTheReadSet_ReturnsNull_AndOutsideItDoesNot()
    {
        PatternSeriesContext bad = PatternEngineTestBars.Context(PatternEngineTestBars.Corrupt(PatternEngineTestBars.Trend(80, 100m, 1m, 0.1m), 40));

        // e = 45: the ATR read set [31, 45] holds bar 40.
        Assert.Null(PatternMath.ShortTermTrend(bad, 45, 10, 1.5m));
        // e = 75: ATR [61, 75], price change [65, 75], EMA read set [63, 75]: bar 40 is outside every set.
        Assert.Equal(1, PatternMath.ShortTermTrend(bad, 75, 10, 1.5m));
    }

    // ---- 4.2 mid-term trend -------------------------------------------------------------------------------------

    private static PatternSeriesContext RisingForMid() => PatternEngineTestBars.Context(PatternEngineTestBars.Trend(80, 100m, 1m, 0.1m));

    [Fact]
    public void MidTermUptrend_PercentOrAtrMove_AndMovingAverageCondition()
    {
        PatternSeriesContext ctx = RisingForMid();
        const int s = 60; // window [30, 59]; its lowest low is bar 30: open 130 - 0.1

        Assert.True(PatternMath.MidTermUptrend(ctx, s, referencePrice: 160m, 30, 0.10m, 3.0m));   // 30 / 129.9 = 23 percent
        Assert.False(PatternMath.MidTermUptrend(ctx, s, referencePrice: 133m, 30, 0.10m, 3.0m));  // 3.1 < 3.6 ATR and 2.4 percent
        Assert.True(PatternMath.MidTermUptrend(ctx, s, referencePrice: 134m, 30, 0.10m, 3.0m));   // 4.1 >= 3.6: the ATR alternative
    }

    [Fact]
    public void MidTermUptrend_NeedsAnUpwardAverage()
    {
        PatternSeriesContext falling = PatternEngineTestBars.Context(PatternEngineTestBars.Trend(80, 300m, -1m, 0.1m));
        Assert.False(PatternMath.MidTermUptrend(falling, 60, referencePrice: 1000m, 30, 0.10m, 3.0m)); // big move, but SMA20 < SMA50 and falling
    }

    [Fact]
    public void MidTermDowntrend_IsTheMirror()
    {
        PatternSeriesContext falling = PatternEngineTestBars.Context(PatternEngineTestBars.Trend(80, 300m, -1m, 0.1m));
        Assert.True(PatternMath.MidTermDowntrend(falling, 60, referencePrice: 150m, 30, 0.10m, 3.0m));
        Assert.False(PatternMath.MidTermDowntrend(RisingForMid(), 60, referencePrice: 1m, 30, 0.10m, 3.0m));
    }

    [Fact]
    public void MidTermUptrend_ExcludesThePatternStartBar()
    {
        CandleData[] bars = PatternEngineTestBars.Trend(80, 100m, 1m, 0.1m);
        PatternSeriesContext plain = PatternEngineTestBars.Context(bars);
        bars[60] = bars[60] with { Low = 1m }; // a consistent bar with a very low low exactly at the pattern start
        PatternSeriesContext withLowAtStart = PatternEngineTestBars.Context(bars);

        Assert.Equal(
            PatternMath.MidTermUptrend(plain, 60, 133m, 30, 0.10m, 3.0m),
            PatternMath.MidTermUptrend(withLowAtStart, 60, 133m, 30, 0.10m, 3.0m));
        Assert.False(PatternMath.MidTermUptrend(withLowAtStart, 60, 133m, 30, 0.10m, 3.0m));
    }

    [Fact]
    public void MidTermUptrend_NotComputable_IsFalse()
    {
        Assert.False(PatternMath.MidTermUptrend(RisingForMid(), 20, 500m, 30, 0.10m, 3.0m)); // window leaves the series
        Assert.False(PatternMath.MidTermUptrend(PatternEngineTestBars.Context(PatternEngineTestBars.Corrupt(PatternEngineTestBars.Trend(80, 100m, 1m, 0.1m), 45)), 60, 500m, 30, 0.10m, 3.0m));
    }
}
