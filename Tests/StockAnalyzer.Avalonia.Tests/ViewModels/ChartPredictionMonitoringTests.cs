using System.Collections.Immutable;
using CommunityToolkit.Mvvm.Messaging;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using Xunit;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public sealed class ChartPredictionMonitoringTests
{
    [Fact]
    public async Task RealParquetChartProducer_MaturesAndCorrectsOneDurableForecast()
    {
        var root = Path.Combine(Path.GetTempPath(), "sa_t10_chart_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var db = new DuckDBConnectionManager();
            // Keep the production registry in a temporary root without exposing its test-only constructor.
            var constructor = typeof(ModelGenerationRegistry).GetConstructors(
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .Single(c => c.GetParameters()[0].ParameterType == typeof(string));
            using var registry = (ModelGenerationRegistry)constructor.Invoke([Path.Combine(root, "registry"), null, null]);
            var modelPath = Path.Combine(TestSolution.Root, "Tests", "StockAnalyzer.Core.Tests", "Assets", "analysis_fixture.onnx");
            var generation = await registry.RegisterAsync(modelPath);
            using var onnx = new InferenceSession(modelPath);
            var result = new PredictionResult("Up", 1, [new("Up", 1)])
            {
                ModelId = generation.ModelId,
                OutputContract = PredictionModelMetadata.ReadOutputContract(onnx.ModelMetadata.CustomMetadataMap)
            };
            var source = new ParquetDataService(db, Options.Create(new MarketDataSettings { DailyDataPath = root }));
            void Write(decimal last)
            {
                var path = Path.Combine(root, "AAPL.parquet").Replace("\\", "/").Replace("'", "''");
                using var command = db.GetConnection().CreateCommand();
                var price = $"CAST(CASE WHEN n = 199 THEN {last.ToString(System.Globalization.CultureInfo.InvariantCulture)} ELSE 100 END AS DECIMAL(18,6))";
                command.CommandText = $"COPY (SELECT DATE '2026-09-30' + CAST(n-199 AS INTEGER) AS date, "
                    + $"{price} AS open, {price} AS high, {price} AS low, {price} AS close, CAST(100 AS BIGINT) AS volume, "
                    + $"TRUE AS {ParquetDataService.FinalityColumn} FROM range(200) rows(n)) TO '{path}' (FORMAT PARQUET)";
                command.ExecuteNonQuery();
            }
            Write(106m);
            var initial = await source.LoadCandlesAsync("AAPL", TimeFrame.D1, 0);
            var monitor = new PredictionLogService(registry, root: Path.Combine(root, "monitor"));
            var id = await monitor.RecordAsync(result, "AAPL", TimeframeType.Daily, initial.Take(195).ToArray());
            using var chart = Chart(source, monitor);
            await chart.LoadDataAsync(); await chart.MonitoringObservationTask;
            Assert.Equal("Up", (await monitor.ReadAuditAsync(id!))!.Outcomes[^1].ActualLabel);
            Write(94m);
            await chart.LoadDataAsync(); await chart.MonitoringObservationTask;
            var audit = await monitor.ReadAuditAsync(id!);
            Assert.Equal("Down", audit!.Outcomes[^1].ActualLabel);
            Assert.Equal(3, audit.Outcomes.Length);
            Assert.Equal(1, (await monitor.GetSnapshotsAsync(generation.ModelId)).Single().MaturedCount);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static ImmutableArray<CandleData> Bars() => Enumerable.Range(0, 80)
        .Select(i => new CandleData(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i),
            100m + i, 101m + i, 99m + i, 100m + i, 100)).ToImmutableArray();

    private static MonitoringObservation Observe(ImmutableArray<CandleData> bars) =>
        new(PredictionLogService.SourceRevision(bars), DateTimeOffset.UtcNow, bars,
            Enumerable.Repeat(true, bars.Length).ToImmutableArray());

    private static ChartViewModel Chart(IDataService data, IPredictionLogService log, IPredictionService? prediction = null,
        TimeProvider? clock = null) =>
        new(data, new DialogService(), null!, new MockStockAnalyzerSettings(), new TimeFrameManager(data),
            null!, new StockAnalyzer.Core.Theme.ThemeManager(), new MockChartSettingsManager(),
            new SynchronousDispatcherService(), prediction, null!, messenger: new StrongReferenceMessenger(), predictionLog: log, timeProvider: clock);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedRefresh_MonitorsEvenWithMissingOrFailedInference(bool failingInference)
    {
        var bars = Bars(); var source = new EvidenceDataService(bars.TakeLast(40).ToImmutableArray(), Observe(bars));
        var log = new Mock<IPredictionLogService>(); MonitoringObservation? received = null;
        log.Setup(l => l.RefreshAsync("AAPL", TimeframeType.Daily, It.IsAny<MonitoringObservation>(), It.IsAny<CancellationToken>()))
            .Callback<string, TimeframeType, MonitoringObservation, CancellationToken>((_, _, value, _) => received = value)
            .Returns(Task.CompletedTask);
        var prediction = new Mock<IPredictionService>();
        prediction.Setup(p => p.PredictAsync(It.IsAny<IEnumerable<CandleData>>())).ThrowsAsync(new InvalidOperationException("no model"));
        using var chart = Chart(source, log.Object, failingInference ? prediction.Object : null);
        await chart.LoadDataAsync(); await chart.MonitoringObservationTask;
        await chart.PredictionUpdateTask;
        Assert.NotNull(received); Assert.Equal(80, received.Candles.Length);
        Assert.All(received.FinalBars, flag => Assert.True(flag));
        Assert.Null(chart.CurrentPrediction);
        log.Verify(l => l.RefreshAsync("AAPL", TimeframeType.Daily, It.IsAny<MonitoringObservation>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LegacySource_UsesUnknownMaskAfterEveryRefresh()
    {
        var source = new LegacyDataService(Bars()); var log = new Mock<IPredictionLogService>();
        log.Setup(l => l.RefreshAsync(It.IsAny<string>(), It.IsAny<TimeframeType>(), It.IsAny<MonitoringObservation>(), It.IsAny<CancellationToken>()))
            .Callback<string, TimeframeType, MonitoringObservation, CancellationToken>((_, _, value, _) =>
            {
                Assert.All(value.FinalBars, flag => Assert.False(flag));
                Assert.Equal(PredictionLogService.SourceRevision(value.Candles), value.SourceRevision);
            }).Returns(Task.CompletedTask);
        using var chart = Chart(source, log.Object);
        await chart.LoadDataAsync(); await chart.MonitoringObservationTask;
        await chart.LoadDataAsync(); await chart.MonitoringObservationTask;
        log.Verify(l => l.RefreshAsync("AAPL", TimeframeType.Daily, It.IsAny<MonitoringObservation>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task UnknownFallbackObservation_UsesTheInjectedClock()
    {
        var instant = new DateTimeOffset(2026, 10, 2, 3, 4, 5, TimeSpan.Zero);
        var log = new Mock<IPredictionLogService>(); MonitoringObservation? received = null;
        log.Setup(l => l.RefreshAsync(It.IsAny<string>(), It.IsAny<TimeframeType>(), It.IsAny<MonitoringObservation>(), It.IsAny<CancellationToken>()))
            .Callback<string, TimeframeType, MonitoringObservation, CancellationToken>((_, _, value, _) => received = value)
            .Returns(Task.CompletedTask);
        using var chart = Chart(new LegacyDataService(Bars()), log.Object, clock: new FixedClock(instant));
        await chart.LoadDataAsync(); await chart.MonitoringObservationTask;
        Assert.NotNull(received); Assert.Equal(instant, received.ObservedUtc);
        Assert.All(received.FinalBars, flag => Assert.False(flag));
    }

    [Fact]
    public async Task WeeklyAggregation_DoesNotInheritDailyFinality()
    {
        var bars = Bars(); var source = new EvidenceDataService(bars, Observe(bars));
        var log = new Mock<IPredictionLogService>(); MonitoringObservation? received = null;
        log.Setup(l => l.RefreshAsync("AAPL", TimeframeType.Weekly, It.IsAny<MonitoringObservation>(), It.IsAny<CancellationToken>()))
            .Callback<string, TimeframeType, MonitoringObservation, CancellationToken>((_, _, value, _) => received = value)
            .Returns(Task.CompletedTask);
        using var chart = Chart(source, log.Object);
        chart.SelectedTimeFrame = TimeframeType.Weekly;
        await chart.LoadDataAsync(); await chart.MonitoringObservationTask;
        Assert.NotNull(received); Assert.All(received.FinalBars, flag => Assert.False(flag));
        Assert.Contains(TimeFrame.W1, source.RequestedFrames);
        Assert.Equal(chart.Candles.Count, received.Candles.Length);
    }

    [Theory]
    [InlineData(TimeframeType.Weekly)]
    [InlineData(TimeframeType.Monthly)]
    public async Task MatchingProviderPeriodSnapshot_PreservesItsOwnClosureEvidence(TimeframeType timeframe)
    {
        var bars = Bars();
        var periods = TimeFrameAggregator.Aggregate(bars, timeframe.ToCoreTimeFrame()).ToImmutableArray();
        var source = new EvidenceDataService(bars, Observe(periods));
        var log = new Mock<IPredictionLogService>(); MonitoringObservation? received = null;
        log.Setup(l => l.RefreshAsync("AAPL", timeframe, It.IsAny<MonitoringObservation>(), It.IsAny<CancellationToken>()))
            .Callback<string, TimeframeType, MonitoringObservation, CancellationToken>((_, _, value, _) => received = value)
            .Returns(Task.CompletedTask);
        using var chart = Chart(source, log.Object);
        chart.SelectedTimeFrame = timeframe;
        await chart.LoadDataAsync(); await chart.MonitoringObservationTask;
        Assert.NotNull(received); Assert.Equal(periods.Length, received.Candles.Length);
        Assert.All(received.FinalBars, flag => Assert.True(flag));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledOrDisposedRefresh_CannotPublishDelayedEvidence(bool dispose)
    {
        var source = new DelayedEvidenceDataService(Bars()); var log = new Mock<IPredictionLogService>();
        var chart = Chart(source, log.Object); bool disposed = false;
        try
        {
            await chart.LoadDataAsync(); await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var oldRefresh = chart.MonitoringObservationTask;
            if (dispose) { chart.Dispose(); disposed = true; }
            else chart.SelectedTimeFrame = TimeframeType.Weekly;
            source.Completion.SetResult(Observe(Bars()));
            await oldRefresh;
            log.Verify(l => l.RefreshAsync("AAPL", TimeframeType.Daily, It.IsAny<MonitoringObservation>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally { if (!disposed) chart.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidEvidenceOrSourceFailure_DoesNotFabricateOutcomes(bool fail)
    {
        var bars = Bars(); var observation = Observe(bars) with { SourceRevision = new string('f', 64) };
        var source = new EvidenceDataService(bars, observation) { Fail = fail };
        var log = new Mock<IPredictionLogService>(); using var chart = Chart(source, log.Object);
        await chart.LoadDataAsync(); await chart.MonitoringObservationTask;
        log.Verify(l => l.RefreshAsync(It.IsAny<string>(), It.IsAny<TimeframeType>(), It.IsAny<MonitoringObservation>(), It.IsAny<CancellationToken>()), Times.Never);
        log.Verify(l => l.MarkUnavailableAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
    private class LegacyDataService(ImmutableArray<CandleData> bars) : IDataService
    {
        public Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame timeFrame, int count = 100) =>
            Task.FromResult<IReadOnlyList<CandleData>>(bars);
    }
    private sealed class EvidenceDataService(ImmutableArray<CandleData> bars, MonitoringObservation observation) : LegacyDataService(bars), IDataService
    {
        public bool Fail { get; init; }
        public List<TimeFrame> RequestedFrames { get; } = [];
        public Task<MonitoringObservation?> LoadMonitoringObservationAsync(string symbol, TimeFrame timeFrame, CancellationToken cancellationToken = default)
        {
            RequestedFrames.Add(timeFrame);
            if (Fail) throw new IOException("source temporarily unavailable");
            return Task.FromResult<MonitoringObservation?>(observation);
        }
    }
    private sealed class DelayedEvidenceDataService(ImmutableArray<CandleData> bars) : LegacyDataService(bars), IDataService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<MonitoringObservation?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<MonitoringObservation?> LoadMonitoringObservationAsync(string symbol, TimeFrame timeFrame, CancellationToken cancellationToken = default)
        { Started.TrySetResult(); return Completion.Task; }
    }
}
