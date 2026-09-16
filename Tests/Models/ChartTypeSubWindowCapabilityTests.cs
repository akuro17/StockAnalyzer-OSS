using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Tests.Models;

/// <summary>
/// Regression tests for the indicator capability flags (SupportsIndicators / CanToggleIndicators)
/// of the index-based transform, time-series, and asymmetric (Reverse Watch / Relative Performance)
/// chart types. Per-type expected values and rationale: docs/SA_UI_INTERACTION.md section 27 / 27.1.
/// </summary>
public class ChartTypeSubWindowCapabilityTests
{
    [Theory]
    [InlineData(ChartType.Renko)]
    [InlineData(ChartType.PointAndFigure)]
    [InlineData(ChartType.Kagi)]
    [InlineData(ChartType.ThreeLineBreak)]
    public void IndexBasedChart_ShowsNoIndicators(ChartType type)
    {
        var caps = ChartTypeCapabilitiesRegistry.Get(type);

        Assert.False(caps.SupportsIndicators);
        Assert.False(caps.CanToggleIndicators);
    }

    [Theory]
    [InlineData(ChartType.Candlestick)]
    [InlineData(ChartType.HeikinAshi)]
    [InlineData(ChartType.OHLCBar)]
    [InlineData(ChartType.Line)]
    [InlineData(ChartType.Area)]
    public void TimeSeriesChart_KeepsIndicatorSubWindow(ChartType type)
    {
        var caps = ChartTypeCapabilitiesRegistry.Get(type);

        Assert.True(caps.SupportsIndicators);
        Assert.True(caps.CanToggleIndicators);
    }

    // Asymmetric types: the render pass is skipped (SupportsIndicators = false) yet the two flags
    // are not both false. Reverse Watch keeps CanToggleIndicators = true so its sub-window toggle
    // still follows the user state without changing what is drawn; Relative Performance leaves
    // CanToggleIndicators at its default false. See docs/SA_UI_INTERACTION.md section 27.
    [Theory]
    [InlineData(ChartType.ReverseWatch, false, true)]
    [InlineData(ChartType.RelativePerformance, false, false)]
    public void AsymmetricChart_HasDeclaredFlagCombination(ChartType type, bool supportsIndicators, bool canToggleIndicators)
    {
        var caps = ChartTypeCapabilitiesRegistry.Get(type);

        Assert.Equal(supportsIndicators, caps.SupportsIndicators);
        Assert.Equal(canToggleIndicators, caps.CanToggleIndicators);
    }
}
