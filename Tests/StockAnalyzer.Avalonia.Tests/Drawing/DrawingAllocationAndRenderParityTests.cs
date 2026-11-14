using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public sealed class DrawingAllocationAndRenderParityTests
{
    private const int Width = 800;
    private const int Height = 600;
    private static readonly DateTime T1 = new(2026, 1, 1);
    private static readonly DateTime T2 = new(2026, 1, 11);
    private static readonly DateTime T3 = new(2026, 1, 8, 12, 0, 0);
    private static readonly DateTime QueryTime = new(2026, 1, 6, 12, 0, 0);
    private static readonly Color TestColor = Color.FromArgb(255, 64, 160, 240);

    private readonly ITestOutputHelper _output;

    public DrawingAllocationAndRenderParityTests(ITestOutputHelper output) => _output = output;

    private static GenericCoordinateTransform CreateGenericTransform()
    {
        var transform = new GenericCoordinateTransform(ChartAxisMode.Time, Width, Height);
        transform.UpdateRange(T1.AddDays(-5), T1.AddDays(60), 80m, 140m);
        return transform;
    }

    private static LinearCoordinateTransform CreateLinearTransform() =>
        new(T1.AddDays(-5), T1.AddDays(60), 80m, 140m, Width, Height);

    private static List<CoreCandleData> CreateSampleCandles()
    {
        var candles = new List<CoreCandleData>(10);
        for (int i = 0; i < 10; i++)
        {
            decimal p = 100m + i * 2m;
            candles.Add(new CoreCandleData(T1.AddDays(i), p, p + 3m, p - 2m, p + 1m, 1000 + i * 100));
        }
        return candles;
    }

    private static PitchforkObject CreatePitchfork() =>
        new(new ChartPoint(T1, 100m), new ChartPoint(T1.AddDays(10), 130m), new ChartPoint(T1.AddDays(20), 90m))
        {
            Color = TestColor,
            Thickness = 2.0,
            PitchforkType = PitchforkType.Schiff
        };

    private static AnchoredVwapObject CreateAnchoredVwap()
    {
        var obj = new AnchoredVwapObject(new ChartPoint(T1, 100m))
        {
            Color = TestColor,
            Thickness = 2.0
        };
        obj.Recalculate(CreateSampleCandles());
        return obj;
    }

    private static HorizontalLineObject CreateHorizontalLine(DrawingStrokeStyle strokeStyle = DrawingStrokeStyle.Dash) =>
        new(new ChartPoint(T1, 110m))
        {
            Color = TestColor,
            Thickness = 2.0,
            StrokeStyle = strokeStyle
        };

    private static FibonacciTimeZoneObject CreateFibonacciTimeZone() =>
        new(new ChartPoint(T1, 100m), new ChartPoint(T1.AddDays(5), 110m))
        {
            Color = TestColor,
            Thickness = 2.0
        };

    private static string RenderPngSha256(IChartObject obj, ICoordinateTransform transform)
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
    public void MeasureRenderAllocations_AndReportPngHashes()
    {
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        SKCanvas canvas = surface.Canvas;
        var genericTransform = CreateGenericTransform();
        var linearTransform = CreateLinearTransform();

        var targets = new (string Name, IChartObject Obj, ICoordinateTransform Transform, long BaselineBytes, string ExpectedPngSha256)[]
        {
            ("PitchforkObject", CreatePitchfork(), genericTransform, 1688, "8C3E702660AA02CD90EE9CF7864F74F74958DCB367D3310A457C3EEEF267BAA0"),
            ("AnchoredVwapObject", CreateAnchoredVwap(), genericTransform, 1768, "2AC641451F163322E7882824C4585E113454B5E96587D13100FCAB677AFC86B1"),
            ("HorizontalLineObject", CreateHorizontalLine(DrawingStrokeStyle.Dash), genericTransform, 1688, "CD1C760EBFD1D14679F7EA6E80BFC8BCB7C399C795087200DDC17C7A04085FDA"),
            ("FibonacciTimeZoneObject", CreateFibonacciTimeZone(), linearTransform, 6056, "C39A8980780760782766BAFE61D26BACDC0F68B0D80BE9EB342327F756A552D4"),
        };

        foreach (var (name, obj, transform, baselineBytes, expectedPngSha256) in targets)
        {
            try
            {
                // Warmup JIT / static caches outside measurement.
                canvas.Clear(SKColors.Black);
                obj.Render(canvas, transform);

                long bytes = AllocationProbe.InCollectionFreeWindow(
                    () => obj.Render(canvas, transform),
                    () => canvas.Clear(SKColors.Black));

                string pngHash = RenderPngSha256(obj, transform);
                _output.WriteLine($"RENDER_PROBE | {name} | Before={baselineBytes} | After={bytes} | PNG_SHA256={pngHash}");
                Assert.True(bytes >= 0, $"Collection overlapped every sample for {name}");
                Assert.True(bytes < baselineBytes, $"{name} allocation ({bytes} B) should be less than baseline ({baselineBytes} B)");
                Assert.Equal(expectedPngSha256, pngHash);
            }
            finally
            {
                (obj as IDisposable)?.Dispose();
            }
        }
    }

    [Fact]
    public void CachedPaintRender_UpdatesMutablePropertiesBetweenFrames()
    {
        var genericTransform = CreateGenericTransform();
        using var line = CreateHorizontalLine(DrawingStrokeStyle.Solid);
        string solidHash = RenderPngSha256(line, genericTransform);

        line.StrokeStyle = DrawingStrokeStyle.Dash;
        string dashHash = RenderPngSha256(line, genericTransform);
        Assert.NotEqual(solidHash, dashHash);
        Assert.Equal("CD1C760EBFD1D14679F7EA6E80BFC8BCB7C399C795087200DDC17C7A04085FDA", dashHash);

        using var vwap = CreateAnchoredVwap();
        string firstVwapHash = RenderPngSha256(vwap, genericTransform);
        vwap.Color = Colors.Yellow;
        vwap.Thickness = 4.0;
        string secondVwapHash = RenderPngSha256(vwap, genericTransform);
        Assert.NotEqual(firstVwapHash, secondVwapHash);
    }

    [Fact]
    public void MeasureGetCalculatedValuesAllocations()
    {
        var p1 = new ChartPoint(T1, 100m);
        var p2 = new ChartPoint(T2, 150m);
        var p3 = new ChartPoint(T3, 130m);

        var pivot = new ClassicPivotPointsObject(new ChartPoint(new DateTime(2026, 9, 22, 10, 0, 0), 105m))
        {
            Period = PivotPeriod.Day
        };
        pivot.Recalculate(new List<CoreCandleData>
        {
            new(new DateTime(2026, 9, 21), 95m, 110m, 90m, 100m, 1000),
            new(new DateTime(2026, 9, 22), 100m, 115m, 98m, 105m, 1200),
            new(new DateTime(2026, 9, 23), 105m, 120m, 102m, 118m, 1500),
        });

        var targets = new (string Name, IDrawingCalculatedValuesProvider Provider, DateTime Ts, long BaselineBytes)[]
        {
            ("RayObject", new RayObject(p1, p2), QueryTime, 560),
            ("ArrowObject", new ArrowObject(p1, p2), QueryTime, 512),
            ("PriceLabelObject", new PriceLabelObject(p1), QueryTime, 360),
            ("VerticalLineObject", new VerticalLineObject(p1), QueryTime, 264),
            ("FibonacciRetracementObject", new FibonacciRetracementObject(p1, p2), QueryTime, 2408),
            ("FibonacciFanObject", new FibonacciFanObject(p1, p2), QueryTime, 1024),
            ("FibonacciExpansionObject", new FibonacciExpansionObject(p1, p2, p3), QueryTime, 1544),
            ("FibonacciChannelObject", new FibonacciChannelObject(p1, p2, p3), QueryTime, 1728),
            ("ParallelChannelObject", new ParallelChannelObject(p1, p2, p3), QueryTime, 696),
            ("GannFanObject", new GannFanObject(p1, p2), QueryTime, 2056),
            ("GannBoxObject", new GannBoxObject(p1, p2), QueryTime, 1992),
            ("ClassicPivotPointsObject", pivot, new DateTime(2026, 9, 22, 10, 0, 0), 1112),
        };

        foreach (var (name, provider, ts, baselineBytes) in targets)
        {
            try
            {
                // Warmup
                var warmupResult = provider.GetCalculatedValues(ts, 120m);
                Assert.IsType<DrawingCalculatedValue[]>(warmupResult);

                long bytes = AllocationProbe.InCollectionFreeWindow(
                    () => _ = provider.GetCalculatedValues(ts, 120m));

                _output.WriteLine($"VALUES_PROBE | {name} | Before={baselineBytes} | After={bytes}");
                Assert.True(bytes >= 0, $"Collection overlapped every sample for {name}");
                Assert.True(bytes < baselineBytes, $"{name} allocation ({bytes} B) should be less than baseline ({baselineBytes} B)");
            }
            finally
            {
                (provider as IDisposable)?.Dispose();
            }
        }
    }
}
