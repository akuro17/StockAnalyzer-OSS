using System;
using System.Collections.Generic;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// The common part of the range-analysis drawing tools (see <see cref="IRangeAnalysisDrawing"/>): the chart-object properties, the two vertical range edges with a faint band
/// between them, the selection handles, the disposed-render guard, the cache keyed by the anchor times, and the recalculation entry. A tool supplies the calculation
/// (<see cref="ComputeRange"/>), the result drawing (<see cref="RenderResult"/>) and the Data-tab rows (<see cref="BuildValues"/>).
/// </summary>
/// <remarks>
/// Persistence: both persistence paths (<c>ChartObjectJsonConverter</c> and <c>DrawingDocumentCodec</c>) read every public settable property, so a subclass keeps its computed state in
/// private or protected fields and exposes only the user parameters as public settable properties.
/// </remarks>
public abstract class RangeAnalysisObjectBase : IChartObject, IDisposable, IDrawingCalculatedValuesProvider, IRangeAnalysisDrawing
{
    /// <summary>Alpha of the band between the two range edges.</summary>
    private const byte RangeBandAlpha = 16;

    /// <summary>Alpha of the edge lines.</summary>
    private const byte EdgeAlpha = 200;

    /// <summary>Distance of the overlay text from the range edge and from the top of the plot, in pixels.</summary>
    private const float OverlayTextPadding = 6f;

    private readonly NativeResourceGate _gate = new();
    private readonly SKPaint _bandPaint = new() { Style = SKPaintStyle.Fill };
    private readonly SKPaint _edgePaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _textPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };

    private bool _isComputed;
    private DateTime _computedStart;
    private DateTime _computedEnd;

    public string? CustomName { get; set; }

    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;

    public bool IsMoveAxisModeExplicit { get; set; }

    public Guid Id { get; } = Guid.NewGuid();

    public abstract ChartObjectType Type { get; }

    public List<ChartPoint> Points { get; } = new(2);

    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;

    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;

    public bool IsSelected { get; set; }

    public bool IsVisible { get; set; } = true;

    public bool IsLocked { get; set; }

    public int PanelIndex { get; set; } = -1;

    public int ZIndex { get; set; }

    public int Priority { get; set; }

    public int AnchorPointIndex { get; set; }

    public SKColor SkiaColor => new(Color.R, Color.G, Color.B, Color.A);

    /// <summary>The lock shared by the render path and <see cref="Dispose"/>.</summary>
    protected object SyncRoot => _gate.SyncRoot;

    /// <summary>True once the object was disposed (checked inside <see cref="SyncRoot"/> by the callers that draw).</summary>
    protected bool IsDisposed => _gate.IsDisposed;

    /// <summary>True when the result belongs to the current anchor times (a drag changes the points before the recalculation at its end).</summary>
    protected bool HasCurrentResult => _isComputed && !IsStale;

    /// <summary>The text drawn at the top of the range (a state or a truncation badge); null or empty draws nothing. Built when the result is computed, never in <see cref="RenderResult"/>.</summary>
    protected abstract string? OverlayText { get; }

    private bool IsStale
    {
        get
        {
            if (Points.Count < 2)
            {
                return true;
            }

            DateTime t1 = Points[0].Time;
            DateTime t2 = Points[1].Time;
            DateTime start = t1 < t2 ? t1 : t2;
            DateTime end = t1 < t2 ? t2 : t1;
            return start != _computedStart || end != _computedEnd;
        }
    }

    /// <summary>Recalculates the result for the current anchors from <paramref name="candles"/> (oldest first).</summary>
    public void Recalculate(IReadOnlyList<CoreCandleData> candles)
    {
        lock (_gate.SyncRoot)
        {
            if (_gate.IsDisposed)
            {
                return;
            }

            if (candles == null || Points.Count < 2)
            {
                ClearResult();
                _isComputed = false;
                return;
            }

            DateTime t1 = Points[0].Time;
            DateTime t2 = Points[1].Time;
            _computedStart = t1 < t2 ? t1 : t2;
            _computedEnd = t1 < t2 ? t2 : t1;

            if (DrawingRangeBars.TryFind(candles, _computedStart, _computedEnd, out int first, out int last))
            {
                ComputeRange(candles, first, last);
            }
            else
            {
                ClearResult();
            }

            _isComputed = true;
        }
    }

    /// <summary>Calculates and caches the result for the bars <paramref name="firstIndex"/> .. <paramref name="lastIndex"/> (inside <see cref="SyncRoot"/>). Not called for a range without a bar.</summary>
    protected abstract void ComputeRange(IReadOnlyList<CoreCandleData> candles, int firstIndex, int lastIndex);

    /// <summary>Drops the cached result (inside <see cref="SyncRoot"/>).</summary>
    protected abstract void ClearResult();

    /// <summary>Draws the result (inside <see cref="SyncRoot"/>, only when <see cref="HasCurrentResult"/>). No allocation.</summary>
    protected abstract void RenderResult(SKCanvas canvas, ICoordinateTransform transform, float leftX, float rightX, SKColor color);

    /// <summary>The Data-tab rows of the cached result (inside <see cref="SyncRoot"/>, only when <see cref="HasCurrentResult"/>).</summary>
    protected abstract IReadOnlyList<DrawingCalculatedValue> BuildValues(IndicatorColor color);

    /// <summary>Disposes the native resources of the subclass (inside <see cref="SyncRoot"/>, once).</summary>
    protected virtual void DisposeNativeResources()
    {
    }

    /// <summary>True when the screen point hits the drawn result (beside the two range edges, which the base always tests). No native object is created.</summary>
    protected virtual bool HitTestResult(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance) => false;

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        lock (_gate.SyncRoot)
        {
            if (_gate.IsDisposed || !IsVisible || canvas == null || transform == null || Points.Count < 2)
            {
                return;
            }

            global::Avalonia.Point p1 = transform.ChartToScreen(Points[0]);
            global::Avalonia.Point p2 = transform.ChartToScreen(Points[1]);
            if (!double.IsFinite(p1.X) || !double.IsFinite(p2.X))
            {
                return;
            }

            float x1 = (float)p1.X;
            float x2 = (float)p2.X;
            float left = MathF.Min(x1, x2);
            float right = MathF.Max(x1, x2);
            SKRect clip = canvas.LocalClipBounds;
            SKColor color = SkiaColor;

            _bandPaint.Color = color.WithAlpha(RangeBandAlpha);
            canvas.DrawRect(new SKRect(left, clip.Top, right, clip.Bottom), _bandPaint);

            _edgePaint.Color = color.WithAlpha(EdgeAlpha);
            _edgePaint.StrokeWidth = (float)Thickness;
            canvas.DrawLine(x1, clip.Top, x1, clip.Bottom, _edgePaint);
            canvas.DrawLine(x2, clip.Top, x2, clip.Bottom, _edgePaint);

            bool current = HasCurrentResult;
            if (current)
            {
                RenderResult(canvas, transform, left, right, color);
            }

            string? overlay = current ? OverlayText : null;
            if (!string.IsNullOrEmpty(overlay))
            {
                _textPaint.Color = DrawingThemeContext.MainTextSkColor;
                _textPaint.TextSize = DrawingThemeContext.DetailFontSize > 0f ? DrawingThemeContext.DetailFontSize : DrawingThemeContext.DrawingFontSize;
                _textPaint.Typeface = DrawingThemeContext.DefaultTextTypeface;
                canvas.DrawText(overlay, left + OverlayTextPadding, clip.Top + OverlayTextPadding + _textPaint.TextSize, _textPaint);
            }

            if (IsSelected)
            {
                float midY = clip.MidY;
                SelectionHandleRenderer.Draw(canvas, new global::Avalonia.Point(x1, midY), AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null, radius: ChartConstants.SelectedHandleRadius);
                SelectionHandleRenderer.Draw(canvas, new global::Avalonia.Point(x2, midY), AnchorPointIndex == 1 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null, radius: ChartConstants.SelectedHandleRadius);
            }
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (_gate.IsDisposed || !IsVisible || transform == null || Points.Count < 2)
        {
            return false;
        }

        global::Avalonia.Point p1 = transform.ChartToScreen(Points[0]);
        global::Avalonia.Point p2 = transform.ChartToScreen(Points[1]);
        if (Math.Abs(screenPoint.X - p1.X) <= tolerance || Math.Abs(screenPoint.X - p2.X) <= tolerance)
        {
            return true;
        }

        lock (_gate.SyncRoot)
        {
            return !_gate.IsDisposed && HasCurrentResult && HitTestResult(screenPoint, transform, tolerance);
        }
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            ChartPoint p = Points[i];
            Points[i] = new ChartPoint(p.Time.Add(timeDelta), p.Price + priceDelta);
        }

        // The result is recalculated when the move has finished (DrawingRecalculation.AfterMoveCompleted); until then the object draws the range edges only.
    }

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
    {
        lock (_gate.SyncRoot)
        {
            if (_gate.IsDisposed || Points.Count < 2 || !HasCurrentResult)
            {
                return Array.Empty<DrawingCalculatedValue>();
            }

            return BuildValues(new IndicatorColor(Color.A, Color.R, Color.G, Color.B));
        }
    }

    public void Dispose()
    {
        lock (_gate.SyncRoot)
        {
            if (!_gate.TryMarkDisposed())
            {
                return;
            }

            _bandPaint.Dispose();
            _edgePaint.Dispose();
            _textPaint.Dispose();
            DisposeNativeResources();
        }

        GC.SuppressFinalize(this);
    }
}
