using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Rectangle object implementation (defined by 2 points) supporting fills, gradients, and blend modes.
/// ZeroAllocation enabled via RelativeGeometricRenderer base class.
/// </summary>
public class RectangleObject : RelativeGeometricRenderer, IFillableChartObject
{
    private const float GeometryEpsilon = 1e-4f;

    public override ChartObjectType Type => ChartObjectType.Rectangle;
    
    // Properties for Fill, Gradient, and Blending
    public bool IsFilled { get; set; } = true;
    public DrawingBlendMode BlendMode { get; set; } = DrawingBlendMode.Normal;
    public DrawingGradientType GradientType { get; set; } = DrawingGradientType.None;
    public Color? GradientEndColor { get; set; } = null;
    public byte FillAlpha { get; set; } = 30;
    public byte GradientEndAlpha { get; set; } = 30;

    private readonly SKPaint _fillPaint;

    public RectangleObject(ChartPoint p1, ChartPoint p2) : base()
    {
        Points.Add(p1);
        Points.Add(p2);

        _fillPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
    }

    /// <summary>
    /// Overrides the base selection-handle loop (which only draws the 2 real stored
    /// <see cref="RelativeGeometricRenderer.Points"/>) to draw all 4 visually-dragged corners —
    /// the other 2 are synthesized from the 2 real points, matching the corner math already used
    /// for drag hit-testing in <see cref="Views.Chart.ChartInteractionController"/>.
    /// </summary>
    public override void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (!IsVisible) return;

        _cachedPaint.Color = SkiaColor;
        _cachedPaint.StrokeWidth = (float)Thickness;
        _cachedPath.Reset();

        DrawGeometry(canvas, transform);

        if (IsSelected && Points.Count >= 2)
        {
            var p1 = transform.ChartToScreen(Points[0]);
            var p2 = transform.ChartToScreen(Points[1]);
            var corners = new global::Avalonia.Point[]
            {
                new global::Avalonia.Point(p1.X, p1.Y),
                new global::Avalonia.Point(p2.X, p1.Y),
                new global::Avalonia.Point(p2.X, p2.Y),
                new global::Avalonia.Point(p1.X, p2.Y)
            };

            for (int i = 0; i < corners.Length; i++)
            {
                bool isAnchor = i == AnchorPointIndex;
                SelectionHandleRenderer.Draw(canvas, corners[i], isAnchor ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
            }
        }
    }

    protected override void DrawGeometry(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (Points.Count < 2) return;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        if (float.IsNaN((float)p1.X) || float.IsNaN((float)p1.Y) ||
            float.IsNaN((float)p2.X) || float.IsNaN((float)p2.Y) ||
            float.IsInfinity((float)p1.X) || float.IsInfinity((float)p1.Y) ||
            float.IsInfinity((float)p2.X) || float.IsInfinity((float)p2.Y)) return;

        var left = (float)Math.Min(p1.X, p2.X);
        var top = (float)Math.Min(p1.Y, p2.Y);
        var right = (float)Math.Max(p1.X, p2.X);
        var bottom = (float)Math.Max(p1.Y, p2.Y);
        var rect = new SKRect(left, top, right, bottom);

        bool isDegenerate = rect.Width <= GeometryEpsilon || rect.Height <= GeometryEpsilon;

        // [Layer 1: Fill Layer]
        if (IsFilled && !isDegenerate)
        {
            _fillPaint.BlendMode = BlendMode.ToSkBlendMode();

            if (GradientType == DrawingGradientType.None)
            {
                _fillPaint.Shader = null;
                _fillPaint.Color = new SKColor(Color.R, Color.G, Color.B, FillAlpha);
                canvas.DrawRect(rect, _fillPaint);
            }
            else
            {
                using var shader = DrawingShaderFactory.CreateShader(this, rect, (float)p1.X, (float)p1.Y, (float)p2.X, (float)p2.Y);
                if (shader != null)
                {
                    _fillPaint.Shader = shader;
                    canvas.DrawRect(rect, _fillPaint);
                }
                else
                {
                    _fillPaint.Shader = null;
                    _fillPaint.Color = new SKColor(Color.R, Color.G, Color.B, FillAlpha);
                    canvas.DrawRect(rect, _fillPaint);
                }
            }
        }

        // [Layer 2: Stroke Layer]
        if (Thickness > 0)
        {
            _cachedPaint.Style = SKPaintStyle.Stroke;
            _cachedPaint.BlendMode = SKBlendMode.SrcOver;
            canvas.DrawRect(rect, _cachedPaint);
        }
    }

    public override bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 2) return false;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);
        
        double left = Math.Min(p1.X, p2.X);
        double top = Math.Min(p1.Y, p2.Y);
        double right = Math.Max(p1.X, p2.X);
        double bottom = Math.Max(p1.Y, p2.Y);
        var rect = new global::Avalonia.Rect(new global::Avalonia.Point(left, top), new global::Avalonia.Size(right - left, bottom - top));

        if (IsFilled)
        {
            if (rect.Contains(screenPoint) || rect.Inflate(tolerance).Contains(screenPoint))
                return true;
        }

        // Check border segments (for outline or near border)
        var tl = new global::Avalonia.Point(left, top);
        var tr = new global::Avalonia.Point(right, top);
        var br = new global::Avalonia.Point(right, bottom);
        var bl = new global::Avalonia.Point(left, bottom);

        if (DistancePointToSegment(screenPoint, tl, tr) <= tolerance) return true;
        if (DistancePointToSegment(screenPoint, tr, br) <= tolerance) return true;
        if (DistancePointToSegment(screenPoint, br, bl) <= tolerance) return true;
        if (DistancePointToSegment(screenPoint, bl, tl) <= tolerance) return true;

        return false;
    }

    private static double DistancePointToSegment(global::Avalonia.Point p, global::Avalonia.Point v, global::Avalonia.Point w)
    {
        double l2 = (v.X - w.X) * (v.X - w.X) + (v.Y - w.Y) * (v.Y - w.Y);
        if (l2 == 0) return Math.Sqrt((p.X - v.X) * (p.X - v.X) + (p.Y - v.Y) * (p.Y - v.Y));
        double t = ((p.X - v.X) * (w.X - v.X) + (p.Y - v.Y) * (w.Y - v.Y)) / l2;
        t = Math.Max(0, Math.Min(1, t));
        var projection = new global::Avalonia.Point(v.X + t * (w.X - v.X), v.Y + t * (w.Y - v.Y));
        return Math.Sqrt((p.X - projection.X) * (p.X - projection.X) + (p.Y - projection.Y) * (p.Y - projection.Y));
    }

    public override void Dispose()
    {
        _fillPaint.Dispose();
        base.Dispose();
    }
}
