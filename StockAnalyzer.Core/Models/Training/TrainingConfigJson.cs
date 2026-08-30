using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StockAnalyzer.Core.Models.Training;

/// <summary>
/// Single source of truth for how the training DTOs serialize to and from JSON. The same wire
/// document is written by the Avalonia wizard, read by <c>run_training.py --config</c> and
/// echoed into the ONNX <c>metadata_props</c>, so the property names (snake_case) and the enum
/// spellings are pinned here and mirrored by the Python dataclass.
/// </summary>
public static class TrainingConfigJson
{
    /// <summary>Shared serializer options: snake_case properties, lower-case enum wire strings, indented, nulls omitted.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>
    /// UTF-8 encoding with no byte-order-mark, for writing this contract's JSON to files the
    /// Python side reads with <c>Path.read_text(encoding="utf-8")</c> (which does not strip a
    /// BOM). The static <see cref="Encoding.UTF8"/> instance emits a BOM preamble on write,
    /// which makes <c>json.loads</c> fail with a leading <c>json.decoder.JSONDecodeError</c>.
    /// Mirrors the same fix already applied to <see cref="StockAnalyzer.Core.Services.PythonProcessManager"/>'s pipe writer.
    /// </summary>
    public static Encoding Utf8NoBom { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Serializes <paramref name="config"/> to the canonical job-config JSON.</summary>
    public static string Serialize(TrainingJobConfig config)
        => JsonSerializer.Serialize(config, Options);

    /// <summary>Round-trips a job-config JSON produced by <see cref="Serialize"/>.</summary>
    public static TrainingJobConfig DeserializeConfig(string json)
        => JsonSerializer.Deserialize<TrainingJobConfig>(json, Options)
           ?? throw new JsonException("TrainingJobConfig JSON deserialized to null.");

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
        };
        options.Converters.Add(new TrainingTimeframeJsonConverter());
        options.Converters.Add(new TrainingFrameworkJsonConverter());
        options.Converters.Add(new TrainingFeatureModeJsonConverter());
        options.Converters.Add(new TrainingTargetTypeJsonConverter());
        return options;
    }
}

/// <summary>Maps <see cref="TrainingTimeframe"/> to/from its <c>daily</c>/<c>weekly</c>/<c>monthly</c> wire string.</summary>
internal sealed class TrainingTimeframeJsonConverter : JsonConverter<TrainingTimeframe>
{
    public override TrainingTimeframe Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetString()?.Trim().ToLowerInvariant() switch
        {
            "daily" => TrainingTimeframe.Daily,
            "weekly" => TrainingTimeframe.Weekly,
            "monthly" => TrainingTimeframe.Monthly,
            var other => throw new JsonException($"Unknown timeframe '{other}'."),
        };

    public override void Write(Utf8JsonWriter writer, TrainingTimeframe value, JsonSerializerOptions options)
        => writer.WriteStringValue(value switch
        {
            TrainingTimeframe.Daily => "daily",
            TrainingTimeframe.Weekly => "weekly",
            TrainingTimeframe.Monthly => "monthly",
            _ => throw new JsonException($"Unknown timeframe '{value}'."),
        });
}

/// <summary>Maps <see cref="TrainingFramework"/> to/from its <c>pytorch</c>/<c>lightgbm</c>/<c>tensorflow</c> wire string.</summary>
internal sealed class TrainingFrameworkJsonConverter : JsonConverter<TrainingFramework>
{
    public override TrainingFramework Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetString()?.Trim().ToLowerInvariant() switch
        {
            "pytorch" => TrainingFramework.PyTorch,
            "lightgbm" => TrainingFramework.LightGBM,
            "tensorflow" => TrainingFramework.TensorFlow,
            var other => throw new JsonException($"Unknown framework '{other}'."),
        };

    public override void Write(Utf8JsonWriter writer, TrainingFramework value, JsonSerializerOptions options)
        => writer.WriteStringValue(value switch
        {
            TrainingFramework.PyTorch => "pytorch",
            TrainingFramework.LightGBM => "lightgbm",
            TrainingFramework.TensorFlow => "tensorflow",
            _ => throw new JsonException($"Unknown framework '{value}'."),
        });
}

/// <summary>Maps <see cref="TargetType"/> to/from its <c>classification</c>/<c>regression</c> wire string.</summary>
internal sealed class TrainingTargetTypeJsonConverter : JsonConverter<TargetType>
{
    public override TargetType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetString()?.Trim().ToLowerInvariant() switch
        {
            "classification" => TargetType.Classification,
            "regression" => TargetType.Regression,
            var other => throw new JsonException($"Unknown target_type '{other}'."),
        };

    public override void Write(Utf8JsonWriter writer, TargetType value, JsonSerializerOptions options)
        => writer.WriteStringValue(value switch
        {
            TargetType.Classification => "classification",
            TargetType.Regression => "regression",
            _ => throw new JsonException($"Unknown target_type '{value}'."),
        });
}

/// <summary>
/// Maps <see cref="StockAnalyzer.Core.Models.PredictionFeatureMode"/> to/from the canonical
/// <c>feature_mode</c> wire string shared with <c>dataset.FEATURE_MODES</c> and
/// <see cref="StockAnalyzer.Core.Models.PredictionModelMetadata.ParseFeatureMode"/>.
/// </summary>
internal sealed class TrainingFeatureModeJsonConverter : JsonConverter<PredictionFeatureMode>
{
    public override PredictionFeatureMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();
        return PredictionModelMetadata.ParseFeatureMode(raw)
               ?? throw new JsonException($"Unknown feature_mode '{raw}'.");
    }

    public override void Write(Utf8JsonWriter writer, PredictionFeatureMode value, JsonSerializerOptions options)
        => writer.WriteStringValue(value switch
        {
            PredictionFeatureMode.OhlcvMinMax => "ohlcv_minmax",
            PredictionFeatureMode.LogReturn => "log_return",
            PredictionFeatureMode.ZScoreStandardized => "zscore",
            PredictionFeatureMode.ZScoreOhlcvJoint => "zscore_joint",
            PredictionFeatureMode.LogReturnOhlc => "log_return_ohlc",
            _ => throw new JsonException($"Unknown feature_mode '{value}'."),
        });
}
