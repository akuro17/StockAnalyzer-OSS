#nullable enable
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class RlFinancialEnvelopeTests
{
    private static readonly DateTime Start = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static BacktestInput Input(decimal close = 100m, decimal low = 94m)
    {
        var bars = Enumerable.Range(0, RlContract.SupportLength).Select(i => new CandleData(Start.AddDays(i), close, close + 1m,
            i == RlContract.WindowLength + 1 ? low : close - 1m, close, 1000L)).ToImmutableArray();
        return new(bars, "R03_DECIMAL_INTERVAL", TimeFrame.D1, BacktestInput.CurrentDataVersion, Start, Start.AddDays(bars.Length), 0, RlContract.WindowLength - 1);
    }
    private static BacktestConfiguration Costs(decimal slip = 0m) => RlContract.CreateConfiguration(new BacktestConfiguration
    { InitialCapital = 1000m, CommissionFlat = 2m, CommissionPerUnit = .5m, SlippageRatio = slip,
        SizingModel = BacktestDefaults.SizingModel, SizingParameter = BacktestDefaults.SizingParameter });

    [Theory] [InlineData("0")] [InlineData("0.0123456789012345678901234567")]
    public void DifferentRawStopsHaveDecimalPriceCashRealizedSlippageProof(string slippage)
    {
        var costs = Costs(decimal.Parse(slippage, System.Globalization.CultureInfo.InvariantCulture));
        var input = Input(); var engine = new BacktestEngine(IndicatorFactory.Default);
        using var native = engine.CreateRlContinuousSession(input, costs); using var exported = engine.CreateRlContinuousSession(input, costs);
        native.Reset(); exported.Reset();
        var a = native.Step(RlAction.Buy, 1f, .5f); var b = exported.Step(RlAction.Buy, 1f, .50005f);
        var envelope = new RlFinancialEnvelope(costs);
        envelope.RetainStopInterval(a.Snapshot.EntryPrice, .03874865m, .03875135m);
        var entryA = native.GetResult().Fills[0]; var entryB = exported.GetResult().Fills[0];
        Assert.Equal(0m, envelope.ApplyFill(entryA, entryB, OrderType.Market, input.Bars[64], 0m, 0m, 0m, false));
        var before = a.Snapshot;
        a = native.Step(RlAction.Hold, 0f, 0f); b = exported.Step(RlAction.Hold, 0f, 0f);
        var resultA = native.GetResult(); var resultB = exported.GetResult();
        Assert.Equal(2, resultA.Fills.Length); Assert.Equal(2, resultB.Fills.Length);
        var fillA = resultA.Fills[1]; var fillB = resultB.Fills[1];
        var range = envelope.StopFillInterval(input.Bars[65]); Assert.True(range.Contains(fillA.Price)); Assert.True(range.Contains(fillB.Price));
        decimal bound = envelope.ApplyFill(fillA, fillB, OrderType.Stop, input.Bars[65], before.Quantity, before.EntryPrice, before.EquityPoint.HeldMargin, false);
        var error = envelope.Verify(a.Snapshot, b.Snapshot, resultA.Trades[0].ClosedGross, resultB.Trades[0].ClosedGross,
            resultA.Trades[0].ClosedNet, resultB.Trades[0].ClosedNet, resultA.Fills.Sum(f => f.Commission), resultB.Fills.Sum(f => f.Commission),
            resultA.Fills.Sum(f => f.SlippageAmount), resultB.Fills.Sum(f => f.SlippageAmount), Math.Abs(fillA.Price - fillB.Price), bound);
        Assert.True(error.FillPriceError > 0m); Assert.True(error.CashError > 0m);
        Assert.True(error.FillPriceError <= error.FillPriceBound); Assert.True(error.CashError <= error.CashErrorBound);
        Assert.True(error.EquityError <= error.EquityErrorBound); Assert.True(error.RealizedGrossError <= error.RealizedGrossBound);
        Assert.True(error.SlippageError <= error.SlippageBound); Assert.Equal(0m, error.CommissionError);
        Assert.Throws<InvalidDataException>(() => envelope.ApplyFill(fillA, fillB with { Commission = fillB.Commission + 1m }, OrderType.Stop,
            input.Bars[65], before.Quantity, before.EntryPrice, before.EquityPoint.HeldMargin, false));
        var corrupt = b.Snapshot with { EquityPoint = b.Snapshot.EquityPoint with { Cash = b.Snapshot.EquityPoint.Cash + 1m, Equity = b.Snapshot.EquityPoint.Equity + 1m } };
        Assert.Throws<InvalidDataException>(() => envelope.Verify(a.Snapshot, corrupt, resultA.Trades[0].ClosedGross, resultB.Trades[0].ClosedGross,
            resultA.Trades[0].ClosedNet, resultB.Trades[0].ClosedNet, 6m, 6m, resultA.Fills.Sum(f => f.SlippageAmount), resultB.Fills.Sum(f => f.SlippageAmount), 0m, bound));
    }

    [Fact] public void DecimalEndpointPropagationAllowsRoundedPriceBoundaryAndNeverUsesMlToleranceForMoney()
    {
        var costs = Costs(.0000000000000000000000000001m);
        var envelope = new RlFinancialEnvelope(costs);
        // Hand endpoint prices have 28 fractional digits; multiplication/subtraction exercise decimal rounding.
        decimal entry = 100.00000000000000000000000001m;
        envelope.RetainStopInterval(entry, .0499999999999999999999999999m, .0500000000000000000000000001m);
        var bar = new CandleData(Start, 100m, 101m, 94m, 100m, 1000L);
        var interval = envelope.StopFillInterval(bar);
        Assert.True(interval.Lower <= interval.Upper);
        Assert.Equal(94.99999999999999999999999999m, interval.Lower);
        Assert.Equal(95.00000000000000000000000001m, interval.Upper);
        var entryFill = new BacktestFill(1, 1, 64, Start.AddDays(64), OrderSide.Buy, entry, 2m, 3m, 0m);
        envelope.ApplyFill(entryFill, entryFill, OrderType.Market, bar, 0m, 0m, 0m, false);
        decimal lowerRaw = entry * (1m - .0500000000000000000000000001m);
        decimal upperRaw = entry * (1m - .0499999999999999999999999999m);
        var lower = new BacktestFill(2, 2, 65, Start.AddDays(65), OrderSide.Sell, interval.Lower, 2m, 3m, Math.Abs(interval.Lower - lowerRaw) * 2m);
        var upper = lower with { Price = interval.Upper, SlippageAmount = Math.Abs(interval.Upper - upperRaw) * 2m };
        decimal bound = envelope.ApplyFill(lower, upper, OrderType.Stop, bar, 2m, entry, 2m * entry, false);
        Assert.True(bound >= Math.Abs(lower.Price - upper.Price));
        var engine = new BacktestEngine(IndicatorFactory.Default);
        using var session = engine.CreateRlContinuousSession(Input(), Costs());
        var reset = session.Reset();
        var flat = reset with { EquityPoint = reset.EquityPoint with { Cash = 984m, Equity = 984m }, Drawdown = .016m, MaximumDrawdown = .016m };
        var checkedErrors = envelope.Verify(flat, flat, -10.00000000000000000000000004m, -10m,
            -16.00000000000000000000000004m, -16m, 6m, 6m, .00000000000000000000000002m, .00000000000000000000000002m,
            .00000000000000000000000002m, bound);
        Assert.Equal(0m, checkedErrors.CashError);
        Assert.True(checkedErrors.RealizedGrossError <= checkedErrors.RealizedGrossBound);
    }

    [Fact] public void AttemptsFailuresSuccessAndRegistrationReservationsAreDistinctAndNeverRolledBack()
    {
        var work = new RlWorkLedger(); work.Attempt(true); work.Succeeded(true);
        work.Attempt(false); // a failed/cancelled attempted Step publishes no financial success
        work.Attempt(false); work.Succeeded(false);
        work.ReserveRegistration(3, 1);
        var counts = work.Snapshot();
        Assert.Equal(5, counts.BudgetChargedTransitions); Assert.Equal(2, counts.TransitionAttempts);
        Assert.Equal(1, counts.SuccessfulFinancialTransitions); Assert.Equal(3, counts.RegistrationReservedTransitions);
        Assert.Equal(2, counts.BudgetChargedEpisodeStarts); Assert.Equal(1, counts.SuccessfulEpisodeStarts);
        Assert.Throws<InvalidOperationException>(() => work.RequireCapacity(RlContract.ProcessedTransitionLimit, 0));
        Assert.Equal(counts, work.Snapshot());
        var bounded = new RlWorkLedger(); bounded.ReserveRegistration(RlContract.ProcessedTransitionLimit, RlContract.EpisodeStartLimit);
        Assert.Throws<InvalidOperationException>(() => bounded.Attempt(false));
        Assert.Equal(RlContract.ProcessedTransitionLimit + 1, bounded.Snapshot().BudgetChargedTransitions);
        Assert.Equal(0, bounded.Snapshot().SuccessfulFinancialTransitions);
    }

    [Fact] public void IndividualTradeValuesCannotHideBehindMatchingTotalsAndEvents()
    {
        var input = Input(); using var session = new BacktestEngine(IndicatorFactory.Default).CreateRlContinuousSession(input, Costs());
        session.Reset(); session.Step(RlAction.Buy, 1f, .5f); session.Step(RlAction.Hold, 0f, 0f);
        var result = session.GetResult(); Assert.Single(result.Trades);
        RlContinuousParity.RequireTrades(result, result, input.Bars);
        var trade = result.Trades[0];
        foreach (var altered in new[] { trade with { ExitPrice = trade.ExitPrice + 1m },
            trade with { ClosedGross = trade.ClosedGross + 1m }, trade with { ClosedNet = trade.ClosedNet + 1m },
            trade with { EntrySlippage = trade.EntrySlippage + 1m }, trade with { ExitSlippage = trade.ExitSlippage + 1m },
            trade with { MfePrice = trade.MfePrice + 1m }, trade with { MaePrice = trade.MaePrice + 1m } })
            Assert.Throws<InvalidDataException>(() => RlContinuousParity.RequireTrades(result,
                new BacktestResult(result.Orders, result.Fills, result.Trades.SetItem(0, altered), result.EquityPoints,
                    result.Signals, result.Configuration, result.Status, result.StrategyName, result.ReproducibilityHash, result.IsInsufficientData), input.Bars));
    }
}
