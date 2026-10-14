using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;
using StockAnalyzer.Avalonia.Tests.TestHelpers;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public sealed partial class RlUiTests
{
    private static JsonDocument Locale(string language)
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "StockAnalyzer.sln"))) repo = repo.Parent;
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(repo!.FullName, "StockAnalyzer.Avalonia", "Resources", "Locales", language + ".json")));
    }

    [Fact] public async Task ShortHistoryIsOffByDefaultAndOnlyTheDiscreteTargetCarriesItsId()
    {
        using var chart = Chart(new ImmediateData()); var training = new DelayedTraining();
        using var vm = new RlTrainingDialogViewModel(training, new EmptyRegistry(), new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), chart, StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance);
        await vm.InitializeAsync();
        Assert.False(vm.UseShortHistory); Assert.True(vm.ShowShortHistoryOption);
        vm.Symbol = "SPY"; vm.StartDate = "2000-01-01"; vm.EndDateExclusive = "2026-01-01";
        var untouched = vm.TrainCommand.ExecuteAsync(null); await training.Started.Task;
        Assert.Null(training.Job!.ShortHistorySpecificationId);
        vm.CancelCommand.Execute(null); training.Result.SetResult(new(Guid.NewGuid(), "dir", "model", "eval", null, true, "revision")); await untouched;
    }

    [Fact] public async Task ShortHistoryChoiceIsFrozenIntoTheJobBeforeAnyAwait()
    {
        using var chart = Chart(new ImmediateData()); var training = new DelayedTraining();
        using var vm = new RlTrainingDialogViewModel(training, new EmptyRegistry(), new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), chart, StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance);
        await vm.InitializeAsync();
        vm.Symbol = "SPY"; vm.StartDate = "2000-01-01"; vm.EndDateExclusive = "2026-01-01"; vm.UseShortHistory = true;
        var run = vm.TrainCommand.ExecuteAsync(null); await training.Started.Task;
        vm.UseShortHistory = false; vm.CancelCommand.Execute(null);
        Assert.Equal(RlShortHistoryEvaluationContract.SpecificationId, training.Job!.ShortHistorySpecificationId);
        Assert.Null(training.Job.EvaluationSpecificationId); Assert.Null(training.Job.FrozenDirectionModelId);
        training.Result.SetResult(new(Guid.NewGuid(), "dir", "model", "eval", null, true, "revision")); await run;
    }

    [Fact] public async Task ShortHistoryIsHiddenAndIgnoredWhenTheContinuousTargetIsSelected()
    {
        using var chart = Chart(new ImmediateData()); var training = new DelayedTraining();
        var direction = new RlGeneration(new string('a', 64), "SPY", "revision", RlContract.ContractHash, "evaluation", 0m, 0m, new(), DateTimeOffset.UtcNow);
        using var vm = new RlTrainingDialogViewModel(training, new EmptyRegistry { Generations = new[] { direction } }, new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), chart, StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance);
        await vm.InitializeAsync();
        vm.UseShortHistory = true; vm.UseContinuousPolicy = true;
        Assert.False(vm.ShowShortHistoryOption);
        vm.Symbol = "SPY"; vm.StartDate = "2000-01-01"; vm.EndDateExclusive = "2026-01-01"; vm.SelectedDirectionGeneration = vm.DirectionGenerations.Single();
        var run = vm.TrainCommand.ExecuteAsync(null); await training.Started.Task;
        vm.CancelCommand.Execute(null);
        Assert.Null(training.Job!.ShortHistorySpecificationId); Assert.Equal(new string('a', 64), training.Job.FrozenDirectionModelId);
        training.Result.SetResult(new(Guid.NewGuid(), "dir", "model", "eval", null, true, "revision")); await run;
    }

    // The Japanese count text is a deliberate literal (the wording a Japanese user reads), the one exception to English-only code.
    [Theory] [InlineData("en", "Segments: training 3; validation 1; final evaluation 1")] [InlineData("ja", "区間数：学習 3；検証 1；最終評価 1")]
    public void ShortHistoryReadoutStatesBlockCountsAndTheDescriptiveDisclosure(string language, string counts)
    {
        using var json = Locale(language);
        var localization = new FixtureLocalization(json.RootElement);
        var one = new RlReturnDistribution(1, -0.01m, -0.01m, -0.01m, -0.01m, 0m);
        var report = new RlShortHistoryEvaluationReport(RlShortHistoryEvaluationContract.SpecificationId, RlShortHistoryEvaluationContract.SchemaVersion,
            RlShortHistoryEvaluationContract.ContractHash, "revision", new(3, 1, 1), one, one, RlShortHistoryEvaluation.Estimate(1, 1, 0, false),
            RlSourceContract.Unverified, RlConstraintContract.InvestmentEfficacy, RlShortHistoryEvaluationContract.Statement);
        string text = RlPresentation.FormatShortHistory(report, localization);
        Assert.Contains(counts, text);
        Assert.Contains(localization.GetString("Rl.ShortHistoryMode"), text); Assert.Contains(localization.GetString("Rl.ShortHistoryDisclosure"), text);
        Assert.Contains(localization.GetString("Rl.OosActor"), text);
        Assert.DoesNotContain(localization.GetString("Rl.OosActor"), RlPresentation.FormatShortHistory(report with { Oos = null }, localization));
    }

    [Fact] public void ShortHistoryTextExistsInBothLanguagesWithTheSamePlaceholders()
    {
        using var en = Locale("en"); using var ja = Locale("ja");
        foreach (string key in new[] { "Rl.ShortHistoryOption", "Rl.ShortHistoryMode", "Rl.ShortHistoryCounts", "Rl.ShortHistoryDisclosure" })
        {
            string english = en.RootElement.GetProperty(key).GetString()!, japanese = ja.RootElement.GetProperty(key).GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(english)); Assert.False(string.IsNullOrWhiteSpace(japanese));
            Assert.Equal(english.Contains("{0}"), japanese.Contains("{0}")); Assert.Equal(english.Contains("{2}"), japanese.Contains("{2}"));
        }
    }

    [Fact]
    public async Task GenerationComparisonAndCheckpointTooltips_UpdateCorrectly()
    {
        using var chart = Chart(new ImmediateData()); var training = new DelayedTraining();
        var gen1 = new RlGeneration("model_1", "SPY", "rev1", RlContract.ContractHash, "eval", 0.15m, 0.12m, new(), DateTimeOffset.UtcNow);
        var gen2 = new RlGeneration("model_2", "SPY", "rev1", RlContract.ContractHash, "eval", 0.30m, 0.05m, new(), DateTimeOffset.UtcNow);
        using var vm = new RlTrainingDialogViewModel(training, new EmptyRegistry { Generations = new[] { gen1, gen2 } }, new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), chart, StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance);
        await vm.InitializeAsync();

        // Task 1: Checkpoint tooltips & badges
        Assert.False(vm.HasSelectedCheckpoint);
        Assert.NotEmpty(vm.ResumeTooltip);
        Assert.NotEmpty(vm.AbandonTooltip);
        Assert.NotEmpty(vm.CheckpointStatusBadgeDisplay);

        // Task 2: Generation comparison scatter points
        Assert.True(vm.HasGenerationScatterPoints);
        Assert.Equal(2, vm.GenerationScatterPoints.Count);
        Assert.NotEmpty(vm.GenerationComparisonSummary);

        var p1 = vm.GenerationScatterPoints.Single(p => p.ModelId == "model_1");
        var p2 = vm.GenerationScatterPoints.Single(p => p.ModelId == "model_2");
        Assert.Equal("#26A69A", p1.QualityBadgeColor);
        Assert.Equal("#EF5350", p2.QualityBadgeColor);
    }

    [Fact]
    public async Task GenerationComparisonSummary_RanksByValidationAndShowsTheFinalEvaluationOnlyAsReference()
    {
        using var chart = Chart(new ImmediateData());
        // model_1 has the better final evaluation, model_2 the better validation: the ranking must follow validation.
        var gen1 = new RlGeneration("model_1", "SPY", "rev1", RlContract.ContractHash, "eval", 0.15m, 0.12m, new(), DateTimeOffset.UtcNow);
        var gen2 = new RlGeneration("model_2", "SPY", "rev1", RlContract.ContractHash, "eval", 0.30m, 0.05m, new(), DateTimeOffset.UtcNow);
        using var vm = new RlTrainingDialogViewModel(new DelayedTraining(), new EmptyRegistry { Generations = new[] { gen1, gen2 } }, new UnavailableQ(),
            new FakeBacktestDefaultSettingsManager(), chart, new ArgEchoLocalization());
        await vm.InitializeAsync();
        vm.SelectedGeneration = gen1;
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        string P(decimal value) => value.ToString("P" + RlContract.DisplayDecimals, culture);
        Assert.Equal($"Rl.Ui.GenerationComparison.Summary|model_2|{P(0.30m)}|{P(0.05m)}|{P(0.03m)}", vm.GenerationComparisonSummary);

        // The final evaluation never changes the ranking.
        var gen3 = new RlGeneration("model_3", "SPY", "rev1", RlContract.ContractHash, "eval", 0.20m, 0.90m, new(), DateTimeOffset.UtcNow);
        using var other = new RlTrainingDialogViewModel(new DelayedTraining(), new EmptyRegistry { Generations = new[] { gen1, gen2, gen3 } }, new UnavailableQ(),
            new FakeBacktestDefaultSettingsManager(), chart, new ArgEchoLocalization());
        await other.InitializeAsync();
        Assert.StartsWith("Rl.Ui.GenerationComparison.Summary|model_2|", other.GenerationComparisonSummary);
    }

    [Fact]
    public async Task CheckpointStatusBadgeAndTooltips_UpdateAcrossAllStates()
    {
        using var chart = Chart(new ImmediateData());
        var training = new DelayedTraining();
        var verifiedReview = new RlCheckpointReview(Guid.NewGuid(), "dir1", "cp1", 1, 100, "train", "h1", true, null);
        var unverifiedReview = new RlCheckpointReview(Guid.NewGuid(), "dir2", "cp2", 1, 100, "train", "h2", false, null);

        var registry = new EmptyRegistry
        {
            Capabilities = new(Storage: false, Recovery: false, Checkpoints: true),
            Checkpoints = new[] { verifiedReview, unverifiedReview }
        };

        using var vm = new RlTrainingDialogViewModel(
            training,
            registry,
            new UnavailableQ(),
            new FakeBacktestDefaultSettingsManager(),
            chart,
            StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance);

        await vm.InitializeAsync();

        // 1. Unloaded state
        Assert.False(vm.HasCheckpoints);
        Assert.False(vm.HasSelectedCheckpoint);
        Assert.Equal("Rl.Ui.Badge.NoCheckpointsLoaded", vm.CheckpointStatusBadgeDisplay);
        Assert.False(vm.CanConfirmResume);
        Assert.False(vm.CanConfirmAbandon);
        Assert.Equal("Rl.Ui.Tooltip.ResumeNeedSelect", vm.ResumeTooltip);
        Assert.Equal("Rl.Ui.Tooltip.ResumeConfirmNeedSelect", vm.ResumeConfirmTooltip);
        Assert.Equal("Rl.Ui.Tooltip.InspectCheckpointsHelp", vm.InspectCheckpointsTooltip);

        // 2. Loaded, unselected state
        await vm.InspectCheckpointsCommand.ExecuteAsync(null);
        Assert.True(vm.HasCheckpoints);
        Assert.False(vm.HasSelectedCheckpoint);
        Assert.Equal("Rl.Ui.Badge.NoCheckpointSelected", vm.CheckpointStatusBadgeDisplay);

        // 3. Selected unverified checkpoint
        var unverifiedRow = vm.CheckpointRows.Single(r => r.Review.CheckpointId == "cp2");
        vm.SelectedCheckpoint = unverifiedRow;
        Assert.True(vm.HasSelectedCheckpoint);
        Assert.Equal("Rl.Ui.Badge.ProtectedUnverified", vm.CheckpointStatusBadgeDisplay);
        Assert.False(vm.CanConfirmResume);
        Assert.True(vm.CanConfirmAbandon);
        Assert.Equal("Rl.Ui.Tooltip.ResumeUnverified", vm.ResumeTooltip);

        // 4. Selected verified checkpoint
        var verifiedRow = vm.CheckpointRows.Single(r => r.Review.CheckpointId == "cp1");
        vm.SelectedCheckpoint = verifiedRow;
        Assert.True(vm.HasSelectedCheckpoint);
        Assert.Equal("Rl.Ui.Badge.ProtectedResumable", vm.CheckpointStatusBadgeDisplay);
        Assert.True(vm.CanConfirmResume);
        Assert.True(vm.CanConfirmAbandon);
        Assert.Equal("Rl.Ui.Tooltip.ResumeNeedConfirm", vm.ResumeTooltip);
        Assert.Equal("Rl.Ui.Tooltip.ResumeConfirmHelp", vm.ResumeConfirmTooltip);

        // 5. Resume confirmed
        vm.ResumeConfirmed = true;
        Assert.Equal("Rl.Ui.Badge.ResumeReady", vm.CheckpointStatusBadgeDisplay);
        Assert.True(vm.CanResumeCheckpoint);
        Assert.Equal("Rl.Ui.Tooltip.ResumeReady", vm.ResumeTooltip);

        // 6. Abandon confirmed
        vm.ResumeConfirmed = false;
        vm.AbandonConfirmed = true;
        Assert.Equal("Rl.Ui.Badge.AbandonReady", vm.CheckpointStatusBadgeDisplay);
        Assert.True(vm.CanAbandonCheckpoint);
        Assert.Equal("Rl.Ui.Tooltip.AbandonReady", vm.AbandonTooltip);
    }
}
