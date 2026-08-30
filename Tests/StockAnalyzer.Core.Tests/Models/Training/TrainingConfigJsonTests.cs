using System;
using System.Collections.Generic;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models.Training;

/// <summary>
/// Round-trip coverage for <see cref="TrainingConfigJson"/>, the shared wire contract read by
/// <c>run_training.py --config</c> and written into <c>Data/Experiments/&lt;run-id&gt;/</c> by
/// <see cref="StockAnalyzer.Core.Services.ExperimentLogService"/>. Deferred since Task 1 of the
/// ONNX training UI foundation sprint (see the round-trip serialization note in
/// Y:\Temp\sa_step_log_OnnxTrainingFoundation.md) and added here in Task 10.
/// </summary>
public class TrainingConfigJsonTests
{
    [Fact]
    public void TrainingJobConfig_RoundTrip_FullyPopulated_PreservesAllFields()
    {
        var original = new TrainingJobConfig
        {
            Symbols = new[] { "7203-T", "9984-T" },
            StartDate = new DateOnly(2020, 1, 1),
            EndDate = new DateOnly(2025, 12, 31),
            Timeframe = TrainingTimeframe.Weekly,
            Framework = TrainingFramework.LightGBM,
            Architecture = "gbdt",
            FeatureMode = PredictionFeatureMode.OhlcvMinMax,
            WindowSize = 60,
            Horizon = 5,
            TargetType = TargetType.Regression,
            NSplits = 8,
            Gap = 12,
            OosTailDays = 90,
            Hyperparameters = new Dictionary<string, string> { ["num_leaves"] = "31", ["lr"] = "0.05" },
            OutputName = "multi-2_weekly",
        };

        var json = TrainingConfigJson.Serialize(original);
        var roundTripped = TrainingConfigJson.DeserializeConfig(json);

        Assert.Equal(original.Symbols, roundTripped.Symbols);
        Assert.Equal(original.StartDate, roundTripped.StartDate);
        Assert.Equal(original.EndDate, roundTripped.EndDate);
        Assert.Equal(original.Timeframe, roundTripped.Timeframe);
        Assert.Equal(original.Framework, roundTripped.Framework);
        Assert.Equal(original.Architecture, roundTripped.Architecture);
        Assert.Equal(original.FeatureMode, roundTripped.FeatureMode);
        Assert.Equal(original.WindowSize, roundTripped.WindowSize);
        Assert.Equal(original.Horizon, roundTripped.Horizon);
        Assert.Equal(original.TargetType, roundTripped.TargetType);
        Assert.Equal(original.NSplits, roundTripped.NSplits);
        Assert.Equal(original.Gap, roundTripped.Gap);
        Assert.Equal(original.OosTailDays, roundTripped.OosTailDays);
        Assert.Equal(original.Hyperparameters, roundTripped.Hyperparameters);
        Assert.Equal(original.OutputName, roundTripped.OutputName);
    }

    [Fact]
    public void TrainingJobConfig_RoundTrip_OptionalFieldsNull_PreservesNullsAndDefaults()
    {
        var original = new TrainingJobConfig
        {
            Symbols = new[] { "4527-T" },
            Architecture = "lstm",
            WindowSize = 30,
            Horizon = 3,
            // StartDate, EndDate, OutputName left null; Hyperparameters left at its default empty map.
        };

        var json = TrainingConfigJson.Serialize(original);
        var roundTripped = TrainingConfigJson.DeserializeConfig(json);

        Assert.Null(roundTripped.StartDate);
        Assert.Null(roundTripped.EndDate);
        Assert.Null(roundTripped.OutputName);
        Assert.Empty(roundTripped.Hyperparameters);
        Assert.Equal(TrainingTimeframe.Daily, roundTripped.Timeframe);
        Assert.Equal(TrainingFramework.PyTorch, roundTripped.Framework);
        Assert.Equal(TargetType.Classification, roundTripped.TargetType);
        Assert.Equal(WalkForwardDataRequirement.DefaultSplitCount, roundTripped.NSplits);
        Assert.Null(roundTripped.Gap);
        Assert.Null(roundTripped.OosTailDays);
    }

    [Theory]
    [InlineData(TargetType.Classification, "classification")]
    [InlineData(TargetType.Regression, "regression")]
    public void TrainingJobConfig_TargetType_SerializesToExpectedWireString(TargetType targetType, string expectedWireValue)
    {
        var config = new TrainingJobConfig { Symbols = new[] { "7203-T" }, Architecture = "lstm", WindowSize = 60, Horizon = 5, TargetType = targetType };

        var json = TrainingConfigJson.Serialize(config);

        Assert.Contains($"\"target_type\": \"{expectedWireValue}\"", json);
        Assert.Equal(targetType, TrainingConfigJson.DeserializeConfig(json).TargetType);
    }

    [Fact]
    public void TrainingJobConfig_Deserialize_UnknownTargetTypeWireString_ThrowsJsonException()
    {
        const string json = """{"symbols":["7203-T"],"architecture":"lstm","window_size":60,"horizon":5,"target_type":"ranking"}""";

        Assert.Throws<JsonException>(() => TrainingConfigJson.DeserializeConfig(json));
    }

    [Fact]
    public void TrainingJobConfig_Serialize_EmitsNSplitsAndTargetType_OmitsNullGapAndOosTailDays()
    {
        var config = new TrainingJobConfig { Symbols = new[] { "7203-T" }, Architecture = "lstm", WindowSize = 60, Horizon = 5 };

        var json = TrainingConfigJson.Serialize(config);

        Assert.Contains("\"n_splits\"", json);
        Assert.Contains("\"target_type\"", json);
        // Null optionals are omitted from the wire document (DefaultIgnoreCondition.WhenWritingNull).
        Assert.DoesNotContain("\"gap\"", json);
        Assert.DoesNotContain("\"oos_tail_days\"", json);
    }

    [Fact]
    public void TrainingJobConfig_Serialize_UsesSnakeCasePropertyNames()
    {
        var config = new TrainingJobConfig
        {
            Symbols = new[] { "7203-T" },
            Architecture = "lstm",
            WindowSize = 60,
            Horizon = 5,
        };

        var json = TrainingConfigJson.Serialize(config);

        Assert.Contains("\"window_size\"", json);
        Assert.Contains("\"feature_mode\"", json);
        Assert.DoesNotContain("\"WindowSize\"", json);
        // Null optionals are omitted from the wire document (DefaultIgnoreCondition.WhenWritingNull).
        Assert.DoesNotContain("\"output_name\"", json);
        Assert.DoesNotContain("\"start_date\"", json);
    }

    [Theory]
    [InlineData(TrainingTimeframe.Daily, "daily")]
    [InlineData(TrainingTimeframe.Weekly, "weekly")]
    [InlineData(TrainingTimeframe.Monthly, "monthly")]
    public void TrainingJobConfig_Timeframe_SerializesToExpectedWireString(TrainingTimeframe timeframe, string expectedWireValue)
    {
        var config = new TrainingJobConfig { Symbols = new[] { "7203-T" }, Architecture = "lstm", WindowSize = 60, Horizon = 5, Timeframe = timeframe };

        var json = TrainingConfigJson.Serialize(config);

        Assert.Contains($"\"timeframe\": \"{expectedWireValue}\"", json);
        Assert.Equal(timeframe, TrainingConfigJson.DeserializeConfig(json).Timeframe);
    }

    [Theory]
    [InlineData(TrainingFramework.PyTorch, "pytorch")]
    [InlineData(TrainingFramework.LightGBM, "lightgbm")]
    [InlineData(TrainingFramework.TensorFlow, "tensorflow")]
    public void TrainingJobConfig_Framework_SerializesToExpectedWireString(TrainingFramework framework, string expectedWireValue)
    {
        var config = new TrainingJobConfig { Symbols = new[] { "7203-T" }, Architecture = "lstm", WindowSize = 60, Horizon = 5, Framework = framework };

        var json = TrainingConfigJson.Serialize(config);

        Assert.Contains($"\"framework\": \"{expectedWireValue}\"", json);
        Assert.Equal(framework, TrainingConfigJson.DeserializeConfig(json).Framework);
    }

    [Fact]
    public void TrainingJobConfig_Deserialize_UnknownTimeframeWireString_ThrowsJsonException()
    {
        const string json = """{"symbols":["7203-T"],"architecture":"lstm","window_size":60,"horizon":5,"timeframe":"quarterly"}""";

        Assert.Throws<JsonException>(() => TrainingConfigJson.DeserializeConfig(json));
    }

    [Fact]
    public void TrainingRunResult_RoundTrip_ViaSharedOptions_PreservesAllFields()
    {
        // ExperimentLogService writes metrics.json via JsonSerializer.Serialize(result,
        // TrainingConfigJson.Options) directly (no dedicated helper), so this test exercises
        // that exact call shape rather than a TrainingConfigJson-specific method.
        var original = new TrainingRunResult
        {
            RunId = "20260829-153000",
            Success = true,
            ExitCode = 0,
            OnnxArtifactPath = @"I:\stock\StockAnalyzer.Python\training\artifacts\multi-2_weekly.onnx",
            MetricsArtifactPath = @"I:\stock\StockAnalyzer.Python\training\artifacts\multi-2_weekly.onnx.metrics.json",
            Metrics = new Dictionary<string, double> { ["accuracy"] = 0.62, ["macro_f1"] = 0.58 },
            Message = "completed",
            StartedUtc = new DateTimeOffset(2026, 8, 29, 15, 30, 0, TimeSpan.Zero),
            CompletedUtc = new DateTimeOffset(2026, 8, 29, 15, 42, 0, TimeSpan.Zero),
        };

        var json = JsonSerializer.Serialize(original, TrainingConfigJson.Options);
        var roundTripped = JsonSerializer.Deserialize<TrainingRunResult>(json, TrainingConfigJson.Options);

        Assert.NotNull(roundTripped);
        Assert.Equal(original.RunId, roundTripped!.RunId);
        Assert.Equal(original.Success, roundTripped.Success);
        Assert.Equal(original.ExitCode, roundTripped.ExitCode);
        Assert.Equal(original.OnnxArtifactPath, roundTripped.OnnxArtifactPath);
        Assert.Equal(original.MetricsArtifactPath, roundTripped.MetricsArtifactPath);
        Assert.Equal(original.Metrics, roundTripped.Metrics);
        Assert.Equal(original.Message, roundTripped.Message);
        Assert.Equal(original.StartedUtc, roundTripped.StartedUtc);
        Assert.Equal(original.CompletedUtc, roundTripped.CompletedUtc);
    }

    [Fact]
    public void Utf8NoBom_EmitsNoPreamble()
    {
        // Regression for: json.decoder.JSONDecodeError killing every training run. Files written
        // with this encoding are read back by run_training.py via
        // Path.read_text(encoding="utf-8"), which does not strip a byte-order-mark - a BOM makes
        // json.loads fail on the very first character.
        Assert.Empty(TrainingConfigJson.Utf8NoBom.GetPreamble());
    }

    [Fact]
    public void TrainingRunResult_Serialize_UsesSnakeCasePropertyNames()
    {
        var result = new TrainingRunResult { RunId = "run-1", Success = false, ExitCode = 1 };

        var json = JsonSerializer.Serialize(result, TrainingConfigJson.Options);

        Assert.Contains("\"run_id\"", json);
        Assert.Contains("\"exit_code\"", json);
        Assert.DoesNotContain("\"RunId\"", json);
    }
}
