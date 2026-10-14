using System;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

/// <summary>
/// The screen X of a chart point depends on its time only, never on its price. The Point &amp; Figure renderer once measured a "column width" as the X
/// difference between two points of the same time and different prices (1 and 0): that difference is 0 for every transform setting, so the
/// on-chart trend-count label it guarded was never drawn. The count is shown in the Data tab only; this test keeps the premise on record.
/// </summary>
public class ChartPointXIgnoresPriceTests
{
    [Theory]
    [InlineData(ChartAxisMode.Time)]
    [InlineData(ChartAxisMode.GaplessTime)]
    [InlineData(ChartAxisMode.Index)]
    public void ChartToScreen_XDifferenceBetweenTwoPricesAtTheSameTime_IsZero(ChartAxisMode mode)
    {
        var transform = new GenericCoordinateTransform(mode, 1000, 500);
        transform.UpdateRange(new DateTime(2026, 1, 1), new DateTime(2026, 1, 30), 50m, 200m);

        double xAtPrice1 = transform.ChartToScreen(new ChartPoint(DateTime.MinValue, 1m)).X;
        double xAtPrice0 = transform.ChartToScreen(new ChartPoint(DateTime.MinValue, 0m)).X;

        Assert.Equal(0.0, Math.Abs(xAtPrice1 - xAtPrice0));
    }

    [Fact]
    public void ChartToScreen_XDifferenceBetweenTwoTimes_IsNotZero_SoTheScaleXIsTheColumnWidth()
    {
        var transform = new GenericCoordinateTransform(ChartAxisMode.Index, 1000, 500);
        transform.UpdateRange(new DateTime(100), new DateTime(200), 50m, 200m);

        double x0 = transform.ChartToScreen(GenericCoordinateTransform.SafeIndexPoint(100)).X;
        double x1 = transform.ChartToScreen(GenericCoordinateTransform.SafeIndexPoint(101)).X;

        Assert.True(Math.Abs(x1 - x0) > 0.0);
        Assert.Equal(transform.ScaleX, Math.Abs(x1 - x0), 6);
    }
}
