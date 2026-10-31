using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// IoHydrationWebAiReview P-3 (measurement only, no production change): the workspace restore applies the indicator list with
/// <c>Indicators.Clear()</c> and one <c>Add</c> per indicator, and every collection notification starts an indicator calculation. The test records how
/// long that takes on the calling (UI) thread for 30 indicators and how often the calculation id moves. The plan's threshold is a median of 16.7 ms
/// (one frame at 60 Hz) over 20 runs; the timing is printed for the step log and deliberately not asserted (a timing assertion would be machine dependent).
/// </summary>
// Shares the static WeakReferenceMessenger.Default with other MainWindowViewModel/TickerListViewModel-hosting tests.
[Collection("MessengerSharedState")]
public class WorkspaceIndicatorApplyMeasurementTests
{
    private const int IndicatorCount = 30;
    private const int Runs = 20;

    private readonly ITestOutputHelper _output;
    public WorkspaceIndicatorApplyMeasurementTests(ITestOutputHelper output) => _output = output;

    private static readonly FieldInfo CalculationId =
        typeof(ChartViewModel).GetField("_indicatorCalcId", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static List<CoreIndicatorSettings> ThirtyIndicators() => Enumerable.Range(0, IndicatorCount)
        .Select(i => i % 3 == 0
            ? new CoreIndicatorSettings { TypeEnum = IndicatorType.SMA, IsEnabled = true, IsOverlay = true, ParameterObject = new CoreSmaParameter { Period = 5 + i } }
            : i % 3 == 1
                ? new CoreIndicatorSettings { TypeEnum = IndicatorType.EMA, IsEnabled = true, IsOverlay = true, ParameterObject = new CoreEmaParameter { Period = 5 + i } }
                : new CoreIndicatorSettings { TypeEnum = IndicatorType.RSI, IsEnabled = true, IsOverlay = false, ParameterObject = new CoreRsiParameter { Period = 5 + i } })
        .ToList();

    private static CoreCandleData Bar(int index) =>
        new(new DateTime(2024, 1, 1).AddDays(index), 100m + index % 7, 102m + index % 7, 98m + index % 7, 101m + index % 5, 1000 + index);

    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    public void ApplyIndicators_ThirtyIndicators_TimeOnTheCallingThreadAndCalculationIdIncrements(int candleCount)
    {
        var h = MainWindowViewModelFactory.Create();
        using var vm = h.Vm;
        var chart = vm.ChartViewModel;
        chart.Candles = Enumerable.Range(0, candleCount).Select(Bar).ToArray();
        IWorkspaceLayoutTarget target = vm;

        target.ApplyIndicators(ThirtyIndicators()); // warm-up: JIT and first subscriptions are not what a restore pays every time

        var milliseconds = new List<double>();
        var idIncrements = new List<int>();
        for (int run = 0; run < Runs; run++)
        {
            var indicators = ThirtyIndicators();
            int before = (int)CalculationId.GetValue(chart)!;
            long start = Stopwatch.GetTimestamp();
            target.ApplyIndicators(indicators);
            milliseconds.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            idIncrements.Add((int)CalculationId.GetValue(chart)! - before);
        }

        var sorted = milliseconds.OrderBy(m => m).ToList();
        double median = (sorted[Runs / 2 - 1] + sorted[Runs / 2]) / 2;
        _output.WriteLine($"candles={candleCount} indicators={IndicatorCount} runs={Runs}: median={median:F2} ms, min={sorted[0]:F2} ms, max={sorted[^1]:F2} ms (threshold 16.7 ms)");
        _output.WriteLine($"calculation id increments per apply: {string.Join(",", idIncrements.Distinct())} (expected {IndicatorCount + 1} = Clear + {IndicatorCount} Add)");

        Assert.Equal(IndicatorCount, chart.Indicators.Count);
        Assert.All(idIncrements, increment => Assert.Equal(IndicatorCount + 1, increment));
    }
}
