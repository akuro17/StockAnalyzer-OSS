using System.Collections.Generic;
using SkiaSharp;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Chart.Renderers;

public class AxisLabelRendererTests
{
    [Fact]
    public void ResolveOverlaps_ShouldNotChangePositions_WhenNoOverlap()
    {
        // Arrange
        var labels = new List<AxisLabelRenderer.ResolvedLabel>
        {
            new AxisLabelRenderer.ResolvedLabel(10, 10, "A", SKColors.Red, AxisLabelStyle.Default, 0),
            new AxisLabelRenderer.ResolvedLabel(50, 50, "B", SKColors.Blue, AxisLabelStyle.Default, 0),
            new AxisLabelRenderer.ResolvedLabel(90, 90, "C", SKColors.Green, AxisLabelStyle.Default, 0)
        };

        // Act
        AxisLabelRenderer.ResolveOverlaps(labels, 0f, 1000f);

        // Assert
        Assert.Equal(10, labels[0].Y);
        Assert.Equal(50, labels[1].Y);
        Assert.Equal(90, labels[2].Y);
    }

    [Fact]
    public void ResolveOverlaps_ShouldShiftDown_WhenOverlapping()
    {
        // Arrange
        var labels = new List<AxisLabelRenderer.ResolvedLabel>
        {
            new AxisLabelRenderer.ResolvedLabel(10, 10, "A", SKColors.Red, AxisLabelStyle.Default, 0),
            new AxisLabelRenderer.ResolvedLabel(15, 15, "B", SKColors.Blue, AxisLabelStyle.Default, 0) // Overlaps with A
        };

        // Act
        AxisLabelRenderer.ResolveOverlaps(labels, 0f, 1000f);

        // Assert
        Assert.True(labels[0].Y < 15); // A should be shifted up
        Assert.True(labels[1].Y > labels[0].Y); // B should be below A
        Assert.True(labels[1].Y - labels[0].Y >= 18); // Minimum spacing (Height=18)
    }

    [Fact]
    public void ResolveOverlaps_ShouldHandleMultipleCascadingOverlaps()
    {
        // Arrange
        var labels = new List<AxisLabelRenderer.ResolvedLabel>
        {
            new AxisLabelRenderer.ResolvedLabel(10, 10, "A", SKColors.Red, AxisLabelStyle.Default, 0),
            new AxisLabelRenderer.ResolvedLabel(12, 12, "B", SKColors.Blue, AxisLabelStyle.Default, 0),
            new AxisLabelRenderer.ResolvedLabel(14, 14, "C", SKColors.Green, AxisLabelStyle.Default, 0)
        };

        // Act
        AxisLabelRenderer.ResolveOverlaps(labels, 0f, 1000f);

        // Assert
        Assert.True(labels[0].Y < 12); 
        Assert.True(labels[1].Y > labels[0].Y);
        Assert.True(labels[2].Y > labels[1].Y);
    }
}
