#nullable enable
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class PredictionLogServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_t10_monitor_" + Guid.NewGuid().ToString("N"));
    private readonly RetrainingSchedulerTests.MutableClock _clock = new(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
    private ModelGenerationRegistry? _registry;
    private PredictionResult? _result;
    private PredictionLogService Create() => new(_registry!, _clock, Path.Combine(_root, "monitor"));
    private async Task<PredictionLogService> Setup()
    {
        _registry = new ModelGenerationRegistry(Path.Combine(_root, "registry"));
        var model = await _registry.RegisterAsync(Path.Combine("Assets", "analysis_fixture.onnx"));
        using var session = new InferenceSession(Path.Combine("Assets", "analysis_fixture.onnx"));
        _result = new("Up", 1, [new("Up", 1)])
        {
            ModelId = model.ModelId, OutputContract = PredictionModelMetadata.ReadOutputContract(session.ModelMetadata.CustomMetadataMap),
        };
        return Create();
    }

    private static ImmutableArray<CandleData> Bars(decimal future = 106m) => Enumerable.Range(0, 200)
        .Select(i => new CandleData(new DateTime(2026, 9, 30).AddDays(i - 199),
            i == 199 ? future : 100m, i == 199 ? future : 100m, i == 199 ? future : 100m,
            i == 199 ? future : 100m, 100)).ToImmutableArray();
    private MonitoringObservation Observe(ImmutableArray<CandleData> bars, bool final = true) =>
        new(PredictionLogService.SourceRevision(bars), _clock.Now, bars, Enumerable.Repeat(final, bars.Length).ToImmutableArray());

    [Theory]
    [InlineData(100, 100.5, "Neutral")]
    [InlineData(100, 99.5, "Neutral")]
    [InlineData(100, 100.501, "Up")]
    [InlineData(100, 99.499, "Down")]
    public void Labels_UseDecimalStrictTrainingBoundaries(double anchor, double future, string label) =>
        Assert.Equal(label, PredictionLogService.ActualLabel((decimal)anchor, (decimal)future, .005m));

    [Fact]
    public async Task DurableReplay_CorrectionsReplaceVote_RetainOriginalForecastAndAudit()
    {
        var monitor = await Setup(); var bars = Bars(); var inputs = bars.Take(195).ToArray();
        var id = await monitor.RecordAsync(_result!, "X", TimeframeType.Daily, inputs);
        Assert.Equal(id, await Create().RecordAsync(_result!, "X", TimeframeType.Daily, inputs));
        await monitor.RefreshAsync("X", TimeframeType.Daily, Observe(bars));
        var before = await monitor.ReadAuditAsync(id!);
        Assert.Equal(PredictionOutcomeState.Matured, before!.Outcomes[^1].State); Assert.Equal("Up", before.Outcomes[^1].ActualLabel);
        _clock.Now = _clock.Now.AddMinutes(1);
        await monitor.RefreshAsync("X", TimeframeType.Daily, Observe(Bars(94m)));
        await monitor.RefreshAsync("X", TimeframeType.Daily, Observe(bars) with { ObservedUtc = _clock.Now.AddMinutes(-1) });
        await monitor.RefreshAsync("X", TimeframeType.Daily, Observe(Bars(94m)));
        var corrected = await Create().ReadAuditAsync(id!);
        Assert.Equal(3, corrected!.Outcomes.Length); Assert.Equal("Down", corrected.Outcomes[^1].ActualLabel);
        Assert.Equal("Up", corrected.Prediction.PredictedLabel); Assert.Equal(1, (await monitor.GetSnapshotsAsync(_result!.ModelId!)).Single().MaturedCount);
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "monitor", "predictions"), "*.json"));
        Assert.NotEqual(corrected.Outcomes[1].SourceRevision, corrected.Outcomes[2].SourceRevision);
    }

    [Fact]
    public async Task MissingFutureOrFinality_RemainsPending_NotUnavailableByTime()
    {
        var monitor = await Setup(); var bars = Bars();
        var id = await monitor.RecordAsync(_result!, "X", TimeframeType.Daily, bars.Take(195).ToArray());
        await monitor.RefreshAsync("X", TimeframeType.Daily, Observe(bars.Take(199).ToImmutableArray()));
        await monitor.RefreshAsync("X", TimeframeType.Daily, Observe(bars, false));
        _clock.Now = _clock.Now.AddDays(100);
        await monitor.RefreshAsync("X", TimeframeType.Daily, Observe(bars, false));
        Assert.Equal(PredictionOutcomeState.Pending, (await monitor.ReadAuditAsync(id!))!.Outcomes[^1].State);
        var state = (await monitor.GetSnapshotsAsync(_result!.ModelId!)).Single();
        Assert.Equal(ModelHealthState.InsufficientData, state.State); Assert.Null(state.Accuracy);
        await monitor.MarkUnavailableAsync(id!, "provider explicitly deleted the source");
        Assert.Equal(PredictionOutcomeState.Unavailable, (await monitor.ReadAuditAsync(id!))!.Outcomes[^1].State);
        await monitor.RefreshAsync("X", TimeframeType.Daily, Observe(bars));
        Assert.Equal(0, (await monitor.GetSnapshotsAsync(_result.ModelId!)).Single().MaturedCount);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("CAST(NULL AS BOOLEAN)", false)]
    [InlineData("FALSE", false)]
    [InlineData("CASE WHEN date = DATE '2026-09-30' THEN FALSE ELSE TRUE END", false)]
    [InlineData("CASE WHEN date = DATE '2026-09-30' THEN CAST(NULL AS BOOLEAN) ELSE TRUE END", false)]
    [InlineData("TRUE", true)]
    public async Task RealProviderEvidence_ControlsMaturityAndConfirmedCorrections(string? final, bool matured)
    {
        var monitor = await Setup();
        using var source = new MonitoringParquetFixture();
        var bars = ParquetMonitoringObservationTests.Bars();
        var id = await monitor.RecordAsync(_result!, "X", TimeframeType.Daily, bars.Take(195).ToArray());
        source.Write(bars, final);
        var snapshot = await source.Service.LoadMonitoringObservationAsync("X", TimeFrame.D1);
        await monitor.RefreshAsync("X", TimeframeType.Daily, snapshot! with { ObservedUtc = _clock.Now });
        Assert.Equal(matured ? PredictionOutcomeState.Matured : PredictionOutcomeState.Pending,
            (await monitor.ReadAuditAsync(id!))!.Outcomes[^1].State);
        if (!matured) return;
        _clock.Now = _clock.Now.AddMinutes(1);
        source.Write(ParquetMonitoringObservationTests.Bars(94m), "TRUE");
        snapshot = await source.Service.LoadMonitoringObservationAsync("X", TimeFrame.D1);
        await monitor.RefreshAsync("X", TimeframeType.Daily, snapshot! with { ObservedUtc = _clock.Now });
        var audit = await monitor.ReadAuditAsync(id!);
        Assert.Equal("Down", audit!.Outcomes[^1].ActualLabel);
        Assert.Equal(3, audit.Outcomes.Length);
        Assert.Equal(1, (await monitor.GetSnapshotsAsync(_result!.ModelId!)).Single().MaturedCount);
        Assert.NotEqual(audit.Outcomes[1].DatasetRevision, audit.Outcomes[2].DatasetRevision);
    }

    [Fact]
    public async Task ThirtyMatured_ExactHalfHealthy_TransitionOnlyAlert_RecoveryRearms()
    {
        var monitor = await Setup(); var bars = Bars(); int alerts = 0;
        monitor.Decayed += (_, _) => alerts++;
        for (int i = 0; i < 30; i++)
        {
            await monitor.RecordAsync(_result!, "S" + i, TimeframeType.Daily, bars.Take(195).ToArray());
            await monitor.RefreshAsync("S" + i, TimeframeType.Daily, Observe(bars));
            if (i == 28) Assert.Equal(ModelHealthState.InsufficientData, (await monitor.GetSnapshotsAsync(_result!.ModelId!)).Single().State);
        }
        Assert.Equal(ModelHealthState.Healthy, (await monitor.GetSnapshotsAsync(_result!.ModelId!)).Single().State);
        for (int i = 0; i < 15; i++) await monitor.RefreshAsync("S" + i, TimeframeType.Daily, Observe(Bars(94m)));
        Assert.Equal(.5m, (await monitor.GetSnapshotsAsync(_result.ModelId!)).Single().Accuracy); Assert.Equal(0, alerts);
        await monitor.RefreshAsync("S15", TimeframeType.Daily, Observe(Bars(94m)));
        await monitor.RefreshAsync("S15", TimeframeType.Daily, Observe(Bars(94m))); Assert.Equal(1, alerts);
        Assert.Equal(ModelHealthState.Decayed, (await Create().GetSnapshotsAsync(_result.ModelId!)).Single().State);
        await monitor.RefreshAsync("S15", TimeframeType.Daily, Observe(bars));
        await monitor.RefreshAsync("S15", TimeframeType.Daily, Observe(Bars(94m))); Assert.Equal(2, alerts);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_root, "monitor", "alerts"), "*.json").Length);
        _clock.Now = _clock.Now.AddDays(31);
        await monitor.RefreshAsync("S15", TimeframeType.Daily, Observe(bars));
        Assert.Equal(0, (await monitor.GetSnapshotsAsync(_result.ModelId!)).Single().MaturedCount);
    }

    [Fact]
    public async Task InvalidSourceAndFallbackAreRejected_AndMaturityUsesLabelEndWindow()
    {
        var monitor = await Setup(); var bars = Bars();
        Assert.Null(await monitor.RecordAsync(PredictionResult.Empty, "X", TimeframeType.Daily, bars));
        Assert.Null(await monitor.RecordAsync(_result! with { IsRegression = true }, "X", TimeframeType.Daily, bars));
        await monitor.RecordAsync(_result!, "X", TimeframeType.Daily, bars.Take(195).ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => monitor.RefreshAsync("X", TimeframeType.Daily, Observe(bars) with { ObservedUtc = _clock.Now.AddDays(1) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => monitor.RefreshAsync("X", TimeframeType.Daily, Observe(bars.Reverse().ToImmutableArray())));
        _clock.Now = new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero);
        await monitor.RefreshAsync("X", TimeframeType.Daily, Observe(bars));
        Assert.Equal(1, (await monitor.GetSnapshotsAsync(_result!.ModelId!)).Single().MaturedCount);
        _clock.Now = _clock.Now.AddDays(1);
        await monitor.RefreshAsync("X", TimeframeType.Daily, Observe(bars));
        Assert.Equal(0, (await monitor.GetSnapshotsAsync(_result.ModelId!)).Single().MaturedCount);
    }

    // ---- FP02: current-time health -------------------------------------------------------------------------------

    private static readonly DateTime Sept2 = new(2026, 9, 2);
    private static readonly DateTime Sept28 = new(2026, 9, 28);

    private static ImmutableArray<CandleData> BarsEnding(DateTime end, decimal future) => Enumerable.Range(0, 200)
        .Select(i => new CandleData(end.AddDays(i - 199),
            i == 199 ? future : 100m, i == 199 ? future : 100m, i == 199 ? future : 100m,
            i == 199 ? future : 100m, 100)).ToImmutableArray();

    /// <summary>Records a forecast ("Up") for <paramref name="symbol"/> and observes it as matured on <paramref name="end"/>.</summary>
    private async Task Vote(PredictionLogService monitor, string symbol, DateTime end, bool correct)
    {
        await monitor.RecordAsync(_result!, symbol, TimeframeType.Daily, BarsEnding(end, 106m).Take(195).ToArray());
        await ObserveVote(monitor, symbol, end, correct);
    }
    private Task ObserveVote(PredictionLogService monitor, string symbol, DateTime end, bool correct) =>
        monitor.RefreshAsync(symbol, TimeframeType.Daily, Observe(BarsEnding(end, correct ? 106m : 94m)));

    [Theory]
    [InlineData(0, 0, ModelHealthState.InsufficientData)]
    [InlineData(29, 29, ModelHealthState.InsufficientData)]
    [InlineData(30, 0, ModelHealthState.Decayed)]
    [InlineData(30, 14, ModelHealthState.Decayed)]
    [InlineData(30, 15, ModelHealthState.Healthy)]
    [InlineData(30, 16, ModelHealthState.Healthy)]
    [InlineData(30, 30, ModelHealthState.Healthy)]
    [InlineData(31, 15, ModelHealthState.Decayed)]
    [InlineData(31, 16, ModelHealthState.Healthy)]
    public void HealthState_IsAPureFunctionOfMaturedAndCorrectCounts(int matured, int correct, ModelHealthState expected) =>
        Assert.Equal(expected, PredictionMonitoringPolicy.ComputeHealthState(matured, correct));

    [Fact]
    public void Accuracy_IsNullWithoutResults_ElseTheExactDecimalRatio_AndInvalidCountsAreRejected()
    {
        Assert.Null(PredictionMonitoringPolicy.ComputeAccuracy(0, 0));
        Assert.Equal((decimal)14 / 30, PredictionMonitoringPolicy.ComputeAccuracy(30, 14));
        Assert.Equal(.5m, PredictionMonitoringPolicy.ComputeAccuracy(30, 15));
        Assert.Throws<ArgumentOutOfRangeException>(() => PredictionMonitoringPolicy.ComputeHealthState(5, 6));
        Assert.Throws<ArgumentOutOfRangeException>(() => PredictionMonitoringPolicy.ComputeHealthState(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PredictionMonitoringPolicy.ComputeAccuracy(5, -1));
    }

    [Fact]
    public async Task ServiceIsBothTheCachedReaderAndTheCurrentHealthEvaluator()
    {
        var monitor = await Setup();
        Assert.IsAssignableFrom<IPredictionLogService>(monitor);
        Assert.IsAssignableFrom<ICurrentPredictionHealthService>(monitor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-sha256")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task EvaluateCurrent_InvalidModelId_ThrowsBeforeAnyIo(string modelId)
    {
        var monitor = await Setup();
        await Assert.ThrowsAsync<ArgumentException>(() => monitor.EvaluateCurrentAsync(modelId));
        Assert.False(Directory.Exists(Path.Combine(_root, "monitor")));
    }

    [Fact]
    public async Task EvaluateCurrent_EmptyStore_ReturnsEmptyAndCreatesNothing()
    {
        var monitor = await Setup();
        Assert.Empty(await monitor.EvaluateCurrentAsync());
        Assert.Empty(await monitor.EvaluateCurrentAsync(new string('a', 64)));
        Assert.False(Directory.Exists(Path.Combine(_root, "monitor")));
    }

    [Fact]
    public async Task EvaluateCurrent_ForecastWithoutMaturedResult_IsAnEmptyInsufficientGroup()
    {
        var monitor = await Setup();
        await monitor.RecordAsync(_result!, "X", TimeframeType.Daily, Bars().Take(195).ToArray());
        var group = Assert.Single(await monitor.EvaluateCurrentAsync(_result!.ModelId));
        Assert.Equal((0, 0, null, ModelHealthState.InsufficientData), (group.MaturedCount, group.CorrectCount, group.Accuracy, group.State));
        Assert.Empty(await monitor.EvaluateCurrentAsync(new string('b', 64)));
    }

    [Fact]
    public async Task EvaluateCurrent_ExpiresVotesByClockAlone_AndLegacyCachedReadKeepsItsContract()
    {
        var monitor = await Setup();
        await monitor.RecordAsync(_result!, "X", TimeframeType.Daily, Bars().Take(195).ToArray());
        await monitor.RefreshAsync("X", TimeframeType.Daily, Observe(Bars()));
        var model = _result!.ModelId!;
        _clock.Now = _clock.Now.AddDays(31);
        Assert.Equal(1, (await monitor.GetSnapshotsAsync(model)).Single().MaturedCount); // cached read is not time-aware
        var current = Assert.Single(await monitor.EvaluateCurrentAsync(model));
        Assert.Equal((0, 0, null, ModelHealthState.InsufficientData), (current.MaturedCount, current.CorrectCount, current.Accuracy, current.State));
        Assert.Equal(_clock.Now, current.EvaluatedUtc);
        Assert.Equal(0, (await monitor.GetSnapshotsAsync(model)).Single().MaturedCount); // the evaluation persisted it
    }

    [Fact]
    public async Task EvaluateCurrent_WindowEndpoints_ExcludeLowerBoundAndFutureButIncludeEvaluationInstant()
    {
        var monitor = await Setup();
        await monitor.RecordAsync(_result!, "X", TimeframeType.Daily, Bars().Take(195).ToArray());
        await monitor.RefreshAsync("X", TimeframeType.Daily, Observe(Bars()));
        var model = _result!.ModelId!; var labelEnd = new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero);
        async Task<int> Matured(DateTimeOffset at)
        {
            _clock.Now = at;
            return (await monitor.EvaluateCurrentAsync(model)).Single().MaturedCount;
        }
        Assert.Equal(0, await Matured(labelEnd.AddTicks(-1)));            // LabelEnd after v
        Assert.Equal(1, await Matured(labelEnd));                         // LabelEnd == v is included
        Assert.Equal(1, await Matured(labelEnd.AddDays(30).AddTicks(-1)));
        Assert.Equal(0, await Matured(labelEnd.AddDays(30)));             // LabelEnd == v-30d is excluded
    }

    [Fact]
    public async Task EvaluateCurrent_ExpiredCorrectVotes_CauseOneHealthyToDecayedTransition_AndRepeatsAreSilent()
    {
        var monitor = await Setup(); int alerts = 0, changed = 0;
        for (int i = 0; i < 10; i++) await Vote(monitor, "O" + i, Sept2, true);
        for (int i = 0; i < 14; i++) await Vote(monitor, "C" + i, Sept28, true);
        for (int i = 0; i < 16; i++) await Vote(monitor, "W" + i, Sept28, false);
        monitor.Decayed += (_, _) => alerts++; monitor.Changed += (_, _) => changed++;
        var model = _result!.ModelId!;

        var healthy = Assert.Single(await monitor.EvaluateCurrentAsync(model));
        Assert.Equal((40, 24, ModelHealthState.Healthy), (healthy.MaturedCount, healthy.CorrectCount, healthy.State));
        Assert.Equal(0, alerts);

        _clock.Now = new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero); // the 09-02 votes leave the 30-day window
        Assert.Equal(40, (await monitor.GetSnapshotsAsync(model)).Single().MaturedCount);
        int before = changed;
        var decayed = Assert.Single(await monitor.EvaluateCurrentAsync(model));
        Assert.Equal((30, 14, (decimal)14 / 30, ModelHealthState.Decayed), (decayed.MaturedCount, decayed.CorrectCount, decayed.Accuracy, decayed.State));
        Assert.NotNull(decayed.LastAlertId); Assert.Equal(1, alerts); Assert.Equal(before + 1, changed);

        Assert.Equal(decayed, Assert.Single(await monitor.EvaluateCurrentAsync(model)));  // repeat at the same instant
        _clock.Now = _clock.Now.AddMinutes(1);
        var later = Assert.Single(await monitor.EvaluateCurrentAsync(model));             // only EvaluatedUtc moved
        Assert.Equal(decayed with { EvaluatedUtc = later.EvaluatedUtc }, later); Assert.Equal(_clock.Now, later.EvaluatedUtc);
        Assert.Equal(1, alerts); Assert.Equal(before + 1, changed);
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "monitor", "alerts"), "*.json"));

        _clock.Now = _clock.Now.AddMinutes(1);
        await ObserveVote(monitor, "W0", Sept28, true);   // K=15 -> Healthy again (recovery)
        Assert.Equal(ModelHealthState.Healthy, Assert.Single(await monitor.EvaluateCurrentAsync(model)).State);
        await ObserveVote(monitor, "C0", Sept28, false);  // K=14 -> Decayed again: the re-armed alert fires once more
        Assert.Equal(ModelHealthState.Decayed, Assert.Single(await monitor.EvaluateCurrentAsync(model)).State);
        Assert.Equal(2, alerts);
    }

    [Fact]
    public async Task EvaluateCurrent_InitialDecayedFromInsufficientData_RaisesNoAlert()
    {
        var monitor = await Setup(); int alerts = 0; monitor.Decayed += (_, _) => alerts++;
        for (int i = 0; i < 14; i++) await Vote(monitor, "C" + i, Sept28, true);
        for (int i = 0; i < 16; i++) await Vote(monitor, "W" + i, Sept28, false);
        var group = Assert.Single(await monitor.EvaluateCurrentAsync(_result!.ModelId));
        Assert.Equal((30, 14, ModelHealthState.Decayed), (group.MaturedCount, group.CorrectCount, group.State));
        Assert.Null(group.LastAlertId); Assert.Equal(0, alerts);
        Assert.False(Directory.Exists(Path.Combine(_root, "monitor", "alerts")));
    }

    [Fact]
    public async Task EvaluateCurrent_SavedStateInconsistentWithCounts_IsRejectedWithoutWrites()
    {
        var monitor = await Setup(); int alerts = 0; monitor.Decayed += (_, _) => alerts++;
        await Vote(monitor, "X", Sept28, true);
        var path = Path.Combine(_root, "monitor", "health.json");
        var saved = JsonSerializer.Deserialize<System.Collections.Generic.List<ModelMonitoringSnapshot>>(File.ReadAllText(path), TrainingConfigJson.Options)!;
        Assert.Equal(ModelHealthState.InsufficientData, saved.Single().State);
        File.WriteAllText(path, JsonSerializer.Serialize(new[] { saved.Single() with { State = ModelHealthState.Healthy } }, TrainingConfigJson.Options));
        var bytes = File.ReadAllBytes(path);
        await Assert.ThrowsAsync<InvalidDataException>(() => monitor.EvaluateCurrentAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => monitor.GetSnapshotsAsync(_result!.ModelId!));
        Assert.Equal(bytes, File.ReadAllBytes(path)); Assert.Equal(0, alerts);
        Assert.False(Directory.Exists(Path.Combine(_root, "monitor", "alerts")));
    }

    [Fact]
    public async Task EvaluateCurrent_CancelledToken_ThrowsWithoutWriting()
    {
        var monitor = await Setup();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.EvaluateCurrentAsync(null, new CancellationToken(true)));
        Assert.False(Directory.Exists(Path.Combine(_root, "monitor")));
    }

    public void Dispose() { _registry?.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
