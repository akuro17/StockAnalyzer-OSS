using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Moq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Evaluation;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using StockAnalyzer.Core.Services.Screener;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Core.Tests.ReadOnly;

/// <summary>
/// Plan 04 / T1: <see cref="IReadOnlyStockAnalyzerGateway.RunBacktestBatchAsync"/> must return, per snapshot and in input order, exactly what
/// <see cref="IReadOnlyStockAnalyzerGateway.RunBacktestAsync"/> returns (proof (a)), independent of the degree of parallelism (proof (b)),
/// while reading each stored series once. The stored closes swing as a sine wave, so "close above its SMA" trades and has drawdown.
/// </summary>
public sealed class RunBacktestBatchTests : IDisposable
{
    private const int Days = 400;
    private const string Msft = "MSFT";
    private const string Toyota = "7203.T";
    private static readonly DateTime Start = new(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2024, 12, 31, 0, 0, 0, DateTimeKind.Utc);
    private static readonly int[] SmaPeriods = { 3, 4, 5, 6, 8, 10, 12, 15 };

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, Msft, Toyota);
    private readonly DuckDBConnectionManager _db = new();
    private readonly CountingDataService _data;

    public RunBacktestBatchTests()
    {
        decimal[] closes = Enumerable.Range(0, Days).Select(i => Math.Round(100m + (decimal)(10 * Math.Sin(i / 6.0)), 2)).ToArray();
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, "MSFT", closes);
        decimal[] shifted = Enumerable.Range(0, Days).Select(i => Math.Round(2000m + (decimal)(150 * Math.Sin(i / 9.0)), 2)).ToArray();
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, "7203-T", shifted);
        _data = new CountingDataService(new ParquetDataService(_db, Options.Create(_fixture.Settings)));
    }

    public void Dispose()
    {
        _db.Dispose();
        _fixture.Dispose();
    }

    private ReadOnlyStockAnalyzerGateway CreateGateway() => SmaCrossBacktestKit.CreateGateway(_data, _db, _fixture);

    private static BacktestRunSnapshot Snapshot(string symbol, int smaPeriod, ExecutionModel model = ExecutionModel.Legacy)
        => SmaCrossBacktestKit.Snapshot(symbol, smaPeriod, Start, End, BacktestDefaults.BootstrapIterations, model);

    private static List<BacktestRunSnapshot> Grid(string symbol = Msft) => SmaPeriods.Select(p => Snapshot(symbol, p)).ToList();

    private static void AssertSameArtifact(BacktestEvaluationArtifact expected, BacktestEvaluationArtifact actual, string label)
    {
        Assert.True(expected.ReportIdentity.IsAvailable, label);
        Assert.Equal(expected.RunFingerprint.Sha256, actual.RunFingerprint.Sha256);
        Assert.Equal(expected.ReportIdentity.Sha256, actual.ReportIdentity.Sha256);
        Assert.Equal(Convert.ToHexString(expected.Result.ReproducibilityHash), Convert.ToHexString(actual.Result.ReproducibilityHash));
        Assert.Equal(expected.RunStatus, actual.RunStatus);
        Assert.Equal(expected.Result.Trades.Length, actual.Result.Trades.Length);
        Assert.Equal(expected.Result.EquityPoints.Length, actual.Result.EquityPoints.Length);
        Assert.Equal(expected.Result.EquityPoints[^1].Equity, actual.Result.EquityPoints[^1].Equity);
        Assert.Equal(expected.Report!.TotalTrades, actual.Report!.TotalTrades);
        Assert.Equal(expected.Report.TotalPnL.Value, actual.Report.TotalPnL.Value);
        Assert.Equal(expected.Report.AnnualizedSharpe.Value, actual.Report.AnnualizedSharpe.Value);
        Assert.Equal(expected.Report.MaxDrawdown.Value, actual.Report.MaxDrawdown.Value);
    }

    // ---- proof (a): batch == individual runs -------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryBatchResultEqualsTheIndividualRunOfItsSnapshot()
    {
        var gateway = CreateGateway();
        List<BacktestRunSnapshot> snapshots = Grid();
        var individual = new List<BacktestEvaluationArtifact>();
        foreach (BacktestRunSnapshot snapshot in snapshots)
        {
            individual.Add(await gateway.RunBacktestAsync(snapshot));
        }

        IReadOnlyList<BacktestEvaluationArtifact> batch = await gateway.RunBacktestBatchAsync(snapshots, degreeOfParallelism: 4);

        Assert.Equal(snapshots.Count, batch.Count);
        Assert.True(individual.Sum(a => a.Result.Trades.Length) > 0);
        Assert.Equal(snapshots.Count, individual.Select(a => a.RunFingerprint.Sha256).Distinct().Count());
        for (int i = 0; i < snapshots.Count; i++)
        {
            AssertSameArtifact(individual[i], batch[i], $"SMA({SmaPeriods[i]})");
        }
    }

    // ---- proof (b): the degree of parallelism changes nothing --------------------------------------------------------------------------

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(16)]
    public async Task TheResultDoesNotDependOnTheDegreeOfParallelism(int degree)
    {
        var gateway = CreateGateway();
        List<BacktestRunSnapshot> snapshots = Grid();

        IReadOnlyList<BacktestEvaluationArtifact> sequential = await gateway.RunBacktestBatchAsync(snapshots, degreeOfParallelism: 1);
        IReadOnlyList<BacktestEvaluationArtifact> parallel = await gateway.RunBacktestBatchAsync(snapshots, degreeOfParallelism: degree);

        Assert.Equal(sequential.Count, parallel.Count);
        for (int i = 0; i < sequential.Count; i++)
        {
            AssertSameArtifact(sequential[i], parallel[i], $"SMA({SmaPeriods[i]}) dop={degree}");
        }
    }

    [Fact]
    public async Task TheOutputKeepsTheInputOrderForMixedSymbolsAndRepeatedSnapshots()
    {
        var gateway = CreateGateway();
        var snapshots = new List<BacktestRunSnapshot>
        {
            Snapshot(Toyota, 5), Snapshot("msft", 5), Snapshot(Toyota, 10), Snapshot(Msft, 5), Snapshot(Toyota, 5),
        };

        IReadOnlyList<BacktestEvaluationArtifact> batch = await gateway.RunBacktestBatchAsync(snapshots, degreeOfParallelism: 4);

        Assert.Equal(new[] { "7203-T", "MSFT", "7203-T", "MSFT", "7203-T" }, batch.Select(a => a.Metadata.Symbol).ToArray());
        Assert.Equal(batch[1].RunFingerprint.Sha256, batch[3].RunFingerprint.Sha256);
        Assert.Equal(batch[0].RunFingerprint.Sha256, batch[4].RunFingerprint.Sha256);
        Assert.NotEqual(batch[0].RunFingerprint.Sha256, batch[2].RunFingerprint.Sha256);
        for (int i = 0; i < snapshots.Count; i++)
        {
            AssertSameArtifact(await gateway.RunBacktestAsync(snapshots[i]), batch[i], $"item {i}");
        }
    }

    // ---- the adjusted-Sortino bootstrap can be left out of a sweep run (owner decision D-6 = A) --------------------------------------------

    [Fact]
    public async Task SkippingTheBootstrapChangesOnlyTheAdjustedSortinoAndTheReportIdentity()
    {
        var gateway = CreateGateway();
        BacktestRunSnapshot full = Snapshot(Msft, 5);
        BacktestRunSnapshot skipped = full with { ComputeAdjustedSortinoBootstrap = false };

        BacktestEvaluationArtifact withBootstrap = await gateway.RunBacktestAsync(full);
        BacktestEvaluationArtifact without = await gateway.RunBacktestAsync(skipped);
        BacktestEvaluationArtifact viaBatch = (await gateway.RunBacktestBatchAsync(new[] { skipped }, degreeOfParallelism: 1)).Single();

        // control: the normal run computes it (so the skip is what removes it)
        Assert.Equal(MetricStatus.Valid, withBootstrap.Report!.AnnualizedSortinoAutocorrelationAdjusted.Status);
        Assert.NotNull(withBootstrap.Report.AnnualizedSortinoAutocorrelationAdjustedConfidenceInterval);
        Assert.True(withBootstrap.BootstrapDiagnostics.ExecutedReplicates > 0);

        MetricValue adjusted = without.Report!.AnnualizedSortinoAutocorrelationAdjusted;
        Assert.Equal(MetricStatus.NotApplicable, adjusted.Status);
        Assert.Equal(MetricReason.NotRequested, adjusted.Reason);
        Assert.Null(adjusted.Value);
        Assert.Null(without.Report.AnnualizedSortinoAutocorrelationAdjustedConfidenceInterval);
        Assert.Equal(BootstrapDiagnostics.NotRequestedReason, without.BootstrapDiagnostics.Reason);
        Assert.Equal(0, without.BootstrapDiagnostics.ExecutedReplicates);

        // the run itself and its identity are untouched; every other metric is identical
        Assert.Equal(withBootstrap.RunFingerprint.Sha256, without.RunFingerprint.Sha256);
        Assert.Equal(Convert.ToHexString(withBootstrap.Result.ReproducibilityHash), Convert.ToHexString(without.Result.ReproducibilityHash));
        Assert.Equal(withBootstrap.Result.Trades.Length, without.Result.Trades.Length);
        Assert.True(withBootstrap.Result.Trades.Length > 0);
        foreach (System.Reflection.PropertyInfo property in typeof(BacktestReport).GetProperties().Where(p => p.PropertyType == typeof(MetricValue) && p.Name != nameof(BacktestReport.AnnualizedSortinoAutocorrelationAdjusted)))
        {
            Assert.Equal(property.GetValue(withBootstrap.Report), property.GetValue(without.Report));
        }

        // the report identity tells the two kinds of run apart, and the batch path equals the single path
        Assert.NotEqual(withBootstrap.ReportIdentity.Sha256, without.ReportIdentity.Sha256);
        Assert.Equal(without.ReportIdentity.Sha256, viaBatch.ReportIdentity.Sha256);
    }

    [Fact]
    public async Task TheDefaultSnapshotStillComputesTheBootstrap()
    {
        var gateway = CreateGateway();

        BacktestEvaluationArtifact artifact = await gateway.RunBacktestAsync(Snapshot(Msft, 5));

        Assert.True(Snapshot(Msft, 5).ComputeAdjustedSortinoBootstrap);
        Assert.Equal(MetricStatus.Valid, artifact.Report!.AnnualizedSortinoAutocorrelationAdjusted.Status);
    }


    // ---- arithmetic failure capture (owner decision D-8 = C) ------------------------------------------------------------------------------

    /// <summary>A position of 1e27 shares overflows decimal at the first entry (about 1e29 against a limit of 7.9e28).</summary>
    private static BacktestRunSnapshot Overflowing(string symbol, bool capture)
    {
        BacktestRunSnapshot normal = Snapshot(symbol, 5);
        return normal with
        {
            Configuration = new BacktestConfiguration
            {
                InitialCapital = BacktestDefaults.InitialCapital,
                SizingModel = PositionSizingModel.FixedQuantity,
                SizingParameter = 1e27m,
                ExecutionModel = ExecutionModel.Legacy,
            },
            CaptureArithmeticFailures = capture,
        };
    }

    [Fact]
    public async Task WithCapture_AnEngineOverflowIsAFailedArtifactWithTheWhereOfTheFailure()
    {
        var gateway = CreateGateway();

        BacktestEvaluationArtifact artifact = await gateway.RunBacktestAsync(Overflowing(Msft, capture: true));

        Assert.Equal(RunStatus.Failed, artifact.RunStatus);
        BacktestRunDiagnostic diagnostic = Assert.IsType<BacktestRunDiagnostic>(artifact.RunDiagnostic);
        Assert.Equal(BacktestDiagnosticCode.ArithmeticOverflow, diagnostic.Code);
        Assert.Equal(BacktestRunPhase.Bar, diagnostic.Phase);
        Assert.NotNull(diagnostic.BarIndex);
        Assert.NotNull(diagnostic.BarTimestamp);
        Assert.Null(artifact.Report);
        Assert.True(artifact.RunFingerprint.IsAvailable); // sealed together with the diagnostic
    }

    [Fact]
    public async Task WithoutCapture_TheSameOverflowStillThrows_AndAnOrdinaryRunHasNoDiagnostic()
    {
        var gateway = CreateGateway();

        await Assert.ThrowsAsync<OverflowException>(() => gateway.RunBacktestAsync(Overflowing(Msft, capture: false)));
        BacktestEvaluationArtifact ordinary = await gateway.RunBacktestAsync(Snapshot(Msft, 5) with { CaptureArithmeticFailures = true });

        Assert.Equal(RunStatus.Completed, ordinary.RunStatus);
        Assert.Null(ordinary.RunDiagnostic);
        Assert.Equal((await gateway.RunBacktestAsync(Snapshot(Msft, 5))).RunFingerprint.Sha256, ordinary.RunFingerprint.Sha256); // the flag changes nothing of a run that does not overflow
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task TheBatchFailureIsTheLowestIndexAndCarriesIt_WhateverTheThreadCount(int threads)
    {
        var gateway = CreateGateway();
        var snapshots = new List<BacktestRunSnapshot>
        {
            Snapshot(Msft, 5), Overflowing(Msft, capture: false), Snapshot(Msft, 6), Overflowing(Toyota, capture: false),
        };

        OverflowException error = await Assert.ThrowsAsync<OverflowException>(() => gateway.RunBacktestBatchAsync(snapshots, threads));

        Assert.Equal(1, BacktestBatchFailure.ItemIndexOf(error));
        Assert.Null(BacktestBatchFailure.ItemIndexOf(new OverflowException("not from a batch")));
    }


    // ---- the stored bars are read once per batch ----------------------------------------------------------------------------------------

    [Fact]
    public async Task TheStoredBarsOfASeriesAreLoadedOncePerBatchAndOncePerSingleRun()
    {
        var gateway = CreateGateway();
        List<BacktestRunSnapshot> snapshots = Grid();

        int before = _data.WholeHistoryLoads;
        await gateway.RunBacktestBatchAsync(snapshots, degreeOfParallelism: 4);
        int batchLoads = _data.WholeHistoryLoads - before;

        before = _data.WholeHistoryLoads;
        foreach (BacktestRunSnapshot snapshot in snapshots)
        {
            await gateway.RunBacktestAsync(snapshot);
        }

        Assert.Equal(1, batchLoads);
        Assert.Equal(snapshots.Count, _data.WholeHistoryLoads - before);
    }

    [Fact]
    public async Task TwoSymbolsInOneBatchAreLoadedOnceEach()
    {
        var gateway = CreateGateway();
        var snapshots = SmaPeriods.SelectMany(p => new[] { Snapshot(Msft, p), Snapshot(Toyota, p) }).ToList();

        int before = _data.WholeHistoryLoads;
        await gateway.RunBacktestBatchAsync(snapshots, degreeOfParallelism: 8);

        Assert.Equal(2, _data.WholeHistoryLoads - before);
    }

    // ---- projection (a large batch keeps only what the caller needs) ----------------------------------------------------------------------

    [Fact]
    public async Task AProjectedBatchHoldsTheProjectionsOfTheSameRunsInInputOrder()
    {
        var gateway = CreateGateway();
        List<BacktestRunSnapshot> snapshots = Grid();
        IReadOnlyList<BacktestEvaluationArtifact> artifacts = await gateway.RunBacktestBatchAsync(snapshots, degreeOfParallelism: 4);

        IReadOnlyList<(string Fingerprint, int Trades, decimal? Pnl)> projected = await gateway.RunBacktestBatchAsync(
            snapshots, 4, artifact => (artifact.RunFingerprint.Sha256!, artifact.Result.Trades.Length, artifact.Report?.TotalPnL.Value));

        Assert.Equal(
            artifacts.Select(a => (a.RunFingerprint.Sha256!, a.Result.Trades.Length, a.Report?.TotalPnL.Value)).ToArray(),
            projected.ToArray());
    }

    [Fact]
    public async Task AFailingProjectionIsAFailureOfItsSnapshotAndTheLowestIndexWins()
    {
        var gateway = CreateGateway();
        List<BacktestRunSnapshot> snapshots = Grid();

        for (int repeat = 0; repeat < 5; repeat++)
        {
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.RunBacktestBatchAsync(
                snapshots, 8, artifact => artifact.Result.Trades.Length >= 0 ? throw new InvalidOperationException(artifact.RunFingerprint.Sha256) : 0));
            Assert.Equal((await gateway.RunBacktestAsync(snapshots[0])).RunFingerprint.Sha256, error.Message);
        }
    }

    [Fact]
    public async Task ANullProjectionIsRefused()
    {
        var gateway = CreateGateway();

        await Assert.ThrowsAsync<ArgumentNullException>(() => gateway.RunBacktestBatchAsync<int>(Grid(), 2, null!));
    }

    // ---- failures, arguments, cancellation ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(16)]
    public async Task TheFirstFailureInInputOrderIsRethrownWithItsOwnTypeWhateverTheParallelism(int degree)
    {
        var gateway = CreateGateway();
        var snapshots = new List<BacktestRunSnapshot>
        {
            Snapshot(Msft, 5),
            Snapshot("../MSFT", 5),                                           // index 1: ArgumentException (symbol not well formed)
            Snapshot(Msft, 6),
            Snapshot(Msft, 7, ExecutionModel.StrictEvidence),                 // index 3: NotSupportedException
            Snapshot(Msft, 8),
            Snapshot(Msft, 10),
        };

        for (int repeat = 0; repeat < 10; repeat++)
        {
            ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(
                () => gateway.RunBacktestBatchAsync(snapshots, degree));
            Assert.Equal("symbol", error.ParamName);
        }
    }

    [Fact]
    public async Task AFailureWithoutAnEarlierFailureKeepsItsTypeAndHidesNoResult()
    {
        var gateway = CreateGateway();
        var snapshots = new List<BacktestRunSnapshot> { Snapshot(Msft, 5), Snapshot(Msft, 7, ExecutionModel.YFinanceApproximate), Snapshot(Msft, 8) };
        BacktestEvaluationArtifact first = await gateway.RunBacktestAsync(snapshots[0]);

        await Assert.ThrowsAsync<NotSupportedException>(() => gateway.RunBacktestBatchAsync(snapshots, 4));
        await Assert.ThrowsAsync<NotSupportedException>(() => gateway.RunBacktestAsync(snapshots[1]));

        // The failed batch left nothing behind: the same snapshot still runs alone and gives the same artifact.
        AssertSameArtifact(first, await gateway.RunBacktestAsync(snapshots[0]), "after the failed batch");
    }

    [Fact]
    public async Task AnEmptyBatchReturnsAnEmptyListWithoutReadingAnyBar()
    {
        var gateway = CreateGateway();
        int before = _data.WholeHistoryLoads;

        IReadOnlyList<BacktestEvaluationArtifact> batch = await gateway.RunBacktestBatchAsync(Array.Empty<BacktestRunSnapshot>(), 4);

        Assert.Empty(batch);
        Assert.Equal(before, _data.WholeHistoryLoads);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task ADegreeOfParallelismBelowOneIsRefused(int degree)
    {
        var gateway = CreateGateway();

        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => gateway.RunBacktestBatchAsync(Grid(), degree));
        Assert.Equal("degreeOfParallelism", error.ParamName);
    }

    [Fact]
    public async Task ANullListIsRefused()
    {
        var gateway = CreateGateway();

        await Assert.ThrowsAsync<ArgumentNullException>(() => gateway.RunBacktestBatchAsync(null!, 2));
    }

    [Fact]
    public async Task ACancelledTokenStopsTheBatchWithoutAResult()
    {
        var gateway = CreateGateway();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gateway.RunBacktestBatchAsync(Grid(), 4, cts.Token));
    }

    // ---- the preparation overload with a cache -------------------------------------------------------------------------------------------

    [Fact]
    public async Task PreparingThroughACacheGivesTheSameRunAsPreparingDirectly()
    {
        var preparation = new BacktestRunPreparationService(_data);
        BacktestRunSnapshot snapshot = Snapshot("MSFT", 5);
        var cache = new BacktestBarCache();

        PreparedBacktestRun direct = await preparation.PrepareAsync(snapshot, CancellationToken.None);
        PreparedBacktestRun cached = await preparation.PrepareAsync(snapshot, cache, CancellationToken.None);
        PreparedBacktestRun cachedAgain = await preparation.PrepareAsync(snapshot, cache, CancellationToken.None);

        Assert.Equal(direct.Input.Bars, cached.Input.Bars);
        Assert.Equal(direct.EvaluationStartIndex, cached.EvaluationStartIndex);
        Assert.Equal(direct.Input.Bars, cachedAgain.Input.Bars);
        Assert.Equal(direct.ReportOptions.AnnualPeriods, cached.ReportOptions.AnnualPeriods);
    }

    [Fact]
    public async Task TheDefaultBatchOfAHostWithoutSharedLoadingRunsTheSnapshotsOneByOneInOrder()
    {
        var real = CreateGateway();
        BacktestEvaluationArtifact artifact = await real.RunBacktestAsync(Snapshot(Msft, 5));
        var seen = new List<string>();
        var mock = new Mock<IReadOnlyStockAnalyzerGateway> { CallBase = true };
        mock.Setup(g => g.RunBacktestAsync(It.IsAny<BacktestRunSnapshot>(), It.IsAny<CancellationToken>()))
            .Returns<BacktestRunSnapshot, CancellationToken>((s, _) =>
            {
                seen.Add(s.Symbol);
                return Task.FromResult(artifact);
            });
        var snapshots = new List<BacktestRunSnapshot> { Snapshot("AAA", 5), Snapshot("BBB", 5), Snapshot("CCC", 5) };

        IReadOnlyList<BacktestEvaluationArtifact> batch = await mock.Object.RunBacktestBatchAsync(snapshots, 4);

        Assert.Equal(new[] { "AAA", "BBB", "CCC" }, seen);
        Assert.Equal(3, batch.Count);
        Assert.All(batch, a => Assert.Same(artifact, a));
    }

    // ---- BacktestBarCache ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheCacheLoadsEachKeyOnceEvenWhenManyCallersRace()
    {
        var cache = new BacktestBarCache();
        int loads = 0;
        var bars = new List<CandleData> { new(new DateTime(2024, 1, 1), 1m, 2m, 0.5m, 1.5m, 10) };
        Task<IReadOnlyList<CandleData>> Load()
        {
            Interlocked.Increment(ref loads);
            return Task.FromResult<IReadOnlyList<CandleData>>(bars);
        }

        IReadOnlyList<CandleData>[] results = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => cache.GetOrLoadAsync("MSFT", TimeFrame.D1, Load))));
        await cache.GetOrLoadAsync("MSFT", TimeFrame.W1, Load);
        await cache.GetOrLoadAsync("OTHER", TimeFrame.D1, Load);

        Assert.Equal(3, loads);
        Assert.All(results, r => Assert.Same(bars, r));
    }

    [Fact]
    public async Task AFailedLoadStaysFailedForItsKeyAndDoesNotTouchOtherKeys()
    {
        var cache = new BacktestBarCache();
        int loads = 0;
        Task<IReadOnlyList<CandleData>> Fail()
        {
            Interlocked.Increment(ref loads);
            return Task.FromException<IReadOnlyList<CandleData>>(new InvalidOperationException("boom"));
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetOrLoadAsync("MSFT", TimeFrame.D1, Fail));
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetOrLoadAsync("MSFT", TimeFrame.D1, Fail));
        IReadOnlyList<CandleData> other = await cache.GetOrLoadAsync("OTHER", TimeFrame.D1, () => Task.FromResult<IReadOnlyList<CandleData>>(Array.Empty<CandleData>()));

        Assert.Equal(1, loads);
        Assert.Empty(other);
    }

    /// <summary>Counts the loads of a whole stored history (count 0), the way the backtest preparation reads it.</summary>
    private sealed class CountingDataService : IDataService
    {
        private readonly IDataService _inner;
        private int _wholeHistoryLoads;

        public CountingDataService(IDataService inner) => _inner = inner;

        public int WholeHistoryLoads => Volatile.Read(ref _wholeHistoryLoads);

        public Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame timeFrame, int count = 100)
        {
            if (count == 0)
            {
                Interlocked.Increment(ref _wholeHistoryLoads);
            }

            return _inner.LoadCandlesAsync(symbol, timeFrame, count);
        }

        public Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame timeFrame, int count, bool includePartialBar, CancellationToken cancellationToken = default)
            => _inner.LoadCandlesAsync(symbol, timeFrame, count, includePartialBar, cancellationToken);

        public Task<MonitoringObservation?> LoadMonitoringObservationAsync(string symbol, TimeFrame timeFrame, CancellationToken cancellationToken = default)
            => _inner.LoadMonitoringObservationAsync(symbol, timeFrame, cancellationToken);
    }
}
