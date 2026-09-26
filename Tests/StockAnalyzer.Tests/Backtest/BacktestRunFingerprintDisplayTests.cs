using System.Collections.Generic;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>
/// Owner decision G5 (adoption): the Results tab shows the run fingerprint - or the reason there is none - as one localized line, and the window ViewModel
/// freezes the run's identity BEFORE the run and hands it to the report generator through the report options.
/// </summary>
public class BacktestRunFingerprintDisplayTests
{
    private const string AvailableKey = "Backtest_Results_RunFingerprint";
    private const string UnavailableKey = "Backtest_Results_RunFingerprintUnavailable";

    private static BacktestResultsViewModel CreateResultsViewModel()
        => new(
            new FakeLocalizationService(new Dictionary<string, string> { [AvailableKey] = "FP[{0}]", [UnavailableKey] = "NOFP[{0}]" }),
            new FakeBacktestReportExporter(),
            new FakeDialogService());

    [Fact]
    public void AnAvailableFingerprint_IsShownAsTheLocalizedLineWithTheFullDigest()
    {
        BacktestResultsViewModel vm = CreateResultsViewModel();
        string digest = new string('A', 64);

        vm.Update(BacktestTestFactory.CreateResult(), BacktestTestFactory.CreateStubReport(new BacktestReportRunFingerprint { IsAvailable = true, Sha256 = digest, SchemaVersion = 1, ExecutionSemanticsVersion = 2 }), 0);

        Assert.Equal($"FP[{digest}]", vm.RunFingerprintText);
    }

    [Fact]
    public void AnUnavailableFingerprint_IsShownWithItsReason_NotAsAnEmptyHash()
    {
        BacktestResultsViewModel vm = CreateResultsViewModel();

        vm.Update(BacktestTestFactory.CreateResult(), BacktestTestFactory.CreateStubReport(new BacktestReportRunFingerprint { IsAvailable = false, UnavailableReason = "custom strategy" }), 0);

        Assert.Equal("NOFP[custom strategy]", vm.RunFingerprintText);
    }

    [Fact]
    public void AReportWithoutAFingerprint_ShowsNoLine_AndAnEarlierLineDoesNotLinger()
    {
        BacktestResultsViewModel vm = CreateResultsViewModel();
        vm.Update(BacktestTestFactory.CreateResult(), BacktestTestFactory.CreateStubReport(new BacktestReportRunFingerprint { IsAvailable = true, Sha256 = new string('B', 64) }), 0);
        Assert.NotNull(vm.RunFingerprintText);

        vm.Update(BacktestTestFactory.CreateResult(), BacktestTestFactory.CreateStubReport(), 0);

        Assert.Null(vm.RunFingerprintText);
    }

    [Fact]
    public async Task TheWindowViewModel_FreezesTheRunIdentityBeforeTheRun_AndPassesItToTheReportGenerator()
    {
        var engine = new ScriptableBacktestEngine();
        engine.EnqueueResult(() => BacktestTestFactory.CreateResult());
        var generator = new StubBacktestReportGenerator();
        var vm = BacktestTestFactory.CreateViewModel(engine: engine, reportGenerator: generator);
        vm.Symbol = "TEST";

        await vm.RunBacktestCommand.ExecuteAsync(null);

        Assert.NotNull(generator.LastOptions);
        Assert.NotNull(generator.LastOptions!.RunFingerprintBuilder);
    }

    [Fact]
    public async Task WithTheRealEngineAndGenerator_TheFrozenIdentityIsSealedAgainstTheRunsOwnResult_AndReachesTheResultsTab()
    {
        // No stub between the window and the report: a mismatch between what was frozen and what the engine received would throw here.
        var localization = new FakeLocalizationService(new Dictionary<string, string> { [AvailableKey] = "FP[{0}]", [UnavailableKey] = "NOFP[{0}]" });
        var vm = BacktestTestFactory.CreateViewModel(
            engine: new StockAnalyzer.Core.Services.Backtest.Engine.BacktestEngine(new StockAnalyzer.Core.Models.Indicators.IndicatorFactory()),
            reportGenerator: new BacktestReportGenerator(),
            localizationService: localization);
        vm.Symbol = "TEST";

        await vm.RunBacktestCommand.ExecuteAsync(null);

        Assert.Equal(BacktestRunState.Completed, vm.State);
        string? text = vm.Results.RunFingerprintText;
        Assert.NotNull(text);
        Assert.Matches(@"^FP\[[0-9A-F]{64}\]$", text);
    }
}
