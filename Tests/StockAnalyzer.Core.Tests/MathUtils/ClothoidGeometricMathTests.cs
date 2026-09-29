using System;
using SkiaSharp;
using StockAnalyzer.Core.MathUtils;
using Xunit;

namespace StockAnalyzer.Core.Tests.MathUtils;

public class ClothoidGeometricMathTests
{
    [Fact]
    public void GenerateClothoidArc_ZeroCurvature_ReturnsStraightSegment()
    {
        var start = new SKPoint(100f, 100f);
        var end = new SKPoint(300f, 100f);
        Span<SKPoint> buffer = stackalloc SKPoint[65];

        int count = ClothoidGeometricMath.GenerateClothoidArc(start, end, 0.0, buffer, out double terminalAngle);

        Assert.Equal(2, count);
        Assert.Equal(start, buffer[0]);
        Assert.Equal(end, buffer[1]);
        Assert.Equal(0.0, terminalAngle, 1e-4);
    }

    [Fact]
    public void GenerateClothoidArc_DegenerateDistance_ReturnsEndpoints()
    {
        var start = new SKPoint(100f, 100f);
        var end = new SKPoint(100.5f, 100.2f); // Distance < 1 px
        Span<SKPoint> buffer = stackalloc SKPoint[65];

        int count = ClothoidGeometricMath.GenerateClothoidArc(start, end, 0.5, buffer, out double terminalAngle);

        Assert.Equal(2, count);
        Assert.Equal(start, buffer[0]);
        Assert.Equal(end, buffer[1]);
    }

    [Theory]
    [InlineData(0.2)]
    [InlineData(0.5)]
    [InlineData(-0.3)]
    [InlineData(-0.8)]
    public void GenerateClothoidArc_StrictC0Boundary_StartAndEndMatchExactly(double curvature)
    {
        var start = new SKPoint(50f, 80f);
        var end = new SKPoint(450f, 280f);
        Span<SKPoint> buffer = stackalloc SKPoint[65];

        int count = ClothoidGeometricMath.GenerateClothoidArc(start, end, curvature, buffer, out _);

        Assert.True(count >= 16);
        Assert.Equal(start.X, buffer[0].X, 1e-4f);
        Assert.Equal(start.Y, buffer[0].Y, 1e-4f);
        Assert.Equal(end.X, buffer[count - 1].X, 1e-4f);
        Assert.Equal(end.Y, buffer[count - 1].Y, 1e-4f);
    }

    [Fact]
    public void GenerateClothoidArc_OppositeCurvatures_ExhibitMirrorSymmetry()
    {
        var start = new SKPoint(0f, 0f);
        var end = new SKPoint(200f, 0f); // Horizontal chord
        Span<SKPoint> posBuffer = stackalloc SKPoint[65];
        Span<SKPoint> negBuffer = stackalloc SKPoint[65];

        int posCount = ClothoidGeometricMath.GenerateClothoidArc(start, end, 0.6, posBuffer, out double posAngle);
        int negCount = ClothoidGeometricMath.GenerateClothoidArc(start, end, -0.6, negBuffer, out double negAngle);

        Assert.Equal(posCount, negCount);

        // At midpoint, X coordinates should match and Y coordinates should be opposite
        int mid = posCount / 2;
        Assert.Equal(posBuffer[mid].X, negBuffer[mid].X, 1.0f);
        Assert.Equal(posBuffer[mid].Y, -negBuffer[mid].Y, 1.0f);

        // Terminal tangent angles should be opposite
        Assert.Equal(posAngle, -negAngle, 1e-4);
    }

    [Fact]
    public void GenerateClothoidArc_CurvatureBendsTerminalTangent()
    {
        var start = new SKPoint(100f, 100f);
        var end = new SKPoint(200f, 100f); // Chord angle = 0.0
        Span<SKPoint> buffer = stackalloc SKPoint[ClothoidGeometricMath.MaxSamplePoints + 1];

        ClothoidGeometricMath.GenerateClothoidArc(start, end, 0.5, buffer, out double terminalAngle);

        // With positive curvature, terminal angle should be greater than chord angle (0.0)
        Assert.True(terminalAngle > 0.0);
    }

    [Fact]
    public void GenerateClothoidArc_LongDistance_AdaptsStepCountUpToMax()
    {
        var start = new SKPoint(0f, 0f);
        var end = new SKPoint(1200f, 0f); // Long 1200px distance
        Span<SKPoint> buffer = stackalloc SKPoint[ClothoidGeometricMath.MaxSamplePoints + 1];

        int count = ClothoidGeometricMath.GenerateClothoidArc(start, end, 0.8, buffer, out _);

        // Step count should adaptively scale beyond standard 64 up to MaxSamplePoints
        Assert.True(count > 64);
        Assert.True(count <= ClothoidGeometricMath.MaxSamplePoints + 1);
        Assert.Equal(start.X, buffer[0].X, 1e-4f);
        Assert.Equal(end.X, buffer[count - 1].X, 1e-4f);
    }

    [Fact]
    public void GenerateClothoidArc_SmallDestinationBuffer_ReturnsStraightEndpointsSafelyWithoutOverflow()
    {
        var start = new SKPoint(10f, 10f);
        var end = new SKPoint(200f, 200f);
        // Destination buffer with length 10 (< MinSamplePoints + 1 = 17)
        Span<SKPoint> smallBuffer = stackalloc SKPoint[10];

        int count = ClothoidGeometricMath.GenerateClothoidArc(start, end, 0.7, smallBuffer, out double terminalAngle);

        Assert.Equal(2, count);
        Assert.Equal(start, smallBuffer[0]);
        Assert.Equal(end, smallBuffer[1]);
        Assert.True(terminalAngle != 0.0);
    }
}
