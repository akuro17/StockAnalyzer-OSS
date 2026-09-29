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
/// T9 (the P1 acceptance-coverage plan), original acceptance cases 10, 11, 18 and 20: the independent ledger (which never calls the
/// production account math) must reproduce every equity point exactly for the events the P1 correctness work introduced - entry-bar and gap liquidation, a
/// MarketOnClose position liquidated at the next Open, a marketable-limit reversal, a three-fill bar - plus the single-bar and end-of-data edge runs.
/// E0=1000, Q=20, r=0.3, m=0.2 (Long threshold 62.5, Short threshold 125) unless stated.
/// </summary>
public class BacktestAcceptanceLedgerCoverageTests
{
    private static CandleData Flat(int day, decimal price) => SyntheticBars.Bar(day, price, price, price, price);

    private static StrategyOrderRequest Req(SignalType type, OrderType orderType = OrderType.Market, decimal? limit = null)
        => VerificationHarness.Req(type, orderType, limitPrice: limit);

    private static BacktestConfiguration Margin(decimal quantity = 20m, decimal slippage = 0m, decimal penalty = 0m)
        => VerificationHarness.MakeConfig(initialCapital: 1000m, sizingParameter: quantity, initialMarginRatio: 0.3m, maintenanceMarginRatio: 0.2m, slippageRatio: slippage, liquidationPenaltyRatio: penalty);

    private static BacktestResult RunChecked(BacktestConfiguration config, ImmutableArray<CandleData> bars,
        Dictionary<int, StrategyOrderRequest> primary, Dictionary<int, StrategyOrderRequest>? exit = null)
    {
        BacktestInput input = VerificationHarness.MakeInput(bars);
        BacktestResult result = VerificationHarness.Run(input, config, new ScriptedStrategy(primary, exit));
        LedgerAssertions.AllInvariants(input, config, result);
        return result;
    }

    [Fact]
    public void EntryBarLiquidation_MatchesTheIndependentLedger()
    {
        BacktestResult result = RunChecked(Margin(), ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), SyntheticBars.Bar(2, 100m, 100m, 60m, 100m), Flat(3, 100m)),
            new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry) });

        Assert.True(Assert.Single(result.Trades).IsForcedLiquidation);
    }

    [Fact]
    public void GapLiquidations_BothSides_MatchTheIndependentLedger()
    {
        BacktestResult longGap = RunChecked(Margin(penalty: 0.02m), ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Flat(2, 55m), Flat(3, 55m)),
            new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry) });
        BacktestResult shortGap = RunChecked(Margin(penalty: 0.02m), ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Flat(2, 130m), Flat(3, 130m)),
            new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.ShortEntry) });

        Assert.True(Assert.Single(longGap.Trades).IsForcedLiquidation);
        Assert.True(Assert.Single(shortGap.Trades).IsForcedLiquidation);
    }

    [Fact]
    public void InsolventGapLiquidation_MatchesTheIndependentLedger()
    {
        BacktestResult result = RunChecked(Margin(), ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Flat(2, 45m), Flat(3, 45m)),
            new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry) });

        Assert.Equal(RunStatus.Insolvent, result.Status);
    }

    [Fact]
    public void MarketOnCloseEntry_LiquidatedAtTheNextOpen_MatchesTheIndependentLedger()
    {
        BacktestResult result = RunChecked(Margin(quantity: 15m, slippage: 0.5m), ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Flat(2, 90m), Flat(3, 90m)),
            new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry, OrderType.MarketOnClose) });

        Assert.Equal(2, Assert.Single(result.Trades).ExitBar);
    }

    [Fact]
    public void MarketableLimitReversal_MatchesTheIndependentLedger()
    {
        // Long from bar 1's Open; bar 2 (O100 H110 L95 C102): Exit Limit 105 fills at 0.5, the Sell Limit 102 Entry is marketable at 105 and fills the same bar.
        BacktestResult result = RunChecked(VerificationHarness.MakeConfig(initialCapital: 1000m, sizingParameter: 1m),
            ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), SyntheticBars.Bar(2, 100m, 110m, 95m, 102m), Flat(3, 102m)),
            new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry), [1] = Req(SignalType.ShortEntry, OrderType.Limit, limit: 102m) },
            new Dictionary<int, StrategyOrderRequest> { [1] = Req(SignalType.LongExit, OrderType.Limit, limit: 105m) });

        Assert.Equal(3, result.Fills.Length);
        Assert.Equal(2, result.Fills[2].BarIndex);
    }

    [Fact]
    public void ThreeFillBar_ExitEntryAndLiquidationOfTheNewPosition_MatchesTheIndependentLedger()
    {
        BacktestResult result = RunChecked(Margin(), ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), SyntheticBars.Bar(2, 100m, 130m, 100m, 120m), Flat(3, 120m)),
            new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry), [1] = Req(SignalType.ShortEntry) },
            new Dictionary<int, StrategyOrderRequest> { [1] = Req(SignalType.LongExit) });

        Assert.Equal(2, result.Trades.Length);
        Assert.Equal(4, result.Fills.Length); // the bar-1 entry plus the three fills of bar 2
    }

    [Fact]
    public void SingleBarRun_RecordsTheSignalledOrder_ExpiresItAtEndOfData_AndNeverFills()
    {
        BacktestResult result = RunChecked(Margin(), ImmutableArray.Create(Flat(0, 100m)),
            new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry) });

        BacktestOrder order = Assert.Single(result.Orders);
        Assert.Equal(ExpiredReason.EndOfData, order.ExpiredReason);
        Assert.Empty(result.Fills);
        EquityPoint only = Assert.Single(result.EquityPoints);
        Assert.Equal(1000m, only.Equity);
    }

    [Fact]
    public void MarketOnCloseOrders_StillWaitingAtTheEndOfData_ExpireWithoutFilling()
    {
        // Long held from bar 1's Open. At bar 1's Close a MOC Short Entry waits behind the open position (no Exit is pending), so it can never fill: it
        // stays Submitted until the data ends and then expires with EndOfData while the position stays open and marked.
        BacktestResult result = RunChecked(VerificationHarness.MakeConfig(initialCapital: 1000m, sizingParameter: 1m),
            ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Flat(2, 101m), Flat(3, 102m)),
            new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry), [1] = Req(SignalType.ShortEntry, OrderType.MarketOnClose) });

        Assert.Empty(result.Trades);
        Assert.Equal(2, result.Orders.Length);
        Assert.Equal(ExpiredReason.EndOfData, result.Orders[1].ExpiredReason);
        Assert.Single(result.Fills);
        Assert.Equal(102m, result.EquityPoints[^1].MarketValue);
    }
}
