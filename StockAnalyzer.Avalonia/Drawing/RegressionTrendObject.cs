using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services.Analysis;

namespace StockAnalyzer.Avalonia.Drawing;

public class RegressionTrendObject : IChartObject, IDisposable, IDrawingCalculatedValuesProvider
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
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
    public int PanelIndex { get; set; } = -1;
    public int ZIndex { get; set; } = 0;
    public int Priority { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;

    private const decimal DefaultStdDevMultiplier = 2.0m;
    private const double MinStdDevMultiplierRange = 0.1;
    private const double MaxStdDevMultiplierRange = 10.0;
    private const decimal MinStdDevMultiplier = (decimal)MinStdDevMultiplierRange;
    private const decimal MaxStdDevMultiplier = (decimal)MaxStdDevMultiplierRange;
    private const double MinExtendScreenDeltaX = 0.5;

    private decimal _stdDevMultiplier = DefaultStdDevMultiplier;

    [Category("Parameters")]
    [DisplayName("StdDev Multiplier")]
    [Range(MinStdDevMultiplierRange, MaxStdDevMultiplierRange)]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public decimal StdDevMultiplier
    {
        get => _stdDevMultiplier;
        set => _stdDevMultiplier = Math.Clamp(value, MinStdDevMultiplier, MaxStdDevMultiplier);
    }

    [Category("Parameters")]
    [DisplayName("Extend Right")]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public bool ExtendRight { get; set; } = false;

    // Regression State
    private RegressionService.RegressionResult _result;
    private DateTime _start;
    private DateTime _end;
    private DateTime[] _barTimes = Array.Empty<DateTime>();
    private DateTime[] _tailTimes = Array.Empty<DateTime>();

    // Reused across Render() calls (ZeroAllocation Render Loop, SA_RENDERING_PERFORMANCE.md
    // §1) instead of a `new SKPaint`/`new SKPath` per frame. Color-dependent properties are
    // refreshed from the current property values on each use since they can change between
    // renders; properties that never change (StrokeWidth/PathEffect/Style) are set once here.
    private readonly SKPaint _linePaint = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _bandPaint = new SKPaint
    {
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 1,
        IsAntialias = true,
        PathEffect = SKPathEffect.CreateDash(new float[] { 3, 3 }, 0)
    };
    private readonly SKPaint _fillPaint = new SKPaint { Style = SKPaintStyle.Fill };
    private readonly SKPath _fillPath = new SKPath();
    private readonly SKPath _extendPath = new SKPath();

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
        _extendPath.Dispose();
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
        int fastTailStartIndex = -1;
        int fastTotalCount = 0;
        List<DateTime>? slowTail = null;
        if (candles is IReadOnlyList<CoreCandleData> list)
        {
            int count = list.Count;
            fastTotalCount = count;
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
                    if (endIndex + 1 < count)
                    {
                        fastTailStartIndex = endIndex + 1;
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
                else if (c.Timestamp > end)
                {
                    slowTail ??= new List<DateTime>();
                    slowTail.Add(c.Timestamp);
                }
            }
            range.Sort(static (a, b) => a.Timestamp.CompareTo(b.Timestamp));
            slowTail?.Sort();
        }

        // 2. Calculate Regression
        var service = new RegressionService();
        _result = service.Calculate(range);

        // Bar times of the regression range, in the order of the fit's x (0 .. Count-1): GetCalculatedValues maps a time to its bar number with them.
        if (_result.IsValid)
        {
            if (_barTimes.Length != _result.Count) _barTimes = new DateTime[_result.Count];
            for (int i = 0; i < _barTimes.Length; i++) _barTimes[i] = range[i].Timestamp;

            if (candles is IReadOnlyList<CoreCandleData> tailList && fastTailStartIndex >= 0)
            {
                int tailCount = fastTotalCount - fastTailStartIndex;
                if (_tailTimes.Length != tailCount) _tailTimes = new DateTime[tailCount];
                for (int i = 0; i < tailCount; i++) _tailTimes[i] = tailList[fastTailStartIndex + i].Timestamp;
            }
            else if (slowTail != null && slowTail.Count > 0)
            {
                if (_tailTimes.Length != slowTail.Count) _tailTimes = new DateTime[slowTail.Count];
                for (int i = 0; i < slowTail.Count; i++) _tailTimes[i] = slowTail[i];
            }
            else
            {
                _tailTimes = Array.Empty<DateTime>();
            }
        }
        else
        {
            _tailTimes = Array.Empty<DateTime>();
        }

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
        decimal std = _result.StdDev * StdDevMultiplier;

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

        if (ExtendRight)
        {
            float clipRight = canvas.LocalClipBounds.Right;
            if (!float.IsFinite(clipRight) || clipRight <= 0f)
            {
                clipRight = (float)transform.CanvasWidth;
            }
            DrawExtendedLine(canvas, transform, _linePaint, screenEnd, 0m, clipRight);
            DrawExtendedLine(canvas, transform, _bandPaint, sEndUpper, std, clipRight);
            DrawExtendedLine(canvas, transform, _bandPaint, sEndLower, -std, clipRight);
        }

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

    private void DrawExtendedLine(
        SKCanvas canvas,
        ICoordinateTransform transform,
        SKPaint paint,
        global::Avalonia.Point rangeEnd,
        decimal bandOffset,
        float clipRight)
    {
        if (!double.IsFinite(rangeEnd.X) || !double.IsFinite(rangeEnd.Y)) return;

        _extendPath.Reset();
        _extendPath.MoveTo((float)rangeEnd.X, (float)rangeEnd.Y);

        global::Avalonia.Point prev = rangeEnd;
        global::Avalonia.Point last = rangeEnd;
        bool hasSegment = false;

        if (_tailTimes.Length == 0)
        {
            if (_barTimes.Length >= 2)
            {
                decimal prevPrice = _result.GetValueAt(_result.Count - 2) + bandOffset;
                prev = transform.ChartToScreen(new ChartPoint(_barTimes[_result.Count - 2], prevPrice));
            }
        }
        else
        {
            bool aborted = false;
            for (int i = 0; i < _tailTimes.Length; i++)
            {
                decimal extrapolated = _result.GetValueAt(_result.Count + i) + bandOffset;
                var pt = transform.ChartToScreen(new ChartPoint(_tailTimes[i], extrapolated));
                if (!double.IsFinite(pt.X) || !double.IsFinite(pt.Y))
                {
                    aborted = true;
                    break;
                }
                _extendPath.LineTo((float)pt.X, (float)pt.Y);
                prev = last;
                last = pt;
                hasSegment = true;
            }
            if (aborted)
            {
                if (hasSegment) canvas.DrawPath(_extendPath, paint);
                return;
            }
        }

        if (double.IsFinite(prev.X) && double.IsFinite(prev.Y) && double.IsFinite(last.X) && double.IsFinite(last.Y))
        {
            double dx = last.X - prev.X;
            if (dx >= MinExtendScreenDeltaX && clipRight > last.X)
            {
                double t = (clipRight - prev.X) / dx;
                double extY = prev.Y + t * (last.Y - prev.Y);
                if (double.IsFinite(extY))
                {
                    _extendPath.LineTo(clipRight, (float)extY);
                    hasSegment = true;
                }
            }
        }

        if (hasSegment)
        {
            canvas.DrawPath(_extendPath, paint);
        }
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
        decimal std = _result.StdDev * StdDevMultiplier;
        
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

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
    {
        if (!_result.IsValid) return Array.Empty<DrawingCalculatedValue>();

        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);

        int x = 0;
        if (_start != _end && _result.Count > 1)
        {
            if (timestamp >= _start && timestamp <= _end)
            {
                // Inside the range: the bar number is the position of the newest bar at or before the time (non-trading days do not shift it).
                int index = Array.BinarySearch(_barTimes, timestamp);
                if (index < 0) index = ~index - 1;
                x = Math.Clamp(index, 0, _result.Count - 1);
            }
            else if (ExtendRight && _tailTimes.Length > 0 && timestamp > _end && timestamp <= _tailTimes[^1])
            {
                // On trailing existing bars when ExtendRight is enabled: j = index + 1 (1-based after range end), so x = (n - 1) + j = n + index.
                int index = Array.BinarySearch(_tailTimes, timestamp);
                if (index < 0) index = ~index - 1;
                index = Math.Clamp(index, 0, _tailTimes.Length - 1);
                x = _result.Count + index;
            }
            else
            {
                double totalSec = (_end - _start).TotalSeconds;
                if (totalSec != 0)
                {
                    double curSec = (timestamp - _start).TotalSeconds;
                    x = (int)Math.Round((curSec / totalSec) * (_result.Count - 1));
                }
            }
        }

        decimal center = _result.GetValueAt(x);
        decimal std = _result.StdDev * StdDevMultiplier;
        decimal upper2 = center + std;
        decimal lower2 = center - std;

        return new DrawingCalculatedValue[]
        {
            new DrawingCalculatedValue("RegressionCenter", "Regression Center", center, $"{center:F3}", color),
            new DrawingCalculatedValue("RegressionUpper2", "+2σ Band", upper2, $"{upper2:F3}", color),
            new DrawingCalculatedValue("RegressionLower2", "-2σ Band", lower2, $"{lower2:F3}", color),
            new DrawingCalculatedValue("Slope", "Slope", _result.Slope, $"{_result.Slope:F4}", color),
            new DrawingCalculatedValue("StdDev", "StdDev", _result.StdDev, $"{_result.StdDev:F3}", IndicatorColor.Gray)
        };
    }
}

