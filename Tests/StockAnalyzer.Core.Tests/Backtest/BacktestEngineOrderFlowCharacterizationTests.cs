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
/// Run-level characterization of order-flow behavior that is already correct and must survive later
/// correctness work (the P1 characterization plan). Every expectation is
/// hand-computed with zero fees/slippage, InitialMarginRatio=1 and MaintenanceMarginRatio=0.5, so no
/// scenario here is ever near a margin breach; scenarios that depend on a known-defective or
/// still-undecided rule (MOC over an open position, StopLimit-vs-liquidation priority, entry-bar
/// liquidation) are deliberately absent.
/// </summary>
public class BacktestEngineOrderFlowCharacterizationTests
{
    private static readonly DateTime Bar0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeInForce Gtc = new(true, -1);

    /// <summary>Entry-type slot plus Exit-type slot: the engine holds at most this many pending orders per bar.</summary>
    private const int PendingSlotsPerBar = 2;

    /// <summary>Exit + Entry + liquidation of the position the Entry opened (A1 (lifetime and reversal chronology)).</summary>
    private const int MaxFillsPerBar = 3;

    /// <summary>The Exit's trade plus the trade of the position the Entry opened and the same bar liquidated.</summary>
    private const int MaxTradesPerBar = 2;

    private static CandleData Bar(int dayOffset, decimal open, decimal high, decimal low, decimal close, long volume = 1000)
        => new(Bar0.AddDays(dayOffset), open, high, low, close, volume);

    private static CandleData FlatBar(int dayOffset, decimal price) => Bar(dayOffset, price, price, price, price);

    private static BacktestInput MakeInput(ImmutableArray<CandleData> bars)
        => new(bars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion, Bar0, Bar0.AddDays(bars.Length + 1), 0, 0);

    private static BacktestConfiguration MakeConfig() => new()
    {
        InitialCapital = 1000m,
        SizingModel = PositionSizingModel.FixedQuantity,
        SizingParameter = 1m,
        InitialMarginRatio = 1m,
        MaintenanceMarginRatio = 0.5m,
    };

    private static StrategyOrderRequest Req(SignalType type, OrderType orderType, decimal? limitPrice = null, decimal? stopPrice = null)
        => new(type, orderType, limitPrice, stopPrice, Gtc, "r");

    /// <summary>Submits an opposite-side Entry (Evaluate) plus an Exit of the held side (EvaluateExit) on every bar once a position exists.</summary>
    private sealed class AlwaysReversingStrategy : IBacktestStrategy
    {
        public string Name => "AlwaysReversing";
        public IReadOnlyList<StrategyIndicatorRequest> GetRequiredIndicators() => Array.Empty<StrategyIndicatorRequest>();

        public StrategyOrderRequest? Evaluate(StrategyContext context) => context.PositionSide switch
        {
            null => Req(SignalType.LongEntry, OrderType.Market),
            TradeSide.Long => Req(SignalType.ShortEntry, OrderType.Market),
            _ => Req(SignalType.LongEntry, OrderType.Market),
        };

        public StrategyOrderRequest? EvaluateExit(StrategyContext context) => context.PositionSide switch
        {
            TradeSide.Long => Req(SignalType.LongExit, OrderType.Market),
            TradeSide.Short => Req(SignalType.ShortExit, OrderType.Market),
            _ => null,
        };
    }

    private static BacktestResult RunScript(ImmutableArray<CandleData> bars, Dictionary<int, StrategyOrderRequest> script)
        => VerificationHarness.CreateEngine().Run(MakeInput(bars), MakeConfig(), new ScriptedStrategy(script));

    [Fact]
    public void RunLevel_LimitBuy_LowReachesLimit_FillsAtLimit()
    {
        BacktestResult result = RunScript(
            ImmutableArray.Create(FlatBar(0, 100m), Bar(1, 100m, 101m, 94m, 99m)),
            new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry, OrderType.Limit, limitPrice: 95m) });

        BacktestFill fill = Assert.Single(result.Fills);
        Assert.Equal(1, fill.BarIndex);
        Assert.Equal(95m, fill.Price);
        Assert.Equal(OrderStatus.Filled, Assert.Single(result.Orders).Status);
        EquityPoint last = result.EquityPoints[^1];
        Assert.Equal(905m, last.Cash);
        Assert.Equal(95m, last.HeldMargin);
        Assert.Equal(1004m, last.Equity); // 905 + 95 + 1 * (99 - 95)
    }

    [Fact]
    public void RunLevel_LimitBuy_GapOpenBelowLimit_FillsAtOpen()
    {
        BacktestResult result = RunScript(
            ImmutableArray.Create(FlatBar(0, 100m), Bar(1, 90m, 92m, 89m, 91m)),
            new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry, OrderType.Limit, limitPrice: 95m) });

        BacktestFill fill = Assert.Single(result.Fills);
        Assert.Equal(90m, fill.Price);
        Assert.Equal(1001m, result.EquityPoints[^1].Equity); // 910 + 90 + 1 * (91 - 90)
    }

    [Fact]
    public void RunLevel_LimitBuy_LowStaysAboveLimit_NeverFills_ExpiresEndOfData()
    {
        BacktestResult result = RunScript(
            ImmutableArray.Create(FlatBar(0, 100m), Bar(1, 100m, 101m, 96m, 99m)),
            new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry, OrderType.Limit, limitPrice: 95m) });

        Assert.Empty(result.Fills);
        BacktestOrder order = Assert.Single(result.Orders);
        Assert.Equal(OrderStatus.Expired, order.Status);
        Assert.Equal(ExpiredReason.EndOfData, order.ExpiredReason);
        Assert.Equal(1000m, result.EquityPoints[^1].Equity);
    }

    [Fact]
    public void RunLevel_StopBuy_GapOpenAboveStop_FillsAtOpenWithoutClamp()
    {
        BacktestResult result = RunScript(
            ImmutableArray.Create(FlatBar(0, 100m), Bar(1, 110m, 112m, 109m, 111m)),
            new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry, OrderType.Stop, stopPrice: 105m) });

        BacktestFill fill = Assert.Single(result.Fills);
        Assert.Equal(110m, fill.Price);
        Assert.Equal(1001m, result.EquityPoints[^1].Equity); // 890 + 110 + 1 * (111 - 110)
    }

    [Fact]
    public void RunLevel_BuyStopLimit_TriggersUpwardThenFillsAtLimitOnRemainingDrop()
    {
        // O95 H105 L85 C92: Stop=100 is crossed on the Open->High leg, then the still-unconsumed High->Low leg reaches Limit=90.
        BacktestResult result = RunScript(
            ImmutableArray.Create(FlatBar(0, 100m), Bar(1, 95m, 105m, 85m, 92m)),
            new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry, OrderType.StopLimit, limitPrice: 90m, stopPrice: 100m) });

        BacktestFill fill = Assert.Single(result.Fills);
        Assert.Equal(90m, fill.Price);
        BacktestOrder order = Assert.Single(result.Orders);
        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.True(order.StopActivated);
        Assert.Equal(1002m, result.EquityPoints[^1].Equity); // 910 + 90 + 1 * (92 - 90)
    }

    [Fact]
    public void RunLevel_SellStopLimit_ActivatesWithoutFillOnFirstBar_ActivationPersistsAndFillsAtLimitNextBar()
    {
        // Bar2 (O95 H105 L85 C92) triggers Stop=90 partway down the High->Low leg; High is already consumed and
        // Close=92 never recovers to Limit=100, so the order only activates. Bar3 (O93 H101 L92 C99) reaches
        // Limit=100 on its Open->High leg - which can only fill if StopActivated was persisted from bar2
        // (a fresh Stop=90 scan on bar3 would find no trigger, its Low being 92).
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            FlatBar(0, 100m),
            FlatBar(1, 100m),
            Bar(2, 95m, 105m, 85m, 92m),
            Bar(3, 93m, 101m, 92m, 99m));

        BacktestResult result = RunScript(bars, new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.LongEntry, OrderType.Market),
            [1] = Req(SignalType.LongExit, OrderType.StopLimit, limitPrice: 100m, stopPrice: 90m),
        });

        BacktestOrder exitOrder = result.Orders[^1];
        Assert.Equal(OrderSide.Sell, exitOrder.Side);
        Assert.Equal(OrderStatus.Filled, exitOrder.Status);
        Assert.True(exitOrder.StopActivated);
        BacktestFill exitFill = result.Fills[^1];
        Assert.Equal(3, exitFill.BarIndex);
        Assert.Equal(100m, exitFill.Price);
        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.Equal(0m, trade.ClosedGross); // entry 100, exit 100
        Assert.Equal(1000m, result.EquityPoints[^1].Equity);
    }

    [Fact]
    public void RunLevel_SignalOnFinalBar_OrderRecordedThenExpiredEndOfData_NoFill()
    {
        BacktestResult result = RunScript(
            ImmutableArray.Create(FlatBar(0, 100m), FlatBar(1, 100m), FlatBar(2, 100m)),
            new Dictionary<int, StrategyOrderRequest> { [2] = Req(SignalType.LongEntry, OrderType.Market) });

        Assert.Equal(RunStatus.Completed, result.Status);
        BacktestOrder order = Assert.Single(result.Orders);
        Assert.Equal(2, order.SubmittedBar);
        Assert.Equal(3, order.EarliestFillBar);
        Assert.Equal(OrderStatus.Expired, order.Status);
        Assert.Equal(ExpiredReason.EndOfData, order.ExpiredReason);
        Assert.Empty(result.Fills);
        Assert.Single(result.Signals);
        Assert.All(result.EquityPoints, point => Assert.Equal(1000m, point.Cash));
    }

    [Fact]
    public void Run_NullArguments_ThrowArgumentNullExceptionNamingTheParameter()
    {
        BacktestEngine engine = VerificationHarness.CreateEngine();
        BacktestInput input = MakeInput(ImmutableArray.Create(FlatBar(0, 100m)));
        BacktestConfiguration config = MakeConfig();
        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>());

        Assert.Equal("input", Assert.Throws<ArgumentNullException>(() => engine.Run(null!, config, strategy)).ParamName);
        Assert.Equal("configuration", Assert.Throws<ArgumentNullException>(() => engine.Run(input, null!, strategy)).ParamName);
        Assert.Equal("strategy", Assert.Throws<ArgumentNullException>(() => engine.Run(input, config, null!)).ParamName);
    }

    [Fact]
    public void SaturatedAlternatingReversals_StayWithinDualSlotBounds_WithSequentialIdsAndExitBeforeEntry()
    {
        const int barCount = 20;
        var bars = ImmutableArray.CreateBuilder<CandleData>(barCount);
        for (int i = 0; i < barCount; i++) bars.Add(FlatBar(i, 100m));

        BacktestResult result = VerificationHarness.CreateEngine().Run(MakeInput(bars.MoveToImmutable()), MakeConfig(), new AlwaysReversingStrategy());

        Assert.Equal(RunStatus.Completed, result.Status);

        // Documented upper bounds of the dual-slot model with entry-bar liquidation.
        Assert.True(result.Orders.Length <= PendingSlotsPerBar * barCount);
        Assert.True(result.Fills.Length <= MaxFillsPerBar * barCount);
        Assert.True(result.Trades.Length <= MaxTradesPerBar * barCount);
        Assert.Equal(barCount, result.EquityPoints.Length);

        // Exact counts for this saturated shape: bar0 submits 1 order, bars 1..N-1 submit 2 each; the first entry
        // fills on bar1 and every later bar fills an Exit plus an Entry; the last bar's two orders expire unfilled.
        Assert.Equal((PendingSlotsPerBar * (barCount - 1)) + 1, result.Orders.Length);
        Assert.Equal((PendingSlotsPerBar * (barCount - 2)) + 1, result.Fills.Length);
        Assert.Equal(barCount - 2, result.Trades.Length);

        for (int k = 0; k < result.Orders.Length; k++) Assert.Equal(k + 1L, result.Orders[k].OrderId);
        for (int k = 0; k < result.Fills.Length; k++) Assert.Equal(k + 1L, result.Fills[k].FillId);
        for (int k = 0; k < result.Trades.Length; k++) Assert.Equal(k + 1L, result.Trades[k].TradeId);

        // Bar i >= 1 submits the opposite Entry (order id 2i) then the Exit (order id 2i + 1); on bar i + 1 the Exit fill precedes the Entry fill.
        for (int bar = 2; bar < barCount; bar++)
        {
            int firstFill = (PendingSlotsPerBar * (bar - 2)) + 1;
            long submittedBar = bar - 1;
            Assert.Equal(bar, result.Fills[firstFill].BarIndex);
            Assert.Equal(bar, result.Fills[firstFill + 1].BarIndex);
            Assert.Equal((PendingSlotsPerBar * submittedBar) + 1, result.Fills[firstFill].OrderId);
            Assert.Equal(PendingSlotsPerBar * submittedBar, result.Fills[firstFill + 1].OrderId);
        }

        for (int k = 0; k < result.Trades.Length; k++)
        {
            BacktestTrade trade = result.Trades[k];
            Assert.Equal(k % 2 == 0 ? TradeSide.Long : TradeSide.Short, trade.Side);
            Assert.Equal(k + 1, trade.EntryBar);
            Assert.Equal(k + 2, trade.ExitBar);
            Assert.Equal(0m, trade.ClosedNet);
        }

        for (int k = 0; k < result.Orders.Length; k++)
        {
            bool isFinalBarPair = k >= result.Orders.Length - PendingSlotsPerBar;
            Assert.Equal(isFinalBarPair ? OrderStatus.Expired : OrderStatus.Filled, result.Orders[k].Status);
        }

        Assert.All(result.EquityPoints, point => Assert.Equal(1000m, point.Equity));
    }
}
