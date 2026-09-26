using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>
/// Reproduction for "Run, change the signal, Run again: Equity Curve still shows the previous run".
/// Drives the real ViewModel, real preparation service, real <see cref="BacktestEngine"/> and real
/// <see cref="BacktestReportGenerator"/> on real <see cref="CandleData"/> - only the data source is a fake.
/// </summary>
public class BacktestRerunEquityCurveTests
{
    private static readonly DateTime Start = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static List<CandleData> BuildBars(int count)
    {
        var bars = new List<CandleData>(count);
        for (int i = 0; i < count; i++)
        {
            decimal close = 100m + (decimal)(10 * Math.Sin(i / 6.0)) + (i / 10m);
            bars.Add(new CandleData(Start.AddDays(i), close, close + 1m, close - 1m, close, 1000));
        }
        return bars;
    }

    private static BacktestConditionEntry CloseAboveSma(int period, BacktestConditionRole role, TradeSide position) => new()
    {
        Left = new BacktestConditionSide { IndicatorType = IndicatorType.Price, PriceSource = PriceType.Close },
        Operator = role == BacktestConditionRole.ExitOnly ? ComparisonOperator.LessThan : ComparisonOperator.GreaterThan,
        TargetMode = RightHandTargetMode.Indicator,
        Right = new BacktestConditionSide { IndicatorType = IndicatorType.SMA, Parameters = new CoreSmaParameter { Period = period } },
        Role = role,
        Position = position,
    };

    private static BacktestWindowViewModel CreateRealViewModel(List<CandleData> bars)
    {
        BacktestWindowViewModel vm = BacktestTestFactory.CreateViewModel(
            engine: new BacktestEngine(new IndicatorFactory()),
            reportGenerator: new BacktestReportGenerator(),
            dataService: new FakeDataService(bars));
        vm.Symbol = "TEST";
        vm.EvaluationStartUtc = bars[0].Timestamp;
        vm.EvaluationEndUtc = bars[^1].Timestamp;
        return vm;
    }

    [Fact]
    public async Task SecondRunWithDifferentSignals_ReplacesEquityPointsAndRevision()
    {
        List<CandleData> bars = BuildBars(200);
        BacktestWindowViewModel vm = CreateRealViewModel(bars);

        vm.IndicatorSelection.ConditionEntries.Add(CloseAboveSma(5, BacktestConditionRole.EntryOnly, TradeSide.Long));
        vm.IndicatorSelection.ConditionEntries.Add(CloseAboveSma(5, BacktestConditionRole.ExitOnly, TradeSide.Long));
        await vm.RunBacktestCommand.ExecuteAsync(null);
        Assert.True(vm.State == BacktestRunState.Completed, $"Run 1 state={vm.State} message={vm.StatusMessage}");
        var firstEquity = vm.Results.EquityPoints.Select(p => p.Equity).ToArray();
        long firstRevision = vm.Results.ResultRevision;
        Assert.NotEmpty(firstEquity);

        vm.IndicatorSelection.ConditionEntries.Clear();
        vm.IndicatorSelection.ConditionEntries.Add(CloseAboveSma(30, BacktestConditionRole.EntryOnly, TradeSide.Long));
        vm.IndicatorSelection.ConditionEntries.Add(CloseAboveSma(30, BacktestConditionRole.ExitOnly, TradeSide.Long));
        await vm.RunBacktestCommand.ExecuteAsync(null);

        Assert.True(vm.State == BacktestRunState.Completed, $"Run 2 state={vm.State} message={vm.StatusMessage}");
        Assert.True(vm.Results.ResultRevision > firstRevision);
        var secondEquity = vm.Results.EquityPoints.Select(p => p.Equity).ToArray();
        Assert.False(firstEquity.SequenceEqual(secondEquity), "Equity series is identical after changing the signals.");
    }

    [Fact]
    public async Task SecondRunWithDifferentSignals_DifferentSideOrRole_ChangesEquity()
    {
        List<CandleData> bars = BuildBars(200);
        BacktestWindowViewModel vm = CreateRealViewModel(bars);

        vm.IndicatorSelection.ConditionEntries.Add(CloseAboveSma(5, BacktestConditionRole.EntryOnly, TradeSide.Long));
        vm.IndicatorSelection.ConditionEntries.Add(CloseAboveSma(5, BacktestConditionRole.ExitOnly, TradeSide.Long));
        await vm.RunBacktestCommand.ExecuteAsync(null);
        var firstEquity = vm.Results.EquityPoints.Select(p => p.Equity).ToArray();

        // Add a Short-side entry on top of the existing chain: trades must differ.
        vm.IndicatorSelection.ConditionEntries.Add(CloseAboveSma(5, BacktestConditionRole.Reversal, TradeSide.Short));
        await vm.RunBacktestCommand.ExecuteAsync(null);

        Assert.True(vm.State == BacktestRunState.Completed, $"Run 2 state={vm.State} message={vm.StatusMessage}");
        var secondEquity = vm.Results.EquityPoints.Select(p => p.Equity).ToArray();
        Assert.False(firstEquity.SequenceEqual(secondEquity), "Equity series is identical after adding a signal.");
    }
}
