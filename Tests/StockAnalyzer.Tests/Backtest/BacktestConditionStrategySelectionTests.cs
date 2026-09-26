using System.Collections.Generic;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>
/// Task 5 proof (plan section 4.3/Task 5 of Y:\Temp\sa_implementation_plan_BacktestComparisonSignals.md):
/// <see cref="BacktestWindowViewModel.RunBacktestCommand"/> selects
/// <see cref="ConditionBasedBacktestStrategy"/> once at least one condition entry exists on
/// <see cref="BacktestIndicatorSelectionViewModel.ConditionEntries"/>, falls back to the untouched
/// <see cref="NoOpBacktestStrategy"/> for a zero-condition configuration (the "existing behavior
/// preservation" regression guard), and loads any foreign Frame a condition references into
/// <see cref="BacktestInput.AdditionalTimeframeBars"/> before the run starts.
/// </summary>
public class BacktestConditionStrategySelectionTests
{
    private static CandleData Bar(int dayOffset, decimal close) =>
        new(new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc).AddDays(dayOffset), close, close, close, close, 1000);

    [Fact]
    public async System.Threading.Tasks.Task ZeroConditions_SelectsNoOpStrategy_AndNoAdditionalTimeframeBars()
    {
        var engine = new ScriptableBacktestEngine();
        engine.EnqueueResult(() => BacktestTestFactory.CreateResult());
        var vm = BacktestTestFactory.CreateViewModel(engine: engine, dataService: new FakeDataService(new[] { Bar(0, 100m) }));
        vm.Symbol = "TEST";

        await vm.RunBacktestCommand.ExecuteAsync(null);

        Assert.Equal(BacktestRunState.Completed, vm.State);
        Assert.IsType<NoOpBacktestStrategy>(engine.LastStrategy);
        Assert.Null(engine.LastInput!.AdditionalTimeframeBars);
    }

    [Fact]
    public async System.Threading.Tasks.Task SameFrameConditionOnly_SelectsConditionBasedStrategy_AndNoAdditionalTimeframeBars()
    {
        var engine = new ScriptableBacktestEngine();
        engine.EnqueueResult(() => BacktestTestFactory.CreateResult());
        var vm = BacktestTestFactory.CreateViewModel(engine: engine, dataService: new FakeDataService(new[] { Bar(0, 100m) }));
        vm.Symbol = "TEST";
        vm.IndicatorSelection.ConditionEntries.Add(new BacktestConditionEntry
        {
            Left = new BacktestConditionSide { IndicatorType = IndicatorType.SMA, Frame = null },
            RightNumericValue = 50m,
        });

        await vm.RunBacktestCommand.ExecuteAsync(null);

        Assert.Equal(BacktestRunState.Completed, vm.State);
        Assert.IsType<ConditionBasedBacktestStrategy>(engine.LastStrategy);
        Assert.Null(engine.LastInput!.AdditionalTimeframeBars);
    }

    [Fact]
    public async System.Threading.Tasks.Task ForeignFrameCondition_LoadsThatFramesBarsIntoAdditionalTimeframeBars()
    {
        var engine = new ScriptableBacktestEngine();
        engine.EnqueueResult(() => BacktestTestFactory.CreateResult());
        CandleData[] dailyBars = { Bar(0, 100m) };
        CandleData[] weeklyBars = { Bar(0, 200m), Bar(7, 210m) };
        var dataService = new FakeDataService(dailyBars, new Dictionary<TimeFrame, IReadOnlyList<CandleData>>
        {
            [TimeFrame.W1] = weeklyBars,
        });
        var vm = BacktestTestFactory.CreateViewModel(engine: engine, dataService: dataService);
        vm.Symbol = "TEST";
        vm.Frame = TimeFrame.D1;
        vm.IndicatorSelection.ConditionEntries.Add(new BacktestConditionEntry
        {
            Left = new BacktestConditionSide { IndicatorType = IndicatorType.SMA, Frame = TimeFrame.W1 },
            RightNumericValue = 150m,
        });

        await vm.RunBacktestCommand.ExecuteAsync(null);

        Assert.Equal(BacktestRunState.Completed, vm.State);
        Assert.IsType<ConditionBasedBacktestStrategy>(engine.LastStrategy);
        Assert.NotNull(engine.LastInput!.AdditionalTimeframeBars);
        Assert.True(engine.LastInput!.AdditionalTimeframeBars!.ContainsKey(TimeFrame.W1));
        Assert.Equal(weeklyBars.Length, engine.LastInput!.AdditionalTimeframeBars![TimeFrame.W1].Length);
    }
}
