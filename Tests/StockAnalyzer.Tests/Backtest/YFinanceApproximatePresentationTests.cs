using System;
using System.Collections.Immutable;
using System.Collections.Generic;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Core.Models.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

public class YFinanceApproximatePresentationTests
{
    [Fact]
    public async Task ConfigurationMode_RoundTripsAsSeparateSelection()
    {
        var manager = new FakeBacktestConfigurationManager();
        BacktestWindowViewModel vm = BacktestTestFactory.CreateViewModel(configurationManager: manager);
        Assert.Contains(ExecutionModel.YFinanceApproximate, vm.AvailableExecutionModels);

        vm.ExecutionModel = ExecutionModel.YFinanceApproximate;
        await vm.SaveConfigurationCommand.ExecuteAsync(null);
        vm.ExecutionModel = ExecutionModel.Legacy;
        await vm.RestoreConfigurationCommand.ExecuteAsync(null);

        Assert.Equal(ExecutionModel.YFinanceApproximate, vm.ExecutionModel);
    }

    [Fact]
    public void ResultsLabel_IdentifiesApproximationAndRejectsMismatchedReport()
    {
        const string disclosure = "Approximate OHLC; yfinance source user-declared and unverified; not fill evidence.";
        var vm = new BacktestResultsViewModel(
            new FakeLocalizationService(new Dictionary<string, string>
            {
                ["Backtest_Results_ExecutionMode_YFinanceApproximate"] = disclosure,
            }),
            new FakeBacktestReportExporter(),
            new FakeDialogService());
        BacktestResult result = BacktestTestFactory.CreateResult(executionModel: ExecutionModel.YFinanceApproximate);

        bool mismatched = vm.TryUpdate(result, BacktestTestFactory.CreateStubReport(), 0,
            ImmutableArray<BacktestConditionEntry>.Empty, false, out Exception? failure);

        Assert.False(mismatched);
        Assert.IsType<ArgumentException>(failure);
        Assert.Null(vm.Presentation);

        vm.Update(result, BacktestTestFactory.CreateStubReport(
            executionModel: ExecutionModel.YFinanceApproximate), 0);

        Assert.Equal(ExecutionModel.YFinanceApproximate, vm.Presentation!.ExecutionMode);
        Assert.Equal(disclosure, vm.Presentation.ExecutionModeDescription);
    }
}
