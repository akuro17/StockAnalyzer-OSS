using System;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

/// <summary>
/// "Auto Elliott Wave" / "HarmonicPattern" / "DTW Projection" draw a light background band
/// between their start/end points whose opacity was previously a hardcoded, non-adjustable
/// SKColor alpha byte. `FillOpacity` (0-100%) makes it user-configurable via the individual
/// settings dialog, defaulting to 10% across all three tools (unified per user request, rather
/// than each tool preserving its old distinct hardcoded alpha).
/// </summary>
public class PatternFillOpacityTests
{
    private static LinearCoordinateTransform CreateTransform(int size)
        => new LinearCoordinateTransform(
            minTime: new DateTime(2026, 1, 1), maxTime: new DateTime(2026, 1, 1).AddDays(size),
            minPrice: 0m, maxPrice: size,
            canvasWidth: size, canvasHeight: size);

    [Fact]
    public void HarmonicPatternObject_DefaultFillOpacity_Is10Percent()
    {
        var obj = new HarmonicPatternObject();
        Assert.Equal(10, obj.FillOpacity);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(100)]
    public void HarmonicPatternObject_Render_BackgroundBandAlpha_MatchesFillOpacity(int fillOpacity)
    {
        const int size = 40;
        using var bitmap = new SKBitmap(size, size);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        var transform = CreateTransform(size);

        var obj = new HarmonicPatternObject { FillOpacity = fillOpacity };
        obj.Points.Add(new ChartPoint(new DateTime(2026, 1, 6), 10m));
        obj.Points.Add(new ChartPoint(new DateTime(2026, 1, 21), 30m));

        obj.Render(canvas, transform);
        canvas.Flush();

        var p1 = transform.ChartToScreen(obj.Points[0]);
        var p2 = transform.ChartToScreen(obj.Points[1]);
        int midX = (int)((p1.X + p2.X) / 2);
        int midY = size / 2;

        byte expectedAlpha = (byte)(255 * fillOpacity / 100.0);
        Assert.Equal(expectedAlpha, bitmap.GetPixel(midX, midY).Alpha);
    }

    [Fact]
    public void HarmonicPatternObject_DefaultFillColor_MatchesGlobalDefaultDrawingColor()
    {
        var obj = new HarmonicPatternObject();
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.FillColor);
    }

    [Fact]
    public void HarmonicPatternObject_Render_BackgroundBandRgb_MatchesFillColor_NotLineColor()
    {
        const int size = 40;
        using var bitmap = new SKBitmap(size, size);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        var transform = CreateTransform(size);

        var obj = new HarmonicPatternObject
        {
            FillOpacity = 100, // fully opaque so the RGB read-back is exact
            Color = global::Avalonia.Media.Colors.Red,
            FillColor = global::Avalonia.Media.Colors.Blue
        };
        obj.Points.Add(new ChartPoint(new DateTime(2026, 1, 6), 10m));
        obj.Points.Add(new ChartPoint(new DateTime(2026, 1, 21), 30m));

        obj.Render(canvas, transform);
        canvas.Flush();

        var p1 = transform.ChartToScreen(obj.Points[0]);
        var p2 = transform.ChartToScreen(obj.Points[1]);
        int midX = (int)((p1.X + p2.X) / 2);
        int midY = size / 2;

        var pixel = bitmap.GetPixel(midX, midY);
        Assert.Equal(SKColors.Blue.Red, pixel.Red);
        Assert.Equal(SKColors.Blue.Blue, pixel.Blue);
    }

    [Fact]
    public void AutoElliottWaveObject_DefaultFillOpacity_Is10Percent()
    {
        var obj = new AutoElliottWaveObject();
        Assert.Equal(10, obj.FillOpacity);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(100)]
    public void AutoElliottWaveObject_Render_BackgroundBandAlpha_MatchesFillOpacity(int fillOpacity)
    {
        const int size = 40;
        using var bitmap = new SKBitmap(size, size);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        var transform = CreateTransform(size);

        var obj = new AutoElliottWaveObject { FillOpacity = fillOpacity };
        obj.Points.Add(new ChartPoint(new DateTime(2026, 1, 6), 10m));
        obj.Points.Add(new ChartPoint(new DateTime(2026, 1, 21), 30m));

        obj.Render(canvas, transform);
        canvas.Flush();

        var p1 = transform.ChartToScreen(obj.Points[0]);
        var p2 = transform.ChartToScreen(obj.Points[1]);
        int midX = (int)((p1.X + p2.X) / 2);
        int midY = size / 2;

        byte expectedAlpha = (byte)(255 * fillOpacity / 100.0);
        Assert.Equal(expectedAlpha, bitmap.GetPixel(midX, midY).Alpha);
    }

    [Fact]
    public void AutoElliottWaveObject_DefaultFillColor_MatchesGlobalDefaultDrawingColor()
    {
        var obj = new AutoElliottWaveObject();
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.FillColor);
    }

    [Fact]
    public void AutoElliottWaveObject_Render_BackgroundBandRgb_MatchesFillColor_NotLineColor()
    {
        const int size = 40;
        using var bitmap = new SKBitmap(size, size);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        var transform = CreateTransform(size);

        var obj = new AutoElliottWaveObject
        {
            FillOpacity = 100,
            Color = global::Avalonia.Media.Colors.Red,
            FillColor = global::Avalonia.Media.Colors.Blue
        };
        obj.Points.Add(new ChartPoint(new DateTime(2026, 1, 6), 10m));
        obj.Points.Add(new ChartPoint(new DateTime(2026, 1, 21), 30m));

        obj.Render(canvas, transform);
        canvas.Flush();

        var p1 = transform.ChartToScreen(obj.Points[0]);
        var p2 = transform.ChartToScreen(obj.Points[1]);
        int midX = (int)((p1.X + p2.X) / 2);
        int midY = size / 2;

        var pixel = bitmap.GetPixel(midX, midY);
        Assert.Equal(SKColors.Blue.Red, pixel.Red);
        Assert.Equal(SKColors.Blue.Blue, pixel.Blue);
    }

    [Fact]
    public void DtwProjectionObject_DefaultFillOpacity_Is10Percent()
    {
        var obj = new DtwProjectionObject();
        Assert.Equal(10, obj.FillOpacity);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(100)]
    public void DtwProjectionObject_Render_SelectionBandAlpha_MatchesFillOpacity(int fillOpacity)
    {
        const int size = 40;
        using var bitmap = new SKBitmap(size, size);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        var transform = CreateTransform(size);

        var obj = new DtwProjectionObject { FillOpacity = fillOpacity };
        obj.Points.Add(new ChartPoint(new DateTime(2026, 1, 6), 10m));
        obj.Points.Add(new ChartPoint(new DateTime(2026, 1, 21), 30m));

        obj.Render(canvas, transform);
        canvas.Flush();

        var p1 = transform.ChartToScreen(obj.Points[0]);
        var p2 = transform.ChartToScreen(obj.Points[1]);
        int midX = (int)((p1.X + p2.X) / 2);
        int midY = size / 2;

        byte expectedAlpha = (byte)(255 * fillOpacity / 100.0);
        Assert.Equal(expectedAlpha, bitmap.GetPixel(midX, midY).Alpha);
    }

    [Fact]
    public void DtwProjectionObject_DefaultFutureSteps_Is20()
    {
        var obj = new DtwProjectionObject();
        Assert.Equal(20, obj.FutureSteps);
    }

    [Fact]
    public void DtwProjectionObject_DefaultFillColor_MatchesGlobalDefaultDrawingColor()
    {
        var obj = new DtwProjectionObject();
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.FillColor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DtwProjectionObject_Render_SelectionBandRgb_MatchesFillColor_RegardlessOfMatchStatus(bool isUnmatched)
    {
        const int size = 40;
        using var bitmap = new SKBitmap(size, size);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        var transform = CreateTransform(size);

        var obj = new DtwProjectionObject
        {
            FillOpacity = 100, // fully opaque so the RGB read-back is exact
            IsUnmatched = isUnmatched,
            Color = global::Avalonia.Media.Colors.Red,
            UnmatchedColor = global::Avalonia.Media.Colors.Yellow,
            FillColor = global::Avalonia.Media.Colors.Blue
        };
        obj.Points.Add(new ChartPoint(new DateTime(2026, 1, 6), 10m));
        obj.Points.Add(new ChartPoint(new DateTime(2026, 1, 21), 30m));

        obj.Render(canvas, transform);
        canvas.Flush();

        var p1 = transform.ChartToScreen(obj.Points[0]);
        var p2 = transform.ChartToScreen(obj.Points[1]);
        int midX = (int)((p1.X + p2.X) / 2);
        int midY = size / 2;

        var pixel = bitmap.GetPixel(midX, midY);
        Assert.Equal(SKColors.Blue.Red, pixel.Red);
        Assert.Equal(SKColors.Blue.Blue, pixel.Blue);
    }
}
