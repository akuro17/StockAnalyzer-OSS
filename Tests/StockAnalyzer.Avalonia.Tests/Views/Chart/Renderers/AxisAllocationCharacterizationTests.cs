using System;
using System.Collections.Generic;
using System.Reflection;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Chart.Renderers;

/// <summary>
/// Pins the outputs of AxisRenderer.GetDecimalPlaces and AxisLabelRenderer.ResolveOverlaps so that the allocation-free
/// rewrites (stackalloc) are proven to produce the same numbers. The ResolveOverlaps golden values were captured from the
/// implementation that allocated float[] arrays, before it was changed.
/// </summary>
public class AxisAllocationCharacterizationTests
{
    private static int GetDecimalPlaces(decimal step)
    {
        var renderer = new AxisRenderer();
        var method = typeof(AxisRenderer).GetMethod("GetDecimalPlaces", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return (int)method!.Invoke(renderer, new object[] { step })!;
    }

    [Theory]
    [InlineData("1", 0)]
    [InlineData("0.1", 1)]
    [InlineData("0.01", 2)]
    [InlineData("0.000001", 6)]
    [InlineData("0.0000001", 6)]
    [InlineData("0.00000000001", 6)]
    [InlineData("1.50", 2)]
    [InlineData("0", 2)]
    [InlineData("-0.5", 2)]
    public void GetDecimalPlaces_ReturnsScaleClampedToSix(string step, int expected)
    {
        Assert.Equal(expected, GetDecimalPlaces(decimal.Parse(step, System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static List<AxisLabelRenderer.ResolvedLabel> Cascade(int n)
    {
        var labels = new List<AxisLabelRenderer.ResolvedLabel>();
        for (int i = 0; i < n; i++)
        {
            labels.Add(new AxisLabelRenderer.ResolvedLabel(i * 3f, i * 3f, "L" + i, SKColors.Red, AxisLabelStyle.Default, 0));
        }
        return labels;
    }

    [Theory]
    [InlineData(0, 0f, 0f, 0f, 0d)]
    [InlineData(1, 0f, 0f, 0f, 0d)]
    [InlineData(2, -8.5f, 11.5f, 0f, 3d)]
    [InlineData(3, -17f, 23f, 3f, 9d)]
    [InlineData(64, -535.5f, 724.5f, 104.5f, 6048d)]
    [InlineData(65, -544f, 736f, 96f, 6240d)]
    [InlineData(200, -2980f, 1000f, -980f, -198000d)]
    [InlineData(1000, -18980f, 1000f, -8980f, -8990000d)]
    public void ResolveOverlaps_ProducesPinnedPositions(int n, float first, float last, float mid, double sum)
    {
        var labels = Cascade(n);

        AxisLabelRenderer.ResolveOverlaps(labels, 0f, 1000f);

        Assert.Equal(n, labels.Count);
        if (n > 0)
        {
            Assert.Equal(first, labels[0].Y);
            Assert.Equal(last, labels[n - 1].Y);
        }
        if (n > 2) Assert.Equal(mid, labels[n / 2].Y);
        double total = 0;
        foreach (var l in labels) total += l.Y;
        Assert.Equal(sum, total);
    }
}
