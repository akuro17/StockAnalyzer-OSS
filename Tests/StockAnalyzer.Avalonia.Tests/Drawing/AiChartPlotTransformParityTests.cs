using System;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Rendering;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// Plan 15, T1: the price-to-pixel mapping of the headless AI image (the Core transformation layer plus the panel offset) gives the same y as the chart's own
/// <see cref="LinearCoordinateTransform"/> for the same range and panel height, so a price sits at the same height in the image as in the chart.
/// </summary>
public sealed class AiChartPlotTransformParityTests
{
    [Theory]
    [InlineData(900.0, 1100.0, 300, 20)]
    [InlineData(0.0003, 0.0009, 180, 20)]
    [InlineData(-50.0, 25.0, 421, 57)]
    public void ThePriceGoesToTheSameRowAsInTheChartTransform(double minValue, double maxValue, int panelHeight, int panelTop)
    {
        decimal min = (decimal)minValue;
        decimal max = (decimal)maxValue;
        var chart = new LinearCoordinateTransform(new DateTime(2026, 1, 1), new DateTime(2026, 6, 1), min, max, 1116, panelHeight);
        var image = new AiChartPlotTransform(12, panelTop, 1116, panelHeight, min, max, 100);

        for (int step = 0; step <= 40; step++)
        {
            decimal price = min + ((max - min) * step / 40m);
            Assert.Equal(panelTop + chart.GetYFromPrice(price), image.YPx(price), 6);
        }
    }
}
