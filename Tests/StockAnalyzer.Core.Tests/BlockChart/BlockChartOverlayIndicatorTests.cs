using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Chart;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using Xunit;
using static StockAnalyzer.Core.Tests.BlockChart.BlockChartTestData;

namespace StockAnalyzer.Core.Tests.BlockChart;

/// <summary>
/// T4: the four registered overlay indicators (Renko, Point and Figure, Kagi, Three Line Break): registration, result shape (TC-15 non-render part), display names (owner supplement 4), defaults and their drift
/// guard, independence from the non-time-series chart settings (owner confirmation), and the protection of the direction flag from price normalization (owner supplement 5).
/// </summary>
public class BlockChartOverlayIndicatorTests
{
    private static readonly IndicatorType[] FourTypes = { IndicatorType.Renko, IndicatorType.PointAndFigure, IndicatorType.Kagi, IndicatorType.ThreeLineBreak };
    private static readonly IndicatorType[] TypesWithUpdate = { IndicatorType.Renko, IndicatorType.PointAndFigure };
    private static readonly IndicatorType[] TypesWithoutUpdate = { IndicatorType.Kagi, IndicatorType.ThreeLineBreak };

    private static CoreIndicatorParameterBase DefaultParameter(IndicatorType type) => type switch
    {
        IndicatorType.Renko => new CoreRenkoChartParameter(),
        IndicatorType.PointAndFigure => new CorePointAndFigureChartParameter(),
        IndicatorType.Kagi => new CoreKagiChartParameter(),
        IndicatorType.ThreeLineBreak => new CoreThreeLineBreakChartParameter(),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static CoreIndicatorSettings DefaultSetting(IndicatorType type)
        => DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == type);

    // --- registration and result shape (TC-15, non-render part) -------------------------------------------------------------------------------------

    [Fact]
    public void TheFourTypes_AreRegistered_AndBlockChartOverlayTypesNamesExactlyThem()
    {
        foreach (var type in FourTypes)
        {
            Assert.True(IndicatorFactory.IsRegisteredStatic(type), type.ToString());
            Assert.True(BlockChartOverlayTypes.Contains(type));
            Assert.NotNull(IndicatorFactory.CreateStatic(type, DefaultParameter(type)));
        }

        Assert.Equal(FourTypes.OrderBy(t => t.ToString(), StringComparer.Ordinal), BlockChartOverlayTypes.All.OrderBy(t => t.ToString(), StringComparer.Ordinal));
    }

    [Fact]
    public void TheLegacyPlaceholderTypesStayUnregistered()
    {
        foreach (var type in new[] { IndicatorType.RenkoOverlay, IndicatorType.KagiOverlay, IndicatorType.PointAndFigureOverlay, IndicatorType.RenkoChart, IndicatorType.KagiChart, IndicatorType.PointAndFigureChart })
        {
            Assert.False(IndicatorFactory.IsRegisteredStatic(type), type.ToString());
        }
    }

    [Fact]
    public void ResultKeys_UpdateExistsOnlyForRenkoAndPointAndFigure_NoCustomData_AndEveryListHasTheInputLength()
    {
        var candles = RandomWalk(61, 160, 100m, 2m);

        foreach (var type in FourTypes)
        {
            var result = IndicatorFactory.CreateStatic(type, DefaultParameter(type))!.Calculate(candles);

            Assert.True(result.IsSuccessful, type.ToString());
            Assert.Null(result.CustomData); // a P&F analysis here would leak into the non-time-series P&F chart (ChartDataCoordinator adopts it)
            var expected = new List<string> { "Main", "Reversal", IndicatorSeriesNames.UpTrendFlag };
            if (TypesWithUpdate.Contains(type)) expected.Add("Update");
            Assert.Equal(expected.OrderBy(s => s, StringComparer.Ordinal), result.SeriesNames.OrderBy(s => s, StringComparer.Ordinal));
            foreach (string name in result.SeriesNames) Assert.Equal(candles.Count, result.GetSeries(name).Count);
        }
    }

    [Fact]
    public void KagiAndThreeLineBreak_HaveNoUpdateSeriesPropertyOrEntry_AndNoUpdateSeriesColour()
    {
        foreach (var type in TypesWithoutUpdate)
        {
            var indicator = IndicatorFactory.CreateStatic(type, DefaultParameter(type))!;
            Assert.Null(indicator.GetType().GetProperty("Update"));

            var setting = DefaultSetting(type);
            Assert.DoesNotContain(setting.SeriesColors, c => c.Name == "Update" || c.TargetSeries.Contains("Update"));
            Assert.False(indicator.Calculate(FromCloses(100m, 105m, 110m)).HasSeries("Update"));
        }

        foreach (var type in TypesWithUpdate)
        {
            Assert.NotNull(IndicatorFactory.CreateStatic(type, DefaultParameter(type))!.GetType().GetProperty("Update"));
            Assert.Contains(DefaultSetting(type).SeriesColors, c => c.Name == "Update");
        }
    }

    [Fact]
    public void EmptyAndSingleCandle_ReturnLengthMatchingResults_WithoutThrowing()
    {
        foreach (var type in FourTypes)
        {
            var indicator = IndicatorFactory.CreateStatic(type, DefaultParameter(type))!;
            var empty = indicator.Calculate(new List<CoreCandleData>());
            Assert.True(empty.IsSuccessful);
            Assert.Empty(empty.MainValues);

            var one = indicator.Calculate(FromCloses(100m));
            Assert.Single(one.MainValues);
            Assert.Null(one.MainValues[0]);
        }
    }

    [Fact]
    public void ReusedInstance_DoesNotKeepTheSecondaryListsOfThePreviousRun()
    {
        var indicator = (CoreRenkoChartIndicator)IndicatorFactory.CreateStatic(IndicatorType.Renko, new CoreRenkoChartParameter { SizingMode = ChartSizingMode.Fixed, ManualSize = 1m })!;

        indicator.Calculate(RandomWalk(62, 100, 100m, 2m));
        var second = indicator.Calculate(FromCloses(100m, 105m, 110m));

        // As for every indicator with secondary lists, the instance lists are cleared by each run: the returned result of the LAST run is the one to read (no stale 100 entries).
        Assert.Equal(3, second.MainValues.Count);
        Assert.Equal(3, second.GetSeries("Reversal").Count);
        Assert.Equal(3, second.GetSeries("Update").Count);
        Assert.Equal(3, second.GetSeries(IndicatorSeriesNames.UpTrendFlag).Count);
    }

    [Fact]
    public void Configure_ChangesTheResult_AndAnInvalidParameterIsRefusedByTheFactory()
    {
        var candles = RandomWalk(63, 120, 100m, 2m);
        var coarse = IndicatorFactory.CreateStatic(IndicatorType.Renko, new CoreRenkoChartParameter { SizingMode = ChartSizingMode.Fixed, ManualSize = 10m })!.Calculate(candles);
        var fine = IndicatorFactory.CreateStatic(IndicatorType.Renko, new CoreRenkoChartParameter { SizingMode = ChartSizingMode.Fixed, ManualSize = 0.5m })!.Calculate(candles);

        Assert.NotEqual(coarse.MainValues, fine.MainValues);
        Assert.Throws<ArgumentOutOfRangeException>(() => IndicatorFactory.CreateStatic(IndicatorType.Renko, new CoreRenkoChartParameter { ReversalBricks = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => IndicatorFactory.CreateStatic(IndicatorType.ThreeLineBreak, new CoreThreeLineBreakChartParameter { NumberOfLines = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => IndicatorFactory.CreateStatic(IndicatorType.Kagi, new CoreKagiChartParameter { ManualSize = 0m }));
    }

    // --- display names (owner supplement 4) ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void DisplayNames_ShowTheSizingAndTheReversal_AndMinimumMoveOnlyWhenItIsSet()
    {
        Assert.Equal("Renko (ATR14 x1, R2)", new CoreRenkoChartParameter().GetDisplayName("Renko"));
        Assert.Equal("Renko (Fixed 5, R2)", new CoreRenkoChartParameter { SizingMode = ChartSizingMode.Fixed, ManualSize = 5m }.GetDisplayName("Renko"));
        Assert.Equal("Renko (1%, R2)", new CoreRenkoChartParameter { SizingMode = ChartSizingMode.Percentage }.GetDisplayName("Renko"));
        Assert.Equal("Point and Figure (ATR14 x1, R3)", new CorePointAndFigureChartParameter().GetDisplayName("Point and Figure"));
        Assert.Equal("Kagi (ATR14 x1)", new CoreKagiChartParameter().GetDisplayName("Kagi"));
        Assert.Equal("Kagi (ATR20 x1.5)", new CoreKagiChartParameter { AtrPeriod = 20, AtrMultiplier = 1.5m }.GetDisplayName("Kagi"));

        Assert.Equal("Three Line Break (3)", new CoreThreeLineBreakChartParameter().GetDisplayName("Three Line Break"));
        Assert.Equal("Three Line Break (3, min 0.5)", new CoreThreeLineBreakChartParameter { MinimumMove = 0.5m }.GetDisplayName("Three Line Break"));

        // Two settings that differ only in the minimum move are told apart on the screen.
        Assert.NotEqual(
            new CoreThreeLineBreakChartParameter { MinimumMove = 0.5m }.GetDisplayName("Three Line Break"),
            new CoreThreeLineBreakChartParameter { MinimumMove = 1m }.GetDisplayName("Three Line Break"));
        Assert.NotEqual(
            new CoreThreeLineBreakChartParameter().GetDisplayName("Three Line Break"),
            new CoreThreeLineBreakChartParameter { MinimumMove = 1m }.GetDisplayName("Three Line Break"));

        // The indicator instance and the culture: the text never depends on the current culture.
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("Three Line Break (3, min 0.5)", new CoreThreeLineBreakChartParameter { MinimumMove = 0.5m }.GetDisplayName("Three Line Break"));
            Assert.Equal("Renko (ATR14 x1.5, R2)", new CoreRenkoChartParameter { AtrMultiplier = 1.5m }.GetDisplayName("Renko"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        var indicator = IndicatorFactory.CreateStatic(IndicatorType.ThreeLineBreak, new CoreThreeLineBreakChartParameter { MinimumMove = 2m })!;
        Assert.Equal("Three Line Break (3, min 2)", indicator.Name);
    }

    // --- the sizing description wording (owner supplement 1) ----------------------------------------------------------------------------------------

    [Fact]
    public void TheSizingModeTooltip_StatesThatTheOverlaySizeCanDifferFromTheNonTimeSeriesChart()
    {
        var description = typeof(CoreBlockChartSizingParameterBase).GetProperty(nameof(CoreBlockChartSizingParameterBase.SizingMode))!
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        Assert.Equal(CoreBlockChartSizingParameterBase.SizingModeDescription, description);
        Assert.Contains("fixed once, at the anchor bar", description);
        Assert.Contains("can therefore differ from the box size of the corresponding non-time-series chart", description);
    }

    // --- defaults, their drift guard and the independence from the chart-type settings -----------------------------------------------------------------

    [Fact]
    public void Defaults_EqualTheDefaultsOfTheCorrespondingChartTypes()
    {
        var chart = new GlobalChartSettings();

        var renko = new CoreRenkoChartParameter();
        Assert.Equal(chart.RenkoSizingMode, renko.SizingMode);
        Assert.Equal(chart.RenkoBrickSize, renko.ManualSize);
        Assert.Equal(chart.RenkoBrickPercent, renko.Percentage);
        Assert.Equal(chart.RenkoAtrPeriod, renko.AtrPeriod);
        Assert.Equal(chart.RenkoAtrMultiplier, renko.AtrMultiplier);
        Assert.Equal(chart.RenkoRoundingMode, renko.RoundingMode);
        Assert.Equal(chart.RenkoFallbackMode, renko.FallbackMode);
        Assert.Equal(chart.RenkoReversal, renko.ReversalBricks);

        var pnf = new CorePointAndFigureChartParameter();
        Assert.Equal(chart.PnfSizingMode, pnf.SizingMode);
        Assert.Equal(chart.PnfBoxSize, pnf.ManualSize);
        Assert.Equal(chart.PnfBoxPercent, pnf.Percentage);
        Assert.Equal(chart.PnfAtrPeriod, pnf.AtrPeriod);
        Assert.Equal(chart.PnfAtrMultiplier, pnf.AtrMultiplier);
        Assert.Equal(chart.PnfRoundingMode, pnf.RoundingMode);
        Assert.Equal(chart.PnfFallbackMode, pnf.FallbackMode);
        Assert.Equal(chart.PnfReversal, pnf.ReversalBoxes);

        var kagi = new CoreKagiChartParameter();
        Assert.Equal(chart.KagiReversalMode, kagi.SizingMode);
        Assert.Equal(chart.KagiReversalAmount, kagi.ManualSize);
        Assert.Equal(chart.KagiReversalPercent, kagi.Percentage);
        Assert.Equal(chart.KagiAtrPeriod, kagi.AtrPeriod);
        Assert.Equal(chart.KagiAtrMultiplier, kagi.AtrMultiplier);
        Assert.Equal(chart.KagiRoundingMode, kagi.RoundingMode);
        Assert.Equal(chart.KagiFallbackMode, kagi.FallbackMode);

        var tlb = new CoreThreeLineBreakChartParameter();
        Assert.Equal(ChartConstants.DefaultThreeLineBreakLineCount, tlb.NumberOfLines);
        Assert.Equal(ChartConstants.DefaultThreeLineBreakMinMove, tlb.MinimumMove);
    }

    [Fact]
    public void TheUpAndDownColours_AreTheOnesOfTheChartTypes()
    {
        Assert.Equal(ParseHex(ChartSettingsConstants.DefaultBullishColor), IndicatorDefaultConstants.BlockChartOverlayUpColor);
        Assert.Equal(ParseHex(ChartSettingsConstants.DefaultBearishColor), IndicatorDefaultConstants.BlockChartOverlayDownColor);

        static IndicatorColor ParseHex(string hex)
            => new(255, byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber), byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber), byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber));
    }

    [Fact]
    public void DefaultSettings_AreAStepLineColouredByDirection_WithDotColoursByRole_AndNoColourForTheDirectionFlag()
    {
        foreach (var type in FourTypes)
        {
            var s = DefaultSetting(type);

            Assert.Equal(CoreIndicatorCategory.Chart, s.Category);
            Assert.True(s.IsOverlay);
            Assert.False(s.IsEnabled);
            Assert.Equal(CoreLineStyle.Step, s.Style);
            Assert.True(s.UseUpDownColors);
            Assert.Equal(IndicatorDefaultConstants.BlockChartOverlayUpColor, s.UpColor);
            Assert.Equal(IndicatorDefaultConstants.BlockChartOverlayDownColor, s.DownColor);
            Assert.Equal(IndicatorDefaultConstants.DefaultOverlayThickness, s.Thickness);
            Assert.IsType(DefaultParameter(type).GetType(), s.ParameterObject);

            var close = s.SeriesColors.Single(c => c.Name == "Main");
            Assert.Equal("Close", close.DisplayName);
            Assert.Equal(IndicatorDefaultConstants.BlockChartOverlayReversalColor, s.SeriesColors.Single(c => c.Name == "Reversal").Color);
            Assert.DoesNotContain(s.SeriesColors, c => c.Name == IndicatorSeriesNames.UpTrendFlag || c.TargetSeries.Contains(IndicatorSeriesNames.UpTrendFlag));
            Assert.Equal(TypesWithUpdate.Contains(type) ? 3 : 2, s.SeriesColors.Count);
        }

        Assert.Equal(IndicatorDefaultConstants.BlockChartOverlayUpdateColor, DefaultSetting(IndicatorType.Renko).SeriesColors.Single(c => c.Name == "Update").Color);
    }

    [Fact]
    public void OverlaySettings_AreIndependentOfTheChartTypeSettings()
    {
        // Separate objects: changing one never changes the other, in either direction (defaults are copied once, there is no live link).
        var overlay = new CoreRenkoChartParameter();
        var chart = new GlobalChartSettings { RenkoBrickSize = 7m, RenkoReversal = 5 };
        overlay.ManualSize = 3m;

        Assert.Equal(3m, overlay.ManualSize);
        Assert.Equal(7m, chart.RenkoBrickSize);
        Assert.Equal(5, chart.RenkoReversal);
        Assert.Equal(ChartConstants.DefaultRenkoReversalBricks, overlay.ReversalBricks);
        Assert.Equal(new GlobalChartSettings().RenkoBrickSize, new CoreRenkoChartParameter().ManualSize);
    }

    [Fact]
    public void TheOverlayTypes_NeverReferenceTheChartTypeSettings_AndExposeNoBreakoutMultiWaveOrGhostItems()
    {
        var overlayTypes = new[]
        {
            typeof(CoreBlockChartSizingParameterBase), typeof(CoreRenkoChartParameter), typeof(CorePointAndFigureChartParameter), typeof(CoreKagiChartParameter), typeof(CoreThreeLineBreakChartParameter),
            typeof(CoreBlockChartOverlayIndicatorBase), typeof(CoreRenkoChartIndicator), typeof(CorePointAndFigureChartIndicator), typeof(CoreKagiChartIndicator), typeof(CoreThreeLineBreakChartIndicator),
            typeof(BlockChartSizingSpec), typeof(BlockChartOverlayMapper),
        };
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var type in overlayTypes)
        {
            var used = type.GetFields(all).Select(f => f.FieldType)
                .Concat(type.GetProperties(all).Select(p => p.PropertyType))
                .Concat(type.GetMethods(all).SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)));
            Assert.DoesNotContain(used, t => t == typeof(GlobalChartSettings) || t == typeof(IChartRenderSettings));
        }

        foreach (var type in overlayTypes.Where(t => typeof(CoreIndicatorParameterBase).IsAssignableFrom(t)))
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.DoesNotContain("Breakout", property.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("MultiWave", property.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Ghost", property.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    // --- direction flag and normalization (owner supplement 5) --------------------------------------------------------------------------------------

    [Fact]
    public void TheDirectionFlag_IsNeverAnOutputName_SoItCannotBeOfferedAsAPriceNormalizedChannel()
    {
        foreach (var type in FourTypes)
        {
            var names = IndicatorOutputSeriesResolver.GetDisplayNames(type);

            Assert.DoesNotContain(IndicatorSeriesNames.UpTrendFlag, names);
            Assert.Contains("Reversal", names);
            Assert.Equal(TypesWithUpdate.Contains(type), names.Contains("Update"));
            Assert.Null(IndicatorFactory.CreateStatic(type, DefaultParameter(type))!.GetType().GetProperty(IndicatorSeriesNames.UpTrendFlag));

            // The normalization default is per indicator TYPE (it cannot see an output name): price units, exactly because every offered output is a price.
            Assert.Equal(ChannelNormalization.WindowMinMax, FeatureChannelNormalizationDefaults.ForIndicator(type));
        }
    }

    [Fact]
    public void TheDirectionFlag_IsSuppressedFromTheChartRangeAndTheDataWindow_ThePricesAreNot()
    {
        foreach (var type in FourTypes)
        {
            Assert.True(IndicatorChartSuppression.IsSuppressed(type, IndicatorSeriesNames.UpTrendFlag));
            Assert.True(IndicatorChartSuppression.IsDataWindowSuppressed(type, IndicatorSeriesNames.UpTrendFlag));

            foreach (string price in new[] { "Main", "Reversal", "Update" })
            {
                Assert.False(IndicatorChartSuppression.IsSuppressed(type, price), $"{type} {price}");
                Assert.False(IndicatorChartSuppression.IsDataWindowSuppressed(type, price), $"{type} {price}");
            }
        }
    }

    [Fact]
    public void TheIndicatorsOfTheOtherTypes_KeepTheirNormalizationDefault()
    {
        Assert.Equal(ChannelNormalization.None, FeatureChannelNormalizationDefaults.ForIndicator(IndicatorType.RSI));
        Assert.Equal(ChannelNormalization.WindowZScore, FeatureChannelNormalizationDefaults.ForIndicator(IndicatorType.ATR));
        Assert.Equal(ChannelNormalization.WindowZScore, FeatureChannelNormalizationDefaults.ForIndicator(IndicatorType.ThreeLineBreakSignal));
    }
}
