using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Tests.Services;

public class PredictionModelMetadataTests
{
    private static readonly string[] Classes = { "Up", "Down", "Neutral" };

    private static void Validate(
        IReadOnlyDictionary<string, string>? map,
        PredictionFeatureMode mode = PredictionFeatureMode.OhlcvMinMax,
        int window = 10)
        => PredictionModelMetadata.Validate(map, mode, window, Classes, NullLogger.Instance);

    internal static Dictionary<string, string> Version2Metadata(string? gap = "0")
    {
        var metadata = new Dictionary<string, string>
        {
            [PredictionModelMetadata.FeatureModeKey] = "ohlcv_minmax",
            [PredictionModelMetadata.WindowSizeKey] = "10",
            [PredictionModelMetadata.ClassOrderKey] = "Up,Down,Neutral",
            [PredictionModelMetadata.ContractVersionKey] = "2",
            [PredictionModelMetadata.TargetTypeKey] = "classification",
            [PredictionModelMetadata.HorizonKey] = "5",
            [PredictionModelMetadata.OutputSemanticKey] = "class_probabilities",
            [PredictionModelMetadata.OutputUnitKey] = "probability",
            [PredictionModelMetadata.OutputHorizonKey] = "5",
            [PredictionModelMetadata.OutputTimeframeKey] = "daily",
            [PredictionModelMetadata.OutputConfidenceTypeKey] = "class_probability",
            [PredictionModelMetadata.TargetFormulaKey] = "thresholded_simple_return",
        };
        if (gap is not null) metadata[PredictionModelMetadata.PurgeGapKey] = gap;
        return metadata;
    }

    private static Dictionary<string, string> RegressionMetadata()
    {
        var metadata = Version2Metadata();
        metadata[PredictionModelMetadata.TargetTypeKey] = "regression";
        metadata[PredictionModelMetadata.OutputSemanticKey] = "log_return";
        metadata[PredictionModelMetadata.OutputUnitKey] = "dimensionless";
        metadata[PredictionModelMetadata.OutputConfidenceTypeKey] = "none";
        metadata[PredictionModelMetadata.TargetFormulaKey] = "log_future_close_over_anchor_close";
        return metadata;
    }

    /// <summary>Minimal <see cref="ILogger"/> that records each entry's level and rendered message.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public readonly List<(LogLevel Level, string Message)> Entries = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception exception,
            Func<TState, Exception, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    [Theory]
    [InlineData("ohlcv_minmax", PredictionFeatureMode.OhlcvMinMax)]
    [InlineData("log_return", PredictionFeatureMode.LogReturn)]
    [InlineData("zscore", PredictionFeatureMode.ZScoreStandardized)]
    [InlineData("zscore_joint", PredictionFeatureMode.ZScoreOhlcvJoint)]
    [InlineData("log_return_ohlc", PredictionFeatureMode.LogReturnOhlc)]
    [InlineData("  ZScore  ", PredictionFeatureMode.ZScoreStandardized)]
    public void ParseFeatureMode_KnownStrings_MapToEnum(string wire, PredictionFeatureMode expected)
        => Assert.Equal(expected, PredictionModelMetadata.ParseFeatureMode(wire));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("minmax")]
    [InlineData("OhlcvMinMax")]
    public void ParseFeatureMode_UnknownOrEmpty_ReturnsNull(string? wire)
        => Assert.Null(PredictionModelMetadata.ParseFeatureMode(wire));

    [Fact]
    public void Validate_NullMap_RejectsMissingSemanticContract()
        => Assert.Throws<InvalidOperationException>(() => Validate(null));

    [Fact]
    public void Validate_EmptyMap_RejectsMissingSemanticContract()
        => Assert.Throws<InvalidOperationException>(() => Validate(new Dictionary<string, string>()));

    [Fact]
    public void Validate_MatchingContract_DoesNotThrow()
    {
        var metadata = Version2Metadata();
        metadata[PredictionModelMetadata.ClassOrderKey] = "Up, Down, Neutral";
        Validate(metadata);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("12")]
    public void Validate_Version2PurgeGap_AcceptsCanonicalBarCount(string gap)
    {
        Validate(Version2Metadata(gap));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("-1")]
    [InlineData("01")]
    [InlineData("1.5")]
    public void Validate_Version2PurgeGap_RejectsMissingOrNoncanonicalValue(string? gap)
    {
        var metadata = Version2Metadata(gap);
        Assert.Throws<InvalidOperationException>(() => Validate(metadata));
    }

    [Fact]
    public void ReadOutputContract_Version2Regression_IsFrameworkNeutralAndTyped()
    {
        var metadata = Version2Metadata();
        metadata[PredictionModelMetadata.TargetTypeKey] = "regression";
        metadata[PredictionModelMetadata.OutputSemanticKey] = "log_return";
        metadata[PredictionModelMetadata.OutputUnitKey] = "dimensionless";
        metadata[PredictionModelMetadata.OutputTimeframeKey] = "weekly";
        metadata[PredictionModelMetadata.OutputConfidenceTypeKey] = "none";
        metadata[PredictionModelMetadata.TargetFormulaKey] = "log_future_close_over_anchor_close";

        var contract = PredictionModelMetadata.ReadOutputContract(metadata);

        Assert.NotNull(contract);
        Assert.Equal(PredictionOutputSemantic.LogReturn, contract.Semantic);
        Assert.Equal(PredictionOutputUnit.Dimensionless, contract.Unit);
        Assert.Equal(5, contract.HorizonBars);
        Assert.Equal(TimeframeType.Weekly, contract.Timeframe);
        Assert.Equal(ConfidenceType.None, contract.ConfidenceType);
        Validate(metadata);
    }

    [Fact]
    public void ReadOutputContract_RejectsContradictorySemanticAndHorizon()
    {
        var metadata = Version2Metadata();
        metadata[PredictionModelMetadata.OutputSemanticKey] = "log_return";
        Assert.Throws<InvalidOperationException>(() => PredictionModelMetadata.ReadOutputContract(metadata));

        metadata = Version2Metadata();
        metadata[PredictionModelMetadata.OutputHorizonKey] = "4";
        Assert.Throws<InvalidOperationException>(() => PredictionModelMetadata.ReadOutputContract(metadata));
    }

    [Theory]
    [InlineData("fixed_zscore", null, "[]", "")]
    [InlineData("fixed_zscore", "", "[]", "")]
    [InlineData("fixed_zscore", "model.onnx.scaler.json", null, "")]
    [InlineData("fixed_zscore", "model.onnx.scaler.json", "[]", null)]
    [InlineData("ohlcv_minmax", "model.onnx.scaler.json", "[]", "")]
    [InlineData("ohlcv_minmax", null, "[]", null)]
    [InlineData("ohlcv_minmax", null, null, "3.0")]
    [InlineData("unexpected", null, null, null)]
    public void Validate_PartialOrContradictoryFixedContract_Throws(
        string normalization, string? reference, string? lags, string? clip)
    {
        var metadata = new Dictionary<string, string>
        {
            ["feature_mode"] = "ohlcv_minmax", ["window_size"] = "10",
            ["normalization"] = normalization,
        };
        if (reference is not null) metadata["scaler_ref"] = reference;
        if (lags is not null) metadata["lags"] = lags;
        if (clip is not null) metadata["clip_sigma"] = clip;
        Assert.Throws<InvalidOperationException>(() => Validate(metadata));
    }

    [Fact]
    public void Validate_CompleteFixedContractAndCurrentNormalization_AreAccepted()
    {
        var fixedMetadata = Version2Metadata();
        fixedMetadata["normalization"] = "fixed_zscore";
        fixedMetadata["scaler_ref"] = "model.onnx.scaler.json";
        fixedMetadata["lags"] = "[]";
        fixedMetadata["clip_sigma"] = "";
        Validate(fixedMetadata);

        var normalMetadata = Version2Metadata();
        normalMetadata["normalization"] = "ohlcv_minmax";
        Validate(normalMetadata);
    }

    [Fact]
    public void Validate_LegacyComposedSpecWithOmittedDefaults_MatchesCurrentEmptyLags()
    {
        const string legacy = """{"channels":[{"kind":"price","price":"close"}]}""";
        var current = JsonSerializer.Serialize(new FeatureSpec
        {
            Channels = new[] { new FeatureChannel { Kind = FeatureChannelKind.Price, Price = PriceType.Close } },
        }, TrainingConfigJson.Options);
        var metadata = Version2Metadata();
        metadata[PredictionModelMetadata.FeatureModeKey] = "composed_features";
        metadata[PredictionModelMetadata.FeatureSpecKey] = legacy;
        PredictionModelMetadata.Validate(metadata, PredictionFeatureMode.ComposedFeatures, 10,
            Classes, NullLogger.Instance, current);

        metadata[PredictionModelMetadata.FeatureSpecKey] =
            """{"channels":[{"kind":"price","price":"open"}]}""";
        Assert.Throws<InvalidOperationException>(() => PredictionModelMetadata.Validate(metadata,
            PredictionFeatureMode.ComposedFeatures, 10, Classes, NullLogger.Instance, current));
        metadata[PredictionModelMetadata.FeatureSpecKey] =
            """{"channels":[{"kind":"price","price":"close"}],"lags":null}""";
        Assert.Throws<InvalidOperationException>(() => PredictionModelMetadata.Validate(metadata,
            PredictionFeatureMode.ComposedFeatures, 10, Classes, NullLogger.Instance, current));
    }

    [Fact]
    public void Validate_NestedLagsWithoutScaler_RejectsLegacyPreprocessing()
    {
        var metadata = new Dictionary<string, string>
        {
            [PredictionModelMetadata.FeatureModeKey] = "composed_features",
            [PredictionModelMetadata.WindowSizeKey] = "10",
            [PredictionModelMetadata.FeatureSpecKey] =
                """{"channels":[{"kind":"price","price":"close"}],"lags":[1]}""",
        };
        Assert.Throws<InvalidOperationException>(() => PredictionModelMetadata.RequiresFixedScaler(metadata));
    }

    [Fact]
    public void Validate_FeatureModeMismatch_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Validate(new Dictionary<string, string>
            {
                [PredictionModelMetadata.FeatureModeKey] = "zscore",
            }));
        Assert.Contains("feature mode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_UnknownFeatureMode_Throws()
        => Assert.Throws<InvalidOperationException>(() =>
            Validate(new Dictionary<string, string>
            {
                [PredictionModelMetadata.FeatureModeKey] = "bogus_mode",
            }));

    [Fact]
    public void Validate_WindowSizeMismatch_Throws()
        => Assert.Throws<InvalidOperationException>(() =>
            Validate(new Dictionary<string, string>
            {
                [PredictionModelMetadata.FeatureModeKey] = "ohlcv_minmax",
                [PredictionModelMetadata.WindowSizeKey] = "40",
            }));

    [Fact]
    public void Validate_WindowSizeNotAnInteger_Throws()
        => Assert.Throws<InvalidOperationException>(() =>
            Validate(new Dictionary<string, string>
            {
                [PredictionModelMetadata.FeatureModeKey] = "ohlcv_minmax",
                [PredictionModelMetadata.WindowSizeKey] = "ten",
            }));

    [Fact]
    public void Validate_ClassOrderMismatch_Throws()
        => Assert.Throws<InvalidOperationException>(() =>
            Validate(new Dictionary<string, string>
            {
                [PredictionModelMetadata.FeatureModeKey] = "ohlcv_minmax",
                [PredictionModelMetadata.ClassOrderKey] = "Up,Neutral,Down",
            }));

    [Fact]
    public void Validate_PresentKeysOnlyChecked_WhenFeatureModePresent()
    {
        var metadata = Version2Metadata();
        metadata.Remove(PredictionModelMetadata.ClassOrderKey);
        Validate(metadata);
    }

    [Fact]
    public void Validate_NonEmptyMapMissingFeatureMode_Throws()
    {
        // A known contract key is present but feature_mode is absent -> partial/corrupt contract.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Validate(new Dictionary<string, string>
            {
                [PredictionModelMetadata.WindowSizeKey] = "10",
            }));
        Assert.Contains("feature_mode", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RegressionTarget_SkipsClassOrderCrossCheck()
    {
        // A regression model keeps class_order present (for a stable key set) but its
        // output index order is meaningless, so a mismatch must NOT be rejected.
        var metadata = RegressionMetadata();
        metadata[PredictionModelMetadata.ClassOrderKey] = "Up,Neutral,Down";
        Validate(metadata);
    }

    [Fact]
    public void Validate_RegressionTarget_StillEnforcesFeatureModeAndWindow()
    {
        // target_type=regression relaxes only the class_order check; feature_mode / window_size
        // remain load-bearing.
        Assert.Throws<InvalidOperationException>(() =>
            Validate(new Dictionary<string, string>
            {
                [PredictionModelMetadata.FeatureModeKey] = "zscore",
                [PredictionModelMetadata.TargetTypeKey] = PredictionModelMetadata.TargetTypeRegression,
            }));

        Assert.Throws<InvalidOperationException>(() =>
            Validate(new Dictionary<string, string>
            {
                [PredictionModelMetadata.FeatureModeKey] = "ohlcv_minmax",
                [PredictionModelMetadata.WindowSizeKey] = "40",
                [PredictionModelMetadata.TargetTypeKey] = PredictionModelMetadata.TargetTypeRegression,
            }));
    }

    [Theory]
    [InlineData("classification")]
    [InlineData("  Classification  ")]
    public void Validate_ClassificationTarget_KeepsClassOrderCrossCheck(string targetType)
    {
        // An explicit classification value (or any casing/whitespace variant) leaves the
        // class_order enforcement in place.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Validate(new Dictionary<string, string>
            {
                [PredictionModelMetadata.FeatureModeKey] = "ohlcv_minmax",
                [PredictionModelMetadata.ClassOrderKey] = "Up,Neutral,Down",
                [PredictionModelMetadata.TargetTypeKey] = targetType,
            }));
        Assert.Contains("class order", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AbsentTargetType_DefaultsToClassification()
    {
        // No target_type key => historical behavior: class_order mismatch is rejected.
        Assert.Throws<InvalidOperationException>(() =>
            Validate(new Dictionary<string, string>
            {
                [PredictionModelMetadata.FeatureModeKey] = "ohlcv_minmax",
                [PredictionModelMetadata.ClassOrderKey] = "Up,Neutral,Down",
            }));
    }

    [Fact]
    public void Validate_UnrecognizedTargetType_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Validate(new Dictionary<string, string>
            {
                [PredictionModelMetadata.FeatureModeKey] = "ohlcv_minmax",
                [PredictionModelMetadata.WindowSizeKey] = "10",
                [PredictionModelMetadata.TargetTypeKey] = "ranking",
            }));
        Assert.Contains(PredictionModelMetadata.TargetTypeKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_TargetTypeKeyOnly_MissingFeatureMode_Throws()
    {
        // target_type is a known contract key; present without feature_mode => partial/corrupt.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Validate(new Dictionary<string, string>
            {
                [PredictionModelMetadata.TargetTypeKey] = PredictionModelMetadata.TargetTypeRegression,
            }));
        Assert.Contains("feature_mode", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RegressionTarget_LogsTargetInContractLine()
    {
        var logger = new CapturingLogger();
        var map = RegressionMetadata();

        PredictionModelMetadata.Validate(map, PredictionFeatureMode.OhlcvMinMax, 10, Classes, logger);

        var info = Assert.Single(logger.Entries, e => e.Level == LogLevel.Information).Message;
        Assert.Contains(PredictionModelMetadata.TargetTypeRegression, info, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ForeignKeyOnly_RejectsMissingSemanticContract()
    {
        Assert.Throws<InvalidOperationException>(() => Validate(new Dictionary<string, string>
        {
            ["converted_by"] = "some_other_tool",
        }));
    }

    [Fact]
    public void Validate_LogsTrainingAndValidationSpans()
    {
        var logger = new CapturingLogger();
        var map = Version2Metadata();
        map[PredictionModelMetadata.TrainingStartKey] = "2015-01-02";
        map[PredictionModelMetadata.TrainingEndKey] = "2022-12-30";
        map[PredictionModelMetadata.ValidationStartKey] = "2023-01-03";
        map[PredictionModelMetadata.ValidationEndKey] = "2024-06-28";

        PredictionModelMetadata.Validate(
            map, PredictionFeatureMode.OhlcvMinMax, 10, Classes, logger);

        var info = Assert.Single(logger.Entries, e => e.Level == LogLevel.Information).Message;
        Assert.Contains("2015-01-02", info);
        Assert.Contains("2022-12-30", info);
        Assert.Contains("2023-01-03", info);
        Assert.Contains("2024-06-28", info);
    }

    [Fact]
    public void Validate_LogsPredictionHorizon_WhenPresent()
    {
        var logger = new CapturingLogger();
        var map = Version2Metadata();

        PredictionModelMetadata.Validate(map, PredictionFeatureMode.OhlcvMinMax, 10, Classes, logger);

        var info = Assert.Single(logger.Entries, e => e.Level == LogLevel.Information).Message;
        Assert.Contains("5", info);
    }
}
