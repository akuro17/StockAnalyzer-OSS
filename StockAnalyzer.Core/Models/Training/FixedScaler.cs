using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Models.Training;

public sealed record FixedScalerStatistic(double Mean, double Std, double Min, double Max);

/// <summary>Versioned generation-bound, per-expanded-channel population z-score.</summary>
public sealed record FixedScaler(
    int SchemaVersion, string FeatureMode, int WindowSize, int[] Lags, double? ClipSigma,
    string[] ChannelIdentities, FixedScalerStatistic[] Statistics, string FeatureContractHash)
{
    public const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static FixedScaler Load(string modelPath, IReadOnlyDictionary<string, string> metadata)
    {
        var expectedName = Path.GetFileName(modelPath) + ".scaler.json";
        if (!metadata.TryGetValue(PredictionModelMetadata.ScalerReferenceKey, out var reference)
            || reference != expectedName)
            throw new InvalidDataException("ONNX scaler_ref must name its own generation sidecar.");
        var path = modelPath + ".scaler.json";
        var scaler = JsonSerializer.Deserialize<FixedScaler>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Scaler sidecar is empty.");
        scaler.Validate(metadata);
        return scaler;
    }

    public void Validate(IReadOnlyDictionary<string, string> metadata)
    {
        if (SchemaVersion != CurrentSchemaVersion
            || FeatureContractHash != ComputeContractHash(metadata)
            || !TrainingResourceOverrides.ValidLags(Lags, out _)
            || FeatureMode != metadata.GetValueOrDefault(PredictionModelMetadata.FeatureModeKey)
            || WindowSize <= 0
            || WindowSize.ToString(CultureInfo.InvariantCulture) != metadata.GetValueOrDefault(PredictionModelMetadata.WindowSizeKey)
            || !int.TryParse(metadata.GetValueOrDefault("channels"), out var channelCount)
            || ChannelIdentities is null || Statistics is null
            || ChannelIdentities.Length != channelCount || Statistics.Length != channelCount
            || channelCount is < 1 or > FeatureSpec.MaxChannels)
            throw new InvalidDataException("Scaler schema, contract hash, shape, or lag contract is invalid.");
        if (ClipSigma is { } clip && (!double.IsFinite(clip) || clip <= 0))
            throw new InvalidDataException("Scaler clip sigma is invalid.");
        var clipText = metadata.GetValueOrDefault("clip_sigma") ?? "";
        if (metadata.GetValueOrDefault("normalization") != "fixed_zscore"
            || metadata.GetValueOrDefault("lags") != JsonSerializer.Serialize(Lags)
            || (ClipSigma is null ? clipText.Length != 0
                : !double.TryParse(clipText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedClip)
                    || parsedClip != ClipSigma.Value))
            throw new InvalidDataException("Scaler preprocessing metadata is inconsistent.");
        var expected = ChannelNames(metadata, Lags);
        if (!ChannelIdentities.SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidDataException("Scaler channel identities disagree with the ONNX feature contract.");
        if (FeatureMode == "composed_features")
        {
            var spec = JsonSerializer.Deserialize<FeatureSpec>(metadata[PredictionModelMetadata.FeatureSpecKey],
                TrainingConfigJson.Options) ?? throw new InvalidDataException("Composed scaler lacks a FeatureSpec.");
            if (!spec.IsValid(out _) || !spec.Lags!.SequenceEqual(Lags)
                || spec.Channels.Any(channel => channel.Normalization != ChannelNormalization.None))
                throw new InvalidDataException("Composed scaler conflicts with lags or window normalization.");
        }
        foreach (var s in Statistics)
            if (s is null || !double.IsFinite(s.Mean) || !double.IsFinite(s.Std)
                || !double.IsFinite(s.Min) || !double.IsFinite(s.Max)
                || s.Std < 0 || s.Min > s.Max)
                throw new InvalidDataException("Scaler statistics are invalid.");
    }

    public void Transform(ReadOnlySpan<double> raw, Span<float> output)
    {
        int count = checked(WindowSize * Statistics.Length);
        if (raw.Length != count || output.Length != count)
            throw new InvalidDataException("Scaler tensor dimensions are inconsistent.");
        for (int row = 0; row < WindowSize; row++)
        for (int ch = 0; ch < Statistics.Length; ch++)
        {
            int index = row * Statistics.Length + ch;
            var stat = Statistics[ch];
            if (!double.IsFinite(raw[index])) throw new InvalidDataException("Raw feature is nonfinite.");
            double z = stat.Std <= IMLDataProcessor.Epsilon ? 0.0 : (raw[index] - stat.Mean) / stat.Std;
            if (ClipSigma is { } clip) z = Math.Min(clip, Math.Max(-clip, z));
            if (!double.IsFinite(z)) throw new InvalidDataException("Scaled feature is nonfinite.");
            output[index] = (float)z;
        }
    }

    public static string ComputeContractHash(IReadOnlyDictionary<string, string> metadata)
    {
        string[] keys = { "feature_mode", "window_size", "channels", "feature_spec", "lags", "clip_sigma" };
        var text = string.Join("\n", keys.Select(key => metadata.GetValueOrDefault(key) ?? ""));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static string[] ChannelNames(IReadOnlyDictionary<string, string> metadata, IReadOnlyList<int> lags)
    {
        string[] baseNames;
        if (metadata.GetValueOrDefault(PredictionModelMetadata.FeatureModeKey) == "composed_features")
        {
            using var doc = JsonDocument.Parse(metadata[PredictionModelMetadata.FeatureSpecKey]);
            var channels = doc.RootElement.GetProperty("channels");
            baseNames = channels.EnumerateArray().Select((channel, index) =>
            {
                string kind = channel.GetProperty("kind").GetString() ?? "";
                string name = channel.GetProperty(kind == "price" ? "price" : "indicator").GetString() ?? "";
                return $"{index}:{kind}:{name}";
            }).ToArray();
        }
        else if (metadata.GetValueOrDefault(PredictionModelMetadata.FeatureModeKey) == "ohlcv_minmax")
            baseNames = new[] { "open", "high", "low", "close", "volume" };
        else throw new InvalidDataException("Fixed scaler feature mode is unsupported.");
        return new[] { 0 }.Concat(lags).SelectMany(lag => baseNames.Select(name => $"lag{lag}:{name}")).ToArray();
    }
}
