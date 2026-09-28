using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Curved arrow drawing object driven by screen-space Clothoid (Cornu spiral) geometry.
/// Features continuous curvature variation, strict C0 endpoint continuity, and
/// tangential auto-alignment of the arrowhead along the terminal curve angle.
/// </summary>
public class ClothoidArrowObject : RelativeGeometricRenderer, IDrawingCalculatedValuesProvider
{
    public override ChartObjectType Type => ChartObjectType.ClothoidArrow;

    [Category("Geometry")]
    [DisplayName("Curvature Intensity")]
    [Range(-1.0, 1.0)]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public double CurvatureIntensity { get; set; } = 0.3;

    [Category("Style")]
    [DisplayName("Arrow Head Size")]
    [Range(5.0, 50.0)]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public float ArrowHeadSize { get; set; } = 15f;

    [Category("Style")]
    [DisplayName("Arrow Head Angle")]
    [Range(10.0, 60.0)]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public float ArrowHeadAngle { get; set; } = 25f;

    [Category("Style")]
    [DisplayName("Fill Head")]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public bool IsHeadFilled { get; set; } = true;

    [Category("Style")]
    [DisplayName("Stroke Style")]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public DrawingStrokeStyle StrokeStyle { get; set; } = DrawingStrokeStyle.Solid;

    [Browsable(false)]
    [JsonIgnore]
    public DrawingStrokeStyle Style
    {
        get => StrokeStyle;
        set => StrokeStyle = value;
    }

    private const float MinHeadChordPx = 3.0f;
    private const float MaxHeadChordRatio = 0.45f;
    private const float DashIntervalOn = 6.0f;
    private const float DashIntervalOff = 4.0f;
    private const float DotIntervalOn = 2.0f;
    private const float DotIntervalOff = 3.0f;

    private readonly SKPaint _fillPaint;
    private readonly SKPathEffect _dashEffect;
    private readonly SKPathEffect _dotEffect;

    public ClothoidArrowObject() : base()
    {
        _fillPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        _dashEffect = SKPathEffect.CreateDash(new float[] { DashIntervalOn, DashIntervalOff }, 0f);
        _dotEffect = SKPathEffect.CreateDash(new float[] { DotIntervalOn, DotIntervalOff }, 0f);
    }

    public ClothoidArrowObject(ChartPoint tail, ChartPoint head) : this()
    {
        Points.Add(tail);
        Points.Add(head);
    }

    public ClothoidArrowObject(IEnumerable<ChartPoint> points) : this()
    {
        if (points != null)
        {
            Points.AddRange(points);
        }
    }

    public void AddPoint(ChartPoint point) => Points.Add(point);

    protected override void DrawGeometry(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points.Count < 2) return;

        _cachedPath.Reset();

        Span<SKPoint> arcPoints = stackalloc SKPoint[ClothoidGeometricMath.MaxSamplePoints + 1];
        double lastTerminalAngle = 0.0;
        SKPoint lastTip = default;
        bool hasDrawnAny = false;
        float lastChordLength = 0f;

        for (int s = 0; s < Points.Count - 1; s++)
        {
            var p1 = transform.ChartToScreen(Points[s]);
            var p2 = transform.ChartToScreen(Points[s + 1]);

            if (double.IsNaN(p1.X) || double.IsNaN(p1.Y) || double.IsInfinity(p1.X) || double.IsInfinity(p1.Y) ||
                double.IsNaN(p2.X) || double.IsNaN(p2.Y) || double.IsInfinity(p2.X) || double.IsInfinity(p2.Y))
            {
                continue;
            }

            int pointCount = ClothoidGeometricMath.GenerateClothoidArc(
                new SKPoint((float)p1.X, (float)p1.Y),
                new SKPoint((float)p2.X, (float)p2.Y),
                CurvatureIntensity,
                arcPoints,
                out double segTerminalAngle);

            if (pointCount < 2) continue;

            if (!hasDrawnAny)
            {
                _cachedPath.MoveTo(arcPoints[0]);
                hasDrawnAny = true;
            }

            for (int i = 1; i < pointCount; i++)
            {
                _cachedPath.LineTo(arcPoints[i]);
            }

            lastTerminalAngle = segTerminalAngle;
            lastTip = arcPoints[pointCount - 1];
            double dx = p2.X - p1.X;
            double dy = p2.Y - p1.Y;
            lastChordLength = (float)Math.Sqrt(dx * dx + dy * dy);
        }

        if (!hasDrawnAny) return;

        _cachedPaint.PathEffect = StrokeStyle switch
        {
            DrawingStrokeStyle.Dash => _dashEffect,
            DrawingStrokeStyle.Dot => _dotEffect,
            _ => null
        };

        canvas.DrawPath(_cachedPath, _cachedPaint);

        // Reset path effect to solid and draw arrowhead tangentially aligned to the final clothoid curve
        _cachedPaint.PathEffect = null;
        DrawArrowHead(canvas, lastTip, lastTerminalAngle, lastChordLength);
    }

    private void DrawArrowHead(SKCanvas canvas, SKPoint tip, double terminalAngle, float chordLength)
    {
        float effectiveArrowSize = Math.Min(ArrowHeadSize, Math.Max(MinHeadChordPx, chordLength * MaxHeadChordRatio));
        if (chordLength < MinHeadChordPx) return;

        double halfAngleRad = ArrowHeadAngle * (Math.PI / 180.0);

        float x1 = (float)(tip.X - effectiveArrowSize * Math.Cos(terminalAngle - halfAngleRad));
        float y1 = (float)(tip.Y - effectiveArrowSize * Math.Sin(terminalAngle - halfAngleRad));
        float x2 = (float)(tip.X - effectiveArrowSize * Math.Cos(terminalAngle + halfAngleRad));
        float y2 = (float)(tip.Y - effectiveArrowSize * Math.Sin(terminalAngle + halfAngleRad));

        _cachedPath.Reset();
        _cachedPath.MoveTo(tip.X, tip.Y);
        _cachedPath.LineTo(x1, y1);
        _cachedPath.LineTo(x2, y2);
        _cachedPath.Close();

        if (IsHeadFilled)
        {
            _fillPaint.Color = _cachedPaint.Color;
            canvas.DrawPath(_cachedPath, _fillPaint);
        }
        else
        {
            // Open arrowhead (stroke fins only)
            _cachedPath.Reset();
            _cachedPath.MoveTo(x1, y1);
            _cachedPath.LineTo(tip.X, tip.Y);
            _cachedPath.LineTo(x2, y2);
            canvas.DrawPath(_cachedPath, _cachedPaint);
        }
    }

    public override bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (transform == null || Points.Count < 2) return false;

        Span<SKPoint> arcPoints = stackalloc SKPoint[ClothoidGeometricMath.MaxSamplePoints + 1];

        for (int s = 0; s < Points.Count - 1; s++)
        {
            var p1 = transform.ChartToScreen(Points[s]);
            var p2 = transform.ChartToScreen(Points[s + 1]);

            if (double.IsNaN(p1.X) || double.IsNaN(p1.Y) || double.IsInfinity(p1.X) || double.IsInfinity(p1.Y) ||
                double.IsNaN(p2.X) || double.IsNaN(p2.Y) || double.IsInfinity(p2.X) || double.IsInfinity(p2.Y))
            {
                continue;
            }

            int pointCount = ClothoidGeometricMath.GenerateClothoidArc(
                new SKPoint((float)p1.X, (float)p1.Y),
                new SKPoint((float)p2.X, (float)p2.Y),
                CurvatureIntensity,
                arcPoints,
                out _);

            if (pointCount < 2) continue;

            // Check proximity to polyline segments of the clothoid curve
            for (int i = 0; i < pointCount - 1; i++)
            {
                var v = new global::Avalonia.Point(arcPoints[i].X, arcPoints[i].Y);
                var w = new global::Avalonia.Point(arcPoints[i + 1].X, arcPoints[i + 1].Y);
                if (DistancePointToSegment(screenPoint, v, w) <= tolerance)
                {
                    return true;
                }
            }
        }

        // Check proximity to arrowhead tip area at final point
        var pEnd = transform.ChartToScreen(Points[Points.Count - 1]);
        double distHead = Math.Sqrt(Math.Pow(screenPoint.X - pEnd.X, 2) + Math.Pow(screenPoint.Y - pEnd.Y, 2));
        if (distHead <= Math.Max(tolerance, ArrowHeadSize))
        {
            return true;
        }

        return false;
    }

    private static double DistancePointToSegment(global::Avalonia.Point p, global::Avalonia.Point v, global::Avalonia.Point w)
    {
        double l2 = Math.Pow(v.X - w.X, 2) + Math.Pow(v.Y - w.Y, 2);
        if (l2 == 0) return Math.Sqrt(Math.Pow(p.X - v.X, 2) + Math.Pow(p.Y - v.Y, 2));
        double t = ((p.X - v.X) * (w.X - v.X) + (p.Y - v.Y) * (w.Y - v.Y)) / l2;
        t = Math.Max(0, Math.Min(1, t));
        var projection = new global::Avalonia.Point(v.X + t * (w.X - v.X), v.Y + t * (w.Y - v.Y));
        return Math.Sqrt(Math.Pow(p.X - projection.X, 2) + Math.Pow(p.Y - projection.Y, 2));
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
        var p2 = Points[Points.Count - 1];
        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);

        decimal arrowPrice = DrawingMath.InterpolatePrice(p1, p2, timestamp);

        var values = new List<DrawingCalculatedValue>();
        values.Add(new DrawingCalculatedValue("ArrowPrice", "Arrow Price", arrowPrice, $"{arrowPrice:F3}", color));

        decimal priceDelta = p2.Price - p1.Price;
        string direction = priceDelta > 0 ? "Up (Bullish)" : (priceDelta < 0 ? "Down (Bearish)" : "Neutral");
        var dirColor = priceDelta > 0 ? IndicatorColor.Bullish : (priceDelta < 0 ? IndicatorColor.Bearish : IndicatorColor.Gray);
        values.Add(new DrawingCalculatedValue("Direction", "Direction", null, direction, dirColor));

        decimal deltaPct = DrawingMath.CalculatePercentageChange(p1.Price, p2.Price);
        values.Add(new DrawingCalculatedValue("PriceDelta", "Price Change", priceDelta, $"{priceDelta:+0.000;-0.000;0.000} ({deltaPct:+0.00;-0.00;0.00}%)", dirColor));

        double days = Math.Abs((p2.Time - p1.Time).TotalDays);
        values.Add(new DrawingCalculatedValue("Duration", "Duration", (decimal)Math.Round(days, 1), $"{days:F1} days", IndicatorColor.Gray));

        values.Add(new DrawingCalculatedValue("Curvature", "Curvature Intensity", (decimal)Math.Round(CurvatureIntensity, 2), $"{CurvatureIntensity:F2}", IndicatorColor.Gray));

        return values;
    }
}
