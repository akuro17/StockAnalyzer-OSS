using System.Collections.Generic;
using Avalonia;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Chart;

/// <summary>
/// T2 of "SubWindow Drawing Tools — coordinate fixes": ChartLayoutExtensions.ResolvePanelIndex
/// classifies the reserved-but-empty volume band as its own region (VolumeAreaPanelIndex) so pointer
/// handlers can keep it out of drawing interaction instead of folding it into the main chart.
/// </summary>
public class ChartLayoutExtensionsTests
{
    private static ChartLayoutContext Layout(bool withVolumeBand)
    {
        var panels = new List<Rect>
        {
            new(50, 370, 600, 100), // panel 0
            new(50, 480, 600, 100), // panel 1
        };
        return new ChartLayoutContext(
            totalBounds: new Rect(0, 0, 700, 620),
            chartArea: new Rect(50, 0, 600, 300),
            volumeArea: withVolumeBand ? new Rect(50, 300, 600, 60) : new Rect(0, 0, 0, 0),
            panelAreas: panels,
            marginTop: 0, marginBottom: 0, marginLeft: 50, marginRight: 50);
    }

    [Fact]
    public void ChartArea_ResolvesToMainChart()
        => Assert.Equal(ChartLayoutExtensions.MainChartPanelIndex, Layout(true).ResolvePanelIndex(new Point(300, 150)));

    [Theory]
    [InlineData(410, 0)]
    [InlineData(520, 1)]
    public void SubPanels_ResolveToTheirIndex(double y, int expected)
        => Assert.Equal(expected, Layout(true).ResolvePanelIndex(new Point(300, y)));

    [Fact]
    public void VolumeBand_ResolvesToVolumeAreaPanelIndex()
        => Assert.Equal(ChartLayoutExtensions.VolumeAreaPanelIndex, Layout(true).ResolvePanelIndex(new Point(300, 330)));

    [Fact]
    public void GapBelowLastPanel_ResolvesToOutside()
        => Assert.Equal(ChartLayoutExtensions.OutsidePanelsIndex, Layout(true).ResolvePanelIndex(new Point(300, 605)));

    [Fact]
    public void WhenNoVolumeBand_TheSameYFallsThroughToOutside_NotMainChart()
        => Assert.Equal(ChartLayoutExtensions.OutsidePanelsIndex, Layout(false).ResolvePanelIndex(new Point(300, 330)));
}
