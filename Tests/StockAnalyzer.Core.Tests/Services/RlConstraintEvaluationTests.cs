#nullable enable
using System;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class RlConstraintEvaluationTests
{
    private static readonly DateTime Start = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static BacktestInput Input(decimal nextOpen = 100m, decimal close = 100m)
    {
        var bars = Enumerable.Range(0, RlContract.SupportLength).Select(i =>
        {
            decimal price = i == RlContract.WindowLength ? nextOpen : i > RlContract.WindowLength ? close : 100m;
            return new CandleData(Start.AddDays(i), price, price + 1m, price - 1m, price, 1000L);
        }).ToImmutableArray();
        return new(bars, "R04_HAND", TimeFrame.D1, BacktestInput.CurrentDataVersion, Start, Start.AddDays(bars.Length), 0, RlContract.WindowLength - 1);
    }
    private static BacktestConfiguration Costs(decimal capital = 1000m, decimal flat = 2m, decimal unit = .5m, decimal slip = 0m)
        => RlContract.CreateConfiguration(new BacktestConfiguration { InitialCapital = capital, CommissionFlat = flat,
            CommissionPerUnit = unit, SlippageRatio = slip, SizingModel = BacktestDefaults.SizingModel, SizingParameter = BacktestDefaults.SizingParameter });
    private static IRlContinuousTradingSession Session(BacktestInput input, BacktestConfiguration costs)
        => new BacktestEngine(IndicatorFactory.Default).CreateRlContinuousSession(input, costs);

    [Fact] public void CoreEntryEvidenceCapturesActualGapFillAndRetainedControls()
    {
        using var session = Session(Input(400m, 400m), Costs()); session.Reset();
        var step = session.Step(RlAction.Buy, 1f, 1f); var entry = step.EntryExecution!;
        Assert.Equal(100m, entry.DecisionClose); Assert.Equal(1000m, entry.DecisionEquity); Assert.Equal(300m, entry.DecisionBudget);
        Assert.Equal(1000m, entry.CashBeforeEntry); Assert.Equal(2m, entry.RequestedQuantity); Assert.Equal(2m, entry.ExecutableQuantity);
        Assert.Equal(2m, entry.FilledQuantity); Assert.Equal(400m, entry.FillPrice); Assert.Equal(800m, entry.FilledValue);
        Assert.Equal(800m, entry.HeldMargin); Assert.Equal(3m, entry.Commission);
        Assert.Equal(.30m, step.Snapshot.RetainedEntrySizeRatio);
        Assert.Equal(RlEntryConstraintStatus.Unconstrained, RlConstraintEvaluation.Summarize(new[] { entry }).Status);
        session.Step(RlAction.Sell, -1f, -1f);
        var closed = session.Step(RlAction.Hold, -1f, -1f);
        Assert.Null(closed.Snapshot.RetainedEntrySizeRatio);
    }

    [Fact] public void ZeroCapFundsAndHoldAreDifferentEvidenceAndNoRejectionRateThresholdExists()
    {
        using var zero = Session(Input(), Costs()); zero.Reset();
        var noEntry = zero.Step(RlAction.Buy, -1f, 0f).EntryExecution!;
        Assert.Equal(0m, noEntry.RequestedQuantity); Assert.Null(noEntry.OrderId);
        Assert.Equal(RlActionOutcome.NoEntry, noEntry.Outcome);
        Assert.Equal(1, RlConstraintEvaluation.Summarize(new[] { noEntry }).ZeroQuantity);
        using var cap = Session(Input(), Costs(3333667m, 0m, 0m)); cap.Reset();
        var capped = cap.Step(RlAction.Buy, 1f, 0f).EntryExecution!;
        Assert.Equal(10001m, capped.RequestedQuantity); Assert.Equal(0m, capped.ExecutableQuantity); Assert.Equal(0m, capped.FilledQuantity);
        Assert.Equal(RlEntryConstraintStatus.NoAdmittedBuyBecauseOfCap, RlConstraintEvaluation.Summarize(new[] { capped }).Status);
        Assert.Equal(RlEntryConstraintStatus.CapConstrained, RlConstraintEvaluation.Summarize(new[] { noEntry, capped }).Status);
        using var funds = Session(Input(600m), Costs()); funds.Reset();
        var denied = funds.Step(RlAction.Buy, 1f, 0f).EntryExecution!;
        Assert.Equal(RejectedReason.InsufficientFunds, denied.RejectionReason); Assert.Equal(2m, denied.RequestedQuantity);
        Assert.Equal(0m, denied.FilledValue); Assert.Null(denied.FillPrice);
        Assert.Equal(1, RlConstraintEvaluation.Summarize(new[] { denied }).InsufficientFunds);
        using var hold = Session(Input(), Costs()); hold.Reset();
        Assert.Null(hold.StepFixedControl(RlAction.Hold).EntryExecution);
        Assert.Equal(RlEntryConstraintStatus.NotApplicable, RlConstraintEvaluation.Summarize(Array.Empty<RlEntryExecution>()).Status);
    }

    [Fact] public void ExitSensitivityHasFixedHandAnswerAndDoesNotInventFillsOrChangeTheMarkScore()
    {
        var fixedAnswer = RlConstraintEvaluation.ExitSensitivity(2m, 100m, 1000m, Costs(slip: .01m));
        Assert.Equal(99m, fixedAnswer.SyntheticExitPrice); Assert.Equal(3m, fixedAnswer.AdditionalCommission);
        Assert.Equal(995m, fixedAnswer.AdjustedEquity); Assert.Equal(-.005m, fixedAnswer.AdjustedReturn);
        var flat = RlConstraintEvaluation.ExitSensitivity(0m, 100m, 997m, Costs(slip: .01m));
        Assert.Equal(0m, flat.AdditionalCommission); Assert.Null(flat.SyntheticExitPrice);
        Assert.Equal(997m, flat.AdjustedEquity); Assert.Equal(-.003m, flat.AdjustedReturn);
        using var session = Session(Input(), Costs()); var state = session.Reset();
        var first = session.Step(RlAction.Buy, 1f, 1f); state = first.Snapshot;
        while (!state.Terminated) state = session.Step(RlAction.Hold, 0f, 0f).Snapshot;
        var result = session.GetResult(); var before = result.ReproducibilityHash;
        var evidence = RlConstraintEvaluation.Finish(state, result, new[] { first.EntryExecution! });
        Assert.Equal(2m, evidence.ResidualQuantity); Assert.Equal(997m, evidence.MarkEquity); Assert.Equal(994m, evidence.ExitCostSensitivity.AdjustedEquity);
        Assert.Single(session.GetResult().Fills); Assert.Empty(session.GetResult().Trades);
        Assert.Equal(before, session.GetResult().ReproducibilityHash);
        Assert.Equal(-.003m, RlEvaluation.Parse(RlEvaluation.Finish(new("fixture", "validation", 0, RlContract.SupportLength), state, result, 0, 0).TerminalReturn));
    }

    [Fact] public void SensitivityOverflowIsAnExplicitFailure()
        => Assert.Throws<OverflowException>(() => RlConstraintEvaluation.ExitSensitivity(3m, decimal.MaxValue, 1000m, Costs(slip: .5m)));

    [Fact] public void FixedBaselineUsesItsOwnControlWhileActorProposalAndHeldEntryRemainDistinct()
    {
        var costs = Costs(); using var session = Session(Input(), costs); var state = session.Reset();
        var proposal = RlContinuousContract.Map(0f, 0f);
        var actor = RlConstraintEvaluation.Readout(state, costs, proposal, RlAction.Buy, RlContinuousContract.Target, "direction");
        var baseline = RlConstraintEvaluation.Readout(state, costs, proposal, RlAction.Buy, RlContinuousContract.PairedBaselineId, "direction");
        Assert.Equal(.15m, baseline.Action.SizeRatio); Assert.Equal(.0275m, baseline.Action.StopDistance);
        Assert.Equal(.30m, baseline.SelectedPolicyControl!.SizeRatio); Assert.Equal(.02m, baseline.SelectedPolicyControl.StopDistance);
        Assert.Equal(1m, actor.EstimatedQuantity); Assert.Equal(2m, baseline.EstimatedQuantity); Assert.Equal(98m, baseline.EstimatedStopPrice);
        var filled = session.StepFixedControl(RlAction.Buy).Snapshot;
        var held = RlConstraintEvaluation.Readout(filled, costs, proposal, RlAction.Hold, RlContinuousContract.Target, "direction");
        Assert.True(held.SelectedPolicyControl!.RetainedFromActualEntry); Assert.Equal(.30m, held.SelectedPolicyControl.SizeRatio);
        Assert.Equal(.02m, held.SelectedPolicyControl.StopDistance); Assert.Null(held.EstimatedQuantity);
        Assert.Null(RlConstraintEvaluation.Readout(filled with { RetainedEntrySizeRatio = null }, costs, proposal, RlAction.Hold, RlContinuousContract.Target, "direction").SelectedPolicyControl);
        Assert.Null(RlConstraintEvaluation.Readout(state, costs, proposal, RlAction.Hold, RlContract.HoldBaselineId, "direction").EstimatedQuantity);
    }
}
