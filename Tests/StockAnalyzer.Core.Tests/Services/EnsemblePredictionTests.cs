using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
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
    private static readonly TimeSpan BarrierTimeout = PredictionBarrierProcessor.BarrierTimeout;
    private readonly ITestOutputHelper _output;
    public EnsemblePredictionTests(ITestOutputHelper output) => _output = output;

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _pending = new();
        public override void Post(SendOrPostCallback callback, object? state) =>
            _pending.Enqueue((callback, state));
        public void Drain()
        {
            while (_pending.TryDequeue(out var item)) item.Callback(item.State);
        }
    }

    private sealed class DelayedSettingsManager : IEnsembleSettingsManager
    {
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public EnsembleSpec? Current => null;
        public async Task LoadAsync()
        {
            Started.TrySetResult(true);
            await _release.Task;
        }
        public Task SaveAsync(EnsembleSpec? spec) => throw new NotSupportedException();
        internal void Release() => _release.TrySetResult(true);
    }

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

    [Fact]
    public async Task DisposeDuringSettingsLoad_RejectsPendingPrediction()
    {
        var settings = new PredictionServiceTests.TestSettings();
        using var single = new PredictionService(settings, new MLDataProcessor());
        var manager = new DelayedSettingsManager();
        using var ensemble = new EnsemblePredictionService(single, manager, new FakeRegistry(),
            settings, new MLDataProcessor());
        var candles = Enumerable.Range(0, 20).Select(i =>
            new CandleData(new DateTime(2024, 1, 1).AddDays(i), 100m, 105m, 95m, 100m + i, 1000)).ToArray();
        var pending = ensemble.PredictAsync(candles);
        await manager.Started.Task.WaitAsync(BarrierTimeout);
        ensemble.Dispose();
        manager.Release();
        Assert.True((await pending.WaitAsync(BarrierTimeout)).IsFallback);
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

    [Fact]
    public void EnsembleValidation_ReportsStableReasonWithoutChangingExceptionType()
    {
        var registry = new FakeRegistry(Manifest("a", "rev"), Manifest("b", "rev") with { Horizon = 2 });
        var missing = Assert.Throws<InvalidOperationException>(() =>
            EnsembleSettingsManager.ValidateGenerations(SpecFor("missing", "a", .5, .5), registry));
        Assert.Equal(EnsembleValidationFailure.MissingGeneration,
            EnsembleSettingsManager.GetValidationFailure(missing));

        var incompatible = Assert.Throws<InvalidOperationException>(() =>
            EnsembleSettingsManager.ValidateGenerations(SpecFor("a", "b", .5, .5), registry));
        Assert.Equal(EnsembleValidationFailure.IncompatibleContract,
            EnsembleSettingsManager.GetValidationFailure(incompatible));

        var invalidWeights = Assert.Throws<InvalidOperationException>(() => SpecFor("a", "b", .2, .2).Validate());
        Assert.Null(EnsembleSettingsManager.GetValidationFailure(invalidWeights));

        var invalidAccuracy = Assert.Throws<InvalidOperationException>(() =>
            EnsembleSpec.AccuracyProportional(new[] { Manifest("a", "rev") }, _ => 0));
        Assert.Equal(EnsembleValidationFailure.InvalidAccuracyEvidence,
            EnsembleSettingsManager.GetValidationFailure(invalidAccuracy));
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
    public async Task SaveAsync_FreezesCallerMembersBeforeWaitingForAnEarlierSave()
    {
        var path = Path.Combine(Path.GetTempPath(), "sa_ensemble_snapshot_" + Guid.NewGuid().ToString("N") + ".json");
        var registry = new FakeRegistry(Manifest("a", "rev"), Manifest("b", "rev"));
        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int blocked = 0;
        registry.BeforeManifestLookup = _ =>
        {
            if (Interlocked.Exchange(ref blocked, 1) != 0) return;
            entered.TrySetResult(true);
            if (!release.Wait(BarrierTimeout)) throw new TimeoutException("Registry barrier was not released.");
        };
        try
        {
            var manager = new EnsembleSettingsManager(registry, path);
            var first = manager.SaveAsync(SpecFor("a", "b", 1, 0));
            await entered.Task.WaitAsync(BarrierTimeout);
            var mutableMembers = new List<EnsembleMember> { new("a", 1) };
            var second = manager.SaveAsync(new EnsembleSpec(mutableMembers));
            mutableMembers[0] = new EnsembleMember("b", 1);
            release.Set();
            await Task.WhenAll(first, second).WaitAsync(BarrierTimeout);
            Assert.Equal("a", manager.Current!.Members[0].ModelId);
            var reloaded = new EnsembleSettingsManager(registry, path);
            await reloaded.LoadAsync();
            Assert.Equal("a", reloaded.Current!.Members[0].ModelId);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.SaveAsync(new EnsembleSpec(null!)));
        }
        finally
        {
            release.Set();
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
            using var processor = new PredictionBarrierProcessor();
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
            foreach (var reversed in new[] { false, true })
            {
                var invalid = candles.ToArray();
                if (reversed) (invalid[8], invalid[9]) = (invalid[9], invalid[8]);
                else invalid[8] = invalid[8] with { Timestamp = invalid[7].Timestamp };
                Assert.True((await ensemble.PredictAsync(invalid)).IsFallback);
            }
            processor.Arm();
            var stablePrediction = Task.Run(() => ensemble.PredictAsync(candles));
            await processor.Started.WaitAsync(BarrierTimeout);
            var lastCandle = candles[^1];
            try
            {
                candles[^1] = lastCandle with { Close = lastCandle.Close + 1000m,
                    Timestamp = candles[^2].Timestamp };
            }
            finally { processor.Release(); }
            var stableResult = await stablePrediction.WaitAsync(BarrierTimeout);
            candles[^1] = lastCandle;
            Assert.False(stableResult.IsFallback);
            Assert.Equal(2, processor.ObservedCloses.Length);
            Assert.All(processor.ObservedCloses, close => Assert.Equal(lastCandle.Close, close));
            Assert.Equal(combined.Scores.Select(score => score.Score),
                stableResult.Scores.Select(score => score.Score));
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

            await manager.SaveAsync(SpecFor(a.ModelId, b.ModelId, .25, .75));
            using var uiEnsemble = new EnsemblePredictionService(single, manager, registry, settings, processor);
            Assert.False((await uiEnsemble.PredictAsync(candles)).IsFallback);
            var uiContext = new QueuedSynchronizationContext();
            var disposeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            processor.Arm();
            var uiThread = Task.Run(() =>
            {
                SynchronizationContext.SetSynchronizationContext(uiContext);
                try
                {
                    var prediction = uiEnsemble.PredictAsync(candles);
                    processor.Started.Wait(BarrierTimeout);
                    disposeStarted.TrySetResult(true);
                    uiEnsemble.Dispose();
                    return prediction;
                }
                finally { SynchronizationContext.SetSynchronizationContext(null); }
            });
            await disposeStarted.Task.WaitAsync(BarrierTimeout);
            processor.Release();
            try
            {
                var result = await uiThread.WaitAsync(BarrierTimeout);
                Assert.False(result.IsFallback);
            }
            catch (TimeoutException)
            {
                uiContext.Drain();
                await uiThread.WaitAsync(BarrierTimeout);
                throw;
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ActualOnnxOutputShapeIsCheckedBeforeClassReordering_EvenAtZeroWeight()
    {
        var root = Path.Combine(Path.GetTempPath(), "sa_ensemble_shape_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var registry = new ModelGenerationRegistry(Path.Combine(root, "generations"));
            async Task<ModelGenerationManifest> Register(string name, string fixture)
            {
                var directory = Path.Combine(root, name);
                Directory.CreateDirectory(directory);
                var model = Path.Combine(directory, "member.onnx");
                File.Copy(Path.Combine("Assets", fixture), model);
                using var session = new InferenceSession(model);
                var metadata = session.ModelMetadata.CustomMetadataMap;
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(model))).ToLowerInvariant();
                var revision = new string('a', 64);
                var report = new
                {
                    evaluation_status = "independent_outer", evaluation_revision = revision,
                    scored_model_sha256 = hash, target_type = "classification", timeframe = "daily",
                    horizon = int.Parse(metadata[PredictionModelMetadata.HorizonKey]),
                    class_order = metadata[PredictionModelMetadata.ClassOrderKey], fold_macro_f1 = .4,
                    fold_is_holdout = 1.0, fold = 2.0, fold_n = 20.0, n_splits = 3.0,
                    outer_folds = new[]
                    {
                        new { fold = 0.0, evaluation_revision = revision, scored_model_sha256 = hash, fold_macro_f1 = .2 },
                        new { fold = 1.0, evaluation_revision = revision, scored_model_sha256 = hash, fold_macro_f1 = .3 },
                        new { fold = 2.0, evaluation_revision = revision, scored_model_sha256 = hash, fold_macro_f1 = .4 },
                    },
                };
                File.WriteAllText(model + ".metrics.json", JsonSerializer.Serialize(report));
                return await registry.RegisterAsync(model);
            }
            var valid = await Register("valid", "trend_predictor_goodmeta.onnx");
            var extraClass = await Register("extra_class", "trend_predictor_runtime_extra_class.onnx");
            var extraBatch = await Register("extra_batch", "trend_predictor_runtime_extra_batch.onnx");
            var reordered = await Register("reordered", "trend_predictor_reordered_classes.onnx");
            var settings = new PredictionServiceTests.TestSettings(
                Path.Combine("Assets", "trend_predictor_goodmeta.onnx"), predictionWindowSize: 10);
            var processor = new MLDataProcessor();
            using var single = new PredictionService(settings, processor);
            var manager = new EnsembleSettingsManager(registry, Path.Combine(root, "ensemble.json"));
            using var ensemble = new EnsemblePredictionService(single, manager, registry, settings, processor);
            var candles = Enumerable.Range(0, 20).Select(i =>
                new CandleData(new DateTime(2024, 1, 1).AddDays(i), 100m, 105m, 95m, 100m + i, 1000)).ToArray();
            foreach (var invalid in new[] { extraClass, extraBatch })
            {
                await manager.SaveAsync(SpecFor(valid.ModelId, invalid.ModelId, 1, 0));
                Assert.True((await ensemble.PredictAsync(candles)).IsFallback);
            }
            await manager.SaveAsync(SpecFor(valid.ModelId, reordered.ModelId, .5, .5));
            var expected = await single.PredictAsync(candles);
            var actual = await ensemble.PredictAsync(candles);
            Assert.False(expected.IsFallback);
            Assert.False(actual.IsFallback);
            for (int i = 0; i < expected.Scores.Count; i++)
                Assert.Equal(expected.Scores[i].Score, actual.Scores[i].Score, 5);
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
        public Action<string>? BeforeManifestLookup { get; set; }
        public void Replace(ModelGenerationManifest model) => _models[model.ModelId] = model;
        public string? ActiveId => null;
        public string? PreviousId => null;
        public IReadOnlyList<ModelGenerationSummary> List() => Array.Empty<ModelGenerationSummary>();
        public Task<ModelGenerationManifest> RegisterAsync(string onnxPath, string? metricsPath = null, System.Threading.CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> ActivateAsync(string modelId, bool manual = false, bool confirmLowerScore = false, System.Threading.CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> ConfirmActivationAsync(string modelId, string? expectedActiveId, System.Threading.CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> RollbackAsync(bool confirmLowerScore = false, System.Threading.CancellationToken ct = default) => throw new NotSupportedException();
        public ModelGenerationLease? AcquireActive() => null;
        public ModelGenerationManifest? GetManifest(string id)
        {
            BeforeManifestLookup?.Invoke(id);
            return _models.GetValueOrDefault(id);
        }
        public ModelGenerationLease? Acquire(string id) => _models.ContainsKey(id)
            ? new ModelGenerationLease(id, Path.Combine("Assets", "trend_predictor_goodmeta.onnx"), () => { }) : null;
    }
}
