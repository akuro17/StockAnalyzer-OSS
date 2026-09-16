using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>
/// Task #8: the named acceptance suite required by Y:\0915 Backtesting\01_P1_SimulationEngine.md section 6
/// and its section 1.5.9 margin/short additions (see Y:\Temp\sa_ai_context_BacktestEngine_P1.md). Each
/// [Fact] name below is the exact required method name per the spec's "do not rename" instruction.
///
/// Coverage note: five required names already exist verbatim elsewhere in this folder and are NOT
/// duplicated here (duplicating an identical scenario under an identical name in a second file would be
/// pure redundancy, not additional coverage): `LimitBuy_BarLowBelowLimit_FillsAtLimit`,
/// `LimitBuy_BarLowAboveLimit_NoFill`, `StopBuy_GapOpen_FillsAtOpen` (all three in
/// OrderFillEvaluatorTests.cs), `InvalidOHLC_HighBelowLow_Throws` (BacktestInputTests.cs), and
/// `EmptyBars_ReturnsEmptyWithConfig` (BacktestEngineTests.cs).
///
/// `FutureSuffixAppend_PrefixSignalUnchanged` from the source doc's section 6 list is intentionally
/// SKIPPED: it has no corresponding description anywhere in the spec document (section 1-5.11), and the
/// user explicitly chose to skip it (2026-09-16) rather than have its intent guessed, per CLAUDE.md's
/// Specification Authority rule against inferring unspecified behavior.
/// </summary>
public class BacktestAcceptanceTests
{
    private static readonly DateTime Bar0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeInForce Gtc = new(true, -1);

    private static CandleData Bar(int dayOffset, decimal open, decimal high, decimal low, decimal close, long volume = 1000)
        => new(Bar0.AddDays(dayOffset), open, high, low, close, volume);

    private static BacktestInput MakeInput(ImmutableArray<CandleData> bars, int tradingStartIndex = 0)
        => new(bars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion, Bar0, Bar0.AddDays(bars.Length + 1), 0, tradingStartIndex);

    private static BacktestConfiguration MakeConfig(
        decimal initialCapital = 1000m, decimal commissionFlat = 0m, decimal commissionPerUnit = 0m,
        decimal slippageRatio = 0m, PositionSizingModel sizingModel = PositionSizingModel.FixedQuantity,
        decimal sizingParameter = 1m, decimal initialMarginRatio = 1m, decimal maintenanceMarginRatio = 0.5m,
        decimal liquidationPenaltyRatio = 0m)
        => new()
        {
            InitialCapital = initialCapital,
            CommissionFlat = commissionFlat,
            CommissionPerUnit = commissionPerUnit,
            SlippageRatio = slippageRatio,
            SizingModel = sizingModel,
            SizingParameter = sizingParameter,
            InitialMarginRatio = initialMarginRatio,
            MaintenanceMarginRatio = maintenanceMarginRatio,
            LiquidationPenaltyRatio = liquidationPenaltyRatio,
        };

    private static StrategyOrderRequest Req(SignalType type, OrderType orderType, decimal? limitPrice = null, decimal? stopPrice = null, TimeInForce? tif = null, string reason = "r")
        => new(type, orderType, limitPrice, stopPrice, tif ?? Gtc, reason);

    /// <summary>Deterministic strategy that fires pre-scripted requests on specific bar indices, so acceptance tests do not depend on any indicator computation.</summary>
    private sealed class ScriptedStrategy : IBacktestStrategy
    {
        private readonly Dictionary<int, StrategyOrderRequest> _script;
        public string Name => "Scripted";
        public ScriptedStrategy(Dictionary<int, StrategyOrderRequest> script) => _script = script;
        public IReadOnlyList<StrategyIndicatorRequest> GetRequiredIndicators() => Array.Empty<StrategyIndicatorRequest>();
        public StrategyOrderRequest? Evaluate(StrategyContext context)
            => _script.TryGetValue(context.BarIndex, out StrategyOrderRequest request) ? request : null;
    }

    private sealed class StubIndicatorFactory : IIndicatorFactory
    {
        public ICoreIndicator? Create(IndicatorType type, CoreIndicatorParameterBase? parameters = null) => null;
        public bool IsRegistered(IndicatorType type) => false;
        public IEnumerable<IndicatorType> GetRegisteredTypes() => Array.Empty<IndicatorType>();
    }

    private static BacktestEngine CreateEngine() => new(new StubIndicatorFactory());

    // ---- Golden path / core correctness ----

    [Fact]
    public void GoldenLong_E1000_3Bars_NetPnL10()
    {
        // bar0 Close=100 -> LongEntry(Market); bar1 Open=101 fills entry; LongExit(Market) at bar1 Close;
        // bar2 Open=111 fills exit. ClosedGross = 1*(111-101) = 10.
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            Bar(0, 100m, 100m, 100m, 100m),
            Bar(1, 101m, 112m, 101m, 108m),
            Bar(2, 111m, 115m, 109m, 112m));
        BacktestConfiguration config = MakeConfig(initialCapital: 1000m);
        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.LongEntry, OrderType.Market),
            [1] = Req(SignalType.LongExit, OrderType.Market),
        });

        BacktestResult result = CreateEngine().Run(MakeInput(bars), config, strategy);

        Assert.Equal(RunStatus.Completed, result.Status);
        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.Equal(101m, trade.EntryPrice);
        Assert.Equal(111m, trade.ExitPrice);
        Assert.Equal(10m, trade.ClosedGross);
        Assert.Equal(1010m, result.EquityPoints[^1].Equity);
    }

    [Fact]
    public void GoldenCost_Slip001_Flat1_PerUnit0()
    {
        // SlippageRatio=0.01, CommissionFlat=1, CommissionPerUnit=0 - verifies fee+slippage combine correctly.
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            Bar(0, 100m, 100m, 100m, 100m),
            Bar(1, 100m, 112m, 99m, 105m),
            Bar(2, 110m, 115m, 108m, 112m));
        BacktestConfiguration config = MakeConfig(initialCapital: 1000m, commissionFlat: 1m, commissionPerUnit: 0m, slippageRatio: 0.01m);
        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.LongEntry, OrderType.Market),
            [1] = Req(SignalType.LongExit, OrderType.Market),
        });

        BacktestResult result = CreateEngine().Run(MakeInput(bars), config, strategy);

        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.Equal(101m, trade.EntryPrice);   // 100 * 1.01
        Assert.Equal(108.9m, trade.ExitPrice);  // 110 * 0.99
        Assert.Equal(1m, trade.EntryFee);
        Assert.Equal(1m, trade.ExitFee);
        Assert.Equal(7.9m, trade.ClosedGross);  // 108.9 - 101
        Assert.Equal(5.9m, trade.ClosedNet);    // 7.9 - 1 - 1
        Assert.Equal(1005.9m, result.EquityPoints[^1].Equity);
    }

    [Fact]
    public void GoldenMOC_EntryAtClose_ExitAtNextOpen()
    {
        // Worked example from the source spec doc (section 6): bar0 Close=100 -> LongEntry -> MOC queued;
        // bar1 Open=105/Close=108 -> buy fills at Close=108 (MOC); LongExit(MOO) queued after Close settles;
        // bar2 Open=110 -> sell fills at Open=110 (MOO). ClosedGross = 1*(110-108) = 2.
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            Bar(0, 100m, 100m, 100m, 100m),
            Bar(1, 105m, 112m, 104m, 108m),
            Bar(2, 110m, 115m, 109m, 111m));
        BacktestConfiguration config = MakeConfig(initialCapital: 1000m);
        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.LongEntry, OrderType.MarketOnClose),
            [1] = Req(SignalType.LongExit, OrderType.Market),
        });

        BacktestResult result = CreateEngine().Run(MakeInput(bars), config, strategy);

        Assert.Equal(RunStatus.Completed, result.Status);
        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.Equal(108m, trade.EntryPrice);
        Assert.Equal(110m, trade.ExitPrice);
        Assert.Equal(2m, trade.ClosedGross);
    }

    [Fact]
    public void MOC_vs_MOO_SameSignal_DifferentFillPrice()
    {
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            Bar(0, 100m, 100m, 100m, 100m),
            Bar(1, 105m, 112m, 104m, 108m));
        BacktestConfiguration config = MakeConfig();

        BacktestResult mooResult = CreateEngine().Run(
            MakeInput(bars), config,
            new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry, OrderType.Market) }));
        BacktestResult mocResult = CreateEngine().Run(
            MakeInput(bars), config,
            new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry, OrderType.MarketOnClose) }));

        decimal mooPrice = Assert.Single(mooResult.Fills).Price;
        decimal mocPrice = Assert.Single(mocResult.Fills).Price;
        Assert.Equal(105m, mooPrice); // Open
        Assert.Equal(108m, mocPrice); // Close
        Assert.NotEqual(mooPrice, mocPrice);
    }

    // ---- Order-type fill mechanics ----

    [Fact]
    public void StopLimit_OHLCvsOLHC_TriggerDifference()
    {
        // Same bar, mirrored Stop/Limit thresholds: the canonical O->H->L->C path lets a Buy StopLimit's
        // post-trigger Limit scan reuse the whole High->Low leg (High/Low not yet consumed by an Upward
        // trigger), but a Sell StopLimit triggered partway down the High->Low leg has already consumed
        // High - only a Low->Close recovery remains. This asymmetry is what "OHLC vs OLHC" refers to.
        var bar = Bar(0, 95m, 105m, 85m, 92m);
        var buyOrder = new BacktestOrder(1, OrderSide.Buy, OrderType.StopLimit, 1m, LimitPrice: 90m, StopPrice: 100m,
            OrderStatus.Submitted, Gtc, 0, 1, StopActivated: false, null, null);
        var sellOrder = new BacktestOrder(2, OrderSide.Sell, OrderType.StopLimit, 1m, LimitPrice: 100m, StopPrice: 90m,
            OrderStatus.Submitted, Gtc, 0, 1, StopActivated: false, null, null);

        OrderFillOutcome buyOutcome = OrderFillEvaluator.Evaluate(buyOrder, bar, 0m, 0m, 0m);
        OrderFillOutcome sellOutcome = OrderFillEvaluator.Evaluate(sellOrder, bar, 0m, 0m, 0m);

        Assert.True(buyOutcome.Filled);
        Assert.Equal(90m, buyOutcome.FillPrice); // reuses the still-available Low=85 to reach Limit=90
        Assert.False(sellOutcome.Filled);        // High=105 already consumed by the downward trigger; Close=92 never reaches Limit=100
    }

    [Fact]
    public void GTD_ExpiryBar_FillOnExpiryDay_ThenExpireNext()
    {
        var gtd = new TimeInForce(false, 1); // expires strictly after bar index 1 - the expiry bar itself may still fill.
        BacktestConfiguration config = MakeConfig();

        // Case A: the limit touches exactly on the expiry bar -> still fills there.
        ImmutableArray<CandleData> fillingBars = ImmutableArray.Create(
            Bar(0, 100m, 100m, 100m, 100m),
            Bar(1, 100m, 100m, 94m, 97m)); // Low=94 <= limit=95
        var fillingStrategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.LongEntry, OrderType.Limit, limitPrice: 95m, tif: gtd),
        });
        BacktestResult filled = CreateEngine().Run(MakeInput(fillingBars), config, fillingStrategy);
        BacktestOrder filledOrder = Assert.Single(filled.Orders);
        Assert.Equal(OrderStatus.Filled, filledOrder.Status);

        // Case B: the limit never touches on the expiry bar -> expires strictly on the NEXT bar.
        ImmutableArray<CandleData> expiringBars = ImmutableArray.Create(
            Bar(0, 100m, 100m, 100m, 100m),
            Bar(1, 100m, 101m, 98m, 100m),  // Low=98 > limit=95, never touches
            Bar(2, 99m, 101m, 97m, 100m));  // i=2 > ExpiryBar=1 -> Expired here
        var expiringStrategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.LongEntry, OrderType.Limit, limitPrice: 95m, tif: gtd),
        });
        BacktestResult expired = CreateEngine().Run(MakeInput(expiringBars), config, expiringStrategy);
        BacktestOrder expiredOrder = Assert.Single(expired.Orders);
        Assert.Empty(expired.Fills);
        Assert.Equal(OrderStatus.Expired, expiredOrder.Status);
        Assert.Equal(ExpiredReason.GTDExpired, expiredOrder.ExpiredReason);
    }

    // ---- Lifecycle / expiry / cancellation ----

    [Fact]
    public void EndOfData_PendingExpired_OpenPositionUnchanged()
    {
        // An open Long position with a pending exit Limit that never touches by the last bar: the pending
        // order must expire with reason EndOfData, and the OPEN POSITION itself must remain untouched
        // (no forced close) - it just keeps marking to market, exactly like the Insolvency case's rule.
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            Bar(0, 100m, 100m, 100m, 100m),
            Bar(1, 101m, 105m, 100m, 105m),   // entry fills at Open=101; LongExit(Limit=200) queued, never touches
            Bar(2, 106m, 110m, 104m, 108m));  // last bar - Limit=200 still never touched
        BacktestConfiguration config = MakeConfig(initialCapital: 1000m);
        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.LongEntry, OrderType.Market),
            [1] = Req(SignalType.LongExit, OrderType.Limit, limitPrice: 200m),
        });

        BacktestResult result = CreateEngine().Run(MakeInput(bars), config, strategy);

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Empty(result.Trades); // position was never closed
        BacktestOrder exitOrder = result.Orders[^1];
        Assert.Equal(OrderStatus.Expired, exitOrder.Status);
        Assert.Equal(ExpiredReason.EndOfData, exitOrder.ExpiredReason);
        Assert.Equal(108m, result.EquityPoints[^1].MarketValue); // 1 * last Close=108 - position still open, marked to market
    }

    [Fact]
    public void TerminalCancel_AlreadyTerminal_NoChange()
    {
        // A GTD order that fills exactly on its own expiry bar must stay Filled/unchanged on every later
        // bar, even though i > ExpiryBar holds for all of them - a terminal order must never be reprocessed.
        var gtd = new TimeInForce(false, 1);
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            Bar(0, 100m, 100m, 100m, 100m),
            Bar(1, 100m, 100m, 100m, 100m), // fills here (i=1=ExpiryBar, still allowed)
            Bar(2, 100m, 100m, 100m, 100m), // i=2 > ExpiryBar=1
            Bar(3, 100m, 100m, 100m, 100m)); // i=3 > ExpiryBar=1
        BacktestConfiguration config = MakeConfig();
        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.LongEntry, OrderType.Limit, limitPrice: 100m, tif: gtd),
        });

        BacktestResult result = CreateEngine().Run(MakeInput(bars), config, strategy);

        BacktestOrder order = Assert.Single(result.Orders);
        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Null(order.ExpiredReason);
    }

    // ---- Funds / solvency ----

    [Fact]
    public void Insolvent_EquityZero_StopsWithSnapshot()
    {
        // Plain Step 9 end-of-bar insolvency (NOT a margin-call liquidation - InitialMarginRatio=1 keeps
        // the liquidation threshold permanently negative/unreachable): a voluntary exit's own fee tips an
        // already fully-invested account's Cash to exactly zero-or-below.
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            Bar(0, 100m, 100m, 100m, 100m),
            Bar(1, 100m, 105m, 95m, 100m),   // entry fills at Open=100
            Bar(2, 0.5m, 1m, 0.4m, 0.6m));   // exit fills at Open=0.5 - catastrophic loss
        BacktestConfiguration config = MakeConfig(initialCapital: 102m, commissionFlat: 0m, commissionPerUnit: 1.5m, initialMarginRatio: 1m, maintenanceMarginRatio: 0.5m);
        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.LongEntry, OrderType.Market),
            [1] = Req(SignalType.LongExit, OrderType.Market),
        });

        BacktestResult result = CreateEngine().Run(MakeInput(bars), config, strategy);

        Assert.Equal(RunStatus.Insolvent, result.Status);
        EquityPoint last = result.EquityPoints[^1];
        Assert.True(last.Equity <= 0m);
        Assert.Equal(2, last.BarIndex); // snapshot IS recorded for the stopping bar
        Assert.Single(result.Trades); // the exit itself still executed normally (never funds-rejected)
    }

    [Fact]
    public void CashInsufficient_Rejected_NoPartialFill()
    {
        BacktestConfiguration config = MakeConfig(initialCapital: 50m, sizingParameter: 10m, initialMarginRatio: 1m, maintenanceMarginRatio: 0.5m);
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            Bar(0, 100m, 100m, 100m, 100m),
            Bar(1, 100m, 100m, 100m, 100m));
        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry, OrderType.Market) });

        BacktestResult result = CreateEngine().Run(MakeInput(bars), config, strategy);

        Assert.Empty(result.Fills);
        Assert.Empty(result.Trades);
        BacktestOrder order = Assert.Single(result.Orders);
        Assert.Equal(OrderStatus.Rejected, order.Status);
        Assert.Equal(RejectedReason.InsufficientFunds, order.RejectedReason);
    }

    // ---- Input validation / edge cases ----

    [Fact]
    public void ChronologicalViolation_Throws()
    {
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            Bar(0, 100m, 105m, 95m, 100m),
            Bar(0, 100m, 105m, 95m, 100m)); // same timestamp as bar 0 - not strictly increasing

        Assert.Throws<ArgumentException>(() => MakeInput(bars));
    }

    [Fact]
    public void NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new BacktestInput(
            ImmutableArray.Create(Bar(0, 100m, 105m, 95m, 100m)),
            symbol: null!, TimeFrame.D1, BacktestInput.CurrentDataVersion, Bar0, Bar0.AddDays(1), 0, 0));
    }

    [Fact]
    public void SingleBar_NoTrade_EquityEquals_E0()
    {
        BacktestConfiguration config = MakeConfig(initialCapital: 1000m);
        ImmutableArray<CandleData> bars = ImmutableArray.Create(Bar(0, 100m, 105m, 95m, 102m));

        BacktestResult result = CreateEngine().Run(MakeInput(bars), config, new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>()));

        Assert.Empty(result.Trades);
        Assert.Single(result.EquityPoints);
        Assert.Equal(1000m, result.EquityPoints[0].Equity);
    }

    [Fact]
    public void AccountingIdentity_ExactDecimalMatch_EveryBar()
    {
        // Two full trade cycles plus a still-open position at the end. For every EquityPoint:
        //   Equity - InitialCapital == Sum(ClosedNet so far) + UnrealizedGross - OpenPositionEntryFee
        // (section 7's reconciliation identity; still holds under the section 1.5 margin model since
        // margin only moves capital between Cash/HeldMargin, it never changes Equity itself.)
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            Bar(0, 100m, 100m, 100m, 100m),
            Bar(1, 101m, 112m, 100m, 110m),
            Bar(2, 115m, 116m, 114m, 115m),
            Bar(3, 120m, 132m, 119m, 130m),
            Bar(4, 131m, 140m, 129m, 135m));
        BacktestConfiguration config = MakeConfig(initialCapital: 1000m, commissionFlat: 1m, commissionPerUnit: 0m);
        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.LongEntry, OrderType.Market),
            [1] = Req(SignalType.LongExit, OrderType.Market),
            [2] = Req(SignalType.LongEntry, OrderType.Market),
        });

        BacktestResult result = CreateEngine().Run(MakeInput(bars), config, strategy);

        // Hand-derived per-bar identity components (EntryFee=1, ExitFee=1 apply on each fill):
        //   trade1: Entry@101 Bar1, Exit@115 Bar2 -> ClosedGross=14, ClosedNet=12.
        //   trade2 (still open at end): Entry@120 Bar3.
        decimal[] expectedEquity = { 1000m, 1008m, 1012m, 1021m, 1026m };
        decimal[] closedNetSoFar = { 0m, 0m, 12m, 12m, 12m };
        decimal[] unrealizedGross = { 0m, 9m, 0m, 10m, 15m };   // trade1 unrealized at bar1 (110-101); trade2 unrealized at bars 3,4 (Close-120)
        decimal[] openEntryFee = { 0m, 1m, 0m, 1m, 1m };

        Assert.Equal(expectedEquity.Length, result.EquityPoints.Length);
        for (int i = 0; i < expectedEquity.Length; i++)
        {
            Assert.Equal(expectedEquity[i], result.EquityPoints[i].Equity);
            decimal identity = closedNetSoFar[i] + unrealizedGross[i] - openEntryFee[i];
            Assert.Equal(result.EquityPoints[i].Equity - config.InitialCapital, identity);
        }
    }

    [Fact]
    public void AccountingIdentity_Short_ExactDecimalMatch_EveryBar()
    {
        // Mirror of AccountingIdentity_ExactDecimalMatch_EveryBar for the Short side (q<0): one full
        // Short round-trip plus a still-open Short position at the end. The same identity must hold with
        // the sign-correct RealizedPnL/UnrealizedPnL formulas (q*(EntryPrice-P) profits on a price drop).
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            Bar(0, 100m, 100m, 100m, 100m),
            Bar(1, 99m, 101m, 88m, 90m),
            Bar(2, 85m, 86m, 83m, 84m),
            Bar(3, 80m, 82m, 74m, 75m),
            Bar(4, 74m, 76m, 70m, 72m));
        BacktestConfiguration config = MakeConfig(initialCapital: 1000m, commissionFlat: 1m, commissionPerUnit: 0m);
        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.ShortEntry, OrderType.Market),
            [1] = Req(SignalType.ShortExit, OrderType.Market),
            [2] = Req(SignalType.ShortEntry, OrderType.Market),
        });

        BacktestResult result = CreateEngine().Run(MakeInput(bars), config, strategy);

        // Hand-derived per-bar identity components (EntryFee=1, ExitFee=1 apply on each fill):
        //   trade1 (Short): Entry@99 Bar1, Exit@85 Bar2 -> ClosedGross=14, ClosedNet=12 (profits from the price drop).
        //   trade2 (Short, still open at end): Entry@80 Bar3.
        decimal[] expectedEquity = { 1000m, 1008m, 1012m, 1016m, 1019m };
        decimal[] closedNetSoFar = { 0m, 0m, 12m, 12m, 12m };
        decimal[] unrealizedGross = { 0m, 9m, 0m, 5m, 8m }; // trade1 unrealized at bar1: -1*(90-99)=9; trade2: -1*(Close-80) at bars 3,4
        decimal[] openEntryFee = { 0m, 1m, 0m, 1m, 1m };

        Assert.Equal(expectedEquity.Length, result.EquityPoints.Length);
        for (int i = 0; i < expectedEquity.Length; i++)
        {
            Assert.Equal(expectedEquity[i], result.EquityPoints[i].Equity);
            decimal identity = closedNetSoFar[i] + unrealizedGross[i] - openEntryFee[i];
            Assert.Equal(result.EquityPoints[i].Equity - config.InitialCapital, identity);
        }
        Assert.Equal(TradeSide.Short, Assert.Single(result.Trades).Side);
    }

    [Fact]
    public void MOC_LimitPriceNonNull_InputError()
    {
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            Bar(0, 100m, 100m, 100m, 100m),
            Bar(1, 100m, 105m, 95m, 102m));
        BacktestConfiguration config = MakeConfig();
        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.LongEntry, OrderType.MarketOnClose, limitPrice: 100m), // MOC must not carry a LimitPrice
        });

        Assert.Throws<ArgumentException>(() => CreateEngine().Run(MakeInput(bars), config, strategy));
    }

    // ---- Section 1.5.9: margin/short additions ----

    [Fact]
    public void MarginEntry_Long_HeldMarginDeductedNotFullNotional()
    {
        BacktestConfiguration config = MakeConfig(initialCapital: 1000m, sizingParameter: 10m, initialMarginRatio: 0.3m, maintenanceMarginRatio: 0.2m);
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            Bar(0, 100m, 100m, 100m, 100m),
            Bar(1, 100m, 100m, 100m, 100m));
        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry, OrderType.Market) });

        BacktestResult result = CreateEngine().Run(MakeInput(bars), config, strategy);

        EquityPoint snapshot = result.EquityPoints[^1];
        Assert.Equal(300m, snapshot.HeldMargin); // 10 * 100 * 0.3, NOT the full 1000 notional
        Assert.Equal(700m, snapshot.Cash);       // 1000 - 300, not 1000 - 1000
        Assert.Equal(1000m, snapshot.Equity);    // unchanged - only margin moved, no fee/price change
    }

    [Fact]
    public void MarginEntry_Short_ProceedsNotAddedToCash_OnlyMarginHeld()
    {
        BacktestConfiguration config = MakeConfig(initialCapital: 1000m, sizingParameter: 10m, initialMarginRatio: 0.3m, maintenanceMarginRatio: 0.2m);
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            Bar(0, 100m, 100m, 100m, 100m),
            Bar(1, 100m, 100m, 100m, 100m));
        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.ShortEntry, OrderType.Market) });

        BacktestResult result = CreateEngine().Run(MakeInput(bars), config, strategy);

        EquityPoint snapshot = result.EquityPoints[^1];
        Assert.Equal(300m, snapshot.HeldMargin);
        Assert.Equal(700m, snapshot.Cash); // NOT 1000 + 1000 (notional) - 300; short proceeds are never added to Cash
        Assert.Equal(1000m, snapshot.Equity);
    }

    [Fact]
    public void MarginExit_Long_ReleasesHeldMarginPlusPnL()
    {
        BacktestConfiguration config = MakeConfig(initialCapital: 1000m, sizingParameter: 10m, initialMarginRatio: 0.3m, maintenanceMarginRatio: 0.2m);
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            Bar(0, 100m, 100m, 100m, 100m),
            Bar(1, 100m, 112m, 100m, 110m),
            Bar(2, 120m, 125m, 118m, 122m));
        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.LongEntry, OrderType.Market),
            [1] = Req(SignalType.LongExit, OrderType.Market),
        });

        BacktestResult result = CreateEngine().Run(MakeInput(bars), config, strategy);

        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.Equal(200m, trade.ClosedGross); // 10 * (120 - 100)
        Assert.Equal(1200m, result.EquityPoints[^1].Cash); // 700 (post-entry cash) + 300 (released margin) + 200 (PnL)
        Assert.Equal(0m, result.EquityPoints[^1].HeldMargin);
    }

    [Fact]
    public void MarginExit_Short_ReleasesHeldMarginPlusPnL()
    {
        BacktestConfiguration config = MakeConfig(initialCapital: 1000m, sizingParameter: 10m, initialMarginRatio: 0.3m, maintenanceMarginRatio: 0.2m);
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            Bar(0, 100m, 100m, 100m, 100m),
            Bar(1, 100m, 100m, 88m, 90m),
            Bar(2, 80m, 82m, 78m, 81m));
        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.ShortEntry, OrderType.Market),
            [1] = Req(SignalType.ShortExit, OrderType.Market),
        });

        BacktestResult result = CreateEngine().Run(MakeInput(bars), config, strategy);

        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.Equal(TradeSide.Short, trade.Side);
        Assert.Equal(200m, trade.ClosedGross); // q=-10 * (80-100) = 200 (short profits from the price drop)
        Assert.Equal(1200m, result.EquityPoints[^1].Cash); // same release formula as the Long case
        Assert.Equal(0m, result.EquityPoints[^1].HeldMargin);
    }

    [Fact]
    public void MarginDegenerate_InitialRatio1_MatchesOriginalCashModel_GoldenLong()
    {
        // InitialMarginRatio=1 degenerates the margin model back to the original full-cash-outlay model:
        // HeldMargin must equal the full notional (no leverage), and Cash must match the pre-amendment
        // "Cash' = Cash - Quantity*Price - Fee" formula exactly.
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            Bar(0, 100m, 100m, 100m, 100m),
            Bar(1, 101m, 112m, 101m, 108m),
            Bar(2, 111m, 115m, 109m, 112m));
        BacktestConfiguration config = MakeConfig(initialCapital: 1000m, initialMarginRatio: 1m, maintenanceMarginRatio: 0.5m);
        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.LongEntry, OrderType.Market),
            [1] = Req(SignalType.LongExit, OrderType.Market),
        });

        BacktestResult result = CreateEngine().Run(MakeInput(bars), config, strategy);

        EquityPoint afterEntry = result.EquityPoints[1];
        Assert.Equal(101m, afterEntry.HeldMargin); // full notional (1 * 101), no leverage
        Assert.Equal(899m, afterEntry.Cash);       // 1000 - 1*101 - 0, matches the original cash-model formula
        Assert.Equal(1010m, result.EquityPoints[^1].Equity); // 899(cash)+101(margin)+10(PnL)
    }

    [Fact]
    public void LiquidationThreshold_Long_LowTouchesPLiq_ForcedCloseAtPenalizedPrice_RunStatusInsolvent()
    {
        // Q=20 @ EntryPrice=100, ratio=0.3: notional=2000, margin=600, cash after=400.
        // P_liq = (2000-400-600)/(20*0.8) = 1000/16 = 62.5. Penalty=0.2 -> FillPrice=62.5*0.8=50.
        // RealizedPnL=20*(50-100)=-1000 -> Cash'=400+600-1000=0 -> Insolvent (boundary).
        BacktestConfiguration config = MakeConfig(initialCapital: 1000m, sizingParameter: 20m, initialMarginRatio: 0.3m, maintenanceMarginRatio: 0.2m, liquidationPenaltyRatio: 0.2m);
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            Bar(0, 100m, 100m, 100m, 100m),
            Bar(1, 100m, 100m, 100m, 100m),
            Bar(2, 90m, 91m, 60m, 65m)); // Low=60 breaches P_liq=62.5
        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry, OrderType.Market) });

        BacktestResult result = CreateEngine().Run(MakeInput(bars), config, strategy);

        Assert.Equal(RunStatus.Insolvent, result.Status);
        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.True(trade.IsForcedLiquidation);
        Assert.Equal(50m, trade.ExitPrice);
        Assert.True(result.EquityPoints[^1].Equity <= 0m);
    }

    [Fact]
    public void LiquidationThreshold_Short_HighTouchesPLiq_ForcedCloseAtPenalizedPrice_RunStatusInsolvent()
    {
        // Q=20 @ EntryPrice=100, ratio=0.3: notional=2000, margin=600, cash after=400.
        // P_liq = (400+600+2000)/(20*1.2) = 3000/24 = 125. Penalty=0.2 -> FillPrice=125*1.2=150.
        // RealizedPnL=-20*(150-100)=-1000 -> Cash'=400+600-1000=0 -> Insolvent (boundary).
        BacktestConfiguration config = MakeConfig(initialCapital: 1000m, sizingParameter: 20m, initialMarginRatio: 0.3m, maintenanceMarginRatio: 0.2m, liquidationPenaltyRatio: 0.2m);
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            Bar(0, 100m, 100m, 100m, 100m),
            Bar(1, 100m, 100m, 100m, 100m),
            Bar(2, 110m, 130m, 108m, 120m)); // High=130 breaches P_liq=125
        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.ShortEntry, OrderType.Market) });

        BacktestResult result = CreateEngine().Run(MakeInput(bars), config, strategy);

        Assert.Equal(RunStatus.Insolvent, result.Status);
        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.Equal(TradeSide.Short, trade.Side);
        Assert.True(trade.IsForcedLiquidation);
        Assert.Equal(150m, trade.ExitPrice);
        Assert.True(result.EquityPoints[^1].Equity <= 0m);
    }

    [Fact]
    public void LiquidationVsPendingExit_SamePathSameBar_FirstTouchWins()
    {
        // A Sell-Limit take-profit (Upward touch, Open->High leg) sits earlier on the canonical path than
        // the margin threshold (Long breach, only reachable via the later High->Low leg) - the pending
        // order must win and execute as a NORMAL exit, not a forced liquidation, even though the same bar
        // also independently breaches the maintenance margin later on.
        // Q=20 @ EntryPrice=100, ratio=0.3: notional=2000, margin=600, cash=400. P_liq=(2000-1000)/16=62.5.
        BacktestConfiguration config = MakeConfig(initialCapital: 1000m, sizingParameter: 20m, initialMarginRatio: 0.3m, maintenanceMarginRatio: 0.2m, liquidationPenaltyRatio: 0.1m);
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            Bar(0, 100m, 100m, 100m, 100m),
            Bar(1, 100m, 100m, 100m, 100m),
            Bar(2, 100m, 115m, 60m, 90m)); // Limit=110 touches on Open->High (position ~0.67); P_liq=62.5 touches on High->Low (position ~1.95)
        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.LongEntry, OrderType.Market),
            [1] = Req(SignalType.LongExit, OrderType.Limit, limitPrice: 110m),
        });

        BacktestResult result = CreateEngine().Run(MakeInput(bars), config, strategy);

        Assert.Equal(RunStatus.Completed, result.Status); // liquidation did NOT fire
        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.False(trade.IsForcedLiquidation);
        Assert.Equal(110m, trade.ExitPrice);
        Assert.Equal(200m, trade.ClosedGross); // 20 * (110-100)
    }

    [Fact]
    public void ShortGoldenPath_EntryExit_NetPnLMatchesSignConvention()
    {
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            Bar(0, 100m, 100m, 100m, 100m),
            Bar(1, 99m, 100m, 85m, 90m),
            Bar(2, 85m, 87m, 83m, 86m));
        BacktestConfiguration config = MakeConfig(initialCapital: 1000m);
        var strategy = new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.ShortEntry, OrderType.Market),
            [1] = Req(SignalType.ShortExit, OrderType.Market),
        });

        BacktestResult result = CreateEngine().Run(MakeInput(bars), config, strategy);

        Assert.Equal(RunStatus.Completed, result.Status);
        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.Equal(TradeSide.Short, trade.Side);
        Assert.Equal(99m, trade.EntryPrice);
        Assert.Equal(85m, trade.ExitPrice);
        Assert.Equal(14m, trade.ClosedGross); // -1 * (85-99) = 14, positive because price fell
        Assert.Equal(1014m, result.EquityPoints[^1].Equity);
    }
}
