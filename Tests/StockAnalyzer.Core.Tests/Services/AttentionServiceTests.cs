using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Moq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.Models.Training;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// T24b attention computation against the Python reference (<c>Tests/AttentionParity.json</c>): the fixture model and its sidecar are written by the production
/// Python code and the service reproduces the attention of the newest bar from raw candles, through the real generation registry.
/// </summary>
public sealed class AttentionServiceTests : IDisposable
{
    private const double Tolerance = 1e-4;
    private const int Window = 8;
    private const string Revision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly string Fixture = Path.Combine("Assets", "attention_fixture.onnx");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_attention_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        TestDirectory.Delete(_root);   // retried: a child process of another test can hold an inherited model handle for a moment
    }

    public static IEnumerable<object[]> CaseNames()
    {
        using var parity = AttentionContractTests.Parity();
        foreach (var item in parity.RootElement.GetProperty("cases").EnumerateArray()) yield return new object[] { item.GetProperty("name").GetString()! };
    }

    private static PredictionServiceTests.TestSettings Settings(int window = Window) => new(predictionWindowSize: window);

    private static CandleData[] Candles(JsonElement item) => item.GetProperty("candles").EnumerateArray().Select(row =>
    {
        var c = row.EnumerateArray().ToArray();
        return new CandleData(DateTime.ParseExact(c[0].GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture), (decimal)c[1].GetDouble(),
            (decimal)c[2].GetDouble(), (decimal)c[3].GetDouble(), (decimal)c[4].GetDouble(), c[5].GetInt64());
    }).ToArray();

    private static JsonElement Case(JsonDocument parity, string name) =>
        parity.RootElement.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

    /// <summary>A source model (and optionally its attention sidecar) with the independent-evaluation evidence the registry demands.</summary>
    private string Source(bool withSidecar)
    {
        var directory = Path.Combine(_root, "source_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var model = Path.Combine(directory, "attention_fixture.onnx");
        File.Copy(Fixture, model);
        if (withSidecar) File.Copy(Fixture + AttentionContract.SidecarSuffix, model + AttentionContract.SidecarSuffix);
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

    private async Task<(ModelGenerationRegistry Registry, ModelGenerationManifest Manifest)> Registered(bool withSidecar, string name = "registry")
    {
        var registry = new ModelGenerationRegistry(Path.Combine(_root, name));
        var manifest = await registry.RegisterAsync(Source(withSidecar));
        return (registry, manifest);
    }

    private static AttentionRequestKey Key(string modelId, CandleData[] candles, string timeframe = "Daily") =>
        new(modelId, "SYM", timeframe, candles[^1].Timestamp, 1);

    /// <summary>A registry stub that hands out the fixture folder with a chosen manifest (for the refusal cases the real registry cannot produce).</summary>
    private (Mock<IModelGenerationRegistry> Registry, string Model) Stub(string modelHash, Action<string> changeFolder = null, string timeframe = "daily")
    {
        var folder = Path.Combine(_root, "stub_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var model = Path.Combine(folder, "attention_fixture.onnx");
        File.Copy(Fixture, model);
        File.Copy(Fixture + AttentionContract.SidecarSuffix, model + AttentionContract.SidecarSuffix);
        changeFolder?.Invoke(model);
        var manifest = new ModelGenerationManifest(ModelGenerationRegistry.SchemaVersion, "stub", "attention_fixture.onnx", modelHash,
            Array.AsReadOnly(new[] { new ModelSidecar("attention_fixture.onnx.metrics.json", new string('b', 64), "metrics") }), new string('c', 64), Revision,
            "classification", timeframe, 3, "Up,Down,Neutral", 0.4);
        var registry = new Mock<IModelGenerationRegistry>();
        registry.Setup(r => r.Acquire("stub")).Returns(() => new ModelGenerationLease("stub", model, () => { }));
        registry.Setup(r => r.GetManifest("stub")).Returns(manifest);
        return (registry, model);
    }

    private static string FixtureHash() => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Fixture))).ToLowerInvariant();

    // --- Reproduction of the Python reference ----------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task Compute_MatchesPythonAttention(string name)
    {
        using var parity = AttentionContractTests.Parity();
        var item = Case(parity, name);
        var expected = item.GetProperty("expected");
        var candles = Candles(item);
        var (registry, manifest) = await Registered(withSidecar: true);
        using (registry)
        {
            var service = new AttentionService(registry, Settings(), new MLDataProcessor());
            var key = Key(manifest.ModelId, candles);
            var result = await service.ComputeAsync(key, candles);

            Assert.Equal(AttentionStatus.Ready, result.Status);
            var snapshot = result.Snapshot!;
            Assert.Equal(key, snapshot.Key);
            Assert.Equal(parity.RootElement.GetProperty("model").GetProperty("heads").GetInt32(), snapshot.Heads);
            Assert.Equal(candles.Skip(candles.Length - Window).Select(c => c.Timestamp), snapshot.Timestamps);
            var weights = expected.GetProperty("weights").EnumerateArray().Select(v => v.GetSingle()).ToArray();
            var relative = expected.GetProperty("relative").EnumerateArray().Select(v => v.GetSingle()).ToArray();
            for (int i = 0; i < Window; i++)
            {
                Assert.True(Math.Abs(weights[i] - snapshot.Weights[i]) <= Tolerance, $"weights[{i}]: expected {weights[i]}, actual {snapshot.Weights[i]}");
                Assert.True(Math.Abs(relative[i] - snapshot.Relative[i]) <= Tolerance, $"relative[{i}]: expected {relative[i]}, actual {snapshot.Relative[i]}");
            }

            Assert.Equal(1f, snapshot.Relative.Max());   // 1.0 = the most attended bar of this window
            Assert.InRange(snapshot.Weights.Sum(), 1f - 1e-4f, 1f + 1e-4f);   // a softmax row (heads averaged) still sums to 1
        }
    }

    [Fact]
    public async Task Compute_AfterTheFeatureModeChanged_RebuildsTheContext_InsteadOfReusingAStaleOne()
    {
        using var parity = AttentionContractTests.Parity();
        var candles = Candles(Case(parity, "newest_bar_of_a_short_history"));
        var (registry, manifest) = await Registered(withSidecar: true);
        using (registry)
        {
            var settings = Settings();
            var service = new AttentionService(registry, settings, new MLDataProcessor());
            var key = Key(manifest.ModelId, candles);
            Assert.Equal(AttentionStatus.Ready, (await service.ComputeAsync(key, candles)).Status);

            settings.SetPredictionFeatureMode(PredictionFeatureMode.LogReturn);   // 1 channel per bar: the 5-channel sidecar must refuse it
            var changed = await service.ComputeAsync(key, candles);
            Assert.Equal(AttentionStatus.Unsupported, changed.Status);

            settings.SetPredictionFeatureMode(PredictionFeatureMode.OhlcvMinMax);
            Assert.Equal(AttentionStatus.Ready, (await service.ComputeAsync(key, candles)).Status);
        }
    }

    [Fact]
    public async Task Compute_ReadsOnlyTheLastWindow_NotTheOlderBars()
    {
        using var parity = AttentionContractTests.Parity();
        var candles = Candles(Case(parity, "only_the_last_window_matters"));
        var altered = candles.Select((c, i) => i < candles.Length - Window ? c with { Close = c.Close * 3m, Open = c.Open * 3m, High = c.High * 3m, Low = c.Low * 3m } : c).ToArray();
        var (registry, manifest) = await Registered(withSidecar: true);
        using (registry)
        {
            var service = new AttentionService(registry, Settings(), new MLDataProcessor());
            var left = (await service.ComputeAsync(Key(manifest.ModelId, candles), candles)).Snapshot!;
            var right = (await service.ComputeAsync(Key(manifest.ModelId, altered), altered)).Snapshot!;
            Assert.Equal(left.Weights, right.Weights);
        }
    }

    // --- The registry carries the sidecar; its absence is "unsupported", never an error ----------------------------------------------------

    [Fact]
    public async Task Compute_AGenerationWithoutASidecar_IsUnsupported()
    {
        using var parity = AttentionContractTests.Parity();
        var candles = Candles(Case(parity, "newest_bar_of_a_short_history"));
        var (registry, manifest) = await Registered(withSidecar: false);
        using (registry)
        {
            var result = await new AttentionService(registry, Settings(), new MLDataProcessor()).ComputeAsync(Key(manifest.ModelId, candles), candles);
            Assert.Equal(AttentionStatus.Unsupported, result.Status);
            Assert.Equal(AttentionContract.UnsupportedReason.NoSidecar, result.Reason);
            Assert.Null(result.Snapshot);
        }
    }

    [Fact]
    public async Task Compute_AnUnknownGeneration_AndAnotherTimeframe_AreUnsupported()
    {
        using var parity = AttentionContractTests.Parity();
        var candles = Candles(Case(parity, "newest_bar_of_a_short_history"));
        var (registry, manifest) = await Registered(withSidecar: true);
        using (registry)
        {
            var service = new AttentionService(registry, Settings(), new MLDataProcessor());
            Assert.Equal(AttentionContract.UnsupportedReason.NoModel, (await service.ComputeAsync(Key(new string('f', 64), candles), candles)).Reason);
            Assert.Equal(AttentionContract.UnsupportedReason.Timeframe, (await service.ComputeAsync(Key(manifest.ModelId, candles, "Weekly"), candles)).Reason);
        }
    }

    [Fact]
    public async Task Compute_ASidecarOfAnotherParent_IsUnsupported_AndNothingIsRun()
    {
        using var parity = AttentionContractTests.Parity();
        var candles = Candles(Case(parity, "newest_bar_of_a_short_history"));
        var (registry, _) = Stub(new string('d', 64));
        var result = await new AttentionService(registry.Object, Settings(), new MLDataProcessor()).ComputeAsync(Key("stub", candles), candles);
        Assert.Equal(AttentionStatus.Unsupported, result.Status);
        Assert.Contains("parent", result.Detail);
    }

    [Fact]
    public async Task Compute_ASidecarWithAnUnknownMask_OrAnotherWindow_IsUnsupported()
    {
        using var parity = AttentionContractTests.Parity();
        var candles = Candles(Case(parity, "newest_bar_of_a_short_history"));
        var (maskRegistry, _) = Stub(FixtureHash(), model =>
        {
            var path = model + AttentionContract.SidecarSuffix;
            var bytes = File.ReadAllBytes(path);
            var text = Encoding.ASCII.GetBytes(AttentionContract.Mask);
            int at = IndexOf(bytes, text);
            Assert.True(at >= 0, "the mask value must be present in the sidecar");
            bytes[at + text.Length - 1] = (byte)'X';   // same length: still a valid file, but an unknown mask
            File.WriteAllBytes(path, bytes);
        });
        var masked = await new AttentionService(maskRegistry.Object, Settings(), new MLDataProcessor()).ComputeAsync(Key("stub", candles), candles);
        Assert.Equal(AttentionContract.UnsupportedReason.NoSidecar, masked.Reason);

        var (windowRegistry, _) = Stub(FixtureHash());
        var otherWindow = await new AttentionService(windowRegistry.Object, Settings(window: Window + 1), new MLDataProcessor()).ComputeAsync(Key("stub", candles), candles);
        Assert.Equal(AttentionStatus.Unsupported, otherWindow.Status);
    }

    // --- Failures: present but unusable -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Compute_ACorruptSidecar_IsAFailureNotAnException()
    {
        using var parity = AttentionContractTests.Parity();
        var candles = Candles(Case(parity, "newest_bar_of_a_short_history"));
        var (registry, _) = Stub(FixtureHash(), model => File.WriteAllBytes(model + AttentionContract.SidecarSuffix, new byte[] { 1, 2, 3, 4 }));
        var result = await new AttentionService(registry.Object, Settings(), new MLDataProcessor()).ComputeAsync(Key("stub", candles), candles);
        Assert.Equal(AttentionStatus.Failed, result.Status);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task Compute_ATooShortOrUnorderedHistory_Fails()
    {
        using var parity = AttentionContractTests.Parity();
        var candles = Candles(Case(parity, "newest_bar_of_a_short_history"));
        var (registry, manifest) = await Registered(withSidecar: true);
        using (registry)
        {
            var service = new AttentionService(registry, Settings(), new MLDataProcessor());
            var tooShort = candles[^(Window - 1)..];
            Assert.Equal(AttentionStatus.Failed, (await service.ComputeAsync(Key(manifest.ModelId, tooShort), tooShort)).Status);
            var unordered = candles.ToArray();
            (unordered[^2], unordered[^3]) = (unordered[^3], unordered[^2]);
            Assert.Equal(AttentionStatus.Failed, (await service.ComputeAsync(Key(manifest.ModelId, unordered), unordered)).Status);
        }
    }

    [Fact]
    public async Task Compute_ACancelledToken_Throws()
    {
        using var parity = AttentionContractTests.Parity();
        var candles = Candles(Case(parity, "newest_bar_of_a_short_history"));
        var (registry, manifest) = await Registered(withSidecar: true);
        using (registry)
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new AttentionService(registry, Settings(), new MLDataProcessor()).ComputeAsync(Key(manifest.ModelId, candles), candles, cancelled.Token));
        }
    }

    // --- Shared-function proof: the registry change is additive ---------------------------------------------------------------------------

    [Fact]
    public async Task Registry_CarriesTheSidecar_WithoutChangingTheGenerationIdentity()
    {
        var (withRegistry, withManifest) = await Registered(withSidecar: true, "with");
        var (withoutRegistry, withoutManifest) = await Registered(withSidecar: false, "without");
        using (withRegistry)
        using (withoutRegistry)
        {
            Assert.Equal(withoutManifest.ModelId, withManifest.ModelId);                 // not part of the identity
            Assert.Equal(withoutManifest.RequiredSidecars.Select(s => s.Kind), withManifest.RequiredSidecars.Select(s => s.Kind));   // not a required sidecar
            using var lease = withRegistry.Acquire(withManifest.ModelId)!;
            Assert.True(File.Exists(lease.ModelPath + AttentionContract.SidecarSuffix));
            Assert.Equal(File.ReadAllBytes(Fixture + AttentionContract.SidecarSuffix), File.ReadAllBytes(lease.ModelPath + AttentionContract.SidecarSuffix));
            using var plain = withoutRegistry.Acquire(withoutManifest.ModelId)!;
            Assert.False(File.Exists(plain.ModelPath + AttentionContract.SidecarSuffix));
        }
    }

    [Fact]
    public async Task Registry_ARepeatedRegistration_AddsAMissingSidecarToTheExistingGeneration()
    {
        var registry = new ModelGenerationRegistry(Path.Combine(_root, "repeat"));
        using (registry)
        {
            var first = await registry.RegisterAsync(Source(withSidecar: false));
            using (var lease = registry.Acquire(first.ModelId)!) Assert.False(File.Exists(lease.ModelPath + AttentionContract.SidecarSuffix));
            var second = await registry.RegisterAsync(Source(withSidecar: true));
            Assert.Equal(first.ModelId, second.ModelId);
            using (var lease = registry.Acquire(first.ModelId)!) Assert.True(File.Exists(lease.ModelPath + AttentionContract.SidecarSuffix));
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }

        return -1;
    }
}
