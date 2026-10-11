using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Moq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// The grouping key of the model list is the very value <see cref="EnsembleSettingsManager.ValidateGenerations"/> compares: generations with an equal key
/// can be saved as one ensemble, generations with different keys cannot.
/// </summary>
public sealed class EnsembleCompatibilityTests : IDisposable
{
    private const string Revision = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_ens_compat_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>Copies a fixture model next to metrics that carry the independent-evaluation evidence the registry demands.</summary>
    private string Source(string fixture, string name)
    {
        var directory = Path.Combine(_root, "source_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var model = Path.Combine(directory, name);
        File.Copy(Path.Combine("Assets", fixture), model);
        using var session = new InferenceSession(model);
        var metadata = session.ModelMetadata.CustomMetadataMap;
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(model))).ToLowerInvariant();
        var report = new
        {
            evaluation_status = "independent_outer", evaluation_revision = Revision, scored_model_sha256 = hash,
            target_type = metadata[PredictionModelMetadata.TargetTypeKey], timeframe = "daily",
            horizon = int.Parse(metadata[PredictionModelMetadata.HorizonKey]), class_order = metadata[PredictionModelMetadata.ClassOrderKey],
            fold_macro_f1 = 0.4, fold_is_holdout = 1.0, fold = 2.0, fold_n = 20.0, n_splits = 3.0,
            outer_folds = new[]
            {
                new { fold = 0.0, evaluation_revision = Revision, scored_model_sha256 = hash, fold_macro_f1 = 0.2 },
                new { fold = 1.0, evaluation_revision = Revision, scored_model_sha256 = hash, fold_macro_f1 = 0.3 },
                new { fold = 2.0, evaluation_revision = Revision, scored_model_sha256 = hash, fold_macro_f1 = 0.4 },
            },
        };
        File.WriteAllText(model + ".metrics.json", JsonSerializer.Serialize(report));
        return model;
    }

    private async Task<(ModelGenerationRegistry Registry, ModelGenerationManifest SameA, ModelGenerationManifest SameB, ModelGenerationManifest Other)> Registered()
    {
        var registry = new ModelGenerationRegistry(Path.Combine(_root, "registry"));
        var a = await registry.RegisterAsync(Source("ensemble_fixture_m01.onnx", "a.onnx"));
        var b = await registry.RegisterAsync(Source("ensemble_fixture_m02.onnx", "b.onnx"));
        var other = await registry.RegisterAsync(Source("attention_fixture.onnx", "other.onnx")); // horizon 3 instead of 5
        return (registry, a, b, other);
    }

    [Fact]
    public async Task GenerationsOfTheSameContractShareAKey_AndAnotherHorizonDoesNot()
    {
        var (registry, a, b, other) = await Registered();
        using (registry)
        {
            var keyA = EnsembleSettingsManager.Describe(a, registry);
            var keyB = EnsembleSettingsManager.Describe(b, registry);
            var keyOther = EnsembleSettingsManager.Describe(other, registry);

            Assert.NotNull(keyA);
            Assert.Equal(keyA!.Key, keyB!.Key);
            Assert.Equal(5, keyA.Horizon);
            Assert.Equal("daily", keyA.Timeframe);
            Assert.Equal("Down,Neutral,Up", keyA.Labels); // the label SET, in a fixed order
            Assert.Equal("0.005", keyA.NeutralThreshold);
            Assert.Equal("adjusted", keyA.PriceAdjustment);
            Assert.NotEqual(keyA.Key, keyOther!.Key);
            Assert.Equal(3, keyOther.Horizon);
            Assert.Same(keyA, EnsembleSettingsManager.Describe(a, registry)); // read once per generation
        }
    }

    [Fact]
    public async Task TheKeyAndTheEnsembleValidationAgree()
    {
        var (registry, a, b, other) = await Registered();
        using (registry)
        {
            EnsembleSettingsManager.ValidateGenerations(EnsembleSpec.Equal(new[] { a.ModelId, b.ModelId }), registry);

            var refused = Assert.Throws<InvalidOperationException>(() =>
                EnsembleSettingsManager.ValidateGenerations(EnsembleSpec.Equal(new[] { a.ModelId, other.ModelId }), registry));
            Assert.Equal(EnsembleValidationFailure.IncompatibleContract, EnsembleSettingsManager.GetValidationFailure(refused));
        }
    }

    [Fact]
    public void ModelsThatCanNeverBeMembers_HaveNoKey_WithoutTouchingTheRegistry()
    {
        var registry = new Mock<IModelGenerationRegistry>(MockBehavior.Strict);
        ModelGenerationManifest Manifest(string target, string classOrder) => new(ModelGenerationRegistry.SchemaVersion, new string('1', 64), "m.onnx",
            new string('2', 64), Array.AsReadOnly(new[] { new ModelSidecar("m.onnx.metrics.json", new string('3', 64), "metrics") }), new string('4', 64), Revision,
            target, "daily", 5, classOrder, 0.4);

        Assert.Null(EnsembleSettingsManager.Describe(Manifest("regression", "Up,Down,Neutral"), registry.Object));
        Assert.Null(EnsembleSettingsManager.Describe(Manifest("classification", "Up,Up,Neutral"), registry.Object));
        Assert.Null(EnsembleSettingsManager.Describe(Manifest("classification", " Up,Down"), registry.Object));
        registry.VerifyNoOtherCalls();
    }

    // --- Metadata of the compatibility key ------------------------------------------------------------------------------------------------

    private static ModelGenerationManifest ClassificationManifest(string id = "6") => new(ModelGenerationRegistry.SchemaVersion, new string(id[0], 64), "m.onnx",
        new string('2', 64), Array.AsReadOnly(new[] { new ModelSidecar("m.onnx.metrics.json", new string('3', 64), "metrics") }), new string('4', 64), Revision,
        "classification", "daily", 5, "Up,Down,Neutral", 0.4);

    private static Dictionary<string, string> Metadata(string neutral = "0.005", string price = "adjusted") => new()
    {
        [PredictionModelMetadata.NeutralThresholdKey] = neutral,
        [PredictionModelMetadata.PriceAdjustmentKey] = price,
    };

    [Theory]
    [InlineData(PredictionModelMetadata.NeutralThresholdKey)]
    [InlineData(PredictionModelMetadata.PriceAdjustmentKey)]
    public void AGenerationWithoutCompatibilityMetadata_IsNotAnEnsembleCandidate(string missingKey)
    {
        var metadata = Metadata();
        metadata.Remove(missingKey);

        var error = Assert.Throws<InvalidOperationException>(() => EnsembleSettingsManager.BuildCompatibility(ClassificationManifest(), metadata));

        Assert.Equal(EnsembleValidationFailure.IncompatibleContract, EnsembleSettingsManager.GetValidationFailure(error));
        Assert.Contains(missingKey, error.Message);
    }

    [Fact]
    public void ABlankCompatibilityValue_IsTreatedAsMissing()
    {
        Assert.Throws<InvalidOperationException>(() => EnsembleSettingsManager.BuildCompatibility(ClassificationManifest(), Metadata(neutral: " ")));
    }

    [Fact]
    public void TheKey_FollowsTheThresholdAndThePriceBasis_ButNotTheGenerationId()
    {
        var baseline = EnsembleSettingsManager.BuildCompatibility(ClassificationManifest("6"), Metadata());

        Assert.NotEqual(baseline.Key, EnsembleSettingsManager.BuildCompatibility(ClassificationManifest("6"), Metadata(neutral: "0.02")).Key);
        Assert.NotEqual(baseline.Key, EnsembleSettingsManager.BuildCompatibility(ClassificationManifest("6"), Metadata(price: "raw")).Key);
        Assert.Equal(baseline.Key, EnsembleSettingsManager.BuildCompatibility(ClassificationManifest("7"), Metadata()).Key);
        Assert.Equal(string.Join("\n", "daily", "5", "Down,Neutral,Up", "0.005", "adjusted"), baseline.Key);
    }

    [Fact]
    public void TheMetadataKeyNames_AreTheOnesThePythonTrainersWrite()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StockAnalyzer.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var source = File.ReadAllText(Path.Combine(directory!.FullName, "StockAnalyzer.Python", "training", "onnx_meta.py"));
        var start = source.IndexOf("CONTRACT_KEYS", StringComparison.Ordinal);
        var tuple = source.Substring(start, source.IndexOf(')', start) - start);

        Assert.Contains("\"" + PredictionModelMetadata.NeutralThresholdKey + "\"", tuple);
        Assert.Contains("\"" + PredictionModelMetadata.PriceAdjustmentKey + "\"", tuple);
    }

    [Fact]
    public void TheCompatibilityCache_NeverHoldsMoreThanItsCapacity()
    {
        var registry = new Mock<IModelGenerationRegistry>();
        registry.Setup(r => r.Acquire(It.IsAny<string>())).Returns(() => new ModelGenerationLease("fixture", Path.Combine("Assets", "trend_predictor_goodmeta.onnx"), () => { }));
        int distinct = EnsembleLimits.MaxCachedCompatibilities + 10;

        for (int i = 0; i < distinct; i++)
        {
            var id = i.ToString("x").PadLeft(64, '0');   // 64 hexadecimal characters, distinct per generation
            var manifest = ClassificationManifest() with { ModelId = id };
            Assert.NotNull(EnsembleSettingsManager.Describe(manifest, registry.Object));
        }

        Assert.True(EnsembleSettingsManager.CompatibilityCacheCount <= EnsembleLimits.MaxCachedCompatibilities);
        Assert.Equal(EnsembleLimits.MaxCachedCompatibilities, EnsembleSettingsManager.CompatibilityCacheCount);   // full: the oldest were evicted
    }

    [Fact]
    public void AnUnavailableGenerationHasNoKey_AndIsNotRememberedAsUnusable()
    {
        var registry = new Mock<IModelGenerationRegistry>();
        registry.Setup(r => r.Acquire(It.IsAny<string>())).Returns((ModelGenerationLease?)null);
        var manifest = new ModelGenerationManifest(ModelGenerationRegistry.SchemaVersion, new string('5', 64), "m.onnx", new string('2', 64),
            Array.AsReadOnly(new[] { new ModelSidecar("m.onnx.metrics.json", new string('3', 64), "metrics") }), new string('4', 64), Revision,
            "classification", "daily", 5, "Up,Down,Neutral", 0.4);

        Assert.Null(EnsembleSettingsManager.Describe(manifest, registry.Object));
        Assert.Null(EnsembleSettingsManager.Describe(manifest, registry.Object));
        registry.Verify(r => r.Acquire(manifest.ModelId), Times.Exactly(2)); // asked again, never cached as "unusable"
    }
}
