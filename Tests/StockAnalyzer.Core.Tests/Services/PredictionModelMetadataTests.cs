using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Tests.Services;

public class PredictionModelMetadataTests
{
    private static readonly string[] Classes = { "Up", "Down", "Neutral" };

    private static void Validate(
        IReadOnlyDictionary<string, string>? map,
        PredictionFeatureMode mode = PredictionFeatureMode.OhlcvMinMax,
        int window = 10)
        => PredictionModelMetadata.Validate(map, mode, window, Classes, NullLogger.Instance);

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
    public void Validate_NullMap_DoesNotThrow() => Validate(null);

    [Fact]
    public void Validate_EmptyMap_DoesNotThrow() => Validate(new Dictionary<string, string>());

    [Fact]
    public void Validate_MatchingContract_DoesNotThrow()
    {
        Validate(new Dictionary<string, string>
        {
            [PredictionModelMetadata.FeatureModeKey] = "ohlcv_minmax",
            [PredictionModelMetadata.WindowSizeKey] = "10",
            [PredictionModelMetadata.ClassOrderKey] = "Up, Down, Neutral",
        });
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
        // feature_mode present and matching, class_order absent -> only present keys checked, no throw.
        Validate(new Dictionary<string, string>
        {
            [PredictionModelMetadata.FeatureModeKey] = "ohlcv_minmax",
            [PredictionModelMetadata.WindowSizeKey] = "10",
        });
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
        Validate(new Dictionary<string, string>
        {
            [PredictionModelMetadata.FeatureModeKey] = "ohlcv_minmax",
            [PredictionModelMetadata.WindowSizeKey] = "10",
            [PredictionModelMetadata.ClassOrderKey] = "Up,Neutral,Down",
            [PredictionModelMetadata.TargetTypeKey] = PredictionModelMetadata.TargetTypeRegression,
        });
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
        var map = new Dictionary<string, string>
        {
            [PredictionModelMetadata.FeatureModeKey] = "ohlcv_minmax",
            [PredictionModelMetadata.WindowSizeKey] = "10",
            [PredictionModelMetadata.ClassOrderKey] = "Up,Down,Neutral",
            [PredictionModelMetadata.TargetTypeKey] = PredictionModelMetadata.TargetTypeRegression,
        };

        PredictionModelMetadata.Validate(map, PredictionFeatureMode.OhlcvMinMax, 10, Classes, logger);

        var info = Assert.Single(logger.Entries, e => e.Level == LogLevel.Information).Message;
        Assert.Contains(PredictionModelMetadata.TargetTypeRegression, info, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ForeignKeyOnly_DoesNotThrow()
    {
        // A metadata_props entry stamped by an unrelated tool is not a contract key -> ignored.
        Validate(new Dictionary<string, string>
        {
            ["converted_by"] = "some_other_tool",
        });
    }

    [Fact]
    public void Validate_LogsTrainingAndValidationSpans()
    {
        var logger = new CapturingLogger();
        var map = new Dictionary<string, string>
        {
            [PredictionModelMetadata.FeatureModeKey] = "ohlcv_minmax",
            [PredictionModelMetadata.WindowSizeKey] = "10",
            [PredictionModelMetadata.ClassOrderKey] = "Up,Down,Neutral",
            [PredictionModelMetadata.TrainingStartKey] = "2015-01-02",
            [PredictionModelMetadata.TrainingEndKey] = "2022-12-30",
            [PredictionModelMetadata.ValidationStartKey] = "2023-01-03",
            [PredictionModelMetadata.ValidationEndKey] = "2024-06-28",
        };

        PredictionModelMetadata.Validate(
            map, PredictionFeatureMode.OhlcvMinMax, 10, Classes, logger);

        var info = Assert.Single(logger.Entries, e => e.Level == LogLevel.Information).Message;
        Assert.Contains("2015-01-02", info);
        Assert.Contains("2022-12-30", info);
        Assert.Contains("2023-01-03", info);
        Assert.Contains("2024-06-28", info);
    }
}
