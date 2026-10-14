#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>PLAN D1: the RL window header shows only MEASURED values of the selected generation's saved evaluation.</summary>
public sealed partial class RlUiTests
{
    private sealed class MeasuredData(IRlModelGenerationRegistry registry) : RlDialogDataService(registry, new UnavailableQ())
    {
        public Func<string, CancellationToken, Task<RlDialogBlocks>> Load { get; set; } = (_, _) => Task.FromResult(new RlDialogBlocks(DefaultPlaybackBlocks, null, null));
        public override Task<RlDialogBlocks> LoadBlocksAsync(string modelId, CancellationToken ct) => Load(modelId, ct);
    }

    /// <summary>Echoes each key; the scope key carries exactly the four placeholders the real text has.</summary>
    private sealed class MeasuredLocalization : ILocalizationService
    {
        public string GetString(string key) => key == "Rl.RiskMetrics.Scope" ? "{0}|{1}|{2}|{3}" : key;
    }

    private sealed class ProgressTraining : IRlResumableTrainingService
    {
        public TaskCompletionSource Reported { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<RlTrainingResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<RlTrainingResult> RunAsync(RlTrainingJob job, IProgress<RlTrainingProgress>? progress = null, CancellationToken cancellationToken = default)
        { progress?.Report(new(Guid.NewGuid(), 1, "Training", "step", 1)); Reported.TrySetResult(); return Result.Task; }
        public Task<RlTrainingResult> RunResumableAsync(RlTrainingJob job, CancellationToken pauseRequest, IProgress<RlTrainingProgress>? progress = null, CancellationToken ct = default)
            => RunAsync(job, progress, ct);
        public Task<RlTrainingResult> ResumeAsync(RlCheckpointReview review, CancellationToken pauseRequest, IProgress<RlTrainingProgress>? progress = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static RlDialogBlocks MeasuredBlocks(RlMeasuredEvaluation? measured) => new(DefaultPlaybackBlocks, null, null) { Measured = measured };

    private RlTrainingDialogViewModel MeasuredViewModel(ChartViewModel chart, MeasuredData data, EmptyRegistry registry)
        => new(new DelayedTraining(), registry, new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), chart, new MeasuredLocalization(), null, data);

    [Fact]
    public void Header_ShowsNothingBeforeAnythingIsMeasured()
    {
        using var chart = Chart(new ImmediateData());
        using var vm = new RlTrainingDialogViewModel(new DelayedTraining(), new EmptyRegistry(), new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), chart, new MeasuredLocalization());
        Assert.False(vm.HasRiskMetrics); Assert.Null(vm.Measured); Assert.Equal("", vm.RiskMetricsScopeDisplay);
    }

    [Fact]
    public async Task Header_TrainingProgressNeverProducesNumbers()
    {
        using var chart = Chart(new ImmediateData()); var training = new ProgressTraining();
        using var vm = new RlTrainingDialogViewModel(training, new EmptyRegistry(), new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), chart, new MeasuredLocalization());
        await vm.InitializeAsync(); vm.Symbol = "SPY"; vm.StartDate = "2000-01-01"; vm.EndDateExclusive = "2026-01-01"; vm.SmokeOnly = true;
        var run = vm.TrainCommand.ExecuteAsync(null); await training.Reported.Task;
        await PlaybackUntil(() => vm.CompletedSteps == 1);
        Assert.True(vm.TotalStepsTarget > 0); Assert.Equal("Training", vm.Status);
        Assert.False(vm.HasRiskMetrics); Assert.Null(vm.Measured);
        vm.CancelCommand.Execute(null);
        training.Result.SetResult(new(Guid.NewGuid(), "dir", "model", "eval", null, true, "revision")); await run;
        Assert.False(vm.HasRiskMetrics);
    }

    [Fact]
    public async Task Header_ShowsTheMeasuredValuesOfTheSelectedGenerationOnlyAfterTheReportIsRead()
    {
        using var chart = Chart(new ImmediateData());
        var gen = PlaybackGeneration("a"); var registry = new EmptyRegistry { Generations = new[] { gen } };
        var gate = new TaskCompletionSource<RlDialogBlocks>(TaskCreationOptions.RunContinuationsAsynchronously);
        var data = new MeasuredData(registry) { Load = (_, _) => gate.Task };
        using var vm = MeasuredViewModel(chart, data, registry);
        await vm.InitializeAsync();

        vm.SelectedGeneration = gen;
        Assert.False(vm.HasRiskMetrics); // the generation's stored returns are NOT turned into pill numbers
        gate.SetResult(MeasuredBlocks(new RlMeasuredEvaluation(RlReplayDefaults.OosPartition, 3, 0.125m, 0.042m, -0.0345m, 2)));
        await PlaybackUntil(() => vm.HasRiskMetrics);

        Assert.Equal("+12.50%", vm.CumulativeReturnDisplay); Assert.Equal("4.20%", vm.MaxDrawdownDisplay);
        Assert.Equal("Rl.RiskMetrics.Scope.Final|3|2|-3.45%", vm.RiskMetricsScopeDisplay);

        vm.SelectedGeneration = null;
        Assert.False(vm.HasRiskMetrics); Assert.Null(vm.Measured);
    }

    [Fact]
    public async Task Header_NamesTheValidationPartitionAndStaysHiddenWithoutAMeasuredReport()
    {
        using var chart = Chart(new ImmediateData());
        var gen = PlaybackGeneration("a"); var registry = new EmptyRegistry { Generations = new[] { gen } };
        var data = new MeasuredData(registry) { Load = (_, _) => Task.FromResult(MeasuredBlocks(new RlMeasuredEvaluation("validation", 2, -0.0123m, 0m, -0.0123m, 0))) };
        using var vm = MeasuredViewModel(chart, data, registry);
        await vm.InitializeAsync();
        vm.SelectedGeneration = gen; await PlaybackUntil(() => vm.HasRiskMetrics);
        Assert.Equal("-1.23%", vm.CumulativeReturnDisplay); Assert.Equal("0.00%", vm.MaxDrawdownDisplay);
        Assert.Equal("Rl.RiskMetrics.Scope.Validation|2|0|-1.23%", vm.RiskMetricsScopeDisplay);

        data.Load = (_, _) => Task.FromResult(MeasuredBlocks(null));
        vm.SelectedGeneration = null; vm.SelectedGeneration = gen; await PlaybackUntil(() => vm.BlocksState == RlDialogLoadState.Loaded);
        Assert.False(vm.HasRiskMetrics);
    }

    [Theory]
    [InlineData(1, 0.0831, 0.0831, 1, "Rl.RiskMetrics.Scope.Final|1|1|+8.31%")]       // one episode: the mean is the only episode
    [InlineData(4, 0.05, 0.01, 4, "Rl.RiskMetrics.Scope.Final|4|4|+1.00%")]           // all positive: the worst episode is still positive
    [InlineData(2, -0.05, -0.09, 0, "Rl.RiskMetrics.Scope.Final|2|0|-9.00%")]         // none positive
    [InlineData(3, 0.0, 0.0, 0, "Rl.RiskMetrics.Scope.Final|3|0|0.00%")]              // zero is not positive
    public async Task Header_ScopeTextCarriesThePositiveCountAndTheWorstEpisodeReturn(int episodes, double mean, double min, int positive, string expected)
    {
        using var chart = Chart(new ImmediateData());
        var gen = PlaybackGeneration("a"); var registry = new EmptyRegistry { Generations = new[] { gen } };
        var measured = new RlMeasuredEvaluation(RlReplayDefaults.OosPartition, episodes, (decimal)mean, 0.01m, (decimal)min, positive);
        var data = new MeasuredData(registry) { Load = (_, _) => Task.FromResult(MeasuredBlocks(measured)) };
        using var vm = MeasuredViewModel(chart, data, registry);
        await vm.InitializeAsync();
        vm.SelectedGeneration = gen; await PlaybackUntil(() => vm.HasRiskMetrics);
        Assert.Equal(expected, vm.RiskMetricsScopeDisplay);
    }
}
