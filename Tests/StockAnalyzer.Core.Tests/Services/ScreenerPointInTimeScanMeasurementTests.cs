#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Screener;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;
using static StockAnalyzer.Core.Tests.TestHelpers.ScreenerSideFrameFixtures;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>A collection that never runs beside another test: the probe below counts the allocations of the whole process.</summary>
[CollectionDefinition(AllocationMeasurementCollection.Name, DisableParallelization = true)]
public sealed class AllocationMeasurementCollection
{
    public const string Name = "AllocationMeasurement";
}

/// <summary>
/// Opt-in measurement (MCP Part 1 external review, T6): what a point-in-time Screener scan allocates and how long it takes, so that a change of how the daily history is cut at the as-of date
/// (T7: cut once per symbol instead of once per time frame) is decided by numbers. 500 synthetic symbols with 5000 daily bars each, as-of date 30 days before the last stored day, a condition whose
/// sides read D1, W1 and MN1. Procedure: one warm-up run (discarded), then five measured runs; the report holds the MEDIAN and the minimum of the process-wide allocated bytes
/// (<see cref="GC.GetTotalAllocatedBytes(bool)"/>: the scan continues on pool threads, so a per-thread count would under-report), the elapsed time and the Gen2 collections (reference only).
/// Runs only when <see cref="MeasureVariable"/> is set; run it alone (<c>--filter</c>). No threshold is asserted: the test reports, and checks that the scan itself still works.
/// </summary>
[Collection(AllocationMeasurementCollection.Name)]
public sealed class ScreenerPointInTimeScanMeasurementTests
{
    private const string MeasureVariable = "SA_PIT_SCAN_MEASURE";
    private const int SymbolCount = 500;
    private const int DailyBars = 5000;
    private const int AsOfDaysBeforeLastBar = 30;
    private const int WarmUpRuns = 1;
    private const int MeasuredRuns = 5;
    private const int Shapes = 5;
    private const int WeeklySmaPeriod = 20;
    private const int MonthlySmaPeriod = 5;
    private const int DailySmaPeriod = 10;
    private const double BytesPerMegabyte = 1024.0 * 1024.0;

    private readonly ITestOutputHelper _output;

    public ScreenerPointInTimeScanMeasurementTests(ITestOutputHelper output) => _output = output;

    [EnvironmentFact(MeasureVariable)]
    public async Task T6_AllocationsAndTimeOfAPointInTimeScan()
    {
        // Five different series shared by the symbols (the scan reads them, it never changes them), so the data itself costs 5 lists and not 500.
        List<List<CandleData>> shapes = Enumerable.Range(0, Shapes).Select(Shape).ToList();
        var bySymbol = new Dictionary<string, List<CandleData>>(SymbolCount, StringComparer.Ordinal);
        for (int i = 0; i < SymbolCount; i++)
        {
            bySymbol[$"S{i:D3}"] = shapes[i % Shapes];
        }

        List<string> symbols = bySymbol.Keys.OrderBy(symbol => symbol, StringComparer.Ordinal).ToList();
        var data = new RecordingDataService(bySymbol);
        var service = new ScreenerService(data, new PythonIpcTestSettingsBase(), Array.Empty<IMarketDataProvider>());
        DateOnly asOf = DateOnly.FromDateTime(shapes[0][^1].Timestamp).AddDays(-AsOfDaysBeforeLastBar);
        var selection = new ScreenerBarSelection(asOf, true);

        // sides in D1, W1 and MN1 (the bar-set path: one load per symbol, every frame built from the cut daily history)
        var sideFrames = new ScreenerConditionTreeCondition(Group(
            LogicalOperator.And,
            Leaf(Versus(Close(), ComparisonOperator.GreaterThan, Sma(DailySmaPeriod))),
            Leaf(Versus(Sma(WeeklySmaPeriod, TimeFrame.W1), ComparisonOperator.GreaterThan, Sma(MonthlySmaPeriod, TimeFrame.MN1)))));
        // one frame (W1) for both sides, the plain scan over the as-of bars
        ScreenerIndicatorEntry weekly = Versus(Sma(MonthlySmaPeriod), ComparisonOperator.LessThan, Close());

        _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"T6 conditions: {SymbolCount} symbols x {DailyBars} daily bars ({Shapes} shared shapes), as-of {asOf:yyyy-MM-dd} (last stored day - {AsOfDaysBeforeLastBar}), {WarmUpRuns} warm-up + {MeasuredRuns} measured runs, GC.GetTotalAllocatedBytes(precise: true)"));

        IReadOnlyList<string> sideMatched = await ReportAsync(
            "side frames D1/W1/MN1 (ScreenWithSideFramesAsync)",
            async () => (await service.ScreenWithSideFramesAsync(symbols, sideFrames, selection, new Progress<int>(), CancellationToken.None)).Matched);
        IReadOnlyList<string> plainMatched = await ReportAsync(
            "plain W1 scan over the as-of bars",
            async () => await ((IScreenerServiceFactory)service).CreateScreener(new ScreenerBarsDataService(data, selection))
                .ScreenAsync(symbols, weekly, TimeFrame.W1, new Progress<int>(), CancellationToken.None));

        _output.WriteLine($"matched: side frames {sideMatched.Count}, plain W1 {plainMatched.Count} (of {SymbolCount})");
        Assert.NotEmpty(sideMatched);
        Assert.NotEmpty(plainMatched);
    }

    /// <summary>One warm-up run, then <see cref="MeasuredRuns"/> measured runs; writes the median and the minimum and returns the matched symbols of the last run.</summary>
    private async Task<IReadOnlyList<string>> ReportAsync(string name, Func<Task<IReadOnlyList<string>>> scan)
    {
        IReadOnlyList<string> matched = Array.Empty<string>();
        var bytes = new List<long>();
        var millis = new List<double>();
        var gen2 = new List<int>();
        for (int run = 0; run < WarmUpRuns + MeasuredRuns; run++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long before = GC.GetTotalAllocatedBytes(precise: true);
            int collectionsBefore = GC.CollectionCount(2);
            long started = Stopwatch.GetTimestamp();

            matched = await scan();

            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            if (run >= WarmUpRuns)
            {
                bytes.Add(allocated);
                millis.Add(elapsed);
                gen2.Add(GC.CollectionCount(2) - collectionsBefore);
            }
        }

        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{name}: allocated MB median {Median(bytes) / BytesPerMegabyte:F1} (min {bytes.Min() / BytesPerMegabyte:F1}); elapsed ms median {Median(millis):F0} (min {millis.Min():F0}); Gen2 median {Median(gen2.Select(count => (double)count).ToList()):F0}; matched {matched.Count} (fingerprint {Fingerprint(matched)}); runs MB [{string.Join(", ", bytes.Select(b => (b / BytesPerMegabyte).ToString("F1", CultureInfo.InvariantCulture)))}] ms [{string.Join(", ", millis.Select(m => m.ToString("F0", CultureInfo.InvariantCulture)))}]"));
        return matched;
    }

    /// <summary>A short fingerprint of the matched symbols, to see that two variants of the scan match the same set.</summary>
    private static string Fingerprint(IReadOnlyList<string> matched)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join(",", matched.OrderBy(symbol => symbol, StringComparer.Ordinal))))).Substring(0, 12);

    private static double Median(List<long> values) => Median(values.Select(value => (double)value).ToList());

    private static double Median(List<double> values)
    {
        List<double> sorted = values.OrderBy(value => value).ToList();
        int middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    /// <summary>A series of <see cref="DailyBars"/> daily bars (one per calendar day from 2024-01-01): a drift plus a wave whose length depends on <paramref name="shape"/>.</summary>
    private static List<CandleData> Shape(int shape)
        => Enumerable.Range(0, DailyBars)
            .Select(day =>
            {
                decimal close = Math.Round(100m + (day * 0.01m) + (decimal)(10 * Math.Sin(day / (5.0 + (shape * 3)))), 2);
                return new CandleData(FirstDay.AddDays(day), close, close + 1m, close - 1m, close, 1000);
            })
            .ToList();
}
