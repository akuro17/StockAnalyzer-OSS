using System.Collections.Generic;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models.Training;

/// <summary>
/// Round-trip and validation coverage for <see cref="FeatureSpec"/> and the
/// <see cref="PredictionFeatureMode.ComposedFeatures"/> wiring added for the composed-features
/// (Group 3) training path: an ordered, variable-width mix of individually selected price channels
/// and registered indicators, each with its own <see cref="ChannelNormalization"/>.
/// </summary>
public class FeatureSpecJsonTests
{
    private static TrainingJobConfig ComposedConfig(FeatureSpec spec) => new()
    {
        Symbols = new[] { "7203-T" },
        Architecture = "lstm",
        WindowSize = 60,
        Horizon = 5,
        FeatureMode = PredictionFeatureMode.ComposedFeatures,
        FeatureSpec = spec,
    };

    private static FeatureSpec SampleSpec() => new()
    {
        Channels = new List<FeatureChannel>
        {
            new() { Kind = FeatureChannelKind.Price, Price = PriceType.Close, Normalization = ChannelNormalization.WindowMinMax },
            new() { Kind = FeatureChannelKind.Price, Price = PriceType.High, Normalization = ChannelNormalization.WindowMinMax },
            new()
            {
                Kind = FeatureChannelKind.Indicator,
                Indicator = IndicatorType.RSI,
                Params = new Dictionary<string, string> { ["period"] = "14" },
                Normalization = ChannelNormalization.None,
            },
            new()
            {
                Kind = FeatureChannelKind.Indicator,
                Indicator = IndicatorType.SMA,
                Params = new Dictionary<string, string> { ["period"] = "20" },
                Normalization = ChannelNormalization.WindowZScore,
            },
        },
    };

    [Fact]
    public void ComposedConfig_RoundTrip_PreservesFeatureSpec()
    {
        var original = ComposedConfig(SampleSpec());

        var json = TrainingConfigJson.Serialize(original);
        var roundTripped = TrainingConfigJson.DeserializeConfig(json);

        Assert.Equal(PredictionFeatureMode.ComposedFeatures, roundTripped.FeatureMode);
        Assert.NotNull(roundTripped.FeatureSpec);
        Assert.Equal(FeatureSpec.CurrentSchemaVersion, roundTripped.FeatureSpec!.SchemaVersion);

        // Serializing again yields byte-identical JSON: the wire form is a fixed point.
        Assert.Equal(json, TrainingConfigJson.Serialize(roundTripped));

        var before = original.FeatureSpec!.Channels;
        var after = roundTripped.FeatureSpec!.Channels;
        Assert.Equal(before.Count, after.Count);
        for (int i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].Kind, after[i].Kind);
            Assert.Equal(before[i].Price, after[i].Price);
            Assert.Equal(before[i].Indicator, after[i].Indicator);
            Assert.Equal(before[i].Normalization, after[i].Normalization);
            Assert.Equal(before[i].Params, after[i].Params);
        }
    }

    [Fact]
    public void ComposedConfig_Serialize_UsesReadableWireStrings()
    {
        var json = TrainingConfigJson.Serialize(ComposedConfig(SampleSpec()));

        Assert.Contains("\"feature_mode\": \"composed_features\"", json);
        Assert.Contains("\"kind\": \"price\"", json);
        Assert.Contains("\"price\": \"close\"", json);
        Assert.Contains("\"indicator\": \"RSI\"", json);
        Assert.Contains("\"normalization\": \"window_min_max\"", json);
        Assert.Contains("\"normalization\": \"window_zscore\"", json);
    }

    [Fact]
    public void NonComposedConfig_OmitsFeatureSpec()
    {
        var config = new TrainingJobConfig
        {
            Symbols = new[] { "7203-T" }, Architecture = "lstm", WindowSize = 60, Horizon = 5,
            FeatureMode = PredictionFeatureMode.OhlcvMinMax,
        };

        var json = TrainingConfigJson.Serialize(config);

        Assert.DoesNotContain("\"feature_spec\"", json);
        Assert.Null(TrainingConfigJson.DeserializeConfig(json).FeatureSpec);
    }

    [Fact]
    public void Deserialize_UnknownIndicatorName_ThrowsJsonException()
    {
        const string json = """
        {"symbols":["7203-T"],"architecture":"lstm","window_size":60,"horizon":5,
         "feature_mode":"composed_features",
         "feature_spec":{"channels":[{"kind":"indicator","indicator":"NotAnIndicator","normalization":"none"}]}}
        """;

        Assert.Throws<JsonException>(() => TrainingConfigJson.DeserializeConfig(json));
    }

    [Fact]
    public void Validate_Composed_WithValidSpec_DoesNotThrow()
        => ComposedConfig(SampleSpec()).Validate();

    [Fact]
    public void Validate_Composed_MissingSpec_Throws()
    {
        var config = new TrainingJobConfig
        {
            Symbols = new[] { "7203-T" }, Architecture = "lstm", WindowSize = 60, Horizon = 5,
            FeatureMode = PredictionFeatureMode.ComposedFeatures,
        };

        var ex = Assert.Throws<System.InvalidOperationException>(config.Validate);
        Assert.Contains("FeatureSpec", ex.Message);
    }

    [Fact]
    public void Validate_Composed_EmptyChannels_Throws()
    {
        var config = ComposedConfig(new FeatureSpec { Channels = new List<FeatureChannel>() });

        Assert.Throws<System.InvalidOperationException>(config.Validate);
    }

    [Fact]
    public void Validate_NonComposed_WithSpec_Throws()
    {
        var config = new TrainingJobConfig
        {
            Symbols = new[] { "7203-T" }, Architecture = "lstm", WindowSize = 60, Horizon = 5,
            FeatureMode = PredictionFeatureMode.OhlcvMinMax,
            FeatureSpec = SampleSpec(),
        };

        Assert.Throws<System.InvalidOperationException>(config.Validate);
    }

    [Fact]
    public void FeatureChannel_PriceKindWithIndicatorPayload_IsInvalid()
    {
        var channel = new FeatureChannel
        {
            Kind = FeatureChannelKind.Price,
            Price = PriceType.Close,
            Indicator = IndicatorType.RSI,
        };

        Assert.False(channel.IsValid(out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(PriceType.Open, "open")]
    [InlineData(PriceType.High, "high")]
    [InlineData(PriceType.Low, "low")]
    [InlineData(PriceType.Close, "close")]
    [InlineData(PriceType.Median, "median")]
    [InlineData(PriceType.Midpoint, "midpoint")]
    [InlineData(PriceType.Typical, "typical")]
    [InlineData(PriceType.Weighted, "weighted")]
    [InlineData(PriceType.Average, "average")]
    [InlineData(PriceType.HeikinAshiOpen, "heikin_ashi_open")]
    [InlineData(PriceType.HeikinAshiHigh, "heikin_ashi_high")]
    [InlineData(PriceType.HeikinAshiLow, "heikin_ashi_low")]
    [InlineData(PriceType.HeikinAshiClose, "heikin_ashi_close")]
    [InlineData(PriceType.TrueHigh, "true_high")]
    [InlineData(PriceType.TrueLow, "true_low")]
    public void FeatureChannel_AllFifteenPriceTypes_RoundTripCleanly(PriceType type, string wireString)
    {
        var spec = new FeatureSpec
        {
            Channels = new List<FeatureChannel>
            {
                new() { Kind = FeatureChannelKind.Price, Price = type }
            }
        };
        var config = ComposedConfig(spec);

        var json = TrainingConfigJson.Serialize(config);
        Assert.Contains($"\"price\": \"{wireString}\"", json);

        var roundTripped = TrainingConfigJson.DeserializeConfig(json);
        Assert.NotNull(roundTripped.FeatureSpec);
        var row = Assert.Single(roundTripped.FeatureSpec!.Channels);
        Assert.Equal(type, row.Price);
    }
}
