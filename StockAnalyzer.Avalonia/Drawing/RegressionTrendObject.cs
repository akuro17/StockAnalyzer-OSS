using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services.Analysis;

namespace StockAnalyzer.Avalonia.Drawing;

public class RegressionTrendObject : IChartObject, IDisposable
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.RegressionTrend;

    // Start and End define the Time Range. Price is ignored (calculated).
    // However, to support dragging handles, we store ChartPoint.
    // Price in Points[0]/[1] can be the regression value at that time?
    public List<ChartPoint> Points { get; private set; }

    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int ZIndex { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;

    // Regression State
    private RegressionService.RegressionResult _result;
    private DateTime _start;
    private DateTime _end;

    // Reused across Render() calls (ZeroAllocation Render Loop, SA_RENDERING_PERFORMANCE.md
    // §1) instead of a `new SKPaint`/`new SKPath` per frame. Color-dependent properties are
    // refreshed from the current property values on each use since they can change between
    // renders; properties that never change (StrokeWidth/PathEffect/Style) are set once here.
    private readonly SKPaint _linePaint = new SKPaint { IsAntialias = true };
    private readonly SKPaint _bandPaint = new SKPaint
    {
        StrokeWidth = 1,
        IsAntialias = true,
        PathEffect = SKPathEffect.CreateDash(new float[] { 3, 3 }, 0)
    };
    private readonly SKPaint _fillPaint = new SKPaint { Style = SKPaintStyle.Fill };
    private readonly SKPath _fillPath = new SKPath();

    public RegressionTrendObject(ChartPoint start, ChartPoint end)
    {
        Points = new List<ChartPoint> { start, end };
        _start = start.Time;
        _end = end.Time;
    }

    public void Dispose()
    {
        _linePaint.Dispose();
        _bandPaint.Dispose();
        _fillPaint.Dispose();
        _fillPath.Dispose();
        GC.SuppressFinalize(this);
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Recalculate(IEnumerable<CoreCandleData> candles)
    {
        // 1. Filter candles in range [Points[0].Time, Points[1].Time]
        var p1Time = Points[0].Time;
        var p2Time = Points[1].Time;

        var start = p1Time < p2Time ? p1Time : p2Time;
        var end = p1Time < p2Time ? p2Time : p1Time;

        _start = start;
        _end = end;

        // LINQ-free filtering: callers on high-frequency paths (ChartInteractionController.
        // HandleObjectDrag/UpdateNewShape, invoked per PointerMoved frame) pass the real
        // IReadOnlyList<CoreCandleData> snapshot, which takes an O(log N) binary-search
        // range lookup here -- mirroring RangeSplineObject.Recalculate()'s identical fast
        // path -- instead of the O(N) `.Where().OrderBy().ToList()` LINQ chain this used to
        // run on every such frame (SA_RENDERING_PERFORMANCE.md "LINQ in Hot Paths").
        var range = new List<CoreCandleData>();
        if (candles is IReadOnlyList<CoreCandleData> list)
        {
            int count = list.Count;
            if (count > 0)
            {
                // Binary search lower bound (first candle with Timestamp >= start)
                int low = 0, high = count - 1;
                int startIndex = -1;
                while (low <= high)
                {
                    int mid = low + ((high - low) >> 1);
                    if (list[mid].Timestamp >= start)
                    {
                        startIndex = mid;
                        high = mid - 1;
                    }
                    else
                    {
                        low = mid + 1;
                    }
                }

                // Binary search upper bound (last candle with Timestamp <= end)
                low = 0; high = count - 1;
                int endIndex = -1;
                while (low <= high)
                {
                    int mid = low + ((high - low) >> 1);
                    if (list[mid].Timestamp <= end)
                    {
                        endIndex = mid;
                        low = mid + 1;
                    }
                    else
                    {
                        high = mid - 1;
                    }
                }

                if (startIndex != -1 && endIndex != -1 && startIndex <= endIndex)
                {
                    for (int i = startIndex; i <= endIndex; i++)
                    {
                        range.Add(list[i]);
                    }
                }
            }
        }
        else
        {
            // Slow path (e.g. the LINQ .Select()-wrapped IEnumerable passed once per drag
            // at HandlePointerReleased, not a hot path): preserve the original semantics of
            // filtering plus an explicit chronological sort, without LINQ syntax.
            foreach (var c in candles)
            {
                if (c.Timestamp >= start && c.Timestamp <= end)
                {
                    range.Add(c);
                }
            }
            range.Sort(static (a, b) => a.Timestamp.CompareTo(b.Timestamp));
        }

        // 2. Calculate Regression
        var service = new RegressionService();
        _result = service.Calculate(range);

        // 3. Sync Points[] to the rendered handle positions (regression-fitted price at
        // _start/_end). Render()/HitTest() draw and hit-test handles at these computed
        // positions, not at the user's raw click price, so Points[] must match them or
        // the interaction controller's generic Points[]-based handle hit-test (used to
        // start a handle drag) never lines up with the visible handle circle. Each
        // point keeps its own chronological identity (earlier vs. later time) so a
        // handle already being dragged doesn't jump to the other end mid-drag.
        if (_result.IsValid)
        {
            decimal yAnalysisStart = _result.GetValueAt(0);
            decimal yAnalysisEnd = _result.GetValueAt(_result.Count - 1);
            if (Points[0].Time <= Points[1].Time)
            {
                Points[0] = new ChartPoint(_start, yAnalysisStart);
                Points[1] = new ChartPoint(_end, yAnalysisEnd);
            }
            else
            {
                Points[0] = new ChartPoint(_end, yAnalysisEnd);
                Points[1] = new ChartPoint(_start, yAnalysisStart);
            }
        }
    }

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        // "Stale" means Points[] has moved since _result/_start/_end were last computed
        // by Recalculate() -- e.g. mid-drag, where ChartInteractionController.HandleObjectDrag
        // intentionally defers Recalculate() to HandlePointerReleased for performance
        // (avoids per-PointerMoved-frame regression recompute). Without this check the
        // rendered line/handles -- which are derived from _start/_end/_result, not from
        // Points[] directly -- would stay frozen at their pre-drag position and only
        // jump to the new one once the mouse is released.
        var p1Time = Points[0].Time;
        var p2Time = Points[1].Time;
        var currentStart = p1Time < p2Time ? p1Time : p2Time;
        var currentEnd = p1Time < p2Time ? p2Time : p1Time;
        bool isStale = currentStart != _start || currentEnd != _end;

        if (!_result.IsValid || _result.Count < 2 || isStale)
        {
            // Not enough candles in the [Points[0].Time, Points[1].Time] range yet to
            // compute a regression fit (e.g. right after the first click), or the fit is
            // stale (see above). Draw a lightweight raw-point preview directly from
            // Points[] so the line/handles visibly follow the cursor instead of either
            // showing nothing or freezing until release.
            var previewStart = transform.ChartToScreen(Points[0]);
            var previewEnd = transform.ChartToScreen(Points[1]);
            _linePaint.Color = SkiaColor;
            _linePaint.StrokeWidth = (float)Thickness;
            canvas.DrawLine((float)previewStart.X, (float)previewStart.Y, (float)previewEnd.X, (float)previewEnd.Y, _linePaint);
            SelectionHandleRenderer.Draw(canvas, previewStart, radius: ChartConstants.SelectedHandleRadius);
            SelectionHandleRenderer.Draw(canvas, previewEnd, radius: ChartConstants.SelectedHandleRadius);
            return;
        }

        // Render 3 Lines: Center, +2SD, -2SD
        // Start and End X in Screen Coordinates
        // To draw correctly we need the Screen Points corresponding to Index 0 and Index (Count-1) of the REGRESSION range.
        
        // Problem: _result is 0-based index relative to the RANGE.
        // We need to map Range Start Time to Screen.
        
        var startPt = new ChartPoint(_start, 0); // Price 0 placeholder
        var endPt = new ChartPoint(_end, 0);

        var s1 = transform.ChartToScreen(startPt);
        var s2 = transform.ChartToScreen(endPt);

        // Calculate Y in Screen Space?
        // NO. Regression is in Price (Y) vs Index (X).
        // Regression Y = mx + c.
        // Value At Start (x=0) = Intercept.
        // Value At End (x=Count-1) = Slope*(Count-1) + Intercept.
        
        decimal yAnalysisStart = _result.GetValueAt(0);
        decimal yAnalysisEnd = _result.GetValueAt(_result.Count - 1);

        // Convert these Prices to Screen Y
        var pStart = new ChartPoint(_start, yAnalysisStart);
        var pEnd = new ChartPoint(_end, yAnalysisEnd);
        
        var screenStart = transform.ChartToScreen(pStart);
        var screenEnd = transform.ChartToScreen(pEnd);
        
        // Adjust Screen X. s1.X should match screenStart.X
        
        // Center Line
        _linePaint.Color = SkiaColor;
        _linePaint.StrokeWidth = (float)Thickness;
        canvas.DrawLine((float)screenStart.X, (float)screenStart.Y, (float)screenEnd.X, (float)screenEnd.Y, _linePaint);

        // StdDev Bands
        decimal std = _result.StdDev * 2;

        var pStartUpper = new ChartPoint(_start, yAnalysisStart + std);
        var pEndUpper = new ChartPoint(_end, yAnalysisEnd + std);
        var sStartUpper = transform.ChartToScreen(pStartUpper);
        var sEndUpper = transform.ChartToScreen(pEndUpper);

        var pStartLower = new ChartPoint(_start, yAnalysisStart - std);
        var pEndLower = new ChartPoint(_end, yAnalysisEnd - std);
        var sStartLower = transform.ChartToScreen(pStartLower);
        var sEndLower = transform.ChartToScreen(pEndLower);

        _bandPaint.Color = SkiaColor;

        canvas.DrawLine((float)sStartUpper.X, (float)sStartUpper.Y, (float)sEndUpper.X, (float)sEndUpper.Y, _bandPaint);
        canvas.DrawLine((float)sStartLower.X, (float)sStartLower.Y, (float)sEndLower.X, (float)sEndLower.Y, _bandPaint);

        // Fill
        _fillPath.Rewind();
        _fillPath.MoveTo((float)sStartUpper.X, (float)sStartUpper.Y);
        _fillPath.LineTo((float)sEndUpper.X, (float)sEndUpper.Y);
        _fillPath.LineTo((float)sEndLower.X, (float)sEndLower.Y);
        _fillPath.LineTo((float)sStartLower.X, (float)sStartLower.Y);
        _fillPath.Close();

        _fillPaint.Color = new SKColor(SkiaColor.Red, SkiaColor.Green, SkiaColor.Blue, 30);
        canvas.DrawPath(_fillPath, _fillPaint);

        // Control-point handles are always shown (not gated by IsSelected), matching the
        // "not enough data yet" fallback above: while placing point 2 during two-click
        // creation the object is never IsSelected (that only becomes true once it joins
        // ChartObjectManager on finish), so gating here made the handles vanish the
        // moment enough candles existed to compute a valid fit -- only the line/bands
        // stayed visible. The Start/End range IS the object's defining feature, so
        // showing it persistently (like other always-visible analysis boundaries) is
        // intentional rather than a plain selection affordance.
        SelectionHandleRenderer.Draw(canvas, screenStart, radius: ChartConstants.SelectedHandleRadius);
        SelectionHandleRenderer.Draw(canvas, screenEnd, radius: ChartConstants.SelectedHandleRadius);
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (!_result.IsValid) return false;
        
        // Similar bounding box + path contains logic
         // For simplicity, just check distance to Center Line?
         // Or check if inside Band?
         
         // We can reconstruct the polygon locally and test
        decimal yAnalysisStart = _result.Slope * 0 + _result.Intercept;
        decimal yAnalysisEnd = _result.Slope * (_result.Count - 1) + _result.Intercept;
        decimal std = _result.StdDev * 2;
        
        var pStartUpper = new ChartPoint(_start, yAnalysisStart + std);
        var pEndUpper = new ChartPoint(_end, yAnalysisEnd + std);
        var pStartLower = new ChartPoint(_start, yAnalysisStart - std);
        var pEndLower = new ChartPoint(_end, yAnalysisEnd - std);

        var s1 = transform.ChartToScreen(pStartUpper);
        var s2 = transform.ChartToScreen(pEndUpper);
        var s3 = transform.ChartToScreen(pEndLower);
        var s4 = transform.ChartToScreen(pStartLower);
        
        using var path = new SKPath();
        path.MoveTo((float)s1.X, (float)s1.Y);
        path.LineTo((float)s2.X, (float)s2.Y);
        path.LineTo((float)s3.X, (float)s3.Y);
        path.LineTo((float)s4.X, (float)s4.Y);
        path.Close();
        
        return path.Contains((float)screenPoint.X, (float)screenPoint.Y);
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        // Translate start/end times
        // Ignore priceDelta as auto-calculated
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price);
        }
        // NOTE: Must trigger Recalculate externally after translate!
    }
}

