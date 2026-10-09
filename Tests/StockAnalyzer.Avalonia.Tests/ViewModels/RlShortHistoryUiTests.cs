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
        foreach (string key in new[] { "Rl.ShortHistoryOption", "Rl.ShortHistoryHelp", "Rl.ShortHistoryMode", "Rl.ShortHistoryCounts", "Rl.ShortHistoryDisclosure" })
        {
            string english = en.RootElement.GetProperty(key).GetString()!, japanese = ja.RootElement.GetProperty(key).GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(english)); Assert.False(string.IsNullOrWhiteSpace(japanese));
            Assert.Equal(english.Contains("{0}"), japanese.Contains("{0}")); Assert.Equal(english.Contains("{2}"), japanese.Contains("{2}"));
        }
    }
}
