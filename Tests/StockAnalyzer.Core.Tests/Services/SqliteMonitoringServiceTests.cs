#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// FP05: the transactional SQLite backend keeps the JSON contract (same reducer, same labels, window, health and alert
/// rules) while committing outcomes, health, the transition event and its outbox row atomically.
/// </summary>
public sealed class SqliteMonitoringServiceTests : IDisposable
{
    private readonly SqliteMonitoringTestKit _kit = new();
    public void Dispose() => _kit.Dispose();

    [Fact]
    public void Settings_RejectUnrepresentableOrNonWaitingValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PredictionMonitoringSettingsValidator.Validate(SqliteMonitoringTestKit.Settings(busySeconds: 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => PredictionMonitoringSettingsValidator.Validate(SqliteMonitoringTestKit.Settings(busySeconds: -1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => PredictionMonitoringSettingsValidator.Validate(SqliteMonitoringTestKit.Settings(busySeconds: int.MaxValue))); // > Int32 milliseconds
        Assert.Throws<ArgumentOutOfRangeException>(() => PredictionMonitoringSettingsValidator.Validate(new PredictionMonitoringSettings { OutboxPollIntervalSeconds = 0 }));
        PredictionMonitoringSettingsValidator.Validate(new PredictionMonitoringSettings());
        Assert.Equal((PredictionMonitoringBackendKind.Json, 5, 60), (new PredictionMonitoringSettings().Backend,
            new PredictionMonitoringSettings().BusyTimeoutSeconds, new PredictionMonitoringSettings().OutboxPollIntervalSeconds));
    }

    [Fact]
    public void OperationalDefaults_AreTheFormerConstants()
    {
        var defaults = new PredictionMonitoringSettings();
        Assert.Equal((TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(250), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(60)),
            (defaults.SchedulerPollInterval, defaults.LeaseRetryInterval, defaults.HealthEvaluationInterval, defaults.AttestedWorkingCopyRetention));
    }

    [Fact]
    public async Task Store_EnforcesDurablePragmas_AndRejectsUninitializedAndFutureFormats()
    {
        var store = _kit.Store();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.OpenAsync(requireInitialized: true, CancellationToken.None)); // empty file
        await _kit.CreateServiceAsync();
        Assert.Equal(("wal", 2L, 1L, 1L), (
            await _kit.QueryAsync("PRAGMA journal_mode;", r => r.GetString(0)),
            await _kit.QueryAsync("PRAGMA synchronous;", r => r.GetInt64(0)),
            await _kit.QueryAsync("PRAGMA foreign_keys;", r => r.GetInt64(0)),
            await _kit.QueryAsync("PRAGMA user_version;", r => r.GetInt64(0))));
        await using (var connection = await store.OpenAsync(requireInitialized: false, CancellationToken.None))
            await SqliteMonitoringStore.ExecAsync(connection, null, "PRAGMA user_version = 2;", CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.OpenAsync(requireInitialized: true, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.IsImportCompleteAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RecordReplayAndCorrections_KeepOneVotePerIdentity_AndEveryRevision()
    {
        await _kit.EnsureRegistryAsync(); var log = await _kit.CreateServiceAsync();
        var bars = SqliteMonitoringTestKit.BarsEnding(SqliteMonitoringTestKit.Sept28); var inputs = bars.Take(195).ToArray();
        var id = await log.RecordAsync(_kit.Result!, "X", TimeframeType.Daily, inputs);
        Assert.Equal(id, await log.RecordAsync(_kit.Result!, "X", TimeframeType.Daily, inputs)); // identical replay
        Assert.Equal(1, await _kit.CountAsync("Predictions"));
        await log.RefreshAsync("X", TimeframeType.Daily, _kit.Observe(bars));
        Assert.Equal("Up", (await log.ReadAuditAsync(id!))!.Outcomes[^1].ActualLabel);
        _kit.Clock.Now = _kit.Clock.Now.AddMinutes(1);
        await log.RefreshAsync("X", TimeframeType.Daily, _kit.Observe(SqliteMonitoringTestKit.BarsEnding(SqliteMonitoringTestKit.Sept28, 94m)));
        var audit = (await log.ReadAuditAsync(id!))!;
        Assert.Equal((3, "Down", "Up"), (audit.Outcomes.Length, audit.Outcomes[^1].ActualLabel, audit.Prediction.PredictedLabel));
        Assert.NotEqual(audit.Outcomes[1].SourceRevision, audit.Outcomes[2].SourceRevision);
        Assert.Equal(1, (await log.GetSnapshotsAsync(_kit.ModelId)).Single().MaturedCount);
        Assert.Null(await log.ReadAuditAsync(new string('f', 64)));
        await Assert.ThrowsAsync<ArgumentException>(() => log.ReadAuditAsync("not-a-hash"));
    }

    [Fact]
    public async Task PendingStaysPending_UnavailableIsTerminal_AndNoRecordIsFabricated()
    {
        await _kit.EnsureRegistryAsync(); var log = await _kit.CreateServiceAsync();
        var bars = SqliteMonitoringTestKit.BarsEnding(SqliteMonitoringTestKit.Sept28);
        var id = await log.RecordAsync(_kit.Result!, "X", TimeframeType.Daily, bars.Take(195).ToArray());
        await log.RefreshAsync("X", TimeframeType.Daily, _kit.Observe(bars.Take(199).ToImmutableArrayCompat()));
        await log.RefreshAsync("X", TimeframeType.Daily, _kit.Observe(bars, final: false));
        _kit.Clock.Now = _kit.Clock.Now.AddDays(100);
        await log.RefreshAsync("X", TimeframeType.Daily, _kit.Observe(bars, final: false));
        Assert.Equal(PredictionOutcomeState.Pending, (await log.ReadAuditAsync(id!))!.Outcomes[^1].State);
        await log.MarkUnavailableAsync(id!, "provider explicitly deleted the source");
        await log.MarkUnavailableAsync(id!, "again"); // already terminal: no second revision
        Assert.Equal(2, (await log.ReadAuditAsync(id!))!.Outcomes.Length);
        await log.RefreshAsync("X", TimeframeType.Daily, _kit.Observe(bars));
        Assert.Equal((PredictionOutcomeState.Unavailable, 0), ((await log.ReadAuditAsync(id!))!.Outcomes[^1].State,
            (await log.GetSnapshotsAsync(_kit.ModelId)).Single().MaturedCount));
        await Assert.ThrowsAsync<InvalidDataException>(() => log.MarkUnavailableAsync(new string('e', 64), "missing"));
        await Assert.ThrowsAsync<ArgumentException>(() => log.MarkUnavailableAsync(id!, " "));
    }

    [Fact]
    public async Task HealthTransitions_FollowTheSameRulesAsJson_WithDeterministicAlertIdsAndRevisions()
    {
        await _kit.EnsureRegistryAsync(); var log = await _kit.CreateServiceAsync(); int alerts = 0, changed = 0;
        for (int i = 0; i < 10; i++) await _kit.VoteAsync(log, "O" + i, SqliteMonitoringTestKit.Sept2, true);
        for (int i = 0; i < 14; i++) await _kit.VoteAsync(log, "C" + i, SqliteMonitoringTestKit.Sept28, true);
        for (int i = 0; i < 16; i++) await _kit.VoteAsync(log, "W" + i, SqliteMonitoringTestKit.Sept28, false);
        log.Decayed += (_, _) => alerts++; log.Changed += (_, _) => changed++;
        var model = _kit.ModelId;

        var healthy = Assert.Single(await log.EvaluateCurrentAsync(model));
        Assert.Equal((40, 24, ModelHealthState.Healthy), (healthy.MaturedCount, healthy.CorrectCount, healthy.State));
        var healthyRow = Assert.Single(await _kit.HealthAsync());

        _kit.Clock.Now = new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero); // the 09-02 votes leave the window
        Assert.Equal(40, (await log.GetSnapshotsAsync(model)).Single().MaturedCount);        // cached read is not time-aware
        int before = changed;
        var decayed = Assert.Single(await log.EvaluateCurrentAsync(model));
        Assert.Equal((30, 14, (decimal)14 / 30, ModelHealthState.Decayed), (decayed.MaturedCount, decayed.CorrectCount, decayed.Accuracy, decayed.State));
        var decayedRow = Assert.Single(await _kit.HealthAsync());
        Assert.Equal(healthyRow.Revision + 1, decayedRow.Revision);
        Assert.Equal(SqlitePredictionLogService.AlertIdentity(decayed.ModelId, decayed.TargetContractHash, decayedRow.Revision), decayed.LastAlertId);
        Assert.Equal((1, before + 1, 1L), (alerts, changed, await _kit.CountAsync("Alerts")));
        Assert.Equal((0L, decayedRow.Revision), (await _kit.QueryAsync("SELECT State FROM Outbox;", r => r.GetInt64(0)),
            await _kit.QueryAsync("SELECT HealthRevision FROM Alerts;", r => r.GetInt64(0))));

        Assert.Equal(decayed, Assert.Single(await log.EvaluateCurrentAsync(model)));            // repeat at the same instant
        _kit.Clock.Now = _kit.Clock.Now.AddMinutes(1);
        var later = Assert.Single(await log.EvaluateCurrentAsync(model));                        // only the instant moved
        Assert.Equal(decayed with { EvaluatedUtc = later.EvaluatedUtc }, later);
        Assert.Equal((decayedRow.Revision, 1, before + 1), ((await _kit.HealthAsync()).Single().Revision, alerts, changed));

        // Same N and K, different represented votes: the revision moves, no alert fires.
        await _kit.ObserveVoteAsync(log, "C0", SqliteMonitoringTestKit.Sept28, false); // K=13, still Decayed
        await _kit.ObserveVoteAsync(log, "W0", SqliteMonitoringTestKit.Sept28, true);  // K=14 again
        var flipped = Assert.Single(await log.EvaluateCurrentAsync(model));
        Assert.Equal((30, 14, ModelHealthState.Decayed, 1), (flipped.MaturedCount, flipped.CorrectCount, flipped.State, alerts));
        Assert.Equal(decayedRow.Revision + 2, (await _kit.HealthAsync()).Single().Revision); // one step per changed evaluation (K=13, then K=14)

        // Recovery re-arms; the next Healthy -> Decayed edge mints exactly one new event with a new id.
        await _kit.ObserveVoteAsync(log, "W1", SqliteMonitoringTestKit.Sept28, true);   // K=15 Healthy
        Assert.Equal(ModelHealthState.Healthy, Assert.Single(await log.EvaluateCurrentAsync(model)).State);
        await _kit.ObserveVoteAsync(log, "C1", SqliteMonitoringTestKit.Sept28, false);  // K=14 Decayed again
        var second = Assert.Single(await log.EvaluateCurrentAsync(model));
        Assert.Equal((ModelHealthState.Decayed, 2L, 2L), (second.State, await _kit.CountAsync("Alerts"), await _kit.CountAsync("Outbox")));
        Assert.NotEqual(decayed.LastAlertId, second.LastAlertId);
    }

    [Fact]
    public async Task InitialDecayed_RaisesNoAlert_AndEmptyStoreEvaluatesToNothing()
    {
        await _kit.EnsureRegistryAsync(); var log = await _kit.CreateServiceAsync(); int alerts = 0; log.Decayed += (_, _) => alerts++;
        Assert.Empty(await log.EvaluateCurrentAsync());
        for (int i = 0; i < 14; i++) await _kit.VoteAsync(log, "C" + i, SqliteMonitoringTestKit.Sept28, true);
        for (int i = 0; i < 16; i++) await _kit.VoteAsync(log, "W" + i, SqliteMonitoringTestKit.Sept28, false);
        var group = Assert.Single(await log.EvaluateCurrentAsync(_kit.ModelId));
        Assert.Equal((30, 14, ModelHealthState.Decayed, 0), (group.MaturedCount, group.CorrectCount, group.State, alerts));
        Assert.Null(group.LastAlertId); Assert.Equal(0, await _kit.CountAsync("Alerts"));
        await Assert.ThrowsAsync<ArgumentException>(() => log.EvaluateCurrentAsync("not-a-sha256"));
    }

    [Fact]
    public async Task FailureInsideTheTransaction_RollsBackEveryOutcomeAndPublishesNothing()
    {
        await _kit.EnsureRegistryAsync(); var log = await _kit.CreateServiceAsync(); int events = 0;
        var bars = SqliteMonitoringTestKit.BarsEnding(SqliteMonitoringTestKit.Sept28);
        var id = await log.RecordAsync(_kit.Result!, "X", TimeframeType.Daily, bars.Take(195).ToArray());
        await log.EvaluateCurrentAsync(); // creates the health row
        await using (var connection = await _kit.Store().OpenAsync(true, CancellationToken.None)) // an unusable predecessor: State disagrees with f(N,K)
            await SqliteMonitoringStore.ExecAsync(connection, null, "UPDATE Health SET State = 1;", CancellationToken.None);
        log.Changed += (_, _) => events++; log.Decayed += (_, _) => events++;
        var revisionBefore = (await ReadRevisionAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => log.RefreshAsync("X", TimeframeType.Daily, _kit.Observe(bars)));
        Assert.Equal((1L, 0), (await _kit.CountAsync("Outcomes"), events)); // the new Matured revision was NOT committed
        Assert.Equal(PredictionOutcomeState.Pending, (await log.ReadAuditAsync(id!))!.Outcomes[^1].State);
        Assert.Equal(revisionBefore, await ReadRevisionAsync());
    }

    private Task<long> ReadRevisionAsync() => _kit.QueryAsync("SELECT HealthRevision FROM Health;", r => r.GetInt64(0));

    [Fact]
    public async Task UncommittedTransaction_IsInvisible_AsAfterACrashBeforeCommit()
    {
        await _kit.EnsureRegistryAsync(); var log = await _kit.CreateServiceAsync(); var store = _kit.Store();
        var prediction = new MonitoredPrediction(new string('1', 64), new string('2', 64), new string('3', 64), "Z", TimeframeType.Daily,
            _kit.Clock.Now.AddDays(-9), 5, .005m, "Up", new string('4', 64), new string('5', 64), new string('6', 64), _kit.Clock.Now.AddDays(-9));
        await using (var connection = await store.OpenAsync(true, CancellationToken.None))
        {
            using var transaction = store.BeginWriter(connection, CancellationToken.None);
            await SqliteMonitoringStore.InsertPredictionAsync(connection, transaction, prediction, CancellationToken.None);
            await SqliteMonitoringStore.InsertOutcomeAsync(connection, transaction, prediction.PredictionId,
                PredictionMonitoringReducer.InitialOutcome(prediction), CancellationToken.None);
        } // connection disposed without Commit: the process "died" before commit
        Assert.Equal(0, await _kit.CountAsync("Predictions"));
        Assert.Null(await log.ReadAuditAsync(prediction.PredictionId));
        await log.EvaluateCurrentAsync(); // the store keeps working
    }

    [Fact]
    public async Task BusyWriter_SurfacesATypedFault_AndCommitsNothing()
    {
        await _kit.EnsureRegistryAsync(); var log = await _kit.CreateServiceAsync(busySeconds: 1); var store = _kit.Store(busySeconds: 1);
        var bars = SqliteMonitoringTestKit.BarsEnding(SqliteMonitoringTestKit.Sept28);
        await using (var holder = await store.OpenAsync(true, CancellationToken.None))
        {
            using var held = store.BeginWriter(holder, CancellationToken.None); // another process holds the writer lock
            await Assert.ThrowsAsync<PredictionMonitoringBusyException>(() => log.RecordAsync(_kit.Result!, "X", TimeframeType.Daily, bars.Take(195).ToArray()));
            await Assert.ThrowsAsync<PredictionMonitoringBusyException>(() => log.EvaluateCurrentAsync());
        }
        Assert.Equal(0, await _kit.CountAsync("Predictions"));
        Assert.NotNull(await log.RecordAsync(_kit.Result!, "X", TimeframeType.Daily, bars.Take(195).ToArray())); // lock released: works again
    }

    [Fact]
    public async Task BusyWriter_IsLoggedAsAWarning_BeforeTheTypedFaultIsThrown()
    {
        var logger = new CapturingLogger();
        await _kit.EnsureRegistryAsync(); var log = await _kit.CreateServiceAsync(busySeconds: 1, logger: logger); var store = _kit.Store(busySeconds: 1);
        var bars = SqliteMonitoringTestKit.BarsEnding(SqliteMonitoringTestKit.Sept28);
        await using var holder = await store.OpenAsync(true, CancellationToken.None);
        using var held = store.BeginWriter(holder, CancellationToken.None);
        await Assert.ThrowsAsync<PredictionMonitoringBusyException>(() => log.RecordAsync(_kit.Result!, "X", TimeframeType.Daily, bars.Take(195).ToArray()));
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, entry.Level);
        Assert.Contains("locked", entry.Message);
    }

    [Fact]
    public async Task IndependentWriters_OnOneDatabase_SerializeWithoutLosingAVote()
    {
        await _kit.EnsureRegistryAsync();
        var first = await _kit.CreateServiceAsync(); var second = await _kit.CreateServiceAsync(); // separate gates and connections
        async Task Work(SqlitePredictionLogService log, string prefix)
        {
            for (int i = 0; i < 10; i++) await _kit.VoteAsync(log, prefix + i, SqliteMonitoringTestKit.Sept28, true);
        }
        await Task.WhenAll(Task.Run(() => Work(first, "A")), Task.Run(() => Work(second, "B")));
        var group = Assert.Single(await first.EvaluateCurrentAsync(_kit.ModelId));
        Assert.Equal((20, 20, 20L), (group.MaturedCount, group.CorrectCount, await _kit.CountAsync("Predictions")));
        Assert.Equal(group, (await second.GetSnapshotsAsync(_kit.ModelId)).Single());
    }

    [Fact]
    public async Task CancelledOperations_ThrowWithoutWriting()
    {
        await _kit.EnsureRegistryAsync(); var log = await _kit.CreateServiceAsync();
        var bars = SqliteMonitoringTestKit.BarsEnding(SqliteMonitoringTestKit.Sept28);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => log.RecordAsync(_kit.Result!, "X", TimeframeType.Daily, bars.Take(195).ToArray(), new CancellationToken(true)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => log.EvaluateCurrentAsync(null, new CancellationToken(true)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => log.RefreshAsync("X", TimeframeType.Daily, _kit.Observe(bars), new CancellationToken(true)));
        Assert.Equal(0, await _kit.CountAsync("Predictions"));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("th-TH")]
    public async Task DecimalsAndInstants_RoundTripExactly_UnderAnyCulture(string culture)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            await _kit.EnsureRegistryAsync(); var log = await _kit.CreateServiceAsync();
            await _kit.VoteAsync(log, "A", SqliteMonitoringTestKit.Sept28, true); await _kit.VoteAsync(log, "B", SqliteMonitoringTestKit.Sept28, true);
            await _kit.VoteAsync(log, "C", SqliteMonitoringTestKit.Sept28, false);
            var group = Assert.Single(await log.EvaluateCurrentAsync(_kit.ModelId));
            var expected = 2m / 3;
            Assert.Equal(expected, group.Accuracy);
            Assert.Equal(expected.ToString(CultureInfo.InvariantCulture), await _kit.QueryAsync("SELECT AccuracyText FROM Health;", r => r.GetString(0)));
            Assert.Equal(_kit.Clock.Now.UtcTicks, await _kit.QueryAsync("SELECT EvaluatedTicks FROM Health;", r => r.GetInt64(0)));
            var stored = await _kit.QueryAsync("SELECT PredictionJson FROM Predictions LIMIT 1;", r => r.GetString(0));
            Assert.Contains("0.005", stored); // the neutral threshold is stored as an exact decimal
            Assert.Equal(group, (await log.GetSnapshotsAsync(_kit.ModelId)).Single());
        }
        finally { CultureInfo.CurrentCulture = original; }
    }
}

internal static class ImmutableArrayCompat
{
    public static System.Collections.Immutable.ImmutableArray<T> ToImmutableArrayCompat<T>(this IEnumerable<T> source) =>
        System.Collections.Immutable.ImmutableArray.CreateRange(source);
}
