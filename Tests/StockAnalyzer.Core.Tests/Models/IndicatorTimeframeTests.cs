using System;
using System.Linq;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

/// <summary>
/// Indicator Timeframe, Sprint 1 / T1: the setting, its policy SSoT, the legend suffix and the Pivot self-timeframe marker.
/// Real <see cref="CoreIndicatorSettings"/>, parameter objects and <see cref="IndicatorType"/> values only (no mocks).
/// </summary>
public sealed class IndicatorTimeframeTests
{
    private static readonly IndicatorType[] BlockTypes =
    {
        IndicatorType.Renko, IndicatorType.RenkoChart, IndicatorType.RenkoOverlay,
        IndicatorType.Kagi, IndicatorType.KagiChart, IndicatorType.KagiOverlay,
        IndicatorType.PointAndFigure, IndicatorType.PointAndFigureChart, IndicatorType.PointAndFigureOverlay,
#pragma warning disable CS0618 // Deliberately testing obsolete ThreeLineBreakSignal timeframe policy
        IndicatorType.ThreeLineBreak, IndicatorType.ThreeLineBreakSignal,
#pragma warning restore CS0618
    };

    private static CoreIndicatorSettings Sma(int period = 20) => new()
    {
        TypeEnum = IndicatorType.SMA,
        ParameterObject = new CoreSmaParameter { Period = period },
    };

    private static CoreIndicatorSettings Obv()
    {
        var settings = new CoreIndicatorSettings { TypeEnum = IndicatorType.OBV };
        settings.UpdateDisplayName();
        return settings;
    }

    // ---- enum ----------------------------------------------------------------------------------------------

    [Fact]
    public void Enum_IntegerValuesArePinned_AndEqualPivotSourcePeriod()
    {
        Assert.Equal(0, (int)IndicatorTimeframe.SameAsChart);
        Assert.Equal(1, (int)IndicatorTimeframe.Weekly);
        Assert.Equal(2, (int)IndicatorTimeframe.Monthly);
        Assert.Equal((int)PivotSourcePeriod.PreviousBar, (int)IndicatorTimeframe.SameAsChart);
        Assert.Equal((int)PivotSourcePeriod.Week, (int)IndicatorTimeframe.Weekly);
        Assert.Equal((int)PivotSourcePeriod.Month, (int)IndicatorTimeframe.Monthly);
    }

    [Fact]
    public void Enum_HasDeclarationOrderSortOptOut()
    {
        var attribute = (EnumDisplaySortAttribute?)Attribute.GetCustomAttribute(typeof(IndicatorTimeframe), typeof(EnumDisplaySortAttribute));

        Assert.NotNull(attribute);
        Assert.False(attribute!.SortAlphabetically);
        Assert.True(new EnumDisplaySortAttribute().SortAlphabetically);
    }

    [Theory]
    [InlineData(TimeFrame.D1, IndicatorTimeframe.SameAsChart, TimeFrame.D1)]
    [InlineData(TimeFrame.W1, IndicatorTimeframe.SameAsChart, TimeFrame.W1)]
    [InlineData(TimeFrame.D1, IndicatorTimeframe.Weekly, TimeFrame.W1)]
    [InlineData(TimeFrame.D1, IndicatorTimeframe.Monthly, TimeFrame.MN1)]
    [InlineData(TimeFrame.MN1, IndicatorTimeframe.Weekly, TimeFrame.W1)]
    public void ToTimeFrame_MapsToTheEffectiveFrame(TimeFrame chart, IndicatorTimeframe timeframe, TimeFrame expected)
    {
        Assert.Equal(expected, timeframe.ToTimeFrame(chart));
    }

    // ---- setting -------------------------------------------------------------------------------------------

    [Fact]
    public void Timeframe_DefaultsToSameAsChart()
    {
        Assert.Equal(IndicatorTimeframe.SameAsChart, new CoreIndicatorSettings().Timeframe);
    }

    [Fact]
    public void Timeframe_Change_BumpsMathematicalVersionOncePerChange()
    {
        var settings = Sma();
        var before = settings.MathematicalVersion;

        settings.Timeframe = IndicatorTimeframe.Weekly;
        Assert.Equal(before + 1, settings.MathematicalVersion);

        settings.Timeframe = IndicatorTimeframe.Weekly; // same value: no change event
        Assert.Equal(before + 1, settings.MathematicalVersion);

        settings.Timeframe = IndicatorTimeframe.Monthly;
        Assert.Equal(before + 2, settings.MathematicalVersion);
    }

    [Fact]
    public void Timeframe_RoundTripsThroughWorkspaceJson_AsInteger()
    {
        var settings = Sma();
        settings.Timeframe = IndicatorTimeframe.Monthly;

        var json = JsonSerializer.Serialize(settings, WorkspaceSerializationService.SharedJsonOptions);
        var back = JsonSerializer.Deserialize<CoreIndicatorSettings>(json, WorkspaceSerializationService.SharedJsonOptions);

        Assert.Contains("\"Timeframe\": 2", json);
        Assert.NotNull(back);
        Assert.Equal(IndicatorTimeframe.Monthly, back!.Timeframe);
        Assert.Equal(settings.DisplayName, back.DisplayName);
        Assert.Equal(settings.ShortDisplayName, back.ShortDisplayName);
    }

    [Fact]
    public void Timeframe_OldJsonWithoutTheKey_KeepsSameAsChart()
    {
        var settings = Sma();
        var json = JsonSerializer.Serialize(settings, WorkspaceSerializationService.SharedJsonOptions);
        var withoutKey = string.Join('\n', json.Split('\n').Where(line => !line.Contains("\"Timeframe\"")));

        var back = JsonSerializer.Deserialize<CoreIndicatorSettings>(withoutKey, WorkspaceSerializationService.SharedJsonOptions);

        Assert.NotNull(back);
        Assert.Equal(IndicatorTimeframe.SameAsChart, back!.Timeframe);
    }

    [Fact]
    public void CloneAndSnapshot_PreserveTimeframe_WithoutExtraVersionBump()
    {
        var settings = Sma();
        settings.Timeframe = IndicatorTimeframe.Weekly;

        var clone = settings.Clone();
        var snapshot = settings.Snapshot();

        Assert.Equal(IndicatorTimeframe.Weekly, clone.Timeframe);
        Assert.Equal(IndicatorTimeframe.Weekly, snapshot.Timeframe);
        Assert.Equal(settings.MathematicalVersion, snapshot.MathematicalVersion);
        Assert.Equal(settings.DisplayName, clone.DisplayName);
        Assert.Equal(settings.ShortDisplayName, clone.ShortDisplayName);
        Assert.Equal(settings.ShortDisplayName, snapshot.ShortDisplayName);
    }

    // ---- policy: chart types -------------------------------------------------------------------------------

    [Fact]
    public void IsApplicable_TruthTable_CoversEveryChartType()
    {
        var applicable = new[]
        {
            ChartType.Candlestick, ChartType.HeikinAshi, ChartType.OHLCBar, ChartType.Line, ChartType.Area, ChartType.Dot, ChartType.Invisible,
        };

        var all = Enum.GetValues<ChartType>();
        Assert.Equal(13, all.Length);
        foreach (var chartType in all)
        {
            Assert.Equal(applicable.Contains(chartType), IndicatorTimeframePolicy.IsApplicable(chartType));
        }

        Assert.False(IndicatorTimeframePolicy.IsApplicable(ChartType.ThreeLineBreak));
        Assert.False(IndicatorTimeframePolicy.IsApplicable(ChartType.RelativePerformance));
    }

    // ---- policy: indicator types ---------------------------------------------------------------------------

    [Theory]
    [InlineData(IndicatorType.SMA)]
    [InlineData(IndicatorType.EMA)]
    [InlineData(IndicatorType.MACD)]
    [InlineData(IndicatorType.OBV)]
    public void Resolve_OrdinaryIndicators_AreSupported(IndicatorType type)
    {
        Assert.Equal(IndicatorTimeframeSupport.Supported, IndicatorTimeframePolicy.Resolve(type));
        Assert.True(IndicatorTimeframePolicy.SupportsIndicatorTimeframe(type));
    }

    [Fact]
    public void Resolve_ObvWithoutParameterObject_IsSupported()
    {
        var obv = Obv();

        Assert.Null(obv.ParameterObject);
        Assert.True(IndicatorTimeframePolicy.SupportsIndicatorTimeframe(IndicatorType.OBV, obv.ParameterObject));
    }

    [Theory]
    [InlineData(IndicatorType.PivotPointsAdvanced)]
    [InlineData(IndicatorType.ClassicPivotPoints)]
    public void Resolve_PivotTypes_AreSelfHandled(IndicatorType type)
    {
        Assert.Equal(IndicatorTimeframeSupport.SelfHandled, IndicatorTimeframePolicy.Resolve(type));
        Assert.False(IndicatorTimeframePolicy.SupportsIndicatorTimeframe(type));
    }

    [Fact]
    public void Resolve_ParameterObjectWithSelfTimeframe_IsSelfHandledForAnyType()
    {
        Assert.Equal(IndicatorTimeframeSupport.SelfHandled, IndicatorTimeframePolicy.Resolve(IndicatorType.SMA, new CorePivotPointsAdvancedParameter()));
        Assert.Equal(IndicatorTimeframeSupport.SelfHandled, IndicatorTimeframePolicy.Resolve(IndicatorType.SMA, new CoreClassicPivotPointsParameter()));
    }

    [Theory]
    [InlineData(IndicatorType.Correlation)]
    [InlineData(IndicatorType.CrossTickerDtw)]
    [InlineData(IndicatorType.MarketBreadth)]
    [InlineData(IndicatorType.ZigZag)]
    [InlineData(IndicatorType.AnchoredVWAP)]
    [InlineData(IndicatorType.Price)]
    public void Resolve_CrossTickerBreadthNonCausalAnchoredAndPrice_AreNotApplicable(IndicatorType type)
    {
        Assert.Equal(IndicatorTimeframeSupport.NotApplicable, IndicatorTimeframePolicy.Resolve(type));
        Assert.False(IndicatorTimeframePolicy.SupportsIndicatorTimeframe(type));
    }

    [Fact]
    public void Resolve_AllElevenBlockTypes_AreNotApplicable()
    {
        Assert.Equal(11, BlockTypes.Distinct().Count());
        foreach (var type in BlockTypes)
        {
            Assert.Equal(IndicatorTimeframeSupport.NotApplicable, IndicatorTimeframePolicy.Resolve(type));
        }
    }

    [Fact]
    public void Resolve_EveryNonCausalType_IsNotApplicable()
    {
        foreach (var type in StockAnalyzer.Core.Services.Backtest.Engine.BacktestIndicatorEligibility.NonCausalIndicatorTypes)
        {
            Assert.False(IndicatorTimeframePolicy.SupportsIndicatorTimeframe(type), type.ToString());
        }
    }

    [Fact]
    public void SelfTimeframe_MapsSourcePeriod_OnBothPivotParameters()
    {
        foreach (var period in Enum.GetValues<PivotSourcePeriod>())
        {
            var advanced = new CorePivotPointsAdvancedParameter { SourcePeriod = period };
            var classic = new CoreClassicPivotPointsParameter { SourcePeriod = period };

            Assert.Equal((IndicatorTimeframe)(int)period, ((ISelfTimeframeParameter)advanced).SelfTimeframe);
            Assert.Equal((IndicatorTimeframe)(int)period, ((ISelfTimeframeParameter)classic).SelfTimeframe);
        }
    }

    [Fact]
    public void SelfTimeframe_IsNotExposedAsAParameterProperty()
    {
        Assert.Null(typeof(CorePivotPointsAdvancedParameter).GetProperty(nameof(ISelfTimeframeParameter.SelfTimeframe)));
        Assert.Null(typeof(CoreClassicPivotPointsParameter).GetProperty(nameof(ISelfTimeframeParameter.SelfTimeframe)));
    }

    // ---- display name --------------------------------------------------------------------------------------

    [Fact]
    public void DisplayName_Suffix_FollowsTimeframeWithoutStaleOrDoubleLabel()
    {
        var settings = Sma(20);
        var baseLong = settings.DisplayName;
        var baseShort = settings.ShortDisplayName;
        Assert.Equal("SMA(20)", baseShort);
        Assert.DoesNotContain("[", baseLong);

        settings.Timeframe = IndicatorTimeframe.Weekly;
        Assert.Equal(baseLong + " [W1]", settings.DisplayName);
        Assert.Equal("SMA(20) [W1]", settings.ShortDisplayName);

        settings.Timeframe = IndicatorTimeframe.Monthly;
        Assert.Equal(baseLong + " [MN1]", settings.DisplayName);
        Assert.Equal("SMA(20) [MN1]", settings.ShortDisplayName);

        settings.Timeframe = IndicatorTimeframe.SameAsChart;
        Assert.Equal(baseLong, settings.DisplayName);
        Assert.Equal(baseShort, settings.ShortDisplayName);
    }

    [Fact]
    public void DisplayName_ShortNameMode_CarriesTheSuffix_AndParameterChangeKeepsItOnce()
    {
        var settings = Sma(20);
        settings.UseShortName = true;
        settings.Timeframe = IndicatorTimeframe.Weekly;
        Assert.Equal("SMA(20) [W1]", settings.DisplayName);
        Assert.Equal("SMA(20) [W1]", settings.GetFormattedDisplayName());

        ((CoreSmaParameter)settings.ParameterObject!).Period = 50;
        Assert.Equal("SMA(50) [W1]", settings.DisplayName);
        Assert.Equal("SMA(50) [W1]", settings.ShortDisplayName);
    }

    [Fact]
    public void DisplayName_WithoutParameterObject_GetsSuffixInLongAndShortForm()
    {
        var obv = Obv();
        var baseLong = obv.DisplayName;

        obv.Timeframe = IndicatorTimeframe.Weekly;
        Assert.Equal(baseLong + " [W1]", obv.DisplayName);
        Assert.Equal("OBV [W1]", obv.ShortDisplayName);
        Assert.Equal(baseLong + " [W1]", obv.GetFormattedDisplayName());

        obv.Timeframe = IndicatorTimeframe.SameAsChart;
        Assert.Equal(baseLong, obv.DisplayName);
        Assert.Equal("OBV", obv.ShortDisplayName);
    }

    [Fact]
    public void DisplayName_UnsupportedType_NeverGetsASuffix()
    {
        var pivot = new CoreIndicatorSettings { TypeEnum = IndicatorType.PivotPointsAdvanced, ParameterObject = new CorePivotPointsAdvancedParameter() };
        var before = pivot.DisplayName;

        pivot.Timeframe = IndicatorTimeframe.Weekly;

        Assert.Equal(before, pivot.DisplayName);
        Assert.DoesNotContain("[W1]", pivot.ShortDisplayName);
    }

    [Theory]
    [InlineData(PivotSourcePeriod.PreviousBar, "Pivot Points Advanced (Standard)")]
    [InlineData(PivotSourcePeriod.Week, "Pivot Points Advanced (Standard, Week)")]
    [InlineData(PivotSourcePeriod.Month, "Pivot Points Advanced (Standard, Month)")]
    public void PivotGetDisplayName_IsUnchanged(PivotSourcePeriod period, string expected)
    {
        var parameter = new CorePivotPointsAdvancedParameter { SourcePeriod = period };

        Assert.Equal(expected, parameter.GetDisplayName("Pivot Points Advanced"));
    }

    [Theory]
    [InlineData(PivotSourcePeriod.PreviousBar, "Classic Pivot Points")]
    [InlineData(PivotSourcePeriod.Week, "Classic Pivot Points (Week)")]
    [InlineData(PivotSourcePeriod.Month, "Classic Pivot Points (Month)")]
    public void ClassicPivotGetDisplayName_IsUnchanged(PivotSourcePeriod period, string expected)
    {
        var parameter = new CoreClassicPivotPointsParameter { SourcePeriod = period };

        Assert.Equal(expected, parameter.GetDisplayName("Classic Pivot Points"));
    }
}
