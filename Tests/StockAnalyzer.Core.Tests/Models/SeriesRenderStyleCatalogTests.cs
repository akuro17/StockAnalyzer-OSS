using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Rendering;
using StockAnalyzer.Core.Serialization;
using StockAnalyzer.Core.Utilities;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

public sealed class SeriesRenderStyleCatalogTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        TypeInfoResolver = WorkspacePolymorphicResolver.CreateResolver(),
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new IndicatorColorJsonConverter() }
    };

    [Fact]
    public void SelectablePrimitives_PinsExact12SingleSeriesTypes_AndExcludesColorStripAndMultiSeriesTypes()
    {
        var expected = new[]
        {
            SeriesPrimitiveType.Area,
            SeriesPrimitiveType.DashedLine,
            SeriesPrimitiveType.Dot,
            SeriesPrimitiveType.DynamicStyling,
            SeriesPrimitiveType.Histogram,
            SeriesPrimitiveType.HorizontalSegment,
            SeriesPrimitiveType.Line,
            SeriesPrimitiveType.Lollipop,
            SeriesPrimitiveType.MarkerSymbol,
            SeriesPrimitiveType.PositiveNegativeArea,
            SeriesPrimitiveType.Spline,
            SeriesPrimitiveType.StepLine
        };

        Assert.Equal(expected, SeriesRenderStyleCatalog.SelectablePrimitives);
        foreach (var item in expected)
        {
            Assert.True(SeriesRenderStyleCatalog.IsSelectablePrimitive(item));
        }

        var excluded = new[]
        {
            SeriesPrimitiveType.BandCloud,
            SeriesPrimitiveType.FloatingBar,
            SeriesPrimitiveType.StackedArea,
            SeriesPrimitiveType.Bubble,
            SeriesPrimitiveType.ColorStrip,
            SeriesPrimitiveType.Vector,
            SeriesPrimitiveType.Waterfall
        };

        foreach (var item in excluded)
        {
            Assert.False(SeriesRenderStyleCatalog.IsSelectablePrimitive(item));
            Assert.DoesNotContain(item, SeriesRenderStyleCatalog.SelectablePrimitives);
        }
    }

    [Theory]
    [InlineData(IndicatorType.VolumeProfile, false)]
    [InlineData(IndicatorType.TimeAtPrice, false)]
    [InlineData(IndicatorType.MarketStructureShift, false)]
    [InlineData(IndicatorType.Renko, false)]
    [InlineData(IndicatorType.RenkoOverlay, false)]
    [InlineData(IndicatorType.Kagi, false)]
    [InlineData(IndicatorType.KagiOverlay, false)]
    [InlineData(IndicatorType.PointAndFigure, false)]
    [InlineData(IndicatorType.PointAndFigureOverlay, false)]
    [InlineData(IndicatorType.ThreeLineBreak, false)]
    [InlineData(IndicatorType.SMA, true)]
    [InlineData(IndicatorType.MACD, true)]
    [InlineData(IndicatorType.Ichimoku, true)]
    [InlineData(IndicatorType.VolumeProfileLevels, true)]
    [InlineData(IndicatorType.ZigZag, true)]
    [InlineData(null, true)]
    public void SupportsRenderStyleSelection_ReturnsExpectedCapability(IndicatorType? type, bool expected)
    {
        Assert.Equal(expected, SeriesRenderStyleCatalog.SupportsRenderStyleSelection(type));
    }

    [Fact]
    public void ResolveEffectivePrimitive_FollowsLegacyDefaultAndOverridePrecedence()
    {
        var parSar = new CoreIndicatorSettings { TypeEnum = IndicatorType.ParabolicSAR };
        Assert.Equal(SeriesPrimitiveType.Dot, SeriesRenderStyleCatalog.ResolveEffectivePrimitive(parSar, "Main"));

        var dottedLine = new CoreIndicatorSettings { TypeEnum = IndicatorType.SMA, Style = CoreLineStyle.Dot };
        Assert.Equal(SeriesPrimitiveType.DashedLine, SeriesRenderStyleCatalog.ResolveEffectivePrimitive(dottedLine, "Main"));

        var dashDotLine = new CoreIndicatorSettings { TypeEnum = IndicatorType.SMA, Style = CoreLineStyle.DashDot };
        Assert.Equal(SeriesPrimitiveType.DashedLine, SeriesRenderStyleCatalog.ResolveEffectivePrimitive(dashDotLine, "Main"));

        var stepLine = new CoreIndicatorSettings { TypeEnum = IndicatorType.SMA, Style = CoreLineStyle.Step };
        Assert.Equal(SeriesPrimitiveType.StepLine, SeriesRenderStyleCatalog.ResolveEffectivePrimitive(stepLine, "Main"));

        var macdHist = new CoreIndicatorSettings
        {
            TypeEnum = IndicatorType.MACD,
            SeriesColors = new ObservableCollection<SeriesColorConfig>
            {
                new() { Name = "MACD", TargetSeries = new List<string> { "Main" } },
                new() { Name = "Histogram", TargetSeries = new List<string> { "Histogram" } }
            }
        };
        Assert.Equal(SeriesPrimitiveType.Line, SeriesRenderStyleCatalog.ResolveEffectivePrimitive(macdHist, "Main"));
        Assert.Equal(SeriesPrimitiveType.Histogram, SeriesRenderStyleCatalog.ResolveEffectivePrimitive(macdHist, "Histogram"));

        macdHist.SeriesColors[1].RenderPrimitive = SeriesPrimitiveType.Lollipop;
        Assert.Equal(SeriesPrimitiveType.Lollipop, SeriesRenderStyleCatalog.ResolveEffectivePrimitive(macdHist, "Histogram"));

        var sma = new CoreIndicatorSettings { TypeEnum = IndicatorType.SMA, MainRenderPrimitive = SeriesPrimitiveType.Area };
        Assert.Equal(SeriesPrimitiveType.Area, SeriesRenderStyleCatalog.ResolveEffectivePrimitive(sma, ""));
        Assert.Equal(SeriesPrimitiveType.Area, SeriesRenderStyleCatalog.ResolveEffectivePrimitive(sma, "Main"));
        Assert.Equal(SeriesPrimitiveType.Line, SeriesRenderStyleCatalog.ResolveEffectivePrimitive(sma, "Other"));
    }

    [Fact]
    public void ResolveExplicitPrimitive_SanitizesNonSelectableValuesBackToDefault()
    {
        var sma = new CoreIndicatorSettings
        {
            TypeEnum = IndicatorType.SMA,
            MainRenderPrimitive = SeriesPrimitiveType.BandCloud
        };
        Assert.Null(SeriesRenderStyleCatalog.ResolveExplicitPrimitive(sma, "Main"));
        Assert.Equal(SeriesPrimitiveType.Line, SeriesRenderStyleCatalog.ResolveEffectivePrimitive(sma, "Main"));

        var multi = new CoreIndicatorSettings
        {
            TypeEnum = IndicatorType.BB,
            SeriesColors = new ObservableCollection<SeriesColorConfig>
            {
                new()
                {
                    Name = "Bands",
                    TargetSeries = new List<string> { "Upper", "Lower" },
                    RenderPrimitive = (SeriesPrimitiveType)999
                }
            }
        };
        Assert.Null(SeriesRenderStyleCatalog.ResolveExplicitPrimitive(multi, "Upper"));
        Assert.Equal(SeriesPrimitiveType.Line, SeriesRenderStyleCatalog.ResolveEffectivePrimitive(multi, "Upper"));
        Assert.Equal(SeriesPrimitiveType.Line, SeriesRenderStyleCatalog.ResolveEffectivePrimitive(multi, "Lower"));
    }

    [Fact]
    public void ResolveNullGapPolicy_ConnectsThroughOnlyForZigZag()
    {
        Assert.Equal(NullGapPolicy.ConnectThrough, SeriesRenderStyleCatalog.ResolveNullGapPolicy(IndicatorType.ZigZag));
        Assert.Equal(NullGapPolicy.BreakPath, SeriesRenderStyleCatalog.ResolveNullGapPolicy(IndicatorType.SMA));
        Assert.Equal(NullGapPolicy.BreakPath, SeriesRenderStyleCatalog.ResolveNullGapPolicy(null));
    }

    [Fact]
    public void IsSeriesVisible_And_ShouldExcludeFromYRange_RespectVisibilitySignalsAndSanitizedOverride()
    {
        var sma = new CoreIndicatorSettings
        {
            TypeEnum = IndicatorType.SMA,
            IsMainSeriesVisible = false
        };
        Assert.False(SeriesRenderStyleCatalog.IsSeriesVisible(sma, "Main"));
        Assert.False(SeriesRenderStyleCatalog.IsSeriesVisible(sma, ""));
        Assert.True(SeriesRenderStyleCatalog.ShouldExcludeFromYRange(sma, "Main"));

        sma.IsMainSeriesVisible = true;
        sma.MainRenderPrimitive = SeriesPrimitiveType.ColorStrip;
        // ColorStrip is excluded from user-selectable dropdown menu so explicit override sanitizes to default Line
        Assert.Null(SeriesRenderStyleCatalog.ResolveExplicitPrimitive(sma, "Main"));
        Assert.True(SeriesRenderStyleCatalog.IsSeriesVisible(sma, "Main"));
        Assert.False(SeriesRenderStyleCatalog.ShouldExcludeFromYRange(sma, "Main"));

        sma.MainRenderPrimitive = SeriesPrimitiveType.Area;
        Assert.False(SeriesRenderStyleCatalog.ShouldExcludeFromYRange(sma, "Main"));
        Assert.True(SeriesRenderStyleCatalog.ShouldExcludeFromYRange(sma, "BuySignals"));

        var bb = new CoreIndicatorSettings
        {
            TypeEnum = IndicatorType.BB,
            SeriesColors = new ObservableCollection<SeriesColorConfig>
            {
                new() { Name = "Middle", TargetSeries = new List<string> { "Main" }, IsVisible = true },
                new() { Name = "Bands", TargetSeries = new List<string> { "Upper", "Lower" }, IsVisible = false }
            }
        };
        Assert.False(SeriesRenderStyleCatalog.ShouldExcludeFromYRange(bb, "Main"));
        Assert.True(SeriesRenderStyleCatalog.ShouldExcludeFromYRange(bb, "Upper"));
        Assert.True(SeriesRenderStyleCatalog.ShouldExcludeFromYRange(bb, "Lower"));
    }

    [Fact]
    public void AccumulateRange_SkipsHiddenMultiSeriesDescriptorsAndHiddenStackedLayers()
    {
        var setting = new CoreIndicatorSettings
        {
            TypeEnum = IndicatorType.BB,
            SeriesColors = new ObservableCollection<SeriesColorConfig>
            {
                new() { Name = "Middle", TargetSeries = new List<string> { "Main" }, IsVisible = true },
                new() { Name = "Bands", TargetSeries = new List<string> { "Upper", "Lower" }, IsVisible = false }
            }
        };

        var result = IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
        {
            ["Main"] = new decimal?[] { 10m, 12m },
            ["Upper"] = new decimal?[] { 50m, 60m },
            ["Lower"] = new decimal?[] { 1m, 2m }
        });

        var descriptors = new List<SeriesPrimitiveDescriptor>
        {
            new() { PrimitiveType = SeriesPrimitiveType.Line, PrimarySeriesName = "Main" },
            new() { PrimitiveType = SeriesPrimitiveType.BandCloud, PrimarySeriesName = "Upper", SecondarySeriesName = "Lower" }
        };

        decimal min = decimal.MaxValue;
        decimal max = decimal.MinValue;
        bool hasValue = false;

        SeriesPrimitiveValueRangeHelper.AccumulateRange(result, descriptors, setting, 2, PriceScaleType.Linear, ref min, ref max, ref hasValue);
        Assert.True(hasValue);
        Assert.Equal(10m, min);
        Assert.Equal(12m, max);

        // If PrimarySeries is visible and only SecondarySeries is hidden, BandCloud must still be skipped as a unit.
        setting.SeriesColors[1] = new SeriesColorConfig { Name = "Upper", TargetSeries = new List<string> { "Upper" }, IsVisible = true };
        setting.SeriesColors.Add(new SeriesColorConfig { Name = "Lower", TargetSeries = new List<string> { "Lower" }, IsVisible = false });

        min = decimal.MaxValue;
        max = decimal.MinValue;
        hasValue = false;
        SeriesPrimitiveValueRangeHelper.AccumulateRange(result, descriptors, setting, 2, PriceScaleType.Linear, ref min, ref max, ref hasValue);
        Assert.True(hasValue);
        Assert.Equal(10m, min);
        Assert.Equal(12m, max);
    }

    [Fact]
    public void VisualOnlyProperties_PreserveMathematicalVersion_AndSurviveCloneSnapshotDuplicateAndJsonRoundTrip()
    {
        var original = new CoreIndicatorSettings
        {
            TypeEnum = IndicatorType.MACD,
            IsMainSeriesVisible = false,
            MainRenderPrimitive = SeriesPrimitiveType.Lollipop,
            SeriesColors = new ObservableCollection<SeriesColorConfig>
            {
                new()
                {
                    Name = "MACD",
                    DisplayName = "MACD Line",
                    TargetSeries = new List<string> { "Main" },
                    IsVisible = false,
                    RenderPrimitive = SeriesPrimitiveType.Spline
                },
                new()
                {
                    Name = "Signal",
                    DisplayName = "Signal Line",
                    TargetSeries = new List<string> { "Signal" },
                    IsVisible = true,
                    RenderPrimitive = SeriesPrimitiveType.ColorStrip
                }
            }
        };

        long initialMathVersion = original.MathematicalVersion;
        original.IsMainSeriesVisible = true;
        original.MainRenderPrimitive = SeriesPrimitiveType.Area;
        original.SeriesColors[0].IsVisible = false;
        original.SeriesColors[0].RenderPrimitive = SeriesPrimitiveType.DynamicStyling;
        Assert.Equal(initialMathVersion, original.MathematicalVersion);

        var cloned = original.Clone();
        Assert.True(cloned.IsMainSeriesVisible);
        Assert.Equal(SeriesPrimitiveType.Area, cloned.MainRenderPrimitive);
        Assert.False(cloned.SeriesColors[0].IsVisible);
        Assert.Equal(SeriesPrimitiveType.DynamicStyling, cloned.SeriesColors[0].RenderPrimitive);
        Assert.True(cloned.SeriesColors[1].IsVisible);
        Assert.Equal(SeriesPrimitiveType.ColorStrip, cloned.SeriesColors[1].RenderPrimitive);
        Assert.Equal(initialMathVersion, cloned.MathematicalVersion);

        var snapshot = original.Snapshot();
        Assert.Equal(original.Id, snapshot.Id);
        Assert.Equal(original.IsMainSeriesVisible, snapshot.IsMainSeriesVisible);
        Assert.Equal(original.MainRenderPrimitive, snapshot.MainRenderPrimitive);
        Assert.False(snapshot.SeriesColors[0].IsVisible);
        Assert.Equal(SeriesPrimitiveType.DynamicStyling, snapshot.SeriesColors[0].RenderPrimitive);

        var json = JsonSerializer.Serialize(original, SerializerOptions);
        var roundTripped = JsonSerializer.Deserialize<CoreIndicatorSettings>(json, SerializerOptions);
        Assert.NotNull(roundTripped);
        Assert.True(roundTripped!.IsMainSeriesVisible);
        Assert.Equal(SeriesPrimitiveType.Area, roundTripped.MainRenderPrimitive);
        Assert.False(roundTripped.SeriesColors[0].IsVisible);
        Assert.Equal(SeriesPrimitiveType.DynamicStyling, roundTripped.SeriesColors[0].RenderPrimitive);
        Assert.Equal(SeriesPrimitiveType.ColorStrip, roundTripped.SeriesColors[1].RenderPrimitive);
    }

    [Fact]
    public void LegacyJsonWithoutNewProperties_DeserializesWithBackwardCompatibleDefaults()
    {
        const string legacyJson = """
            {
              "TypeEnum": 0,
              "Style": 0,
              "IsOverlay": true,
              "SeriesColors": [
                {
                  "Name": "Main",
                  "DisplayName": "Main",
                  "TargetSeries": ["Main"]
                }
              ]
            }
            """;

        var loaded = JsonSerializer.Deserialize<CoreIndicatorSettings>(legacyJson, SerializerOptions);
        Assert.NotNull(loaded);
        Assert.True(loaded!.IsMainSeriesVisible);
        Assert.Null(loaded.MainRenderPrimitive);
        Assert.Single(loaded.SeriesColors);
        Assert.True(loaded.SeriesColors[0].IsVisible);
        Assert.Null(loaded.SeriesColors[0].RenderPrimitive);
        Assert.True(SeriesRenderStyleCatalog.IsSeriesVisible(loaded, "Main"));
        Assert.Equal(SeriesPrimitiveType.Line, SeriesRenderStyleCatalog.ResolveEffectivePrimitive(loaded, "Main"));
    }

    [Fact]
    public void ResolveDefaultPrimitive_ReturnsHistogramForVolume()
    {
        var volume = new CoreIndicatorSettings { TypeEnum = IndicatorType.Volume };
        Assert.Equal(SeriesPrimitiveType.Histogram, SeriesRenderStyleCatalog.ResolveDefaultPrimitive(volume, "Main"));
        Assert.Equal(SeriesPrimitiveType.Histogram, SeriesRenderStyleCatalog.ResolveEffectivePrimitive(volume, "Main"));

        volume.MainRenderPrimitive = SeriesPrimitiveType.Area;
        Assert.Equal(SeriesPrimitiveType.Area, SeriesRenderStyleCatalog.ResolveEffectivePrimitive(volume, "Main"));
    }

    [Fact]
    public void FindSeriesConfig_IsCaseInsensitive_AndNormalizesIchimokuTenkanSenAlias()
    {
        var ichimokuMainTarget = new CoreIndicatorSettings
        {
            TypeEnum = IndicatorType.Ichimoku,
            SeriesColors = new ObservableCollection<SeriesColorConfig>
            {
                new() { Name = "Tenkan", TargetSeries = new List<string> { "Main" }, IsVisible = false, RenderPrimitive = SeriesPrimitiveType.Dot }
            }
        };

        Assert.False(SeriesRenderStyleCatalog.IsSeriesVisible(ichimokuMainTarget, "TenkanSen"));
        Assert.False(SeriesRenderStyleCatalog.IsSeriesVisible(ichimokuMainTarget, "tenkansen"));
        Assert.True(SeriesRenderStyleCatalog.ShouldExcludeFromYRange(ichimokuMainTarget, "TenkanSen"));
        Assert.Equal(SeriesPrimitiveType.Dot, SeriesRenderStyleCatalog.ResolveExplicitPrimitive(ichimokuMainTarget, "TenkanSen"));

        var ichimokuTenkanTarget = new CoreIndicatorSettings
        {
            TypeEnum = IndicatorType.Ichimoku,
            SeriesColors = new ObservableCollection<SeriesColorConfig>
            {
                new() { Name = "Tenkan", TargetSeries = new List<string> { "TenkanSen" }, IsVisible = false, RenderPrimitive = SeriesPrimitiveType.Lollipop }
            }
        };

        Assert.False(SeriesRenderStyleCatalog.IsSeriesVisible(ichimokuTenkanTarget, "Main"));
        Assert.True(SeriesRenderStyleCatalog.ShouldExcludeFromYRange(ichimokuTenkanTarget, "Main"));
        Assert.Equal(SeriesPrimitiveType.Lollipop, SeriesRenderStyleCatalog.ResolveExplicitPrimitive(ichimokuTenkanTarget, "Main"));

        var caseDiff = new CoreIndicatorSettings
        {
            TypeEnum = IndicatorType.BB,
            SeriesColors = new ObservableCollection<SeriesColorConfig>
            {
                new() { Name = "Bands", TargetSeries = new List<string> { "upper" }, IsVisible = false }
            }
        };
        Assert.False(SeriesRenderStyleCatalog.IsSeriesVisible(caseDiff, "Upper"));
    }

    [Fact]
    public void NormalizePrimitiveOverride_ReturnsNullWhenMatchingDefault_AndPreservesExplicitOverrides()
    {
        var volume = new CoreIndicatorSettings { TypeEnum = IndicatorType.Volume };
        Assert.Null(SeriesRenderStyleCatalog.NormalizePrimitiveOverride(volume, "Main", SeriesPrimitiveType.Histogram));
        Assert.Equal(SeriesPrimitiveType.Line, SeriesRenderStyleCatalog.NormalizePrimitiveOverride(volume, "Main", SeriesPrimitiveType.Line));

        var sma = new CoreIndicatorSettings { TypeEnum = IndicatorType.SMA };
        Assert.Null(SeriesRenderStyleCatalog.NormalizePrimitiveOverride(sma, "Main", SeriesPrimitiveType.Line));
        Assert.Equal(SeriesPrimitiveType.Area, SeriesRenderStyleCatalog.NormalizePrimitiveOverride(sma, "Main", SeriesPrimitiveType.Area));

        var stepSma = new CoreIndicatorSettings { TypeEnum = IndicatorType.SMA, Style = CoreLineStyle.Step };
        Assert.Null(SeriesRenderStyleCatalog.NormalizePrimitiveOverride(stepSma, "Main", SeriesPrimitiveType.StepLine));
        Assert.Equal(SeriesPrimitiveType.Line, SeriesRenderStyleCatalog.NormalizePrimitiveOverride(stepSma, "Main", SeriesPrimitiveType.Line));

        Assert.Null(SeriesRenderStyleCatalog.NormalizePrimitiveOverride(sma, "Main", SeriesPrimitiveType.BandCloud));
    }

    [Fact]
    public void RequiresZeroBaselineInYRange_RespectsOverlayLogAndEffectivePrimitive()
    {
        var histSub = new CoreIndicatorSettings
        {
            TypeEnum = IndicatorType.SMA,
            IsOverlay = false,
            MainRenderPrimitive = SeriesPrimitiveType.Histogram
        };
        Assert.True(SeriesRenderStyleCatalog.RequiresZeroBaselineInYRange(histSub, "Main", PriceScaleType.Linear));
        Assert.False(SeriesRenderStyleCatalog.RequiresZeroBaselineInYRange(histSub, "Main", PriceScaleType.Log));

        histSub.IsOverlay = true;
        Assert.False(SeriesRenderStyleCatalog.RequiresZeroBaselineInYRange(histSub, "Main", PriceScaleType.Linear));

        histSub.IsOverlay = false;
        histSub.MainRenderPrimitive = SeriesPrimitiveType.Lollipop;
        Assert.True(SeriesRenderStyleCatalog.RequiresZeroBaselineInYRange(histSub, "Main", PriceScaleType.Linear));

        histSub.MainRenderPrimitive = SeriesPrimitiveType.Line;
        Assert.False(SeriesRenderStyleCatalog.RequiresZeroBaselineInYRange(histSub, "Main", PriceScaleType.Linear));

        histSub.MainRenderPrimitive = SeriesPrimitiveType.PositiveNegativeArea;
        Assert.False(SeriesRenderStyleCatalog.RequiresZeroBaselineInYRange(histSub, "Main", PriceScaleType.Linear));
        histSub.IncludeZeroBaseline = true;
        Assert.True(SeriesRenderStyleCatalog.RequiresZeroBaselineInYRange(histSub, "Main", PriceScaleType.Linear));
    }

    [Fact]
    public void AccumulateRange_SyncsWithEffectivePrimitiveOverride_AndZeroBaselineRequirement()
    {
        var setting = new CoreIndicatorSettings
        {
            TypeEnum = IndicatorType.RSI,
            IsOverlay = false,
            MainRenderPrimitive = SeriesPrimitiveType.Line
        };

        var result = IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
        {
            ["Main"] = new decimal?[] { 95m, 105m }
        });

        var pnaDescriptors = new List<SeriesPrimitiveDescriptor>
        {
            new() { PrimitiveType = SeriesPrimitiveType.PositiveNegativeArea, PrimarySeriesName = "Main", BaselineValue = 0m }
        };

        decimal min = decimal.MaxValue;
        decimal max = decimal.MinValue;
        bool hasValue = false;

        // PositiveNegativeArea overridden to Line must NOT drag Y-range down to BaselineValue = 0m.
        SeriesPrimitiveValueRangeHelper.AccumulateRange(result, pnaDescriptors, setting, 2, PriceScaleType.Linear, ref min, ref max, ref hasValue);
        Assert.True(hasValue);
        Assert.Equal(95m, min);
        Assert.Equal(105m, max);

        // Negative-only Line overridden to Histogram on non-overlay linear panel must include 0m ceiling.
        var negSetting = new CoreIndicatorSettings
        {
            TypeEnum = IndicatorType.Momentum,
            IsOverlay = false,
            MainRenderPrimitive = SeriesPrimitiveType.Histogram
        };
        var negResult = IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
        {
            ["Main"] = new decimal?[] { -15m, -5m }
        });
        var lineDescriptors = new List<SeriesPrimitiveDescriptor>
        {
            new() { PrimitiveType = SeriesPrimitiveType.Line, PrimarySeriesName = "Main" }
        };

        min = decimal.MaxValue;
        max = decimal.MinValue;
        hasValue = false;
        SeriesPrimitiveValueRangeHelper.AccumulateRange(negResult, lineDescriptors, negSetting, 2, PriceScaleType.Linear, ref min, ref max, ref hasValue);
        Assert.True(hasValue);
        Assert.Equal(-15m, min);
        Assert.Equal(0m, max);
    }
}
