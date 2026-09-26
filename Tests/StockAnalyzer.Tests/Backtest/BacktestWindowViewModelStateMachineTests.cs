using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Evaluation;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>
/// Task 17 acceptance tests for BacktestWindowViewModel's Run/Cancel state machine (spec §5.4). Uses
/// <see cref="ScriptableBacktestEngine"/> to hold a run in-flight so tests can act on the ViewModel
/// mid-run instead of racing a real background computation.
/// </summary>
public class BacktestWindowViewModelStateMachineTests
{
    [Fact]
    public async Task StandardRun_UsesOwnedEvaluationServiceAndPublishesItsArtifact()
    {
        var engine = new ScriptableBacktestEngine();
        engine.EnqueueResult(() => BacktestTestFactory.CreateResult());
        var generator = new BacktestReportGenerator();
        var evaluationService = new BacktestEvaluationService(
            engine,
            generator,
            new NoSamplingEvidenceTrustPolicy());
        BacktestWindowViewModel vm = BacktestTestFactory.CreateViewModel(
            engine: engine,
            reportGenerator: generator,
            evaluationService: evaluationService);
        vm.Symbol = "TEST";

        await vm.RunBacktestCommand.ExecuteAsync(null);

        Assert.Equal(1, engine.RunCallCount);
        Assert.Equal(BacktestRunState.Completed, vm.State);
        Assert.NotNull(vm.LastEvaluationArtifact);
        Assert.Same(vm.LastEvaluationArtifact!.Report, vm.LastReport);
        Assert.NotEmpty(vm.Results.AuditRows);
        Assert.Equal(SamplingStatus.Unverified, vm.LastEvaluationArtifact.Sampling.Status);
    }

    [Fact]
    public async Task Cancel_DuringReportGeneration_PreservesPublishedPresentation()
    {
        BacktestResult previousResult = BacktestTestFactory.CreateResult();
        BacktestReport previousReport = BacktestTestFactory.CreateStubReport();
        var generator = new BlockingCancellableReportGenerator();
        var engine = new ScriptableBacktestEngine();
        engine.EnqueueResult(() => BacktestTestFactory.CreateResult());
        BacktestWindowViewModel vm = BacktestTestFactory.CreateViewModel(engine: engine, reportGenerator: generator);
        vm.Symbol = "TEST";
        vm.Results.Update(previousResult, previousReport, 0);

        Task run = vm.RunBacktestCommand.ExecuteAsync(null);
        await generator.Entered.Task;
        vm.CancelBacktestCommand.Execute(null);
        await run;

        Assert.Equal(BacktestRunState.Cancelled, vm.State);
        Assert.Same(previousResult, vm.LastResult);
        Assert.Same(previousReport, vm.LastReport);
    }
    private static CandleData FlatBar(DateTime timestamp, decimal price = 100m) =>
        new(timestamp, price, price, price, price, 1000);

    [Fact]
    public void Cancel_BeforeRun_IsEnabledAndRequestsWindowClose()
    {
        var vm = BacktestTestFactory.CreateViewModel();
        int closeCount = 0;
        vm.RequestClose += () => closeCount++;

        Assert.Equal(BacktestRunState.Idle, vm.State);
        Assert.True(vm.CancelBacktestCommand.CanExecute(null));
        vm.CancelBacktestCommand.Execute(null);

        Assert.Equal(1, closeCount);
        Assert.Equal(BacktestRunState.Idle, vm.State);
    }

    [Fact]
    public async Task Cancel_AfterCompletedRun_ClosesWindow()
    {
        var engine = new ScriptableBacktestEngine();
        engine.EnqueueResult(() => BacktestTestFactory.CreateResult());
        BacktestWindowViewModel vm = BacktestTestFactory.CreateViewModel(engine: engine);
        vm.Symbol = "TEST";
        int closeCount = 0;
        vm.RequestClose += () => closeCount++;

        await vm.RunBacktestCommand.ExecuteAsync(null);
        Assert.Equal(BacktestRunState.Completed, vm.State);

        Assert.True(vm.CancelBacktestCommand.CanExecute(null));
        vm.CancelBacktestCommand.Execute(null);
        Assert.Equal(1, closeCount);
    }

    [Fact]
    public void InitialState_IsIdle_StateDisplayTextIsEmpty()
    {
        var vm = BacktestTestFactory.CreateViewModel();
        Assert.Equal(BacktestRunState.Idle, vm.State);
        Assert.Equal(string.Empty, vm.StateDisplayText);
    }

    [Fact]
    public async Task StrictEvidenceWithoutAdapter_TransitionsToUnsupportedWithoutLegacyRun()
    {
        var engine = new ScriptableBacktestEngine();
        BacktestWindowViewModel vm = BacktestTestFactory.CreateViewModel(engine: engine);
        vm.ExecutionModel = ExecutionModel.StrictEvidence;

        await vm.RunBacktestCommand.ExecuteAsync(null);

        Assert.Equal(BacktestRunState.Unsupported, vm.State);
        Assert.Equal("Backtest_State_Unsupported", vm.StateDisplayText);
        Assert.Equal("Backtest_StrictEvidence_AdapterUnavailable", vm.StatusMessage);
        Assert.Equal(0, engine.RunCallCount);
    }

    [Fact]
    public async Task DoubleClick_RunOnce()
    {
        var engine = new ScriptableBacktestEngine();
        engine.EnqueueResult(() => BacktestTestFactory.CreateResult());
        var vm = BacktestTestFactory.CreateViewModel(engine: engine);
        vm.Symbol = "TEST";

        Assert.True(vm.RunBacktestCommand.CanExecute(null));
        Task firstRun = vm.RunBacktestCommand.ExecuteAsync(null);
        // The method runs synchronously up to the first genuinely incomplete await (Task.Run wrapping
        // the engine call), so State is already Running by the time ExecuteAsync returns control here.
        Assert.Equal(BacktestRunState.Running, vm.State);

        // Simulated double-click: a bound button re-checks CanExecute before invoking Execute again
        // (spec §5.4: Run and Restore are disabled while Running), and CanRunBacktest() is already false here - a
        // second click cannot start a second run while this one is in flight.
        Assert.False(vm.RunBacktestCommand.CanExecute(null));

        await firstRun;

        Assert.Equal(1, engine.RunCallCount);
        Assert.Equal(BacktestRunState.Completed, vm.State);
    }

    [Fact]
    public async Task Cancel_DuringRun_PreviousResultMaintained()
    {
        var engine = new ScriptableBacktestEngine();
        var vm = BacktestTestFactory.CreateViewModel(engine: engine);
        vm.Symbol = "TEST";

        // Run 1: completes normally and seeds LastResult/LastReport.
        BacktestResult resultA = BacktestTestFactory.CreateResult(
            equityPoints: ImmutableArray.Create(new EquityPoint(0, vm.EvaluationStartUtc, 100_000m, 100_000m, 0m, 0m)));
        engine.EnqueueResult(() => resultA);
        await vm.RunBacktestCommand.ExecuteAsync(null);
        Assert.Equal(BacktestRunState.Completed, vm.State);
        Assert.Same(resultA, vm.LastResult);
        BacktestReport? reportA = vm.LastReport;
        Assert.NotNull(reportA);

        // Run 2: held in-flight, then cancelled before it can commit.
        BacktestResult resultB = BacktestTestFactory.CreateResult(
            equityPoints: ImmutableArray.Create(new EquityPoint(0, vm.EvaluationStartUtc, 999_999m, 999_999m, 0m, 0m)));
        engine.EnqueueResult(() => resultB);
        engine.Gate = new ManualResetEventSlim(initialState: false);

        Task secondRun = vm.RunBacktestCommand.ExecuteAsync(null);
        Assert.Equal(BacktestRunState.Running, vm.State);

        vm.CancelBacktestCommand.Execute(null);
        Assert.Equal(BacktestRunState.Cancelling, vm.State);

        engine.Gate.Set();
        await secondRun;

        Assert.Equal(BacktestRunState.Cancelled, vm.State);
        Assert.Same(resultA, vm.LastResult);
        Assert.Same(reportA, vm.LastReport);
    }

    /// <summary>
    /// P3 Hardening Task 5: proves real, cooperative cancellation - not just the pre-existing "discard on
    /// arrival" mechanism. <see cref="ScriptableBacktestEngine.Gate"/> is deliberately never Set() here: if
    /// Cancel only flipped State without actually aborting the blocked engine call, this test would hang
    /// waiting for a signal that never comes. Only a real CancellationToken threaded into the engine's
    /// Gate.Wait(cancellationToken) can unblock it via OperationCanceledException.
    /// </summary>
    [Fact]
    public async Task Cancel_DuringRun_AbortsEngineViaCancellationToken_ResolvesToCancelled()
    {
        var engine = new ScriptableBacktestEngine();
        engine.EnqueueResult(() => BacktestTestFactory.CreateResult()); // never actually dequeued
        engine.Gate = new ManualResetEventSlim(initialState: false);
        var vm = BacktestTestFactory.CreateViewModel(engine: engine);
        vm.Symbol = "TEST";

        Task run = vm.RunBacktestCommand.ExecuteAsync(null);
        Assert.Equal(BacktestRunState.Running, vm.State);

        vm.CancelBacktestCommand.Execute(null);
        Assert.Equal(BacktestRunState.Cancelling, vm.State);

        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(BacktestRunState.Cancelled, vm.State);
        Assert.Null(vm.StatusMessage);
        Assert.Equal(1, engine.RunCallCount);
    }

    /// <summary>
    /// Regression guard for the pre-existing, separately-confirmed rule that a genuine (non-cancellation)
    /// exception occurring while State is Cancelling still resolves to Failed, not Cancelled - this must
    /// keep holding now that <see cref="BacktestWindowViewModel"/> special-cases OperationCanceledException
    /// ahead of the generic failure branch. State is set directly (bypassing CancelBacktestCommand) because
    /// that command now also cancels the real CancellationToken, which would make the blocked engine call
    /// throw OperationCanceledException instead of the InvalidOperationException this test needs to exercise
    /// the orthogonal "Cancelling but not actually a cancellation" case.
    /// </summary>
    [Fact]
    public async Task NonCancellationException_WhileCancelling_StillResolvesToFailed()
    {
        var engine = new ScriptableBacktestEngine();
        engine.EnqueueResult(() => throw new InvalidOperationException("boom"));
        engine.Gate = new ManualResetEventSlim(initialState: false);
        var vm = BacktestTestFactory.CreateViewModel(engine: engine);
        vm.Symbol = "TEST";

        Task run = vm.RunBacktestCommand.ExecuteAsync(null);
        Assert.Equal(BacktestRunState.Running, vm.State);

        vm.State = BacktestRunState.Cancelling;
        engine.Gate.Set();

        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(BacktestRunState.Failed, vm.State);
        Assert.Equal("boom", vm.StatusMessage);
    }

    [Fact]
    public async Task DestroyWindow_DuringRun_NoCrash()
    {
        var engine = new ScriptableBacktestEngine();
        engine.EnqueueResult(() => BacktestTestFactory.CreateResult());
        engine.Gate = new ManualResetEventSlim(initialState: false);
        var vm = BacktestTestFactory.CreateViewModel(engine: engine);
        vm.Symbol = "TEST";

        Task run = vm.RunBacktestCommand.ExecuteAsync(null);
        Assert.Equal(BacktestRunState.Running, vm.State);

        // Window close while Running: request cancellation without blocking on the worker (spec §5.4).
        vm.OnWindowClosing();
        engine.Gate.Set();

        Exception? exception = await Record.ExceptionAsync(() => run);

        Assert.Null(exception);
        // The stale (superseded) generation's completion must be discarded entirely: State/LastResult
        // are left exactly as they were when the window closed, not overwritten by the late result.
        Assert.Equal(BacktestRunState.Running, vm.State);
        Assert.Null(vm.LastResult);
    }

    [Fact]
    public async Task ButtonTimeSnapshot_IsUsedAcrossLoadEngineAndReport_ThenNextRunUsesEdits()
    {
        DateTime firstDate = new(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        var data = new BlockingDataService(new[] { FlatBar(firstDate) });
        var engine = new ScriptableBacktestEngine();
        engine.EnqueueResult(() => BacktestTestFactory.CreateResult());
        engine.EnqueueResult(() => BacktestTestFactory.CreateResult());
        var reports = new StubBacktestReportGenerator();
        var vm = BacktestTestFactory.CreateViewModel(dataService: data, engine: engine, reportGenerator: reports);
        var parameters = new CoreSmaParameter { Period = 5 };

        vm.Symbol = "FIRST";
        vm.Frame = TimeFrame.D1;
        vm.EvaluationStartUtc = firstDate;
        vm.EvaluationEndUtc = firstDate;
        vm.AnnualRiskFreeRate = 0.01m;
        vm.IndicatorSelection.AddedIndicators.Add(new BacktestIndicatorSelectionItem(
            "sma", IndicatorType.SMA, "SMA", TimeFrame.W1, parameters));

        Task firstRun = vm.RunBacktestCommand.ExecuteAsync(null);
        await data.FirstCallEntered.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BacktestRunState.Validating, vm.State);

        vm.Symbol = "SECOND";
        vm.Frame = TimeFrame.W1;
        vm.EvaluationStartUtc = firstDate.AddDays(1);
        vm.EvaluationEndUtc = firstDate.AddDays(1);
        vm.AnnualRiskFreeRate = 0.09m;
        parameters.Period = 99;
        data.ReleaseFirstCall();
        await firstRun.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("FIRST", engine.LastInput!.Symbol);
        Assert.Equal(TimeFrame.D1, engine.LastInput.Frame);
        Assert.Equal(firstDate, engine.LastInput.EvaluationStartUtc);
        Assert.Equal(0.01m, reports.LastOptions!.AnnualRiskFreeRate);
        Assert.Equal(5, Assert.IsType<CoreSmaParameter>(Assert.Single(Assert.IsType<NoOpBacktestStrategy>(engine.LastStrategy).RequiredIndicators).Parameters).Period);

        await vm.RunBacktestCommand.ExecuteAsync(null);

        Assert.Equal("SECOND", engine.LastInput.Symbol);
        Assert.Equal(TimeFrame.W1, engine.LastInput.Frame);
        Assert.Equal(firstDate.AddDays(1), engine.LastInput.EvaluationStartUtc);
        Assert.Equal(0.09m, reports.LastOptions.AnnualRiskFreeRate);
        Assert.Equal(99, Assert.IsType<CoreSmaParameter>(Assert.Single(Assert.IsType<NoOpBacktestStrategy>(engine.LastStrategy).RequiredIndicators).Parameters).Period);
    }

    [Fact]
    public async Task CloseWhileValidating_CancelsPreparation_AndNeverStartsEngine()
    {
        DateTime date = new(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        var data = new BlockingDataService(new[] { FlatBar(date) });
        var engine = new ScriptableBacktestEngine();
        var reports = new StubBacktestReportGenerator();
        var vm = BacktestTestFactory.CreateViewModel(dataService: data, engine: engine, reportGenerator: reports);
        vm.Symbol = "TEST";
        vm.EvaluationStartUtc = date;
        vm.EvaluationEndUtc = date;

        Task run = vm.RunBacktestCommand.ExecuteAsync(null);
        await data.FirstCallEntered.WaitAsync(TimeSpan.FromSeconds(5));
        vm.OnWindowClosing();
        data.ReleaseFirstCall();

        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, engine.RunCallCount);
        Assert.Equal(0, reports.GenerateCallCount);
        Assert.Null(vm.LastResult);
    }

    [Fact]
    public async Task UnrelatedOperationCanceledException_IsFailed_NotCancelled()
    {
        using var unrelated = new CancellationTokenSource();
        unrelated.Cancel();
        var engine = new ScriptableBacktestEngine();
        engine.EnqueueResult(() => throw new OperationCanceledException(unrelated.Token));
        var vm = BacktestTestFactory.CreateViewModel(engine: engine);
        vm.Symbol = "TEST";

        await vm.RunBacktestCommand.ExecuteAsync(null);

        Assert.Equal(BacktestRunState.Failed, vm.State);
    }

    [Theory]
    [InlineData(RunStatus.Cancelled, BacktestRunState.Cancelled)]
    [InlineData(RunStatus.Failed, BacktestRunState.Failed)]
    [InlineData(RunStatus.Insolvent, BacktestRunState.Insolvent)]
    public async Task NonCompletedEngineResult_DoesNotGenerateReport(RunStatus status, BacktestRunState expectedState)
    {
        var engine = new ScriptableBacktestEngine();
        engine.EnqueueResult(() => BacktestTestFactory.CreateResult(status));
        var reports = new StubBacktestReportGenerator();
        var vm = BacktestTestFactory.CreateViewModel(engine: engine, reportGenerator: reports);
        vm.Symbol = "TEST";

        await vm.RunBacktestCommand.ExecuteAsync(null);

        Assert.Equal(expectedState, vm.State);
        Assert.Equal(0, reports.GenerateCallCount);
    }
}
