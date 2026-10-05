using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Services;

/// <summary>FP06 orchestration: enrollment gate, capability gate, chart/source alignment, real SQLite store, generated producer snapshot.</summary>
public sealed class AttestedMonitoringRefresherTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_fp06_av_" + Guid.NewGuid().ToString("N"));
    private ModelGenerationRegistry? _registry;
    private PredictionResult? _result;

    public void Dispose()
    {
        _registry?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static ImmutableArray<CandleData> Bars(decimal future = 106m) => Enumerable.Range(0, 200)
        .Select(i => new CandleData(new DateTime(2026, 9, 28).AddDays(i - 199), i == 199 ? future : 100m, i == 199 ? future : 100m,
            i == 199 ? future : 100m, i == 199 ? future : 100m, 100)).ToImmutableArray();

    private static AttestedMonitoringSnapshot Snapshot(ImmutableArray<CandleData> bars, string provider = "prov", ulong sequence = 1) => new(provider, "INST",
        TimeframeType.Daily, 0, sequence, PredictionLogService.SourceRevision(bars), Now, bars,
        bars.Select(b => { var start = PredictionLogService.Utc(b.Timestamp); return new BarEvidence(start, start, start.AddDays(1), start.AddDays(1), BarFinality.Final); }).ToImmutableArray(),
        ImmutableArray<BarTombstone>.Empty);

    private async Task<(PredictionMonitoringBackendFactory Backend, string Id)> ArrangeAsync(PredictionMonitoringBackendKind kind, bool enrolled = true)
    {
        var constructor = typeof(ModelGenerationRegistry).GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Single(c => c.GetParameters()[0].ParameterType == typeof(string));
        _registry = (ModelGenerationRegistry)constructor.Invoke([Path.Combine(_root, "registry"), null, null]);
        var model = Path.Combine(TestSolution.Root, "Tests", "StockAnalyzer.Core.Tests", "Assets", "analysis_fixture.onnx");
        var generation = await _registry.RegisterAsync(model);
        using var onnx = new InferenceSession(model);
        _result = new PredictionResult("Up", 1, [new("Up", 1)])
        { ModelId = generation.ModelId, OutputContract = PredictionModelMetadata.ReadOutputContract(onnx.ModelMetadata.CustomMetadataMap) };
        var settings = new PredictionMonitoringSettings { Backend = kind };
        if (enrolled) settings.AttestedSources.Add(new AttestedSourceEnrollment { Symbol = "SYM", Timeframe = TimeframeType.Daily, ProviderId = "prov", InstrumentId = "INST" });
        var backend = new PredictionMonitoringBackendFactory(_registry, Options.Create(settings), clock: new ManualTimeProvider(Now), root: Path.Combine(_root, "monitor"));
        var id = await backend.Log.RecordAsync(_result, "SYM", TimeframeType.Daily, Bars().Take(195).ToArray());
        return (backend, id!);
    }

    private static AttestedMonitoringRefresher Refresher(PredictionMonitoringBackendFactory backend, IDataService data, bool enrolled = true)
    {
        var settings = new PredictionMonitoringSettings();
        if (enrolled) settings.AttestedSources.Add(new AttestedSourceEnrollment { Symbol = "SYM", Timeframe = TimeframeType.Daily, ProviderId = "prov", InstrumentId = "INST" });
        return new AttestedMonitoringRefresher(backend, data, Options.Create(settings));
    }

    private sealed class AttestingSource(AttestedMonitoringSnapshot? snapshot) : IDataService, IAttestedMonitoringDataSource
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame timeFrame, int count = 100) => Task.FromResult<IReadOnlyList<CandleData>>([]);
        public Task<AttestedMonitoringSnapshot?> LoadAttestedObservationAsync(string instrumentId, TimeFrame timeframe, CancellationToken ct = default)
        { Calls++; return Task.FromResult(snapshot); }
    }

    private sealed class PlainSource : IDataService
    {
        public Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame timeFrame, int count = 100) => Task.FromResult<IReadOnlyList<CandleData>>([]);
    }

    private static async Task<PredictionOutcomeState> StateAsync(PredictionMonitoringBackendFactory backend, string id) =>
        (await backend.Log.ReadAuditAsync(id))!.Outcomes[^1].State;

    [Fact]
    public async Task EnrolledAndAligned_AppliesTheSnapshot_AndTheForecastMatures()
    {
        var (backend, id) = await ArrangeAsync(PredictionMonitoringBackendKind.Sqlite);
        var source = new AttestingSource(Snapshot(Bars()));
        Assert.True(await Refresher(backend, source).TryRefreshAsync("SYM", TimeframeType.Daily, Bars().Skip(100).ToArray(), CancellationToken.None));
        Assert.Equal(PredictionOutcomeState.Matured, await StateAsync(backend, id));
    }

    [Fact]
    public async Task NotEnrolled_NeverReadsTheSource()
    {
        var (backend, id) = await ArrangeAsync(PredictionMonitoringBackendKind.Sqlite);
        var source = new AttestingSource(Snapshot(Bars()));
        Assert.False(await Refresher(backend, source, enrolled: false).TryRefreshAsync("SYM", TimeframeType.Daily, Bars(), CancellationToken.None));
        Assert.False(await Refresher(backend, source).TryRefreshAsync("OTHER", TimeframeType.Daily, Bars(), CancellationToken.None));
        Assert.False(await Refresher(backend, source).TryRefreshAsync("SYM", TimeframeType.Weekly, Bars(), CancellationToken.None));
        Assert.Equal(0, source.Calls);
        Assert.Equal(PredictionOutcomeState.Pending, await StateAsync(backend, id));
    }

    [Fact]
    public async Task JsonBackendOrSourceWithoutTheCapabilityOrWithoutAnAttestation_StaysOnTheLegacyPath()
    {
        var (json, jsonId) = await ArrangeAsync(PredictionMonitoringBackendKind.Json);
        Assert.False(await Refresher(json, new AttestingSource(Snapshot(Bars()))).TryRefreshAsync("SYM", TimeframeType.Daily, Bars(), CancellationToken.None));
        Assert.Equal(PredictionOutcomeState.Pending, await StateAsync(json, jsonId));
    }

    [Fact]
    public async Task SourceWithoutCapabilityOrWithoutAnAttestation_ReturnsFalse()
    {
        var (backend, id) = await ArrangeAsync(PredictionMonitoringBackendKind.Sqlite);
        Assert.False(await Refresher(backend, new PlainSource()).TryRefreshAsync("SYM", TimeframeType.Daily, Bars(), CancellationToken.None));
        Assert.False(await Refresher(backend, new AttestingSource(null)).TryRefreshAsync("SYM", TimeframeType.Daily, Bars(), CancellationToken.None));
        Assert.Equal(PredictionOutcomeState.Pending, await StateAsync(backend, id));
    }

    [Fact]
    public async Task ChartBarsThatTheEvidenceDoesNotDescribe_AreNotAttested()
    {
        var (backend, id) = await ArrangeAsync(PredictionMonitoringBackendKind.Sqlite);
        var source = new AttestingSource(Snapshot(Bars()));
        var chart = Bars(future: 107m).Skip(100).ToArray();                                      // the chart saw another close for the last bar
        Assert.False(await Refresher(backend, source).TryRefreshAsync("SYM", TimeframeType.Daily, chart, CancellationToken.None));
        Assert.Equal(PredictionOutcomeState.Pending, await StateAsync(backend, id));
        var extra = Bars().Append(new CandleData(new DateTime(2026, 9, 29), 1m, 1m, 1m, 1m, 1)).Skip(100).ToArray(); // a bar the evidence lacks
        Assert.False(await Refresher(backend, source).TryRefreshAsync("SYM", TimeframeType.Daily, extra, CancellationToken.None));
    }

    [Fact]
    public async Task EachOutcome_IsLogged_WhyAnAttestationWasOrWasNotApplied()
    {
        var (backend, _) = await ArrangeAsync(PredictionMonitoringBackendKind.Sqlite);
        var settings = new PredictionMonitoringSettings();
        settings.AttestedSources.Add(new AttestedSourceEnrollment { Symbol = "SYM", Timeframe = TimeframeType.Daily, ProviderId = "prov", InstrumentId = "INST" });
        var logger = new CapturingLogger<AttestedMonitoringRefresher>();
        AttestedMonitoringRefresher Make(IDataService data) => new(backend, data, Options.Create(settings), logger);

        await Make(new AttestingSource(null)).TryRefreshAsync("SYM", TimeframeType.Daily, Bars(), CancellationToken.None);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Debug, logger.Entries[^1].Level);
        Assert.Contains("No attestation", logger.Entries[^1].Message);

        await Make(new AttestingSource(Snapshot(Bars()))).TryRefreshAsync("SYM", TimeframeType.Daily, Bars(future: 107m), CancellationToken.None);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, logger.Entries[^1].Level);
        Assert.Contains("does not describe", logger.Entries[^1].Message);

        await Make(new AttestingSource(Snapshot(Bars()))).TryRefreshAsync("SYM", TimeframeType.Daily, Bars(), CancellationToken.None);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Debug, logger.Entries[^1].Level);
        Assert.Contains("Applied", logger.Entries[^1].Message);
    }

    [Fact]
    public async Task AnAttestationFromAnotherProvider_IsAFaultNotAMerge()
    {
        var (backend, id) = await ArrangeAsync(PredictionMonitoringBackendKind.Sqlite);
        var source = new AttestingSource(Snapshot(Bars(), provider: "someone-else"));
        await Assert.ThrowsAsync<InvalidDataException>(() => Refresher(backend, source).TryRefreshAsync("SYM", TimeframeType.Daily, Bars(), CancellationToken.None));
        Assert.Equal(PredictionOutcomeState.Pending, await StateAsync(backend, id));
    }
}
