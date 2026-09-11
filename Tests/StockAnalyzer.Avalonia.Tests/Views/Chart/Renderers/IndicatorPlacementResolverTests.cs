using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Chart.Renderers;

public class IndicatorPlacementResolverTests
{
    [Fact]
    public void Resolve_DisabledIndicator_IsSkippedRegardlessOfSubWindowState()
    {
        var setting = new CoreIndicatorSettings { IsEnabled = false, IsOverlay = false };

        Assert.Equal(IndicatorPlacement.Skipped, IndicatorPlacementResolver.Resolve(setting, isSubWindowVisible: true));
        Assert.Equal(IndicatorPlacement.Skipped, IndicatorPlacementResolver.Resolve(setting, isSubWindowVisible: false));
    }

    [Fact]
    public void Resolve_OverlayIndicator_RendersOnMainChart_WhenSubWindowVisible()
    {
        var setting = new CoreIndicatorSettings { IsEnabled = true, IsOverlay = true, TypeEnum = IndicatorType.SMA };

        Assert.Equal(IndicatorPlacement.MainChartOverlay, IndicatorPlacementResolver.Resolve(setting, isSubWindowVisible: true));
    }

    [Fact]
    public void Resolve_OverlayIndicator_StillRendersOnMainChart_WhenSubWindowHidden()
    {
        // Regression test for the "SubChart OFF hides main-chart overlay indicators" bug:
        // an overlay indicator (e.g. a moving average) must keep rendering on the main chart
        // even when the sub-window panel is toggled off.
        var setting = new CoreIndicatorSettings { IsEnabled = true, IsOverlay = true, TypeEnum = IndicatorType.SMA };

        Assert.Equal(IndicatorPlacement.MainChartOverlay, IndicatorPlacementResolver.Resolve(setting, isSubWindowVisible: false));
    }

    [Fact]
    public void Resolve_SubWindowIndicator_RendersInPanel_WhenSubWindowVisible()
    {
        var setting = new CoreIndicatorSettings { IsEnabled = true, IsOverlay = false, TypeEnum = IndicatorType.RSI };

        Assert.Equal(IndicatorPlacement.SubWindowPanel, IndicatorPlacementResolver.Resolve(setting, isSubWindowVisible: true));
    }

    [Fact]
    public void Resolve_SubWindowIndicator_IsSkipped_WhenSubWindowHidden()
    {
        var setting = new CoreIndicatorSettings { IsEnabled = true, IsOverlay = false, TypeEnum = IndicatorType.RSI };

        Assert.Equal(IndicatorPlacement.Skipped, IndicatorPlacementResolver.Resolve(setting, isSubWindowVisible: false));
    }

    [Fact]
    public void Resolve_Granville_RendersMainOnly_WhenSubWindowHidden()
    {
        var setting = new CoreIndicatorSettings
        {
            IsEnabled = true,
            TypeEnum = IndicatorType.GranvilleLaw,
            ParameterObject = new CoreGranvilleLawParameter { ShowSubWindowBar = true }
        };

        Assert.Equal(IndicatorPlacement.GranvilleMainOnly, IndicatorPlacementResolver.Resolve(setting, isSubWindowVisible: false));
    }

    [Fact]
    public void Resolve_Granville_RendersMainAndSubWindow_WhenBarEnabledAndSubWindowVisible()
    {
        var setting = new CoreIndicatorSettings
        {
            IsEnabled = true,
            TypeEnum = IndicatorType.GranvilleLaw,
            ParameterObject = new CoreGranvilleLawParameter { ShowSubWindowBar = true }
        };

        Assert.Equal(IndicatorPlacement.GranvilleMainAndSubWindow, IndicatorPlacementResolver.Resolve(setting, isSubWindowVisible: true));
    }

    [Fact]
    public void Resolve_Granville_RendersMainOnly_WhenSubWindowBarDisabled()
    {
        var setting = new CoreIndicatorSettings
        {
            IsEnabled = true,
            TypeEnum = IndicatorType.GranvilleLaw,
            ParameterObject = new CoreGranvilleLawParameter { ShowSubWindowBar = false }
        };

        Assert.Equal(IndicatorPlacement.GranvilleMainOnly, IndicatorPlacementResolver.Resolve(setting, isSubWindowVisible: true));
    }

    [Fact]
    public void Resolve_VolumeProfile_AlwaysRendersOnMainChart_EvenIfIsOverlayIsFalse()
    {
        // Volume Profile is strictly a main-window overlay indicator and must never be placed in a sub-window panel.
        var setting = new CoreIndicatorSettings
        {
            IsEnabled = true,
            TypeEnum = IndicatorType.VolumeProfile,
            IsOverlay = false
        };

        Assert.Equal(IndicatorPlacement.MainChartOverlay, IndicatorPlacementResolver.Resolve(setting, isSubWindowVisible: true));
        Assert.Equal(IndicatorPlacement.MainChartOverlay, IndicatorPlacementResolver.Resolve(setting, isSubWindowVisible: false));
    }
}
