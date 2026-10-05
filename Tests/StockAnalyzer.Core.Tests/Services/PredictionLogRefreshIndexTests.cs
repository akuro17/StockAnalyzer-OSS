#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Moq;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// FP04: the indexed anchor search and the keyed prior-health lookup must keep the exact semantics of the former linear
/// scans. Audits are written directly (real records, real service, real files), so any model/target group can be built.
/// </summary>
public sealed class PredictionLogRefreshIndexTests : IDisposable
{
    private const int Horizon = 5;
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTime LastDay = new(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_fp04_" + Guid.NewGuid().ToString("N"));
    private readonly string _monitor;
    private readonly PredictionLogService _service;
    private int _sequence;

    public PredictionLogRefreshIndexTests()
    {
        _monitor = Path.Combine(_root, "monitor");
        _service = new PredictionLogService(new Mock<IModelGenerationRegistry>().Object, new RetrainingSchedulerTests.MutableClock(Now), _monitor);
    }

    private static string Hex(string seed) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed))).ToLowerInvariant();

    private static ImmutableArray<CandleData> DailyBars(int count, decimal lastClose = 106m, DateTime? lastDay = null) =>
        Enumerable.Range(0, count).Select(i =>
        {
            var close = i == count - 1 ? lastClose : 100m;
            return new CandleData((lastDay ?? LastDay).AddDays(i - count + 1), close, close, close, close, 100);
        }).ToImmutableArray();

    private static MonitoringObservation Observe(ImmutableArray<CandleData> bars, bool final = true) =>
        new(PredictionLogService.SourceRevision(bars), Now, bars, Enumerable.Repeat(final, bars.Length).ToImmutableArray());

    private async Task<string> WriteAudit(string symbol, ImmutableArray<CandleData> bars, int anchorRow, int horizon = Horizon,
        string model = "model", string target = "target", PredictionOutcomeRevision? matured = null)
    {
        var id = Hex("prediction" + _sequence++);
        var anchor = PredictionLogService.Utc(bars[anchorRow].Timestamp);
        var prediction = new MonitoredPrediction(id, Hex(model), Hex("hash"), symbol, TimeframeType.Daily, anchor, horizon, .005m,
            "Up", Hex(target), Hex("feature"), Hex("input"), anchor);
        var outcomes = ImmutableArray.Create(new PredictionOutcomeRevision(0, Hex("pending"), PredictionOutcomeState.Pending, anchor.AddMinutes(1)));
        if (matured is not null) outcomes = outcomes.Add(matured);
        await AtomicJsonFile.SaveAsync(Path.Combine(_monitor, "predictions", id + ".json"),
            new PredictionAuditRecord(PredictionMonitoringPolicy.SchemaVersion, prediction, outcomes), TrainingConfigJson.Options);
        return id;
    }

    private async Task<PredictionOutcomeRevision> LatestOutcome(string id) => (await _service.ReadAuditAsync(id))!.Outcomes[^1];

    // ---- the pure index ----------------------------------------------------------------------------------------

    [Fact]
    public void Index_RejectsNull_AcceptsEmptyAndSingleRow_AndRejectsDuplicateTimestamps()
    {
        Assert.Throws<ArgumentNullException>(() => MonitoringAnchorIndex.Create(null!));
        var empty = MonitoringAnchorIndex.Create(Array.Empty<CandleData>());
        Assert.Equal(-1, empty.Find(Now));
        var single = MonitoringAnchorIndex.Create(DailyBars(1));
        Assert.Equal((0, -1), (single.Find(PredictionLogService.Utc(LastDay)), single.Find(Now)));
        var duplicated = new[] { DailyBars(1)[0], DailyBars(1)[0] };
        Assert.Throws<InvalidDataException>(() => MonitoringAnchorIndex.Create(duplicated));
    }

    [Fact]
    public void Index_AgreesWithTheLinearScanItReplaces_OnRandomBarsAndAnchors()
    {
        var random = new Random(20261002);
        var bars = new List<CandleData>(); var day = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 500; i++)
        {
            day = day.AddDays(1 + random.Next(4)); // gaps (weekends/holidays) are allowed, order stays strict
            var kind = random.Next(2) == 0 ? DateTimeKind.Utc : DateTimeKind.Unspecified; // Local is rejected upstream
            bars.Add(new CandleData(DateTime.SpecifyKind(day, kind), 1m, 1m, 1m, 1m, 1));
        }
        static int Linear(IReadOnlyList<CandleData> candles, DateTimeOffset anchor)
        {
            for (int i = 0; i < candles.Count; i++) if (PredictionLogService.Utc(candles[i].Timestamp) == anchor) return i;
            return -1;
        }
        var index = MonitoringAnchorIndex.Create(bars);
        for (int n = 0; n < 2_000; n++)
        {
            var probe = random.Next(3) switch
            {
                0 => PredictionLogService.Utc(bars[random.Next(bars.Count)].Timestamp),                          // present
                1 => PredictionLogService.Utc(bars[random.Next(bars.Count)].Timestamp).AddHours(random.Next(1, 23)), // between bars
                _ => PredictionLogService.Utc(bars[random.Next(bars.Count)].Timestamp).ToOffset(TimeSpan.FromHours(9)), // same instant, other offset
            };
            Assert.Equal(Linear(bars, probe), index.Find(probe));
        }
    }

    // ---- refresh: indexed search keeps every outcome ---------------------------------------------------------------

    [Fact]
    public async Task EmptyAndSingleRowObservations_MatureNothing()
    {
        var bars = DailyBars(10); var id = await WriteAudit("S", bars, 4);
        await _service.RefreshAsync("S", TimeframeType.Daily, Observe(ImmutableArray<CandleData>.Empty));
        await _service.RefreshAsync("S", TimeframeType.Daily, Observe(bars.Take(1).ToImmutableArray()));
        Assert.Equal(PredictionOutcomeState.Pending, (await LatestOutcome(id)).State);
    }

    [Fact]
    public async Task LastValidTargetMatures_OneBarShortOrMissingAnchorOrHugeHorizonStayPending()
    {
        var bars = DailyBars(10);
        var last = await WriteAudit("S", bars, 4);                       // anchor 4 + h5 = row 9, the last bar
        var oneShort = await WriteAudit("S", bars, 5);                    // needs row 10
        var missing = await WriteAudit("S", DailyBars(10, lastDay: LastDay.AddDays(-400)), 4); // anchor absent from the observation
        var huge = await WriteAudit("S", bars, 4, horizon: int.MaxValue);  // h >= B - a must be rejected without index overflow
        await _service.RefreshAsync("S", TimeframeType.Daily, Observe(bars));
        var matured = await LatestOutcome(last);
        Assert.Equal((PredictionOutcomeState.Matured, "Up"), (matured.State, matured.ActualLabel));
        Assert.Equal(PredictionLogService.Utc(bars[9].Timestamp), matured.LabelEndUtc);
        foreach (var id in new[] { oneShort, missing, huge })
            Assert.Equal(PredictionOutcomeState.Pending, (await LatestOutcome(id)).State);
    }

    [Fact]
    public async Task InvalidObservations_FailBeforeAnySearchOrWrite()
    {
        var bars = DailyBars(10); var id = await WriteAudit("S", bars, 4);
        var before = File.ReadAllBytes(Path.Combine(_monitor, "predictions", id + ".json"));
        var mismatch = Observe(bars) with { FinalBars = Enumerable.Repeat(true, 9).ToImmutableArray() };
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.RefreshAsync("S", TimeframeType.Daily, mismatch));
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.RefreshAsync("S", TimeframeType.Daily,
            new MonitoringObservation(Hex("x"), Now, default, default)));
        var duplicated = bars.SetItem(5, bars[4]);
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.RefreshAsync("S", TimeframeType.Daily, Observe(duplicated)));
        var local = bars.Select(c => new CandleData(DateTime.SpecifyKind(c.Timestamp, DateTimeKind.Local), c.Open, c.High, c.Low, c.Close, c.Volume)).ToImmutableArray();
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.RefreshAsync("S", TimeframeType.Daily, Observe(local)));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(_monitor, "predictions", id + ".json")));
    }

    [Fact]
    public async Task UnchangedOhlcvWithDifferentFinality_DecidesMaturityOnlyThroughTheFinalityFlags()
    {
        var bars = DailyBars(10); var id = await WriteAudit("S", bars, 4);
        await _service.RefreshAsync("S", TimeframeType.Daily, Observe(bars, final: false));
        Assert.Equal(PredictionOutcomeState.Pending, (await LatestOutcome(id)).State);
        await _service.RefreshAsync("S", TimeframeType.Daily, Observe(bars));
        Assert.Equal((PredictionOutcomeState.Matured, 2), ((await LatestOutcome(id)).State, (await _service.ReadAuditAsync(id))!.Outcomes.Length));
        await _service.RefreshAsync("S", TimeframeType.Daily, Observe(bars, final: false)); // unknown finality never withdraws a vote
        await _service.RefreshAsync("S", TimeframeType.Daily, Observe(bars));               // same evidence again adds no revision
        Assert.Equal(2, (await _service.ReadAuditAsync(id))!.Outcomes.Length);
    }

    // ---- evaluation: keyed prior-health lookup keeps every group's predecessor ------------------------------------

    [Fact]
    public async Task PriorHealthLookup_IsPerExactGroup_SoOnlyTheDecayingGroupAlertsAndOthersKeepTheirAlertId()
    {
        var bars = DailyBars(10);
        PredictionOutcomeRevision Correct() => new(1, Hex("old-source"), PredictionOutcomeState.Matured, Now.AddHours(-1),
            PredictionLogService.Utc(bars[9].Timestamp), "Up", DatasetRevision: Hex("old-dataset"));
        for (int i = 0; i < 30; i++) { await WriteAudit("A", bars, 4, model: "modelA", matured: Correct()); await WriteAudit("B", bars, 4, model: "modelB", matured: Correct()); }
        ModelMonitoringSnapshot Healthy(string model, string? alert) => new(Hex(model), Hex("target"), 30, 30, 1m,
            ModelHealthState.Healthy, Now.AddHours(-1), alert);
        await AtomicJsonFile.SaveAsync(Path.Combine(_monitor, "health.json"),
            new[] { Healthy("modelB", "carried-alert"), Healthy("modelA", null) }, TrainingConfigJson.Options);
        var alerts = new List<ModelDecayAlert>(); _service.Decayed += (_, alert) => alerts.Add(alert);

        await _service.RefreshAsync("A", TimeframeType.Daily, Observe(DailyBars(10, lastClose: 94m))); // every A vote becomes Down: wrong
        var a = (await _service.GetSnapshotsAsync(Hex("modelA"))).Single();
        var b = (await _service.GetSnapshotsAsync(Hex("modelB"))).Single();
        Assert.Equal((30, 0, ModelHealthState.Decayed), (a.MaturedCount, a.CorrectCount, a.State));
        Assert.Single(alerts); Assert.Equal(Hex("modelA"), alerts[0].Snapshot.ModelId); Assert.Equal(alerts[0].AlertId, a.LastAlertId);
        Assert.Equal((30, 30, ModelHealthState.Healthy, "carried-alert"), (b.MaturedCount, b.CorrectCount, b.State, b.LastAlertId));
        Assert.Single(Directory.GetFiles(Path.Combine(_monitor, "alerts"), "*.json"));

        await _service.RefreshAsync("A", TimeframeType.Daily, Observe(DailyBars(10, lastClose: 94m))); // repeated evidence: no second alert
        Assert.Single(alerts);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
