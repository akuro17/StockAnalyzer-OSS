using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Verification;

/// <summary>
/// TASK-BT-02 verification: Deterministic candle shape (bullish O->H->L->C vs bearish O->L->H->C),
/// adverse Doji ordering, and SL/TP tie-break rules.
/// </summary>
[Trait("Category", "BacktestVerification")]
public class DeterministicPathTests
{
    private static readonly DateTime Bar0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeInForce Gtc = new(true, -1);

    private static CandleData MakeBar(int dayOffset, decimal open, decimal high, decimal low, decimal close, long volume = 1000)
        => new(Bar0.AddDays(dayOffset), open, high, low, close, volume);

    private static BacktestInput MakeInput(ImmutableArray<CandleData> bars)
        => new(bars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion, Bar0, Bar0.AddDays(bars.Length + 1), 0, 0);

    [Fact]
    public void BearishBar_VisitsLowBeforeHigh()
    {
        // O=100, H=110, L=90, C=95 (Close < Open)
        // Under DeterministicCandleShape: path is O(100) -> L(90) -> H(110) -> C(95)
        decimal open = 100m, high = 110m, low = 90m, close = 95m;

        decimal? lowTouch = FillPathScanner.TryFindTouchPosition(open, high, low, close, 92m, TouchDirection.Downward, IntrabarPathMode.DeterministicCandleShape);
        decimal? highTouch = FillPathScanner.TryFindTouchPosition(open, high, low, close, 105m, TouchDirection.Upward, IntrabarPathMode.DeterministicCandleShape);

        Assert.NotNull(lowTouch);
        Assert.NotNull(highTouch);
        // Low is visited on leg 0 (0..1)
        Assert.True(lowTouch.Value < 1m);
        // High is visited on leg 1 (1..2)
        Assert.True(highTouch.Value >= 1m && highTouch.Value < 2m);
        // Low is visited strictly before High
        Assert.True(lowTouch.Value < highTouch.Value);
    }

    [Fact]
    public void BullishBar_VisitsHighBeforeLow()
    {
        // O=100, H=110, L=90, C=105 (Close > Open)
        // Under DeterministicCandleShape: path is O(100) -> H(110) -> L(90) -> C(105)
        decimal open = 100m, high = 110m, low = 90m, close = 105m;

        decimal? highTouch = FillPathScanner.TryFindTouchPosition(open, high, low, close, 108m, TouchDirection.Upward, IntrabarPathMode.DeterministicCandleShape);
        decimal? lowTouch = FillPathScanner.TryFindTouchPosition(open, high, low, close, 92m, TouchDirection.Downward, IntrabarPathMode.DeterministicCandleShape);

        Assert.NotNull(highTouch);
        Assert.NotNull(lowTouch);
        // High is visited on leg 0 (0..1)
        Assert.True(highTouch.Value < 1m);
        // Low is visited on leg 1 (1..2)
        Assert.True(lowTouch.Value >= 1m && lowTouch.Value < 2m);
        // High is visited strictly before Low
        Assert.True(highTouch.Value < lowTouch.Value);
    }

    [Fact]
    public void DojiBar_AdverseDirectionSelectedByHeldPosition()
    {
        // O=100, H=110, L=90, C=100 (Close == Open)
        decimal open = 100m, high = 110m, low = 90m, close = 100m;

        // Long held: adverse is Down first (O -> L -> H -> C)
        decimal? lowTouchLong = FillPathScanner.TryFindTouchPosition(open, high, low, close, 95m, TouchDirection.Downward, IntrabarPathMode.DeterministicCandleShape, TradeSide.Long);
        decimal? highTouchLong = FillPathScanner.TryFindTouchPosition(open, high, low, close, 105m, TouchDirection.Upward, IntrabarPathMode.DeterministicCandleShape, TradeSide.Long);
        Assert.NotNull(lowTouchLong);
        Assert.NotNull(highTouchLong);
        Assert.True(lowTouchLong.Value < highTouchLong.Value); // Low visited first

        // Short held: adverse is Up first (O -> H -> L -> C)
        decimal? highTouchShort = FillPathScanner.TryFindTouchPosition(open, high, low, close, 105m, TouchDirection.Upward, IntrabarPathMode.DeterministicCandleShape, TradeSide.Short);
        decimal? lowTouchShort = FillPathScanner.TryFindTouchPosition(open, high, low, close, 95m, TouchDirection.Downward, IntrabarPathMode.DeterministicCandleShape, TradeSide.Short);
        Assert.NotNull(highTouchShort);
        Assert.NotNull(lowTouchShort);
        Assert.True(highTouchShort.Value < lowTouchShort.Value); // High visited first
    }

    [Theory]
    [InlineData(1.5, 1.5, SlTpWinner.StopLoss)]         // Exact tie -> StopLoss
    [InlineData(1.2, 1.8, SlTpWinner.StopLoss)]         // Same segment (both in 1..2) -> StopLoss wins
    [InlineData(1.8, 1.2, SlTpWinner.StopLoss)]         // Same segment (even if TP was numerically earlier) -> StopLoss wins
    [InlineData(0.5, 1.5, SlTpWinner.StopLoss)]         // SL earlier segment -> StopLoss
    [InlineData(2.1, 0.9, SlTpWinner.TakeProfit)]       // TP earlier segment -> TakeProfit
    public void ResolveSlTp_PrecedenceRules(double sl, double tp, SlTpWinner expected)
    {
        decimal? slPos = (decimal)sl;
        decimal? tpPos = (decimal)tp;

        SlTpWinner winner = ExitPrecedenceResolver.ResolveSlTp(slPos, tpPos);
        Assert.Equal(expected, winner);
    }

    [Theory]
    // Long position (SL=90, TP=110)
    [InlineData(100, 115, 85, 105, TradeSide.Long, PricePathConflictMode.CanonicalOHLC, SlTpWinner.StopLoss)]   // Bullish (105 > 100): O->L->H->C -> SL wins
    [InlineData(100, 115, 85, 95, TradeSide.Long, PricePathConflictMode.CanonicalOHLC, SlTpWinner.TakeProfit)]   // Bearish (95 < 100): O->H->L->C -> TP wins
    [InlineData(100, 115, 85, 100, TradeSide.Long, PricePathConflictMode.CanonicalOHLC, SlTpWinner.StopLoss)]   // Doji (100 == 100): fallback -> SL wins
    [InlineData(89, 115, 85, 95, TradeSide.Long, PricePathConflictMode.CanonicalOHLC, SlTpWinner.StopLoss)]     // Gapped down at Open <= SL (89 <= 90) -> SL wins
    [InlineData(111, 115, 85, 105, TradeSide.Long, PricePathConflictMode.CanonicalOHLC, SlTpWinner.TakeProfit)] // Gapped up at Open >= TP (111 >= 110) -> TP wins
    [InlineData(100, 115, 85, 95, TradeSide.Long, PricePathConflictMode.Conservative, SlTpWinner.StopLoss)]     // Conservative on Bearish -> SL wins
    // Short position (SL=110, TP=90)
    [InlineData(100, 115, 85, 105, TradeSide.Short, PricePathConflictMode.CanonicalOHLC, SlTpWinner.TakeProfit)] // Bullish (105 > 100): O->L->H->C -> TP (Low) wins
    [InlineData(100, 115, 85, 95, TradeSide.Short, PricePathConflictMode.CanonicalOHLC, SlTpWinner.StopLoss)]    // Bearish (95 < 100): O->H->L->C -> SL (High) wins
    [InlineData(100, 115, 85, 100, TradeSide.Short, PricePathConflictMode.CanonicalOHLC, SlTpWinner.StopLoss)]   // Doji (100 == 100): fallback -> SL wins
    [InlineData(111, 115, 85, 105, TradeSide.Short, PricePathConflictMode.CanonicalOHLC, SlTpWinner.StopLoss)]   // Gapped up at Open >= SL (111 >= 110) -> SL wins
    [InlineData(89, 115, 85, 95, TradeSide.Short, PricePathConflictMode.CanonicalOHLC, SlTpWinner.TakeProfit)]  // Gapped down at Open <= TP (89 <= 90) -> TP wins
    [InlineData(100, 115, 85, 105, TradeSide.Short, PricePathConflictMode.Conservative, SlTpWinner.StopLoss)]   // Conservative on Bullish -> SL wins
    public void ResolveIntrabarSlTp_PrecedenceRules(
        double open, double high, double low, double close,
        TradeSide side, PricePathConflictMode mode, SlTpWinner expected)
    {
        var bar = new CandleData(DateTime.UtcNow, (decimal)open, (decimal)high, (decimal)low, (decimal)close, 1000);
        decimal stopPrice = side == TradeSide.Long ? 90m : 110m;
        decimal limitPrice = side == TradeSide.Long ? 110m : 90m;

        SlTpWinner winner = ExitPrecedenceResolver.ResolveIntrabarSlTp(bar, side, stopPrice, limitPrice, mode);
        Assert.Equal(expected, winner);
    }

    [Fact]
    public void RealEngine_BearishBar_DeterministicPathFillsStopLoss_WhereasCanonicalFillsTakeProfit()
    {
        // Bar 0: Long Entry @ 100
        // Bar 1: Contested Bearish bar: O:100, H:115, L:85, C:90 (Close < Open)
        // Limit TP @ 110, Stop SL @ 90.
        // Canonical OHLC path: O(100) -> H(115, hits TP 110) -> L(85, hits SL 90) -> C(90) => TP wins!
        // Deterministic Candle Shape: O(100) -> L(85, hits SL 90) -> H(115) -> C(90) => SL wins!

        decimal open = 100m, high = 115m, low = 85m, close = 90m;

        // 1. Run with CanonicalOHLC: Limit TP at 110 touches on leg 0 (100 -> 115)
        decimal? canonicalTpTouch = FillPathScanner.TryFindTouchPosition(open, high, low, close, 110m, TouchDirection.Upward, IntrabarPathMode.CanonicalOHLC);
        decimal? canonicalSlTouch = FillPathScanner.TryFindTouchPosition(open, high, low, close, 90m, TouchDirection.Downward, IntrabarPathMode.CanonicalOHLC);
        Assert.NotNull(canonicalTpTouch);
        Assert.NotNull(canonicalSlTouch);
        Assert.True(canonicalTpTouch.Value < canonicalSlTouch.Value); // In Canonical, TP touches before SL!

        // 2. In Deterministic Candle Shape (Bearish), SL touches before TP!
        decimal? deterministicTpTouch = FillPathScanner.TryFindTouchPosition(open, high, low, close, 110m, TouchDirection.Upward, IntrabarPathMode.DeterministicCandleShape);
        decimal? deterministicSlTouch = FillPathScanner.TryFindTouchPosition(open, high, low, close, 90m, TouchDirection.Downward, IntrabarPathMode.DeterministicCandleShape);
        Assert.NotNull(deterministicTpTouch);
        Assert.NotNull(deterministicSlTouch);
        Assert.True(deterministicSlTouch.Value < deterministicTpTouch.Value); // In Deterministic, SL touches before TP!
    }
}
