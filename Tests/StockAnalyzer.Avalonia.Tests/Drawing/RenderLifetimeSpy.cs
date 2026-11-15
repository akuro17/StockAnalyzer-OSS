using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>Wraps an optional real object; records dispose/render order; a render after the dispose is recorded, never forwarded.</summary>
internal sealed class RenderLifetimeSpy : IChartObject, IDisposable
{
    private readonly IChartObject? _inner;
    private readonly Action? _onRender;
    private readonly Action? _onDispose;
    private int _disposeCount;
    private int _renderedAfterDispose;
    private int _renderCount;

    public RenderLifetimeSpy(IChartObject? inner = null, Action? onRender = null, Action? onDispose = null)
    {
        _inner = inner;
        _onRender = onRender;
        _onDispose = onDispose;
        Id = inner?.Id ?? Guid.NewGuid();
    }

    public int DisposeCount => Volatile.Read(ref _disposeCount);
    public bool RenderedAfterDispose => Volatile.Read(ref _renderedAfterDispose) != 0;
    public int RenderCount => Volatile.Read(ref _renderCount);

    public Guid Id { get; }
    public ChartObjectType Type => _inner?.Type ?? default;
    public string? CustomName { get; set; }
    public List<ChartPoint> Points { get; } = new();
    public Color Color { get; set; } = Colors.White;
    public double Thickness { get; set; } = 1;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; }
    public int ZIndex { get; set; }
    public int Priority { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; }
    public bool IsMoveAxisModeExplicit { get; set; }
    public int AnchorPointIndex { get; set; }
    public SKColor SkiaColor => SKColors.White;

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (DisposeCount > 0)
        {
            Interlocked.Exchange(ref _renderedAfterDispose, 1);
            return;
        }

        Interlocked.Increment(ref _renderCount);
        _onRender?.Invoke();
        try
        {
            _inner?.Render(canvas, transform);
        }
        catch (Exception)
        {
            // A placeholder object built by ChartObjectInstanceFactory may legitimately reject degenerate geometry; only the object lifetime is under test.
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = StockAnalyzer.Avalonia.Common.ChartConstants.DefaultHitTestTolerance) => false;

    public void Translate(TimeSpan timeDelta, decimal priceDelta) { }

    public void Dispose()
    {
        Interlocked.Increment(ref _disposeCount);
        _onDispose?.Invoke();
        (_inner as IDisposable)?.Dispose();
    }
}

/// <summary>The four ways a <see cref="ChartObjectManager"/> retires objects (every one of them disposes what it removes), shared by the render-scope tests.</summary>
internal static class RetirementPaths
{
    public static IEnumerable<object[]> All()
    {
        yield return new object[] { "RemoveObject" };
        yield return new object[] { "DeleteAll" };
        yield return new object[] { "Clear" };
        yield return new object[] { "LoadSnapshot" };
    }

    public static void Retire(ChartObjectManager manager, string path, RenderLifetimeSpy target)
    {
        switch (path)
        {
            case "RemoveObject": Assert.True(manager.RemoveObject(target.Id)); break;
            case "DeleteAll": manager.DeleteAll(); break;
            case "Clear": manager.Clear(); break;
            case "LoadSnapshot": manager.LoadSnapshot(new Dictionary<ChartDrawingContextType, List<IChartObject>> { [ChartDrawingContextType.Standard] = new() }); break;
            default: throw new ArgumentOutOfRangeException(nameof(path), path, null);
        }
    }
}
