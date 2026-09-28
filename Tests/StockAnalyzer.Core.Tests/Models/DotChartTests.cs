using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

public class DotChartTests
{
    [Fact]
    public void GlobalChartSettings_DotDefaults_AreValid()
    {
        var settings = new GlobalChartSettings();

        Assert.Equal(ChartSettingsConstants.DefaultDotBaseRadius, settings.DotBaseRadius);
        Assert.Equal(ChartSettingsConstants.DefaultBullishColor, settings.DotUpColor);
        Assert.Equal(ChartSettingsConstants.DefaultBearishColor, settings.DotDownColor);
        Assert.Equal(ChartSettingsConstants.DefaultNeutralColor, settings.DotNaturalColor);
        Assert.Equal(DotShapeType.Circle, settings.DotShape);
        Assert.Equal(PriceType.Close, settings.DotPriceType);
    }

    [Fact]
    public void GlobalChartSettings_Validate_ClampsInvalidDotBaseRadius()
    {
        // Lower than min
        var settingsLow = new GlobalChartSettings { DotBaseRadius = 0.1 }.Validate();
        Assert.Equal(ChartSettingsConstants.MinDotBaseRadius, settingsLow.DotBaseRadius);

        // Higher than max
        var settingsHigh = new GlobalChartSettings { DotBaseRadius = 100.0 }.Validate();
        Assert.Equal(ChartSettingsConstants.MaxDotBaseRadius, settingsHigh.DotBaseRadius);

        // NaN recovers to default
        var settingsNaN = new GlobalChartSettings { DotBaseRadius = double.NaN }.Validate();
        Assert.Equal(ChartSettingsConstants.DefaultDotBaseRadius, settingsNaN.DotBaseRadius);

        // Infinity recovers to default
        var settingsInf = new GlobalChartSettings { DotBaseRadius = double.PositiveInfinity }.Validate();
        Assert.Equal(ChartSettingsConstants.DefaultDotBaseRadius, settingsInf.DotBaseRadius);
    }

    [Fact]
    public void GlobalChartSettings_Validate_RecoversInvalidColors()
    {
        var settingsInvalidColor = new GlobalChartSettings
        {
            DotUpColor = "invalid",
            DotDownColor = "",
            DotNaturalColor = "#XYZ123"
        }.Validate();

        Assert.Equal(ChartSettingsConstants.DefaultBullishColor, settingsInvalidColor.DotUpColor);
        Assert.Equal(ChartSettingsConstants.DefaultBearishColor, settingsInvalidColor.DotDownColor);
        Assert.Equal(ChartSettingsConstants.DefaultNeutralColor, settingsInvalidColor.DotNaturalColor);
    }

    [Fact]
    public void GlobalChartSettings_Validate_RecoversUndefinedEnums()
    {
        var settingsInvalidEnums = new GlobalChartSettings
        {
            DotShape = (DotShapeType)999,
            DotPriceType = (PriceType)999
        }.Validate();

        Assert.Equal(DotShapeType.Circle, settingsInvalidEnums.DotShape);
        Assert.Equal(PriceType.Close, settingsInvalidEnums.DotPriceType);
    }

    [Fact]
    public void ChartTypeCapabilitiesRegistry_Dot_HasCorrectFlags()
    {
        var caps = ChartTypeCapabilitiesRegistry.Get(ChartType.Dot);

        Assert.True(caps.IsTimeBased);
        Assert.False(caps.IsIndexBased);
        Assert.True(caps.HasStandardHeader);
        Assert.True(caps.SupportsIndicators);
        Assert.True(caps.CanToggleIndicators);
    }
}
