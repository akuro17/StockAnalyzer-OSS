using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class ModelGenerationRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_generations_" + Guid.NewGuid().ToString("N"));
    private readonly string _fixture = Path.Combine("Assets", "trend_predictor_goodmeta.onnx");
    private const string Revision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const int CleanupAttempts = 5;
    private const int CleanupRetryDelayMilliseconds = 50;

    private string Source(string name, double score, string revision = Revision, string? fixture = null)
    {
        var directory = Path.Combine(_root, "source_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var model = Path.Combine(directory, name);
        File.Copy(fixture ?? _fixture, model);
        if (fixture is not null && File.Exists(fixture + ".scaler.json"))
            File.Copy(fixture + ".scaler.json", model + ".scaler.json");
        using var session = new InferenceSession(model);
        var metadata = session.ModelMetadata.CustomMetadataMap;
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(model))).ToLowerInvariant();
        var report = new
        {
            evaluation_status = "independent_outer",
            evaluation_revision = revision,
            scored_model_sha256 = hash,
            target_type = metadata[PredictionModelMetadata.TargetTypeKey],
            timeframe = "daily",
            horizon = int.Parse(metadata[PredictionModelMetadata.HorizonKey]),
            class_order = metadata[PredictionModelMetadata.ClassOrderKey],
            fold_macro_f1 = score,
            fold_is_holdout = 1.0,
            fold = 2.0,
            fold_n = 20.0,
            n_splits = 3.0,
            outer_folds = new[]
            {
                new { fold = 0.0, evaluation_revision = revision, scored_model_sha256 = hash, fold_macro_f1 = 0.2 },
                new { fold = 1.0, evaluation_revision = revision, scored_model_sha256 = hash, fold_macro_f1 = 0.3 },
                new { fold = 2.0, evaluation_revision = revision, scored_model_sha256 = hash, fold_macro_f1 = score },
            },
        };
        File.WriteAllText(model + ".metrics.json", JsonSerializer.Serialize(report));
        return model;
    }

    [Fact]
    public async Task RegistersImmutableGenerations_ActivatesOnlyStrictImprovement_AndRollsBack()
    {
        using var registry = new ModelGenerationRegistry(Path.Combine(_root, "registry"));
        var first = await registry.RegisterAsync(Source("same.onnx", 0.4));
        Assert.True(await registry.ActivateAsync(first.ModelId));
        using var oldLease = registry.AcquireActive();
        Assert.Equal(first.ModelId, oldLease!.ModelId);

        var tie = await registry.RegisterAsync(Source("same.onnx", 0.4));
        Assert.Equal(first.ModelId, tie.ModelId);
        var tiedSource = Source("same.onnx", 0.4);
        var tiedMetrics = tiedSource + ".metrics.json";
        File.WriteAllText(tiedMetrics, File.ReadAllText(tiedMetrics)[..^1] + ",\"audit_note\":\"same score\"}");
        var differentTie = await registry.RegisterAsync(tiedSource);
        Assert.NotEqual(first.ModelId, differentTie.ModelId);
        Assert.False(await registry.ActivateAsync(differentTie.ModelId));
        Assert.Equal(first.ModelId, registry.ActiveId);
        var better = await registry.RegisterAsync(Source("same.onnx", 0.6));
        Assert.NotEqual(first.ModelId, better.ModelId);
        Assert.True(await registry.ActivateAsync(better.ModelId));
        Assert.Equal(first.ModelId, oldLease.ModelId);
        Assert.True(File.Exists(oldLease.ModelPath));
        Assert.False(await registry.ActivateAsync(first.ModelId));
        await Assert.ThrowsAsync<ModelActivationConfirmationRequiredException>(
            () => registry.ActivateAsync(first.ModelId, manual: true));
        Assert.True(await registry.RollbackAsync(confirmLowerScore: true));
        Assert.Equal(first.ModelId, registry.ActiveId);
        Assert.Contains(registry.List(), item => item.ModelId == first.ModelId && item.IsActive);
        using var reopened = new ModelGenerationRegistry(Path.Combine(_root, "registry"));
        Assert.Equal(first.ModelId, reopened.ActiveId);
    }

    [Fact]
    public async Task Confirmation_RejectsChangedBaselineWithoutChangingPointerOrHistory()
    {
        var registryRoot = Path.Combine(_root, "registry");
        using var registry = new ModelGenerationRegistry(registryRoot);
        var baseline = await registry.RegisterAsync(Source("same.onnx", 0.4));
        var candidate = await registry.RegisterAsync(Source("same.onnx", 0.3));
        var replacement = await registry.RegisterAsync(Source("same.onnx", 0.6));
        Assert.True(await registry.ActivateAsync(baseline.ModelId));

        var warning = await Assert.ThrowsAsync<ModelActivationConfirmationRequiredException>(
            () => registry.ActivateAsync(candidate.ModelId, manual: true));
        Assert.Equal(candidate.ModelId, warning.CandidateId);
        Assert.Equal(baseline.ModelId, warning.ExpectedActiveId);

        Assert.True(await registry.ActivateAsync(replacement.ModelId));
        var pointerPath = Path.Combine(registryRoot, "active.json");
        var before = File.ReadAllText(pointerPath);
        await Assert.ThrowsAsync<ModelActivationContextChangedException>(() =>
            registry.ConfirmActivationAsync(warning.CandidateId!, warning.ExpectedActiveId));
        Assert.Equal(before, File.ReadAllText(pointerPath));
        Assert.Equal(replacement.ModelId, registry.ActiveId);
        Assert.Equal(baseline.ModelId, registry.PreviousId);

        await Assert.ThrowsAsync<ModelActivationContextChangedException>(() =>
            registry.ConfirmActivationAsync(candidate.ModelId, expectedActiveId: null));
        Assert.Equal(before, File.ReadAllText(pointerPath));

        var renewedWarning = await Assert.ThrowsAsync<ModelActivationConfirmationRequiredException>(
            () => registry.ActivateAsync(candidate.ModelId, manual: true));
        Assert.Equal(replacement.ModelId, renewedWarning.ExpectedActiveId);
        Assert.True(await registry.ConfirmActivationAsync(renewedWarning.CandidateId!, renewedWarning.ExpectedActiveId));
        Assert.Equal(candidate.ModelId, registry.ActiveId);
    }

    [Fact]
    public async Task RetainsFivePriorActiveIds()
    {
        var registryRoot = Path.Combine(_root, "registry");
        using var registry = new ModelGenerationRegistry(registryRoot);
        for (var step = 1; step <= ModelGenerationRegistry.PriorGenerationLimit + 2; step++)
        {
            var generation = await registry.RegisterAsync(Source("same.onnx", step / 10.0));
            Assert.True(await registry.ActivateAsync(generation.ModelId));
        }
        using var pointer = JsonDocument.Parse(File.ReadAllText(Path.Combine(registryRoot, "active.json")));
        Assert.Equal(ModelGenerationRegistry.PriorGenerationLimit,
            pointer.RootElement.GetProperty("history").GetArrayLength());
    }

    [Fact]
    public async Task RejectsMissingAndAlteredSidecars_AndRevisionMismatch()
    {
        using var registry = new ModelGenerationRegistry(Path.Combine(_root, "registry"));
        var missing = Source("missing.onnx", 0.3);
        File.Delete(missing + ".metrics.json");
        await Assert.ThrowsAsync<FileNotFoundException>(() => registry.RegisterAsync(missing));

        var first = await registry.RegisterAsync(Source("first.onnx", 0.4));
        Assert.True(await registry.ActivateAsync(first.ModelId));
        var otherRevision = await registry.RegisterAsync(Source("other.onnx", 0.8,
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ActivateAsync(otherRevision.ModelId));
        Assert.Equal(first.ModelId, registry.ActiveId);

        var candidate = await registry.RegisterAsync(Source("candidate.onnx", 0.7));
        var sidecar = Path.Combine(_root, "registry", candidate.ModelId, candidate.ModelFile + ".metrics.json");
        File.AppendAllText(sidecar, " ");
        await Assert.ThrowsAsync<InvalidDataException>(() => registry.ActivateAsync(candidate.ModelId));
        Assert.Equal(first.ModelId, registry.ActiveId);
    }

    [Fact]
    public async Task RegisterRejectsFixedNormalizationWithoutScalerReference()
    {
        using var registry = new ModelGenerationRegistry(Path.Combine(_root, "registry"));
        var model = Source("missing_scaler.onnx", 0.4, fixture:
            Path.Combine("Assets", "trend_predictor_missing_scaler.onnx"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.RegisterAsync(model));
    }

    [Fact]
    public async Task RegisterAcceptsCompleteFixedScalerAndLegacyModel()
    {
        using var registry = new ModelGenerationRegistry(Path.Combine(_root, "registry"));
        var fixedModel = Source("trend_predictor_fixed_scaler.onnx", 0.4, fixture:
            Path.Combine("Assets", "trend_predictor_fixed_scaler.onnx"));
        var generation = await registry.RegisterAsync(fixedModel);
        Assert.Contains(generation.RequiredSidecars, sidecar => sidecar.Kind == "scaler");
        Assert.True(await registry.ActivateAsync(generation.ModelId));
        var legacy = await registry.RegisterAsync(Source("legacy.onnx", 0.5));
        Assert.DoesNotContain(legacy.RequiredSidecars, sidecar => sidecar.Kind == "scaler");
    }

    [Fact]
    public async Task FixedPreprocessingLagsAndClipProduceDistinctContractHashesAtEqualWidth()
    {
        using var registry = new ModelGenerationRegistry(Path.Combine(_root, "registry"));
        async Task<ModelGenerationManifest> Register(string name) => await registry.RegisterAsync(
            Source(name, 0.4, fixture: Path.Combine("Assets", name)));
        var lag1 = await Register("trend_predictor_fixed_lag1_clip3.onnx");
        var lag2 = await Register("trend_predictor_fixed_lag2_clip3.onnx");
        var clip4 = await Register("trend_predictor_fixed_lag1_clip4.onnx");
        Assert.All(new[] { lag1, lag2, clip4 }, m => Assert.Equal(ModelGenerationRegistry.SchemaVersion, m.SchemaVersion));
        Assert.NotEqual(lag1.ContractHash, lag2.ContractHash);
        Assert.NotEqual(lag1.ContractHash, clip4.ContractHash);
        Assert.NotEqual(lag1.ModelId, lag2.ModelId);
        Assert.NotEqual(lag1.ModelId, clip4.ModelId);
    }

    [Theory]
    [InlineData("timeframe", "\"99\"")]
    [InlineData("timeframe", "\"quarterly\"")]
    [InlineData("horizon", "0")]
    [InlineData("fold_n", "0.5")]
    [InlineData("fold_n", "0")]
    [InlineData("n_splits", "2.5")]
    [InlineData("fold_macro_f1", "1.2")]
    [InlineData("target_type", "\"unsupported\"")]
    public async Task RegistrationRejectsInvalidEvaluationSemantics(string property, string value)
    {
        using var registry = new ModelGenerationRegistry(Path.Combine(_root, "registry"));
        var source = Source("model.onnx", 0.4);
        var metricPath = source + ".metrics.json";
        var report = JsonNode.Parse(File.ReadAllText(metricPath))!;
        report[property] = JsonNode.Parse(value);
        File.WriteAllText(metricPath, report.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => registry.RegisterAsync(source));
        Assert.Null(registry.ActiveId);
        Assert.Empty(registry.List());
    }

    [Theory]
    [InlineData("trend_predictor_unknown_feature_mode.onnx")]
    [InlineData("trend_predictor_invalid_composed_spec.onnx")]
    [InlineData("trend_predictor_duplicate_classes.onnx")]
    [InlineData("trend_predictor_whitespace_classes.onnx")]
    [InlineData("trend_predictor_fixed_batch_two.onnx")]
    [InlineData("trend_predictor_double_input.onnx")]
    [InlineData("trend_predictor_double_output.onnx")]
    public async Task RegistrationRejectsModelsOutsideRuntimeLabelTensorAndBatchContract(string fixture)
    {
        using var registry = new ModelGenerationRegistry(Path.Combine(_root, "registry"));
        var source = Source("model.onnx", 0.4, fixture: Path.Combine("Assets", fixture));
        await Assert.ThrowsAsync<InvalidDataException>(() => registry.RegisterAsync(source));
        Assert.Empty(registry.List());
    }

    [Theory]
    [InlineData("trend_predictor_goodmeta.onnx")]
    [InlineData("trend_predictor_fixed_batch_one.onnx")]
    [InlineData("trend_predictor_legacy_composed.onnx")]
    public async Task RegistrationAndRestartAcceptRuntimeCompatibleDynamicOrBatchOneModel(string fixture)
    {
        var registryRoot = Path.Combine(_root, "registry");
        ModelGenerationManifest generation;
        using (var registry = new ModelGenerationRegistry(registryRoot))
        {
            generation = await registry.RegisterAsync(Source("model.onnx", 0.4,
                fixture: Path.Combine("Assets", fixture)));
            Assert.True(await registry.ActivateAsync(generation.ModelId));
        }
        using var reopened = new ModelGenerationRegistry(registryRoot);
        Assert.Equal(generation.ModelId, reopened.ActiveId);
    }

    [Theory]
    [InlineData("trend_predictor_unknown_feature_mode.onnx")]
    [InlineData("trend_predictor_invalid_composed_spec.onnx")]
    [InlineData("trend_predictor_duplicate_classes.onnx")]
    [InlineData("trend_predictor_whitespace_classes.onnx")]
    [InlineData("trend_predictor_fixed_batch_two.onnx")]
    [InlineData("trend_predictor_double_input.onnx")]
    [InlineData("trend_predictor_double_output.onnx")]
    [InlineData("trend_predictor_fixed_batch_one.onnx")]
    public async Task RestartValidatesHashConsistentModelsAgainstRuntimeContract(string fixture)
    {
        var registryRoot = Path.Combine(_root, "registry");
        ModelGenerationManifest original;
        using (var registry = new ModelGenerationRegistry(registryRoot))
        {
            original = await registry.RegisterAsync(Source("model.onnx", 0.4));
            Assert.True(await registry.ActivateAsync(original.ModelId));
        }
        var directory = Path.Combine(registryRoot, original.ModelId);
        var modelPath = Path.Combine(directory, original.ModelFile);
        File.Copy(Path.Combine("Assets", fixture), modelPath, overwrite: true);
        string classOrder;
        using (var session = new InferenceSession(modelPath))
            classOrder = session.ModelMetadata.CustomMetadataMap[PredictionModelMetadata.ClassOrderKey];
        var modelHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(modelPath))).ToLowerInvariant();
        var metricsPath = modelPath + ".metrics.json";
        var metrics = JsonNode.Parse(File.ReadAllText(metricsPath))!;
        metrics["scored_model_sha256"] = modelHash;
        metrics["class_order"] = classOrder;
        metrics["outer_folds"]![2]!["scored_model_sha256"] = modelHash;
        File.WriteAllText(metricsPath, metrics.ToJsonString());
        var metricsHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(metricsPath))).ToLowerInvariant();
        var sidecars = original.RequiredSidecars.Select(sidecar => sidecar.Kind == "metrics"
            ? sidecar with { Hash = metricsHash } : sidecar).ToArray();
        var contractHash = LegacyContractHash(modelPath, original with { ClassOrder = classOrder });
        var id = LegacyModelId(modelPath, sidecars, contractHash);
        var invalid = original with
        {
            SchemaVersion = 1, ModelId = id, ModelHash = modelHash,
            RequiredSidecars = sidecars, ContractHash = contractHash, ClassOrder = classOrder,
        };
        var movedDirectory = Path.Combine(registryRoot, id);
        Directory.Move(directory, movedDirectory);
        File.WriteAllText(Path.Combine(movedDirectory, "manifest.json"),
            JsonSerializer.Serialize(invalid, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        File.WriteAllText(Path.Combine(registryRoot, "active.json"),
            JsonSerializer.Serialize(new { activeId = id, history = Array.Empty<string>() }));
        if (fixture == "trend_predictor_fixed_batch_one.onnx")
        {
            using var reopened = new ModelGenerationRegistry(registryRoot);
            Assert.Equal(id, reopened.ActiveId);
        }
        else
            Assert.Throws<InvalidDataException>(() => new ModelGenerationRegistry(registryRoot));
    }

    [Theory]
    [InlineData("timeframe", "99")]
    [InlineData("targetType", "unsupported")]
    [InlineData("horizon", 0)]
    [InlineData("score", -1.0)]
    [InlineData("score", 1.2)]
    public async Task RestartRejectsParseableButInvalidActiveManifest(string property, object value)
    {
        var registryPath = Path.Combine(_root, "registry");
        ModelGenerationManifest generation;
        using (var registry = new ModelGenerationRegistry(registryPath))
        {
            generation = await registry.RegisterAsync(Source("model.onnx", 0.4));
            Assert.True(await registry.ActivateAsync(generation.ModelId));
        }
        var manifestPath = Path.Combine(registryPath, generation.ModelId, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        manifest[property] = JsonSerializer.SerializeToNode(value);
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        Assert.Throws<InvalidDataException>(() => new ModelGenerationRegistry(registryPath));
    }

    [Theory]
    [InlineData("fold_n", 0.5)]
    [InlineData("fold_macro_f1", 1.2)]
    public async Task RestartRejectsSemanticallyInvalidButHashConsistentEvaluation(string property, double value)
    {
        var registryPath = Path.Combine(_root, "registry");
        ModelGenerationManifest generation;
        using (var registry = new ModelGenerationRegistry(registryPath))
        {
            generation = await registry.RegisterAsync(Source("model.onnx", 0.4));
            Assert.True(await registry.ActivateAsync(generation.ModelId));
        }
        var directory = Path.Combine(registryPath, generation.ModelId);
        var metricPath = Path.Combine(directory, generation.ModelFile + ".metrics.json");
        var report = JsonNode.Parse(File.ReadAllText(metricPath))!;
        report[property] = value;
        if (property == "fold_macro_f1")
            report["outer_folds"]![2]!["fold_macro_f1"] = value;
        File.WriteAllText(metricPath, report.ToJsonString());
        var metricHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(metricPath))).ToLowerInvariant();
        var sidecars = generation.RequiredSidecars.Select(sidecar => sidecar.Kind == "metrics"
            ? sidecar with { Hash = metricHash } : sidecar).ToArray();
        var updatedId = LegacyModelId(Path.Combine(directory, generation.ModelFile), sidecars, generation.ContractHash);
        var updated = generation with
        {
            ModelId = updatedId,
            RequiredSidecars = sidecars,
            Score = property == "fold_macro_f1" ? value : generation.Score,
        };
        var updatedDirectory = Path.Combine(registryPath, updatedId);
        Directory.Move(directory, updatedDirectory);
        File.WriteAllText(Path.Combine(updatedDirectory, "manifest.json"),
            JsonSerializer.Serialize(updated, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        File.WriteAllText(Path.Combine(registryPath, "active.json"),
            JsonSerializer.Serialize(new { activeId = updatedId, history = Array.Empty<string>() }));
        Assert.Throws<InvalidDataException>(() => new ModelGenerationRegistry(registryPath));
    }

    [Theory]
    [InlineData("model.onnx", null)]
    [InlineData("trend_predictor_fixed_scaler.onnx", "Assets/trend_predictor_fixed_scaler.onnx")]
    [InlineData("trend_predictor_legacy_composed.onnx", "Assets/trend_predictor_legacy_composed.onnx")]
    public async Task ReopensAndActivatesHistoricalSchemaOneGenerationWithoutChangingItsIdentity(
        string name, string? fixture)
    {
        var registryPath = Path.Combine(_root, "registry");
        ModelGenerationManifest current;
        using (var registry = new ModelGenerationRegistry(registryPath))
            current = await registry.RegisterAsync(Source(name, 0.4, fixture: fixture));
        var oldHash = LegacyContractHash(Path.Combine(registryPath, current.ModelId, current.ModelFile), current);
        var oldId = LegacyModelId(Path.Combine(registryPath, current.ModelId, current.ModelFile),
            current.RequiredSidecars, oldHash);
        var old = current with { SchemaVersion = 1, ContractHash = oldHash, ModelId = oldId };
        var oldDirectory = Path.Combine(registryPath, oldId);
        Directory.Move(Path.Combine(registryPath, current.ModelId), oldDirectory);
        File.WriteAllText(Path.Combine(oldDirectory, "manifest.json"),
            JsonSerializer.Serialize(old, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using (var reopened = new ModelGenerationRegistry(registryPath))
        {
            Assert.Equal(oldId, Assert.Single(reopened.List()).ModelId);
            Assert.True(await reopened.ActivateAsync(oldId));
        }
        using var activeReopened = new ModelGenerationRegistry(registryPath);
        Assert.Equal(oldId, activeReopened.ActiveId);
        Assert.Equal(1, activeReopened.GetManifest(oldId)!.SchemaVersion);
    }

    private static string LegacyContractHash(string path, ModelGenerationManifest manifest)
    {
        using var session = new InferenceSession(path);
        var metadata = session.ModelMetadata.CustomMetadataMap;
        var contract = JsonSerializer.Serialize(new
        {
            featureMode = metadata[PredictionModelMetadata.FeatureModeKey],
            windowSize = int.Parse(metadata[PredictionModelMetadata.WindowSizeKey]),
            channels = metadata.GetValueOrDefault("channels") ?? "",
            channelOrder = metadata.GetValueOrDefault("channel_order") ?? "",
            featureSpec = metadata.GetValueOrDefault(PredictionModelMetadata.FeatureSpecKey) ?? "",
            neutralThreshold = metadata.GetValueOrDefault("neutral_threshold") ?? "",
            normalization = metadata.GetValueOrDefault("normalization") ?? "",
            priceAdjustment = metadata.GetValueOrDefault("price_adjustment") ?? "",
            scalerRef = metadata.GetValueOrDefault(PredictionModelMetadata.ScalerReferenceKey) ?? "",
            target = manifest.TargetType, horizon = manifest.Horizon,
            timeframe = manifest.Timeframe, classOrder = manifest.ClassOrder,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contract))).ToLowerInvariant();
    }

    private static string LegacyModelId(string path, System.Collections.Generic.IEnumerable<ModelSidecar> sidecars,
        string contractHash)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        digest.AppendData(File.ReadAllBytes(path));
        foreach (var sidecar in sidecars.OrderBy(s => s.File, StringComparer.Ordinal))
            digest.AppendData(Convert.FromHexString(sidecar.Hash));
        digest.AppendData(Convert.FromHexString(contractHash));
        return Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
    }

    [Fact]
    public async Task PointerPersistenceFailureLeavesActiveUnchanged()
    {
        var registryRoot = Path.Combine(_root, "registry");
        var saves = 0;
        using var registry = new ModelGenerationRegistry(registryRoot, async (path, pointer) =>
        {
            if (++saves == 2) throw new IOException("save failed");
            await StockAnalyzer.Core.Common.AtomicJsonFile.SaveAsync(path, pointer,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        });
        var first = await registry.RegisterAsync(Source("model.onnx", 0.4));
        await registry.ActivateAsync(first.ModelId);
        var candidate = await registry.RegisterAsync(Source("model.onnx", 0.5));
        await Assert.ThrowsAsync<IOException>(() => registry.ActivateAsync(candidate.ModelId));
        Assert.Equal(first.ModelId, registry.ActiveId);
        using var persisted = JsonDocument.Parse(File.ReadAllText(Path.Combine(registryRoot, "active.json")));
        Assert.Equal(first.ModelId, persisted.RootElement.GetProperty("activeId").GetString());
    }

    [Fact]
    public async Task DisposeWaitsForPointerCommit_ThenPreventsFurtherPublication()
    {
        var registryRoot = Path.Combine(_root, "registry");
        var saveEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var saveCount = 0;
        using var registry = new ModelGenerationRegistry(registryRoot, async (path, pointer) =>
        {
            if (Interlocked.Increment(ref saveCount) == 2)
            {
                saveEntered.TrySetResult(true);
                await releaseSave.Task;
            }
            await StockAnalyzer.Core.Common.AtomicJsonFile.SaveAsync(path, pointer,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        });
        var first = await registry.RegisterAsync(Source("model.onnx", 0.4));
        Assert.True(await registry.ActivateAsync(first.ModelId));
        var second = await registry.RegisterAsync(Source("model.onnx", 0.6));
        var activation = registry.ActivateAsync(second.ModelId);
        await saveEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var shutdownStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdown = Task.Run(() =>
        {
            shutdownStarted.TrySetResult(true);
            registry.Dispose();
        });
        try
        {
            await shutdownStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(shutdown.IsCompleted);
        }
        finally { releaseSave.TrySetResult(true); }
        Assert.True(await activation.WaitAsync(TimeSpan.FromSeconds(10)));
        await shutdown.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(second.ModelId, registry.ActiveId);
        using var pointer = JsonDocument.Parse(File.ReadAllText(Path.Combine(registryRoot, "active.json")));
        Assert.Equal(second.ModelId, pointer.RootElement.GetProperty("activeId").GetString());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => registry.ActivateAsync(first.ModelId,
            manual: true, confirmLowerScore: true));
    }

    [Fact]
    public async Task RestartRejectsTraversingOrCorruptActiveManifest()
    {
        var registryPath = Path.Combine(_root, "registry");
        string modelId;
        using (var registry = new ModelGenerationRegistry(registryPath))
        {
            var candidate = await registry.RegisterAsync(Source("model.onnx", 0.5));
            modelId = candidate.ModelId;
            await registry.ActivateAsync(modelId);
        }
        var manifest = Path.Combine(registryPath, modelId, "manifest.json");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("model.onnx", "../model.onnx"));
        Assert.Throws<InvalidDataException>(() => new ModelGenerationRegistry(registryPath));
    }

    public void Dispose()
    {
        for (var attempt = 1; attempt <= CleanupAttempts; attempt++)
        {
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
                return;
            }
            catch (IOException) when (attempt < CleanupAttempts)
            {
                Thread.Sleep(CleanupRetryDelayMilliseconds);
            }
        }
    }
}
