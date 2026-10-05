#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>FP05: explicit quiescent import, backend selection by marker, version rejection and no JSON writer after the cutover.</summary>
public sealed class PredictionMonitoringMigrationTests : IDisposable
{
    private readonly SqliteMonitoringTestKit _kit = new();
    public void Dispose() => _kit.Dispose();

    /// <summary>Runs a real JSON-backend history: a Healthy group that decays on 2026-10-04 (one GUID alert), plus a forecast that is still Pending.</summary>
    private async Task<(PredictionLogService Json, ModelMonitoringSnapshot Final)> SeedJsonAsync()
    {
        await _kit.EnsureRegistryAsync();
        var json = new PredictionLogService(_kit.Registry!, _kit.Clock, _kit.MonitorRoot);
        for (int i = 0; i < 10; i++) await _kit.VoteAsync(json, "O" + i, SqliteMonitoringTestKit.Sept2, true);
        for (int i = 0; i < 14; i++) await _kit.VoteAsync(json, "C" + i, SqliteMonitoringTestKit.Sept28, true);
        for (int i = 0; i < 16; i++) await _kit.VoteAsync(json, "W" + i, SqliteMonitoringTestKit.Sept28, false);
        await json.RecordAsync(_kit.Result!, "PENDING", TimeframeType.Daily, SqliteMonitoringTestKit.BarsEnding(SqliteMonitoringTestKit.Sept28).Take(195).ToArray());
        _kit.Clock.Now = new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
        var final = (await json.EvaluateCurrentAsync(_kit.ModelId)).Single();
        Assert.Equal(ModelHealthState.Decayed, final.State);
        return (json, final);
    }

    private Dictionary<string, string> HashSources() => Directory.EnumerateFiles(_kit.MonitorRoot, "*.json", SearchOption.AllDirectories)
        .ToDictionary(p => Path.GetRelativePath(_kit.MonitorRoot, p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));

    private static string Serialize(PredictionAuditRecord? record) => JsonSerializer.Serialize(record, TrainingConfigJson.Options);

    [Fact]
    public async Task Import_PreservesIdsRevisionsHealthAndAlerts_ValidatesAndNeverReimportsOrReplays()
    {
        var (json, final) = await SeedJsonAsync();
        var ids = Directory.GetFiles(Path.Combine(_kit.MonitorRoot, "predictions"), "*.json").Select(Path.GetFileNameWithoutExtension).ToArray();
        int outcomes = 0; foreach (var id in ids) outcomes += (await json.ReadAuditAsync(id!))!.Outcomes.Length;
        var sourcesBefore = HashSources(); var store = _kit.Store();

        var summary = await PredictionMonitoringMigrator.ImportAsync(store, _kit.Clock.Now, CancellationToken.None);
        Assert.Equal((41, outcomes, 1, 1), (summary!.Predictions, summary.Outcomes, summary.Health, summary.Alerts));
        Assert.Equal(sourcesBefore, HashSources()); // source files are never modified, renamed or deleted
        Assert.True(await store.IsImportCompleteAsync(CancellationToken.None));
        Assert.Equal(64, summary.ManifestDigest.Length);

        var sqlite = new SqlitePredictionLogService(_kit.Registry!, store, TimeSpan.FromMinutes(1), _kit.Clock);
        foreach (var id in ids) Assert.Equal(Serialize(await json.ReadAuditAsync(id!)), Serialize(await sqlite.ReadAuditAsync(id!))); // identity, labels, revision numbers
        Assert.Equal(final, (await sqlite.GetSnapshotsAsync(_kit.ModelId)).Single());
        var health = Assert.Single(await _kit.HealthAsync());
        Assert.Equal(1L, health.Revision);

        // The historical GUID alert keeps its id/payload, has no revision, and is Presented: it is never replayed.
        Assert.Equal((final.LastAlertId, true, 1L, 0L), (
            await _kit.QueryAsync("SELECT AlertId FROM Alerts;", r => r.GetString(0)),
            await _kit.QueryAsync("SELECT HealthRevision IS NULL FROM Alerts;", r => r.GetInt64(0) == 1),
            await _kit.QueryAsync("SELECT State FROM Outbox;", r => r.GetInt64(0)),
            await _kit.QueryAsync("SELECT AttemptCount FROM Outbox;", r => r.GetInt64(0))));
        Assert.Equal(0, await sqlite.Outbox.DispatchPendingAsync((_, _) => throw new InvalidOperationException("history must not be replayed")));

        // A second start with the complete marker neither re-imports nor re-votes; evaluating changes nothing semantically.
        var completeValue = await _kit.QueryAsync("SELECT value FROM StoreMetadata WHERE key = 'ImportComplete';", r => r.GetString(0));
        Assert.Null(await PredictionMonitoringMigrator.ImportAsync(store, _kit.Clock.Now.AddDays(1), CancellationToken.None));
        Assert.Equal(completeValue, await _kit.QueryAsync("SELECT value FROM StoreMetadata WHERE key = 'ImportComplete';", r => r.GetString(0)));
        int alerts = 0; sqlite.Decayed += (_, _) => alerts++;
        Assert.Equal(final, (await sqlite.EvaluateCurrentAsync(_kit.ModelId)).Single());
        Assert.Equal((0, 1L, 1L, 41L), (alerts, await _kit.CountAsync("Alerts"), (await _kit.HealthAsync()).Single().Revision, await _kit.CountAsync("Predictions")));
    }

    [Fact]
    public async Task CorruptOrIrreproducibleSources_AbortTheWholeImport_BeforeAnyDatabaseExists()
    {
        await SeedJsonAsync(); var store = _kit.Store();
        var predictions = Directory.GetFiles(Path.Combine(_kit.MonitorRoot, "predictions"), "*.json");
        var good = File.ReadAllBytes(predictions[0]);
        File.WriteAllText(predictions[0], "{ not json");
        await Assert.ThrowsAsync<InvalidDataException>(() => PredictionMonitoringMigrator.ImportAsync(store, _kit.Clock.Now, CancellationToken.None));
        Assert.Equal((false, false), (store.DatabaseExists, await store.IsImportCompleteAsync(CancellationToken.None)));
        File.WriteAllBytes(predictions[0], good);

        var healthPath = Path.Combine(_kit.MonitorRoot, "health.json");
        var saved = JsonSerializer.Deserialize<List<ModelMonitoringSnapshot>>(File.ReadAllText(healthPath), TrainingConfigJson.Options)!;
        var tampered = saved[0] with { MaturedCount = 41, CorrectCount = 24, Accuracy = 24m / 41, State = ModelHealthState.Healthy }; // internally consistent, not reproducible
        File.WriteAllText(healthPath, JsonSerializer.Serialize(new[] { tampered }, TrainingConfigJson.Options));
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => PredictionMonitoringMigrator.ImportAsync(store, _kit.Clock.Now, CancellationToken.None));
        Assert.Contains("cannot be reproduced", ex.Message);
        Assert.False(store.DatabaseExists);

        File.WriteAllText(healthPath, JsonSerializer.Serialize(new[] { saved[0] with { State = ModelHealthState.Healthy } }, TrainingConfigJson.Options)); // State != f(N,K)
        await Assert.ThrowsAsync<InvalidDataException>(() => PredictionMonitoringMigrator.ImportAsync(store, _kit.Clock.Now, CancellationToken.None));
        Assert.False(store.DatabaseExists);
    }

    [Fact]
    public async Task FailureInsideTheImportTransaction_LeavesNoMarkerNoSchemaAndNoRows()
    {
        await SeedJsonAsync(); var store = _kit.Store();
        await using (var connection = await store.OpenAsync(requireInitialized: false, CancellationToken.None)) // a foreign table that makes the inserts fail
            await SqliteMonitoringStore.ExecAsync(connection, null, "CREATE TABLE Predictions(x TEXT);", CancellationToken.None);
        await Assert.ThrowsAnyAsync<Exception>(() => PredictionMonitoringMigrator.ImportAsync(store, _kit.Clock.Now, CancellationToken.None));
        Assert.False(await store.IsImportCompleteAsync(CancellationToken.None));
        await using var check = await store.OpenAsync(requireInitialized: false, CancellationToken.None);
        await using var command = check.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM sqlite_master WHERE name = 'StoreMetadata'), (SELECT COUNT(*) FROM Predictions), (SELECT * FROM pragma_user_version);";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        Assert.Equal((0L, 0L, 0L), (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)));
    }

    [Fact]
    public async Task UnknownMarkerVersionAndFutureFormat_FailInsteadOfFallingBack()
    {
        var log = await _kit.CreateServiceAsync(); var store = _kit.Store();
        Assert.True(await store.IsImportCompleteAsync(CancellationToken.None));
        await using (var connection = await store.OpenAsync(true, CancellationToken.None))
        {
            using var transaction = store.BeginWriter(connection, CancellationToken.None);
            await SqliteMonitoringStore.SetMetadataAsync(connection, transaction, SqliteMonitoringStore.VersionKey, "9", CancellationToken.None);
            store.Commit(transaction, CancellationToken.None);
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => store.IsImportCompleteAsync(CancellationToken.None));
        var factory = new PredictionMonitoringBackendFactory(_kit.RegistryOrMock, Options.Create(SqliteMonitoringTestKit.Settings(backend: PredictionMonitoringBackendKind.Json)),
            null, _kit.Clock, _kit.MonitorRoot);
        Assert.Throws<InvalidDataException>(() => factory.UsesSqlite);
        Assert.NotNull(log);
    }

    [Fact]
    public void Factory_DefaultsToTheLegacyJsonBackend_AndCreatesNoDatabase()
    {
        var factory = new PredictionMonitoringBackendFactory(_kit.RegistryOrMock, Options.Create(new PredictionMonitoringSettings()), null, _kit.Clock, _kit.MonitorRoot);
        Assert.False(factory.UsesSqlite);
        Assert.IsType<PredictionLogService>(factory.Log);
        Assert.Same(factory.Log, factory.Health);
        Assert.False(factory.Outbox.IsActive); Assert.IsType<NoMonitoringNotificationOutbox>(factory.Outbox);
        Assert.False(File.Exists(Path.Combine(_kit.MonitorRoot, "monitoring.db")));
        var withoutOptions = new PredictionMonitoringBackendFactory(_kit.RegistryOrMock, null, null, _kit.Clock, _kit.MonitorRoot);
        Assert.False(withoutOptions.UsesSqlite);
    }

    [Fact]
    public async Task SqliteSetting_ImportsOnce_RestartsKeepTheMarker_AndTheJsonFilesStayUntouched()
    {
        var (json, final) = await SeedJsonAsync(); var sourcesBefore = HashSources();
        PredictionMonitoringBackendFactory Create(PredictionMonitoringBackendKind kind) => new(_kit.Registry!,
            Options.Create(SqliteMonitoringTestKit.Settings(backend: kind)), null, _kit.Clock, _kit.MonitorRoot);

        var first = Create(PredictionMonitoringBackendKind.Sqlite);
        Assert.True(first.UsesSqlite); Assert.True(first.Outbox.IsActive); Assert.Same(first.Log, first.Health);
        Assert.Equal(final, (await first.Log.GetSnapshotsAsync(_kit.ModelId)).Single());
        var imported = await _kit.QueryAsync("SELECT value FROM StoreMetadata WHERE key = 'ImportComplete';", r => r.GetString(0));

        // A new forecast after the cutover is written to SQLite only: no legacy JSON writer remains active.
        var id = await first.Log.RecordAsync(_kit.Result!, "AFTER", TimeframeType.Daily, SqliteMonitoringTestKit.BarsEnding(SqliteMonitoringTestKit.Sept28).Take(195).ToArray());
        Assert.NotNull(await first.Log.ReadAuditAsync(id!));
        Assert.Equal(sourcesBefore, HashSources());

        var restarted = Create(PredictionMonitoringBackendKind.Sqlite);       // restart: marker present, no second import
        var stillJson = Create(PredictionMonitoringBackendKind.Json);         // the marker wins over the setting
        Assert.True(restarted.UsesSqlite); Assert.True(stillJson.UsesSqlite);
        Assert.Equal(imported, await _kit.QueryAsync("SELECT value FROM StoreMetadata WHERE key = 'ImportComplete';", r => r.GetString(0)));
        Assert.Equal(42L, await _kit.CountAsync("Predictions"));
        Assert.NotNull(await stillJson.Log.ReadAuditAsync(id!));
        Assert.NotNull(json);
    }

    [Fact]
    public async Task FailedImportAtStartup_ThrowsEveryTime_AndLeavesTheJsonBackendUntouched()
    {
        await SeedJsonAsync();
        File.WriteAllText(Directory.GetFiles(Path.Combine(_kit.MonitorRoot, "predictions"), "*.json")[0], "{ not json");
        var sourcesBefore = HashSources();
        var factory = new PredictionMonitoringBackendFactory(_kit.Registry!, Options.Create(SqliteMonitoringTestKit.Settings()), null, _kit.Clock, _kit.MonitorRoot);
        Assert.Throws<InvalidDataException>(() => factory.Log);
        Assert.Throws<InvalidDataException>(() => factory.Outbox); // the same explicit outcome for every consumer, no silent JSON fallback
        Assert.Equal(sourcesBefore, HashSources());
        Assert.False(File.Exists(Path.Combine(_kit.MonitorRoot, "monitoring.db")));
    }

    private PredictionMonitoringBackendFactory SqliteFactory() => new(_kit.Registry!, Options.Create(SqliteMonitoringTestKit.Settings()), null, _kit.Clock, _kit.MonitorRoot);

    [Fact]
    public async Task InitializeAsync_ReturnsAtOnceWhileTheImportWaits_ThenCompletesTheSameImport_AndTheAccessorsReuseIt()
    {
        var (_, final) = await SeedJsonAsync();
        var store = _kit.Store();
        await using var holder = await store.OpenAsync(requireInitialized: false, CancellationToken.None);
        var held = store.BeginWriter(holder, CancellationToken.None);                       // the import cannot start its writer transaction
        var factory = SqliteFactory();

        var init = factory.InitializeAsync();
        Assert.False(init.IsCompleted);                                                      // the caller was not blocked by the import
        held.Dispose();                                                                      // the lock is released: the import proceeds
        await init;

        Assert.True(factory.UsesSqlite);
        Assert.Equal(final, (await factory.Log.GetSnapshotsAsync(_kit.ModelId)).Single());
        await factory.InitializeAsync();                                                     // a repeat shares the finished resolution
        Assert.Equal(1L, await _kit.QueryAsync("SELECT COUNT(*) FROM StoreMetadata WHERE key = 'ImportComplete';", r => r.GetInt64(0)));
    }

    [Fact]
    public async Task InitializeAsync_FailedImport_FaultsEveryCall_AndMatchesTheAccessors_WithoutFallback()
    {
        await SeedJsonAsync();
        File.WriteAllText(Directory.GetFiles(Path.Combine(_kit.MonitorRoot, "predictions"), "*.json")[0], "{ not json");
        var factory = SqliteFactory();
        await Assert.ThrowsAsync<InvalidDataException>(() => factory.InitializeAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => factory.InitializeAsync());
        Assert.Throws<InvalidDataException>(() => factory.UsesSqlite);
        Assert.False(File.Exists(Path.Combine(_kit.MonitorRoot, "monitoring.db")));
    }

    [Fact]
    public async Task InitializeAsync_CancellationStopsOnlyTheWait_TheStartedImportStillFinishes()
    {
        await SeedJsonAsync();
        var store = _kit.Store();
        await using var holder = await store.OpenAsync(requireInitialized: false, CancellationToken.None);
        var held = store.BeginWriter(holder, CancellationToken.None);
        var factory = SqliteFactory();
        using var cancellation = new CancellationTokenSource();
        var waiting = factory.InitializeAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        held.Dispose();
        await factory.InitializeAsync();                                                     // the import was not abandoned
        Assert.True(factory.UsesSqlite);
    }

    [Fact]
    public async Task NotesDatabaseAndRegistryPointer_AreNeverTouched()
    {
        await SeedJsonAsync();
        var pointer = Directory.EnumerateFiles(Path.Combine(_kit.Root, "registry"), "*.json", SearchOption.AllDirectories).ToDictionary(p => p, p => File.ReadAllBytes(p));
        var factory = new PredictionMonitoringBackendFactory(_kit.Registry!, Options.Create(SqliteMonitoringTestKit.Settings()), null, _kit.Clock, _kit.MonitorRoot);
        Assert.True(factory.UsesSqlite);
        Assert.Equal(pointer.Keys, Directory.EnumerateFiles(Path.Combine(_kit.Root, "registry"), "*.json", SearchOption.AllDirectories));
        foreach (var (path, bytes) in pointer) Assert.Equal(bytes, File.ReadAllBytes(path)); // candidate/active registry state unchanged
        Assert.Empty(Directory.EnumerateFiles(_kit.Root, "notes.db", SearchOption.AllDirectories));
    }
}
