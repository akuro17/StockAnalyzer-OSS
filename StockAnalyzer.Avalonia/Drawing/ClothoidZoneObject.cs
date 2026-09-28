using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Support/Resistance Zone Enclosure drawing object driven by screen-space Clothoid fillet geometry.
/// Features zone-strength adaptive curvature (S -> 1.0 creates gentle organic Clothoid-filleted zone enclosures,
/// S -> 0.0 approaches sharp rectangles), closed or open boundary modes, and full IFillableChartObject integration.
/// </summary>
public class ClothoidZoneObject : RelativeGeometricRenderer, IFillableChartObject, IDrawingCalculatedValuesProvider
{
    private const float GeometryEpsilon = 1e-4f;

    public override ChartObjectType Type => ChartObjectType.ClothoidZone;

    [Category("Geometry")]
    [DisplayName("Zone Strength")]
    [Range(0.01, 1.0)]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public double ZoneStrength { get; set; } = 0.5;

    [Category("Geometry")]
    [DisplayName("Is Closed Zone")]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public bool IsClosedZone { get; set; } = true;

    [Category("Style")]
    [DisplayName("Stroke Style")]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public DrawingStrokeStyle StrokeStyle { get; set; } = DrawingStrokeStyle.Solid;

    [Category("Fill Style")]
    [DisplayName("Is Filled")]
    [ParameterTag(DrawingParameterTags.Fill)]
    public bool IsFilled { get; set; } = true;

    [Category("Fill Style")]
    [DisplayName("Blend Mode")]
    [ParameterTag(DrawingParameterTags.Fill)]
    public DrawingBlendMode BlendMode { get; set; } = DrawingBlendMode.Normal;

    [Category("Fill Style")]
    [DisplayName("Gradient Type")]
    [ParameterTag(DrawingParameterTags.Fill)]
    public DrawingGradientType GradientType { get; set; } = DrawingGradientType.None;

    [Category("Fill Style")]
    [DisplayName("Gradient End Color")]
    [ParameterTag(DrawingParameterTags.Fill)]
    public Color? GradientEndColor { get; set; } = null;

    [Category("Fill Style")]
    [DisplayName("Fill Alpha")]
    [Range(0, 255)]
    [ParameterTag(DrawingParameterTags.Fill)]
    public byte FillAlpha { get; set; } = 30;

    [Category("Fill Style")]
    [DisplayName("Gradient End Alpha")]
    [Range(0, 255)]
    [ParameterTag(DrawingParameterTags.Fill)]
    public byte GradientEndAlpha { get; set; } = 30;

    private readonly SKPaint _fillPaint;
    private readonly SKPathEffect _dashEffect;
    private readonly SKPathEffect _dotEffect;

    public ClothoidZoneObject() : base()
    {
        _fillPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        _dashEffect = SKPathEffect.CreateDash(new float[] { 6f, 4f }, 0f);
        _dotEffect = SKPathEffect.CreateDash(new float[] { 2f, 3f }, 0f);
    }

    public ClothoidZoneObject(ChartPoint p1, ChartPoint p2) : this()
    {
        Points.Add(p1);
        Points.Add(p2);
    }

    public override void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (!IsVisible) return;

        _cachedPaint.Color = SkiaColor;
        _cachedPaint.StrokeWidth = (float)Thickness;
        _cachedPath.Reset();

        DrawGeometry(canvas, transform);

        // Selection handles on all 4 rectangle corners
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
        if (canvas == null || transform == null || Points.Count < 2) return;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        if (double.IsNaN(p1.X) || double.IsNaN(p1.Y) || double.IsInfinity(p1.X) || double.IsInfinity(p1.Y) ||
            double.IsNaN(p2.X) || double.IsNaN(p2.Y) || double.IsInfinity(p2.X) || double.IsInfinity(p2.Y))
        {
            return;
        }

        float xMin = (float)Math.Min(p1.X, p2.X);
        float xMax = (float)Math.Max(p1.X, p2.X);
        float yMin = (float)Math.Min(p1.Y, p2.Y);
        float yMax = (float)Math.Max(p1.Y, p2.Y);
        float w = xMax - xMin;
        float h = yMax - yMin;

        if (w <= GeometryEpsilon || h <= GeometryEpsilon) return;

        var rect = new SKRect(xMin, yMin, xMax, yMax);
        float maxFillet = Math.Min(w, h) * 0.5f;
        float d = maxFillet * (float)Math.Clamp(ZoneStrength, 0.01, 1.0);
        double curvature = 0.2 + 0.6 * Math.Clamp(ZoneStrength, 0.01, 1.0); // Outward convex clothoid corner bulge adapts with strength

        _cachedPath.Reset();

        if (d < 1.0f)
        {
            // Fallback to straight rectangle
            _cachedPath.AddRect(rect);
        }
        else
        {
            BuildClothoidZonePath(_cachedPath, xMin, xMax, yMin, yMax, d, curvature, IsClosedZone);
        }

        // Layer 1: Fill (if closed and filled)
        if (IsClosedZone && IsFilled)
        {
            _fillPaint.BlendMode = BlendMode.ToSkBlendMode();

            if (GradientType == DrawingGradientType.None)
            {
                _fillPaint.Shader = null;
                _fillPaint.Color = new SKColor(Color.R, Color.G, Color.B, FillAlpha);
                canvas.DrawPath(_cachedPath, _fillPaint);
            }
            else
            {
                using var shader = DrawingShaderFactory.CreateShader(this, rect, (float)p1.X, (float)p1.Y, (float)p2.X, (float)p2.Y);
                if (shader != null)
                {
                    _fillPaint.Shader = shader;
                    canvas.DrawPath(_cachedPath, _fillPaint);
                }
                else
                {
                    _fillPaint.Shader = null;
                    _fillPaint.Color = new SKColor(Color.R, Color.G, Color.B, FillAlpha);
                    canvas.DrawPath(_cachedPath, _fillPaint);
                }
            }
        }

        // Layer 2: Outline Stroke
        if (Thickness > 0)
        {
            _cachedPaint.Style = SKPaintStyle.Stroke;
            _cachedPaint.StrokeCap = IsClosedZone ? SKStrokeCap.Butt : SKStrokeCap.Round;
            _cachedPaint.PathEffect = StrokeStyle switch
            {
                DrawingStrokeStyle.Dash => _dashEffect,
                DrawingStrokeStyle.Dot => _dotEffect,
                _ => null
            };

            canvas.DrawPath(_cachedPath, _cachedPaint);
            _cachedPaint.PathEffect = null;
            _cachedPaint.StrokeCap = SKStrokeCap.Butt;
        }
    }

    private static void BuildClothoidZonePath(
        SKPath path,
        float xMin, float xMax,
        float yMin, float yMax,
        float d, double curvature,
        bool isClosed)
    {
        Span<SKPoint> cornerArc = stackalloc SKPoint[ClothoidGeometricMath.MaxSamplePoints + 1];

        if (isClosed)
        {
            // Start at top edge right after top-left fillet
            path.MoveTo(xMin + d, yMin);

            // 1. Top Edge
            path.LineTo(xMax - d, yMin);

            // 2. Top-Right Fillet: (xMax - d, yMin) -> (xMax, yMin + d)
            int c1 = ClothoidGeometricMath.GenerateClothoidArc(
                new SKPoint(xMax - d, yMin),
                new SKPoint(xMax, yMin + d),
                curvature, cornerArc, out _);
            for (int i = 1; i < c1; i++) path.LineTo(cornerArc[i]);

            // 3. Right Edge
            path.LineTo(xMax, yMax - d);

            // 4. Bottom-Right Fillet: (xMax, yMax - d) -> (xMax - d, yMax)
            int c2 = ClothoidGeometricMath.GenerateClothoidArc(
                new SKPoint(xMax, yMax - d),
                new SKPoint(xMax - d, yMax),
                curvature, cornerArc, out _);
            for (int i = 1; i < c2; i++) path.LineTo(cornerArc[i]);

            // 5. Bottom Edge
            path.LineTo(xMin + d, yMax);

            // 6. Bottom-Left Fillet: (xMin + d, yMax) -> (xMin, yMax - d)
            int c3 = ClothoidGeometricMath.GenerateClothoidArc(
                new SKPoint(xMin + d, yMax),
                new SKPoint(xMin, yMax - d),
                curvature, cornerArc, out _);
            for (int i = 1; i < c3; i++) path.LineTo(cornerArc[i]);

            // 7. Left Edge
            path.LineTo(xMin, yMin + d);

            // 8. Top-Left Fillet: (xMin, yMin + d) -> (xMin + d, yMin)
            int c4 = ClothoidGeometricMath.GenerateClothoidArc(
                new SKPoint(xMin, yMin + d),
                new SKPoint(xMin + d, yMin),
                curvature, cornerArc, out _);
            for (int i = 1; i < c4; i++) path.LineTo(cornerArc[i]);

            path.Close();
        }
        else
        {
            // Open Mode: Draw Top Boundary
            path.MoveTo(xMin, yMin + d);
            int cTopLeft = ClothoidGeometricMath.GenerateClothoidArc(
                new SKPoint(xMin, yMin + d),
                new SKPoint(xMin + d, yMin),
                curvature, cornerArc, out _);
            for (int i = 1; i < cTopLeft; i++) path.LineTo(cornerArc[i]);

            path.LineTo(xMax - d, yMin);

            int cTopRight = ClothoidGeometricMath.GenerateClothoidArc(
                new SKPoint(xMax - d, yMin),
                new SKPoint(xMax, yMin + d),
                curvature, cornerArc, out _);
            for (int i = 1; i < cTopRight; i++) path.LineTo(cornerArc[i]);

            // Open Mode: Draw Bottom Boundary
            path.MoveTo(xMin, yMax - d);
            int cBottomLeft = ClothoidGeometricMath.GenerateClothoidArc(
                new SKPoint(xMin, yMax - d),
                new SKPoint(xMin + d, yMax),
                -curvature, cornerArc, out _);
            for (int i = 1; i < cBottomLeft; i++) path.LineTo(cornerArc[i]);

            path.LineTo(xMax - d, yMax);

            int cBottomRight = ClothoidGeometricMath.GenerateClothoidArc(
                new SKPoint(xMax - d, yMax),
                new SKPoint(xMax, yMax - d),
                -curvature, cornerArc, out _);
            for (int i = 1; i < cBottomRight; i++) path.LineTo(cornerArc[i]);
        }
    }

    public override bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (transform == null || Points.Count < 2) return false;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        if (double.IsNaN(p1.X) || double.IsNaN(p1.Y) || double.IsInfinity(p1.X) || double.IsInfinity(p1.Y) ||
            double.IsNaN(p2.X) || double.IsNaN(p2.Y) || double.IsInfinity(p2.X) || double.IsInfinity(p2.Y))
        {
            return false;
        }

        float xMin = (float)Math.Min(p1.X, p2.X);
        float xMax = (float)Math.Max(p1.X, p2.X);
        float yMin = (float)Math.Min(p1.Y, p2.Y);
        float yMax = (float)Math.Max(p1.Y, p2.Y);

        var rect = new global::Avalonia.Rect(xMin, yMin, xMax - xMin, yMax - yMin);

        // Filled closed zone hit test
        if (IsClosedZone && IsFilled && rect.Contains(screenPoint))
        {
            return true;
        }

        // Boundary proximity test (outer box expanded by tolerance)
        var expanded = rect.Inflate(tolerance);
        if (!expanded.Contains(screenPoint)) return false;

        var contracted = rect.Inflate(-tolerance);
        if (!contracted.Contains(screenPoint)) return true;

        return false;
    }

    public override void Dispose()
    {
        _fillPaint.Dispose();
        _dashEffect.Dispose();
        _dotEffect.Dispose();
        base.Dispose();
    }

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
    {
        if (Points.Count < 2) return Array.Empty<DrawingCalculatedValue>();

        var p1 = Points[0];
        var p2 = Points[1];
        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);

        decimal topPrice = Math.Max(p1.Price, p2.Price);
        decimal bottomPrice = Math.Min(p1.Price, p2.Price);
        decimal priceSpan = topPrice - bottomPrice;

        var values = new List<DrawingCalculatedValue>();
        values.Add(new DrawingCalculatedValue("TopPrice", "Zone High", topPrice, $"{topPrice:F3}", IndicatorColor.Bullish));
        values.Add(new DrawingCalculatedValue("BottomPrice", "Zone Low", bottomPrice, $"{bottomPrice:F3}", IndicatorColor.Bearish));
        values.Add(new DrawingCalculatedValue("PriceSpan", "Zone Height", priceSpan, $"{priceSpan:F3}", color));

        double days = Math.Abs((p2.Time - p1.Time).TotalDays);
        values.Add(new DrawingCalculatedValue("Duration", "Duration", (decimal)Math.Round(days, 1), $"{days:F1} days", IndicatorColor.Gray));

        values.Add(new DrawingCalculatedValue("ZoneStrength", "Zone Strength", (decimal)Math.Round(ZoneStrength, 2), $"{ZoneStrength:P0}", IndicatorColor.Gray));

        return values;
    }
}
