using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Cross-check coverage for the <c>feature_spec</c> contract key added for
/// <see cref="PredictionFeatureMode.ComposedFeatures"/> models. Two composed models of equal
/// channel width are indistinguishable by tensor shape, so the JSON composition itself is enforced.
/// </summary>
public class PredictionModelMetadataComposedTests
{
    private static readonly string[] Classes = { "Up", "Down", "Neutral" };

    private const string SpecA =
        """{"channels":[{"kind":"price","price":"close","normalization":"window_min_max"}]}""";
    private const string SpecB =
        """{"channels":[{"kind":"indicator","indicator":"RSI","params":{"period":"14"},"normalization":"none"}]}""";

    private static Dictionary<string, string> BaseMap(string? featureSpec) => new()
    {
        ["feature_mode"] = "composed_features",
        ["window_size"] = "10",
        ["class_order"] = "Up,Down,Neutral",
        ["feature_spec"] = featureSpec ?? "",
    };

    [Fact]
    public void ParseFeatureMode_ComposedFeatures_MapsToEnum()
        => Assert.Equal(PredictionFeatureMode.ComposedFeatures, PredictionModelMetadata.ParseFeatureMode("composed_features"));

    [Fact]
    public void Validate_MatchingFeatureSpec_DoesNotThrow()
        => PredictionModelMetadata.Validate(
            BaseMap(SpecA), PredictionFeatureMode.ComposedFeatures, 10, Classes, NullLogger.Instance, SpecA);

    [Fact]
    public void Validate_FeatureSpecEquivalentUpToWhitespace_DoesNotThrow()
    {
        var spaced = SpecA.Replace(":", " : ").Replace(",", " , ");

        PredictionModelMetadata.Validate(
            BaseMap(spaced), PredictionFeatureMode.ComposedFeatures, 10, Classes, NullLogger.Instance, SpecA);
    }

    [Fact]
    public void Validate_MismatchedFeatureSpec_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PredictionModelMetadata.Validate(
            BaseMap(SpecB), PredictionFeatureMode.ComposedFeatures, 10, Classes, NullLogger.Instance, SpecA));

        Assert.Contains("feature_spec", ex.Message);
    }

    [Fact]
    public void Validate_ModelHasSpec_ConfigHasNone_Throws()
        => Assert.Throws<InvalidOperationException>(() => PredictionModelMetadata.Validate(
            BaseMap(SpecA), PredictionFeatureMode.ComposedFeatures, 10, Classes, NullLogger.Instance, expectedFeatureSpec: null));

    [Fact]
    public void Validate_ConfigHasSpec_ModelHasNone_Throws()
    {
        var map = new Dictionary<string, string>
        {
            ["feature_mode"] = "composed_features",
            ["window_size"] = "10",
            ["class_order"] = "Up,Down,Neutral",
        };

        Assert.Throws<InvalidOperationException>(() => PredictionModelMetadata.Validate(
            map, PredictionFeatureMode.ComposedFeatures, 10, Classes, NullLogger.Instance, SpecA));
    }

    [Fact]
    public void Validate_FixedModeModel_NoFeatureSpec_Unaffected()
    {
        var map = new Dictionary<string, string>
        {
            ["feature_mode"] = "ohlcv_minmax",
            ["window_size"] = "10",
            ["class_order"] = "Up,Down,Neutral",
        };

        PredictionModelMetadata.Validate(
            map, PredictionFeatureMode.OhlcvMinMax, 10, Classes, NullLogger.Instance);
    }
}
