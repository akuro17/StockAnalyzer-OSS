using System;
using Avalonia;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using Xunit;
using Point = Avalonia.Point;

namespace StockAnalyzer.Tests.Drawing;

public sealed class ScatterCoordinateTransformTests
{
    private const double Tolerance = 1e-6;

    [Fact]
    public void LinearTransform_ForwardAndInverse_ReversibleWithinTolerance()
    {
        var transform = new ScatterCoordinateTransform(
            minX: -10.0,
            maxX: 50.0,
            minY: 100.0,
            maxY: 500.0,
            viewportLeft: 50.0,
            viewportTop: 20.0,
            viewportWidth: 800.0,
            viewportHeight: 400.0,
            canvasWidth: 900.0,
            canvasHeight: 500.0,
            xScale: PriceScaleType.Linear,
            yScale: PriceScaleType.Linear);

        double[] testX = [-10.0, 0.0, 20.0, 50.0, 33.33333];
        double[] testY = [100.0, 250.0, 300.0, 500.0, 412.3456];

        foreach (double x in testX)
        {
            foreach (double y in testY)
            {
                var screenPoint = transform.NumericToScreen(x, y);
                var (invX, invY) = transform.ScreenToNumeric(screenPoint);

                Assert.Equal(x, invX, Tolerance);
                Assert.Equal(y, invY, Tolerance);
            }
        }
    }

    [Fact]
    public void LogTransform_ForwardAndInverse_ReversibleWithinTolerance()
    {
        var transform = new ScatterCoordinateTransform(
            minX: 1.0,
            maxX: 1000.0,
            minY: 0.1,
            maxY: 100.0,
            viewportLeft: 60.0,
            viewportTop: 30.0,
            viewportWidth: 600.0,
            viewportHeight: 300.0,
            canvasWidth: 700.0,
            canvasHeight: 400.0,
            xScale: PriceScaleType.Log,
            yScale: PriceScaleType.Log);

        double[] testX = [1.0, 10.0, 100.0, 500.0, 1000.0];
        double[] testY = [0.1, 1.0, 10.0, 50.0, 100.0];

        foreach (double x in testX)
        {
            foreach (double y in testY)
            {
                var screenPoint = transform.NumericToScreen(x, y);
                var (invX, invY) = transform.ScreenToNumeric(screenPoint);

                // Relative error check for log scale
                Assert.True(Math.Abs((invX - x) / x) < Tolerance, $"X mismatch: expected {x}, got {invX}");
                Assert.True(Math.Abs((invY - y) / y) < Tolerance, $"Y mismatch: expected {y}, got {invY}");
            }
        }
    }

    [Fact]
    public void DegenerateDataBounds_IdenticalMinMax_FallsBackToCenterWithoutException()
    {
        var transform = new ScatterCoordinateTransform(
            minX: 5.0,
            maxX: 5.0,
            minY: 10.0,
            maxY: 10.0,
            viewportLeft: 100.0,
            viewportTop: 50.0,
            viewportWidth: 400.0,
            viewportHeight: 200.0,
            canvasWidth: 600.0,
            canvasHeight: 300.0);

        var screenPoint = transform.NumericToScreen(5.0, 10.0);

        // Center of viewport: Left + 0.5 * Width = 100 + 200 = 300
        // Top + 0.5 * Height = 50 + 100 = 150
        Assert.Equal(300.0, screenPoint.X, Tolerance);
        Assert.Equal(150.0, screenPoint.Y, Tolerance);
    }

    [Fact]
    public void DegenerateViewport_ZeroDimensions_SafeFallbackWithoutDivideByZero()
    {
        var transform = new ScatterCoordinateTransform(
            minX: 0.0,
            maxX: 100.0,
            minY: 0.0,
            maxY: 100.0,
            viewportLeft: 10.0,
            viewportTop: 10.0,
            viewportWidth: 0.0,
            viewportHeight: 0.0,
            canvasWidth: 10.0,
            canvasHeight: 10.0);

        var screenPoint = transform.NumericToScreen(50.0, 50.0);
        Assert.Equal(10.0, screenPoint.X, Tolerance);
        Assert.Equal(10.0, screenPoint.Y, Tolerance);

        var (invX, invY) = transform.ScreenToNumeric(screenPoint);
        Assert.Equal(0.0, invX, Tolerance);
        Assert.Equal(0.0, invY, Tolerance);
    }

    [Fact]
    public void NonFiniteInputs_YieldNaNWithoutExceptions()
    {
        var transform = new ScatterCoordinateTransform(
            minX: 0.0, maxX: 10.0, minY: 0.0, maxY: 10.0,
            viewportLeft: 0.0, viewportTop: 0.0,
            viewportWidth: 100.0, viewportHeight: 100.0);

        var ptNaN = transform.NumericToScreen(double.NaN, 5.0);
        Assert.True(double.IsNaN(ptNaN.X));
        Assert.False(double.IsNaN(ptNaN.Y));

        var ptInf = transform.NumericToScreen(5.0, double.PositiveInfinity);
        Assert.False(double.IsNaN(ptInf.X));
        Assert.True(double.IsNaN(ptInf.Y));

        var (nanX, nanY) = transform.ScreenToNumeric(new Point(double.NaN, 50.0));
        Assert.True(double.IsNaN(nanX));
        Assert.False(double.IsNaN(nanY));
    }

    [Fact]
    public void LogScale_NonPositiveInputs_ReturnNaNWithoutArtifacts()
    {
        var transform = new ScatterCoordinateTransform(
            minX: 1.0, maxX: 100.0, minY: 1.0, maxY: 100.0,
            viewportLeft: 0.0, viewportTop: 0.0,
            viewportWidth: 100.0, viewportHeight: 100.0,
            xScale: PriceScaleType.Log,
            yScale: PriceScaleType.Log);

        Assert.True(transform.CanUseLogX);
        Assert.True(transform.CanUseLogY);
        Assert.True(transform.CanUseLogScale);

        // Non-positive input to log scale returns double.NaN to prevent edge-sticking artifacts
        var ptZero = transform.NumericToScreen(0.0, -10.0);
        Assert.True(double.IsNaN(ptZero.X));
        Assert.True(double.IsNaN(ptZero.Y));

        // When bounds are completely non-positive
        var nonPositiveTransform = new ScatterCoordinateTransform(
            minX: -50.0, maxX: 0.0, minY: -10.0, maxY: -1.0,
            xScale: PriceScaleType.Log,
            yScale: PriceScaleType.Log);

        Assert.False(nonPositiveTransform.CanUseLogX);
        Assert.False(nonPositiveTransform.CanUseLogY);
        Assert.False(nonPositiveTransform.CanUseLogScale);
        Assert.True(double.IsNaN(nonPositiveTransform.XToScreen(5.0)));
        Assert.True(double.IsNaN(nonPositiveTransform.YToScreen(5.0)));
    }

    [Fact]
    public void ICoordinateTransform_ChartMethods_ExecuteCorrectly()
    {
        var transform = new ScatterCoordinateTransform(
            minX: 0.0, maxX: 100.0, minY: 0.0, maxY: 100.0,
            viewportLeft: 0.0, viewportTop: 0.0,
            viewportWidth: 100.0, viewportHeight: 100.0,
            canvasWidth: 100.0, canvasHeight: 100.0);

        var dt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var chartPt = new ChartPoint(dt, 50m);

        // ChartToScreen and ScreenToChart
        var screenPt = transform.ChartToScreen(chartPt);
        Assert.False(double.IsNaN(screenPt.X));
        Assert.Equal(50.0, screenPt.Y, Tolerance);

        var restoredChartPt = transform.ScreenToChart(screenPt);
        Assert.Equal(50m, restoredChartPt.Price);

        // GetXFromIndex and GetYFromPrice
        Assert.Equal(50.0, transform.GetXFromIndex(50.0), Tolerance);
        Assert.Equal(50.0, transform.GetYFromPrice(50m), Tolerance);
    }
}
