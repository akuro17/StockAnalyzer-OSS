#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Tests.Backtest.Verification;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>
/// Fix T2 (the P1 correctness plan, finding R1): a pending MarketOnClose
/// Entry must obey the same "account must be Flat" admission rule as every other Entry order. All scenarios use flat
/// bars at 100, Q=1, InitialMarginRatio=0.3, no fees/slippage, so a Long opens with Cash=970, HeldMargin=30, Equity=1000.
/// </summary>
public class BacktestEngineMocAdmissionTests
{
    private static readonly DateTime Bar0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeInForce Gtc = new(true, -1);

    private static CandleData FlatBar(int dayOffset, decimal price) => new(Bar0.AddDays(dayOffset), price, price, price, price, 1000);

    private static ImmutableArray<CandleData> FlatBars(int count)
    {
        var builder = ImmutableArray.CreateBuilder<CandleData>(count);
        for (int i = 0; i < count; i++) builder.Add(FlatBar(i, 100m));
        return builder.MoveToImmutable();
    }

    private static BacktestInput MakeInput(ImmutableArray<CandleData> bars) =>
        new(bars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion, Bar0, Bar0.AddDays(bars.Length + 1), 0, 0);

    private static BacktestConfiguration MakeConfig() => new()
    {
        InitialCapital = 1000m,
        SizingModel = PositionSizingModel.FixedQuantity,
        SizingParameter = 1m,
        InitialMarginRatio = 0.3m,
        MaintenanceMarginRatio = 0.2m,
        LiquidationPenaltyRatio = 0m,
    };

    private static StrategyOrderRequest Req(SignalType type, OrderType orderType, decimal? limitPrice = null)
        => new(type, orderType, limitPrice, null, Gtc, "r");

    private static BacktestResult Run(int barCount, Dictionary<int, StrategyOrderRequest> entryScript, Dictionary<int, StrategyOrderRequest>? exitScript = null)
        => VerificationHarness.CreateEngine().Run(MakeInput(FlatBars(barCount)), MakeConfig(), new ScriptedStrategy(entryScript, exitScript));

    [Theory]
    [InlineData(SignalType.LongEntry, SignalType.ShortEntry, 1)]
    [InlineData(SignalType.ShortEntry, SignalType.LongEntry, -1)]
    public void MocOppositeEntry_WhileHolding_WithoutExit_StaysPending_AndNeverOverwritesThePosition(SignalType openSignal, SignalType oppositeSignal, int heldSign)
    {
        // bar0: open signal (Market) -> fills bar1 Open. bar1: opposite MOC Entry signalled with NO Exit; it may not fill on bar2.
        BacktestResult result = Run(3, new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(openSignal, OrderType.Market),
            [1] = Req(oppositeSignal, OrderType.MarketOnClose),
        });

        Assert.Empty(result.Trades);
        Assert.Single(result.Fills);
        EquityPoint last = result.EquityPoints[^1];
        Assert.Equal(970m, last.Cash);
        Assert.Equal(30m, last.HeldMargin);
        Assert.Equal(1000m, last.Equity);
        Assert.Equal(heldSign * 100m, last.MarketValue); // original position untouched: q * Close

        BacktestOrder mocEntry = result.Orders[^1];
        Assert.Equal(OrderType.MarketOnClose, mocEntry.Type);
        Assert.Equal(OrderStatus.Expired, mocEntry.Status);
        Assert.Equal(ExpiredReason.EndOfData, mocEntry.ExpiredReason);
    }

    [Fact]
    public void MocEntry_WithUnfilledPendingExit_StaysPending_UntilFlat()
    {
        // The Exit is a Limit that never touches, so the account never becomes Flat and the MOC Entry must keep waiting.
        BacktestResult result = Run(4,
            new Dictionary<int, StrategyOrderRequest>
            {
                [0] = Req(SignalType.LongEntry, OrderType.Market),
                [1] = Req(SignalType.ShortEntry, OrderType.MarketOnClose),
            },
            new Dictionary<int, StrategyOrderRequest>
            {
                [1] = Req(SignalType.LongExit, OrderType.Limit, limitPrice: 200m),
            });

        Assert.Empty(result.Trades);
        Assert.Single(result.Fills);
        Assert.Equal(1000m, result.EquityPoints[^1].Equity);
        Assert.Equal(100m, result.EquityPoints[^1].MarketValue);
    }

    [Theory]
    [InlineData(SignalType.LongEntry, SignalType.LongExit, SignalType.ShortEntry, TradeSide.Long, -100)]
    [InlineData(SignalType.ShortEntry, SignalType.ShortExit, SignalType.LongEntry, TradeSide.Short, 100)]
    public void MocReversalPair_ExitCommitsFirst_ThenEntryOpensTheOppositeSide(SignalType openSignal, SignalType exitSignal, SignalType oppositeSignal, TradeSide closedSide, int expectedMarketValue)
    {
        BacktestResult result = Run(3,
            new Dictionary<int, StrategyOrderRequest>
            {
                [0] = Req(openSignal, OrderType.Market),
                [1] = Req(oppositeSignal, OrderType.MarketOnClose),
            },
            new Dictionary<int, StrategyOrderRequest>
            {
                [1] = Req(exitSignal, OrderType.MarketOnClose),
            });

        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.Equal(closedSide, trade.Side);
        Assert.Equal(2, trade.ExitBar);
        Assert.Equal(3, result.Fills.Length); // first entry, then Exit, then the opposite Entry - in that order
        EquityPoint last = result.EquityPoints[^1];
        Assert.Equal(970m, last.Cash);
        Assert.Equal(30m, last.HeldMargin);
        Assert.Equal(1000m, last.Equity);
        Assert.Equal(expectedMarketValue, last.MarketValue);
        Assert.Equal(OrderStatus.Filled, result.Orders[^1].Status);
        Assert.Equal(OrderStatus.Filled, result.Orders[^2].Status);
    }

    [Fact]
    public void MocEntry_WhileFlat_StillFillsAtClose()
    {
        BacktestResult result = Run(2, new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.LongEntry, OrderType.MarketOnClose),
        });

        BacktestFill fill = Assert.Single(result.Fills);
        Assert.Equal(1, fill.BarIndex);
        Assert.Equal(100m, fill.Price);
        Assert.Equal(970m, result.EquityPoints[^1].Cash);
    }
}
