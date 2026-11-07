#nullable enable
using System.Collections.Generic;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Tests.Backtest.Verification;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>
/// MAE / MFE must not count prices that are CERTAIN to come after the exit (docs/sa_ai_context_McpPart2WebAiReview, T3, owner decisions D-2 = A, D-3 = A).
/// A Stop (or a margin liquidation) fires when the price reaches its level, so the exit bar's extreme beyond the actual fill price on that side is after the
/// exit; a Limit fires on the favourable side; an exit filled at the bar Open holds nothing of that bar. The other side of the exit bar keeps the documented
/// bar-resolution approximation. The boundary is the actual exit fill price (slippage and liquidation penalty applied).
/// Bars: 0 = signal; 1 = entry fill at the Open 100 (High 102 / Low 99 for a Long); the exit order is submitted at the Close of bar 1 and meets bar 2.
/// </summary>
public class TradeExcursionPostExitTests
{
    private static readonly StrategyOrderRequest LongEntry = VerificationHarness.Req(SignalType.LongEntry);
    private static readonly StrategyOrderRequest ShortEntry = VerificationHarness.Req(SignalType.ShortEntry);

    private static BacktestTrade Run(
        StrategyOrderRequest entry, StrategyOrderRequest exit, (decimal O, decimal H, decimal L, decimal C) entryBar, (decimal O, decimal H, decimal L, decimal C) exitBar,
        decimal slippageRatio = 0m)
    {
        ImmutableArray<CandleData> bars = SyntheticBars.FromTable((100m, 100m, 100m, 100m), entryBar, exitBar);
        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest> { [0] = entry, [1] = exit });
        BacktestResult result = VerificationHarness.Run(VerificationHarness.MakeInput(bars), VerificationHarness.MakeConfig(slippageRatio: slippageRatio), strategy);
        return Assert.Single(result.Trades);
    }

    private static readonly (decimal, decimal, decimal, decimal) LongEntryBar = (100m, 102m, 99m, 101m);
    private static readonly (decimal, decimal, decimal, decimal) ShortEntryBar = (100m, 101m, 98m, 99m);

    [Fact]
    public void LongStop_TheLowBelowTheFillIsAfterTheExit_AndIsNotCounted()
    {
        // Stop 95 fills at 95 on the way down; the bar goes on to 80 (after the exit). The bar High 99 may have come before: it stays but does not exceed 102.
        BacktestTrade trade = Run(LongEntry, VerificationHarness.Req(SignalType.LongExit, OrderType.Stop, stopPrice: 95m), LongEntryBar, (98m, 99m, 80m, 82m));

        Assert.Equal(95m, trade.ExitPrice);
        Assert.Equal(5m, trade.MaePrice);
        Assert.Equal(2m, trade.MfePrice);
    }

    [Fact]
    public void LongStop_FilledAtTheOpenByAGap_HoldsNothingElseOfTheExitBar()
    {
        // The bar opens at 90, through the stop 95: the fill is the Open; the later 120 (and 85) are after the exit.
        BacktestTrade trade = Run(LongEntry, VerificationHarness.Req(SignalType.LongExit, OrderType.Stop, stopPrice: 95m), LongEntryBar, (90m, 120m, 85m, 88m));

        Assert.Equal(90m, trade.ExitPrice);
        Assert.Equal(10m, trade.MaePrice);
        Assert.Equal(2m, trade.MfePrice);
    }

    [Fact]
    public void LongLimit_TheHighAboveTheFillIsAfterTheExit_AndIsNotCounted()
    {
        BacktestTrade trade = Run(LongEntry, VerificationHarness.Req(SignalType.LongExit, OrderType.Limit, limitPrice: 110m), LongEntryBar, (108m, 120m, 105m, 118m));

        Assert.Equal(110m, trade.ExitPrice);
        Assert.Equal(10m, trade.MfePrice);
        Assert.Equal(1m, trade.MaePrice);
    }

    [Fact]
    public void ShortStop_TheHighAboveTheFillIsAfterTheExit_AndIsNotCounted()
    {
        BacktestTrade trade = Run(ShortEntry, VerificationHarness.Req(SignalType.ShortExit, OrderType.Stop, stopPrice: 105m), ShortEntryBar, (102m, 120m, 101m, 118m));

        Assert.Equal(TradeSide.Short, trade.Side);
        Assert.Equal(105m, trade.ExitPrice);
        Assert.Equal(5m, trade.MaePrice);
        Assert.Equal(2m, trade.MfePrice);
    }

    [Fact]
    public void ShortLimit_TheLowBelowTheFillIsAfterTheExit_AndIsNotCounted()
    {
        BacktestTrade trade = Run(ShortEntry, VerificationHarness.Req(SignalType.ShortExit, OrderType.Limit, limitPrice: 90m), ShortEntryBar, (95m, 96m, 80m, 85m));

        Assert.Equal(90m, trade.ExitPrice);
        Assert.Equal(10m, trade.MfePrice);
        Assert.Equal(1m, trade.MaePrice);
    }

    [Fact]
    public void MarketExit_IsUnchanged_OnlyTheFillPriceOfTheExitBarCounts()
    {
        BacktestTrade trade = Run(LongEntry, VerificationHarness.Req(SignalType.LongExit), LongEntryBar, (98m, 120m, 80m, 100m));

        Assert.Equal(98m, trade.ExitPrice);
        Assert.Equal(2m, trade.MaePrice); // 100 - min(99, 98)
        Assert.Equal(2m, trade.MfePrice); // 102 - 100
    }

    [Fact]
    public void LongStop_WithSlippage_IsClippedAtTheActualSlippedFill_AndMeasuredFromTheSlippedEntry()
    {
        // 1 % slippage: the entry fills at 101, the Stop 95 fills below 95. The exit bar's Low 80 is after the exit, so the lowest held price is the
        // slipped fill itself: MAE = EntryPrice - ExitPrice (D-3: measured from the actual fill prices, slippage already inside).
        BacktestTrade trade = Run(LongEntry, VerificationHarness.Req(SignalType.LongExit, OrderType.Stop, stopPrice: 95m), LongEntryBar, (98m, 99m, 80m, 82m), slippageRatio: 0.01m);

        Assert.True(trade.EntryPrice > 100m);
        Assert.True(trade.ExitPrice < 95m);
        Assert.Equal(trade.EntryPrice - trade.ExitPrice, trade.MaePrice);
    }

    // ---- forced liquidation (E0 = 1000, Q = 20, r = 0.3, m = 0.2: the Long threshold is 62.5, see BacktestEngineGapLiquidationTests) ------------

    private static BacktestTrade RunLiquidation(CandleData exitBar)
    {
        ImmutableArray<CandleData> bars = ImmutableArray.Create(SyntheticBars.Bar(0, 100m, 100m, 100m, 100m), SyntheticBars.Bar(1, 100m, 100m, 100m, 100m), exitBar);
        BacktestConfiguration config = VerificationHarness.MakeConfig(
            initialCapital: 1000m, sizingParameter: 20m, initialMarginRatio: 0.3m, maintenanceMarginRatio: 0.2m);
        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest> { [0] = LongEntry });
        BacktestTrade trade = Assert.Single(VerificationHarness.Run(VerificationHarness.MakeInput(bars), config, strategy).Trades);
        Assert.True(trade.IsForcedLiquidation);
        return trade;
    }

    [Fact]
    public void MidBarLiquidation_TheLowBelowTheFillIsAfterTheExit_ButTheHighStays()
    {
        // Open 100 is above the threshold 62.5; the Low 60 is reached after the liquidation fill at 62.5. The High 105 may have come before: it stays.
        BacktestTrade trade = RunLiquidation(SyntheticBars.Bar(2, 100m, 105m, 60m, 100m));

        Assert.Equal(62.5m, trade.ExitPrice);
        Assert.Equal(37.5m, trade.MaePrice);
        Assert.Equal(5m, trade.MfePrice);
    }

    [Fact]
    public void LiquidationAtTheOpenByAGap_HoldsNothingElseOfTheExitBar()
    {
        // The bar opens at 55, below the threshold: the fill is the Open, the later 120 and 50 are after the exit.
        BacktestTrade trade = RunLiquidation(SyntheticBars.Bar(2, 55m, 120m, 50m, 60m));

        Assert.Equal(55m, trade.ExitPrice);
        Assert.Equal(45m, trade.MaePrice);
        Assert.Equal(0m, trade.MfePrice);
    }
}
