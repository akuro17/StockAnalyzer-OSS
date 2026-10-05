#nullable enable
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>Fixed hand ledger answers: expected money never comes from a production accountant.</summary>
public sealed class RlFinancialLedgerTests
{
    private static readonly DateTime Start = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static CandleData Bar(int index, decimal open = 100m, decimal? close = null, decimal? low = null)
        => new(Start.AddDays(index), open, Math.Max(open, close ?? open) + 1m, low ?? Math.Min(open, close ?? open) - 1m, close ?? open, 1000L);
    private static BacktestInput Input(Func<int, CandleData>? factory = null)
    {
        var bars = Enumerable.Range(0, RlContract.SupportLength).Select(factory ?? (i => Bar(i))).ToImmutableArray();
        return new(bars, "R03_HAND_LEDGER", TimeFrame.D1, BacktestInput.CurrentDataVersion, Start, Start.AddDays(bars.Length), 0, RlContract.WindowLength - 1);
    }
    private static BacktestConfiguration Costs(decimal capital = 1000m, decimal flat = 2m, decimal perUnit = .5m, decimal slip = 0m)
        => RlContract.CreateConfiguration(new BacktestConfiguration { InitialCapital = capital, CommissionFlat = flat, CommissionPerUnit = perUnit,
            SlippageRatio = slip, SizingModel = BacktestDefaults.SizingModel, SizingParameter = BacktestDefaults.SizingParameter });
    private static IRlContinuousTradingSession Session(BacktestInput input, BacktestConfiguration costs)
        => new BacktestEngine(IndicatorFactory.Default).CreateRlContinuousSession(input, costs);
    private static void Account(RlSessionSnapshot actual, decimal cash, decimal margin, decimal quantity, decimal entry,
        decimal unrealized, decimal equity, decimal peak, decimal dd, decimal maximumDd)
    {
        Assert.Equal(cash, actual.EquityPoint.Cash); Assert.Equal(margin, actual.EquityPoint.HeldMargin);
        Assert.Equal(quantity, actual.Quantity); Assert.Equal(entry, actual.EntryPrice); Assert.Equal(unrealized, actual.UnrealizedPnL);
        Assert.Equal(equity, actual.EquityPoint.Equity); Assert.Equal(peak, actual.PeakEquity);
        Assert.Equal(dd, actual.Drawdown); Assert.Equal(maximumDd, actual.MaximumDrawdown);
    }

    [Fact] public void FlatAndGapEntryHaveFixedHandAccountAndFeeAnswers()
    {
        using var session = Session(Input(i => i == 64 ? Bar(i, 400m) : Bar(i)), Costs());
        var reset = session.Reset(); Account(reset, 1000m, 0m, 0m, 0m, 0m, 1000m, 1000m, 0m, 0m);
        var step = session.Step(RlAction.Buy, 1f, 1f);
        // floor((1000*.30-2)/(100+.5))=2. Future Open changes margin, never the decision quantity.
        Account(step.Snapshot, 197m, 800m, 2m, 400m, 0m, 997m, 1000m, .003m, .003m);
        Assert.Equal(-.003m, step.Reward); Assert.Equal(380m, step.Snapshot.ActiveStopPrice);
        var fill = Assert.Single(session.GetResult().Fills);
        Assert.Equal(new BacktestFill(1, 1, 64, Start.AddDays(64), OrderSide.Buy, 400m, 2m, 3m, 0m), fill);
        var orders = session.GetResult().Orders; Assert.Equal(63, orders[0].SubmittedBar); Assert.Equal(64, orders[0].EarliestFillBar);
        Assert.Equal(64, orders[1].SubmittedBar); Assert.Equal(65, orders[1].EarliestFillBar);
    }

    [Fact] public void InsufficientFutureOpenPreservesMoneyWhileMarketObservationProgresses()
    {
        using var session = Session(Input(i => i == 64 ? Bar(i, 600m) : Bar(i)), Costs()); var initial = session.Reset();
        var rejected = session.Step(RlAction.Buy, 1f, 1f);
        Account(rejected.Snapshot, 1000m, 0m, 0m, 0m, 0m, 1000m, 1000m, 0m, 0m);
        Assert.Equal(RlActionOutcome.Rejected, rejected.Outcome); Assert.Equal(RejectedReason.InsufficientFunds, rejected.RejectionReason);
        Assert.Equal(0m, rejected.Reward); Assert.Empty(session.GetResult().Fills); Assert.Null(rejected.Snapshot.ActiveStopPrice);
        Assert.Equal(2m, Assert.Single(session.GetResult().Orders).Quantity);
        Assert.Equal(initial.MarketAnchorBarIndex + 1, rejected.Snapshot.MarketAnchorBarIndex);
        Assert.Equal(600m, rejected.Snapshot.MarketWindow[^1].Close); Assert.Equal(1, rejected.Snapshot.CompletedDecisions);
    }

    [Fact] public void GapThroughStopHasFixedExitLedgerAndCosts()
    {
        using var session = Session(Input(i => i == 64 ? Bar(i, 400m) : i == 65 ? Bar(i, 350m, low: 340m) : Bar(i)), Costs());
        session.Reset(); session.Step(RlAction.Buy, 1f, 1f);
        var exit = session.Step(RlAction.Hold, 0f, 0f);
        Account(exit.Snapshot, 894m, 0m, 0m, 0m, 0m, 894m, 1000m, .106m, .106m);
        Assert.Equal(-.103m, exit.Reward);
        var result = session.GetResult(); var fill = result.Fills[1];
        Assert.Equal(new BacktestFill(2, 2, 65, Start.AddDays(65), OrderSide.Sell, 350m, 2m, 3m, 0m), fill);
        var trade = Assert.Single(result.Trades);
        Assert.Equal(-100m, trade.ClosedGross); Assert.Equal(-106m, trade.ClosedNet); Assert.Equal(3m, trade.EntryFee); Assert.Equal(3m, trade.ExitFee);
        Assert.Equal(1, trade.HoldingBars); Assert.Equal(64, trade.EntryBar); Assert.Equal(65, trade.ExitBar);
        Assert.Equal(6m, result.Fills.Sum(f => f.Commission));
    }

    [Fact] public void SellCancellationAndNextMarketExitHaveFixedHandLedgerAndTiming()
    {
        using var session = Session(Input(i => i == 64 ? Bar(i, 400m) : i == 65 ? Bar(i, 410m) : i == 66 ? Bar(i, 450m) : Bar(i)), Costs());
        session.Reset(); session.Step(RlAction.Buy, 1f, 1f);
        var cancel = session.Step(RlAction.Sell, -1f, -1f);
        Account(cancel.Snapshot, 197m, 800m, 2m, 400m, 20m, 1017m, 1017m, 0m, .003m);
        Assert.Equal(RlActionOutcome.StopCancellationQueued, cancel.Outcome); Assert.Equal(.02m, cancel.Reward);
        Assert.Null(cancel.Snapshot.ActiveStopPrice); Assert.True(cancel.Snapshot.PendingExit);
        var orders = session.GetResult().Orders; Assert.Equal(OrderStatus.Cancelled, orders[1].Status);
        Assert.Equal(65, orders[2].SubmittedBar); Assert.Equal(66, orders[2].EarliestFillBar);
        var exit = session.Step(RlAction.Hold, 0f, 0f);
        Account(exit.Snapshot, 1094m, 0m, 0m, 0m, 0m, 1094m, 1094m, 0m, .003m);
        Assert.Equal(.077m, exit.Reward); Assert.Equal(450m, session.GetResult().Fills[1].Price);
        var trade = Assert.Single(session.GetResult().Trades); Assert.Equal(100m, trade.ClosedGross); Assert.Equal(94m, trade.ClosedNet);
        Assert.Equal(2, trade.HoldingBars);
    }

    [Fact] public void SlippageIsReferenceCostAndIsNotDeductedTwice()
    {
        using var session = Session(Input(i => i == 65 ? Bar(i, 110m) : i == 66 ? Bar(i, 120m) : Bar(i)), Costs(slip: .01m));
        session.Reset(); var buy = session.Step(RlAction.Buy, 1f, 1f);
        Account(buy.Snapshot, 795m, 202m, 2m, 101m, -2m, 995m, 1000m, .005m, .005m);
        Assert.Equal(95.95m, buy.Snapshot.ActiveStopPrice);
        session.Step(RlAction.Sell, 0f, 0f); var exit = session.Step(RlAction.Hold, 0f, 0f);
        Assert.Equal(1029.6m, exit.Snapshot.EquityPoint.Cash); Assert.Equal(1029.6m, exit.Snapshot.EquityPoint.Equity);
        var result = session.GetResult(); Assert.Equal(118.8m, result.Fills[1].Price);
        Assert.Equal(2m, result.Fills[0].SlippageAmount); Assert.Equal(2.4m, result.Fills[1].SlippageAmount);
        Assert.Equal(35.6m, result.Trades[0].ClosedGross); Assert.Equal(29.6m, result.Trades[0].ClosedNet);
        Assert.Equal(6m, result.Trades[0].TotalCommission); Assert.Equal(4.4m, result.Trades[0].TotalSlippage);
    }

    [Theory] [InlineData(999, 2, 799)] [InlineData(1000, 3, 700)]
    public void FloorBoundaryUsesFixedDecisionClose(int capital, int quantity, int cash)
    {
        using var session = Session(Input(), Costs(capital, 0m, 0m)); session.Reset(); var buy = session.Step(RlAction.Buy, 1f, 1f);
        Assert.Equal((decimal)quantity, buy.Snapshot.Quantity); Assert.Equal((decimal)cash, buy.Snapshot.EquityPoint.Cash);
        Assert.Equal((decimal)capital, buy.Snapshot.EquityPoint.Equity);
    }

    [Theory] [InlineData(3333334, 10000, false)] [InlineData(3333667, 10001, true)]
    public void QuantityCapAdmitsExactBoundaryAndRejectsExcessWithoutResizing(int capital, int intended, bool rejected)
    {
        using var session = Session(Input(), Costs(capital, 0m, 0m)); session.Reset(); var buy = session.Step(RlAction.Buy, 1f, 1f);
        Assert.Equal((decimal)intended, session.GetResult().Orders[0].Quantity);
        Assert.Equal(rejected ? RlActionOutcome.Rejected : RlActionOutcome.Submitted, buy.Outcome);
        Assert.Equal(rejected ? RejectedReason.QuantityCapExceeded : (RejectedReason?)null, buy.RejectionReason);
        Assert.Equal(rejected ? 0m : 10000m, buy.Snapshot.Quantity);
        Assert.Equal(rejected ? capital : 2333334m, buy.Snapshot.EquityPoint.Cash);
        Assert.Equal((decimal)capital, buy.Snapshot.EquityPoint.Equity);
    }

    [Fact] public void TerminalResidualMarksCloseAndExpiresStopWithoutExtraFill()
    {
        using var session = Session(Input(i => i >= 64 ? Bar(i, 400m) : Bar(i)), Costs()); session.Reset();
        var terminal = session.Step(RlAction.Buy, 1f, 1f);
        for (int i = 1; i < RlContract.EpisodeHorizon; i++) terminal = session.Step(RlAction.Hold, 0f, 0f);
        Account(terminal.Snapshot, 197m, 800m, 2m, 400m, 0m, 997m, 1000m, .003m, .003m);
        Assert.True(terminal.Terminated); Assert.Equal(RlTerminalReason.Horizon, terminal.Snapshot.TerminalReason);
        Assert.Equal(191, terminal.Snapshot.MarketAnchorBarIndex); Assert.Equal(128, terminal.Snapshot.HeldBars);
        Assert.Equal(0m, terminal.Reward); Assert.Single(session.GetResult().Fills); Assert.Empty(session.GetResult().Trades);
        Assert.Equal(OrderStatus.Expired, session.GetResult().Orders[1].Status); Assert.Null(terminal.Snapshot.ActiveStopPrice);
    }

    [Fact] public void CancellationAndArithmeticFailureRestorePrefixAndRequireReset()
    {
        using var cancelled = Session(Input(), Costs()); var before = cancelled.Reset();
        using var ct = new CancellationTokenSource(); ct.Cancel();
        Assert.Throws<OperationCanceledException>(() => cancelled.Step(RlAction.Buy, 1f, 1f, ct.Token));
        Assert.Equal(RunStatus.Cancelled, cancelled.GetResult().Status); Assert.Empty(cancelled.GetResult().Fills);
        Assert.Equal(before.EquityPoint, Assert.Single(cancelled.GetResult().EquityPoints));
        Assert.Throws<InvalidOperationException>(() => cancelled.Step(RlAction.Hold, 0f, 0f));
        cancelled.Reset(); Assert.Equal(1, cancelled.Step(RlAction.Hold, 0f, 0f).Snapshot.CompletedDecisions);
        using var overflow = Session(Input(i => i == 64 ? new(Start.AddDays(i), 400m, decimal.MaxValue, 399m, decimal.MaxValue, 1000L) : Bar(i)), Costs());
        var initial = overflow.Reset(); Assert.Throws<OverflowException>(() => overflow.Step(RlAction.Buy, 1f, 1f));
        var prefix = overflow.GetResult(); Assert.Equal(RunStatus.Failed, prefix.Status);
        Assert.Empty(prefix.Orders); Assert.Empty(prefix.Fills); Assert.Empty(prefix.Trades); Assert.Empty(prefix.Signals);
        Assert.Equal(initial.EquityPoint, Assert.Single(prefix.EquityPoints));
        Assert.Throws<InvalidOperationException>(() => overflow.Step(RlAction.Hold, 0f, 0f));
        Assert.Equal(0, overflow.Reset().CompletedDecisions);
    }

    [Fact] public void MlCloseRawValuesCannotHideFloorCapOrStopEventChanges()
    {
        var input = Input(i => i == 65 ? Bar(i, 100m, low: 96.12495m) : Bar(i));
        using var first = Session(input, Costs(1000m, 0m, 0m)); using var second = Session(input, Costs(1000m, 0m, 0m));
        first.Reset(); second.Reset(); first.Step(RlAction.Buy, 1f, .5f); second.Step(RlAction.Buy, 1f, .50005f);
        var stop = first.Step(RlAction.Hold, 0f, 0f); var hold = second.Step(RlAction.Hold, 0f, 0f);
        Assert.Equal(0m, stop.Snapshot.Quantity); Assert.Equal(3m, hold.Snapshot.Quantity);
        Assert.Throws<InvalidDataException>(() => RlContinuousParity.RequireLifecycle(stop, hold));
        foreach (decimal capital in new[] { 1000m, 3333666.67m })
        {
            using var native = Session(Input(), Costs(capital, 0m, 0m)); using var exported = Session(Input(), Costs(capital, 0m, 0m));
            native.Reset(); exported.Reset();
            var a = native.Step(RlAction.Buy, 1f, 1f); var b = exported.Step(RlAction.Buy, .9999f, 1f);
            Assert.Throws<InvalidDataException>(() => RlContinuousParity.RequireLifecycle(a, b));
        }
    }

    [Fact] public void SameRawTraceCarriesExactDecimalFieldsAndRejectsFinancialTampering()
    {
        using var session = Session(Input(i => i == 64 ? Bar(i, 400m) : Bar(i)), Costs()); session.Reset();
        var step = session.Step(RlAction.Buy, 1f, 1f); var trace = RlFinancialTrace.Capture(step, session.GetResult());
        var wire = trace.ToWire(); Assert.Equal(JsonValueKind.String, wire.GetProperty("account").GetProperty("equity_point").GetProperty("cash").ValueKind);
        RlContinuousParity.RequireExactJson(wire, wire, "fixture");
        var altered = trace with { Account = trace.Account with { EquityPoint = trace.Account.EquityPoint with { Cash = 198m } } };
        Assert.Throws<InvalidDataException>(() => RlContinuousParity.RequireExactJson(wire, altered.ToWire(), "tamper cash"));
        altered = trace with { Fills = trace.Fills.SetItem(0, trace.Fills[0] with { SlippageAmount = 1m }) };
        Assert.Throws<InvalidDataException>(() => RlContinuousParity.RequireExactJson(wire, altered.ToWire(), "tamper slippage"));
    }
}
