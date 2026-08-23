using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Ring/annular-sector object sharing the outer-ellipse geometry of <see cref="EllipseArcObject"/>
/// (Points[0]/[1] = bounding box, Points[2]/[3] = start/end angle control points on the outer
/// circumference) plus a 5th control point (Points[4]) placed toward the center whose distance from
/// the center — as a fraction of the outer radius — defines the inner boundary. No separate
/// Annulus/AnnularSector mode is needed: dragging the end-angle point (Points[3]) all the way around
/// to a 360° sweep collapses the two radial seams to zero length, producing a seamless full ring as
/// a natural limiting case of the same geometry (a "半円周"-style angle preset rather than a distinct
/// shape).
/// </summary>
public class EllipseAnnulusObject : IEllipseFamilyObject
{
    private const float GeometryEpsilon = 1e-4f;

    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.EllipseAnnulus;
    public List<ChartPoint> Points { get; } = new List<ChartPoint>();
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int ZIndex { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;

    /// <summary>When true, constrains the outer ellipse (Points[0]/[1]) to a circle, matching
    /// <see cref="EllipseObject.IsCircular"/>.</summary>
    public bool IsCircular { get; set; } = false;

    public bool IsFilled { get; set; } = true;
    public DrawingBlendMode BlendMode { get; set; } = DrawingBlendMode.Normal;
    public DrawingGradientType GradientType { get; set; } = DrawingGradientType.None;
    public Color? GradientEndColor { get; set; } = null;
    public byte FillAlpha { get; set; } = 30;
    public byte GradientEndAlpha { get; set; } = 30;

    /// <summary>
    /// Fine-tuning accessor for Points[4]'s inner-radius ratio, used by the settings-dialog
    /// NumericUpDown (<see cref="Views.Dialogs.EllipseAnnulusSettingsPanelDefinition"/>). Unlike
    /// <see cref="EllipseArcGeometry.ComputeRadiusRatio"/> (used by <see cref="Render"/>/<see cref="HitTest"/>,
    /// which measures in screen space every frame), this operates entirely in chart space (Time.Ticks
    /// and Price) so it works without an <see cref="ICoordinateTransform"/> — the settings dialog's
    /// Populate/Commit methods do not receive one, matching the pattern already established by
    /// <see cref="Views.Dialogs.CatenaryCurveSettingsPanelDefinition"/> for chart-space-only point edits.
    /// </summary>
    public double InnerRadiusRatio
    {
        get
        {
            if (Points.Count < 5) return 0.5;
            GetOuterCenterAndHalfExtents(out double cx, out double cy, out double halfW, out double halfH);
            double nx = ((double)Points[4].Time.Ticks - cx) / halfW;
            double ny = ((double)Points[4].Price - cy) / halfH;
            return Math.Clamp(Math.Sqrt(nx * nx + ny * ny), 0.02, 0.98);
        }
        set
        {
            if (Points.Count < 5) return;
            double ratio = Math.Clamp(value, 0.02, 0.98);
            GetOuterCenterAndHalfExtents(out double cx, out double cy, out double halfW, out double halfH);

            double curNx = ((double)Points[4].Time.Ticks - cx) / halfW;
            double curNy = ((double)Points[4].Price - cy) / halfH;
            double curDist = Math.Sqrt(curNx * curNx + curNy * curNy);

            // Degenerate direction (Points[4] sits exactly at the outer center): default to +X.
            if (curDist < 1e-6) { curNx = 1.0; curNy = 0.0; curDist = 1.0; }

            double scale = ratio / curDist;
            long newTicks = Math.Clamp((long)Math.Round(cx + curNx * scale * halfW), 0, DateTime.MaxValue.Ticks);
            decimal newPrice = (decimal)(cy + curNy * scale * halfH);
            Points[4] = new ChartPoint(new DateTime(newTicks), newPrice);
        }
    }

    private void GetOuterCenterAndHalfExtents(out double cx, out double cy, out double halfW, out double halfH)
    {
        cx = ((double)Points[0].Time.Ticks + Points[1].Time.Ticks) / 2.0;
        cy = (double)(Points[0].Price + Points[1].Price) / 2.0;
        halfW = Math.Max(Math.Abs((double)Points[1].Time.Ticks - Points[0].Time.Ticks) / 2.0, 1e-6);
        halfH = Math.Max(Math.Abs((double)(Points[1].Price - Points[0].Price)) / 2.0, 1e-6);
    }

    public EllipseAnnulusObject(
        ChartPoint boundsStart, ChartPoint boundsEnd, ChartPoint startAnglePoint, ChartPoint endAnglePoint, ChartPoint innerRadiusPoint)
    {
        Points.Add(boundsStart);
        Points.Add(boundsEnd);
        Points.Add(startAnglePoint);
        Points.Add(endAnglePoint);
        Points.Add(innerRadiusPoint);
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points.Count < 5) return;
        if (!TryComputeRing(transform, out var path, out var handles)) return;

        if (IsFilled)
        {
            using var fillPaint = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                IsAntialias = true,
                BlendMode = BlendMode.ToSkBlendMode(),
                Color = new SKColor(Color.R, Color.G, Color.B, FillAlpha)
            };
            canvas.DrawPath(path, fillPaint);
        }

        if (Thickness > 0)
        {
            using var strokePaint = new SKPaint
            {
                Color = SkiaColor,
                StrokeWidth = (float)Thickness,
                Style = SKPaintStyle.Stroke,
                StrokeJoin = SKStrokeJoin.Round,
                BlendMode = SKBlendMode.SrcOver,
                IsAntialias = true
            };
            canvas.DrawPath(path, strokePaint);
        }

        if (IsSelected)
        {
            for (int i = 0; i < handles.Length; i++)
            {
                SelectionHandleRenderer.Draw(canvas, handles[i], i == AnchorPointIndex ? DrawingThemeContext.AnchorPointColor : (SKColor?)null);
            }
        }
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (transform == null || Points.Count < 5) return false;
        if (!TryComputeRing(transform, out var path, out _)) return false;

        float x = (float)screenPoint.X;
        float y = (float)screenPoint.Y;

        if (IsFilled && path.Contains(x, y)) return true;

        double effectiveTolerance = tolerance + Thickness / 2.0;
        return EllipseArcGeometry.IsNearPathBoundary(path, x, y, effectiveTolerance);
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price + priceDelta);
        }
    }

    /// <summary>
    /// Builds the closed ring/annular-sector path (outer arc forward, radial line in, inner arc
    /// backward, radial line out) from the current 5 control points, or returns false if the
    /// geometry is degenerate and should not be rendered/hit-tested.
    /// </summary>
    private bool TryComputeRing(ICoordinateTransform transform, out SKPath path, out global::Avalonia.Point[] handles)
    {
        path = new SKPath();
        handles = Array.Empty<global::Avalonia.Point>();

        var s0 = transform.ChartToScreen(Points[0]);
        var s1 = transform.ChartToScreen(Points[1]);
        var s2 = transform.ChartToScreen(Points[2]);
        var s3 = transform.ChartToScreen(Points[3]);
        var s4 = transform.ChartToScreen(Points[4]);

        if (!IsFinite(s0) || !IsFinite(s1) || !IsFinite(s2) || !IsFinite(s3) || !IsFinite(s4)) return false;

        var outerRect = EllipseArcGeometry.ComputeBoundingRect(
            new SKPoint((float)s0.X, (float)s0.Y), new SKPoint((float)s1.X, (float)s1.Y), IsCircular);
        if (outerRect.Width <= GeometryEpsilon || outerRect.Height <= GeometryEpsilon) return false;

        float startAngle = EllipseArcGeometry.AngleFromPoint(outerRect, new SKPoint((float)s2.X, (float)s2.Y));
        float endAngle = EllipseArcGeometry.AngleFromPoint(outerRect, new SKPoint((float)s3.X, (float)s3.Y));
        float sweep = EllipseArcGeometry.ComputeSweep(startAngle, endAngle);

        float innerRatio = EllipseArcGeometry.ComputeRadiusRatio(outerRect, new SKPoint((float)s4.X, (float)s4.Y));
        var innerRect = EllipseArcGeometry.ScaleRectAroundCenter(outerRect, innerRatio);

        float sweepEndAngle = startAngle + sweep;
        path.MoveTo(EllipseArcGeometry.PointOnEllipse(outerRect, startAngle));
        path.ArcTo(outerRect, startAngle, sweep, forceMoveTo: false);
        path.LineTo(EllipseArcGeometry.PointOnEllipse(innerRect, sweepEndAngle));
        path.ArcTo(innerRect, sweepEndAngle, -sweep, forceMoveTo: false);
        path.Close();

        handles = new[] { s0, s1, s2, s3, s4 };
        return true;
    }

    private static bool IsFinite(global::Avalonia.Point p)
        => !double.IsNaN(p.X) && !double.IsNaN(p.Y) && !double.IsInfinity(p.X) && !double.IsInfinity(p.Y);
}

