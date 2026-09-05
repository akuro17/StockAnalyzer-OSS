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
        options.Converters.Add(new FeatureChannelKindJsonConverter());
        options.Converters.Add(new PriceFieldJsonConverter());
        options.Converters.Add(new PriceTypeJsonConverter());
        options.Converters.Add(new ChannelNormalizationJsonConverter());
        options.Converters.Add(new IndicatorTypeJsonConverter());
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
            PredictionFeatureMode.ComposedFeatures => "composed_features",
            _ => throw new JsonException($"Unknown feature_mode '{value}'."),
        });
}

/// <summary>Maps <see cref="FeatureChannelKind"/> to/from its <c>price</c>/<c>indicator</c> wire string.</summary>
internal sealed class FeatureChannelKindJsonConverter : JsonConverter<FeatureChannelKind>
{
    public override FeatureChannelKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetString()?.Trim().ToLowerInvariant() switch
        {
            "price" => FeatureChannelKind.Price,
            "indicator" => FeatureChannelKind.Indicator,
            var other => throw new JsonException($"Unknown feature channel kind '{other}'."),
        };

    public override void Write(Utf8JsonWriter writer, FeatureChannelKind value, JsonSerializerOptions options)
        => writer.WriteStringValue(value switch
        {
            FeatureChannelKind.Price => "price",
            FeatureChannelKind.Indicator => "indicator",
            _ => throw new JsonException($"Unknown feature channel kind '{value}'."),
        });
}

/// <summary>Maps <see cref="PriceType"/> to/from its wire string (e.g. <c>open</c>, <c>heikin_ashi_open</c>).</summary>
internal sealed class PriceTypeJsonConverter : JsonConverter<PriceType>
{
    public override PriceType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetString()?.Trim().ToLowerInvariant() switch
        {
            "open" => PriceType.Open,
            "high" => PriceType.High,
            "low" => PriceType.Low,
            "close" => PriceType.Close,
            "median" => PriceType.Median,
            "midpoint" => PriceType.Midpoint,
            "typical" => PriceType.Typical,
            "weighted" => PriceType.Weighted,
            "average" => PriceType.Average,
            "heikin_ashi_open" or "heikinashiopen" or "heikin_open" => PriceType.HeikinAshiOpen,
            "heikin_ashi_high" or "heikinashihigh" or "heikin_high" => PriceType.HeikinAshiHigh,
            "heikin_ashi_low" or "heikinashilow" or "heikin_low" => PriceType.HeikinAshiLow,
            "heikin_ashi_close" or "heikinashiclose" or "heikin_close" => PriceType.HeikinAshiClose,
            "true_high" or "truehigh" => PriceType.TrueHigh,
            "true_low" or "truelow" => PriceType.TrueLow,
            var other => throw new JsonException($"Unknown price type '{other}'."),
        };

    public override void Write(Utf8JsonWriter writer, PriceType value, JsonSerializerOptions options)
        => writer.WriteStringValue(value switch
        {
            PriceType.Open => "open",
            PriceType.High => "high",
            PriceType.Low => "low",
            PriceType.Close => "close",
            PriceType.Median => "median",
            PriceType.Midpoint => "midpoint",
            PriceType.Typical => "typical",
            PriceType.Weighted => "weighted",
            PriceType.Average => "average",
            PriceType.HeikinAshiOpen => "heikin_ashi_open",
            PriceType.HeikinAshiHigh => "heikin_ashi_high",
            PriceType.HeikinAshiLow => "heikin_ashi_low",
            PriceType.HeikinAshiClose => "heikin_ashi_close",
            PriceType.TrueHigh => "true_high",
            PriceType.TrueLow => "true_low",
            _ => throw new JsonException($"Unknown price type '{value}'."),
        });
}

/// <summary>Maps <see cref="PriceField"/> to/from its <c>open</c>/<c>high</c>/<c>low</c>/<c>close</c> wire string.</summary>
internal sealed class PriceFieldJsonConverter : JsonConverter<PriceField>
{
    public override PriceField Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetString()?.Trim().ToLowerInvariant() switch
        {
            "open" => PriceField.Open,
            "high" => PriceField.High,
            "low" => PriceField.Low,
            "close" => PriceField.Close,
            var other => throw new JsonException($"Unknown price field '{other}'."),
        };

    public override void Write(Utf8JsonWriter writer, PriceField value, JsonSerializerOptions options)
        => writer.WriteStringValue(value switch
        {
            PriceField.Open => "open",
            PriceField.High => "high",
            PriceField.Low => "low",
            PriceField.Close => "close",
            _ => throw new JsonException($"Unknown price field '{value}'."),
        });
}

/// <summary>Maps <see cref="ChannelNormalization"/> to/from its <c>none</c>/<c>window_min_max</c>/<c>window_zscore</c> wire string.</summary>
internal sealed class ChannelNormalizationJsonConverter : JsonConverter<ChannelNormalization>
{
    public override ChannelNormalization Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetString()?.Trim().ToLowerInvariant() switch
        {
            "none" => ChannelNormalization.None,
            "window_min_max" => ChannelNormalization.WindowMinMax,
            "window_zscore" => ChannelNormalization.WindowZScore,
            var other => throw new JsonException($"Unknown channel normalization '{other}'."),
        };

    public override void Write(Utf8JsonWriter writer, ChannelNormalization value, JsonSerializerOptions options)
        => writer.WriteStringValue(value switch
        {
            ChannelNormalization.None => "none",
            ChannelNormalization.WindowMinMax => "window_min_max",
            ChannelNormalization.WindowZScore => "window_zscore",
            _ => throw new JsonException($"Unknown channel normalization '{value}'."),
        });
}

/// <summary>
/// Maps <see cref="IndicatorType"/> to/from its enum member name (case-insensitive on read). Name-based
/// rather than a hand-maintained switch because <see cref="IndicatorType"/> has a large, growing
/// membership and its member names are already the catalog-facing identifiers.
/// </summary>
internal sealed class IndicatorTypeJsonConverter : JsonConverter<IndicatorType>
{
    public override IndicatorType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();
        if (!string.IsNullOrWhiteSpace(raw) && Enum.TryParse<IndicatorType>(raw.Trim(), ignoreCase: true, out var parsed)
            && Enum.IsDefined(typeof(IndicatorType), parsed))
        {
            return parsed;
        }

        throw new JsonException($"Unknown indicator type '{raw}'.");
    }

    public override void Write(Utf8JsonWriter writer, IndicatorType value, JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(typeof(IndicatorType), value))
        {
            throw new JsonException($"Unknown indicator type '{value}'.");
        }

        writer.WriteStringValue(value.ToString());
    }
}
