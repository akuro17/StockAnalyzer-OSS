using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>The drawdown flags the Results tab hands to the equity curve: one per drawn point, computed once per presentation.</summary>
public class BacktestEquityUnderwaterFlagsPresentationTests
{
    private static readonly DateTime BaseUtc = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // BacktestTestFactory.CreateResult uses InitialCapital = 1_000_000.
    private const decimal InitialCapital = 1_000_000m;

    private static ImmutableArray<EquityPoint> Points(params decimal[] equity) =>
        equity.Select((e, i) => new EquityPoint(i, BaseUtc.AddDays(i), e, e, 0m, 0m)).ToImmutableArray();

    private static BacktestResultsViewModel NewViewModel() =>
        new(NullLocalizationService.Instance, new FakeBacktestReportExporter(), new FakeDialogService());

    [Fact]
    public void NoResult_HasNoFlags()
    {
        BacktestResultsViewModel vm = NewViewModel();

        Assert.False(vm.EquityUnderwaterFlags.IsDefault);
        Assert.Empty(vm.EquityUnderwaterFlags);
    }

    [Fact]
    public void Update_ProducesOneFlagPerEquityPoint_StartingFromTheInitialCapital()
    {
        BacktestResultsViewModel vm = NewViewModel();

        vm.Update(
            BacktestTestFactory.CreateResult(equityPoints: Points(InitialCapital - 10m, InitialCapital, InitialCapital + 50m, InitialCapital + 20m)),
            BacktestTestFactory.CreateStubReport(),
            evaluationStartIndex: 0);

        Assert.Equal(vm.EquityPoints.Length, vm.EquityUnderwaterFlags.Length);
        // below the initial capital -> drawdown; back at the initial capital -> recovered; new high; below that high.
        Assert.Equal(new[] { true, false, false, true }, vm.EquityUnderwaterFlags.ToArray());
    }

    [Fact]
    public void Update_WithAnEvaluationStart_FlagsTheSlicedSeries_WhoseHighStillStartsAtTheInitialCapital()
    {
        BacktestResultsViewModel vm = NewViewModel();
        // The first two points lie before the evaluation start and are not drawn; the drawn series is 900k, 950k, 1.1M.
        ImmutableArray<EquityPoint> all = Points(InitialCapital + 500_000m, InitialCapital + 400_000m, 900_000m, 950_000m, 1_100_000m);

        vm.Update(BacktestTestFactory.CreateResult(equityPoints: all), BacktestTestFactory.CreateStubReport(), evaluationStartIndex: 2);

        Assert.Equal(3, vm.EquityPoints.Length);
        Assert.Equal(3, vm.EquityUnderwaterFlags.Length);
        // Against the initial capital (1M), not against the discarded warm-up highs.
        Assert.Equal(new[] { true, true, false }, vm.EquityUnderwaterFlags.ToArray());
    }

    [Fact]
    public void Update_EvaluationStartBeyondTheSeries_HasNoFlags()
    {
        BacktestResultsViewModel vm = NewViewModel();

        vm.Update(
            BacktestTestFactory.CreateResult(equityPoints: Points(InitialCapital + 1m)),
            BacktestTestFactory.CreateStubReport(),
            evaluationStartIndex: 5);

        Assert.Empty(vm.EquityPoints);
        Assert.Empty(vm.EquityUnderwaterFlags);
    }

    [Fact]
    public void Update_RaisesTheFlagsNotificationAfterThePointsAndBeforeTheRevision()
    {
        BacktestResultsViewModel vm = NewViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Update(
            BacktestTestFactory.CreateResult(equityPoints: Points(InitialCapital + 1m, InitialCapital - 1m)),
            BacktestTestFactory.CreateStubReport(),
            evaluationStartIndex: 0);

        int points = raised.IndexOf(nameof(BacktestResultsViewModel.EquityPoints));
        int flags = raised.IndexOf(nameof(BacktestResultsViewModel.EquityUnderwaterFlags));
        int revision = raised.IndexOf(nameof(BacktestResultsViewModel.ResultRevision));
        Assert.True(points >= 0 && flags > points && revision > flags,
            $"expected EquityPoints({points}) < EquityUnderwaterFlags({flags}) < ResultRevision({revision})");
    }
}
