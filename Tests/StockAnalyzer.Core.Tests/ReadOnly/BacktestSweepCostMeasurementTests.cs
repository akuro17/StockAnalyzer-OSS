#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Evaluation;
using StockAnalyzer.Core.Tests.Services;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Core.Tests.ReadOnly;

/// <summary>
/// Opt-in measurement (MCP Part 2 external review, T10): what decides whether the sweep / portfolio paths need a cheaper report (T11). Three numbers, no assertion on time:
/// (a) the share of the adjusted-Sortino bootstrap in one 2,556-bar daily evaluation of a sweep (a batch of 20 runs on ONE symbol, so the bars are read once, at one thread; bootstrap iterations
/// 1000 vs 2000 (the default) vs 4000, the cost per iteration is the difference); (b) the time of the portfolio pre-read (<c>GetCandlesAsync</c> x 20, serial, as the tool does) against the batch of
/// 20 symbols (4 threads, the default <c>OptimizationDegreeOfParallelism</c>); (c) the bytes allocated per run of the sweep. Procedure: one warm-up, then five measured repetitions, the MEDIAN is
/// reported. Runs only when <see cref="MeasureVariable"/> is set; run it alone (<c>--filter</c>).
/// </summary>
[Collection(AllocationMeasurementCollection.Name)]
public sealed class BacktestSweepCostMeasurementTests : IDisposable
{
    private const string MeasureVariable = "SA_BACKTEST_COST_MEASURE";
    private const int Days = 2556;
    private const int SymbolCount = 20;
    private const int PreReadBars = 10_000; // the default MaxCandlesPerRequest, the count the portfolio tool asks for
    private const int BatchThreads = 4;     // the default OptimizationDegreeOfParallelism
    private const int WarmUpRuns = 1;
    private const int MeasuredRuns = 5;
    private const int LowIterations = 1000;
    private const int DefaultIterations = 2000;
    private const int HighIterations = 4000;
    private const double BytesPerMegabyte = 1024.0 * 1024.0;
    private static readonly DateTime Start = new(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2040, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    private readonly ITestOutputHelper _output;
    private readonly string[] _symbols = Enumerable.Range(1, SymbolCount).Select(i => $"S{i:D2}").ToArray();
    private readonly ReadOnlyDataTreeFixture _fixture;
    private readonly DuckDBConnectionManager _db = new();

    public BacktestSweepCostMeasurementTests(ITestOutputHelper output)
    {
        _output = output;
        _fixture = ReadOnlyDataTreeFixture.Create(Days, _symbols);
        for (int s = 0; s < SymbolCount; s++)
        {
            int index = s;
            decimal[] closes = Enumerable.Range(0, Days).Select(i => Math.Round(100m + (decimal)(10 * Math.Sin(i / (6.0 + index))), 2)).ToArray();
            ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, _symbols[s], closes);
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        _fixture.Dispose();
    }

    [EnvironmentFact(MeasureVariable)]
    public async Task T10_BootstrapShareAndPortfolioPreReadAndAllocationPerRun()
    {
        ReadOnlyStockAnalyzerGateway gateway = CreateGateway();
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"T10 {Days} daily bars, {SymbolCount} symbols; {WarmUpRuns} warm-up + {MeasuredRuns} measured repetitions, median"));

        // (a) one symbol, 20 runs with different SMA periods, one thread: the bars are read once, so the time is engine + report only, as in a sweep
        List<BacktestRunSnapshot> sameSymbol(int iterations) => Enumerable.Range(0, SymbolCount).Select(i => Snapshot(_symbols[0], 3 + i, iterations)).ToList();
        (double low, long lowBytes) = await MeasureBatchAsync(gateway, sameSymbol(LowIterations), 1);
        (double normal, long normalBytes) = await MeasureBatchAsync(gateway, sameSymbol(DefaultIterations), 1);
        (double high, long highBytes) = await MeasureBatchAsync(gateway, sameSymbol(HighIterations), 1);
        double perIterationMs = (normal - low) / (DefaultIterations - LowIterations);
        double perIterationMsHigh = (high - normal) / (HighIterations - DefaultIterations);
        double share = DefaultIterations * perIterationMs / normal;
        double shareHigh = DefaultIterations * perIterationMsHigh / normal;
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"(a) 20 runs, 1 thread: {LowIterations} it {low:F0} ms | {DefaultIterations} it {normal:F0} ms | {HighIterations} it {high:F0} ms; per run at {DefaultIterations}: {normal / SymbolCount:F1} ms; " +
            $"bootstrap {perIterationMs * 1000:F1} us/it (1000->2000) and {perIterationMsHigh * 1000:F1} us/it (2000->4000); share of a run at the default {DefaultIterations}: {share:P0} (from 1000->2000) / {shareHigh:P0} (from 2000->4000)"));

        // (a2) the same 20 runs with the bootstrap left out (owner decision D-6 = A, what a sweep now does unless the objective is the adjusted Sortino)
        (double skipped, long skippedBytes) = await MeasureBatchAsync(gateway, sameSymbol(DefaultIterations).Select(snapshot => snapshot with { ComputeAdjustedSortinoBootstrap = false }).ToList(), 1);
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"(a2) 20 runs, 1 thread, bootstrap left out: {skipped:F0} ms (with it {normal:F0} ms) = {normal / skipped:F1}x faster, per run {skipped / SymbolCount:F1} ms; allocated per run {skippedBytes / (double)SymbolCount / BytesPerMegabyte:F2} MB"));


        // (c) allocation per run (process-wide, the batch uses pool threads at 4 threads; here 1 thread, so it is the run itself)
        double perRunMb = normalBytes / (double)SymbolCount / BytesPerMegabyte;
        double perIterationKb = (normalBytes - lowBytes) / (double)(DefaultIterations - LowIterations) / SymbolCount / 1024.0;
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"(c) allocated per sweep run at {DefaultIterations} it: {perRunMb:F2} MB (1000 it {lowBytes / (double)SymbolCount / BytesPerMegabyte:F2} MB, 4000 it {highBytes / (double)SymbolCount / BytesPerMegabyte:F2} MB); " +
            $"bootstrap part {perIterationKb:F2} KB per iteration per run"));

        // (b) portfolio: serial pre-read of the bars of 20 symbols (what the tool does to see the first and last bar) against the batch of 20 symbols (the real run)
        var preRead = new List<double>();
        var batch = new List<double>();
        List<BacktestRunSnapshot> portfolio = _symbols.Select(symbol => Snapshot(symbol, 10, DefaultIterations)).ToList();
        for (int run = 0; run < WarmUpRuns + MeasuredRuns; run++)
        {
            long started = Stopwatch.GetTimestamp();
            foreach (string symbol in _symbols)
            {
                IReadOnlyList<CandleData> bars = await gateway.GetCandlesAsync(symbol, TimeFrame.D1, PreReadBars);
                Assert.True(bars.Count > 0);
            }

            double preReadMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            started = Stopwatch.GetTimestamp();
            IReadOnlyList<BacktestEvaluationArtifact> artifacts = await gateway.RunBacktestBatchAsync(portfolio, BatchThreads);
            double batchMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Assert.Equal(SymbolCount, artifacts.Count);
            if (run >= WarmUpRuns)
            {
                preRead.Add(preReadMs);
                batch.Add(batchMs);
            }
        }

        double preReadMedian = Median(preRead);
        double batchMedian = Median(batch);
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"(b) portfolio of {SymbolCount} symbols: pre-read (serial GetCandlesAsync x {SymbolCount}, {PreReadBars} bars each) {preReadMedian:F0} ms | batch ({BatchThreads} threads) {batchMedian:F0} ms | pre-read share of pre-read + batch: {preReadMedian / (preReadMedian + batchMedian):P0}"));
        _output.WriteLine("Decision rule (plan): T11 only if the bootstrap share >= 30 % of a run or the pre-read share >= 20 % of the portfolio call.");
    }

    /// <summary>One warm-up and <see cref="MeasuredRuns"/> measured batches; returns the median milliseconds and the median process-wide allocated bytes.</summary>
    private static async Task<(double Milliseconds, long Bytes)> MeasureBatchAsync(ReadOnlyStockAnalyzerGateway gateway, List<BacktestRunSnapshot> snapshots, int threads)
    {
        var millis = new List<double>();
        var bytes = new List<long>();
        for (int run = 0; run < WarmUpRuns + MeasuredRuns; run++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long before = GC.GetTotalAllocatedBytes(precise: true);
            long started = Stopwatch.GetTimestamp();

            IReadOnlyList<BacktestEvaluationArtifact> artifacts = await gateway.RunBacktestBatchAsync(snapshots, threads);

            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            Assert.Equal(snapshots.Count, artifacts.Count);
            Assert.All(artifacts, artifact => Assert.NotNull(artifact.Report));
            if (run >= WarmUpRuns)
            {
                millis.Add(elapsed);
                bytes.Add(allocated);
            }
        }

        return (Median(millis), bytes.OrderBy(value => value).ToArray()[bytes.Count / 2]);
    }

    private static double Median(List<double> values) => values.OrderBy(value => value).ToArray()[values.Count / 2];

    private ReadOnlyStockAnalyzerGateway CreateGateway()
        => SmaCrossBacktestKit.CreateGateway(new ParquetDataService(_db, Options.Create(_fixture.Settings)), _db, _fixture);

    private static BacktestRunSnapshot Snapshot(string symbol, int smaPeriod, int bootstrapIterations)
        => SmaCrossBacktestKit.Snapshot(symbol, smaPeriod, Start, End, bootstrapIterations);
}
