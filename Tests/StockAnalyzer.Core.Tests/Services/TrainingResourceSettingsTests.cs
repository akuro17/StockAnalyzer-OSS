using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class TrainingResourceSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sa_training_limits_" + Guid.NewGuid().ToString("N"));
    private string PathName => Path.Combine(_directory, TrainingResourceSettings.FileName);

    public TrainingResourceSettingsTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task MissingFileAndRoundTrip_PreserveNullAsInheritance()
    {
        var service = new TrainingResourceSettings(PathName);
        var owner = new object();
        Assert.Null(service.LoadError);
        Assert.Equal(new TrainingResourceOverrides(), service.Snapshot);
        Assert.True(service.TryAcquirePreview(owner));
        service.SetPreview(owner, new TrainingResourceOverrides(1, null, 10_000));
        await service.SaveAsync(owner);
        service.ReleasePreview(owner);

        var reloaded = new TrainingResourceSettings(PathName);
        Assert.Equal(new TrainingResourceOverrides(1, null, 10_000), reloaded.Snapshot);
        using var json = JsonDocument.Parse(File.ReadAllText(PathName));
        Assert.Equal(TrainingResourceSettings.SchemaVersion,
            json.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("max_tensor_size_mib").ValueKind);
    }

    [Fact]
    public void MissingFieldsAndNullValues_Inherit()
    {
        File.WriteAllText(PathName, "{\"schema_version\":1,\"max_channels\":null,\"max_samples\":1}");
        var service = new TrainingResourceSettings(PathName);
        Assert.Null(service.LoadError);
        Assert.Equal(new TrainingResourceOverrides(null, null, 1), service.Snapshot);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"schema_version\":2}")]
    [InlineData("{\"schema_version\":true}")]
    [InlineData("{\"schema_version\":1,\"max_channels\":true}")]
    [InlineData("{\"schema_version\":1,\"max_channels\":1.5}")]
    [InlineData("{\"schema_version\":1,\"max_channels\":1.0}")]
    [InlineData("{\"schema_version\":1,\"max_channels\":0}")]
    [InlineData("{\"schema_version\":1,\"max_tensor_size_mib\":257}")]
    [InlineData("{\"schema_version\":1,\"max_samples\":10001}")]
    public async Task InvalidFile_IsDiagnosedAndNeverOverwritten(string content)
    {
        File.WriteAllText(PathName, content);
        var service = new TrainingResourceSettings(PathName);
        var owner = new object();
        Assert.NotNull(service.LoadError);
        Assert.True(service.TryAcquirePreview(owner));
        service.SetPreview(owner, new TrainingResourceOverrides(2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(owner));
        Assert.Equal(content, File.ReadAllText(PathName));
    }

    [Fact]
    public async Task ExclusivePreviewAndFailedIo_RestoreOnlySavedValue()
    {
        var service = new TrainingResourceSettings(PathName);
        var first = new object();
        var second = new object();
        Assert.True(service.TryAcquirePreview(first));
        Assert.False(service.TryAcquirePreview(second));
        service.SetPreview(first, new TrainingResourceOverrides(12));
        Assert.Equal(12, service.Snapshot.MaxChannels);
        Assert.Equal(new TrainingResourceOverrides(), service.SavedSnapshot);
        service.ReleasePreview(first);
        Assert.Equal(new TrainingResourceOverrides(), service.Snapshot);
        Assert.True(service.TryAcquirePreview(second));
        service.ReleasePreview(second);

        Directory.CreateDirectory(PathName);
        Assert.True(service.TryAcquirePreview(first));
        service.SetPreview(first, new TrainingResourceOverrides(13));
        await Assert.ThrowsAnyAsync<IOException>(() => service.SaveAsync(first));
        Assert.Equal(new TrainingResourceOverrides(), service.SavedSnapshot);
        Assert.Equal(13, service.Snapshot.MaxChannels);
        service.ReleasePreview(first);
        Assert.Equal(new TrainingResourceOverrides(), service.Snapshot);
    }

    [Fact]
    public async Task ConcurrentSaves_RejectSecondWriter()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new TrainingResourceSettings(PathName, async (_, _) => await release.Task);
        var owner = new object();
        Assert.True(service.TryAcquirePreview(owner));
        service.SetPreview(owner, new TrainingResourceOverrides(14));
        var first = service.SaveAsync(owner);
        Assert.True(service.IsSaving);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(owner));
        Assert.Throws<InvalidOperationException>(() => service.SetPreview(owner, new TrainingResourceOverrides(15)));
        Assert.Throws<InvalidOperationException>(() => service.ReleasePreview(owner));
        release.SetResult();
        await first;
        Assert.Equal(14, service.SavedSnapshot.MaxChannels);
    }

    [Fact]
    public void ExplicitValidationAndChildEnvironment_KeepParentAndInheritedValues()
    {
        var config = Config(1, 2);
        TrainingResourceGuard.Validate(config, new TrainingResourceOverrides(2, 1));
        Assert.Throws<InvalidOperationException>(() =>
            TrainingResourceGuard.Validate(config, new TrainingResourceOverrides(1)));

        var exactMiB = Config(TrainingResourceOverrides.MiB /
            (TrainingResourceOverrides.Float32Bytes * 2), 2);
        TrainingResourceGuard.Validate(exactMiB, new TrainingResourceOverrides(null, 1));
        Assert.Throws<InvalidOperationException>(() => TrainingResourceGuard.Validate(
            Config(exactMiB.WindowSize + 1, 2), new TrainingResourceOverrides(null, 1)));

        var inherited = new ProcessStartInfo { UseShellExecute = false };
        var original = Environment.GetEnvironmentVariable("SA_MAX_COMPOSED_CHANNELS");
        inherited.Environment["SA_MAX_COMPOSED_CHANNELS"] = "inherited-channel-value";
        inherited.Environment["SA_MAX_COMPOSED_TENSOR_SIZE_MB"] = "inherited-tensor-value";
        var inheritedLimits = TrainingResourceGuard.Resolve(new TrainingResourceOverrides());
        TrainingResourceGuard.ApplyToChild(inherited, inheritedLimits);
        Assert.Equal(inheritedLimits.MaxChannels.ToString(), ChildValue(inherited, "SA_MAX_COMPOSED_CHANNELS"));
        Assert.Equal(inheritedLimits.MaxTensorSizeMiB.ToString(), ChildValue(inherited, "SA_MAX_COMPOSED_TENSOR_SIZE_MB"));
        var snapshot = new TrainingResourceOverrides(7, null, 23);
        var resolved = TrainingResourceGuard.Resolve(snapshot);
        var child = new ProcessStartInfo { UseShellExecute = false };
        child.Environment["SA_MAX_COMPOSED_CHANNELS"] = "inherited-channel-value";
        child.Environment["SA_MAX_COMPOSED_TENSOR_SIZE_MB"] = "inherited-tensor-value";
        TrainingResourceGuard.ApplyToChild(child, resolved);
        Assert.Equal("7", child.Environment["SA_MAX_COMPOSED_CHANNELS"]);
        Assert.Equal("23", child.Environment["SA_MAX_COMPOSED_BATCH_SAMPLES"]);
        Assert.Equal(resolved.MaxTensorSizeMiB.ToString(), ChildValue(child, "SA_MAX_COMPOSED_TENSOR_SIZE_MB"));
        Assert.Equal(resolved.MaxEvaluationFolds.ToString(), ChildValue(child, "SA_MAX_EVALUATION_FOLDS"));
        Assert.Equal(resolved.MaxFeatureLag.ToString(), ChildValue(child, "SA_MAX_FEATURE_LAG"));
        Assert.Equal(resolved.MaxRunDurationSeconds.ToString(), ChildValue(child, "SA_MAX_RUN_DURATION_SECONDS"));
        Assert.Equal(resolved.ContractVersion.ToString(), ChildValue(child, "SA_TRAINING_RESOURCE_CONTRACT_VERSION"));
        Assert.Equal(original, Environment.GetEnvironmentVariable("SA_MAX_COMPOSED_CHANNELS"));
    }

    [Fact]
    public void ExpandedLagsHonorConfiguredTensorAndChannelLimits()
    {
        var composed = Config(100_000, 1) with
        {
            FixedZScore = true,
            FeatureSpec = new FeatureSpec
            {
                Channels = new[] { new FeatureChannel { Kind = FeatureChannelKind.Price, Price = PriceType.Close } },
                Lags = new[] { 1, 2 },
            },
        };
        TrainingResourceGuard.Validate(composed, new TrainingResourceOverrides(null, 2));
        Assert.Throws<InvalidOperationException>(() => TrainingResourceGuard.Validate(
            composed, new TrainingResourceOverrides(null, 1)));
        Assert.Throws<InvalidOperationException>(() => TrainingResourceGuard.Validate(
            composed, new TrainingResourceOverrides(2, 2)));

        var raw = composed with { FeatureMode = PredictionFeatureMode.OhlcvMinMax, FeatureSpec = null,
            Lags = new[] { 1, 2 }, WindowSize = 20_000 };
        Assert.Throws<InvalidOperationException>(() => TrainingResourceGuard.Validate(
            raw, new TrainingResourceOverrides(null, 1)));
        TrainingResourceGuard.Validate(raw with { FixedZScore = false, Lags = Array.Empty<int>() },
            new TrainingResourceOverrides(null, 1));
    }

    private static TrainingJobConfig Config(int window, int channels)
    {
        var entries = new FeatureChannel[channels];
        for (var i = 0; i < channels; i++)
            entries[i] = new FeatureChannel { Kind = FeatureChannelKind.Price, Price = PriceType.Close };
        return new TrainingJobConfig
        {
            Symbols = new[] { "AAA" }, Architecture = "cnn", WindowSize = window,
            Horizon = 1, FeatureMode = PredictionFeatureMode.ComposedFeatures,
            FeatureSpec = new FeatureSpec { Channels = entries },
        };
    }

    private static string? ChildValue(ProcessStartInfo start, string key) =>
        start.Environment.TryGetValue(key, out var value) ? value : null;
}
