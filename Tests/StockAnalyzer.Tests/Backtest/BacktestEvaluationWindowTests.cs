using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>
/// sa_improvement_plan_BacktestP3Hardening.md Task 3: proves
/// <see cref="BacktestWindowViewModel"/>'s evaluation-window boundary computation (now binary search
/// instead of a linear scan) still finds the exact same indices as before - inclusive
/// EvaluationEndUtc boundary, inclusive-from EvaluationStartUtc boundary, and both open-ended cases.
/// </summary>
public class BacktestEvaluationWindowTests
{
    private static List<CandleData> TenDailyBars()
    {
        var bars = new List<CandleData>();
        for (int i = 1; i <= 10; i++)
        {
            var timestamp = new DateTime(2024, 1, i, 0, 0, 0, DateTimeKind.Utc);
            bars.Add(new CandleData(timestamp, 100m, 101m, 99m, 100m, 1000));
        }
        return bars;
    }

    private static async Task<BacktestInput> RunAndCaptureInputAsync(DateTime evaluationStartUtc, DateTime evaluationEndUtc)
    {
        var engine = new ScriptableBacktestEngine();
        engine.EnqueueResult(() => BacktestTestFactory.CreateResult());
        var dataService = new FakeDataService(TenDailyBars());
        BacktestWindowViewModel vm = BacktestTestFactory.CreateViewModel(dataService: dataService, engine: engine);
        vm.Symbol = "TEST";
        vm.EvaluationStartUtc = evaluationStartUtc;
        vm.EvaluationEndUtc = evaluationEndUtc;

        await vm.RunBacktestCommand.ExecuteAsync(null);

        Assert.Equal(BacktestRunState.Completed, vm.State);
        Assert.NotNull(engine.LastInput);
        return engine.LastInput!;
    }

    [Fact]
    public async Task ExactBoundaryTimestamps_AreBothInclusive()
    {
        // Bars are 2024-01-01..2024-01-10. Start exactly on bar[2] (01-03), end exactly on bar[7] (01-08).
        BacktestInput input = await RunAndCaptureInputAsync(
            new DateTime(2024, 1, 3, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2024, 1, 8, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(8, input.Bars.Length); // bars 01-01..01-08 kept (End is inclusive).
        Assert.Equal(2, input.TradingStartIndex); // first index whose Timestamp >= Start (01-03 -> index 2).
        Assert.Equal(2, input.HistoryStartIndex);
    }

    [Fact]
    public async Task StartBeforeAllBars_ResolvesToIndexZero()
    {
        BacktestInput input = await RunAndCaptureInputAsync(
            new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2024, 1, 10, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(10, input.Bars.Length);
        Assert.Equal(0, input.TradingStartIndex);
    }

    [Fact]
    public async Task EndAfterAllBars_KeepsEveryBar()
    {
        BacktestInput input = await RunAndCaptureInputAsync(
            new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(10, input.Bars.Length);
    }

    [Fact]
    public async Task StartBetweenBars_ResolvesToNextBarIndex()
    {
        // 2024-01-05 12:00 falls strictly between bar[4] (01-05 00:00) and bar[5] (01-06 00:00),
        // so the first bar with Timestamp >= Start must be bar[5] (index 5).
        BacktestInput input = await RunAndCaptureInputAsync(
            new DateTime(2024, 1, 5, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2024, 1, 10, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(5, input.TradingStartIndex);
    }
}
