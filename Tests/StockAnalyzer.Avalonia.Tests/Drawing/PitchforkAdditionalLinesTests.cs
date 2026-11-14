using System;
using System.Linq;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Tests.Common;
using Xunit;
using Point = Avalonia.Point;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public sealed class PitchforkAdditionalLinesTests
{
    private const int Width = 800;
    private const int Height = 600;
    private static readonly DateTime T1 = new(2026, 1, 1);
    private static readonly Color TestColor = Color.FromArgb(255, 64, 160, 240);

    private static PitchforkObject CreateSampleFork(bool showHalf = false, bool showWarning = false) =>
        new(new ChartPoint(T1, 100m), new ChartPoint(T1.AddDays(10), 130m), new ChartPoint(T1.AddDays(20), 90m))
        {
            Color = TestColor,
            Thickness = 2.0,
            PitchforkType = PitchforkType.Schiff,
            ShowHalfLines = showHalf,
            ShowWarningLines = showWarning
        };

    private static GenericCoordinateTransform CreateTransform()
    {
        var transform = new GenericCoordinateTransform(ChartAxisMode.Time, Width, Height);
        transform.UpdateRange(T1.AddDays(-5), T1.AddDays(60), 80m, 140m);
        return transform;
    }

    private static string RenderPngSha256(PitchforkObject obj, ICoordinateTransform transform)
    {
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);
        obj.Render(canvas, transform);
        using var image = surface.Snapshot();
        using var pngData = image.Encode(SKEncodedImageFormat.Png, 100);
        return Convert.ToHexString(SHA256.HashData(pngData.AsSpan()));
    }

    [Fact]
    public void LinearAxis_HalfLineOrigin_EqualsProjectedMidpointOfMedianAndP2()
    {
        var p1 = new ChartPoint(T1, 100m);
        var p2 = new ChartPoint(T1.AddDays(40), 400m);
        var p3 = new ChartPoint(T1.AddDays(80), 50m);
        var transform = new PixelCoordinateTransform();

        var (originHalf, _) = PitchforkObject.GetParallelLineScreenPoints(PitchforkType.Andrews, p1, p2, p3, 0.5m, transform);
        DrawingAnchor median = PitchforkValues.GetMedianPoint(p2.ToAnchor(), p3.ToAnchor());
        var expectedMid = new ChartPoint(
            median.Time + TimeSpan.FromTicks((p2.Time.Ticks - median.Time.Ticks) / 2),
            (median.Price + p2.Price) / 2m);
        Point expectedScreen = transform.ChartToScreen(expectedMid);

        Assert.True(Math.Abs(expectedScreen.X - originHalf.X) <= 0.5 && Math.Abs(expectedScreen.Y - originHalf.Y) <= 0.5,
            $"expected {expectedScreen}, actual {originHalf}");

        // Boundary check: Q(1) = P2 and Q(-1) = P3
        DrawingAnchor q1 = PitchforkValues.GetParallelAnchor(p2.ToAnchor(), median, 1m);
        DrawingAnchor qNeg1 = PitchforkValues.GetParallelAnchor(p2.ToAnchor(), median, -1m);
        Assert.Equal(p2.Price, q1.Price);
        Assert.Equal(p2.Time, q1.Time);
        Assert.Equal(p3.Price, qNeg1.Price);
        Assert.Equal(p3.Time, qNeg1.Time);
    }

    [Fact]
    public void Defaults_MatchT2PinnedRenderHash_AndDoesNotAddCalculatedValues()
    {
        using var obj = CreateSampleFork(showHalf: false, showWarning: false);
        var transform = CreateTransform();

        string hash = RenderPngSha256(obj, transform);
        Assert.Equal("8C3E702660AA02CD90EE9CF7864F74F74958DCB367D3310A457C3EEEF267BAA0", hash);

        obj.ShowHalfLines = true;
        obj.ShowWarningLines = true;
        var values = obj.GetCalculatedValues(T1.AddDays(15));
        Assert.Equal(4, values.Count);
        Assert.Equal(new[] { "UpperLine", "MedianLine", "LowerLine", "ForkType" }, values.Select(v => v.Key).ToArray());
    }

    [Fact]
    public void HitTest_OnlyHitsAdditionalLinesWhenEnabled()
    {
        var p1 = new ChartPoint(new DateTime(2025, 1, 1), 200m);
        var p2 = new ChartPoint(new DateTime(2025, 1, 1).AddDays(40), 260m);
        var p3 = new ChartPoint(new DateTime(2025, 1, 1).AddDays(80), 220m);
        var transform = new PixelCoordinateTransform();

        using var defaultFork = new PitchforkObject(p1, p2, p3);
        using var warningFork = new PitchforkObject(p1, p2, p3) { ShowWarningLines = true };
        using var halfFork = new PitchforkObject(p1, p2, p3) { ShowHalfLines = true };

        // Probe far along the f = 2.0 warning line ray (well away from outer/median lines and handles).
        var (wOrigin, wTarget) = PitchforkObject.GetParallelLineScreenPoints(PitchforkType.Andrews, p1, p2, p3, 2.0m, transform);
        Point warningRayPoint = wOrigin + (wTarget - wOrigin) * 2.0;

        Assert.False(defaultFork.HitTest(warningRayPoint, transform, tolerance: 3.0));
        Assert.False(halfFork.HitTest(warningRayPoint, transform, tolerance: 3.0));
        Assert.True(warningFork.HitTest(warningRayPoint, transform, tolerance: 3.0));

        // Probe along the f = 0.5 half line ray (between median and P2 outer line, well past anchor).
        var (hOrigin, hTarget) = PitchforkObject.GetParallelLineScreenPoints(PitchforkType.Andrews, p1, p2, p3, 0.5m, transform);
        Point halfRayPoint = hOrigin + (hTarget - hOrigin) * 3.0;

        Assert.False(defaultFork.HitTest(halfRayPoint, transform, tolerance: 2.0));
        Assert.False(warningFork.HitTest(halfRayPoint, transform, tolerance: 2.0));
        Assert.True(halfFork.HitTest(halfRayPoint, transform, tolerance: 2.0));
    }

    [Fact]
    public void Render_WithAdditionalLinesEnabled_AllocatesZeroManagedBytes()
    {
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        var canvas = surface.Canvas;
        var transform = CreateTransform();
        using var obj = CreateSampleFork(showHalf: true, showWarning: true);

        canvas.Clear(SKColors.Black);
        obj.Render(canvas, transform);

        long bytes = AllocationProbe.InCollectionFreeWindow(
            () => obj.Render(canvas, transform),
            () => canvas.Clear(SKColors.Black));

        Assert.Equal(0, bytes);
    }

    [AvaloniaFact]
    public void DrawingParameterViewBuilder_ExposesPitchforkTypeHalfLinesAndWarningLines()
    {
        using var obj = CreateSampleFork();
        var metadata = DrawingParameterViewBuilder.GetOrReflectMetadata(typeof(PitchforkObject));

        var typeMeta = Assert.Single(metadata, m => m.Prop.Name == nameof(PitchforkObject.PitchforkType));
        var halfMeta = Assert.Single(metadata, m => m.Prop.Name == nameof(PitchforkObject.ShowHalfLines));
        var warnMeta = Assert.Single(metadata, m => m.Prop.Name == nameof(PitchforkObject.ShowWarningLines));
        Assert.DoesNotContain(metadata, m => m.Prop.Name == nameof(PitchforkObject.IsInside));

        Assert.Contains(DrawingParameterTags.Analysis, typeMeta.Tags);
        Assert.Contains(DrawingParameterTags.Analysis, halfMeta.Tags);
        Assert.Contains(DrawingParameterTags.Analysis, warnMeta.Tags);

        var builder = new DrawingParameterViewBuilder();
        var hiddenCommonAndGeometry = new[] { DrawingParameterTags.Common, DrawingParameterTags.Geometry };
        var control = Assert.IsType<StackPanel>(builder.Build(obj, hiddenCommonAndGeometry));
        var cards = control.Children.OfType<Border>().ToList();
        Assert.NotEmpty(cards);

        var checkBoxes = cards.SelectMany(c => ((StackPanel)c.Child!).Children).OfType<CheckBox>().ToList();
        Assert.Equal(2, checkBoxes.Count);
        Assert.DoesNotContain(checkBoxes, cb => string.Equals(cb.Content?.ToString(), "Is Inside", StringComparison.OrdinalIgnoreCase));
        bool hasComboBox = cards.SelectMany(c => ((StackPanel)c.Child!).Children).OfType<Grid>().Any(g => g.Children.OfType<ComboBox>().Any());
        Assert.True(hasComboBox);
    }
}
