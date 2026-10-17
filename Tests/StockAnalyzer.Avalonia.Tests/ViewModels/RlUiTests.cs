using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using SkiaSharp;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Configuration;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;
using Xunit.Abstractions;
using StockAnalyzer.Avalonia.Tests.TestHelpers;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

[Collection("LocalizationSharedState")]
public sealed partial class RlUiTests(ITestOutputHelper output)
{
    private static RlQSnapshot Snapshot(params float[] q)
    {
        var start = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var bars = Enumerable.Range(0, RlContract.SupportLength).Select(i => new CandleData(start.AddDays(i), 100m, 110m, 90m, 100m, 1000)).ToImmutableArray();
        var input = new BacktestInput(bars, "SPY", TimeFrame.D1, BacktestInput.CurrentDataVersion, start, start.AddDays(bars.Length), 0, RlContract.WindowLength - 1);
        using var session = new BacktestEngine(IndicatorFactory.Default).CreateRlSession(input, RlContract.CreateConfiguration(new BacktestConfiguration { InitialCapital = 1000m, SizingModel = BacktestDefaults.SizingModel, SizingParameter = BacktestDefaults.SizingParameter }));
        var account = session.Reset();
        return new("request", "model", "SPY", account.MarketAnchor, q.ToImmutableArray(), RlContract.SelectAction(q), account, RlFiles.Disclosure, RlFiles.Target);
    }
    private static ChartViewModel Chart(IDataService data) => new(data, new DialogService(), null!, new MockStockAnalyzerSettings(), new TimeFrameManager(data), null!,
        new StockAnalyzer.Core.Theme.ThemeManager(), new MockChartSettingsManager(), new SynchronousDispatcherService(), null, null!, null, null, null, messenger: new StrongReferenceMessenger());

    [Fact] public void Presentation_ZeroAndNegativeQHaveSeparateAxisAndExactHoldFirstSelection()
    {
        var zero = RlPresentation.From(Snapshot(0,0,0), StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance);
        Assert.Equal(-(double)RlContract.FlatAxisHalfRange, zero.AxisMinimum);
        Assert.Equal((double)RlContract.FlatAxisHalfRange, zero.AxisMaximum);
        Assert.Equal(RlAction.Hold, zero.SelectedAction);
        var negative = RlPresentation.From(Snapshot(-3,-1,-2), StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance);
        Assert.True(negative.AxisMinimum < -3); Assert.True(negative.AxisMaximum > 0);
        Assert.Equal(RlAction.Buy, negative.SelectedAction);
        Assert.Contains("2020-03-04", negative.Legend);
        var extreme = RlPresentation.From(Snapshot(-float.MaxValue,float.MaxValue,0), StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance);
        Assert.True(double.IsFinite(extreme.AxisMinimum)); Assert.True(double.IsFinite(extreme.AxisMaximum));
        Assert.True(extreme.AxisMinimum < -float.MaxValue); Assert.True(extreme.AxisMaximum > float.MaxValue);
    }
    [Fact] public async Task Chart_ReplayShowsOnlyCompleted64BarsAndLateOrdinaryLoadCannotOverwrite()
    {
        var data = new DelayedData(); using var chart = Chart(data);
        chart.Symbol = "SPY";
        var ordinary = chart.LoadDataAsync(); await data.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var snapshot = Snapshot(0,1,-1); await chart.ApplyRlReplayAsync(snapshot, StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance);
        Assert.Equal(TimeframeType.Daily, chart.SelectedTimeFrame);
        Assert.Equal(RlContract.WindowLength, chart.Candles.Count);
        Assert.Equal(snapshot.Anchor, chart.Candles[^1].Timestamp);
        Assert.Equal(snapshot.RequestKey, chart.RlPresentation!.RequestKey);
        data.Result.TrySetResult(new[] { new CandleData(new DateTime(2099,1,1), 200m,200m,200m,200m,1) });
        await ordinary;
        Assert.Equal(snapshot.Anchor, chart.Candles[^1].Timestamp);
        chart.SelectedTimeFrame = TimeframeType.Weekly;
        Assert.Null(chart.RlPresentation);
    }
    [Fact] public async Task Chart_RemovingTheReplayRestoresTheNormalDataInsteadOfKeepingTheReplayWindow()
    {
        using var chart = Chart(new FixedData(300));
        chart.Symbol = "SPY";
        await chart.LoadDataAsync();
        int normalBars = chart.Candles.Count;                    // what the chart's range gives for this symbol
        Assert.True(normalBars > RlContract.WindowLength);

        await chart.ApplyRlReplayAsync(Snapshot(0,1,-1), StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance);
        Assert.Equal(RlContract.WindowLength, chart.Candles.Count);

        await chart.ClearRlReplayAsync();

        Assert.Null(chart.RlPresentation);
        Assert.Equal("SPY", chart.Symbol);
        Assert.Equal(normalBars, chart.Candles.Count);          // the range's data again, not the 64-bar replay window
    }
    [Fact] public async Task Chart_RemovingAReplayThatIsNotShownReloadsNothing()
    {
        var data = new FixedData(300); using var chart = Chart(data);
        chart.Symbol = "SPY"; await chart.LoadDataAsync();
        int loads = data.Loads;
        await chart.ClearRlReplayAsync();
        Assert.Equal(loads, data.Loads);
    }
    [Fact] public async Task Dialog_FreezesActualCostsAndLateSuccessAfterCancelDoesNotRegisterOrPresentSuccess()
    {
        using var chart = Chart(new ImmediateData()); var training = new DelayedTraining(); var defaults = new FakeBacktestDefaultSettingsManager();
        var registry = new EmptyRegistry();
        using var vm = new RlTrainingDialogViewModel(training, registry, new UnavailableQ(), defaults, chart, StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance);
        await vm.InitializeAsync(); vm.Symbol="SPY"; vm.StartDate="2000-01-01";vm.EndDateExclusive="2026-01-01";
        Assert.Equal(defaults.Settings.InitialCapital, vm.InitialCapital);
        vm.CommissionFlat=1.25m; vm.SmokeOnly=true;
        var savedBefore = defaults.Settings;
        var run = vm.TrainCommand.ExecuteAsync(null); await training.Started.Task;
        vm.CommissionFlat=99m; vm.CancelCommand.Execute(null);
        Assert.Equal(1.25m, training.Job!.Costs.CommissionFlat); Assert.True(training.Job.SmokeOnly);
        training.Result.SetResult(new(Guid.NewGuid(),"dir","model","eval",null,true,"revision")); await run;
        Assert.Equal("Cancelled",vm.Status); Assert.False(vm.IsBusy); Assert.Empty(vm.Generations);
        // The edited costs belong to this one training: the Settings > Backtest defaults (read by the Backtest window and the tickers tab too) are not written back.
        Assert.Same(savedBefore, defaults.Settings); Assert.NotEqual(1.25m, defaults.Settings.CommissionFlat);
    }
    [Fact] public async Task Dialog_StartingAndFinishingATrainingNeverWritesTheCostsOrOtherDefaultsBack()
    {
        using var chart = Chart(new ImmediateData()); var training = new DelayedTraining(); var defaults = new FakeBacktestDefaultSettingsManager();
        using var vm = new RlTrainingDialogViewModel(training, new EmptyRegistry(), new UnavailableQ(), defaults, chart, StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance);
        await vm.InitializeAsync(); vm.Symbol = "SPY"; vm.StartDate = "2000-01-01"; vm.EndDateExclusive = "2026-01-01";
        var savedBefore = defaults.Settings;
        vm.InitialCapital = 2_000_000m; vm.CommissionFlat = 3m; vm.CommissionPerUnit = 0.5m; vm.SlippageRatio = 0.002m; vm.LiquidationPenaltyRatio = 0.01m;
        var run = vm.TrainCommand.ExecuteAsync(null); await training.Started.Task;
        Assert.Equal((2_000_000m, 3m, 0.5m, 0.002m, 0.01m), (training.Job!.Costs.InitialCapital, training.Job.Costs.CommissionFlat, training.Job.Costs.CommissionPerUnit,
            training.Job.Costs.SlippageRatio, training.Job.Costs.LiquidationPenaltyRatio));          // the run uses the edited values
        vm.CancelCommand.Execute(null);
        training.Result.SetResult(new(Guid.NewGuid(), "dir", "model", "eval", null, true, "revision")); await run;
        Assert.Same(savedBefore, defaults.Settings);                                                // and nothing was saved
    }
    [Fact] public async Task DefaultValueInventory_TheOptionsStartOffAndTheCostsComeFromTheBacktestDefaultsOrTheBuiltInFallback()
    {
        using var chart = Chart(new ImmediateData());
        // The settings file is missing or invalid: the built-in values apply silently (inventory: 1,000,000 / 0 / 0 / 0 / 0.005).
        using (var vm = new RlTrainingDialogViewModel(new DelayedTraining(), new EmptyRegistry(), new UnavailableQ(),
                   new FakeBacktestDefaultSettingsManager(BacktestDefaultSettingsLoadStatus.BuiltInFallback), chart, StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance))
        {
            await vm.InitializeAsync();
            Assert.False(vm.UseContinuousPolicy); Assert.False(vm.UseAllCompleteBlocks); Assert.False(vm.SmokeOnly); Assert.False(vm.UseShortHistory);
            Assert.Equal((1_000_000m, 0m, 0m, 0m, 0.005m), (vm.InitialCapital, vm.CommissionFlat, vm.CommissionPerUnit, vm.SlippageRatio, vm.LiquidationPenaltyRatio));
            Assert.Empty(vm.ModeBadges);                                    // nothing is labelled at the defaults
        }
        // A saved Settings > Backtest file wins.
        var saved = new BacktestDefaultSettings { SchemaVersion = BacktestDefaultSettings.CurrentSchemaVersion, InitialCapital = 250_000m, CommissionFlat = 1m, CommissionPerUnit = 0.1m, SlippageRatio = 0.001m, LiquidationPenaltyRatio = 0.02m };
        using (var vm = new RlTrainingDialogViewModel(new DelayedTraining(), new EmptyRegistry(), new UnavailableQ(),
                   new FakeBacktestDefaultSettingsManager(settings: saved), chart, StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance))
        {
            await vm.InitializeAsync();
            Assert.Equal((250_000m, 1m, 0.1m, 0.001m, 0.02m), (vm.InitialCapital, vm.CommissionFlat, vm.CommissionPerUnit, vm.SlippageRatio, vm.LiquidationPenaltyRatio));
        }
    }
    [Fact] public async Task ClosingDialogPreservesPublishedReplayWhileChartChangesInvalidateIt()
    {
        using var chart = Chart(new ImmediateData());
        using var vm = new RlTrainingDialogViewModel(new DelayedTraining(), new EmptyRegistry(), new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), chart, StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance);
        var snapshot = Snapshot(0, 1, -1);
        await chart.ApplyRlReplayAsync(snapshot, StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance);
        vm.Dispose();
        Assert.Equal(snapshot.RequestKey, chart.RlPresentation!.RequestKey);
        chart.SelectedTimeFrame = TimeframeType.Weekly;
        Assert.Null(chart.RlPresentation);
    }
    [Fact] public async Task ContinuousModeRequiresExplicitDirectionAndFreezesItsIdentityBeforeAwait()
    {
        using var chart = Chart(new ImmediateData()); var training = new DelayedTraining();
        var direction = new RlGeneration(new string('a', 64), "SPY", "revision", RlContract.ContractHash, "evaluation", 0m, 0m, new(), DateTimeOffset.UtcNow);
        using var vm = new RlTrainingDialogViewModel(training, new EmptyRegistry { Generations = new[] { direction } }, new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), chart, StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance);
        await vm.InitializeAsync(); Assert.False(vm.UseAllCompleteBlocks); vm.UseContinuousPolicy = true; vm.UseAllCompleteBlocks = true; vm.Symbol = "SPY"; vm.StartDate = "2000-01-01"; vm.EndDateExclusive = "2026-01-01";
        Assert.False(vm.CanTrain); await vm.TrainCommand.ExecuteAsync(null); Assert.Null(training.Job);
        string id = new('a', 64);
        vm.SelectedDirectionGeneration = vm.DirectionGenerations.Single();
        var run = vm.TrainCommand.ExecuteAsync(null); await training.Started.Task;
        vm.SelectedDirectionGeneration = null; vm.UseContinuousPolicy = false; vm.CancelCommand.Execute(null);
        Assert.Equal(id, training.Job!.FrozenDirectionModelId);
        Assert.Equal(RlAllBlockEvaluationContract.SpecificationId, training.Job.EvaluationSpecificationId);
        training.Result.SetResult(new(Guid.NewGuid(), "dir", "model", "eval", null, true, "revision")); await run;
        Assert.Equal("Cancelled", vm.Status);
    }
    [Fact] public async Task ExpandedOptionIsIgnoredForAnExplicitDirectionJob()
    {
        using var chart = Chart(new ImmediateData()); var training = new DelayedTraining();
        using var vm = new RlTrainingDialogViewModel(training, new EmptyRegistry(), new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), chart, StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance);
        await vm.InitializeAsync(); vm.UseAllCompleteBlocks = true; vm.Symbol = "SPY"; vm.StartDate = "2000-01-01"; vm.EndDateExclusive = "2026-01-01";
        var run = vm.TrainCommand.ExecuteAsync(null); await training.Started.Task;
        Assert.Null(training.Job!.EvaluationSpecificationId); Assert.Null(training.Job.FrozenDirectionModelId);
        vm.CancelCommand.Execute(null); training.Result.SetResult(new(Guid.NewGuid(), "dir", "model", "eval", null, true, "revision")); await run;
    }
    [Theory] [InlineData("en")] [InlineData("ja")]
    public async Task RecoveryRequiresExplicitCommandAndFreezesTheReviewedCandidate(string language)
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "StockAnalyzer.sln"))) repo = repo.Parent;
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo!.FullName, "StockAnalyzer.Avalonia", "Resources", "Locales", language + ".json")));
        var localization = new FixtureLocalization(json.RootElement);
        using var chart = Chart(new ImmediateData());
        var reviewed = new RlPointerRecovery(RlFiles.Target, true, "malformed pointer", new string('a', 64), "current", "backup");
        var registry = new EmptyRegistry { Capabilities = new(false, true, false), Recovery = reviewed, RecoveryCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var vm = new RlTrainingDialogViewModel(new DelayedTraining(), registry, new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), chart, localization);
        await vm.InitializeAsync(); Assert.True(vm.CanRecover); Assert.Null(registry.LastRecovered);
        Assert.Equal("Unavailable", vm.Status); Assert.Contains(reviewed.BackupModelId!, vm.RecoveryDisplay);
        var command = vm.RecoverPointerCommand.ExecuteAsync(null);
        await registry.RecoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsBusy); Assert.False(vm.CanRecover); Assert.Equal(reviewed, registry.LastRecovered);
        registry.Recovery = reviewed with { Unavailable = false, Error = null, BackupModelId = null };
        registry.RecoveryCompletion.SetResult(true); await command;
        Assert.Equal("Ready", vm.Status); Assert.False(vm.CanRecover);
        Assert.Contains(reviewed.BackupModelId!, vm.Message); Assert.Equal(localization.GetString("Rl.RecoveryHealthy"), vm.RecoveryDisplay);
    }
    [Fact] public async Task MissingVerifiedRecoveryCandidateLeavesTheCommandDisabled()
    {
        using var chart = Chart(new ImmediateData());
        var registry = new EmptyRegistry { Capabilities = new(false, true, false), Recovery = new(RlFiles.Target, true, "wrong target", null, "current", "backup") };
        using var vm = new RlTrainingDialogViewModel(new DelayedTraining(), registry, new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), chart, StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance);
        await vm.InitializeAsync(); Assert.False(vm.CanRecover); Assert.False(vm.RecoverPointerCommand.CanExecute(null)); Assert.Null(registry.LastRecovered);
    }
    [Fact] public async Task ChangingRecoveryTargetImmediatelyInvalidatesThePreviousCandidate()
    {
        using var chart = Chart(new ImmediateData());
        var registry = new EmptyRegistry { Capabilities = new(false, true, false), Recovery = new(RlFiles.Target, true, "malformed", new string('a', 64), "current", "backup") };
        using var vm = new RlTrainingDialogViewModel(new DelayedTraining(), registry, new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), chart, StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance);
        await vm.InitializeAsync(); Assert.True(vm.CanRecover);
        vm.UseContinuousPolicy = true;
        Assert.False(vm.CanRecover);
        await vm.InspectRecoveryCommand.ExecuteAsync(null);
        Assert.Null(vm.RecoveryPreview); Assert.Null(registry.LastRecovered);
    }
    [Theory] [InlineData("en")] [InlineData("ja")]
    public void ExpandedDistributionFormatsDecimalPercentagesAndKeepsQUnits(string language)
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "StockAnalyzer.sln"))) repo = repo.Parent;
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo!.FullName, "StockAnalyzer.Avalonia", "Resources", "Locales", language + ".json")));
        var localization = new FixtureLocalization(json.RootElement); var snapshot = Snapshot(0, 1, -1);
        var distribution = new RlReturnDistribution(6, .0125m, .005m, -.05m, .1m, .5m);
        var coverage = new RlEvaluationCoverage(snapshot.Anchor, snapshot.Anchor, 6218, snapshot.Anchor, snapshot.Anchor, 6154, ImmutableArray<RlPartitionCoverage>.Empty);
        var report = new RlAllBlockEvaluationReport(RlAllBlockEvaluationContract.SpecificationId, 2, "source", "model", "direction",
            RlContract.CreateConfiguration(new BacktestConfiguration { InitialCapital = 1000000m, SizingModel = BacktestDefaults.SizingModel, SizingParameter = BacktestDefaults.SizingParameter }),
            coverage, distribution, distribution, distribution, distribution, RlAllBlockEvaluation.Estimate(6, 6, 6218, 1000, false),
            RlConstraintContract.SelectionMetric, RlSourceContract.Unverified, RlConstraintContract.InvestmentEfficacy);
        var presentation = RlPresentation.From(snapshot with { AllBlockEvaluation = report }, localization);
        Assert.Contains(localization.GetString("Rl.AllBlockMode"), presentation.EvaluationReadout);
        Assert.Contains("1.250000%", presentation.EvaluationReadout); Assert.Contains("50.000000%", presentation.EvaluationReadout);
        Assert.Contains(localization.GetString("Rl.AllBlockDisclosure"), presentation.EvaluationReadout);
        Assert.Equal(RlPresentation.From(snapshot, localization).Units, presentation.Units);
    }
    [Theory] [InlineData("en")] [InlineData("ja")]
    public async Task R08_SettingsRequireExplicitReviewAndConfirmationAndEditsInvalidateIt(string language)
    {
        var repo=new DirectoryInfo(AppContext.BaseDirectory);
        while(repo is not null && !File.Exists(Path.Combine(repo.FullName,"StockAnalyzer.sln"))) repo=repo.Parent;
        using var json=JsonDocument.Parse(File.ReadAllText(Path.Combine(repo!.FullName,"StockAnalyzer.Avalonia","Resources","Locales",language+".json")));
        var localization=new FixtureLocalization(json.RootElement);
        using var chart=Chart(new ImmediateData());
        var registry=new EmptyRegistry { Capabilities = new(true, true, true), RlArtifactPolicy=new() };
        using var vm=new RlTrainingDialogViewModel(new DelayedTraining(),registry,new UnavailableQ(),new FakeBacktestDefaultSettingsManager(),chart,localization);
        await vm.InitializeAsync(); Assert.True(vm.StorageAvailable); Assert.Equal(10m,vm.StorageQuotaGiB);
        Assert.Equal(5m,vm.StorageMinimumFreeGiB); Assert.Equal(7,vm.StorageFailedRetentionDays);
        Assert.Null(vm.StorageNonactiveDays); Assert.Null(vm.StorageKeepCount); Assert.False(vm.CanCleanupArtifacts);
        Assert.Equal(localization.GetString("Rl.Storage.Incomplete"),vm.StorageDisplay);
        vm.StorageNonactiveDays=0; vm.StorageKeepCount=1; await vm.SaveArtifactSettingsCommand.ExecuteAsync(null);
        Assert.True(registry.RlArtifactPolicy!.IsComplete);
        registry.ArtifactPreview=new(registry.RlArtifactPolicy,"review",100,1000,ImmutableArray.Create(
            new RlArtifactEntry("owned",RlArtifactKind.FailedRun.ToString(),"owned",100,DateTimeOffset.UtcNow,"identity",ImmutableArray.Create("ExpiredFailure"),true)));
        await vm.InspectArtifactsCommand.ExecuteAsync(null); Assert.Single(vm.ArtifactRows); Assert.Null(registry.LastCleanup);
        Assert.False(vm.CanCleanupArtifacts); vm.CleanupConfirmed=true; Assert.True(vm.CanCleanupArtifacts);
        vm.StorageQuotaGiB=11; Assert.False(vm.CanCleanupArtifacts); Assert.False(vm.CleanupConfirmed); Assert.Empty(vm.ArtifactRows);
        await vm.InspectArtifactsCommand.ExecuteAsync(null); Assert.Empty(vm.ArtifactRows);
        Assert.Equal(localization.GetString("Rl.Storage.Unsaved"),vm.StorageDisplay);
        await vm.SaveArtifactSettingsCommand.ExecuteAsync(null);
        registry.ArtifactPreview=registry.ArtifactPreview with { Policy=registry.RlArtifactPolicy! };
        await vm.InspectArtifactsCommand.ExecuteAsync(null); vm.CleanupConfirmed=true;
        await vm.CleanupArtifactsCommand.ExecuteAsync(null);
        Assert.Same(registry.ArtifactPreview,registry.LastCleanup); Assert.False(vm.CleanupConfirmed); Assert.False(vm.CanCleanupArtifacts);
        Assert.Contains("0",vm.StorageDisplay);
    }
    [Theory] [InlineData("en")] [InlineData("ja")]
    public async Task R09_PauseAndReviewedResumeAreSeparateFromCancelAndAbandonRequiresConfirmation(string language)
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "StockAnalyzer.sln"))) repo = repo.Parent;
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo!.FullName, "StockAnalyzer.Avalonia", "Resources", "Locales", language + ".json")));
        var localization = new FixtureLocalization(json.RootElement);
        using var chart = Chart(new ImmediateData());
        var review = new RlCheckpointReview(Guid.NewGuid(), "owned", Guid.NewGuid().ToString("N"), RlContract.CandidateSeeds[0], RlContinuousContract.RolloutSteps, "train", "review", true, null);
        var generation = new RlGeneration(new string('a', 64), "SPY", "source", RlContract.ContractHash, "evaluation", 0m, 0m, new(), DateTimeOffset.UtcNow);
        var registry = new EmptyRegistry { Capabilities = new(true, true, true), RlArtifactPolicy = new(NonactiveRetentionDays: 0, NonactiveKeepCount: 1),
            Generations = new[] { generation }, Checkpoints = new[] { review } };
        var training = new DelayedTraining();
        using var vm = new RlTrainingDialogViewModel(training, registry, new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), chart, localization);
        await vm.InitializeAsync(); Assert.True(vm.CheckpointAvailable); Assert.Equal(2, vm.StorageCheckpointKeepCount);
        vm.UseContinuousPolicy = true; vm.SelectedDirectionGeneration = generation;
        vm.Symbol = "SPY"; vm.StartDate = "2004-01-01"; vm.EndDateExclusive = "2026-10-04";
        var run = vm.TrainCommand.ExecuteAsync(null); await training.Started.Task;
        Assert.True(vm.CanPause); vm.PauseCommand.Execute(null);
        Assert.True(training.PauseRequest.IsCancellationRequested); Assert.False(training.CancelRequest.IsCancellationRequested);
        training.Result.SetException(new RlTrainingPausedException(review.RunId, review.RunDirectory)); await run;
        Assert.Equal("Paused", vm.Status); Assert.Single(vm.CheckpointRows); Assert.False(vm.CanResumeCheckpoint);
        vm.SelectedCheckpoint = vm.CheckpointRows[0]; vm.ResumeConfirmed = true; Assert.True(vm.CanResumeCheckpoint);
        vm.SelectedCheckpoint = null; Assert.False(vm.ResumeConfirmed); Assert.False(vm.CanResumeCheckpoint);
        vm.SelectedCheckpoint = vm.CheckpointRows[0]; vm.ResumeConfirmed = true;
        var resume = vm.ResumeCheckpointCommand.ExecuteAsync(null); await training.ResumeStarted.Task;
        Assert.Equal(review, training.LastResume); Assert.True(vm.CanPause);
        training.ResumeResult.SetResult(new(review.RunId, "owned", "owned/model", "owned/report", null, true, "source")); await resume;
        Assert.Equal("Succeeded", vm.Status); Assert.False(vm.ResumeConfirmed);
        vm.SelectedCheckpoint = vm.CheckpointRows[0]; Assert.False(vm.CanAbandonCheckpoint);
        vm.AbandonConfirmed = true; Assert.True(vm.CanAbandonCheckpoint); await vm.AbandonCheckpointCommand.ExecuteAsync(null);
        Assert.Equal(review, registry.LastAbandoned); Assert.Empty(vm.CheckpointRows);
    }
    [Fact] public void ContinuousPresentationPreservesQUnitsAndPublishesSeparateReadout()
    {
        var snapshot = Snapshot(0, 1, -1) with { PolicyId = RlContinuousContract.Target,
            Continuous = new(RlContinuousContract.Map(0, 0), 1m, 97.25m, null, "Planned", "frozen") };
        var p = RlPresentation.From(snapshot, StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance);
        Assert.True(p.HasContinuousReadout); Assert.Equal(RlContinuousContract.Target, p.PolicyId);
        Assert.Equal(RlAction.Buy, p.SelectedAction); Assert.Equal("Rl.QUnit", p.Units);
        Assert.False(RlPresentation.From(Snapshot(0, 1, -1), StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance).HasContinuousReadout);
        Assert.Contains("Rl.Temporal.Legacy", p.ContinuousReadout);
        var qualified = RlPresentation.From(snapshot with { TemporalSpecificationId = RlTemporalContract.SpecificationId,
            TemporalStatus = RlTemporalBoundStatus.ConservativeUpperBound }, StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance);
        Assert.Contains("Rl.Temporal.ConservativeUpperBound", qualified.ContinuousReadout);
    }
    [Theory] [InlineData("en")] [InlineData("ja")]
    public async Task R04ShowsProposalAndFixedControlsSeparatelyAndClearsBothOnSymbolAndTimeframe(string language)
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "StockAnalyzer.sln"))) repo = repo.Parent;
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo!.FullName, "StockAnalyzer.Avalonia", "Resources", "Locales", language + ".json")));
        var localization = new FixtureLocalization(json.RootElement);
        var snapshot = Snapshot(0, 1, -1) with { PolicyId = RlContinuousContract.PairedBaselineId,
            Continuous = new(RlContinuousContract.Map(0, 0), 2m, 98m, null, "Planned", "frozen")
                { SelectedPolicyControl = new(.30m, .02m, false), PolicyAction = RlAction.Buy } };
        var p = RlPresentation.From(snapshot, localization);
        Assert.Contains("15.00", p.ContinuousReadout); Assert.Contains("2.75", p.ContinuousReadout);
        Assert.Contains("30.00%", p.PolicyControlReadout); Assert.Contains("2.00%", p.PolicyControlReadout);
        Assert.Contains(localization.GetString("Rl.ControlState.Planned"), p.PolicyControlReadout);
        var unavailable = RlPresentation.From(snapshot with { Continuous = snapshot.Continuous! with { SelectedPolicyControl = null } }, localization);
        Assert.Contains(localization.GetString("Rl.State.Unavailable"), unavailable.PolicyControlReadout);
        Assert.DoesNotContain(localization.GetString("Rl.Control.Prospective"), unavailable.PolicyControlReadout);
        using var chart = Chart(new ImmediateData());
        await chart.ApplyRlReplayAsync(snapshot, localization); Assert.True(chart.RlPresentation!.HasPolicyControlReadout);
        chart.Symbol = "AAPL"; Assert.Null(chart.RlPresentation);
        await chart.ApplyRlReplayAsync(snapshot, localization);
        chart.SelectedTimeFrame = TimeframeType.Weekly; Assert.Null(chart.RlPresentation);
    }
    private sealed class FixtureLocalization(JsonElement strings) : StockAnalyzer.Core.Interfaces.ILocalizationService
    {
        public string GetString(string key) => strings.GetProperty(key).GetString()!;
    }
    [Theory] [InlineData("en", "quantity limit 1", "filled entries 0")] [InlineData("ja", "\u6570\u91cf\u4e0a\u9650\u8d85\u904e 1", "\u6210\u7acb\u30a8\u30f3\u30c8\u30ea\u30fc 0")] // ja: quantity-cap exceeded 1 / filled entries 0
    public void R04ReportDistinguishesRealCapRejectionFromFlatHold(string language, string capText, string entryText)
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "StockAnalyzer.sln"))) repo = repo.Parent;
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo!.FullName, "StockAnalyzer.Avalonia", "Resources", "Locales", language + ".json")));
        var localization = new FixtureLocalization(json.RootElement);
        var account = Snapshot(0, 1, -1).Account;
        var bars = Enumerable.Range(0, RlContract.SupportLength).Select(i => account.MarketWindow[0] with { Timestamp = account.MarketWindow[0].Timestamp.AddDays(i) }).ToImmutableArray();
        var input = new BacktestInput(bars, "SPY", TimeFrame.D1, BacktestInput.CurrentDataVersion, bars[0].Timestamp,
            bars[^1].Timestamp, 0, RlContract.WindowLength - 1);
        using var session = new BacktestEngine(IndicatorFactory.Default).CreateRlContinuousSession(input,
            RlContract.CreateConfiguration(new BacktestConfiguration { InitialCapital = 3333667m,
                SizingModel = BacktestDefaults.SizingModel, SizingParameter = BacktestDefaults.SizingParameter }));
        session.Reset();
        var rejected = session.StepFixedControl(RlAction.Buy).EntryExecution!;
        Assert.Equal(RejectedReason.QuantityCapExceeded, rejected.RejectionReason);
        var cap = RlConstraintEvaluation.Summarize(new[] { rejected });
        var hold = RlConstraintEvaluation.Summarize(Array.Empty<RlEntryExecution>());
        var coverage = new RlEvaluationCoverage(bars[0].Timestamp, bars[^1].Timestamp, bars.Length,
            bars[RlContract.WindowLength - 1].Timestamp, bars[^2].Timestamp, bars.Length - RlContract.WindowLength,
            ImmutableArray<RlPartitionCoverage>.Empty);
        var report = new RlConstraintReport(RlConstraintContract.SpecificationId, "fixture", RlContinuousContract.ContractHash,
            coverage, hold, hold, ImmutableArray<RlPairedPolicyDelta>.Empty,
            ImmutableArray.Create(new RlPolicyConstraintSummary("validation", RlContinuousContract.PairedBaselineId, 1, cap),
                new RlPolicyConstraintSummary("validation", RlContract.HoldBaselineId, 1, hold)),
            ImmutableArray<RlTerminalMarkSummary>.Empty, RlConstraintContract.SelectionMetric, RlContract.Gamma,
            RlContract.EpisodeHorizon, RlSourceContract.Unverified, "FinancialParityVerified",
            RlTemporalBoundStatus.ConservativeUpperBound, RlConstraintContract.InvestmentEfficacy);
        string rendered = RlPresentation.FormatEvaluation(report, localization);
        Assert.Contains(capText, rendered); Assert.Contains(entryText, rendered);
        Assert.Contains(localization.GetString("Rl.ConstraintStatus.NoAdmittedBuyBecauseOfCap"), rendered);
        Assert.Contains(localization.GetString("Rl.ConstraintStatus.NotApplicable"), rendered);
        Assert.Contains(localization.GetString("Rl.ConstraintDisclosure"), rendered);
    }
    [Fact] public async Task TemporalRejectionIsLocalizedAndRefreshKeepsExplicitDirectionSelection()
    {
        using var chart = Chart(new ImmediateData());
        var generation = new RlGeneration(new string('a', 64), "SPY", "source", RlContract.ContractHash, "evaluation", 0m, 0m, new(), DateTimeOffset.UtcNow);
        var registry = new EmptyRegistry { Generations = new[] { generation } };
        var training = new DelayedTraining();
        using var vm = new RlTrainingDialogViewModel(training, registry, new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), chart, StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance);
        await vm.InitializeAsync(); vm.UseContinuousPolicy = true; vm.SelectedDirectionGeneration = generation;
        vm.Symbol = "SPY"; vm.StartDate = "2004-01-01"; vm.EndDateExclusive = "2026-10-04";
        var run = vm.TrainCommand.ExecuteAsync(null); await training.Started.Task;
        training.Result.SetException(new RlTemporalException(RlTemporalRejection.FutureInformation, "internal-message",
            new DateOnly(2027, 1, 1), new DateOnly(2004, 4, 1)));
        await run;
        Assert.Equal("Rejected", vm.Status); Assert.Equal("Rl.Temporal.Rejection.FutureInformation", vm.Message);
        Assert.Equal(generation.ModelId, vm.SelectedDirectionGeneration!.ModelId);
        await vm.InitializeAsync();
        Assert.Equal(generation.ModelId, vm.SelectedDirectionGeneration!.ModelId);
        Assert.Equal("Rl.Temporal.Unverified", vm.TemporalStatusDisplay);
    }
    /// <summary>Measured windows of <see cref="RlContract.InferenceMeasuredCalls"/> draws; the verdict is taken from the smallest one.</summary>
    private const int AllocationWindowCount = 20;

    /// <summary>
    /// The cached draw paths allocate nothing. <see cref="GC.GetAllocatedBytesForCurrentThread"/> is not exact while other threads trigger garbage collections: a GC inside
    /// the measured window adds roughly one allocation chunk to this thread's reading although the draws allocated nothing (captured here in full-suite runs: 3,592 B and 6,144 B,
    /// the guideline's "Allocation Measurements Take the Smallest of Several Short Windows"). The artifact only ever ADDS to a reading, whereas a real allocation adds to EVERY
    /// window, so the verdict is the smallest reading of several windows: 0 unless every window was disturbed, above 0 whenever the draws allocate.
    /// </summary>
    [Fact] public void CachedSkiaDrawPaths_Warmed100Measured1000HaveZeroManagedAllocation()
    {
        using var renderer = new RlQRenderer(); using var bitmap = new SKBitmap(1200, 600); using var canvas = new SKCanvas(bitmap);
        var p = RlPresentation.From(Snapshot(-0.01f,0.2f,-0.2f), StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance); var panel = new global::Avalonia.Rect(0,0,1200,(double)RlContract.PanelHeight);
        var chart = new global::Avalonia.Rect(0,0,1200,400);
        renderer.PreparePanel(p,14,12); renderer.PrepareHud(p,14);
        void Draw() { renderer.DrawPanel(canvas,panel,p,14,12,SKColors.Gray,SKColors.Gray,SKColors.Green,SKColors.Red); renderer.DrawHud(canvas,chart,p,14,SKColors.Gray); }
        for(int i=0;i<RlContract.InferenceWarmupCalls;i++)Draw();
        using var process = Process.GetCurrentProcess(); process.Refresh(); long beforeNative=process.PrivateMemorySize64;
        var windowReadings = new long[AllocationWindowCount];
        for (int window = 0; window < windowReadings.Length; window++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < RlContract.InferenceMeasuredCalls; i++) Draw();
            windowReadings[window] = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        process.Refresh(); long afterNative=process.PrivateMemorySize64;
        output.WriteLine($"{RlContract.InferenceWarmupCalls} warmup/{windowReadings.Length} windows of {RlContract.InferenceMeasuredCalls} cached panel+HUD draws: managed per window={string.Join(", ", windowReadings)} B; process private memory proxy before={beforeNative}, after={afterNative}, delta={afterNative-beforeNative} B (process-wide, not isolated native allocator accounting).");
        Assert.True(windowReadings.Min() == 0, $"The smallest of {windowReadings.Length} windows of {RlContract.InferenceMeasuredCalls} draws still read {windowReadings.Min()} B; readings: {string.Join(", ", windowReadings)}");
    }

    [Fact]
    public void RlPresentation_ExtractsOverlayPropertiesAndCoordinatesSuccessfully()
    {
        var snapshot = Snapshot(0, 1, -1);
        var p = RlPresentation.From(snapshot, StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance);
        Assert.NotNull(p.AnchorDate);
        Assert.True(p.HasDecisionOverlay);
        Assert.NotNull(p.CurrentPrice);
        Assert.Equal(RlAction.Buy, p.SelectedAction);

        using var renderer = new RlQRenderer();
        using var bitmap = new SKBitmap(1200, 600);
        using var canvas = new SKCanvas(bitmap);
        var transform = new StockAnalyzer.Avalonia.Drawing.LinearCoordinateTransform(
            snapshot.Anchor.AddDays(-30), snapshot.Anchor.AddDays(5),
            50m, 150m, 1200, 600);
        var area = new global::Avalonia.Rect(0, 0, 1200, 600);

        renderer.DrawOverlay(canvas, area, p, transform, StockAnalyzer.Core.Theme.ThemeColors.Light);
    }

    [Fact]
    public void RlTrainingDialog_ProgressTrackingProperties_UpdateCorrectly()
    {
        using var chart = Chart(new ImmediateData());
        using var vm = new RlTrainingDialogViewModel(new DelayedTraining(), new EmptyRegistry(), new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), chart, StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance);

        Assert.False(vm.ShowProgressBar);
        Assert.False(vm.IsTrainingInProgress);
        Assert.Equal(0, vm.TotalStepsTarget);
        Assert.Equal(0.0, vm.ProgressPercent);

        vm.TotalStepsTarget = 10000;
        vm.CompletedSteps = 2500;
        vm.ProgressPercent = 25.0;
        Assert.True(vm.ShowProgressBar);
        Assert.Equal("25.0%", vm.ProgressPercentDisplay);
        Assert.Contains("2,500 / 10,000 (25.0%)", vm.ProcessingStepsDisplay);
    }
    private sealed class DelayedData : IDataService
    {
        public TaskCompletionSource Started { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyList<CandleData>> Result { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol,TimeFrame frame,int count=100){Started.TrySetResult();return Result.Task;}
    }
    private sealed class FixedData(int bars) : IDataService
    {
        public int Loads { get; private set; }
        public Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol,TimeFrame frame,int count=100)
        {
            Loads++;
            var start = new DateTime(2020,1,1);
            return Task.FromResult<IReadOnlyList<CandleData>>(Enumerable.Range(0,bars).Select(i => new CandleData(start.AddDays(i), 100m+i,101m+i,99m+i,100m+i,1000)).ToArray());
        }
    }
    private sealed class ImmediateData : IDataService { public Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol,TimeFrame frame,int count=100)=>Task.FromResult<IReadOnlyList<CandleData>>(Array.Empty<CandleData>()); }
    private sealed class DelayedTraining : IRlResumableTrainingService
    {
        public RlTrainingJob? Job; public CancellationToken PauseRequest, CancelRequest; public RlCheckpointReview? LastResume;
        public IProgress<RlTrainingProgress>? Progress;
        public TaskCompletionSource Started { get; } = new(); public TaskCompletionSource ResumeStarted { get; } = new();
        public TaskCompletionSource<RlTrainingResult> Result { get; } = new(); public TaskCompletionSource<RlTrainingResult> ResumeResult { get; } = new();
        public Task<RlTrainingResult> RunAsync(RlTrainingJob job, IProgress<RlTrainingProgress>? progress = null, CancellationToken cancellationToken = default)
        { Job = job; Progress = progress; CancelRequest = cancellationToken; Started.SetResult(); return Result.Task; }
        public Task<RlTrainingResult> RunResumableAsync(RlTrainingJob job, CancellationToken pauseRequest, IProgress<RlTrainingProgress>? progress = null, CancellationToken ct = default)
        { PauseRequest = pauseRequest; return RunAsync(job, progress, ct); }
        public Task<RlTrainingResult> ResumeAsync(RlCheckpointReview review, CancellationToken pauseRequest, IProgress<RlTrainingProgress>? progress = null, CancellationToken ct = default)
        { LastResume = review; PauseRequest = pauseRequest; CancelRequest = ct; ResumeStarted.SetResult(); return ResumeResult.Task; }
    }
    private sealed class UnavailableQ : IQValueService {public Task<RlQSnapshot> ReplayAsync(RlReplayRequest request,CancellationToken cancellationToken=default)=>throw new InvalidOperationException("No generation supplied.");}
    private sealed class EmptyRegistry : IRlModelGenerationRegistry
    {
        public RlRegistryCapabilities Capabilities { get; init; } = new(false, false, false);
        public Exception? SnapshotError { get; init; }
        public bool DefaultSnapshot { get; init; }
        public TaskCompletionSource? SnapshotStarted { get; init; }
        public TaskCompletionSource? SnapshotRelease { get; init; }
        /// <summary>Per-call snapshot content and an optional gate that deliberately ignores cancellation (a superseded owner still completes).</summary>
        public Func<int, (ImmutableArray<RlGeneration> Generations, Task? Gate)>? SnapshotPlan { get; init; }
        private int _snapshotCalls;
        public RlRegistrySnapshot CaptureRlSnapshot(CancellationToken ct = default)
        {
            if (SnapshotPlan is { } plan)
            {
                var (planned, gate) = plan(Interlocked.Increment(ref _snapshotCalls)); gate?.Wait();
                return new(planned, null, null, false, false) { Capabilities = Capabilities };
            }
            SnapshotStarted?.TrySetResult(); SnapshotRelease?.Task.Wait(ct);
            ct.ThrowIfCancellationRequested(); if (SnapshotError is { } error) throw error;
            return new(DefaultSnapshot ? default : Generations.ToImmutableArray(), null, null, Recovery?.Target == RlFiles.Target && Recovery.Unavailable, Recovery?.Target == RlContinuousContract.Target && Recovery.Unavailable) { Capabilities = Capabilities };
        }
        public IReadOnlyList<RlCheckpointReview> Checkpoints { get; set; } = Array.Empty<RlCheckpointReview>();
        public RlCheckpointReview? LastAbandoned { get; private set; }
        public IReadOnlyList<RlCheckpointReview> ListRlCheckpoints() => Checkpoints;
        public Task AbandonRlResumeAsync(RlCheckpointReview reviewed, CancellationToken ct = default)
        { LastAbandoned = reviewed; Checkpoints = Array.Empty<RlCheckpointReview>(); return Task.CompletedTask; }
        public RlArtifactPolicy? RlArtifactPolicy { get; set; }
        public RlArtifactPreview? ArtifactPreview { get; set; }
        public RlArtifactPreview? LastCleanup { get; private set; }
        public Task SaveRlArtifactPolicyAsync(RlArtifactPolicy policy, CancellationToken ct=default) { policy.Validate(); RlArtifactPolicy=policy; return Task.CompletedTask; }
        public RlArtifactPreview? PreviewRlArtifacts()=>ArtifactPreview;
        public Task<RlArtifactCleanupResult> CleanupRlArtifactsAsync(RlArtifactPreview reviewed,CancellationToken ct=default)
        { LastCleanup=reviewed; return Task.FromResult(new RlArtifactCleanupResult(ImmutableArray.Create("owned"),ImmutableArray<string>.Empty,0)); }
        public RlPointerRecovery? Recovery { get; set; }
        public RlPointerRecovery? LastRecovered { get; private set; }
        public TaskCompletionSource<bool>? RecoveryCompletion { get; init; }
        public TaskCompletionSource RecoveryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RlPointerRecovery? PreviewRlRecovery(string target) => Recovery?.Target == target ? Recovery : null;
        public Task<bool> RecoverRlPointerAsync(RlPointerRecovery reviewed, CancellationToken cancellationToken = default)
        { LastRecovered = reviewed; RecoveryStarted.TrySetResult(); return RecoveryCompletion?.Task ?? Task.FromException<bool>(new InvalidOperationException("No recovery configured.")); }
        public IReadOnlyList<RlGeneration> Generations { get; set; } = Array.Empty<RlGeneration>();
        /// <summary>Models whose work folder is gone, and the removals asked of the registry (the default answer removes the model).</summary>
        public IReadOnlySet<string> ModelsWithoutWorkFolder { get; init; } = new HashSet<string>();
        public List<string> RemoveCalls { get; } = new();
        public Func<string, RlGenerationRemoval> RemoveResult { get; set; } = _ => new(true, ImmutableArray<string>.Empty);
        public IReadOnlySet<string> RlModelIdsWithoutWorkFolder() => ModelsWithoutWorkFolder;
        public Task<RlGenerationRemoval> RemoveRlGenerationAsync(string modelId, CancellationToken ct = default)
        {
            RemoveCalls.Add(modelId);
            var result = RemoveResult(modelId);
            if (result.Removed) Generations = Generations.Where(g => g.ModelId != modelId).ToArray();
            return Task.FromResult(result);
        }
        public string? RlActiveId => null;
        /// <summary>When set, <see cref="ListRl"/> throws it (a registry that cannot be read).</summary>
        public Exception? ListError { get; init; }
        public IReadOnlyList<RlGeneration> ListRl() => ListError is { } error ? throw error : Generations;
        public ModelGenerationLease? AcquireRl(string id) => null;
        public ModelGenerationLease? AcquireRlActive() => null;
        public RlGeneration? GetRlGeneration(string id) => null;
        public Task<bool> ActivateRlAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<RlGeneration> RegisterRlAsync(string directory, CancellationToken cancellationToken = default) => throw new InvalidOperationException("UI fixture cannot register generations.");
    }
}
