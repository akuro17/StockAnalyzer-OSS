using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Core.Tests.Services;

[Collection("Non-Parallel ONNX Tests")]
public sealed class EnsemblePredictionTests
{
    private readonly ITestOutputHelper _output;
    public EnsemblePredictionTests(ITestOutputHelper output) => _output = output;

    private static EnsembleSpec Spec(params double[] weights) => new(weights.Select((w, i) =>
        new EnsembleMember("model-" + i, w)).ToArray());

    [Fact]
    public void WeightedFixture_ReordersLabelsWithoutSoftmax_AndOwnsScores()
    {
        var first = new[] { .8f, .1f, .1f };
        // The second model stores Neutral, Up, Down; align it before combination.
        var second = new[] { .5f, .2f, .3f };
        var aligned = new[] { second[1], second[2], second[0] };
        var result = EnsemblePredictionService.Combine(Spec(.25, .75),
            new[] { "Up", "Down", "Neutral" }, new[] { first, aligned }, 0, new MLDataProcessor());
        Assert.Equal("Neutral", result.Label);
        Assert.Equal(.35f, result.Scores[0].Score, 5);
        Assert.Equal(.25f, result.Scores[1].Score, 5);
        Assert.Equal(.40f, result.Scores[2].Score, 5);
        first[0] = 0;
        Assert.Equal(.35f, result.Scores[0].Score, 5);
    }

    [Fact]
    public void EqualScoreTie_ChoosesFirstCanonicalClass()
    {
        var result = EnsemblePredictionService.Combine(Spec(1), new[] { "Up", "Down" },
            new[] { new[] { .5f, .5f } }, 0, new MLDataProcessor());
        Assert.Equal("Up", result.Label);
    }

    [Theory]
    [InlineData(0.5, 0.5, true)]
    [InlineData(0.50005, 0.50005, true)]
    [InlineData(0.5001, 0.5001, false)]
    [InlineData(double.NaN, 1, false)]
    [InlineData(-0.1, 1.1, false)]
    public void WeightValidation_UsesInclusiveToleranceAndFiniteNonnegativeValues(double a, double b, bool valid)
    {
        if (valid) Spec(a, b).Validate();
        else Assert.Throws<InvalidOperationException>(() => Spec(a, b).Validate());
    }

    [Fact]
    public void MissingDuplicateAndExcessMembersAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => EnsembleSpec.Equal(Array.Empty<string>()));
        Assert.Throws<InvalidOperationException>(() => EnsembleSpec.Equal(new[] { "a", "a" }));
        Assert.Throws<InvalidOperationException>(() => EnsembleSpec.Equal(Enumerable.Range(0, 9).Select(x => x.ToString())));
        Assert.Equal(.5, EnsembleSpec.Equal(new[] { "a", "b" }).Members[0].Weight);
    }

    [Theory]
    [InlineData(0.8f, 0.2f, false)]
    [InlineData(float.NaN, 1f, true)]
    [InlineData(1.1f, -0.1f, true)]
    [InlineData(.8f, .1f, true)]
    public void ProbabilityRowsAreValidated(float a, float b, bool invalid)
    {
        Action action = () => EnsemblePredictionService.Combine(Spec(1), new[] { "A", "B" },
            new[] { new[] { a, b } }, 0, new MLDataProcessor());
        if (invalid) Assert.Throws<InvalidDataException>(action);
        else action();
    }

    [Fact]
    public void AccuracyPreset_RequiresSameRevisionAndValidPositiveEvidence()
    {
        var a = Manifest("a", "rev");
        var b = Manifest("b", "rev");
        var spec = EnsembleSpec.AccuracyProportional(new[] { a, b }, m => m.ModelId == "a" ? 0 : .6);
        Assert.Equal(0, spec.Members[0].Weight);
        Assert.Equal(1, spec.Members[1].Weight);
        Assert.Throws<InvalidOperationException>(() => EnsembleSpec.AccuracyProportional(new[] { a, b }, _ => 0));
        Assert.Throws<InvalidOperationException>(() => EnsembleSpec.AccuracyProportional(new[] { a, b }, _ => double.NaN));
        Assert.Throws<InvalidOperationException>(() => EnsembleSpec.AccuracyProportional(new[] { a, b with { EvaluationRevision = "other" } }, _ => .5));
    }

    [Fact]
    public void AggregationLoop_AllocatesNoManagedMemoryAfterWarmup()
    {
        var spec = Spec(.25, .75);
        spec.Validate();
        float[][] rows = { new[] { .8f, .1f, .1f }, new[] { .2f, .3f, .5f } };
        var scratch = new double[3];
        var output = new float[3];
        for (int i = 0; i < 100; i++) EnsemblePredictionService.Aggregate(spec, rows, scratch, output);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) EnsemblePredictionService.Aggregate(spec, rows, scratch, output);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(.35f, output[0], 5);
    }

    [Fact]
    public async Task SettingsPersistExactWeights_AndRejectIncompatibleGenerations()
    {
        var path = Path.Combine(Path.GetTempPath(), "sa_ensemble_" + Guid.NewGuid().ToString("N") + ".json");
        var registry = new FakeRegistry(Manifest("a", "rev"), Manifest("b", "rev"));
        try
        {
            var manager = new EnsembleSettingsManager(registry, path);
            await manager.SaveAsync(SpecFor("a", "b", .25, .75));
            var reloaded = new EnsembleSettingsManager(registry, path);
            await reloaded.LoadAsync();
            Assert.Equal(.25, reloaded.Current!.Members[0].Weight);
            Assert.Equal(.75, reloaded.Current.Members[1].Weight);
            await reloaded.SaveAsync(null);
            var cleared = new EnsembleSettingsManager(registry, path);
            await cleared.LoadAsync();
            Assert.Null(cleared.Current);
            registry.Replace(Manifest("b", "rev") with { Horizon = 2 });
            await Assert.ThrowsAsync<InvalidOperationException>(() => reloaded.SaveAsync(SpecFor("a", "b", .25, .75)));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
        }
    }

    [Fact]
    public async Task RegisteredOnnxMembers_PredictAndClearToSingleModel()
    {
        var root = Path.Combine(Path.GetTempPath(), "sa_ensemble_native_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var registry = new ModelGenerationRegistry(Path.Combine(root, "generations"));
            async Task<ModelGenerationManifest> Register(string name, double accuracy)
            {
                var source = Path.Combine(root, name);
                Directory.CreateDirectory(source);
                var model = Path.Combine(source, "member.onnx");
                File.Copy(Path.Combine("Assets", "trend_predictor_goodmeta.onnx"), model);
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
                    fold_accuracy = accuracy, fold_is_holdout = 1.0, fold = 2.0, fold_n = 20.0,
                    n_splits = 3.0,
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
            var a = await Register("a", .4);
            var b = await Register("b", .6);
            var settings = new PredictionServiceTests.TestSettings(
                Path.Combine("Assets", "trend_predictor_goodmeta.onnx"), predictionWindowSize: 10);
            var processor = new MLDataProcessor();
            using var single = new PredictionService(settings, processor, modelRegistry: registry);
            var manager = new EnsembleSettingsManager(registry, Path.Combine(root, "ensemble.json"));
            await manager.SaveAsync(SpecFor(a.ModelId, b.ModelId, .25, .75));
            Assert.Equal(.4, EnsembleSettingsManager.ReadFinalOuterAccuracy(a, registry));
            var accuracy = EnsembleSpec.AccuracyProportional(new[] { a, b },
                m => EnsembleSettingsManager.ReadFinalOuterAccuracy(m, registry));
            Assert.Equal(.4, accuracy.Members[0].Weight, 6);
            using var ensemble = new EnsemblePredictionService(single, manager, registry, settings, processor);
            var candles = Enumerable.Range(0, 20).Select(i =>
                new CandleData(new DateTime(2024, 1, 1).AddDays(i), 100m, 105m, 95m, 100m + i, 1000)).ToArray();
            var combined = await ensemble.PredictAsync(candles);
            Assert.False(combined.IsFallback);
            Assert.Equal(3, combined.Scores.Count);
            Assert.InRange(combined.Scores.Sum(s => s.Score), .9999f, 1.0001f);
            for (int i = 0; i < 100; i++) await ensemble.PredictAsync(candles);
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            for (int i = 0; i < 1000; i++) await ensemble.PredictAsync(candles);
            long ensembleBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            _output.WriteLine($"Release x64 complete two-member ONNX prediction: {ensembleBytes} managed bytes / 1000 calls (includes ONNX and owned result).");
            await manager.SaveAsync(SpecFor(a.ModelId, b.ModelId, .25, .75));
            var sidecar = Path.Combine(root, "generations", b.ModelId, "member.onnx.metrics.json");
            var original = File.ReadAllText(sidecar);
            File.AppendAllText(sidecar, " ");
            Assert.True((await ensemble.PredictAsync(candles)).IsFallback);
            File.WriteAllText(sidecar, original);
            Assert.False((await ensemble.PredictAsync(candles)).IsFallback);
            await manager.SaveAsync(null);
            Assert.False((await ensemble.PredictAsync(candles)).IsFallback);
            for (int i = 0; i < 100; i++) await ensemble.PredictAsync(candles);
            allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            for (int i = 0; i < 1000; i++) await ensemble.PredictAsync(candles);
            long singleBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            _output.WriteLine($"Release x64 legacy single-model prediction: {singleBytes} managed bytes / 1000 calls (includes ONNX and owned result).");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static EnsembleSpec SpecFor(string a, string b, double x, double y) =>
        new(new[] { new EnsembleMember(a, x), new EnsembleMember(b, y) });

    private static ModelGenerationManifest Manifest(string id, string revision) =>
        new(1, id, "fixture.onnx", "hash", Array.Empty<ModelSidecar>(), "contract", revision,
            PredictionModelMetadata.TargetTypeClassification, "daily", 1, "Up,Down", .5);

    private sealed class FakeRegistry : IModelGenerationRegistry
    {
        private readonly Dictionary<string, ModelGenerationManifest> _models;
        public FakeRegistry(params ModelGenerationManifest[] models) => _models = models.ToDictionary(x => x.ModelId);
        public void Replace(ModelGenerationManifest model) => _models[model.ModelId] = model;
        public string? ActiveId => null;
        public string? PreviousId => null;
        public IReadOnlyList<ModelGenerationSummary> List() => Array.Empty<ModelGenerationSummary>();
        public Task<ModelGenerationManifest> RegisterAsync(string onnxPath, string? metricsPath = null, System.Threading.CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> ActivateAsync(string modelId, bool manual = false, bool confirmLowerScore = false, System.Threading.CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> RollbackAsync(bool confirmLowerScore = false, System.Threading.CancellationToken ct = default) => throw new NotSupportedException();
        public ModelGenerationLease? AcquireActive() => null;
        public ModelGenerationManifest? GetManifest(string id) => _models.GetValueOrDefault(id);
        public ModelGenerationLease? Acquire(string id) => _models.ContainsKey(id)
            ? new ModelGenerationLease(id, Path.Combine("Assets", "trend_predictor_goodmeta.onnx"), () => { }) : null;
    }
}
