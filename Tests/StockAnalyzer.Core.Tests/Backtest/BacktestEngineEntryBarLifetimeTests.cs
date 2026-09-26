#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Tests.Backtest.Verification;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>
/// T7b (A1 (lifetime and reversal chronology), owner decision G2 = A). A position is live from its actual entry fill point
/// through Close: it can be force-liquidated on the residual path of its own entry bar, never on extrema that precede the fill. G2-1: a MarketOnClose
/// Entry already breached at its Close is carried to the next Open. G2-2: behind a mid-bar Exit only a Limit that is marketable at the Exit's reference
/// price fills the same bar. Margin accounts use E0=1000, Q=20, r=0.3, m=0.2, no fees/penalty unless stated: after a
/// Long/Short at 100, Cash 400, HeldMargin 600, Long liquidation price 62.5, Short liquidation price 125.
/// </summary>
public class BacktestEngineEntryBarLifetimeTests
{
    private static readonly DateTime Bar0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeInForce Gtc = new(true, -1);

    private static CandleData Bar(int dayOffset, decimal open, decimal high, decimal low, decimal close) => new(Bar0.AddDays(dayOffset), open, high, low, close, 1000);

    private static CandleData Flat(int dayOffset, decimal price) => Bar(dayOffset, price, price, price, price);

    private static BacktestConfiguration MarginConfig(decimal quantity = 20m, decimal slippageRatio = 0m, decimal penaltyRatio = 0m, decimal commissionFlat = 0m) => new()
    {
        InitialCapital = 1000m,
        SizingModel = PositionSizingModel.FixedQuantity,
        SizingParameter = quantity,
        InitialMarginRatio = 0.3m,
        MaintenanceMarginRatio = 0.2m,
        SlippageRatio = slippageRatio,
        LiquidationPenaltyRatio = penaltyRatio,
        CommissionFlat = commissionFlat,
    };

    /// <summary>Full-cash model (no leverage), so no scenario using it can ever reach a liquidation.</summary>
    private static BacktestConfiguration CashConfig(decimal slippageRatio = 0m) => new()
    {
        InitialCapital = 1000m,
        SizingModel = PositionSizingModel.FixedQuantity,
        SizingParameter = 1m,
        InitialMarginRatio = 1m,
        MaintenanceMarginRatio = 0.5m,
        SlippageRatio = slippageRatio,
    };

    private static StrategyOrderRequest Req(SignalType type, OrderType orderType = OrderType.Market, decimal? limitPrice = null, decimal? stopPrice = null)
        => new(type, orderType, limitPrice, stopPrice, Gtc, "r");

    private static BacktestResult Run(BacktestConfiguration config, ImmutableArray<CandleData> bars, Dictionary<int, StrategyOrderRequest> entryScript, Dictionary<int, StrategyOrderRequest>? exitScript = null)
    {
        var input = new BacktestInput(bars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion, Bar0, Bar0.AddDays(bars.Length + 1), 0, 0);
        return VerificationHarness.CreateEngine().Run(input, config, new ScriptedStrategy(entryScript, exitScript));
    }

    [Fact]
    public void Long_FilledAtOpen_LowBelowItsThresholdSameBar_IsForceClosedOnTheEntryBar()
    {
        // The Market Long fills at Open 100 (position 0); its threshold 62.5 is reached on High->Low, AFTER the fill.
        BacktestResult result = Run(MarginConfig(),
            ImmutableArray.Create(Flat(0, 100m), Bar(1, 100m, 100m, 60m, 100m), Flat(2, 100m)),
            new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry) });

        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.True(trade.IsForcedLiquidation);
        Assert.Equal(1, trade.EntryBar);
        Assert.Equal(1, trade.ExitBar);
        Assert.Equal(0, trade.HoldingBars);
        Assert.Equal(100m, trade.EntryPrice);
        Assert.Equal(62.5m, trade.ExitPrice);
        Assert.Equal(2, result.Fills.Length);
        Assert.Equal(RunStatus.Completed, result.Status); // Equity stays > 0: a partial loss-cut
        EquityPoint entryBarClose = result.EquityPoints[1];
        Assert.Equal(250m, entryBarClose.Cash); // 400 + 600 + 20 * (62.5 - 100)
        Assert.Equal(0m, entryBarClose.HeldMargin);
        Assert.Equal(0m, entryBarClose.MarketValue);
    }

    [Fact]
    public void Short_FilledAtOpen_HighAboveItsThresholdSameBar_IsForceClosedOnTheEntryBar()
    {
        BacktestResult result = Run(MarginConfig(),
            ImmutableArray.Create(Flat(0, 100m), Bar(1, 100m, 130m, 100m, 100m), Flat(2, 100m)),
            new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.ShortEntry) });

        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.Equal(TradeSide.Short, trade.Side);
        Assert.True(trade.IsForcedLiquidation);
        Assert.Equal(1, trade.ExitBar);
        Assert.Equal(125m, trade.ExitPrice);
        Assert.Equal(500m, result.EquityPoints[1].Cash); // 400 + 600 + 20 * (100 - 125)
    }

    [Fact]
    public void ExtremumBeforeTheEntryFill_NeverCountsAgainstTheNewPosition()
    {
        // Sell StopLimit entry (Stop 90, Limit 100) on O95 H130 L85 C102: it triggers going down and fills on the Low->Close recovery at 100,
        // AFTER the High of 130 that would breach the Short's threshold 125. That earlier High must not liquidate the new position.
        BacktestResult result = Run(MarginConfig(),
            ImmutableArray.Create(Flat(0, 100m), Bar(1, 95m, 130m, 85m, 102m)),
            new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.ShortEntry, OrderType.StopLimit, limitPrice: 100m, stopPrice: 90m) });

        Assert.Empty(result.Trades);
        BacktestFill fill = Assert.Single(result.Fills);
        Assert.Equal(100m, fill.Price);
        EquityPoint last = result.EquityPoints[^1];
        Assert.Equal(400m, last.Cash);
        Assert.Equal(600m, last.HeldMargin);
        Assert.Equal(960m, last.Equity); // 400 + 600 + (-20) * (102 - 100)
    }

    // MOC Long: 50% slippage makes the entry fill at 150 against a raw Close of 100 (Q = 15: Cash 325, HeldMargin 675), so the new position's
    // threshold (1250 / 12 = 104.17) already lies above the Close it opened at: breached at the Close state. G2-1: no same-bar liquidation - LiquidationPending.
    private static Dictionary<int, StrategyOrderRequest> MocLongEntry() => new() { [0] = Req(SignalType.LongEntry, OrderType.MarketOnClose) };
    private static Dictionary<int, StrategyOrderRequest> MocShortEntry() => new() { [0] = Req(SignalType.ShortEntry, OrderType.MarketOnClose) };

    [Fact]
    public void MarketOnCloseEntry_BreachedAtItsClose_IsNotLiquidatedOnThatBar_ButAtTheNextOpen()
    {
        BacktestResult result = Run(MarginConfig(quantity: 15m, slippageRatio: 0.5m),
            ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Flat(2, 100m)), MocLongEntry());

        EquityPoint entryBar = result.EquityPoints[1];
        Assert.Equal(325m, entryBar.Cash);
        Assert.Equal(675m, entryBar.HeldMargin); // still open at the end of its entry bar

        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.True(trade.IsForcedLiquidation);
        Assert.Equal(1, trade.EntryBar);
        Assert.Equal(2, trade.ExitBar);
        Assert.Equal(150m, trade.EntryPrice);
        Assert.Equal(100m, trade.ExitPrice); // the next Open, not the 104.17 threshold
        Assert.Equal(250m, result.EquityPoints[2].Cash); // 325 + 675 + 15 * (100 - 150)
        Assert.Equal(RunStatus.Completed, result.Status);
    }

    [Fact]
    public void MarketOnCloseLong_GapDownAtTheNextOpen_LiquidatesAtThatOpenNeverAtTheThreshold()
    {
        BacktestResult result = Run(MarginConfig(quantity: 15m, slippageRatio: 0.5m),
            ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Flat(2, 90m)), MocLongEntry());

        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.Equal(90m, trade.ExitPrice); // a threshold fill at 104.17 would be a better price than the market ever offered
        Assert.Equal(100m, result.EquityPoints[2].Cash); // 1000 + 15 * (90 - 150)
    }

    [Fact]
    public void MarketOnCloseShort_GapUpAtTheNextOpen_LiquidatesAtThatOpen()
    {
        // 50% slippage: the Short sells at 50 (Cash 775, HeldMargin 225); threshold 1750 / 18 = 97.22 is below the Close 100 it opened at.
        BacktestResult result = Run(MarginConfig(quantity: 15m, slippageRatio: 0.5m),
            ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Flat(2, 110m)), MocShortEntry());

        Assert.Equal(225m, result.EquityPoints[1].HeldMargin);
        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.Equal(TradeSide.Short, trade.Side);
        Assert.True(trade.IsForcedLiquidation);
        Assert.Equal(2, trade.ExitBar);
        Assert.Equal(110m, trade.ExitPrice);
        Assert.Equal(100m, result.EquityPoints[2].Cash); // 1000 + 15 * (50 - 110)
    }

    [Fact]
    public void DeferredLiquidation_AppliesThePenaltyToTheOpen_AndTheUsualCommission()
    {
        BacktestResult result = Run(MarginConfig(quantity: 15m, slippageRatio: 0.5m, penaltyRatio: 0.1m, commissionFlat: 2m),
            ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Flat(2, 100m)), MocLongEntry());

        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.Equal(90m, trade.ExitPrice); // Open 100 * (1 - 0.1)
        BacktestFill liquidationFill = result.Fills[^1];
        Assert.Equal(2m, liquidationFill.Commission);
        Assert.Equal(150m, liquidationFill.SlippageAmount); // |90 - 100| * 15
    }

    [Fact]
    public void DeferredLiquidation_LeavingTheAccountInsolvent_StopsTheRunAndExpiresTheSupersededExit()
    {
        // Open 80: Cash = 1000 + 15 * (80 - 150) = -50 <= 0. A pending Exit submitted at the MOC bar's Close is superseded: Expired(Insolvency).
        BacktestResult result = Run(MarginConfig(quantity: 15m, slippageRatio: 0.5m),
            ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Flat(2, 80m), Flat(3, 80m)), MocLongEntry(),
            new Dictionary<int, StrategyOrderRequest> { [1] = Req(SignalType.LongExit, OrderType.Limit, limitPrice: 200m) });

        Assert.Equal(RunStatus.Insolvent, result.Status);
        Assert.Equal(80m, Assert.Single(result.Trades).ExitPrice);
        Assert.Equal(3, result.EquityPoints.Length); // the run stops on the liquidation bar
        BacktestOrder exit = result.Orders[1];
        Assert.Equal(OrderStatus.Expired, exit.Status);
        Assert.Equal(ExpiredReason.Insolvency, exit.ExpiredReason);
    }

    [Fact]
    public void DeferredLiquidation_LeavingTheAccountSolvent_CancelsTheSupersededExit()
    {
        BacktestResult result = Run(MarginConfig(quantity: 15m, slippageRatio: 0.5m),
            ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Flat(2, 100m), Flat(3, 100m)), MocLongEntry(),
            new Dictionary<int, StrategyOrderRequest> { [1] = Req(SignalType.LongExit, OrderType.Limit, limitPrice: 200m) });

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Single(result.Trades);
        Assert.Equal(OrderStatus.Cancelled, result.Orders[1].Status);
    }

    [Fact]
    public void MarketOnCloseEntry_BreachedAtTheFinalClose_HasNoNextOpen_SoThePositionStaysOpen()
    {
        BacktestResult result = Run(MarginConfig(quantity: 15m, slippageRatio: 0.5m),
            ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m)), MocLongEntry());

        Assert.Empty(result.Trades);
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(675m, result.EquityPoints[^1].HeldMargin);
    }

    [Fact]
    public void ReversalMarketEntry_WhoseOnlyEventIsTheOpen_WaitsWhenTheExitFillsLaterInTheBar()
    {
        // Bar2 (O100 H110 L95 C102): the Exit Limit 105 fills at position 0.5. The Market Short Entry's only event is the Open (position 0), which
        // is earlier than the Exit, so it may not be filled retroactively: it stays pending and fills at bar3's Open (98).
        BacktestResult result = Run(CashConfig(),
            ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Bar(2, 100m, 110m, 95m, 102m), Flat(3, 98m)),
            new Dictionary<int, StrategyOrderRequest>
            {
                [0] = Req(SignalType.LongEntry),
                [1] = Req(SignalType.ShortEntry),
            },
            new Dictionary<int, StrategyOrderRequest> { [1] = Req(SignalType.LongExit, OrderType.Limit, limitPrice: 105m) });

        BacktestTrade closedLong = Assert.Single(result.Trades);
        Assert.Equal(105m, closedLong.ExitPrice);
        Assert.Equal(2, closedLong.ExitBar);

        EquityPoint exitBar = result.EquityPoints[2];
        Assert.Equal(0m, exitBar.MarketValue); // Flat at the end of bar2: the Entry did not fill yet
        Assert.Equal(1005m, exitBar.Equity);

        Assert.Equal(3, result.Fills.Length);
        BacktestFill shortEntryFill = result.Fills[2];
        Assert.Equal(3, shortEntryFill.BarIndex);
        Assert.Equal(98m, shortEntryFill.Price);
        Assert.Equal(OrderStatus.Filled, result.Orders[1].Status);
    }

    // Long held from bar1's Open; bar2 (O100 H110 L95 C102): the Exit Limit Sell 105 fills on Open->High at position 0.5, so the Exit's reference price is 105.
    private static Dictionary<int, StrategyOrderRequest> LongExitLimit105() => new() { [1] = Req(SignalType.LongExit, OrderType.Limit, limitPrice: 105m) };

    private static BacktestResult RunReversalIntoShort(StrategyOrderRequest shortEntry, decimal slippageRatio = 0m, decimal nextOpen = 100m)
        => Run(CashConfig(slippageRatio),
            ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Bar(2, 100m, 110m, 95m, 102m), Flat(3, nextOpen)),
            new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry), [1] = shortEntry },
            LongExitLimit105());

    [Fact]
    public void ReversalLimitEntry_MarketableAtTheExitReferencePrice_FillsTheSameBarAtThatReference()
    {
        // Sell Limit 102 <= Ref 105: marketable, filled at the Exit's reference 105 (better than the limit).
        BacktestResult result = RunReversalIntoShort(Req(SignalType.ShortEntry, OrderType.Limit, limitPrice: 102m));

        Assert.Equal(3, result.Fills.Length);
        BacktestFill entryFill = result.Fills[2];
        Assert.Equal(2, entryFill.BarIndex);
        Assert.Equal(105m, entryFill.Price);
    }

    [Fact]
    public void ReversalSellLimitEntry_ThroughTheCostModel_NeverFillsBelowItsLimit()
    {
        // Ref 105, slippage 1%: 105 * 0.99 = 103.95 would breach the Sell Limit 104.5; the limit invariant clamps the fill to 104.5.
        BacktestResult result = RunReversalIntoShort(Req(SignalType.ShortEntry, OrderType.Limit, limitPrice: 104.5m), slippageRatio: 0.01m);

        BacktestFill entryFill = result.Fills[2];
        Assert.Equal(2, entryFill.BarIndex);
        Assert.Equal(104.5m, entryFill.Price);
    }

    [Fact]
    public void ReversalBuyLimitEntry_MarketableAtTheExitReferencePrice_NeverFillsAboveItsLimit()
    {
        // Short held; bar2 (O100 H110 L90 C100): the Exit Buy Limit 95 fills on High->Low at 95. Buy Limit 96 >= Ref 95: marketable; 95 * 1.02 = 96.9 is clamped to 96.
        BacktestResult result = Run(CashConfig(slippageRatio: 0.02m),
            ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Bar(2, 100m, 110m, 90m, 100m)),
            new Dictionary<int, StrategyOrderRequest>
            {
                [0] = Req(SignalType.ShortEntry),
                [1] = Req(SignalType.LongEntry, OrderType.Limit, limitPrice: 96m),
            },
            new Dictionary<int, StrategyOrderRequest> { [1] = Req(SignalType.ShortExit, OrderType.Limit, limitPrice: 95m) });

        Assert.Equal(3, result.Fills.Length);
        Assert.Equal(95m, result.Trades[0].ExitPrice);
        BacktestFill entryFill = result.Fills[2];
        Assert.Equal(2, entryFill.BarIndex);
        Assert.Equal(96m, entryFill.Price);
    }

    [Fact]
    public void ReversalLimitEntry_NotMarketableAtTheReference_WaitsEvenThoughALaterHighWouldTouchIt()
    {
        // Sell Limit 108 > Ref 105 and the bar's High 110 comes after the Exit - the High/Low must not be reused for the new order: it waits and, at the
        // next bar (Open 109 >= 108), fills as an ordinary gap fill at that Open.
        BacktestResult result = RunReversalIntoShort(Req(SignalType.ShortEntry, OrderType.Limit, limitPrice: 108m), nextOpen: 109m);

        Assert.Equal(3, result.Fills.Length);
        BacktestFill entryFill = result.Fills[2];
        Assert.Equal(3, entryFill.BarIndex);
        Assert.Equal(109m, entryFill.Price);
    }

    [Fact]
    public void ReversalStopEntry_WaitsForALaterBar_EvenWhenTheStopLiesOnTheResidualPath()
    {
        // Sell Stop 101 (101 is crossed again on High->Low after the Exit): Stop/StopLimit follow their own trigger rules and are basically pending -
        // no same-bar fill. On bar3 (Open 100 <= 101) the ordinary gap rule fills it at that Open.
        BacktestResult result = RunReversalIntoShort(Req(SignalType.ShortEntry, OrderType.Stop, stopPrice: 101m));

        Assert.Equal(3, result.Fills.Length);
        BacktestFill entryFill = result.Fills[2];
        Assert.Equal(3, entryFill.BarIndex);
        Assert.Equal(100m, entryFill.Price);
    }

    [Fact]
    public void ReversalStopLimitEntry_WaitsForALaterBar_AndIsNotActivatedByTheEarlierPath()
    {
        // Sell StopLimit (Stop 101, Limit 100): nothing is evaluated on bar2 - it is not activated by the bar's earlier extrema. On bar3 the Open 100 is
        // at/below the Stop 101 and already at the Limit 100, so it fills at that Open.
        BacktestResult result = RunReversalIntoShort(Req(SignalType.ShortEntry, OrderType.StopLimit, limitPrice: 100m, stopPrice: 101m));

        Assert.Equal(3, result.Fills.Length);
        BacktestFill entryFill = result.Fills[2];
        Assert.Equal(3, entryFill.BarIndex);
        Assert.Equal(100m, entryFill.Price);
    }

    [Fact]
    public void ReversalLimitEntry_WhenTheExitFillsAtTheOpen_ScansTheWholeBarAsBefore()
    {
        // The Market Exit fills at the Open (position 0), so every later High/Low is genuinely after it: the Sell Limit 105 (not marketable at 100)
        // fills on Open->High at 105 on the same bar - identical to the account having been Flat.
        BacktestResult result = Run(CashConfig(),
            ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Bar(2, 100m, 110m, 95m, 102m)),
            new Dictionary<int, StrategyOrderRequest>
            {
                [0] = Req(SignalType.LongEntry),
                [1] = Req(SignalType.ShortEntry, OrderType.Limit, limitPrice: 105m),
            },
            new Dictionary<int, StrategyOrderRequest> { [1] = Req(SignalType.LongExit) });

        BacktestFill entryFill = result.Fills[2];
        Assert.Equal(2, entryFill.BarIndex);
        Assert.Equal(105m, entryFill.Price);
    }

    [Fact]
    public void ReversalPair_BothFillingAtOpen_ThenTheNewShortIsLiquidated_ProducesThreeFillsAndTwoTradesInOneBar()
    {
        // bar2 Open: Market LongExit (realized 0) then Market ShortEntry at 100 - both at position 0, Exit first. The new Short's threshold 125 is
        // reached on Open->High (High 130). Exit + Entry + liquidation of the new position = the per-bar maximum of 3 fills and 2 trades.
        BacktestResult result = Run(MarginConfig(),
            ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Bar(2, 100m, 130m, 100m, 120m)),
            new Dictionary<int, StrategyOrderRequest>
            {
                [0] = Req(SignalType.LongEntry),
                [1] = Req(SignalType.ShortEntry),
            },
            new Dictionary<int, StrategyOrderRequest> { [1] = Req(SignalType.LongExit) });

        Assert.Equal(4, result.Fills.Length); // long entry (bar1) + three on bar2
        Assert.Equal(3, result.Fills.Count(fill => fill.BarIndex == 2));
        Assert.Equal(2, result.Trades.Length);
        BacktestTrade forced = result.Trades[1];
        Assert.Equal(TradeSide.Short, forced.Side);
        Assert.True(forced.IsForcedLiquidation);
        Assert.Equal(2, forced.EntryBar);
        Assert.Equal(2, forced.ExitBar);
        Assert.Equal(500m, result.EquityPoints[2].Cash); // 400 + 600 + 20 * (100 - 125)
    }
}
