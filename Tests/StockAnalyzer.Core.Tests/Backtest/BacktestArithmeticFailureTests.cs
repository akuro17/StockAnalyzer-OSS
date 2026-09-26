#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Tests.Backtest.Verification;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>
/// T5 (A1 (Run diagnostics), owner decision G3): <c>RunWithDiagnostics</c> turns an OverflowException raised by the ENGINE's own
/// decimal arithmetic into a Failed result holding only the fully committed bars, plus a diagnostic; legacy <c>Run</c> keeps throwing the original exception,
/// and strategy / cancellation / argument errors are never converted. Overflows use hand-computable magnitudes (decimal.MaxValue is about 7.9e28).
/// </summary>
public class BacktestArithmeticFailureTests
{
    private const decimal Huge = 5e28m;      // 3 * Huge overflows a decimal
    private const decimal Large = 2e28m;     // 3 * Large = 6e28 fits, but 6e28 + 6e28 does not

    private static CandleData Flat(int dayOffset, decimal price) => SyntheticBars.Bar(dayOffset, price, price, price, price);

    private static BacktestConfiguration Config() => VerificationHarness.MakeConfig(initialCapital: 1000m, sizingParameter: 3m);

    private static StrategyOrderRequest Req(SignalType type) => VerificationHarness.Req(type);

    private static ScriptedStrategy Script(Dictionary<int, StrategyOrderRequest> primary, Dictionary<int, StrategyOrderRequest>? accompanyingExit = null)
        => new(primary, accompanyingExit);

    private static BacktestDiagnosedRun Diagnose(ImmutableArray<CandleData> bars, IBacktestStrategy strategy, CancellationToken token = default)
        => VerificationHarness.CreateEngine().RunWithDiagnostics(VerificationHarness.MakeInput(bars), Config(), strategy, token);

    private static BacktestResult Legacy(ImmutableArray<CandleData> bars, IBacktestStrategy strategy)
        => VerificationHarness.CreateEngine().Run(VerificationHarness.MakeInput(bars), Config(), strategy);

    private static void AssertFailedAtBar(BacktestDiagnosedRun run, int barIndex, BacktestArithmeticOperation operation)
    {
        Assert.Equal(RunStatus.Failed, run.Result.Status);
        Assert.False(run.Result.IsInsufficientData);
        BacktestRunDiagnostic diagnostic = Assert.IsType<BacktestRunDiagnostic>(run.Diagnostic);
        Assert.Equal(BacktestDiagnosticCode.ArithmeticOverflow, diagnostic.Code);
        Assert.Equal(BacktestRunPhase.Bar, diagnostic.Phase);
        Assert.Equal(barIndex, diagnostic.BarIndex);
        Assert.Equal(operation, diagnostic.Operation);
        Assert.Equal(barIndex, run.Result.EquityPoints.Length); // exactly the bars before the failing one are committed
    }

    [Fact]
    public void EntryNotionalOverflow_FailsAtItsBar_KeepsTheCommittedPrefix_AndLegacyRunStillThrows()
    {
        // Long entry (Q=3) fills at bar 1's Open 5e28: 3 * 5e28 overflows while sizing the entry notional.
        ImmutableArray<CandleData> bars = ImmutableArray.Create(Flat(0, 100m), Flat(1, Huge));
        ScriptedStrategy Strategy() => Script(new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry) });

        BacktestDiagnosedRun run = Diagnose(bars, Strategy());

        AssertFailedAtBar(run, 1, BacktestArithmeticOperation.EntryAccounting);
        BacktestOrder order = Assert.Single(run.Result.Orders); // submitted on the committed bar 0: history, not a fabricated failure status
        Assert.Equal(OrderStatus.Submitted, order.Status);
        Assert.Empty(run.Result.Fills);
        Assert.Single(run.Result.Signals);
        Assert.Throws<OverflowException>(() => Legacy(bars, Strategy()));
    }

    [Fact]
    public void ExitPnLOverflow_IsReportedAsExitAccounting()
    {
        // Long held from bar 1's Open 100; the Market exit fills at bar 2's Open 5e28: 3 * (5e28 - 100) overflows the realized PnL.
        ImmutableArray<CandleData> bars = ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Flat(2, Huge));
        ScriptedStrategy Strategy() => Script(new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry), [1] = Req(SignalType.LongExit) });

        BacktestDiagnosedRun run = Diagnose(bars, Strategy());

        AssertFailedAtBar(run, 2, BacktestArithmeticOperation.ExitAccounting);
        Assert.Single(run.Result.Fills); // only the committed entry
        Assert.Empty(run.Result.Trades);
        Assert.Throws<OverflowException>(() => Legacy(bars, Strategy()));
    }

    [Fact]
    public void MarkToMarketOverflow_IsReportedAsMarkToMarket()
    {
        // Long held at 100; bar 2's Close 5e28 makes the unrealized PnL 3 * (5e28 - 100) overflow at Step 5.
        ImmutableArray<CandleData> bars = ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Flat(2, Huge));
        ScriptedStrategy Strategy() => Script(new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry) });

        BacktestDiagnosedRun run = Diagnose(bars, Strategy());

        AssertFailedAtBar(run, 2, BacktestArithmeticOperation.MarkToMarket);
        Assert.Single(run.Result.Fills);
        Assert.Throws<OverflowException>(() => Legacy(bars, Strategy()));
    }

    [Fact]
    public void FailureAfterTheFirstLegOfAReversal_LeavesNoHalfCommittedBar()
    {
        // Bar 2 (Open 2e28): the Market LongExit fills (leg 1: realized 3 * (2e28 - 100), Cash 6e28 + 700) and the Market ShortEntry fills (leg 2: notional 6e28), and
        // only then does the new Short's liquidation price (700 + 6e28 + 6e28) overflow. Everything the bar did must be rolled back.
        ImmutableArray<CandleData> bars = ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Flat(2, Large));
        ScriptedStrategy Strategy() => Script(
            new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry), [1] = Req(SignalType.ShortEntry) },
            new Dictionary<int, StrategyOrderRequest> { [1] = Req(SignalType.LongExit) });

        BacktestDiagnosedRun run = Diagnose(bars, Strategy());

        AssertFailedAtBar(run, 2, BacktestArithmeticOperation.LiquidationPricing);
        Assert.Single(run.Result.Fills);   // the bar-1 entry only: neither leg of bar 2 survives
        Assert.Empty(run.Result.Trades);
        Assert.Equal(3, run.Result.Orders.Length);
        Assert.Equal(OrderStatus.Submitted, run.Result.Orders[1].Status); // restored to its pre-bar state
        Assert.Equal(OrderStatus.Submitted, run.Result.Orders[2].Status);

        // The committed prefix equals what a run over just the first two bars produced (its end-of-data expiry only touches the still-pending orders).
        BacktestResult prefixOnly = Legacy(bars.Take(2).ToImmutableArray(), Strategy());
        Assert.True(prefixOnly.Fills.SequenceEqual(run.Result.Fills));
        Assert.True(prefixOnly.Trades.SequenceEqual(run.Result.Trades));
        Assert.True(prefixOnly.EquityPoints.SequenceEqual(run.Result.EquityPoints));
        Assert.Throws<OverflowException>(() => Legacy(bars, Strategy()));
    }

    [Fact]
    public void AnOverflowThrownByAStrategy_StaysAnException_OnBothEntryPoints()
    {
        ImmutableArray<CandleData> bars = ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m));
        var strategy = new OverflowingStrategy();

        Assert.Throws<OverflowException>(() => Diagnose(bars, strategy));
        Assert.Throws<OverflowException>(() => Legacy(bars, strategy));
    }

    [Fact]
    public void Cancellation_StaysOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => Diagnose(ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m)), Script(new Dictionary<int, StrategyOrderRequest>()), cts.Token));
    }

    [Fact]
    public void ArgumentErrors_StayExceptions()
    {
        BacktestEngine engine = VerificationHarness.CreateEngine();
        BacktestInput input = VerificationHarness.MakeInput(ImmutableArray.Create(Flat(0, 100m)));
        ScriptedStrategy strategy = Script(new Dictionary<int, StrategyOrderRequest>());

        Assert.Equal("input", Assert.Throws<ArgumentNullException>(() => engine.RunWithDiagnostics(null!, Config(), strategy)).ParamName);
        Assert.Equal("configuration", Assert.Throws<ArgumentNullException>(() => engine.RunWithDiagnostics(input, null!, strategy)).ParamName);
        Assert.Equal("strategy", Assert.Throws<ArgumentNullException>(() => engine.RunWithDiagnostics(input, Config(), null!)).ParamName);
    }

    [Fact]
    public void CompletedRun_HasNoDiagnostic_AndTheSameResultAsLegacyRun()
    {
        ImmutableArray<CandleData> bars = ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Flat(2, 110m), Flat(3, 110m));
        ScriptedStrategy Strategy() => Script(new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry), [2] = Req(SignalType.LongExit) });

        BacktestDiagnosedRun run = Diagnose(bars, Strategy());
        BacktestResult legacy = Legacy(bars, Strategy());

        Assert.Null(run.Diagnostic);
        Assert.Equal(RunStatus.Completed, run.Result.Status);
        Assert.Equal(legacy.ReproducibilityHash, run.Result.ReproducibilityHash);
        Assert.True(legacy.Orders.SequenceEqual(run.Result.Orders));
        Assert.True(legacy.Fills.SequenceEqual(run.Result.Fills));
        Assert.True(legacy.Trades.SequenceEqual(run.Result.Trades));
        Assert.True(legacy.EquityPoints.SequenceEqual(run.Result.EquityPoints));
    }

    [Fact]
    public void EmptyInput_HasNoDiagnostic_AndTheLegacyEmptyResult()
    {
        BacktestDiagnosedRun run = Diagnose(ImmutableArray<CandleData>.Empty, Script(new Dictionary<int, StrategyOrderRequest>()));

        Assert.Null(run.Diagnostic);
        Assert.True(run.Result.IsInsufficientData);
        Assert.Equal(RunStatus.Completed, run.Result.Status);
    }

    [Fact]
    public void OverflowWhilePreparingIndicators_IsAPreparationFailure_WithNoBarIndexAndAnEmptyPrefix()
    {
        // The batch preparation runs before any bar exists, so the diagnostic must carry no bar index (none is fabricated) and the prefix is empty.
        ImmutableArray<CandleData> bars = ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m));
        var engine = new BacktestEngine(new OverflowingIndicatorFactory());
        BacktestInput input = VerificationHarness.MakeInput(bars);

        BacktestDiagnosedRun run = engine.RunWithDiagnostics(input, Config(), VerificationHarness.SmaTrendStrategy(3));

        Assert.Equal(RunStatus.Failed, run.Result.Status);
        BacktestRunDiagnostic diagnostic = Assert.IsType<BacktestRunDiagnostic>(run.Diagnostic);
        Assert.Equal(BacktestRunPhase.Preparation, diagnostic.Phase);
        Assert.Null(diagnostic.BarIndex);
        Assert.Equal(BacktestArithmeticOperation.Unknown, diagnostic.Operation);
        Assert.Empty(run.Result.Orders);
        Assert.Empty(run.Result.EquityPoints);
        Assert.Throws<OverflowException>(() => engine.Run(input, Config(), VerificationHarness.SmaTrendStrategy(3)));
    }

    [Fact]
    public void AnIndicatorThatReportsItsOwnOverflowAsAnUnsuccessfulResult_StaysTheExistingInvalidOperation()
    {
        // The real SMA catches the overflow itself and returns an unsuccessful result; the engine's existing loud failure for that is not an arithmetic diagnostic.
        decimal price = 7e28m;
        ImmutableArray<CandleData> bars = ImmutableArray.Create(Flat(0, price), Flat(1, price), Flat(2, price), Flat(3, price));
        BacktestEngine engine = VerificationHarness.CreateEngine();
        BacktestInput input = VerificationHarness.MakeInput(bars);

        Assert.Throws<InvalidOperationException>(() => engine.RunWithDiagnostics(input, Config(), VerificationHarness.SmaTrendStrategy(3)));
        Assert.Throws<InvalidOperationException>(() => engine.Run(input, Config(), VerificationHarness.SmaTrendStrategy(3)));
    }

    /// <summary>A factory whose indicator creation overflows, standing in for a batch calculation that lets an OverflowException escape.</summary>
    private sealed class OverflowingIndicatorFactory : IIndicatorFactory
    {
        public ICoreIndicator? Create(IndicatorType type, CoreIndicatorParameterBase? parameters = null) => throw new OverflowException("indicator batch arithmetic");
        public bool IsRegistered(IndicatorType type) => true;
        public IEnumerable<IndicatorType> GetRegisteredTypes() => Array.Empty<IndicatorType>();
    }

    private sealed class OverflowingStrategy : IBacktestStrategy
    {
        public string Name => "Overflowing";

        public IReadOnlyList<StrategyIndicatorRequest> GetRequiredIndicators() => Array.Empty<StrategyIndicatorRequest>();

        public StrategyOrderRequest? Evaluate(StrategyContext context)
            => context.BarIndex == 1 ? throw new OverflowException("strategy arithmetic") : null;
    }
}
