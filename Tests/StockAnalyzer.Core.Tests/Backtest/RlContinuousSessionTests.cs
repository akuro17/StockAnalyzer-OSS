using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

public sealed class RlContinuousSessionTests
{
    private static readonly DateTime Start = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static CandleData Bar(int i, decimal open = 100m, decimal low = 99m, decimal close = 100m)
        => new(Start.AddDays(i), open, Math.Max(open, close) + 1m, low, close, 1000);
    private static BacktestInput Input(Func<int, CandleData>? factory = null)
    {
        var bars = Enumerable.Range(0, RlContract.SupportLength).Select(factory ?? (i => Bar(i))).ToImmutableArray();
        return new(bars, "NUMERIC_FIXTURE", TimeFrame.D1, BacktestInput.CurrentDataVersion, Start, Start.AddDays(bars.Length), 0, RlContract.WindowLength - 1);
    }
    private static BacktestConfiguration Costs(decimal capital = 1000m, decimal flat = 0m, decimal perUnit = 0m)
        => RlContract.CreateConfiguration(new BacktestConfiguration { InitialCapital = capital, CommissionFlat = flat, CommissionPerUnit = perUnit,
            SizingModel = BacktestDefaults.SizingModel, SizingParameter = BacktestDefaults.SizingParameter });
    private static BacktestEngine Engine() => new(IndicatorFactory.Default);

    [Theory]
    [InlineData(-3f, 3f, "0", ".05", true)]
    [InlineData(-1f, -1f, "0", ".005", false)]
    [InlineData(0f, 0f, ".15", ".0275", false)]
    [InlineData(1f, 1f, ".30", ".05", false)]
    public void Map_ClipsOnceAndKeepsRawDiagnostic(float size, float stop, string ratio, string distance, bool clipped)
    {
        var mapped = RlContinuousContract.Map(size, stop);
        Assert.Equal(size, mapped.RawSize); Assert.Equal(stop, mapped.RawStop); Assert.Equal(clipped, mapped.WasClipped);
        Assert.Equal(decimal.Parse(ratio, System.Globalization.CultureInfo.InvariantCulture), mapped.SizeRatio);
        Assert.Equal(decimal.Parse(distance, System.Globalization.CultureInfo.InvariantCulture), mapped.StopDistance);
    }
    [Theory]
    [InlineData(float.NaN)] [InlineData(float.PositiveInfinity)] [InlineData(float.NegativeInfinity)]
    public void Nonfinite_IsRejectedBeforeAnyTransition(float value)
    {
        using var session = Engine().CreateRlContinuousSession(Input(), Costs()); session.Reset();
        Assert.Throws<ArgumentException>(() => session.Step(RlAction.Buy, value, 0)); Assert.Empty(session.GetResult().Orders);
    }
    [Fact] public void Stop_IsSubmittedAtActualFillCloseAndCannotExecuteOnTheFillBar()
    {
        using var session = Engine().CreateRlContinuousSession(Input(i => i == 64 ? Bar(i, low: 90m) : Bar(i, low: 94m)), Costs());
        session.Reset(); var fill = session.Step(RlAction.Buy, 1, 1);
        Assert.Equal(3m, fill.Snapshot.Quantity); Assert.Equal(95m, fill.Snapshot.ActiveStopPrice);
        Assert.Equal(.05m, fill.Snapshot.RetainedStopDistance); Assert.Equal(1m, fill.Snapshot.AccountFields[12]);
        var stop = session.GetResult().Orders.Single(o => o.Type == OrderType.Stop);
        Assert.Equal(64, stop.SubmittedBar); Assert.Equal(65, stop.EarliestFillBar); Assert.Single(session.GetResult().Fills);
        var exit = session.Step(RlAction.Hold, -1, -1); Assert.Equal(0m, exit.Snapshot.Quantity);
        Assert.Equal(0m, exit.Snapshot.RetainedStopDistance); Assert.Null(exit.Snapshot.ActiveStopPrice);
        Assert.Equal(95m, session.GetResult().Fills.Last().Price);
    }
    [Fact] public void Sell_CancelsStopThenSubmitsFullMarketAtNextCloseAndCannotBeRevoked()
    {
        using var session = Engine().CreateRlContinuousSession(Input(), Costs()); session.Reset();
        session.Step(RlAction.Buy, 1, 1);
        var sell = session.Step(RlAction.Sell, -1, -1);
        Assert.Equal(RlActionOutcome.StopCancellationQueued, sell.Outcome);
        Assert.Equal(3m, sell.Snapshot.Quantity); Assert.Null(sell.Snapshot.ActiveStopPrice);
        Assert.True(sell.Snapshot.PendingExit); Assert.False(sell.Snapshot.DeferredMarketExit);
        var market = session.GetResult().Orders.Last(); Assert.Equal(OrderType.Market, market.Type);
        Assert.Equal(65, market.SubmittedBar); Assert.Equal(66, market.EarliestFillBar);
        var hold = session.Step(RlAction.Buy, 1, 1); Assert.Equal(0m, hold.Snapshot.Quantity);
        Assert.Equal(new[] { 64, 66 }, session.GetResult().Fills.Select(f => f.BarIndex));
        Assert.Equal(OrderStatus.Cancelled, session.GetResult().Orders.Single(o => o.Type == OrderType.Stop).Status);
    }
    [Fact] public void FixedBaselineUsesExactTwoPercentAndLongControlsCannotResizeOrChangeStop()
    {
        using var session = Engine().CreateRlContinuousSession(Input(), Costs()); session.Reset();
        var first = session.StepFixedControl(RlAction.Buy); Assert.Equal(98m, first.Snapshot.ActiveStopPrice);
        var next = session.Step(RlAction.Buy, -1, 1); Assert.Equal(3m, next.Snapshot.Quantity); Assert.Equal(98m, next.Snapshot.ActiveStopPrice);
        Assert.Equal(.02m, next.Snapshot.RetainedStopDistance); Assert.Equal(RlActionOutcome.AlreadyLong, next.Outcome);
    }
    [Fact] public void QuantityCap_RejectsWithoutSilentCappingAndZeroSuppressesEntry()
    {
        using var session = Engine().CreateRlContinuousSession(Input(), Costs(capital: 10000000m)); session.Reset();
        Assert.Equal(RlActionOutcome.NoEntry, session.Step(RlAction.Buy, -1, 0).Outcome);
        Assert.Empty(session.GetResult().Orders);
        var cap = session.Step(RlAction.Buy, 1, 0); Assert.Equal(RlActionOutcome.Rejected, cap.Outcome);
        Assert.Equal(RejectedReason.QuantityCapExceeded, cap.RejectionReason); Assert.Equal(0m, cap.Snapshot.Quantity);
        Assert.Null(cap.Snapshot.ActiveStopPrice); Assert.Equal(30000m, session.GetResult().Orders.Single().Quantity);
    }
    [Fact] public void SharedPercentBudgetUsesSignalCloseCostsAndDoesNotResizeAtFutureOpen()
    {
        using var session = Engine().CreateRlContinuousSession(Input(i => i == 64 ? Bar(i, open: 400m, low: 399m, close: 400m) : Bar(i)), Costs(flat: 2m, perUnit: .5m));
        session.Reset(); var step = session.Step(RlAction.Buy, 1, 1);
        Assert.Equal(2m, session.GetResult().Orders.First().Quantity); Assert.Equal(800m, step.Snapshot.EquityPoint.HeldMargin);
        Assert.Equal(400m, step.Snapshot.EntryPrice); Assert.Equal(380m, step.Snapshot.ActiveStopPrice);
    }
    [Fact] public void HorizonExpiresStopWithoutInventingSaleOrAnotherBar()
    {
        using var session = Engine().CreateRlContinuousSession(Input(), Costs()); session.Reset();
        var step = session.Step(RlAction.Buy, 1, 1);
        for (int i = 1; i < RlContract.EpisodeHorizon; i++) step = session.Step(RlAction.Hold, 0, 0);
        Assert.True(step.Terminated); Assert.Equal(3m, step.Snapshot.Quantity); Assert.Null(step.Snapshot.ActiveStopPrice);
        Assert.Equal(.05m, step.Snapshot.RetainedStopDistance); Assert.Single(session.GetResult().Fills);
        Assert.Throws<InvalidOperationException>(() => session.Step(RlAction.Hold, 0, 0));
    }

    [Fact] public void CombinedStopAndDeferredExit_MatchExistingRunForIdenticalCanonicalIntents()
    {
        var input = Input(); var costs = Costs(flat: 1m, perUnit: .1m); var engine = Engine();
        var full = engine.Run(input, costs, new CanonicalStrategy());
        using var session = engine.CreateRlContinuousSession(input, costs); session.Reset();
        for (int i = 0; i < RlContract.EpisodeHorizon; i++)
            session.Step(i == 0 ? RlAction.Buy : i == 2 ? RlAction.Sell : RlAction.Hold, 1, 1);
        var incremental = session.GetResult();
        Assert.Equal(full.Orders, incremental.Orders); Assert.Equal(full.Fills, incremental.Fills); Assert.Equal(full.Trades, incremental.Trades);
        Assert.Equal(full.EquityPoints.Skip(RlContract.WindowLength - 1), incremental.EquityPoints);
    }
    private sealed class CanonicalStrategy : IBacktestStrategy, IBacktestOrderCancellationStrategy
    {
        public string Name => "canonical.t16.finance.fixture";
        public IReadOnlyList<StrategyIndicatorRequest> GetRequiredIndicators() => Array.Empty<StrategyIndicatorRequest>();
        public OrderCancellationIntent EvaluateCancellations(StrategyContext context)
            => context.BarIndex == 65 ? new(context.PendingExitOrderId, null) : OrderCancellationIntent.None;
        public StrategyOrderRequest? Evaluate(StrategyContext context) => context.BarIndex switch
        {
            63 => new StrategyOrderRequest(SignalType.LongEntry, OrderType.Market, null, null, new(true, -1), "Buy") { EntrySizeRatio = RlContinuousContract.MaximumSizeRatio },
            64 => new StrategyOrderRequest(SignalType.LongExit, OrderType.Stop, null, context.EntryPrice * (1m - .05m), new(true, -1), "rl.fixed_protective_stop"),
            66 => new StrategyOrderRequest(SignalType.LongExit, OrderType.Market, null, null, new(true, -1), "rl.deferred_full_exit"),
            _ => null,
        };
    }
}
