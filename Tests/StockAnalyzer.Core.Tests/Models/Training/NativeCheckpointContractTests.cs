using System;
using System.IO;
using StockAnalyzer.Core.Models.Training;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models.Training;

public sealed class NativeCheckpointContractTests
{
    private static TrainingJobConfig Config() => new()
    {
        Symbols = new[] { "X" }, Architecture = "lstm", WindowSize = 4, Horizon = 1,
        InitializationMode = TrainingInitializationMode.FineTune,
        CheckpointPath = "best.json", ParentModelId = new string('a', 64),
        FreezePaths = new[] { "lstm", "head" },
    };

    [Fact]
    public void TransferWire_RoundTripsModeAndOrderedFreezePaths()
    {
        var config = Config();
        config.Validate();
        var json = TrainingConfigJson.Serialize(config);
        Assert.Contains("\"initialization_mode\": \"fine_tune\"", json);
        var roundTrip = TrainingConfigJson.DeserializeConfig(json);
        Assert.Equal(config.InitializationMode, roundTrip.InitializationMode);
        Assert.Equal(config.FreezePaths, roundTrip.FreezePaths);
        Assert.Equal(config.ParentModelId, roundTrip.ParentModelId);
    }

    [Theory]
    [InlineData(TrainingFramework.TensorFlow, TrainingInitializationMode.FineTune)]
    [InlineData(TrainingFramework.LightGBM, TrainingInitializationMode.Resume)]
    public void UnsupportedFrameworkMode_FailsBeforeLaunch(TrainingFramework framework, TrainingInitializationMode mode)
        => Assert.Throws<InvalidOperationException>((Config() with { Framework = framework, InitializationMode = mode }).Validate);

    [Theory]
    [InlineData("model.onnx")]
    [InlineData("state.pt")]
    [InlineData("")]
    public void UnversionedOrOnnxInitialization_IsRejected(string path)
        => Assert.Throws<InvalidOperationException>((Config() with { CheckpointPath = path }).Validate);

    [Theory]
    [InlineData(" features")]
    [InlineData("features..0")]
    [InlineData("features/0")]
    [InlineData("*")]
    public void NonNormalizedFreezePath_IsRejected(string path)
        => Assert.Throws<InvalidOperationException>((Config() with { FreezePaths = new[] { path } }).Validate);

    [Fact]
    public void Resume_RejectsNewLineageOrFreezeControls()
    {
        Assert.Throws<InvalidOperationException>((Config() with { InitializationMode = TrainingInitializationMode.Resume }).Validate);
        (Config() with { InitializationMode = TrainingInitializationMode.Resume, ParentModelId = null,
                        FreezePaths = Array.Empty<string>(), CheckpointPath = "run.json" }).Validate();
    }

    [Fact]
    public void ResumeManifest_RejectsUnsafeRunIdentity()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"schema_version\":1,\"kind\":\"training_run\",\"run_id\":\"../unsafe\"}");
            Assert.Throws<InvalidDataException>(() => TrainingCheckpointContract.ReadResumeRunId(path));
            File.WriteAllText(path, "{\"schema_version\":1,\"kind\":\"training_run\",\"run_id\":\"original_run\"}");
            Assert.Equal("original_run", TrainingCheckpointContract.ReadResumeRunId(path));
        }
        finally { File.Delete(path); }
    }
}
