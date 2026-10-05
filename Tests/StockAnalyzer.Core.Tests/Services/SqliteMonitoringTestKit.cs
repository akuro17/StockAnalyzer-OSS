#nullable enable
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.ML.OnnxRuntime;
using Moq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>Shared arrangement of the FP05 tests: an isolated monitoring root, a manual clock, the real registry/ONNX fixture and a SQLite service.</summary>
internal sealed class SqliteMonitoringTestKit : IDisposable
{
    public static readonly DateTime Sept2 = new(2026, 9, 2), Sept28 = new(2026, 9, 28);
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "sa_fp05_" + Guid.NewGuid().ToString("N"));
    public string MonitorRoot => Path.Combine(Root, "monitor");
    public RetrainingSchedulerTests.MutableClock Clock { get; } = new(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
    public ModelGenerationRegistry? Registry { get; private set; }
    public PredictionResult? Result { get; private set; }
    public string ModelId => Result!.ModelId!;

    public static PredictionMonitoringSettings Settings(int busySeconds = 5, PredictionMonitoringBackendKind backend = PredictionMonitoringBackendKind.Sqlite) =>
        new() { Backend = backend, BusyTimeoutSeconds = busySeconds, OutboxPollIntervalSeconds = 60 };

    public SqliteMonitoringStore Store(int busySeconds = 5, Microsoft.Extensions.Logging.ILogger? logger = null) => new(MonitorRoot, Settings(busySeconds), logger);

    /// <summary>Registers the analysis fixture model and builds the "Up" forecast result used by every vote.</summary>
    public async Task EnsureRegistryAsync()
    {
        if (Registry is not null) return;
        Registry = new ModelGenerationRegistry(Path.Combine(Root, "registry"));
        var model = await Registry.RegisterAsync(Path.Combine("Assets", "analysis_fixture.onnx"));
        using var session = new InferenceSession(Path.Combine("Assets", "analysis_fixture.onnx"));
        Result = new("Up", 1, [new("Up", 1)])
        {
            ModelId = model.ModelId, OutputContract = PredictionModelMetadata.ReadOutputContract(session.ModelMetadata.CustomMetadataMap),
        };
    }

    public IModelGenerationRegistry RegistryOrMock => Registry ?? new Mock<IModelGenerationRegistry>().Object;

    /// <summary>A SQLite service on an initialized store (an import of an empty JSON root creates the schema and the marker).</summary>
    public async Task<SqlitePredictionLogService> CreateServiceAsync(int busySeconds = 5, System.Collections.Generic.IReadOnlyList<AttestedSourceEnrollment>? attestedSources = null,
        Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        var store = Store(busySeconds, logger);
        if (!await store.IsImportCompleteAsync(CancellationToken.None))
            await PredictionMonitoringMigrator.ImportAsync(store, Clock.GetUtcNow(), CancellationToken.None);
        return new SqlitePredictionLogService(RegistryOrMock, store, TimeSpan.FromMinutes(1), Clock, attestedSources, logger);
    }

    public static ImmutableArray<CandleData> BarsEnding(DateTime end, decimal future = 106m) => Enumerable.Range(0, 200)
        .Select(i => new CandleData(end.AddDays(i - 199), i == 199 ? future : 100m, i == 199 ? future : 100m,
            i == 199 ? future : 100m, i == 199 ? future : 100m, 100)).ToImmutableArray();

    public MonitoringObservation Observe(ImmutableArray<CandleData> bars, bool final = true) =>
        new(PredictionLogService.SourceRevision(bars), Clock.Now, bars, Enumerable.Repeat(final, bars.Length).ToImmutableArray());

    /// <summary>Records a forecast ("Up") for <paramref name="symbol"/> and observes it as matured on <paramref name="end"/>.</summary>
    public async Task<string> VoteAsync(IPredictionLogService log, string symbol, DateTime end, bool correct)
    {
        var id = await log.RecordAsync(Result!, symbol, TimeframeType.Daily, BarsEnding(end).Take(195).ToArray());
        await ObserveVoteAsync(log, symbol, end, correct);
        return id!;
    }

    public Task ObserveVoteAsync(IPredictionLogService log, string symbol, DateTime end, bool correct) =>
        log.RefreshAsync(symbol, TimeframeType.Daily, Observe(BarsEnding(end, correct ? 106m : 94m)));

    // ---- raw reads of the database under test ------------------------------------------------------------------------

    public async Task<T> QueryAsync<T>(string sql, Func<SqliteDataReader, T> read, int busySeconds = 5)
    {
        await using var connection = await Store(busySeconds).OpenAsync(requireInitialized: true, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return read(reader);
    }

    public Task<long> CountAsync(string table) => QueryAsync($"SELECT COUNT(*) FROM {table};", r => r.GetInt64(0));

    public async Task<System.Collections.Generic.List<HealthRow>> HealthAsync()
    {
        await using var connection = await Store().OpenAsync(requireInitialized: true, CancellationToken.None);
        return await SqliteMonitoringStore.ReadHealthAsync(connection, null, CancellationToken.None);
    }

    public void Dispose()
    {
        Registry?.Dispose();
        SqliteConnection.ClearAllPools();
        TestDirectory.Delete(Root);
    }
}
