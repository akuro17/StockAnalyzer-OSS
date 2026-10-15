#nullable enable
using System;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>The long silent stretches of a run (recording and verifying the execution environment) show a phase text of their own, localized by the dialog.</summary>
public sealed partial class RlUiTests
{
    private static async Task WaitForPhase(RlTrainingDialogViewModel vm, string expected)
    {
        for (int attempt = 0; attempt < 200 && vm.ProgressPhaseDisplay != expected; attempt++) await Task.Delay(25);
    }

    [Fact] public async Task Dialog_ShowsALocalizedPhaseForTheKnownRunPhasesAndTheWorkerMessageOtherwise()
    {
        using var chart = Chart(new ImmediateData()); var training = new DelayedTraining();
        using var vm = new RlTrainingDialogViewModel(training, new EmptyRegistry(), new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), chart,
            StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance);
        await vm.InitializeAsync(); vm.Symbol = "SPY"; vm.StartDate = "2000-01-01"; vm.EndDateExclusive = "2026-01-01"; vm.SmokeOnly = true;
        var run = vm.TrainCommand.ExecuteAsync(null); await training.Started.Task;

        training.Progress!.Report(new(Guid.NewGuid(), 1, RlTrainingPhases.Environment, "Recording the execution environment before the run", 0));
        await WaitForPhase(vm, "Rl.State.Environment");
        Assert.Equal("Rl.State.Environment", vm.ProgressPhaseDisplay);

        training.Progress.Report(new(Guid.NewGuid(), 2, RlTrainingPhases.Source, "Capturing existing local Data/Daily bytes; historical evidence unverified", 0));
        await WaitForPhase(vm, "Rl.State.Source");
        Assert.Equal("Rl.State.Source", vm.ProgressPhaseDisplay);

        training.Progress.Report(new(Guid.NewGuid(), 3, "Training", "worker message", 10));
        await WaitForPhase(vm, "worker message");
        Assert.Equal("worker message", vm.ProgressPhaseDisplay);

        vm.CancelCommand.Execute(null);
        training.Result.SetResult(new(Guid.NewGuid(), "dir", "model", "eval", null, true, "revision")); await run;
    }
}
