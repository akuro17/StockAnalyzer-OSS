using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Threading;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

public sealed class RlTradingSessionTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static BacktestEngine Engine() => new(new IndicatorFactory());
    private static CandleData Bar(int index, decimal open = 100m, decimal high = 110m, decimal low = 90m, decimal close = 100m)
        => new(Start.AddDays(index), open, high, low, close, 1_000 + index);
    private static ImmutableArray<CandleData> Bars(Func<int, CandleData>? create = null)
        => Enumerable.Range(0, RlContract.SupportLength).Select(create ?? (i => Bar(i))).ToImmutableArray();
    private static BacktestInput Input(ImmutableArray<CandleData> bars, int anchor = RlContract.WindowLength - 1, TimeFrame frame = TimeFrame.D1)
        => new(bars, "NUMERIC_FIXTURE", frame, BacktestInput.CurrentDataVersion, Start, Start.AddDays(bars.Length), 0, anchor);
    private static BacktestConfiguration Configuration(decimal capital = 1_000m, decimal commission = 0m, decimal unitCommission = 0m, decimal slippage = 0m)
        => RlContract.CreateConfiguration(new BacktestConfiguration
        {
            InitialCapital = capital, CommissionFlat = commission, CommissionPerUnit = unitCommission,
            SlippageRatio = slippage, SizingModel = PositionSizingModel.FixedQuantity, SizingParameter = BacktestDefaults.SizingParameter,
        });

    [Fact]
    public void BuySell_UsesNextOpenCostsExactlyOnceAndDecimalReward()
    {
        var bars = Bars(i => i switch
        {
            64 => Bar(i, 120m, 150m, 100m, 140m),
            65 => Bar(i, 150m, 180m, 110m, 160m),
            _ => Bar(i),
        });
        using var session = Engine().CreateRlSession(Input(bars), Configuration(commission: 2m, unitCommission: 0.5m, slippage: 0.01m));
        var initial = session.Reset();
        var buy = session.Step(RlAction.Buy);
        Assert.Equal(63, initial.MarketAnchorBarIndex);
        Assert.Equal(1_000m, initial.EquityPoint.Equity);
        Assert.Equal(121.2m, buy.Snapshot.EntryPrice);
        Assert.Equal(1_016.3m, buy.Snapshot.EquityPoint.Equity);
        Assert.Equal(0.0163m, buy.Reward);
        Assert.Equal(1m / RlContract.EpisodeHorizon, buy.Snapshot.AccountFields[8]);
        Assert.Equal(1m / RlContract.MaxPositionQuantity, buy.Snapshot.AccountFields[1]);
        var sell = session.Step(RlAction.Sell);
        Assert.Equal(1_022.3m, sell.Snapshot.EquityPoint.Equity);
        Assert.Equal(0.006m, sell.Reward);
        Assert.Equal(0m, sell.Snapshot.Quantity);
        Assert.Equal(0, sell.Snapshot.HeldBars);
        var result = session.GetResult();
        Assert.Equal(new[] { 64, 65 }, result.Fills.Select(f => f.BarIndex));
        Assert.Equal(new[] { 121.2m, 148.5m }, result.Fills.Select(f => f.Price));
        Assert.Equal(22.3m, Assert.Single(result.Trades).ClosedNet);
    }

    [Fact]
    public void IncrementalBookAndCloseAccount_MatchSharedRunOnIdenticalActions()
    {
        var bars = Bars(i => Bar(i, 100m + i / 10m, 115m + i / 10m, 85m + i / 10m, 103m + i / 10m));
        var input = Input(bars);
        var config = Configuration(commission: 1m, slippage: 0.002m);
        var schedule = new Dictionary<int, RlAction> { [63] = RlAction.Buy, [100] = RlAction.Sell, [127] = RlAction.Buy };
        var engine = Engine();
        BacktestResult full = engine.Run(input, config, new ScriptedStrategy(schedule));
        using var session = engine.CreateRlSession(input, config);
        var snapshot = session.Reset();
        decimal rewards = 0m;
        for (int decision = 0; decision < RlContract.EpisodeHorizon; decision++)
        {
            int anchor = RlContract.WindowLength - 1 + decision;
            var step = session.Step(schedule.TryGetValue(anchor, out var action) ? action : RlAction.Hold);
            rewards += step.Reward;
            snapshot = step.Snapshot;
            Assert.Equal(full.EquityPoints[anchor + 1], snapshot.EquityPoint);
            Assert.False(step.Truncated);
        }
        BacktestResult incremental = session.GetResult();
        Assert.Equal(full.Orders, incremental.Orders);
        Assert.Equal(full.Fills, incremental.Fills);
        Assert.Equal(full.Trades, incremental.Trades);
        Assert.Equal(full.Signals, incremental.Signals);
        Assert.Equal(full.EquityPoints.Skip(RlContract.WindowLength - 1), incremental.EquityPoints);
        Assert.True(snapshot.Terminated);
        Assert.Equal(RlTerminalReason.Horizon, snapshot.TerminalReason);
        Assert.Equal((snapshot.EquityPoint.Equity - config.InitialCapital) / config.InitialCapital, rewards);
    }

    [Fact]
    public void FullHorizon_MarksResidualPositionAndDoesNotInventFinalSale()
    {
        using var session = Engine().CreateRlSession(Input(Bars()), Configuration());
        session.Reset();
        var step = session.Step(RlAction.Buy);
        for (int i = 1; i < RlContract.EpisodeHorizon; i++) step = session.Step(RlAction.Hold);
        Assert.True(step.Terminated);
        Assert.False(step.Truncated);
        Assert.Equal(191, step.Snapshot.MarketAnchorBarIndex);
        Assert.Equal(128, step.Snapshot.CompletedDecisions);
        Assert.Equal(0, step.Snapshot.RemainingDecisions);
        Assert.Equal(1m, step.Snapshot.Quantity);
        Assert.Equal(128, step.Snapshot.HeldBars);
        Assert.Equal(0m, step.Snapshot.AccountFields[11]);
        Assert.All(step.Snapshot.AccountFields.Skip(12), value => Assert.Equal(0m, value));
        Assert.Single(session.GetResult().Fills);
        Assert.Empty(session.GetResult().Trades);
        Assert.Throws<InvalidOperationException>(() => session.Step(RlAction.Hold));
    }

    [Fact]
    public void GapFundsRejection_KeepsAccountUnchangedAndContinuesWithoutPenalty()
    {
        using var session = Engine().CreateRlSession(Input(Bars(i => i == 64 ? Bar(i, 200m, 220m, 180m, 200m) : Bar(i))), Configuration(capital: 150m));
        session.Reset();
        var rejected = session.Step(RlAction.Buy);
        Assert.Equal(RlActionOutcome.Rejected, rejected.Outcome);
        Assert.Equal(RejectedReason.InsufficientFunds, rejected.RejectionReason);
        Assert.Equal(150m, rejected.Snapshot.EquityPoint.Cash);
        Assert.Equal(0m, rejected.Snapshot.Quantity);
        Assert.Equal(0m, rejected.Reward);
        Assert.False(rejected.Terminated);
        Assert.False(rejected.Snapshot.PendingEntry);
        Assert.Equal(RlActionOutcome.Submitted, session.Step(RlAction.Buy).Outcome);
        Assert.Equal(1m, session.GetResult().Fills[0].Quantity);
    }

    [Fact]
    public void DuplicateDirection_IsReasonedNoOpWithoutPyramidingOrReversal()
    {
        using var session = Engine().CreateRlSession(Input(Bars()), Configuration());
        session.Reset();
        Assert.Equal(RlActionOutcome.AlreadyFlat, session.Step(RlAction.Sell).Outcome);
        Assert.Equal(RlActionOutcome.Submitted, session.Step(RlAction.Buy).Outcome);
        var duplicate = session.Step(RlAction.Buy);
        Assert.Equal(RlActionOutcome.AlreadyLong, duplicate.Outcome);
        Assert.Equal(1m, duplicate.Snapshot.Quantity);
        Assert.Equal(2, duplicate.Snapshot.HeldBars);
        Assert.Single(session.GetResult().Orders);
        Assert.Equal(RlActionOutcome.Hold, session.Step(RlAction.Hold).Outcome);
        Assert.Equal(RlActionOutcome.Submitted, session.Step(RlAction.Sell).Outcome);
        Assert.Equal(RlActionOutcome.AlreadyFlat, session.Step(RlAction.Sell).Outcome);
        Assert.All(session.GetResult().Trades, t => Assert.Equal(TradeSide.Long, t.Side));
    }

    [Fact]
    public void TerminalCloseNonPositiveEquity_UpdatesCompletedWindowAndAllowsDrawdownAboveOne()
    {
        using var session = Engine().CreateRlSession(Input(Bars(i => i == 65 ? Bar(i, 50m, 60m, 40m, 50m) : Bar(i))),
            Configuration(capital: 200m, commission: 100m));
        session.Reset();
        Assert.Equal(-0.5m, session.Step(RlAction.Buy).Reward);
        var terminal = session.Step(RlAction.Sell);
        Assert.True(terminal.Terminated);
        Assert.Equal(RlTerminalReason.EquityNonPositive, terminal.Snapshot.TerminalReason);
        Assert.Equal(RunStatus.Insolvent, terminal.Snapshot.Status);
        Assert.Equal(-50m, terminal.Snapshot.EquityPoint.Equity);
        Assert.Equal(-0.75m, terminal.Reward);
        Assert.Equal(1.25m, terminal.Snapshot.Drawdown);
        Assert.Equal(1.25m, terminal.Snapshot.MaximumDrawdown);
        Assert.Equal(65, terminal.Snapshot.MarketAnchorBarIndex);
        Assert.Equal(65, terminal.Snapshot.TerminalEventBarIndex);
        Assert.Equal(65, terminal.Snapshot.EquityPoint.BarIndex);
        Assert.Equal(0, terminal.Snapshot.RemainingDecisions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefensiveInjectedMarginState_StopsOnForcedLiquidationAndRetainsPriorCompletedMarket(bool bankrupt)
    {
        // Valid IMR=1 T15 admission leaves Cash >= 0, hence P_liq=-Cash/(q*(1-MMR)) <= 0.
        // Positive-price maintenance liquidation is therefore unreachable through valid T15 actions.
        // Inject only a real decimal Cash state to exercise the defensive shared-phase terminal contract;
        // this is a numeric accountant regression, never synthetic market training or a domain mock.
        var bars = Bars(i => i == 65
            ? bankrupt ? Bar(i, 5m, 110m, 1m, 100m) : Bar(i, 100m, 110m, 10m, 105m)
            : Bar(i));
        using var session = Engine().CreateRlSession(Input(bars), Configuration(capital: 100m));
        session.Reset();
        var admitted = session.Step(RlAction.Buy).Snapshot;
        object state = session.GetType().GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)!;
        state.GetType().GetField("Cash", BindingFlags.Public | BindingFlags.Instance)!.SetValue(state, -10m);
        var account = admitted.AccountFields.SetItem(2, -0.1m).SetItem(4, 0.9m).SetItem(10, 0.1m);
        var injected = admitted with
        {
            EquityPoint = admitted.EquityPoint with { Equity = 90m, Cash = -10m },
            Drawdown = 0.1m, MaximumDrawdown = 0.1m, AccountFields = account,
        };
        var points = (List<EquityPoint>)state.GetType().GetField("EquityPoints", BindingFlags.Public | BindingFlags.Instance)!.GetValue(state)!;
        points[^1] = injected.EquityPoint;
        session.GetType().GetField("_snapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(session, injected);
        var terminal = session.Step(RlAction.Hold);
        decimal expected = bankrupt ? -5.025m : 2.4375m;
        Assert.True(terminal.Terminated);
        Assert.False(terminal.Truncated);
        Assert.Equal(RlTerminalReason.ForcedLiquidation, terminal.Snapshot.TerminalReason);
        Assert.Equal(expected, terminal.Snapshot.EquityPoint.Equity);
        Assert.Equal((expected - 90m) / 100m, terminal.Reward);
        Assert.Equal(64, terminal.Snapshot.MarketAnchorBarIndex);
        Assert.Equal(65, terminal.Snapshot.TerminalEventBarIndex);
        Assert.Equal(65, terminal.Snapshot.EquityPoint.BarIndex);
        Assert.Equal(admitted.MarketWindow, terminal.Snapshot.MarketWindow);
        Assert.Equal(0, terminal.Snapshot.RemainingDecisions);
        Assert.Equal(0m, terminal.Snapshot.Quantity);
        Assert.True(Assert.Single(session.GetResult().Trades).IsForcedLiquidation);
        Assert.Equal(0L, session.GetResult().Fills[^1].OrderId);
        if (bankrupt) Assert.True(terminal.Snapshot.Drawdown > 1m);
        else Assert.True(terminal.Snapshot.EquityPoint.Equity > 0m);
        Assert.Throws<InvalidOperationException>(() => session.Step(RlAction.Hold));
    }

    [Theory]
    [InlineData(RlAction.Buy, false)]
    [InlineData(RlAction.Sell, true)]
    [InlineData(RlAction.Hold, false)]
    public void DefensivePendingMarketSlot_PreservesCommittedOrderAndNoOpsRelevantRepeatedAction(RlAction action, bool exit)
    {
        // Public T15 Step consumes a Market intent and advances atomically. Inject the real pending
        // order state at its completed-Close boundary to verify slot preservation without relaxing sizing.
        using var session = Engine().CreateRlSession(Input(Bars()), Configuration());
        session.Reset();
        if (exit) session.Step(RlAction.Buy);
        object state = GetState(session);
        var orders = (List<BacktestOrder>)state.GetType().GetField("Orders")!.GetValue(state)!;
        int anchor = exit ? 64 : 63;
        long orderId = (long)state.GetType().GetField("NextOrderId")!.GetValue(state)!;
        var order = new BacktestOrder(orderId, exit ? OrderSide.Sell : OrderSide.Buy, OrderType.Market,
            1m, null, null, OrderStatus.Submitted, new TimeInForce(true, -1), anchor, anchor + 1, false, null, null);
        orders.Add(order);
        state.GetType().GetField(exit ? "PendingExitOrderIndex" : "PendingEntryOrderIndex")!.SetValue(state, orders.Count - 1);
        state.GetType().GetField("NextOrderId")!.SetValue(state, orderId + 1);
        int count = orders.Count;
        var step = session.Step(action);
        Assert.Equal(action == RlAction.Hold ? RlActionOutcome.Hold : exit ? RlActionOutcome.PendingExit : RlActionOutcome.PendingEntry, step.Outcome);
        Assert.Equal(count, session.GetResult().Orders.Length);
        Assert.Equal(OrderStatus.Filled, session.GetResult().Orders[^1].Status);
        Assert.Equal(orderId, session.GetResult().Fills[^1].OrderId);
        Assert.Equal(exit ? 0m : 1m, step.Snapshot.Quantity);
    }

    [Fact]
    public void DefensivePendingBeyondHorizon_ExpiresWithoutAnotherFillOrLiquidation()
    {
        using var session = Engine().CreateRlSession(Input(Bars()), Configuration());
        session.Reset();
        session.Step(RlAction.Buy);
        for (int i = 1; i < RlContract.EpisodeHorizon - 1; i++) session.Step(RlAction.Hold);
        object state = GetState(session);
        var orders = (List<BacktestOrder>)state.GetType().GetField("Orders")!.GetValue(state)!;
        long orderId = (long)state.GetType().GetField("NextOrderId")!.GetValue(state)!;
        orders.Add(new BacktestOrder(orderId, OrderSide.Sell, OrderType.Market, 1m, null, null, OrderStatus.Submitted,
            new TimeInForce(true, -1), RlContract.SupportLength - 2, RlContract.SupportLength, false, null, null));
        state.GetType().GetField("PendingExitOrderIndex")!.SetValue(state, orders.Count - 1);
        var terminal = session.Step(RlAction.Hold);
        Assert.True(terminal.Terminated);
        Assert.False(terminal.Snapshot.PendingExit);
        Assert.Equal(1m, terminal.Snapshot.Quantity);
        Assert.Single(session.GetResult().Fills);
        Assert.Empty(session.GetResult().Trades);
        Assert.Equal(OrderStatus.Expired, session.GetResult().Orders[^1].Status);
        Assert.Equal(ExpiredReason.EndOfData, session.GetResult().Orders[^1].ExpiredReason);
    }

    [Fact]
    public void DecimalArithmeticFailure_RollsBackWholeUnpublishedTransitionAndRequiresReset()
    {
        using var session = Engine().CreateRlSession(Input(Bars(i => i == 64 ? Bar(i, 100m, decimal.MaxValue, 90m, decimal.MaxValue) : Bar(i))),
            Configuration(capital: decimal.MaxValue));
        var initial = session.Reset();
        Assert.Throws<OverflowException>(() => session.Step(RlAction.Buy));
        var prefix = session.GetResult();
        Assert.Equal(RunStatus.Failed, prefix.Status);
        Assert.Empty(prefix.Orders);
        Assert.Empty(prefix.Fills);
        Assert.Empty(prefix.Signals);
        Assert.Equal(initial.EquityPoint, Assert.Single(prefix.EquityPoints));
        Assert.Throws<InvalidOperationException>(() => session.Step(RlAction.Hold));
        Assert.Equal(0, session.Reset().CompletedDecisions);
    }

    [Fact]
    public void LegacyRun_PositiveEquityForcedLiquidationStillContinuesAndProcessesLaterClose()
    {
        var bars = Bars(i => i == 64 ? Bar(i, 100m, 110m, 10m, 100m) : Bar(i));
        var config = new BacktestConfiguration
        {
            InitialCapital = 30m, InitialMarginRatio = 0.3m, MaintenanceMarginRatio = 0.2m,
            SizingModel = PositionSizingModel.FixedQuantity, SizingParameter = 1m,
        };
        var result = Engine().Run(Input(bars), config, new ScriptedStrategy(new Dictionary<int, RlAction> { [63] = RlAction.Buy }));
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.True(Assert.Single(result.Trades).IsForcedLiquidation);
        Assert.Equal(RlContract.SupportLength, result.EquityPoints.Length);
        Assert.True(result.EquityPoints[^1].Equity > 0m);
        Assert.Equal(191, result.EquityPoints[^1].BarIndex);
    }

    [Fact]
    public void Observation_IsCausalAndPreviouslyReturnedSnapshotsStayImmutableAfterReset()
    {
        var a = Bars();
        var b = Bars(i => i >= 70 ? Bar(i, 900m, 950m, 850m, 910m) : Bar(i));
        using var left = Engine().CreateRlSession(Input(a), Configuration());
        using var right = Engine().CreateRlSession(Input(b), Configuration());
        var old = left.Reset();
        var rightInitial = right.Reset();
        Assert.Equal(a.Take(64), old.MarketWindow);
        Assert.Equal(old.MarketWindow, rightInitial.MarketWindow);
        Assert.Equal(16, old.AccountFields.Length);
        Assert.Equal(1m, old.AccountFields[11]);
        for (int i = 0; i < 6; i++)
        {
            var leftStep = left.Step(i == 0 ? RlAction.Buy : RlAction.Hold);
            var rightStep = right.Step(i == 0 ? RlAction.Buy : RlAction.Hold);
            Assert.Equal(leftStep.Snapshot.MarketWindow, rightStep.Snapshot.MarketWindow);
            Assert.Equal(leftStep.Snapshot.AccountFields, rightStep.Snapshot.AccountFields);
            Assert.Equal(leftStep.Reward, rightStep.Reward);
        }
        var reset = left.Reset();
        Assert.Equal(0m, reset.Quantity);
        Assert.Equal(0, reset.CompletedDecisions);
        Assert.Equal(128, reset.RemainingDecisions);
        Assert.Empty(left.GetResult().Orders);
        Assert.Equal(63, old.MarketAnchorBarIndex);
        Assert.Equal(0m, old.Quantity);
        Assert.Equal(old.MarketWindow, reset.MarketWindow);
        Assert.Equal(old.AccountFields, reset.AccountFields);
    }

    [Fact]
    public void InvalidUninitializedCancelledAndDisposedRequests_NeverProduceSuccessfulTransition()
    {
        var session = Engine().CreateRlSession(Input(Bars()), Configuration());
        Assert.Throws<InvalidOperationException>(() => session.Step(RlAction.Hold));
        Assert.Throws<InvalidOperationException>(() => session.GetResult());
        session.Reset();
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Step((RlAction)(-1)));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => session.Step(RlAction.Buy, cancelled.Token));
        Assert.Empty(session.GetResult().Orders);
        Assert.Equal(1, session.Step(RlAction.Hold).Snapshot.CompletedDecisions);
        session.Dispose();
        session.Dispose();
        Assert.Throws<ObjectDisposedException>(() => session.Step(RlAction.Hold));
        Assert.Throws<ObjectDisposedException>(() => session.Reset());
        Assert.Throws<ObjectDisposedException>(() => session.GetResult());
    }

    [Fact]
    public void IncompleteSupportAndIncorrectAnchorOrExecutionIdentity_FailBeforeReset()
    {
        var engine = Engine();
        var bars = Bars();
        Assert.Throws<ArgumentException>(() => engine.CreateRlSession(Input(bars.RemoveAt(bars.Length - 1)), Configuration()));
        Assert.Throws<ArgumentException>(() => engine.CreateRlSession(Input(bars, anchor: 64), Configuration()));
        Assert.Throws<ArgumentException>(() => engine.CreateRlSession(Input(bars, frame: TimeFrame.H1), Configuration()));
        var legacy = new BacktestConfiguration { InitialCapital = 1_000m, SizingModel = PositionSizingModel.FixedQuantity, SizingParameter = 1m };
        Assert.Throws<ArgumentException>(() => engine.CreateRlSession(Input(bars), legacy));
        var captured = RlContract.CreateConfiguration(legacy);
        Assert.Equal(ExecutionModel.Legacy, legacy.ExecutionModel);
        Assert.Equal(ExecutionModel.YFinanceApproximate, captured.ExecutionModel);
        Assert.Equal(1m, captured.InitialMarginRatio);
    }

    [Theory]
    [InlineData(0f, 0f, 0f, RlAction.Hold)]
    [InlineData(-1f, 2f, 2f, RlAction.Buy)]
    [InlineData(-1f, -2f, 1f, RlAction.Sell)]
    public void GreedySelection_UsesExactFirstMaximum(float hold, float buy, float sell, RlAction expected)
        => Assert.Equal(expected, RlContract.SelectAction(new[] { hold, buy, sell }));

    [Fact]
    public void GreedySelection_RejectsNonfiniteOrWrongShapeAndDoesNotApplyNearTieTolerance()
    {
        Assert.Throws<ArgumentException>(() => RlContract.SelectAction(new[] { 0f, float.NaN, 1f }));
        Assert.Throws<ArgumentException>(() => RlContract.SelectAction(new[] { 0f, 1f }));
        Assert.Equal(RlAction.Buy, RlContract.SelectAction(new[] { 1f, MathF.BitIncrement(1f), 0f }));
        Assert.Equal(64, RlContract.ContractHash.Length);
        Assert.Equal(RlContract.ObservationLength, (int)RlContract.ToWireDefaults()[nameof(RlContract.ObservationLength)]);
        Assert.Equal(RlContract.AccountFieldCount, RlContract.AccountFieldNames.Length);
    }

    private sealed class ScriptedStrategy(Dictionary<int, RlAction> schedule) : IBacktestStrategy
    {
        public string Name => "rl.direction.fixed1.v1";
        public IReadOnlyList<StrategyIndicatorRequest> GetRequiredIndicators() => Array.Empty<StrategyIndicatorRequest>();
        public StrategyOrderRequest? Evaluate(StrategyContext context)
        {
            if (!schedule.TryGetValue(context.BarIndex, out var action)) return null;
            return new StrategyOrderRequest(action == RlAction.Buy ? SignalType.LongEntry : SignalType.LongExit,
                OrderType.Market, null, null, new TimeInForce(true, -1), action.ToString());
        }
    }

    private static object GetState(object session)
        => session.GetType().GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)!;
}
