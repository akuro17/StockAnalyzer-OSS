using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace StockAnalyzer.Avalonia.Views.Controls;

/// <summary>
/// Shared <see cref="ICustomDrawOperation"/> plumbing for the zero-SKPath plot controls: it acquires
/// the SkiaSharp API lease and exposes the fixed contract (<see cref="Bounds"/>, hit test, dispose,
/// equality). Each concrete operation is an immutable snapshot constructed once per paint and is
/// never equal to another operation, so the compositor always repaints rather than reusing an
/// earlier operation's Skia pixels (mirrors <c>ChartDrawOperation</c>). Derived types supply only
/// <see cref="RenderCore"/>.
/// </summary>
internal abstract class SkiaLeaseDrawOperation : ICustomDrawOperation
{
    protected SkiaLeaseDrawOperation(Rect bounds) => Bounds = bounds;

    public Rect Bounds { get; }

    public void Dispose() { }

    // Rendering depends on the snapshot captured at construction. Letting the compositor reuse a
    // previous operation can retain Skia pixels from an earlier frame, so every paint is a fresh,
    // never-equal operation. Mirrors ChartDrawOperation.
    public bool Equals(ICustomDrawOperation? other) => false;

    public override bool Equals(object? obj) => Equals(obj as ICustomDrawOperation);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    public bool HitTest(global::Avalonia.Point p) => Bounds.Contains(p);

    public void Render(ImmediateDrawingContext context)
    {
        if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature feature)
        {
            return;
        }

        using ISkiaSharpApiLease lease = feature.Lease();
        RenderCore(lease.SkCanvas, Bounds);
    }

    /// <summary>Draws the operation's content onto the leased Skia canvas. Runs on the render thread.</summary>
    protected abstract void RenderCore(SKCanvas canvas, Rect bounds);
}
