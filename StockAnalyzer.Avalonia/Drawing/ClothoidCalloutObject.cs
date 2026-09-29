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
/// High-visibility callout drawing object driven by screen-space Clothoid (Cornu spiral) geometry.
/// Features a smooth curved leader line that cleanly clips at the bounding box perimeter
/// of the text box (0px overlap inside the box) and full multi-line text annotation support.
/// </summary>
public class ClothoidCalloutObject : RelativeGeometricRenderer, ITextAnnotatedObject
{
    public override ChartObjectType Type => ChartObjectType.ClothoidCallout;

    [Category("Geometry")]
    [DisplayName("Curvature Intensity")]
    [Range(-1.0, 1.0)]
    [ParameterTag(DrawingParameterTags.Geometry)]
    public double CurvatureIntensity { get; set; } = 0.3;

    [Category("Style")]
    [DisplayName("Leader Stroke Style")]
    [ParameterTag(DrawingParameterTags.Common)]
    public DrawingStrokeStyle LeaderStrokeStyle { get; set; } = DrawingStrokeStyle.Dash;

    private string _text = "Callout";
    [Category("Text")]
    [DisplayName("Text")]
    [ParameterTag(DrawingParameterTags.Typography)]
    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value ?? string.Empty;
            InvalidateCache();
        }
    }

    private double _fontSize = DrawingThemeContext.DrawingFontSize;
    [Category("Text")]
    [DisplayName("Font Size")]
    [Range(8.0, 72.0)]
    [ParameterTag(DrawingParameterTags.Typography)]
    public double FontSize
    {
        get => _fontSize;
        set
        {
            if (Math.Abs(_fontSize - value) < 1e-6) return;
            _fontSize = value;
            _textPaint.TextSize = (float)value;
            InvalidateCache();
        }
    }

    private TextHorizontalAlignment _alignment = TextHorizontalAlignment.Left;
    [Category("Text")]
    [DisplayName("Text Alignment")]
    [ParameterTag(DrawingParameterTags.Typography)]
    public TextHorizontalAlignment Alignment
    {
        get => _alignment;
        set
        {
            if (_alignment == value) return;
            _alignment = value;
            InvalidateCache();
        }
    }

    private bool _showBackgroundBox = true;
    [Category("Text")]
    [DisplayName("Show Background")]
    [ParameterTag(DrawingParameterTags.Typography)]
    public bool ShowBackgroundBox
    {
        get => _showBackgroundBox;
        set => _showBackgroundBox = value;
    }

    private Color _backgroundColor = Color.FromArgb(220, DrawingThemeContext.AppBackgroundColor.R, DrawingThemeContext.AppBackgroundColor.G, DrawingThemeContext.AppBackgroundColor.B);
    [Category("Text")]
    [DisplayName("Background Color")]
    [ParameterTag(DrawingParameterTags.Typography)]
    public Color BackgroundColor
    {
        get => _backgroundColor;
        set
        {
            if (_backgroundColor == value) return;
            _backgroundColor = value;
            _bgPaint.Color = new SKColor(value.R, value.G, value.B, value.A);
        }
    }

    private float _backgroundPadding = ChartConstants.DefaultTextBackgroundPadding;
    [Category("Text")]
    [DisplayName("Background Padding")]
    [Range(0.0, 50.0)]
    [ParameterTag(DrawingParameterTags.Typography)]
    public float BackgroundPadding
    {
        get => _backgroundPadding;
        set
        {
            if (Math.Abs(_backgroundPadding - value) < 1e-6f) return;
            _backgroundPadding = value;
            InvalidateCache();
        }
    }

    private float _cornerRadius = ChartConstants.DefaultDrawingCornerRadius;
    [Category("Text")]
    [DisplayName("Corner Radius")]
    [Range(0.0, 50.0)]
    [ParameterTag(DrawingParameterTags.Typography)]
    public float CornerRadius
    {
        get => _cornerRadius;
        set => _cornerRadius = value;
    }

    // Pre-allocated cached paints and path effects
    private readonly SKPaint _leaderPaint;
    private readonly SKPaint _textPaint;
    private readonly SKPaint _bgPaint;
    private readonly SKPaint _borderPaint;
    private readonly SKPathEffect _dashEffect;
    private readonly SKPathEffect _dotEffect;

    // Layout cache
    private string[]? _cachedLines;
    private SKSize? _cachedBlockSize;

    public ClothoidCalloutObject() : base()
    {
        _dashEffect = SKPathEffect.CreateDash(new float[] { 5f, 5f }, 0f);
        _dotEffect = SKPathEffect.CreateDash(new float[] { 2f, 3f }, 0f);

        _leaderPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            IsAntialias = true,
            PathEffect = _dashEffect
        };

        _textPaint = new SKPaint
        {
            Color = DrawingThemeContext.MainTextSkColor,
            IsAntialias = true,
            TextSize = (float)_fontSize,
            Typeface = SKTypeface.Default
        };

        _bgPaint = new SKPaint
        {
            Color = new SKColor(_backgroundColor.R, _backgroundColor.G, _backgroundColor.B, _backgroundColor.A),
            Style = SKPaintStyle.Fill
        };

        _borderPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };
    }

    public ClothoidCalloutObject(ChartPoint anchor, ChartPoint body) : this()
    {
        Points.Add(anchor);
        Points.Add(body);
    }

    public void InvalidateCache()
    {
        _cachedLines = null;
        _cachedBlockSize = null;
    }

    private string[] GetLines() => _cachedLines ??= MultilineTextRenderer.SplitLines(_text);

    private SKSize GetBlockSize() => _cachedBlockSize ??= MultilineTextRenderer.MeasureBlock(_textPaint, GetLines());

    protected override void DrawGeometry(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points.Count < 2) return;

        var p1 = transform.ChartToScreen(Points[0]); // Anchor
        var p2 = transform.ChartToScreen(Points[1]); // Text Box Center

        if (double.IsNaN(p1.X) || double.IsNaN(p1.Y) || double.IsInfinity(p1.X) || double.IsInfinity(p1.Y) ||
            double.IsNaN(p2.X) || double.IsNaN(p2.Y) || double.IsInfinity(p2.X) || double.IsInfinity(p2.Y))
        {
            return;
        }

        var lines = GetLines();
        var blockSize = GetBlockSize();

        float w = blockSize.Width + _backgroundPadding * 2f;
        float h = blockSize.Height + _backgroundPadding * 2f;

        float x = (float)p2.X;
        float y = (float)p2.Y;

        var rect = new SKRect(x - w / 2f, y - h / 2f, x + w / 2f, y + h / 2f);

        // 1. Draw smooth Clothoid leader line from Anchor (p1) to Box boundary
        float p0X = (float)p1.X;
        float p0Y = (float)p1.Y;
        bool p0Inside = p0X >= rect.Left && p0X <= rect.Right && p0Y >= rect.Top && p0Y <= rect.Bottom;

        if (!p0Inside)
        {
            Span<SKPoint> arcPoints = stackalloc SKPoint[ClothoidGeometricMath.MaxSamplePoints + 1];
            int count = ClothoidGeometricMath.GenerateClothoidArc(
                new SKPoint(p0X, p0Y),
                new SKPoint(x, y),
                CurvatureIntensity,
                arcPoints,
                out _);

            if (count >= 2)
            {
                // Scan backwards from box center (arcPoints[count-1]) to find first point outside the rectangle
                int exitIndex = 0;
                for (int i = count - 1; i >= 0; i--)
                {
                    if (arcPoints[i].X < rect.Left || arcPoints[i].X > rect.Right ||
                        arcPoints[i].Y < rect.Top || arcPoints[i].Y > rect.Bottom)
                    {
                        exitIndex = i;
                        break;
                    }
                }

                // Compute exact intersection of segment (arcPoints[exitIndex] -> arcPoints[exitIndex+1]) with rect
                SKPoint clipPoint = ClipSegmentToRect(arcPoints[exitIndex], arcPoints[Math.Min(exitIndex + 1, count - 1)], rect);

                _cachedPath.Reset();
                _cachedPath.MoveTo(arcPoints[0]);
                for (int i = 1; i <= exitIndex; i++)
                {
                    _cachedPath.LineTo(arcPoints[i]);
                }
                _cachedPath.LineTo(clipPoint);

                _leaderPaint.Color = SkiaColor;
                _leaderPaint.StrokeWidth = (float)Thickness;
                _leaderPaint.PathEffect = LeaderStrokeStyle switch
                {
                    DrawingStrokeStyle.Dash => _dashEffect,
                    DrawingStrokeStyle.Dot => _dotEffect,
                    _ => null
                };

                canvas.DrawPath(_cachedPath, _leaderPaint);
            }
        }

        // 2. Draw Text Box Background
        if (_showBackgroundBox)
        {
            canvas.DrawRoundRect(rect, _cornerRadius, _cornerRadius, _bgPaint);
        }

        // 3. Draw Text Box Border
        _borderPaint.Color = SkiaColor;
        _borderPaint.StrokeWidth = (float)Thickness;
        canvas.DrawRoundRect(rect, _cornerRadius, _cornerRadius, _borderPaint);

        // 4. Draw Multiline Text
        float textLeftX = rect.Left + _backgroundPadding;
        MultilineTextRenderer.DrawBlock(canvas, lines, textLeftX, y, _textPaint, Alignment, blockSize.Width);
    }

    private static SKPoint ClipSegmentToRect(SKPoint outside, SKPoint inside, SKRect rect)
    {
        float dx = inside.X - outside.X;
        float dy = inside.Y - outside.Y;

        float tMin = 0f;
        float tMax = 1f;

        // Liang-Barsky line clipping against rectangle bounds
        ClipTest(-dx, outside.X - rect.Left, ref tMin, ref tMax);
        ClipTest(dx, rect.Right - outside.X, ref tMin, ref tMax);
        ClipTest(-dy, outside.Y - rect.Top, ref tMin, ref tMax);
        ClipTest(dy, rect.Bottom - outside.Y, ref tMin, ref tMax);

        float t = Math.Clamp(tMin, 0f, 1f);
        return new SKPoint(outside.X + t * dx, outside.Y + t * dy);
    }

    private static void ClipTest(float p, float q, ref float tMin, ref float tMax)
    {
        if (Math.Abs(p) < 1e-7f) return;
        float r = q / p;
        if (p < 0f)
        {
            if (r > tMin) tMin = r;
        }
        else
        {
            if (r < tMax) tMax = r;
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

        // 1. Proximity to Anchor point
        double distAnchor = Math.Sqrt(Math.Pow(screenPoint.X - p1.X, 2) + Math.Pow(screenPoint.Y - p1.Y, 2));
        if (distAnchor <= tolerance * 2) return true;

        // 2. Hit inside Text Box Rect
        var blockSize = GetBlockSize();
        float w = blockSize.Width + _backgroundPadding * 2f;
        float h = blockSize.Height + _backgroundPadding * 2f;
        var boxRect = new global::Avalonia.Rect(p2.X - w / 2.0, p2.Y - h / 2.0, w, h);
        if (boxRect.Contains(screenPoint)) return true;

        // 3. Proximity to Clothoid Leader Line
        Span<SKPoint> arcPoints = stackalloc SKPoint[ClothoidGeometricMath.MaxSamplePoints + 1];
        int count = ClothoidGeometricMath.GenerateClothoidArc(
            new SKPoint((float)p1.X, (float)p1.Y),
            new SKPoint((float)p2.X, (float)p2.Y),
            CurvatureIntensity,
            arcPoints,
            out _);

        for (int i = 0; i < count - 1; i++)
        {
            var v = new global::Avalonia.Point(arcPoints[i].X, arcPoints[i].Y);
            var wPt = new global::Avalonia.Point(arcPoints[i + 1].X, arcPoints[i + 1].Y);
            if (DistancePointToSegment(screenPoint, v, wPt) <= tolerance)
            {
                return true;
            }
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
        _leaderPaint.Dispose();
        _textPaint.Dispose();
        _bgPaint.Dispose();
        _borderPaint.Dispose();
        _dashEffect.Dispose();
        _dotEffect.Dispose();
        base.Dispose();
    }
}
