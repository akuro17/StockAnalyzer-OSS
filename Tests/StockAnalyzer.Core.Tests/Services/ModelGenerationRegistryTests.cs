using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
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

    private string Source(string name, double score, string revision = Revision)
    {
        var directory = Path.Combine(_root, "source_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var model = Path.Combine(directory, name);
        File.Copy(_fixture, model);
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
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
