using System;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;
using Xunit;
using Point = Avalonia.Point;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// The Pitchfork anchor and the P2-P3 midpoint are decided in chart coordinates (time, price) and only then projected to the screen, so the drawn
/// line, the hit area and the Data-tab values describe the same geometry on a linear and on a logarithmic price axis.
/// </summary>
public sealed class PitchforkGeometryTests
{
    private static readonly DateTime T1 = new(2025, 1, 1);

    // P1 -> P2 -> P3 with a wide price range, so that the arithmetic and the geometric (screen) midpoint on a log axis are far apart.
    private static PitchforkObject Fork(PitchforkType type) => new(
        new ChartPoint(T1, 100m),
        new ChartPoint(T1.AddDays(40), 400m),
        new ChartPoint(T1.AddDays(80), 50m))
    { PitchforkType = type };

    private static ChartPoint DataAnchor(PitchforkType type, ChartPoint p1, ChartPoint p2) => type switch
    {
        PitchforkType.Schiff => new ChartPoint(p1.Time, (p1.Price + p2.Price) / 2m),
        PitchforkType.ModifiedSchiff => new ChartPoint(p1.Time + (p2.Time - p1.Time) / 2, (p1.Price + p2.Price) / 2m),
        _ => p1,
    };

    private static ChartPoint DataMidpoint(ChartPoint p2, ChartPoint p3) => new(p2.Time + (p3.Time - p2.Time) / 2, (p2.Price + p3.Price) / 2m);

    [Theory]
    [InlineData(PitchforkType.Andrews)]
    [InlineData(PitchforkType.Schiff)]
    [InlineData(PitchforkType.ModifiedSchiff)]
    public void LinearAxis_ScreenShape_EqualsTheProjectedDataPoints(PitchforkType type)
    {
        var fork = Fork(type);
        var transform = new PixelCoordinateTransform();
        var p1 = fork.Points[0];
        var p2 = fork.Points[1];
        var p3 = fork.Points[2];

        var shape = PitchforkObject.GetScreenShape(type, p1, p2, p3, transform);

        AssertNear(transform.ChartToScreen(DataAnchor(type, p1, p2)), shape.Anchor);
        AssertNear(transform.ChartToScreen(DataMidpoint(p2, p3)), shape.Median);
        AssertNear(transform.ChartToScreen(p1), shape.P1);
        AssertNear(transform.ChartToScreen(p2), shape.P2);
        AssertNear(transform.ChartToScreen(p3), shape.P3);
    }

    [Theory]
    [InlineData(PitchforkType.Andrews)]
    [InlineData(PitchforkType.Schiff)]
    [InlineData(PitchforkType.ModifiedSchiff)]
    public void LogAxis_ScreenShape_EqualsTheProjectedDataPoints_AndTheDataTabMedianValueAtTheAnchorIsTheAnchorPrice(PitchforkType type)
    {
        var fork = Fork(type);
        var transform = new PixelCoordinateTransform(logarithmicPrice: true);
        var p1 = fork.Points[0];
        var p2 = fork.Points[1];
        var p3 = fork.Points[2];
        var dataAnchor = DataAnchor(type, p1, p2);

        var shape = PitchforkObject.GetScreenShape(type, p1, p2, p3, transform);

        AssertNear(transform.ChartToScreen(dataAnchor), shape.Anchor);
        AssertNear(transform.ChartToScreen(DataMidpoint(p2, p3)), shape.Median);

        var median = Assert.Single(fork.GetCalculatedValues(dataAnchor.Time), v => v.Key == "MedianLine");
        Assert.Equal(dataAnchor.Price, median.NumericValue);
    }

    [Fact]
    public void PitchforkKind_HasTheSameMembersAndNumbersAsPitchforkType()
    {
        var guiNames = Enum.GetNames<PitchforkType>();
        var coreNames = Enum.GetNames<PitchforkKind>();
        Assert.Equal(guiNames, coreNames);
        foreach (var type in Enum.GetValues<PitchforkType>())
        {
            Assert.Equal((int)type, (int)PitchforkObject.ToKind(type));
            Assert.Equal(type.ToString(), PitchforkObject.ToKind(type).ToString());
        }
    }

    private static void AssertNear(Point expected, Point actual)
    {
        Assert.True(Math.Abs(expected.X - actual.X) <= 0.5 && Math.Abs(expected.Y - actual.Y) <= 0.5, $"expected {expected}, actual {actual}");
    }

    [Theory]
    [InlineData(PitchforkType.Andrews)]
    [InlineData(PitchforkType.Schiff)]
    [InlineData(PitchforkType.ModifiedSchiff)]
    public void LogAxis_MedianLine_PassesThroughTheProjectedDataAnchorAndDataMidpoint(PitchforkType type)
    {
        var fork = Fork(type);
        var transform = new PixelCoordinateTransform(logarithmicPrice: true);
        var p1 = fork.Points[0];
        var p2 = fork.Points[1];
        var p3 = fork.Points[2];

        var dataAnchor = DataAnchor(type, p1, p2);
        var dataMidpoint = new ChartPoint(p2.Time + (p3.Time - p2.Time) / 2, (p2.Price + p3.Price) / 2m);

        Assert.True(fork.HitTest(transform.ChartToScreen(dataAnchor), transform, tolerance: 1.5), "anchor");
        Assert.True(fork.HitTest(transform.ChartToScreen(dataMidpoint), transform, tolerance: 1.5), "midpoint");
    }
}
