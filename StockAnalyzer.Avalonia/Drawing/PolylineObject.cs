using System;
using System.Buffers;
using System.Collections.Generic;
using global::Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

public enum PolylineLabelType { None, Numeric, Alphabet }

/// <summary>
/// Polyline and Smooth Spline object implementation (connecting N points).
/// Inherits RelativeGeometricRenderer for Zero-Allocation rendering and geometric precision.
/// </summary>
public class PolylineObject : RelativeGeometricRenderer
{
    public override ChartObjectType Type => ChartObjectType.Polyline;

    // Labeling properties (Elliott Wave support)
    public PolylineLabelType LabelType { get; set; } = PolylineLabelType.None;
    public bool ShowLabels { get; set; } = true;
    public double FontSize { get; set; } = DrawingThemeContext.DrawingFontSize;

    // Smooth Line (Cubic Bézier / Catmull-Rom) properties
    public bool IsSmooth { get; set; } = false;
    public double Tension { get; set; } = BezierSplineMath.DefaultTension;

    private readonly SKPaint _cachedTextPaint;

    public PolylineObject()
    {
        Color = DrawingThemeContext.DefaultColor;
        Thickness = DrawingThemeContext.DefaultStrokeThickness;
        _cachedTextPaint = new SKPaint
        {
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold)
        };
    }

    public PolylineObject(IEnumerable<ChartPoint> points) : this()
    {
        if (points != null)
        {
            Points.AddRange(points);
        }
    }

    public PolylineObject(List<ChartPoint> points) : this((IEnumerable<ChartPoint>)points)
    {
    }

    public void AddPoint(ChartPoint point)
    {
        Points.Add(point);
    }

    protected override void DrawGeometry(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points.Count < 2) return;

        int count = Points.Count;
        SKPoint[]? rented = null;
        Span<SKPoint> screenPoints = count <= 128
            ? stackalloc SKPoint[count]
            : (rented = ArrayPool<SKPoint>.Shared.Rent(count)).AsSpan(0, count);

        try
        {
            for (int i = 0; i < count; i++)
            {
                var pt = transform.ChartToScreen(Points[i]);
                screenPoints[i] = new SKPoint((float)pt.X, (float)pt.Y);
            }

            _cachedPaint.StrokeJoin = SKStrokeJoin.Round;

            if (IsSmooth && count >= 3)
            {
                BezierSplineMath.BuildCatmullRomSplinePath(_cachedPath, screenPoints, Tension);
            }
            else
            {
                _cachedPath.MoveTo(screenPoints[0]);
                for (int i = 1; i < count; i++)
                {
                    _cachedPath.LineTo(screenPoints[i]);
                }
            }

            canvas.DrawPath(_cachedPath, _cachedPaint);

            // Render Labels
            if (ShowLabels && LabelType != PolylineLabelType.None)
            {
                _cachedTextPaint.Color = SkiaColor;
                _cachedTextPaint.TextSize = (float)FontSize;

                for (int i = 0; i < count; i++)
                {
                    string label = GetLabel(i);
                    if (!string.IsNullOrEmpty(label))
                    {
                        float offY = (i % 2 == 0) ? ChartConstants.PolylineLabelOffsetYEven : ChartConstants.PolylineLabelOffsetYOdd;
                        canvas.DrawText(label, screenPoints[i].X - 4f, screenPoints[i].Y + offY, _cachedTextPaint);
                    }
                }
            }
        }
        finally
        {
            if (rented != null)
            {
                ArrayPool<SKPoint>.Shared.Return(rented);
            }
        }
    }

    public override bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (transform == null || Points.Count < 2) return false;

        int count = Points.Count;
        SKPoint skScreenPt = new SKPoint((float)screenPoint.X, (float)screenPoint.Y);

        SKPoint[]? rented = null;
        Span<SKPoint> screenPoints = count <= 128
            ? stackalloc SKPoint[count]
            : (rented = ArrayPool<SKPoint>.Shared.Rent(count)).AsSpan(0, count);

        try
        {
            for (int i = 0; i < count; i++)
            {
                var p = transform.ChartToScreen(Points[i]);
                screenPoints[i] = new SKPoint((float)p.X, (float)p.Y);
            }

            if (IsSmooth && count >= 3)
            {
                for (int i = 0; i < count - 1; i++)
                {
                    SKPoint pCurr = screenPoints[i];
                    SKPoint pNext = screenPoints[i + 1];

                    if (BezierSplineMath.DistanceSquared(pCurr, pNext) < BezierSplineMath.EpsilonSquared)
                        continue;

                    SKPoint pPrev = (i == 0)
                        ? new SKPoint(2f * screenPoints[0].X - screenPoints[1].X, 2f * screenPoints[0].Y - screenPoints[1].Y)
                        : screenPoints[i - 1];

                    SKPoint pNextNext = (i == count - 2)
                        ? new SKPoint(2f * screenPoints[count - 1].X - screenPoints[count - 2].X, 2f * screenPoints[count - 1].Y - screenPoints[count - 2].Y)
                        : screenPoints[i + 2];

                    BezierSplineMath.CalculateControlPoints(pPrev, pCurr, pNext, pNextNext, Tension, out var c1, out var c2);
                    if (BezierSplineMath.HitTestCubicSegment(skScreenPt, pCurr, c1, c2, pNext, tolerance))
                        return true;
                }
                return false;
            }
            else
            {
                for (int i = 0; i < count - 1; i++)
                {
                    if (BezierSplineMath.DistancePointToSegment(skScreenPt, screenPoints[i], screenPoints[i + 1]) <= tolerance)
                        return true;
                }
                return false;
            }
        }
        finally
        {
            if (rented != null)
            {
                ArrayPool<SKPoint>.Shared.Return(rented);
            }
        }
    }

    public string GetLabel(int index)
    {
        if (index == 0 && LabelType != PolylineLabelType.None) return ""; // Start point usually has no label in wave counts

        if (LabelType == PolylineLabelType.Numeric)
        {
            return index.ToString();
        }
        else if (LabelType == PolylineLabelType.Alphabet)
        {
            int charIndex = index - 1;
            if (charIndex >= 0 && charIndex < 26)
            {
                return ((char)('A' + charIndex)).ToString();
            }
            return "?";
        }
        return "";
    }

    public override void Dispose()
    {
        _cachedTextPaint?.Dispose();
        base.Dispose();
    }
}
