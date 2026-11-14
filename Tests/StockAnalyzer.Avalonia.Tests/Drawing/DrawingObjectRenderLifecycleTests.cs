using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Tests.Common;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// Cached Skia resources of the line / channel drawing objects: (1) a changed colour, thickness, stroke style, fill flag or channel mode shows on
/// the next frame (no stale cache), (2) no managed allocation per frame, (3) disposing while another thread renders never touches freed
/// native objects (an AccessViolationException would end the test host).
/// </summary>
public sealed class DrawingObjectRenderLifecycleTests
{
    private const int Width = 800;
    private const int Height = 600;
    private const int StressIterations = 60;
    private const int StressFramesPerIteration = 400;

    private static ParallelChannelObject Channel(ChannelMode mode)
        => new(PixelCoordinateTransform.Chart(100, 100), PixelCoordinateTransform.Chart(300, 200), PixelCoordinateTransform.Chart(200, 300))
        {
            ChannelMode = mode,
            Color = Colors.White,
            Thickness = 2
        };

    private static DisjointChannelObject Disjoint()
        => new(PixelCoordinateTransform.Chart(100, 100), PixelCoordinateTransform.Chart(300, 100),
               PixelCoordinateTransform.Chart(100, 300), PixelCoordinateTransform.Chart(300, 200))
        {
            Color = Colors.White,
            Thickness = 2
        };

    private static byte[] Render(IChartObject obj)
    {
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        surface.Canvas.Clear(SKColors.Black);
        obj.Render(surface.Canvas, new PixelCoordinateTransform());
        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);
        return SHA256.HashData(bitmap.GetPixelSpan());   // a hash, not a 2 MB copy (LOH churn would trigger collections that disturb the allocation probes of other tests)
    }

    private static bool Differs(byte[] a, byte[] b) => !a.AsSpan().SequenceEqual(b);

    /// <summary>Applies the same property step to a long-lived (cached) object and to a brand-new one and requires identical pixels.</summary>
    private static void AssertCachedEqualsFresh<T>(Func<T> create, params Action<T>[] steps) where T : class, IChartObject
    {
        var cached = create();
        try
        {
            byte[] first = Render(cached);
            var applied = new List<Action<T>>();
            bool anyChange = false;
            foreach (Action<T> step in steps)
            {
                step(cached);
                applied.Add(step);

                var fresh = create();
                foreach (Action<T> a in applied) a(fresh);
                try
                {
                    byte[] expected = Render(fresh);
                    byte[] actual = Render(cached);
                    Assert.True(expected.AsSpan().SequenceEqual(actual), "A cached render differs from a fresh object after a property change.");
                    anyChange |= Differs(first, actual);
                }
                finally
                {
                    (fresh as IDisposable)?.Dispose();
                }
            }
            Assert.True(anyChange, "The property steps changed nothing on screen: the test would prove nothing.");
        }
        finally
        {
            (cached as IDisposable)?.Dispose();
        }
    }

    // ---------------- (1) no stale cache ----------------

    [Fact]
    public void ParallelChannel_PropertyChanges_AreVisibleOnTheNextFrame_InBothModes()
    {
        foreach (ChannelMode mode in Enum.GetValues<ChannelMode>())
        {
            AssertCachedEqualsFresh(() => Channel(mode),
                c => c.StrokeStyle = DrawingStrokeStyle.Dash,
                c => c.StrokeStyle = DrawingStrokeStyle.Dot,
                c => c.Color = Colors.Orange,
                c => c.Thickness = 5,
                c => c.IsFilled = false,
                c => c.StrokeStyle = DrawingStrokeStyle.Solid,
                c => c.ChannelMode = mode == ChannelMode.Parallel ? ChannelMode.FlatOpposite : ChannelMode.Parallel);
        }
    }

    [Fact]
    public void DisjointChannel_PropertyChanges_AreVisibleOnTheNextFrame()
    {
        AssertCachedEqualsFresh(Disjoint,
            c => c.StrokeStyle = DrawingStrokeStyle.Dash,
            c => c.StrokeStyle = DrawingStrokeStyle.Dot,
            c => c.Color = Colors.Orange,
            c => c.Thickness = 5,
            c => c.IsFilled = false,
            c => c.StrokeStyle = DrawingStrokeStyle.Solid);
    }

    [Fact]
    public void CrossLine_PropertyChanges_AreVisibleOnTheNextFrame()
    {
        AssertCachedEqualsFresh(() => new CrossLineObject(PixelCoordinateTransform.Chart(100, 200)) { Color = Colors.White, Thickness = 2 },
            c => c.StrokeStyle = DrawingStrokeStyle.Dash,
            c => c.Color = Colors.Orange,
            c => c.Thickness = 5,
            c => c.StrokeStyle = DrawingStrokeStyle.Solid);
    }

    // ---------------- (2) zero allocation ----------------

    [Theory]
    [InlineData(ChannelMode.Parallel, true, DrawingStrokeStyle.Solid)]
    [InlineData(ChannelMode.Parallel, false, DrawingStrokeStyle.Dash)]
    [InlineData(ChannelMode.FlatOpposite, true, DrawingStrokeStyle.Dot)]
    [InlineData(ChannelMode.FlatOpposite, false, DrawingStrokeStyle.Solid)]
    public void ParallelChannel_Render_AllocatesZeroManagedBytes(ChannelMode mode, bool filled, DrawingStrokeStyle style)
    {
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        var canvas = surface.Canvas;
        var transform = new PixelCoordinateTransform();
        var obj = Channel(mode);
        obj.IsFilled = filled;
        obj.StrokeStyle = style;
        try
        {
            canvas.Clear(SKColors.Black);
            obj.Render(canvas, transform);

            long bytes = AllocationProbe.InCollectionFreeWindow(() => obj.Render(canvas, transform), () => canvas.Clear(SKColors.Black));

            Assert.Equal(0, bytes);
        }
        finally
        {
            obj.Dispose();
        }
    }

    // ---------------- (3) dispose after / during render ----------------

    public static IEnumerable<object[]> DisposableFactories()
    {
        yield return new object[] { "ParallelChannel", (Func<IChartObject>)(() => Channel(ChannelMode.Parallel)) };
        yield return new object[] { "ParallelChannelFlat", (Func<IChartObject>)(() => Channel(ChannelMode.FlatOpposite)) };
        yield return new object[] { "DisjointChannel", (Func<IChartObject>)Disjoint };
        yield return new object[] { "CrossLine", (Func<IChartObject>)(() => new CrossLineObject(PixelCoordinateTransform.Chart(100, 200)) { Color = Colors.White }) };
        yield return new object[] { "HorizontalLine", (Func<IChartObject>)(() => new HorizontalLineObject(PixelCoordinateTransform.Chart(100, 200)) { Color = Colors.White }) };
        yield return new object[]
        {
            "TrendLineWithProjection",
            (Func<IChartObject>)(() => new TrendLineObject(PixelCoordinateTransform.Chart(100, 100), PixelCoordinateTransform.Chart(300, 200))
                { Color = Colors.White, ShowProjection = true, ExtendMode = LineExtendMode.Both })
        };
        yield return new object[]
        {
            "DatePriceRange",
            (Func<IChartObject>)(() => new DatePriceRangeObject(PixelCoordinateTransform.Chart(100, 200), PixelCoordinateTransform.Chart(260, 100))
                { Color = Colors.White })
        };
        yield return new object[]
        {
            "ElliottWave",
            (Func<IChartObject>)(() => new ElliottWaveObject(new[]
            {
                PixelCoordinateTransform.Chart(50, 200),
                PixelCoordinateTransform.Chart(110, 120),
                PixelCoordinateTransform.Chart(170, 170),
                PixelCoordinateTransform.Chart(230, 90),
                PixelCoordinateTransform.Chart(290, 140),
                PixelCoordinateTransform.Chart(350, 70)
            }) { Color = Colors.White })
        };
    }

    [Theory]
    [MemberData(nameof(DisposableFactories))]
    public void Render_AfterDispose_IsANoOp_AndDisposeIsIdempotent(string name, Func<IChartObject> create)
    {
        Assert.NotNull(name);
        var obj = create();
        ((IDisposable)obj).Dispose();
        ((IDisposable)obj).Dispose();

        byte[] pixels = Render(obj);

        Assert.False(Differs(pixels, EmptyFrame()));
    }

    private static byte[] EmptyFrame()
    {
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        surface.Canvas.Clear(SKColors.Black);
        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);
        return SHA256.HashData(bitmap.GetPixelSpan());   // a hash, not a 2 MB copy (LOH churn would trigger collections that disturb the allocation probes of other tests)
    }

    [Theory]
    [MemberData(nameof(DisposableFactories))]
    public async Task Dispose_WhileAnotherThreadRenders_NeverTouchesFreedNativeObjects(string name, Func<IChartObject> create)
    {
        Assert.NotNull(name);
        var transform = new PixelCoordinateTransform();

        for (int iteration = 0; iteration < StressIterations; iteration++)
        {
            var obj = create();
            using var go = new ManualResetEventSlim(false);
            Task renderer = Task.Run(() =>
            {
                using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
                go.Wait();
                for (int frame = 0; frame < StressFramesPerIteration; frame++)
                {
                    obj.Render(surface.Canvas, transform);
                }
            });

            go.Set();
            Thread.Sleep(iteration % 3);   // vary the moment of disposal inside the render burst
            ((IDisposable)obj).Dispose();
            await renderer;
        }
    }
}
