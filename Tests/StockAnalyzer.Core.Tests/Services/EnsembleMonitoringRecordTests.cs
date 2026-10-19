#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// An ensemble prediction is recorded in prediction monitoring as one forecast of the group <see cref="EnsembleMonitoringIdentity.MonitoringId"/>.
/// Every test runs on both storage backends with the same assertions. Expected values are independent expressions of the documented formulas.
/// </summary>
public sealed class EnsembleMonitoringRecordTests : IDisposable
{
    private const string Symbol = "ENSEMBLE";
    private static readonly string FixtureModel = Path.Combine("Assets", "trend_predictor_goodmeta.onnx");
    private readonly SqliteMonitoringTestKit _kit = new();
    private readonly List<IDisposable> _disposables = new();

    public void Dispose()
    {
        for (int i = _disposables.Count - 1; i >= 0; i--) _disposables[i].Dispose();   // the services that hold leases go before the registry
        _kit.Dispose();
    }

    private sealed class Context
    {
        public required ModelGenerationRegistry Registry { get; init; }
        public required ModelGenerationManifest A { get; init; }
        public required ModelGenerationManifest B { get; init; }
        public required EnsembleSpec Spec { get; init; }
        public required PredictionOutputContract Merged { get; init; }
        public required IReadOnlyDictionary<string, string> Metadata { get; init; }
        public required decimal Threshold { get; init; }
        public required string[] Canonical { get; init; }
        public required IPredictionLogService Log { get; init; }
        public required ICurrentPredictionHealthService Health { get; init; }
        public int Horizon => Merged.HorizonBars;
    }

    private async Task<ModelGenerationManifest> RegisterAsync(ModelGenerationRegistry registry, string name, double accuracy)
    {
        var source = Path.Combine(_kit.Root, "members", name);
        Directory.CreateDirectory(source);
        var model = Path.Combine(source, "member.onnx");
        File.Copy(FixtureModel, model);
        using var session = new InferenceSession(model);
        var metadata = session.ModelMetadata.CustomMetadataMap;
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(model))).ToLowerInvariant();
        var revision = new string('a', 64);
        var report = new
        {
            evaluation_status = "independent_outer", evaluation_revision = revision,
            scored_model_sha256 = hash, target_type = "classification", timeframe = "daily",
            horizon = int.Parse(metadata[PredictionModelMetadata.HorizonKey]),
            class_order = metadata[PredictionModelMetadata.ClassOrderKey], fold_macro_f1 = accuracy,
            fold_accuracy = accuracy, fold_is_holdout = 1.0, fold = 2.0, fold_n = 20.0, n_splits = 3.0,
            outer_folds = new[]
            {
                new { fold = 0.0, evaluation_revision = revision, scored_model_sha256 = hash, fold_macro_f1 = .2, fold_accuracy = .2 },
                new { fold = 1.0, evaluation_revision = revision, scored_model_sha256 = hash, fold_macro_f1 = .3, fold_accuracy = .3 },
                new { fold = 2.0, evaluation_revision = revision, scored_model_sha256 = hash, fold_macro_f1 = accuracy, fold_accuracy = accuracy },
            },
        };
        File.WriteAllText(model + ".metrics.json", JsonSerializer.Serialize(report));
        return await registry.RegisterAsync(model);
    }

    private async Task<Context> CreateAsync(bool sqlite)
    {
        var registry = new ModelGenerationRegistry(Path.Combine(_kit.Root, "registry"));
        _disposables.Add(registry);
        var a = await RegisterAsync(registry, "a", .4);
        var b = await RegisterAsync(registry, "b", .6);
        using var session = new InferenceSession(FixtureModel);
        var metadata = new Dictionary<string, string>(session.ModelMetadata.CustomMetadataMap);
        var contract = PredictionModelMetadata.ReadOutputContract(metadata);
        var canonical = a.ClassOrder.Split(',');
        Assert.All(canonical, label => Assert.Contains(label, new[] { "Up", "Down", "Neutral" }));
        Assert.True(PredictionMonitoringReducer.TryReadNeutralThreshold(metadata, out var threshold));

        IPredictionLogService log;
        if (sqlite)
        {
            var store = _kit.Store();
            if (!await store.IsImportCompleteAsync(CancellationToken.None))
                await PredictionMonitoringMigrator.ImportAsync(store, _kit.Clock.GetUtcNow(), CancellationToken.None);
            log = new SqlitePredictionLogService(registry, store, TimeSpan.FromMinutes(1), _kit.Clock, null, null);
        }
        else log = new PredictionLogService(registry, _kit.Clock, _kit.MonitorRoot);

        return new Context
        {
            Registry = registry, A = a, B = b, Spec = SpecOf(a, b, .25, .75), Metadata = metadata, Threshold = threshold,
            Merged = EnsemblePredictionService.MergeOutputContract(new[] { contract, contract }), Canonical = canonical,
            Log = log, Health = (ICurrentPredictionHealthService)log,
        };
    }

    private static EnsembleSpec SpecOf(ModelGenerationManifest a, ModelGenerationManifest b, double wa, double wb) =>
        new(Array.AsReadOnly(new[] { new EnsembleMember(a.ModelId, wa), new EnsembleMember(b.ModelId, wb) }));

    /// <summary>The real mixing function with a threshold of 0, so the label is always a class label ("Up" has the largest probability).</summary>
    private static PredictionResult ResultOf(Context c, EnsembleSpec? spec = null)
    {
        spec ??= c.Spec;
        int up = Array.IndexOf(c.Canonical, "Up");
        Assert.True(up >= 0, "The fixture must have the class 'Up'.");
        var row = Enumerable.Range(0, c.Canonical.Length)
            .Select(i => i == up ? .6f : .4f / (c.Canonical.Length - 1)).ToArray();
        var rows = spec.Members.Select(_ => (float[])row.Clone()).ToArray();
        var result = EnsemblePredictionService.Combine(spec, c.Canonical, rows, 0f, new MLDataProcessor(), c.Merged) with { Ensemble = spec };
        Assert.Equal("Up", result.Label);
        return result;
    }

    /// <summary>All bars up to the anchor; the target bar lies <paramref name="horizon"/> bars after it.</summary>
    private static CandleData[] Inputs(Context c, DateTime end) => SqliteMonitoringTestKit.BarsEnding(end, 106m).Take(200 - c.Horizon).ToArray();

    private static string Sha(string text) => PredictionMonitoringFormulaTests.Sha256Hex(text);

    private static string ExpectedId(EnsembleSpec spec, string symbol, DateTime anchorTimestamp, string feature, string target) =>
        Sha(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{Sha("ensemble-monitoring:v1\n" + spec.Fingerprint())}|{symbol}|{TimeframeType.Daily}|{new DateTimeOffset(DateTime.SpecifyKind(anchorTimestamp, DateTimeKind.Utc)):O}|{feature}|{target}"));

    private async Task AssertNothingStoredAsync(Context c) => Assert.Empty(await c.Health.EvaluateCurrentAsync());

    // ---- recording ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnsembleResult_IsRecordedWithEveryFieldOfTheDocumentedDefinition(bool sqlite)
    {
        var c = await CreateAsync(sqlite);
        var inputs = Inputs(c, SqliteMonitoringTestKit.Sept28);
        var result = ResultOf(c);

        var id = await c.Log.RecordAsync(result, Symbol, TimeframeType.Daily, inputs);

        Assert.NotNull(id);
        var p = (await c.Log.ReadAuditAsync(id!))!.Prediction;
        string feature = Sha(string.Join("\n", Enumerable.Repeat(FixedScaler.ComputeContractHash(c.Metadata), 2)));
        string target = Sha(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{c.Merged.Semantic}|{c.Merged.TargetFormula}|{c.Merged.Timeframe}|{c.Merged.HorizonBars}|{c.Threshold}"));
        Assert.Equal(ExpectedId(c.Spec, Symbol, inputs[^1].Timestamp, feature, target), p.PredictionId);
        Assert.Equal(id, p.PredictionId);
        Assert.Equal(Sha("ensemble-monitoring:v1\n" + c.Spec.Fingerprint()), p.ModelId);
        Assert.Equal(c.Spec.MonitoringId(), p.ModelId);
        Assert.Equal(Sha(c.A.ModelHash + "\n" + c.B.ModelHash), p.ModelHash);
        Assert.Equal(feature, p.FeatureContractHash);
        Assert.Equal(target, p.TargetContractHash);
        Assert.Equal(Symbol, p.Symbol);
        Assert.Equal(TimeframeType.Daily, p.Timeframe);
        Assert.Equal(c.Horizon, p.HorizonBars);
        Assert.Equal(c.Threshold, p.NeutralThreshold);
        Assert.Equal("Up", p.PredictedLabel);
        Assert.Equal(PredictionLogService.SourceRevision(inputs), p.InputRevision);
        Assert.Equal(_kit.Clock.GetUtcNow(), p.RecordedUtc);
        var audit = (await c.Log.ReadAuditAsync(id!))!;
        Assert.Single(audit.Outcomes);
        Assert.Equal(PredictionOutcomeState.Pending, audit.Outcomes[0].State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealEnsembleService_ResultIsRecordedExactlyWhenItHasAClassLabel(bool sqlite)
    {
        var c = await CreateAsync(sqlite);
        var settings = new PredictionServiceTests.TestSettings(FixtureModel, predictionWindowSize: 10);
        var processor = new MLDataProcessor();
        var single = new PredictionService(settings, processor, modelRegistry: c.Registry);
        _disposables.Add(single);
        var manager = new EnsembleSettingsManager(c.Registry, Path.Combine(_kit.Root, "ensemble.json"));
        await manager.SaveAsync(c.Spec);
        var ensemble = new EnsemblePredictionService(single, manager, c.Registry, settings, processor);
        _disposables.Add(ensemble);
        var candles = Enumerable.Range(0, 20).Select(i =>
            new CandleData(new DateTime(2024, 1, 1).AddDays(i), 100m, 105m, 95m, 100m + i, 1000)).ToArray();

        var result = await ensemble.PredictAsync(candles);
        var id = await c.Log.RecordAsync(result, Symbol, TimeframeType.Daily, candles);

        Assert.False(result.IsFallback);
        Assert.Equal(result.Label == "Unknown", id is null);
        if (id is not null) Assert.Equal(c.Spec.MonitoringId(), (await c.Log.ReadAuditAsync(id))!.Prediction.ModelId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplayOfTheSameForecast_IsOneForecast(bool sqlite)
    {
        var c = await CreateAsync(sqlite);
        var inputs = Inputs(c, SqliteMonitoringTestKit.Sept28);
        var result = ResultOf(c);

        var first = await c.Log.RecordAsync(result, Symbol, TimeframeType.Daily, inputs);
        _kit.Clock.Now = _kit.Clock.Now.AddMinutes(5);
        var second = await c.Log.RecordAsync(result, Symbol, TimeframeType.Daily, inputs);

        Assert.Equal(first, second);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), (await c.Log.ReadAuditAsync(first!))!.Prediction.RecordedUtc);
        if (sqlite) Assert.Equal(1, await _kit.CountAsync("Predictions"));
        else Assert.Single(Directory.GetFiles(Path.Combine(_kit.MonitorRoot, "predictions"), "*.json"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnsembleForecast_FormsItsOwnGroup_AndOtherWeightsFormAnother(bool sqlite)
    {
        var c = await CreateAsync(sqlite);
        var inputs = Inputs(c, SqliteMonitoringTestKit.Sept28);
        var other = SpecOf(c.A, c.B, .5, .5);

        var first = await c.Log.RecordAsync(ResultOf(c), Symbol, TimeframeType.Daily, inputs);
        var group = Assert.Single(await c.Health.EvaluateCurrentAsync());
        Assert.Equal(c.Spec.MonitoringId(), group.ModelId);
        Assert.Equal((0, 0, ModelHealthState.InsufficientData), (group.MaturedCount, group.CorrectCount, group.State));
        Assert.Empty(await c.Log.GetSnapshotsAsync(c.A.ModelId));   // the members receive no vote
        Assert.Empty(await c.Log.GetSnapshotsAsync(c.B.ModelId));

        var second = await c.Log.RecordAsync(ResultOf(c, other), Symbol, TimeframeType.Daily, inputs);
        Assert.NotEqual(first, second);
        var groups = await c.Health.EvaluateCurrentAsync();
        Assert.Equal(new[] { c.Spec.MonitoringId(), other.MonitoringId() }.OrderBy(x => x, StringComparer.Ordinal),
            groups.Select(g => g.ModelId).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Single(await c.Health.EvaluateCurrentAsync(other.MonitoringId()));
    }

    // ---- results that are not monitorable (null, nothing stored) -------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NotMonitorableResults_ReturnNull_AndStoreNothing(bool sqlite)
    {
        var c = await CreateAsync(sqlite);
        var inputs = Inputs(c, SqliteMonitoringTestKit.Sept28);
        var result = ResultOf(c);
        var cases = new (string Name, PredictionResult Result, IReadOnlyList<CandleData> Inputs)[]
        {
            ("G1 fallback", result with { IsFallback = true }, inputs),
            ("G2 regression", result with { IsRegression = true }, inputs),
            ("G3 generation id present", result with { ModelId = c.A.ModelId }, inputs),
            ("G4 no inputs", result, Array.Empty<CandleData>()),
            ("G5 other semantic", result with { OutputContract = c.Merged with { Semantic = PredictionOutputSemantic.LogReturn } }, inputs),
            ("G5 no contract", result with { OutputContract = null }, inputs),
            ("G6 other timeframe", result with { OutputContract = c.Merged with { Timeframe = TimeframeType.Weekly } }, inputs),
            ("G7 unknown label", result with { Label = "Unknown" }, inputs),
        };

        foreach (var (name, candidate, candidateInputs) in cases)
        {
            Assert.True(null == await c.Log.RecordAsync(candidate, Symbol, TimeframeType.Daily, candidateInputs), name);
            await AssertNothingStoredAsync(c);
        }
    }

    // ---- invalid input (throws, nothing stored) ------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidInputs_Throw_AndStoreNothing(bool sqlite)
    {
        var c = await CreateAsync(sqlite);
        var inputs = Inputs(c, SqliteMonitoringTestKit.Sept28);
        var result = ResultOf(c);
        var swapped = inputs.ToArray();
        (swapped[^2], swapped[^1]) = (swapped[^1], swapped[^2]);
        var ghost = new EnsembleSpec(Array.AsReadOnly(new[]
        {
            new EnsembleMember(c.A.ModelId, .5), new EnsembleMember(new string('f', 64), .5),
        }));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAsync<InvalidDataException>(() => c.Log.RecordAsync(result, Symbol, TimeframeType.Daily, swapped));   // T1
        _kit.Clock.Now = new DateTimeOffset(inputs[^1].Timestamp, TimeSpan.Zero).AddDays(-1);
        await Assert.ThrowsAsync<InvalidDataException>(() => c.Log.RecordAsync(result, Symbol, TimeframeType.Daily, inputs));     // T2
        _kit.Clock.Now = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        await Assert.ThrowsAsync<ArgumentException>(() => c.Log.RecordAsync(result, " ", TimeframeType.Daily, inputs));           // T3
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.Log.RecordAsync(
            result with { Ensemble = SpecOf(c.A, c.B, .3, .3) }, Symbol, TimeframeType.Daily, inputs));                            // T4 weights
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.Log.RecordAsync(
            result, Symbol, TimeframeType.Daily, inputs, cancelled.Token));                                                        // T4 cancellation
        await Assert.ThrowsAsync<InvalidDataException>(() => c.Log.RecordAsync(
            result with { Ensemble = ghost }, Symbol, TimeframeType.Daily, inputs));                                                // T5 missing member
        await Assert.ThrowsAsync<InvalidDataException>(() => c.Log.RecordAsync(
            result with { OutputContract = c.Merged with { HorizonBars = c.Horizon + 1 } }, Symbol, TimeframeType.Daily, inputs)); // T6 contract
        await Assert.ThrowsAsync<InvalidDataException>(() => c.Log.RecordAsync(
            result with { Label = "Sideways" }, Symbol, TimeframeType.Daily, inputs));                                              // T8 label

        await AssertNothingStoredAsync(c);
    }

    // ---- dispatch ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SingleModelResult_GoesThroughThePreviousPreparerUnchanged()
    {
        var registry = new ModelGenerationRegistry(Path.Combine(_kit.Root, "single-registry"));
        _disposables.Add(registry);
        var model = await registry.RegisterAsync(Path.Combine("Assets", "analysis_fixture.onnx"));
        using var session = new InferenceSession(Path.Combine("Assets", "analysis_fixture.onnx"));
        var result = new PredictionResult("Up", 1, [new("Up", 1)])
        {
            ModelId = model.ModelId, OutputContract = PredictionModelMetadata.ReadOutputContract(session.ModelMetadata.CustomMetadataMap),
        };
        var inputs = SqliteMonitoringTestKit.BarsEnding(SqliteMonitoringTestKit.Sept28).Take(195).ToArray();

        var viaFactory = await MonitoredPredictionFactory.CreateAsync(registry, _kit.Clock, result, Symbol, TimeframeType.Daily, inputs, CancellationToken.None);
        var direct = await PredictionMonitoringReducer.PrepareAsync(registry, _kit.Clock, result, Symbol, TimeframeType.Daily, inputs, CancellationToken.None);

        Assert.NotNull(direct);
        Assert.Equal(direct, viaFactory);
        Assert.Equal(model.ModelId, direct!.ModelId);
    }

    [Fact]
    public async Task MetadataCache_ReusesExtractedMetadata_WithoutRecreatingSession()
    {
        var registry = new ModelGenerationRegistry(Path.Combine(_kit.Root, "cached-registry"));
        _disposables.Add(registry);
        var model = await registry.RegisterAsync(Path.Combine("Assets", "analysis_fixture.onnx"));

        PredictionMonitoringReducer.ClearMetadataCacheForTesting();
        var meta1 = PredictionMonitoringReducer.GetOrReadModelMetadata(registry, model.ModelId);
        var meta2 = PredictionMonitoringReducer.GetOrReadModelMetadata(registry, model.ModelId);

        Assert.NotNull(meta1);
        Assert.Same(meta1, meta2); // Exactly same instance from LRU cache
    }

    // ---- lifecycle -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnsembleGroup_MaturesVotesAndDecaysLikeAnyModel(bool sqlite)
    {
        var c = await CreateAsync(sqlite);
        Assert.True(c.Threshold < .06m, "The 6% move of the bars must exceed the neutral threshold.");
        var end = SqliteMonitoringTestKit.Sept28;
        var inputs = Inputs(c, end);
        var decayed = new List<ModelDecayAlert>();
        c.Log.Decayed += (_, alert) => decayed.Add(alert);

        for (int i = 0; i < 30; i++)
        {
            await c.Log.RecordAsync(ResultOf(c), "E" + i, TimeframeType.Daily, inputs);
            await c.Log.RefreshAsync("E" + i, TimeframeType.Daily, _kit.Observe(SqliteMonitoringTestKit.BarsEnding(end, i < 15 ? 106m : 94m)));
        }

        var healthy = Assert.Single(await c.Health.EvaluateCurrentAsync(c.Spec.MonitoringId()));
        Assert.Equal((30, 15, .5m, ModelHealthState.Healthy), (healthy.MaturedCount, healthy.CorrectCount, healthy.Accuracy, healthy.State));
        Assert.Empty(decayed);

        await c.Log.RefreshAsync("E0", TimeframeType.Daily, _kit.Observe(SqliteMonitoringTestKit.BarsEnding(end, 94m)));   // one correct vote is corrected to wrong: K = 14

        var decay = Assert.Single(await c.Health.EvaluateCurrentAsync(c.Spec.MonitoringId()));
        Assert.Equal((30, 14, ModelHealthState.Decayed), (decay.MaturedCount, decay.CorrectCount, decay.State));
        var alert = Assert.Single(decayed);
        Assert.Equal(c.Spec.MonitoringId(), alert.Snapshot.ModelId);
        Assert.Empty(await c.Log.GetSnapshotsAsync(c.A.ModelId));
    }

    [Fact]
    public async Task EnsembleForecastsInTheJsonStore_AreImportedIntoSqliteWithTheSameAuditAndHealth()
    {
        var c = await CreateAsync(sqlite: false);
        var end = SqliteMonitoringTestKit.Sept28;
        var inputs = Inputs(c, end);
        var ids = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            ids.Add((await c.Log.RecordAsync(ResultOf(c), "M" + i, TimeframeType.Daily, inputs))!);
            await c.Log.RefreshAsync("M" + i, TimeframeType.Daily, _kit.Observe(SqliteMonitoringTestKit.BarsEnding(end, 106m)));
        }
        var jsonAudits = new List<PredictionAuditRecord>();
        foreach (var id in ids) jsonAudits.Add((await c.Log.ReadAuditAsync(id))!);
        var jsonHealth = await c.Health.EvaluateCurrentAsync();

        var store = _kit.Store();
        await PredictionMonitoringMigrator.ImportAsync(store, _kit.Clock.GetUtcNow(), CancellationToken.None);
        var sqlite = new SqlitePredictionLogService(c.Registry, store, TimeSpan.FromMinutes(1), _kit.Clock, null, null);

        for (int i = 0; i < ids.Count; i++)
        {
            var imported = (await sqlite.ReadAuditAsync(ids[i]))!;
            Assert.Equal(jsonAudits[i].Prediction, imported.Prediction);
            Assert.True(jsonAudits[i].Outcomes.SequenceEqual(imported.Outcomes));
        }
        var health = Assert.Single(await ((ICurrentPredictionHealthService)sqlite).EvaluateCurrentAsync());
        Assert.Equal(c.Spec.MonitoringId(), health.ModelId);
        Assert.Equal((3, 3), (health.MaturedCount, health.CorrectCount));
        Assert.Equal(jsonHealth.Single().TargetContractHash, health.TargetContractHash);
    }

    // ---- the monitoring id ---------------------------------------------------------------------------------------------

    [Fact]
    public void MonitoringId_IsDomainSeparatedHexOfTheFingerprint_AndFollowsEveryChange()
    {
        var a = new string('1', 64);
        var b = new string('2', 64);
        EnsembleSpec Spec(params (string Id, double Weight)[] members) =>
            new(Array.AsReadOnly(members.Select(m => new EnsembleMember(m.Id, m.Weight)).ToArray()));
        var baseSpec = Spec((a, .25), (b, .75));

        var id = baseSpec.MonitoringId();

        Assert.True(PredictionMonitoringReducer.ValidHash(id));
        Assert.Equal(id, Spec((a, .25), (b, .75)).MonitoringId());                                         // deterministic
        Assert.Equal(Sha("ensemble-monitoring:v1\n" + baseSpec.Fingerprint()), id);                        // the documented formula
        Assert.Equal(EnsembleMonitoringIdentity.MonitoringIdDomain, "ensemble-monitoring:v1");
        Assert.NotEqual(id, Spec((a, .5), (b, .5)).MonitoringId());                                        // weight
        Assert.NotEqual(id, Spec((b, .75), (a, .25)).MonitoringId());                                      // order
        Assert.NotEqual(id, Spec((a, .25), (new string('3', 64), .75)).MonitoringId());                    // member
        Assert.NotEqual(baseSpec.Fingerprint()[EnsembleSpec.FingerprintPrefix.Length..], id);              // not the bare fingerprint hash
        Assert.NotEqual(a, id);
        Assert.NotEqual(b, id);
    }
}
