using System;
using System.Collections.Generic;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Drawing.Renderers;
using Xunit;
using DrawingPoint = StockAnalyzer.Core.Models.Point;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class DtwProjectionDbaRendererTests
{
    [Fact]
    public void Render_WithDbaEnsembleFanAndProjectedPath_DrawsWithoutException()
    {
        var dtw = new DtwProjectionObject
        {
            UseDbaEnsemble = true,
            TopKMatches = 5,
            FillOpacity = 20
        };

        var p0 = PixelCoordinateTransform.Chart(10, 100);
        var p1 = PixelCoordinateTransform.Chart(50, 105);
        dtw.Points.Add(p0);
        dtw.Points.Add(p1);

        dtw.ProjectedPath = new List<DrawingPoint>
        {
            new((double)p1.Time.AddDays(10).Ticks, 106.0),
            new((double)p1.Time.AddDays(20).Ticks, 108.0),
            new((double)p1.Time.AddDays(30).Ticks, 110.0)
        };
        dtw.UpperBandPath = new List<DrawingPoint>
        {
            new((double)p1.Time.AddDays(10).Ticks, 109.0),
            new((double)p1.Time.AddDays(20).Ticks, 112.0),
            new((double)p1.Time.AddDays(30).Ticks, 115.0)
        };
        dtw.LowerBandPath = new List<DrawingPoint>
        {
            new((double)p1.Time.AddDays(10).Ticks, 103.0),
            new((double)p1.Time.AddDays(20).Ticks, 104.0),
            new((double)p1.Time.AddDays(30).Ticks, 105.0)
        };

        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        surface.Canvas.Clear(SKColors.Transparent);
        var renderer = new DtwProjectionRenderer();
        var transform = new PixelCoordinateTransform();

        renderer.Render(surface.Canvas, dtw, transform, isSelected: true);

        using var snapshot = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(snapshot);
        Assert.Contains(bitmap.Pixels, p => p.Alpha > 0);

        dtw.Translate(TimeSpan.FromDays(2), 5m);
        Assert.Equal(114.0, dtw.UpperBandPath[0].Y, 6);
        Assert.Equal(108.0, dtw.LowerBandPath[0].Y, 6);
    }
}
